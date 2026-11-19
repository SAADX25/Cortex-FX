using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;

namespace CortexFX.Core.Services.Media;

/// <summary>
/// FFmpeg wrapper: convert/trim/cut, pick GPU encoders when available, report progress.
/// </summary>
public sealed class FFmpegService : IFFmpegService
{
    private readonly IAppConfiguration _config;
    private readonly IProcessManager _processManager;
    private readonly bool _reportIndeterminateProgress;
    private static readonly Regex DurationRegex = new(@"Duration:\s*(?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StatusTimeRegex = new(@"\btime=(?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> PureAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".alac"
    };
    private static readonly HashSet<string> UnindexedStreamExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".vob", ".flv", ".asf", ".wmv"
    };
    private static readonly HashSet<string> InaccurateAudioSeekExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".ogg", ".opus", ".wma"
    };

    // Probed once per process
    private readonly Lazy<HardwareCapabilities> _hwCaps;

    public FFmpegService(IAppConfiguration config, IProcessManager processManager, bool reportIndeterminateProgress = false)
    {
        _config = config;
        _processManager = processManager;
        _reportIndeterminateProgress = reportIndeterminateProgress;
        _hwCaps = new Lazy<HardwareCapabilities>(() => ProbeHardwareEncoders());
    }

    /// <summary>Current machine's hardware encoding capabilities.</summary>
    public HardwareCapabilities HwCapabilities => _hwCaps.Value;

    // IFFmpegService implementation

    /// <inheritdoc />
    public async Task ConvertAsync(string inputFile, string outputFile, string arguments,
                                    CancellationToken ct = default, IProgress<double>? progress = null)
    {
        EnsureFFmpeg();
        await RunFFmpegAsync(arguments, ct, progress);
        progress?.Report(100);
    }

    /// <inheritdoc />
    public async Task TrimAudioAsync(string inputFile, string outputFile,
                                      TimeSpan start, TimeSpan end, CancellationToken ct = default)
    {
        EnsureFFmpeg();
        string ss = start.ToString(@"hh\:mm\:ss\.fff");
        string to = end.ToString(@"hh\:mm\:ss\.fff");
        string outExt = Path.GetExtension(outputFile).TrimStart('.').ToLowerInvariant();
        string id3 = outExt == "mp3" ? "-id3v2_version 3 " : string.Empty;
        // Keep embedded album art (do not use -vn).
        string args = $"-i \"{inputFile}\" -ss {ss} -to {to} -map_metadata 0 -map 0 -c copy {id3}-y \"{outputFile}\"";
        await RunFFmpegAsync(args, ct);
    }

    /// <inheritdoc />
    public async Task CutVideoSegmentsAsync(string inputFile, string outputFile,
                                            IReadOnlyList<VideoCutSegment> segments,
                                            CancellationToken ct = default,
                                            IProgress<double>? progress = null)
    {
        EnsureFFmpeg();

        if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            throw new FileNotFoundException("Input video not found.", inputFile);

        if (segments == null || segments.Count == 0)
            throw new ArgumentException("At least one cut segment is required.", nameof(segments));

        var clips = segments
            .Where(s => s.End > s.Start && s.Duration.TotalMilliseconds >= 40)
            .Select(s => new VideoMontageClip(inputFile, s.Start, s.End))
            .ToList();

        if (clips.Count == 0)
            throw new ArgumentException("No valid cut segments (end must be after start).", nameof(segments));

        string outExt = Path.GetExtension(outputFile).TrimStart('.').ToLowerInvariant();
        string format = string.IsNullOrEmpty(outExt) ? "mp4" : outExt;

        await ExportMontageAsync(
            new VideoMontageExportRequest(clips, outputFile, format, VideoCutMode.AccurateEncode, VideoResolutionPreset.Original),
            ct,
            progress).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VideoMediaInfo> ProbeVideoAsync(string inputFile, CancellationToken ct = default)
    {
        EnsureFFmpeg();

        if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            throw new FileNotFoundException("Input video not found.", inputFile);

        var psi = new ProcessStartInfo
        {
            FileName = _config.FFmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputFile);

        using var process = new Process { StartInfo = psi };
        process.Start();
        _processManager.TrackProcess(process.Id);

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillSafe(process);
            _processManager.UntrackProcess(process.Id);
            throw;
        }

        _processManager.UntrackProcess(process.Id);
        string combined = await stderrTask.ConfigureAwait(false) + "\n" + await stdoutTask.ConfigureAwait(false);
        long size = new FileInfo(inputFile).Length;
        return ParseVideoProbeOutput(combined, inputFile, size);
    }

    /// <summary>Parse ffmpeg -i output into structured stream/container metadata.</summary>
    public static VideoMediaInfo ParseVideoProbeOutput(string output, string filePath, long fileSizeBytes)
    {
        TimeSpan duration = TimeSpan.Zero;
        Match durMatch = DurationRegex.Match(output);
        if (durMatch.Success)
            TryParseTimestamp(durMatch.Groups["time"].Value, out duration);

        string container = string.Empty;
        Match fmtMatch = Regex.Match(output, @"Input #0,\s*(?<fmt>[^,\r\n]+)", RegexOptions.CultureInvariant);
        if (fmtMatch.Success)
            container = fmtMatch.Groups["fmt"].Value.Trim();

        bool hasVideo = false;
        int codedW = 0, codedH = 0, displayW = 0, displayH = 0, rotation = 0;
        double fps = 0;
        string videoCodec = string.Empty;

        bool isPureAudioExt = PureAudioExtensions.Contains(Path.GetExtension(filePath));
        string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        bool inVideoStream = false;
        foreach (string line in lines)
        {
            if (Regex.IsMatch(line, @"^\s*Stream #\d+:\d+.*?: Video:", RegexOptions.CultureInvariant))
            {
                if (isPureAudioExt || line.Contains("(attached pic)", StringComparison.OrdinalIgnoreCase) || hasVideo)
                {
                    inVideoStream = false;
                    continue;
                }

                hasVideo = true;
                inVideoStream = true;

                Match codecMatch = Regex.Match(line, @"Video:\s*(?<codec>[^\s(,]+)", RegexOptions.CultureInvariant);
                if (codecMatch.Success)
                    videoCodec = codecMatch.Groups["codec"].Value.Trim();

                Match resMatch = Regex.Match(line, @",\s*(?<w>\d{2,5})x(?<h>\d{2,5})\b", RegexOptions.CultureInvariant);
                if (resMatch.Success &&
                    int.TryParse(resMatch.Groups["w"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out codedW) &&
                    int.TryParse(resMatch.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out codedH))
                {
                    displayW = codedW;
                    displayH = codedH;

                    Match sarMatch = Regex.Match(line, @"\bSAR\s+(?<sn>\d+):(?<sd>\d+)\b", RegexOptions.CultureInvariant);
                    if (sarMatch.Success &&
                        int.TryParse(sarMatch.Groups["sn"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sn) &&
                        int.TryParse(sarMatch.Groups["sd"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sd) &&
                        sn > 0 && sd > 0 && sn != sd)
                    {
                        displayW = Math.Max(2, (int)Math.Round(codedW * (double)sn / sd));
                    }
                }

                Match fpsMatch = Regex.Match(line, @"(?<fps>\d+(?:\.\d+)?)\s*fps\b", RegexOptions.CultureInvariant);
                if (!fpsMatch.Success)
                    fpsMatch = Regex.Match(line, @"(?<fps>\d+(?:\.\d+)?)\s*tbr\b", RegexOptions.CultureInvariant);
                if (fpsMatch.Success)
                    double.TryParse(fpsMatch.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);

                continue;
            }

            if (Regex.IsMatch(line, @"^\s*Stream #\d+:\d+", RegexOptions.CultureInvariant) ||
                line.StartsWith("Output #", StringComparison.OrdinalIgnoreCase))
            {
                inVideoStream = false;
            }

            if (inVideoStream)
            {
                Match rotMatch = Regex.Match(line, @"rotate\s*:\s*(?<rot>-?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!rotMatch.Success)
                    rotMatch = Regex.Match(line, @"rotation of\s*(?<rot>-?\d+(?:\.\d+)?)\s*degrees", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (rotMatch.Success &&
                    double.TryParse(rotMatch.Groups["rot"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rotDeg))
                {
                    rotation = ((int)Math.Round(rotDeg) % 360 + 360) % 360;
                }
            }
        }

        if ((rotation == 90 || rotation == 270) && displayW > 0 && displayH > 0)
            (displayW, displayH) = (displayH, displayW);

        bool hasAudio = false;
        string audioCodec = string.Empty;
        Match audioMatch = Regex.Match(output, @"Stream #\d+:\d+.*?: Audio:\s*(?<codec>[^\s(,]+)", RegexOptions.CultureInvariant);
        if (audioMatch.Success)
        {
            hasAudio = true;
            audioCodec = audioMatch.Groups["codec"].Value.Trim();
        }

        return new VideoMediaInfo(
            FilePath: filePath,
            Duration: duration,
            CodedWidth: codedW,
            CodedHeight: codedH,
            DisplayWidth: displayW,
            DisplayHeight: displayH,
            RotationDegrees: rotation,
            Fps: fps,
            VideoCodec: videoCodec,
            AudioCodec: audioCodec,
            HasVideo: hasVideo,
            HasAudio: hasAudio,
            ContainerFormat: container,
            FileSizeBytes: fileSizeBytes);
    }

    /// <inheritdoc />
    public async Task ExportMontageAsync(VideoMontageExportRequest request,
                                         CancellationToken ct = default,
                                         IProgress<double>? progress = null)
    {
        EnsureFFmpeg();
        ArgumentNullException.ThrowIfNull(request);

        var validInput = request.Clips
            .Where(c => !string.IsNullOrWhiteSpace(c.SourcePath) && File.Exists(c.SourcePath) &&
                        c.End > c.Start && c.Duration.TotalMilliseconds >= 40)
            .ToList();

        var validOverlays = (request.AudioOverlayClips ?? Array.Empty<VideoMontageClip>())
            .Where(c => !string.IsNullOrWhiteSpace(c.SourcePath) && File.Exists(c.SourcePath) &&
                        c.End > c.Start && c.Duration.TotalMilliseconds >= 40)
            .OrderBy(c => c.TimelineStart ?? TimeSpan.Zero)
            .ToList();

        var validVideoOverlays = (request.VideoOverlayClips ?? Array.Empty<VideoMontageClip>())
            .Where(c => !string.IsNullOrWhiteSpace(c.SourcePath) && File.Exists(c.SourcePath) &&
                        c.End > c.Start && c.Duration.TotalMilliseconds >= 40)
            .ToList();

        if (validInput.Count == 0 && validOverlays.Count == 0 && validVideoOverlays.Count == 0)
            throw new ArgumentException("At least one valid montage clip (>= 40ms) is required.", nameof(request));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);

        // Ensure every clip has accurate source dimensions, FPS, and audio-presence flags (respecting explicit mute/separation).
        var probeCache = new Dictionary<string, VideoMediaInfo>(StringComparer.OrdinalIgnoreCase);
        async Task<List<VideoMontageClip>> ResolveDimsAsync(List<VideoMontageClip> source)
        {
            var resolved = new List<VideoMontageClip>(source.Count);
            foreach (VideoMontageClip clip in source)
            {
                if (clip.SourceWidth <= 0 || clip.SourceHeight <= 0 || clip.SourceFps <= 0)
                {
                    if (!probeCache.TryGetValue(clip.SourcePath, out VideoMediaInfo? probed))
                    {
                        probed = await ProbeVideoAsync(clip.SourcePath, ct).ConfigureAwait(false);
                        probeCache[clip.SourcePath] = probed;
                    }

                    resolved.Add(clip with
                    {
                        HasAudio = clip.HasAudio && probed.HasAudio,
                        SourceWidth = clip.SourceWidth > 0 ? clip.SourceWidth : probed.DisplayWidth,
                        SourceHeight = clip.SourceHeight > 0 ? clip.SourceHeight : probed.DisplayHeight,
                        SourceFps = clip.SourceFps > 0 ? clip.SourceFps : probed.Fps
                    });
                }
                else
                {
                    resolved.Add(clip);
                }
            }
            return resolved;
        }

        var clips = await ResolveDimsAsync(validInput).ConfigureAwait(false);
        var videoOverlays = await ResolveDimsAsync(validVideoOverlays).ConfigureAwait(false);

        string targetFmt = NormalizeOutputFormat(request.OutputFormat, request.OutputPath);
        TimeSpan totalDuration = ComputeTimelineSpan(clips, validOverlays, videoOverlays);

        if (CanUseLosslessCopy(clips, validOverlays, videoOverlays, request, targetFmt))
        {
            try
            {
                await ExportLosslessAsync(clips, request.OutputPath, targetFmt, totalDuration, ct, progress).ConfigureAwait(false);
                progress?.Report(100);
                return;
            }
            catch (ProcessExecutionException) when (request.CutMode != VideoCutMode.FastLossless)
            {
                // Auto mode gracefully falls through to smart render if copy fails.
            }
            catch (ProcessExecutionException)
            {
                // Even if FastLossless fails on odd codec/container pairing, retry via smart render.
            }
        }

        await ExportSmartRenderAsync(clips, validOverlays, videoOverlays, request, targetFmt, totalDuration, ct, progress).ConfigureAwait(false);
        progress?.Report(100);
    }

    private static bool HasTimelineGaps(IReadOnlyList<VideoMontageClip> clips)
    {
        if (clips.Count == 0) return false;
        TimeSpan cursor = TimeSpan.Zero;
        foreach (VideoMontageClip c in clips)
        {
            if (!c.TimelineStart.HasValue)
            {
                cursor += c.Duration;
                continue;
            }
            if ((c.TimelineStart.Value - cursor).Duration() > TimeSpan.FromMilliseconds(25))
                return true;
            cursor = c.TimelineStart.Value + c.Duration;
        }
        return false;
    }

    private static bool HasCustomVideoTransform(VideoMontageClip c) =>
        Math.Abs(c.VideoScale - 1.0) > 0.005 ||
        Math.Abs(c.VideoOffsetX) > 0.003 ||
        Math.Abs(c.VideoOffsetY) > 0.003 ||
        Math.Abs(c.VideoOpacity - 1.0) > 0.005;

    private static TimeSpan ComputeTimelineSpan(
        IReadOnlyList<VideoMontageClip> clips,
        IReadOnlyList<VideoMontageClip> overlayClips,
        IReadOnlyList<VideoMontageClip> videoOverlays)
    {
        TimeSpan cursor = TimeSpan.Zero;
        foreach (VideoMontageClip c in clips)
        {
            TimeSpan start = c.TimelineStart ?? cursor;
            if (start < cursor) start = cursor;
            cursor = start + c.Duration;
        }
        TimeSpan maxA2 = overlayClips.Count > 0
            ? overlayClips.Max(o => (o.TimelineStart ?? TimeSpan.Zero) + o.Duration)
            : TimeSpan.Zero;
        TimeSpan maxV2 = videoOverlays.Count > 0
            ? videoOverlays.Max(o => (o.TimelineStart ?? TimeSpan.Zero) + o.Duration)
            : TimeSpan.Zero;
        TimeSpan maxOverlay = maxA2 > maxV2 ? maxA2 : maxV2;
        return cursor > maxOverlay ? cursor : maxOverlay;
    }

    private static bool CanUseLosslessCopy(
        IReadOnlyList<VideoMontageClip> clips,
        IReadOnlyList<VideoMontageClip> overlayClips,
        IReadOnlyList<VideoMontageClip> videoOverlays,
        VideoMontageExportRequest request,
        string targetFmt)
    {
        if (clips.Count == 0 || overlayClips.Count > 0 || videoOverlays.Count > 0 || HasTimelineGaps(clips))
            return false;

        // Only use keyframe-snapped stream copy when explicitly requested via FastLossless.
        if (request.CutMode != VideoCutMode.FastLossless)
            return false;

        if (request.Resolution != VideoResolutionPreset.Original)
            return false;

        if (clips.Any(c => Math.Abs(c.AudioVolume - 1.0) > 0.01 || HasCustomVideoTransform(c)))
            return false;

        string firstSource = clips[0].SourcePath;
        bool singleSource = clips.All(c => string.Equals(c.SourcePath, firstSource, StringComparison.OrdinalIgnoreCase));
        if (!singleSource)
            return false;

        // All clips must agree on audio presence for stream-copy concat.
        bool firstHasAudio = clips[0].HasAudio;
        if (clips.Any(c => c.HasAudio != firstHasAudio))
            return false;

        string outExt = Path.GetExtension(request.OutputPath).TrimStart('.').ToLowerInvariant();
        string effectiveTarget = string.IsNullOrEmpty(outExt) ? targetFmt : outExt;
        string srcExt = Path.GetExtension(firstSource).TrimStart('.').ToLowerInvariant();
        if (string.Equals(srcExt, effectiveTarget, StringComparison.OrdinalIgnoreCase))
            return true;
        return srcExt is "mp4" or "m4v" or "mov" or "mkv" or "ts" or "mts" &&
               effectiveTarget is "mp4" or "mov" or "mkv";
    }

    private async Task ExportLosslessAsync(
        IReadOnlyList<VideoMontageClip> clips,
        string outputPath,
        string targetFmt,
        TimeSpan totalDuration,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        string fastStart = targetFmt is "mp4" or "mov" ? "-movflags +faststart " : string.Empty;

        if (clips.Count == 1)
        {
            VideoMontageClip c = clips[0];
            string ss = FormatTs(c.Start);
            string dur = FormatTs(c.Duration);
            string audioMap = c.HasAudio ? "-map 0:a:0?" : "-an";
            string args = $"-ss {ss} -i \"{c.SourcePath}\" -t {dur} -map 0:v:0 {audioMap} -c copy -avoid_negative_ts make_zero {fastStart}-y \"{outputPath}\"";
            await RunFFmpegAsync(args, ct, progress, totalDuration).ConfigureAwait(false);
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new ProcessExecutionException("ffmpeg.exe", 1, string.Empty, "Stream copy produced an empty file.");
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "CortexFX_Lossless_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string partExt = string.IsNullOrWhiteSpace(Path.GetExtension(clips[0].SourcePath)) ? ".mp4" : Path.GetExtension(clips[0].SourcePath);

        try
        {
            var partFiles = new List<string>(clips.Count);
            for (int i = 0; i < clips.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                VideoMontageClip c = clips[i];
                string partPath = Path.Combine(tempRoot, $"part_{i:D3}{partExt}");
                string audioMap = c.HasAudio ? "-map 0:a:0?" : "-an";
                string args = $"-ss {FormatTs(c.Start)} -i \"{c.SourcePath}\" -t {FormatTs(c.Duration)} -map 0:v:0 {audioMap} -c copy -avoid_negative_ts make_zero -y \"{partPath}\"";
                await RunFFmpegAsync(args, ct).ConfigureAwait(false);
                if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
                    throw new ProcessExecutionException("ffmpeg.exe", 1, string.Empty, "Lossless segment extraction produced an empty file.");
                partFiles.Add(partPath);
                progress?.Report(Math.Clamp((i + 1) * 75.0 / clips.Count, 1, 75));
            }

            string listPath = Path.Combine(tempRoot, "concat.txt");
            await File.WriteAllLinesAsync(listPath, partFiles.Select(ToConcatFileLine), ct).ConfigureAwait(false);
            string concatArgs = $"-f concat -safe 0 -i \"{listPath}\" -c copy {fastStart}-y \"{outputPath}\"";
            await RunFFmpegAsync(concatArgs, ct, progress, totalDuration).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static bool AreContiguousExportClips(VideoMontageClip? prev, VideoMontageClip? next) =>
        prev != null &&
        next != null &&
        string.Equals(prev.SourcePath, next.SourcePath, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs((prev.End - next.Start).TotalMilliseconds) < 25;

    private static (string InputArgs, string VideoPreTrim, string AudioPreTrim) BuildAccurateInputSpec(
        VideoMontageClip clip,
        bool isAudioOnly)
    {
        string ext = Path.GetExtension(clip.SourcePath);
        double startSec = Math.Max(0.0, clip.Start.TotalSeconds);
        double durSec = Math.Max(0.04, clip.Duration.TotalSeconds);

        bool useFilterSeek = UnindexedStreamExtensions.Contains(ext) ||
                             (isAudioOnly && InaccurateAudioSeekExtensions.Contains(ext));

        if (useFilterSeek)
        {
            string startStr = FormatSecondsExact(startSec);
            string readStr = FormatSecondsExact(durSec + 0.5);
            return (
                $"-i \"{clip.SourcePath}\" ",
                $"setpts=PTS-STARTPTS,trim=start={startStr}:duration={readStr},setpts=PTS-STARTPTS,",
                $"asetpts=PTS-STARTPTS,atrim=start={startStr}:duration={readStr},asetpts=PTS-STARTPTS,");
        }

        // Indexed container: accurate input -ss + generous +1.0s demuxer read margin so trailing B-frames/AAC samples are never starved;
        // exact frame/sample boundaries are enforced strictly inside the filtergraph via trim/atrim.
        string readMargin = FormatSecondsExact(durSec + 1.0);
        string inputArgs = startSec > 0.0005
            ? $"-ss {FormatSecondsExact(startSec)} -t {readMargin} -i \"{clip.SourcePath}\" "
            : $"-t {readMargin} -i \"{clip.SourcePath}\" ";
        return (inputArgs, "setpts=PTS-STARTPTS,", "asetpts=PTS-STARTPTS,");
    }

    private static string BuildAudioDeclickFade(double segDurationSec, bool fadeIn, bool fadeOut)
    {
        if (segDurationSec < 0.03)
            return string.Empty;

        const double fadeSec = 0.004; // 4ms (192 samples @ 48kHz): imperceptible dip, eliminates digital pop/click at cut joins
        var sb = new StringBuilder();
        if (fadeIn)
            sb.Append($",afade=t=in:st=0:d={FormatSecondsExact(fadeSec)}");
        if (fadeOut)
        {
            double outStart = Math.Max(0.0, segDurationSec - fadeSec);
            sb.Append($",afade=t=out:st={FormatSecondsExact(outStart)}:d={FormatSecondsExact(fadeSec)}");
        }
        return sb.ToString();
    }

    private async Task ExportSmartRenderAsync(
        IReadOnlyList<VideoMontageClip> clips,
        IReadOnlyList<VideoMontageClip> overlayClips,
        IReadOnlyList<VideoMontageClip> videoOverlays,
        VideoMontageExportRequest request,
        string targetFmt,
        TimeSpan totalDuration,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        IReadOnlyList<VideoMontageClip> refClips = clips.Count > 0 ? clips : videoOverlays;
        var (targetW, targetH) = ResolveTargetCanvas(refClips, request.Resolution);
        double targetFps = ResolveTargetFps(refClips);
        string fpsStr = targetFps.ToString("0.###", CultureInfo.InvariantCulture);
        double frameDurSec = 1.0 / targetFps;
        TimeSpan minGapThreshold = TimeSpan.FromSeconds(frameDurSec * 0.5);

        bool hasVideoOverlays = videoOverlays.Count > 0;
        bool hasAnyAudioOverlays = overlayClips.Count > 0 || videoOverlays.Any(v => v.HasAudio);
        bool anyAudio = clips.Any(c => c.HasAudio) || hasAnyAudioOverlays;
        bool anyGainBoost = clips.Any(c => c.HasAudio && c.AudioVolume > 1.01);
        int crf = Math.Clamp(request.QualityCrf, 14, 34);

        string scalePostFilter = BuildClipScaleFilter(targetW, targetH, request.AspectFitMode);

        // Build ordered V1 video + black-gap segments spanning [0 .. totalDuration] with strict cursor tracking.
        var segments = new List<(bool IsGap, TimeSpan StartOnTimeline, TimeSpan Duration, VideoMontageClip? Clip)>();
        TimeSpan cursor = TimeSpan.Zero;
        foreach (VideoMontageClip c in clips)
        {
            TimeSpan desiredStart = c.TimelineStart ?? cursor;
            if (desiredStart < cursor) desiredStart = cursor;
            TimeSpan gap = desiredStart - cursor;
            if (gap >= minGapThreshold)
            {
                segments.Add((true, cursor, gap, null));
                cursor += gap;
            }
            segments.Add((false, cursor, c.Duration, c));
            cursor += c.Duration;
        }
        TimeSpan trailingGap = totalDuration - cursor;
        if (trailingGap >= minGapThreshold || segments.Count == 0)
        {
            TimeSpan tDur = trailingGap >= minGapThreshold
                ? trailingGap
                : (totalDuration > TimeSpan.FromSeconds(frameDurSec) ? totalDuration : TimeSpan.FromSeconds(frameDurSec));
            segments.Add((true, cursor, tDur, null));
            cursor += tDur;
        }

        // Compute exact per-segment frame counts using cumulative boundary rounding (zero drift across any number of cuts).
        long cumulativeFrames = 0;
        var segFrameCounts = new long[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            TimeSpan segEnd = segments[i].StartOnTimeline + segments[i].Duration;
            long endFrame = Math.Max(cumulativeFrames + 1, (long)Math.Round(segEnd.TotalSeconds * targetFps));
            segFrameCounts[i] = Math.Max(1, endFrame - cumulativeFrames);
            cumulativeFrames = endFrame;
        }
        TimeSpan exactTotalDuration = TimeSpan.FromSeconds(cumulativeFrames / targetFps);

        static int MakeEvenInt(int val) => Math.Max(2, (val + 1) & ~1);
        (int ScaledW, int ScaledH, int PosX, int PosY) ComputeTransformBox(VideoMontageClip c)
        {
            int srcW = c.SourceWidth > 0 ? c.SourceWidth : targetW;
            int srcH = c.SourceHeight > 0 ? c.SourceHeight : targetH;
            double fitScale = request.AspectFitMode == VideoAspectFitMode.FillCrop
                ? Math.Max(targetW / (double)srcW, targetH / (double)srcH)
                : Math.Min(targetW / (double)srcW, targetH / (double)srcH);
            double effScale = Math.Clamp(c.VideoScale, 0.10, 4.0);
            int sw = MakeEvenInt((int)Math.Round(srcW * fitScale * effScale));
            int sh = MakeEvenInt((int)Math.Round(srcH * fitScale * effScale));
            int px = (int)Math.Round(targetW * 0.5 + c.VideoOffsetX * targetW - sw * 0.5);
            int py = (int)Math.Round(targetH * 0.5 + c.VideoOffsetY * targetH - sh * 0.5);
            return (sw, sh, px, py);
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "CortexFX_Montage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string filterScriptPath = Path.Combine(tempRoot, "montage_filter.txt");

        try
        {
            var inputs = new StringBuilder();
            var filter = new StringBuilder();
            var concatInputs = new StringBuilder();
            int nextInputIndex = 0;

            for (int segIdx = 0; segIdx < segments.Count; segIdx++)
            {
                var (isGap, _, _, c) = segments[segIdx];
                long exactFrames = segFrameCounts[segIdx];
                double exactSegSec = exactFrames / targetFps;
                long exactSamples = Math.Max(1, (long)Math.Round(exactSegSec * 48000.0));
                string bgGenDur = FormatSecondsExact(exactSegSec + 0.15);

                if (isGap || c == null)
                {
                    filter.Append($"color=c=black:s={targetW}x{targetH}:r={fpsStr}:d={bgGenDur},trim=start_frame=0:end_frame={exactFrames},setsar=1,format=yuv420p,setpts=PTS-STARTPTS[v{segIdx}];");
                    if (anyAudio)
                    {
                        filter.Append($"anullsrc=r=48000:cl=stereo,atrim=start_sample=0:end_sample={exactSamples},asetpts=PTS-STARTPTS[a{segIdx}];");
                        concatInputs.Append($"[v{segIdx}][a{segIdx}]");
                    }
                    else
                    {
                        concatInputs.Append($"[v{segIdx}]");
                    }
                }
                else
                {
                    int inIdx = nextInputIndex++;
                    var (inArgs, vPreTrim, aPreTrim) = BuildAccurateInputSpec(c, isAudioOnly: false);
                    inputs.Append(inArgs);

                    // Order is critical: setpts=PTS-STARTPTS BEFORE fps so fps never inserts frozen leading frames,
                    // then tpad + trim=end_frame guarantees exact frame count matching timeline selection.
                    string exactFramePipeline = $"{vPreTrim}fps={fpsStr},tpad=stop_mode=clone:stop_duration=0.25,trim=start_frame=0:end_frame={exactFrames},setpts=PTS-STARTPTS";

                    if (HasCustomVideoTransform(c))
                    {
                        var (sw, sh, px, py) = ComputeTransformBox(c);
                        string alphaPart = c.VideoOpacity < 0.995
                            ? $",format=yuva420p,colorchannelmixer=aa={Math.Clamp(c.VideoOpacity, 0.0, 1.0).ToString("0.###", CultureInfo.InvariantCulture)}"
                            : ",format=yuv420p";
                        filter.Append($"color=c=black:s={targetW}x{targetH}:r={fpsStr}:d={bgGenDur},trim=start_frame=0:end_frame={exactFrames},setsar=1,format=yuv420p,setpts=PTS-STARTPTS[bg{segIdx}];");
                        filter.Append($"[{inIdx}:v:0]{exactFramePipeline},scale={sw}:{sh}:flags=lanczos,setsar=1{alphaPart}[fg{segIdx}];");
                        filter.Append($"[bg{segIdx}][fg{segIdx}]overlay=x={px}:y={py}:eof_action=pass:shortest=0,format=yuv420p,setpts=PTS-STARTPTS[v{segIdx}];");
                    }
                    else
                    {
                        filter.Append($"[{inIdx}:v:0]{exactFramePipeline},{scalePostFilter}[v{segIdx}];");
                    }

                    if (anyAudio)
                    {
                        if (c.HasAudio)
                        {
                            VideoMontageClip? prevClip = segIdx > 0 && !segments[segIdx - 1].IsGap ? segments[segIdx - 1].Clip : null;
                            VideoMontageClip? nextClip = segIdx < segments.Count - 1 && !segments[segIdx + 1].IsGap ? segments[segIdx + 1].Clip : null;

                            bool fadeIn = segIdx == 0
                                ? c.Start > TimeSpan.FromMilliseconds(20)
                                : (prevClip == null || !prevClip.HasAudio || !AreContiguousExportClips(prevClip, c));
                            bool fadeOut = segIdx == segments.Count - 1
                                ? true
                                : (nextClip == null || !nextClip.HasAudio || !AreContiguousExportClips(c, nextClip));

                            string volFilter = BuildVolumeFilterSuffix(c.AudioVolume);
                            string declick = BuildAudioDeclickFade(exactSegSec, fadeIn, fadeOut);
                            filter.Append($"[{inIdx}:a:0]{aPreTrim}aformat=sample_rates=48000:channel_layouts=stereo,aresample=async=1:first_pts=0{volFilter},apad=whole_len={exactSamples},atrim=start_sample=0:end_sample={exactSamples}{declick},asetpts=PTS-STARTPTS[a{segIdx}];");
                        }
                        else
                        {
                            filter.Append($"anullsrc=r=48000:cl=stereo,atrim=start_sample=0:end_sample={exactSamples},asetpts=PTS-STARTPTS[a{segIdx}];");
                        }
                        concatInputs.Append($"[v{segIdx}][a{segIdx}]");
                    }
                    else
                    {
                        concatInputs.Append($"[v{segIdx}]");
                    }
                }
            }

            string baseVideoLabel = hasVideoOverlays ? "[v_base]" : "[vout]";
            string baseAudioLabel = hasAnyAudioOverlays ? "[a_base]" : (anyGainBoost ? "[a_prelim]" : "[aout]");

            if (segments.Count == 1)
            {
                filter.Append($"[v0]copy{baseVideoLabel}");
                if (anyAudio)
                    filter.Append($";[a0]acopy{baseAudioLabel}");
            }
            else if (anyAudio)
            {
                filter.Append($"{concatInputs}concat=n={segments.Count}:v=1:a=1{baseVideoLabel}{baseAudioLabel}");
            }
            else
            {
                filter.Append($"{concatInputs}concat=n={segments.Count}:v=1:a=0{baseVideoLabel}");
            }

            if (anyAudio && !hasAnyAudioOverlays && anyGainBoost)
                filter.Append($";[a_prelim]alimiter=limit=0.98[aout]");

            var extraAudioLabels = new List<string>();
            if (hasVideoOverlays)
            {
                string curV = "[v_base]";
                for (int k = 0; k < videoOverlays.Count; k++)
                {
                    VideoMontageClip ov = videoOverlays[k];
                    int inIdx = nextInputIndex++;
                    var (inArgs, vPreTrim, aPreTrim) = BuildAccurateInputSpec(ov, isAudioOnly: false);
                    inputs.Append(inArgs);

                    var (sw, sh, px, py) = ComputeTransformBox(ov);
                    long ovFrames = Math.Max(1, (long)Math.Round(ov.Duration.TotalSeconds * targetFps));
                    double ovSec = ovFrames / targetFps;
                    double startSec = Math.Max(0.0, (ov.TimelineStart ?? TimeSpan.Zero).TotalSeconds);
                    double endSec = startSec + ovSec;
                    string sStr = FormatSecondsExact(startSec);
                    string eStr = FormatSecondsExact(endSec);
                    string alphaPart = ov.VideoOpacity < 0.995
                        ? $",format=yuva420p,colorchannelmixer=aa={Math.Clamp(ov.VideoOpacity, 0.0, 1.0).ToString("0.###", CultureInfo.InvariantCulture)}"
                        : ",format=yuv420p";

                    filter.Append($";[{inIdx}:v:0]{vPreTrim}fps={fpsStr},tpad=stop_mode=clone:stop_duration=0.25,trim=start_frame=0:end_frame={ovFrames},setpts=PTS-STARTPTS,scale={sw}:{sh}:flags=lanczos,setsar=1{alphaPart},setpts=PTS-STARTPTS+{sStr}/TB[ov_v{k}]");
                    string nextV = k == videoOverlays.Count - 1 ? "[vout]" : $"[v_st{k}]";
                    filter.Append($";{curV}[ov_v{k}]overlay=x={px}:y={py}:enable='between(t,{sStr},{eStr})':eof_action=pass:shortest=0,format=yuv420p{nextV}");
                    curV = nextV;

                    if (ov.HasAudio)
                    {
                        long ovSamples = Math.Max(1, (long)Math.Round(ovSec * 48000.0));
                        string volFilter = BuildVolumeFilterSuffix(ov.AudioVolume);
                        string declick = BuildAudioDeclickFade(ovSec, fadeIn: true, fadeOut: true);
                        long delayMs = Math.Max(0L, (long)Math.Round(startSec * 1000.0));
                        string delayPart = delayMs > 0 ? $",adelay={delayMs}:all=1" : string.Empty;
                        filter.Append($";[{inIdx}:a:0]{aPreTrim}aformat=sample_rates=48000:channel_layouts=stereo,aresample=async=1:first_pts=0{volFilter},apad=whole_len={ovSamples},atrim=start_sample=0:end_sample={ovSamples}{declick},asetpts=PTS-STARTPTS{delayPart}[vov_a{k}]");
                        extraAudioLabels.Add($"[vov_a{k}]");
                    }
                }
            }

            if (overlayClips.Count > 0)
            {
                for (int k = 0; k < overlayClips.Count; k++)
                {
                    VideoMontageClip ov = overlayClips[k];
                    int inIdx = nextInputIndex++;
                    var (inArgs, _, aPreTrim) = BuildAccurateInputSpec(ov, isAudioOnly: true);
                    inputs.Append(inArgs);

                    double ovSec = Math.Max(0.04, ov.Duration.TotalSeconds);
                    long ovSamples = Math.Max(1, (long)Math.Round(ovSec * 48000.0));
                    string volFilter = BuildVolumeFilterSuffix(ov.AudioVolume);
                    string declick = BuildAudioDeclickFade(ovSec, fadeIn: ov.Start > TimeSpan.FromMilliseconds(20) || k > 0, fadeOut: true);
                    long delayMs = Math.Max(0L, (long)Math.Round((ov.TimelineStart ?? TimeSpan.Zero).TotalMilliseconds));
                    string delayPart = delayMs > 0 ? $",adelay={delayMs}:all=1" : string.Empty;
                    filter.Append($";[{inIdx}:a:0]{aPreTrim}aformat=sample_rates=48000:channel_layouts=stereo,aresample=async=1:first_pts=0{volFilter},apad=whole_len={ovSamples},atrim=start_sample=0:end_sample={ovSamples}{declick},asetpts=PTS-STARTPTS{delayPart}[aov{k}]");
                    extraAudioLabels.Add($"[aov{k}]");
                }
            }

            if (hasAnyAudioOverlays)
            {
                if (extraAudioLabels.Count > 0)
                {
                    var mixLabels = new StringBuilder("[a_base]");
                    foreach (string lbl in extraAudioLabels)
                        mixLabels.Append(lbl);
                    filter.Append($";{mixLabels}amix=inputs={1 + extraAudioLabels.Count}:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.98[aout]");
                }
                else
                {
                    filter.Append(";[a_base]acopy[aout]");
                }
            }

            await File.WriteAllTextAsync(filterScriptPath, filter.ToString(), ct).ConfigureAwait(false);

            string mapArgs = anyAudio ? "-map \"[vout]\" -map \"[aout]\"" : "-map \"[vout]\"";
            await RunWithCpuFallbackAsync(
                preferHw =>
                {
                    string enc = BuildContainerEncodeArgs(targetFmt, crf, anyAudio, preferHw);
                    return $"{inputs}-filter_complex_script \"{filterScriptPath}\" {mapArgs} {enc} -max_muxing_queue_size 1024 -y \"{request.OutputPath}\"";
                },
                ct, progress, exactTotalDuration).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private async Task RunWithCpuFallbackAsync(
        Func<bool, string> buildArgs,
        CancellationToken ct,
        IProgress<double>? progress = null,
        TimeSpan? expectedDuration = null)
    {
        string primaryArgs = buildArgs(true);
        try
        {
            await RunFFmpegAsync(primaryArgs, ct, progress, expectedDuration).ConfigureAwait(false);
        }
        catch (ProcessExecutionException) when (!ct.IsCancellationRequested && UsesHardwareEncoder(primaryArgs))
        {
            string cpuArgs = buildArgs(false);
            await RunFFmpegAsync(cpuArgs, ct, progress, expectedDuration).ConfigureAwait(false);
        }
    }

    public static (int Width, int Height) ComputeTargetCanvas(
        IReadOnlyList<VideoMontageClip> clips,
        VideoResolutionPreset preset) =>
        ResolveTargetCanvas(clips, preset);

    private static (int Width, int Height) ResolveTargetCanvas(
        IReadOnlyList<VideoMontageClip> clips,
        VideoResolutionPreset preset)
    {
        if (clips.Count == 0) return (1920, 1080);
        int w0 = clips[0].SourceWidth > 0 ? clips[0].SourceWidth : 1920;
        int h0 = clips[0].SourceHeight > 0 ? clips[0].SourceHeight : 1080;

        static int MakeEven(int value) => Math.Max(2, (value + 1) & ~1);

        return preset switch
        {
            VideoResolutionPreset.Vertical1080x1920 => (1080, 1920),
            VideoResolutionPreset.Square1080x1080 => (1080, 1080),
            VideoResolutionPreset.Uhd4K => ScaleForShortSide(w0, h0, 2160),
            VideoResolutionPreset.Qhd1440 => ScaleForShortSide(w0, h0, 1440),
            VideoResolutionPreset.Fhd1080 => ScaleForShortSide(w0, h0, 1080),
            VideoResolutionPreset.Hd720 => ScaleForShortSide(w0, h0, 720),
            VideoResolutionPreset.Sd480 => ScaleForShortSide(w0, h0, 480),
            _ => (MakeEven(w0), MakeEven(h0))
        };

        static (int, int) ScaleForShortSide(int srcW, int srcH, int shortSide)
        {
            if (srcH > srcW)
            {
                int w = MakeEven(shortSide);
                int h = MakeEven((int)Math.Round(shortSide * (srcH / (double)srcW)));
                return (w, h);
            }
            int outH = MakeEven(shortSide);
            int outW = MakeEven((int)Math.Round(shortSide * (srcW / (double)srcH)));
            return (outW, outH);
        }
    }

    private static double ResolveTargetFps(IReadOnlyList<VideoMontageClip> clips)
    {
        var validFps = clips.Select(c => c.SourceFps).Where(f => f >= 10 && f <= 120).ToList();
        if (validFps.Count == 0) return 30;
        double first = validFps[0];
        if (validFps.All(f => Math.Abs(f - first) < 0.5))
            return Math.Clamp(first, 15, 60);
        return validFps.Any(f => f >= 50) ? 60 : 30;
    }

    private static string BuildVolumeFilterSuffix(double audioVolume)
    {
        double clamped = Math.Clamp(audioVolume, 0.0, 2.5);
        if (Math.Abs(clamped - 1.0) <= 0.01)
            return string.Empty;
        return $",volume={clamped.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    private static string BuildClipScaleFilter(int targetW, int targetH, VideoAspectFitMode aspectFit)
    {
        string scaleAndFit = aspectFit == VideoAspectFitMode.FillCrop
            ? $"scale={targetW}:{targetH}:force_original_aspect_ratio=increase:flags=lanczos,crop={targetW}:{targetH}:(iw-ow)/2:(ih-oh)/2"
            : $"scale={targetW}:{targetH}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2:color=black";

        // Always place setsar=1 AFTER scale/pad/crop so concat sees uniform 1:1 SAR across mixed clips.
        return $"{scaleAndFit},setsar=1,format=yuv420p";
    }

    private string BuildContainerEncodeArgs(string format, int crf, bool hasAudio, bool preferHardware)
    {
        string audioPart = format switch
        {
            "webm" => hasAudio ? "-c:a libopus -b:a 160k" : "-an",
            "avi" => hasAudio ? "-c:a libmp3lame -b:a 192k" : "-an",
            _ => hasAudio ? "-c:a aac -b:a 192k" : "-an"
        };

        return format switch
        {
            "webm" => $"-c:v libvpx-vp9 -crf {Math.Clamp(crf + 4, 18, 38)} -b:v 0 -deadline good -cpu-used 4 -row-mt 1 -pix_fmt yuv420p {audioPart}",
            "avi" => $"-c:v mpeg4 -qscale:v {Math.Clamp((crf - 12) / 3, 2, 12)} -pix_fmt yuv420p {audioPart}",
            "mov" or "mp4" => $"{BuildH264VideoEncodeArgs(crf, preferHardware)} -pix_fmt yuv420p {audioPart} -movflags +faststart",
            _ => $"{BuildH264VideoEncodeArgs(crf, preferHardware)} -pix_fmt yuv420p {audioPart}"
        };
    }

    private string BuildH264VideoEncodeArgs(int crf, bool preferHardware)
    {
        if (preferHardware)
        {
            var caps = HwCapabilities;
            if (caps.HasNvenc)
                return $"-c:v h264_nvenc -rc vbr -cq {crf} -b:v 0 -preset p4";
            if (caps.HasQuickSync)
                return $"-c:v h264_qsv -global_quality {crf} -preset very_fast";
            if (caps.HasAmf)
                return $"-c:v h264_amf -quality speed -rc cqp -qp_i {crf} -qp_p {crf}";
        }

        return $"-c:v libx264 -preset veryfast -crf {crf} -threads 0";
    }

    private static string NormalizeOutputFormat(string format, string outputPath)
    {
        string ext = string.IsNullOrWhiteSpace(format)
            ? Path.GetExtension(outputPath).TrimStart('.')
            : format.TrimStart('.');
        ext = ext.ToLowerInvariant();
        return ext switch
        {
            "mp4" or "mov" or "mkv" or "webm" or "avi" => ext,
            _ => "mp4"
        };
    }

    private static string FormatTs(TimeSpan value) =>
        FormatSecondsExact(Math.Max(0.0, value.TotalSeconds));

    private static string FormatSecondsExact(double seconds) =>
        Math.Max(0.0, seconds).ToString("0.######", CultureInfo.InvariantCulture);

    private static string ToConcatFileLine(string path)
    {
        string normalized = Path.GetFullPath(path).Replace('\\', '/').Replace("'", "'\\''");
        return $"file '{normalized}'";
    }

    /// <inheritdoc />
    public async Task ExtractAudioAsync(string inputFile, string outputFile, CancellationToken ct = default,
                                        IProgress<double>? progress = null)
    {
        EnsureFFmpeg();
        string targetFormat = Path.GetExtension(outputFile).TrimStart('.');
        string args = BuildAudioArgumentsCore(inputFile, outputFile, targetFormat, 75, preserveCover: false);
        await RunFFmpegAsync(args, ct, progress);
        progress?.Report(100);
    }

    /// <inheritdoc />
    public Task ConvertToGifAsync(string inputFile, string outputFile, CancellationToken ct = default,
                                  IProgress<double>? progress = null)
        => ConvertToGifAsync(inputFile, outputFile, new GifConversionOptions(), ct, progress);

    /// <summary>Resolved encoding parameters for a single GIF encode pass.</summary>
    public readonly record struct GifEncodeProfile(
        int MaxDimension,
        int Fps,
        int MaxColors,
        string Dither,
        string? DenoiseFilter,
        double MagickFuzzPercent);

    /// <summary>
    /// Computes the default GIF profile for UI preview and conversion based on quality,
    /// optional overrides, duration, and optional target file size cap.
    /// </summary>
    public static GifEncodeProfile ResolveGifProfile(GifConversionOptions? options, double durationSec = 0)
    {
        var opts = options ?? new GifConversionOptions();
        double q = Math.Clamp(opts.QualityLevel, 0, 100);

        int autoDim;
        int autoFps;
        int autoColors;
        string dither;
        string denoise;
        double fuzz;

        if (q <= 20)
        {
            autoDim = 320;
            autoFps = 8;
            autoColors = q <= 10 ? 48 : 64;
            dither = "none";
            denoise = "hqdn3d=2.5:2.5:8:8";
            fuzz = 4.0;
        }
        else if (q <= 45)
        {
            autoDim = 360;
            autoFps = 10;
            autoColors = 96;
            dither = "bayer:bayer_scale=5";
            denoise = "hqdn3d=2.0:2.0:6:6";
            fuzz = 3.0;
        }
        else if (q <= 65)
        {
            autoDim = 420;
            autoFps = 10;
            autoColors = 128;
            dither = "bayer:bayer_scale=5";
            denoise = "hqdn3d=1.8:1.8:5:5";
            fuzz = 2.5;
        }
        else if (q <= 85)
        {
            autoDim = 480;
            autoFps = 12;
            autoColors = 128;
            dither = "bayer:bayer_scale=5";
            denoise = "hqdn3d=1.5:1.5:4:4";
            fuzz = 2.0;
        }
        else
        {
            autoDim = q >= 95 ? 600 : 540;
            autoFps = 15;
            autoColors = q >= 95 ? 256 : 192;
            dither = "bayer:bayer_scale=3";
            denoise = "hqdn3d=1.0:1.0:3:3";
            fuzz = 1.5;
        }

        // Duration-aware auto ceilings keep long videos from exploding into tens of megabytes
        // unless the user explicitly locked width / FPS / colors.
        if (durationSec > 45)
        {
            autoDim = Math.Min(autoDim, 360);
            autoFps = Math.Min(autoFps, 8);
            autoColors = Math.Min(autoColors, 96);
        }
        else if (durationSec > 20)
        {
            autoDim = Math.Min(autoDim, 440);
            autoFps = Math.Min(autoFps, 10);
            autoColors = Math.Min(autoColors, 128);
        }
        else if (durationSec > 12)
        {
            autoFps = Math.Min(autoFps, 12);
        }

        int maxDim = opts.MaxWidth is > 0 ? Math.Clamp(opts.MaxWidth.Value, 160, 1280) : autoDim;
        int fps = opts.Fps is > 0 ? Math.Clamp(opts.Fps.Value, 4, 30) : autoFps;
        int colors = opts.MaxColors is > 0 ? Math.Clamp(opts.MaxColors.Value, 16, 256) : autoColors;

        // If user set a TargetMaxSizeMb cap and we know duration, start with a smart ceiling
        // so the very first pass already lands near the budget instead of wasting retries.
        if (opts.TargetMaxSizeMb is double targetMb and > 0 && durationSec > 0.5)
        {
            double kbPerSec = (targetMb * 1024.0) / durationSec;
            if (kbPerSec < 80)
            {
                maxDim = Math.Min(maxDim, 280);
                fps = Math.Min(fps, 7);
                colors = Math.Min(colors, 64);
                dither = "none";
                denoise = "hqdn3d=2.5:2.5:8:8";
                fuzz = Math.Max(fuzz, 4.0);
            }
            else if (kbPerSec < 160)
            {
                maxDim = Math.Min(maxDim, 340);
                fps = Math.Min(fps, 9);
                colors = Math.Min(colors, 96);
                dither = "bayer:bayer_scale=5";
                denoise = "hqdn3d=2.0:2.0:6:6";
                fuzz = Math.Max(fuzz, 3.5);
            }
            else if (kbPerSec < 320)
            {
                maxDim = Math.Min(maxDim, 420);
                fps = Math.Min(fps, 10);
                colors = Math.Min(colors, 128);
                fuzz = Math.Max(fuzz, 2.5);
            }
            else if (kbPerSec < 550)
            {
                maxDim = Math.Min(maxDim, 480);
                fps = Math.Min(fps, 12);
                colors = Math.Min(colors, 128);
            }
        }

        maxDim = Math.Max(160, (maxDim / 2) * 2);
        return new GifEncodeProfile(
            MaxDimension: maxDim,
            Fps: fps,
            MaxColors: colors,
            Dither: dither,
            DenoiseFilter: opts.ExtraCompression ? denoise : null,
            MagickFuzzPercent: opts.ExtraCompression ? fuzz : 0);
    }

    private static string BuildGifArguments(string inputFile, string outputFile, GifEncodeProfile profile)
    {
        // Constrain the longest edge (so vertical 9:16 videos don't bloat 3x larger than landscape),
        // never upscale small sources, suppress invisible temporal grain so inter-frame transparency
        // diffing kicks in, and enable rectangle diff + transdiff + offsetting.
        string denoiseSegment = string.IsNullOrWhiteSpace(profile.DenoiseFilter)
            ? string.Empty
            : $",{profile.DenoiseFilter}";
        string scaleExpr = $"scale='if(gte(iw,ih),min({profile.MaxDimension},iw),-2)':'if(gte(iw,ih),-2,min({profile.MaxDimension},ih))':flags=lanczos";
        string vf = $"fps={profile.Fps},{scaleExpr}{denoiseSegment},split[s0][s1];[s0]palettegen=max_colors={profile.MaxColors}:stats_mode=diff[p];[s1][p]paletteuse=dither={profile.Dither}:diff_mode=rectangle";

        return $"-i \"{inputFile}\" -an -vf \"{vf}\" -gifflags +transdiff+offsetting -loop 0 -y \"{outputFile}\"";
    }

    /// <inheritdoc />
    public async Task ConvertToGifAsync(string inputFile, string outputFile, GifConversionOptions? options,
                                        CancellationToken ct = default,
                                        IProgress<double>? progress = null)
    {
        EnsureFFmpeg();
        var opts = options ?? new GifConversionOptions();

        double durationSec = 0;
        TimeSpan? expectedDuration = null;
        try
        {
            var probe = await ProbeVideoAsync(inputFile, ct);
            if (probe.Duration > TimeSpan.Zero)
            {
                durationSec = probe.Duration.TotalSeconds;
                expectedDuration = probe.Duration;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Continue without probed duration if input has non-standard headers.
        }

        GifEncodeProfile profile = ResolveGifProfile(opts, durationSec);
        long? targetBytes = opts.TargetMaxSizeMb is double targetMb and > 0
            ? (long)(targetMb * 1024 * 1024 * 0.98)
            : null;

        int maxAttempts = targetBytes.HasValue ? 4 : 1;
        string? bestTempPath = null;
        long bestBytes = long.MaxValue;
        double highestReported = 0;

        void ReportMonotonic(double value)
        {
            if (progress == null) return;
            double clamped = Math.Clamp(value, 0, 99);
            if (clamped > highestReported)
            {
                highestReported = clamped;
                progress.Report(clamped);
            }
        }

        try
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                double passStart = attempt == 0 ? 0 : Math.Min(92, highestReported);
                double passSpan = attempt == 0
                    ? (targetBytes.HasValue ? 72.0 : 98.0)
                    : Math.Max(4.0, (96.0 - passStart) / Math.Max(1, maxAttempts - attempt));
                double ffmpegSpan = opts.ExtraCompression ? passSpan * 0.78 : passSpan;

                var passProgress = progress == null
                    ? null
                    : new Progress<double>(p =>
                    {
                        if (p >= 0)
                            ReportMonotonic(passStart + (Math.Clamp(p, 0, 100) / 100.0) * ffmpegSpan);
                    });

                string args = BuildGifArguments(inputFile, outputFile, profile);
                await RunFFmpegAsync(args, ct, passProgress, expectedDuration);

                if (opts.ExtraCompression && profile.MagickFuzzPercent > 0)
                {
                    ReportMonotonic(passStart + ffmpegSpan);
                    await TryOptimizeGifWithMagickAsync(outputFile, durationSec, profile, ct);
                }

                ReportMonotonic(passStart + passSpan);

                if (!File.Exists(outputFile))
                    break;

                long currentBytes = new FileInfo(outputFile).Length;
                ConsoleLogger.Info("GIF",
                    $"Pass {attempt + 1}: {profile.MaxDimension}px, {profile.Fps} fps, {profile.MaxColors} colors -> {currentBytes / 1024.0:F1} KB" +
                    (targetBytes.HasValue ? $" (target <= {opts.TargetMaxSizeMb:0.##} MB)" : string.Empty));

                if (currentBytes < bestBytes)
                {
                    bestBytes = currentBytes;
                    if (targetBytes.HasValue && currentBytes > targetBytes.Value && attempt + 1 < maxAttempts)
                    {
                        bestTempPath ??= outputFile + ".best.tmp";
                        File.Copy(outputFile, bestTempPath, overwrite: true);
                    }
                }

                if (!targetBytes.HasValue || currentBytes <= targetBytes.Value || attempt + 1 >= maxAttempts)
                    break;

                // Adaptively tighten parameters proportionally to how far over budget we are.
                double ratio = (double)targetBytes.Value / Math.Max(1, currentBytes);
                double dimFactor = Math.Clamp(Math.Sqrt(ratio) * 0.90, 0.58, 0.88);
                int nextDim = Math.Max(160, ((int)Math.Round((profile.MaxDimension * dimFactor) / 2.0)) * 2);
                if (nextDim >= profile.MaxDimension && profile.MaxDimension > 160)
                    nextDim = Math.Max(160, profile.MaxDimension - 32);

                int nextFps = profile.Fps > 5
                    ? Math.Max(5, (int)Math.Floor(profile.Fps * (ratio < 0.5 ? 0.78 : 0.88)))
                    : 5;
                if (nextFps >= profile.Fps && profile.Fps > 5 && attempt >= 1)
                    nextFps = profile.Fps - 1;

                int nextColors = profile.MaxColors switch
                {
                    > 128 => 128,
                    > 96 => 96,
                    > 64 => 64,
                    > 48 => 48,
                    _ => 32
                };

                profile = new GifEncodeProfile(
                    MaxDimension: nextDim,
                    Fps: nextFps,
                    MaxColors: nextColors,
                    Dither: "none",
                    DenoiseFilter: "hqdn3d=2.5:2.5:8:8",
                    MagickFuzzPercent: Math.Min(5.0, Math.Max(3.0, profile.MagickFuzzPercent + 1.0)));
            }

            // Guarantee we never end up with a file larger than our best pass.
            if (!string.IsNullOrEmpty(bestTempPath) && File.Exists(bestTempPath) && File.Exists(outputFile))
            {
                long finalBytes = new FileInfo(outputFile).Length;
                if (bestBytes < finalBytes)
                    File.Move(bestTempPath, outputFile, overwrite: true);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(bestTempPath))
            {
                try { if (File.Exists(bestTempPath)) File.Delete(bestTempPath); } catch { /* best effort */ }
            }
        }

        progress?.Report(100);
    }

    private static async Task TryOptimizeGifWithMagickAsync(
        string gifPath,
        double durationSec,
        GifEncodeProfile profile,
        CancellationToken ct)
    {
        if (!File.Exists(gifPath))
            return;

        // Guard RAM and runtime on very long multi-minute GIFs (pure FFmpeg already cut 70-85%).
        long originalBytes = new FileInfo(gifPath).Length;
        if (originalBytes <= 0 || originalBytes > 25L * 1024 * 1024)
            return;
        if (durationSec > 0 && durationSec * profile.Fps > 360)
            return;

        string optPath = gifPath + ".opt.tmp";
        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var collection = new MagickImageCollection(gifPath);
                if (collection.Count <= 1 || collection.Count > 400)
                    return;

                ct.ThrowIfCancellationRequested();
                collection.Coalesce();

                var fuzz = new Percentage(Math.Clamp(profile.MagickFuzzPercent, 1.0, 5.0));
                foreach (var frame in collection)
                    frame.ColorFuzz = fuzz;

                ct.ThrowIfCancellationRequested();
                collection.OptimizeTransparency();

                ct.ThrowIfCancellationRequested();
                collection.Write(optPath, MagickFormat.Gif);
            }, ct);

            if (File.Exists(optPath))
            {
                long optimizedBytes = new FileInfo(optPath).Length;
                if (optimizedBytes > 0 && optimizedBytes < originalBytes)
                {
                    File.Move(optPath, gifPath, overwrite: true);
                    double savedPct = (1.0 - (double)optimizedBytes / originalBytes) * 100.0;
                    ConsoleLogger.Info("GIF",
                        $"Magick transparency optimize: {originalBytes / 1024.0:F1} KB -> {optimizedBytes / 1024.0:F1} KB (-{savedPct:F1}%)");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warning("GIF", $"Optional Magick GIF optimize skipped: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(optPath)) File.Delete(optPath); } catch { /* best effort */ }
        }
    }

    // Smart argument builders (used by ConversionRouter)

    /// <summary>
    /// Build FFmpeg arguments for a video conversion with quality settings
    /// and optional hardware acceleration.
    /// </summary>
    public string BuildVideoArguments(string inputFile, string outputFile,
                                       double qualityLevel, string targetFormat,
                                       bool preferHardware = true)
    {
        if (targetFormat.Equals("gif", StringComparison.OrdinalIgnoreCase))
        {
            var profile = ResolveGifProfile(new GifConversionOptions(QualityLevel: qualityLevel));
            return BuildGifArguments(inputFile, outputFile, profile);
        }

        int crf = 28 - (int)((qualityLevel / 100.0) * 10);
        string preset = qualityLevel < 40 ? "fast" : (qualityLevel < 80 ? "medium" : "slow");

        return targetFormat.ToLowerInvariant() switch
        {
            "avi" => BuildAviArguments(inputFile, outputFile, qualityLevel),
            "webm" => BuildWebmArguments(inputFile, outputFile, qualityLevel),
            "mp4" or "mov" or "mkv" => BuildH264ContainerArguments(inputFile, outputFile, targetFormat, crf, preset, qualityLevel, preferHardware),
            _ => BuildH264ContainerArguments(inputFile, outputFile, targetFormat, crf, preset, qualityLevel, preferHardware)
        };
    }

    /// <summary>
    /// Build FFmpeg arguments for audio conversion (format change, not extraction).
    /// Preserves embedded album art when the output format supports it.
    /// </summary>
    public string BuildAudioArguments(string inputFile, string outputFile, string? targetFormat = null, double qualityLevel = 75)
        => BuildAudioArgumentsCore(inputFile, outputFile, targetFormat, qualityLevel, preserveCover: true);

    private static string BuildAudioArgumentsCore(string inputFile, string outputFile, string? targetFormat,
                                                  double qualityLevel, bool preserveCover)
    {
        string target = string.IsNullOrWhiteSpace(targetFormat)
            ? Path.GetExtension(outputFile).TrimStart('.').ToLowerInvariant()
            : targetFormat.TrimStart('.').ToLowerInvariant();

        int vbr = QualityToAudioVbr(qualityLevel);
        string codecArgs = target switch
        {
            "mp3" => $"-c:a libmp3lame -q:a {vbr}",
            "wav" => "-c:a pcm_s16le",
            "flac" => "-c:a flac",
            "m4a" or "aac" => "-c:a aac -b:a 192k",
            "ogg" => $"-c:a libvorbis -q:a {Math.Clamp((int)Math.Round(qualityLevel / 10.0), 1, 10)}",
            _ => string.Empty
        };

        bool keepCover = preserveCover && target is ("mp3" or "m4a" or "aac" or "flac" or "ogg");
        string id3 = target == "mp3" ? "-id3v2_version 3 " : string.Empty;

        if (keepCover)
        {
            return $"-i \"{inputFile}\" -map_metadata 0 -map 0:a:0 -map 0:v? " +
                   $"-c:v copy -disposition:v:0 attached_pic {codecArgs} {id3}-y \"{outputFile}\"";
        }

        // WAV and similar: audio only
        return $"-i \"{inputFile}\" -map_metadata 0 -map 0:a:0 {codecArgs} -y \"{outputFile}\"";
    }

    /// <summary>Whether generated arguments use a hardware encoder that can be retried in software.</summary>
    public bool UsesHardwareEncoder(string arguments)
    {
        return arguments.Contains("_nvenc", StringComparison.OrdinalIgnoreCase) ||
               arguments.Contains("_qsv", StringComparison.OrdinalIgnoreCase) ||
               arguments.Contains("_amf", StringComparison.OrdinalIgnoreCase);
    }

    // Hardware acceleration probing

    /// <summary>
    /// Probe the installed FFmpeg for available hardware encoders.
    /// Runs once and caches the result.
    /// </summary>
    private HardwareCapabilities ProbeHardwareEncoders()
    {
        var caps = new HardwareCapabilities();

        try
        {
            if (!File.Exists(_config.FFmpegPath)) return caps;

            var (listExit, listOutput) = RunProbeSilent("-encoders -hide_banner");
            if (listExit != 0 || string.IsNullOrEmpty(listOutput))
                return caps;

            bool CanRunEncoder(string encoderName)
            {
                var (exitCode, _) = RunProbeSilent(
                    $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x144:r=1:d=0.1 -frames:v 1 -c:v {encoderName} -f null -");
                return exitCode == 0;
            }

            // Probe silently in priority order (NVENC -> QuickSync -> AMF) and stop once a working GPU encoder is confirmed.
            if (listOutput.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase) && CanRunEncoder("h264_nvenc"))
            {
                caps.HasNvenc = true;
                caps.HasNvencHevc = listOutput.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase) && CanRunEncoder("hevc_nvenc");
            }
            else if (listOutput.Contains("h264_qsv", StringComparison.OrdinalIgnoreCase) && CanRunEncoder("h264_qsv"))
            {
                caps.HasQuickSync = true;
                caps.HasQuickSyncHevc = listOutput.Contains("hevc_qsv", StringComparison.OrdinalIgnoreCase) && CanRunEncoder("hevc_qsv");
            }
            else if (listOutput.Contains("h264_amf", StringComparison.OrdinalIgnoreCase) && CanRunEncoder("h264_amf"))
            {
                caps.HasAmf = true;
            }

            if (caps.HasAnyHardwareEncoder)
                ConsoleLogger.Success("GPU", $"Hardware encoder ready: {caps}.");
            else
                ConsoleLogger.Info("GPU", "No hardware video encoder detected — using CPU (libx264).");
        }
        catch
        {
            // Probe failure is non-fatal — we fall back to software encoding
        }

        return caps;
    }

    private (int ExitCode, string Output) RunProbeSilent(string arguments, int timeoutMs = 4000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.FFmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return (-1, string.Empty);

            _processManager.TrackProcess(process.Id);
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                KillSafe(process);
                _processManager.UntrackProcess(process.Id);
                return (-1, string.Empty);
            }

            _processManager.UntrackProcess(process.Id);
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            return (process.ExitCode, stdout + "\n" + stderr);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }

    /// <summary>Select the best available H.264 encoder for MP4/MOV/MKV outputs.</summary>
    private string SelectH264Encoder(bool preferHardware)
    {
        if (!preferHardware)
        {
            return "libx264";
        }

        var caps = HwCapabilities;
        if (caps.HasNvenc) return "h264_nvenc";
        if (caps.HasQuickSync) return "h264_qsv";
        if (caps.HasAmf) return "h264_amf";
        return "libx264";
    }

    private string BuildH264ContainerArguments(string inputFile, string outputFile, string targetFormat,
                                               int crf, string preset, double qualityLevel,
                                               bool preferHardware)
    {
        string encoder = SelectH264Encoder(preferHardware);
        string videoArgs = BuildH264QualityArguments(encoder, crf, preset, qualityLevel);
        string fastStart = targetFormat.Equals("mp4", StringComparison.OrdinalIgnoreCase) ||
                           targetFormat.Equals("mov", StringComparison.OrdinalIgnoreCase)
            ? "-movflags +faststart"
            : string.Empty;

        return $"-i \"{inputFile}\" -map 0:v:0 -map 0:a? {videoArgs} -pix_fmt yuv420p -c:a aac -b:a 192k {fastStart} -y \"{outputFile}\"";
    }

    private static string BuildH264QualityArguments(string encoder, int crf, string preset, double qualityLevel)
    {
        if (encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            int cq = Math.Max(18, 35 - (int)((qualityLevel / 100.0) * 17));
            return $"-c:v {encoder} -rc vbr -cq {cq} -b:v 0 -preset p4";
        }

        if (encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            return $"-c:v {encoder} -global_quality {crf} -preset medium";
        }

        if (encoder.Contains("amf", StringComparison.OrdinalIgnoreCase))
        {
            return $"-c:v {encoder} -quality balanced -rc cqp -qp_i {crf} -qp_p {crf}";
        }

        return $"-c:v libx264 -crf {crf} -preset {preset}";
    }

    private static string BuildAviArguments(string inputFile, string outputFile, double qualityLevel)
    {
        int qscale = Math.Clamp(31 - (int)((qualityLevel / 100.0) * 26), 2, 31);
        return $"-i \"{inputFile}\" -map 0:v:0 -map 0:a? -c:v mpeg4 -qscale:v {qscale} -c:a libmp3lame -b:a 192k -y \"{outputFile}\"";
    }

    private static string BuildWebmArguments(string inputFile, string outputFile, double qualityLevel)
    {
        int crf = Math.Clamp(42 - (int)((qualityLevel / 100.0) * 24), 18, 42);
        return $"-i \"{inputFile}\" -map 0:v:0 -map 0:a? -c:v libvpx-vp9 -crf {crf} -b:v 0 -deadline good -cpu-used 4 -row-mt 1 -c:a libopus -b:a 128k -y \"{outputFile}\"";
    }

    private static int QualityToAudioVbr(double qualityLevel)
    {
        return Math.Clamp(9 - (int)((qualityLevel / 100.0) * 7), 2, 9);
    }

    private async Task<ProcessResult> RunFFmpegAsync(string arguments, CancellationToken ct,
                                                     IProgress<double>? progress = null,
                                                     TimeSpan? expectedDuration = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.FFmpegPath,
            Arguments = BuildExecutionArguments(arguments, progress != null),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new RecentLines(80);
        var stderr = new RecentLines(120);
        TimeSpan? duration = expectedDuration is { TotalMilliseconds: > 0 } ? expectedDuration : null;
        double lastProgress = 0;
        object progressLock = new();

        void ReportProgress(TimeSpan? position)
        {
            if (progress == null)
            {
                return;
            }

            lock (progressLock)
            {
                double nextProgress;
                if (position.HasValue && duration.HasValue && duration.Value.TotalMilliseconds > 0)
                {
                    nextProgress = Math.Clamp(position.Value.TotalMilliseconds / duration.Value.TotalMilliseconds * 100, 0, 99);
                }
                else
                {
                    if (_reportIndeterminateProgress)
                    {
                        progress.Report(-1);
                        return;
                    }
                    nextProgress = Math.Min(lastProgress + 0.25, 95);
                }

                if (nextProgress > lastProgress)
                {
                    lastProgress = nextProgress;
                    progress.Report(nextProgress);
                }
            }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            if (TryParseProgressTimestamp(e.Data, out var position))
            {
                ReportProgress(position);
            }
            else if (!duration.HasValue && e.Data.StartsWith("progress=", StringComparison.OrdinalIgnoreCase))
            {
                ReportProgress(null);
            }
            else if (!e.Data.StartsWith("progress=", StringComparison.OrdinalIgnoreCase))
                stdout.Add(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            duration ??= TryParseDuration(e.Data);
            if (TryParseStatusTimestamp(e.Data, out var position))
            {
                ReportProgress(position);
            }
            else stderr.Add(e.Data);
        };

        ConsoleLogger.Info("Process", $"Starting {Path.GetFileName(_config.FFmpegPath)}.");
        process.Start();
        _processManager.TrackProcess(process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            process.WaitForExit();
            _processManager.UntrackProcess(process.Id);
        }
        catch (OperationCanceledException)
        {
            KillSafe(process);
            _processManager.UntrackProcess(process.Id);
            ConsoleLogger.Warning("Process", $"Cancelled {Path.GetFileName(_config.FFmpegPath)}.");
            throw;
        }

        var result = new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        if (result.ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            string executableName = Path.GetFileName(_config.FFmpegPath);
            ConsoleLogger.Error("Process", $"{executableName} exited with code {result.ExitCode}: {details}");
            throw new ProcessExecutionException(executableName, result.ExitCode, result.StdOut, result.StdErr);
        }

        ConsoleLogger.Success("Process", $"{Path.GetFileName(_config.FFmpegPath)} completed.");
        return result;
    }

    private sealed class RecentLines(int capacity)
    {
        private readonly Queue<string> _lines = new();
        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line.Length > 2000 ? line[..2000] : line);
                while (_lines.Count > capacity) _lines.Dequeue();
            }
        }
        public override string ToString()
        {
            lock (_lines) return string.Join(Environment.NewLine, _lines);
        }
    }

    private static string BuildExecutionArguments(string arguments, bool enableProgress)
    {
        string progressArgs = enableProgress ? "-progress pipe:1 -nostats " : string.Empty;
        return $"-hide_banner -nostdin {progressArgs}{arguments}";
    }

    private static bool TryParseProgressTimestamp(string line, out TimeSpan position)
    {
        position = default;

        if (line.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(line["out_time_ms=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds))
        {
            position = TimeSpan.FromMilliseconds(microseconds / 1000d);
            return true;
        }

        if (line.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseTimestamp(line["out_time=".Length..], out position);
        }

        return false;
    }

    private static bool TryParseStatusTimestamp(string line, out TimeSpan position)
    {
        position = default;
        Match match = StatusTimeRegex.Match(line);
        return match.Success && TryParseTimestamp(match.Groups["time"].Value, out position);
    }

    private static TimeSpan? TryParseDuration(string line)
    {
        Match match = DurationRegex.Match(line);
        return match.Success && TryParseTimestamp(match.Groups["time"].Value, out var duration)
            ? duration
            : null;
    }

    private static bool TryParseTimestamp(string value, out TimeSpan timestamp)
    {
        timestamp = default;
        string[] parts = value.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return false;
        }

        timestamp = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static void KillSafe(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already exited.
        }
    }

    private void EnsureFFmpeg()
    {
        if (!File.Exists(_config.FFmpegPath))
            throw new FileNotFoundException($"FFmpeg not found at: {_config.FFmpegPath}");
    }
}

/// <summary>
/// Cached result of hardware encoder probing.
/// </summary>
public class HardwareCapabilities
{
    public bool HasNvenc { get; set; }
    public bool HasNvencHevc { get; set; }
    public bool HasQuickSync { get; set; }
    public bool HasQuickSyncHevc { get; set; }
    public bool HasAmf { get; set; }

    public bool HasAnyHardwareEncoder =>
        HasNvenc || HasNvencHevc || HasQuickSync || HasQuickSyncHevc || HasAmf;

    public override string ToString()
    {
        if (!HasAnyHardwareEncoder) return "Software only";
        var encoders = new List<string>();
        if (HasNvenc || HasNvencHevc) encoders.Add("NVIDIA NVENC");
        if (HasQuickSync || HasQuickSyncHevc) encoders.Add("Intel QuickSync");
        if (HasAmf) encoders.Add("AMD AMF");
        return string.Join(", ", encoders);
    }
}
