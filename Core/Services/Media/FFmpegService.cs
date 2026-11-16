using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
        VideoCutMode mode = clips.Count == 1 && clips[0].Start <= TimeSpan.FromMilliseconds(50)
            ? VideoCutMode.SmartAuto
            : VideoCutMode.AccurateEncode;

        await ExportMontageAsync(
            new VideoMontageExportRequest(clips, outputFile, format, mode, VideoResolutionPreset.Original),
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

        string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        bool inVideoStream = false;
        foreach (string line in lines)
        {
            if (Regex.IsMatch(line, @"^\s*Stream #\d+:\d+.*?: Video:", RegexOptions.CultureInvariant))
            {
                if (hasVideo)
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

        if (validInput.Count == 0)
            throw new ArgumentException("At least one valid montage clip (>= 40ms) is required.", nameof(request));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);

        // Ensure every clip has accurate source dimensions, FPS, and audio-presence flags.
        var probeCache = new Dictionary<string, VideoMediaInfo>(StringComparer.OrdinalIgnoreCase);
        var clips = new List<VideoMontageClip>(validInput.Count);
        foreach (VideoMontageClip clip in validInput)
        {
            if (clip.SourceWidth <= 0 || clip.SourceHeight <= 0 || clip.SourceFps <= 0)
            {
                if (!probeCache.TryGetValue(clip.SourcePath, out VideoMediaInfo? probed))
                {
                    probed = await ProbeVideoAsync(clip.SourcePath, ct).ConfigureAwait(false);
                    probeCache[clip.SourcePath] = probed;
                }

                clips.Add(clip with
                {
                    HasAudio = probed.HasAudio,
                    SourceWidth = clip.SourceWidth > 0 ? clip.SourceWidth : probed.DisplayWidth,
                    SourceHeight = clip.SourceHeight > 0 ? clip.SourceHeight : probed.DisplayHeight,
                    SourceFps = clip.SourceFps > 0 ? clip.SourceFps : probed.Fps
                });
            }
            else
            {
                clips.Add(clip);
            }
        }

        string targetFmt = NormalizeOutputFormat(request.OutputFormat, request.OutputPath);
        TimeSpan totalDuration = TimeSpan.FromSeconds(clips.Sum(c => c.Duration.TotalSeconds));

        if (CanUseLosslessCopy(clips, request, targetFmt))
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

        await ExportSmartRenderAsync(clips, request, targetFmt, totalDuration, ct, progress).ConfigureAwait(false);
        progress?.Report(100);
    }

    private static bool CanUseLosslessCopy(
        IReadOnlyList<VideoMontageClip> clips,
        VideoMontageExportRequest request,
        string targetFmt)
    {
        if (request.CutMode == VideoCutMode.AccurateEncode)
            return false;

        if (request.Resolution != VideoResolutionPreset.Original)
            return false;

        string firstSource = clips[0].SourcePath;
        bool singleSource = clips.All(c => string.Equals(c.SourcePath, firstSource, StringComparison.OrdinalIgnoreCase));
        if (!singleSource)
            return false;

        string outExt = Path.GetExtension(request.OutputPath).TrimStart('.').ToLowerInvariant();
        string effectiveTarget = string.IsNullOrEmpty(outExt) ? targetFmt : outExt;
        string srcExt = Path.GetExtension(firstSource).TrimStart('.').ToLowerInvariant();
        if (request.CutMode == VideoCutMode.FastLossless)
        {
            // Fast copy is reliable across matching containers or H.264-compatible containers.
            if (string.Equals(srcExt, effectiveTarget, StringComparison.OrdinalIgnoreCase))
                return true;
            return srcExt is "mp4" or "m4v" or "mov" or "mkv" or "ts" or "mts" &&
                   effectiveTarget is "mp4" or "mov" or "mkv";
        }

        // SmartAuto uses lossless only when starting at the very beginning (exact keyframe) in matching container.
        return clips.Count == 1 &&
               clips[0].Start <= TimeSpan.FromMilliseconds(50) &&
               string.Equals(srcExt, effectiveTarget, StringComparison.OrdinalIgnoreCase);
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
            string args = $"-ss {ss} -i \"{c.SourcePath}\" -t {dur} -map 0:v:0 -map 0:a:0? -c copy -avoid_negative_ts make_zero {fastStart}-y \"{outputPath}\"";
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
                string args = $"-ss {FormatTs(c.Start)} -i \"{c.SourcePath}\" -t {FormatTs(c.Duration)} -map 0:v:0 -map 0:a:0? -c copy -avoid_negative_ts make_zero -y \"{partPath}\"";
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

    private async Task ExportSmartRenderAsync(
        IReadOnlyList<VideoMontageClip> clips,
        VideoMontageExportRequest request,
        string targetFmt,
        TimeSpan totalDuration,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        var (targetW, targetH) = ResolveTargetCanvas(clips, request.Resolution);
        double targetFps = ResolveTargetFps(clips);
        string fpsStr = targetFps.ToString("0.###", CultureInfo.InvariantCulture);
        bool anyAudio = clips.Any(c => c.HasAudio);
        int crf = Math.Clamp(request.QualityCrf, 14, 34);

        string scaleFilter = BuildClipVideoFilter(targetW, targetH, request.AspectFitMode, fpsStr);

        if (clips.Count == 1)
        {
            VideoMontageClip c = clips[0];
            string ss = FormatTs(c.Start);
            string dur = FormatTs(c.Duration);
            string audioArgs = c.HasAudio
                ? "-map 0:a:0? -af \"aformat=sample_rates=48000:channel_layouts=stereo,aresample=async=1:first_pts=0\""
                : "-an";

            await RunWithCpuFallbackAsync(
                preferHw =>
                {
                    string enc = BuildContainerEncodeArgs(targetFmt, crf, c.HasAudio, preferHw);
                    return $"-ss {ss} -t {dur} -i \"{c.SourcePath}\" -map 0:v:0 -vf \"{scaleFilter}\" {audioArgs} {enc} -max_muxing_queue_size 1024 -y \"{request.OutputPath}\"";
                },
                ct, progress, totalDuration).ConfigureAwait(false);
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "CortexFX_Montage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string filterScriptPath = Path.Combine(tempRoot, "montage_filter.txt");

        try
        {
            var inputs = new StringBuilder();
            var filter = new StringBuilder();
            var concatInputs = new StringBuilder();

            for (int i = 0; i < clips.Count; i++)
            {
                VideoMontageClip c = clips[i];
                inputs.Append($"-ss {FormatTs(c.Start)} -t {FormatTs(c.Duration)} -i \"{c.SourcePath}\" ");
                filter.Append($"[{i}:v:0]{scaleFilter},setpts=PTS-STARTPTS[v{i}];");

                if (anyAudio)
                {
                    if (c.HasAudio)
                    {
                        filter.Append($"[{i}:a:0]aformat=sample_rates=48000:channel_layouts=stereo,aresample=async=1:first_pts=0,asetpts=PTS-STARTPTS[a{i}];");
                    }
                    else
                    {
                        string durSec = c.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                        filter.Append($"anullsrc=r=48000:cl=stereo:d={durSec}[a{i}];");
                    }
                    concatInputs.Append($"[v{i}][a{i}]");
                }
                else
                {
                    concatInputs.Append($"[v{i}]");
                }
            }

            if (anyAudio)
                filter.Append($"{concatInputs}concat=n={clips.Count}:v=1:a=1[vout][aout]");
            else
                filter.Append($"{concatInputs}concat=n={clips.Count}:v=1:a=0[vout]");

            await File.WriteAllTextAsync(filterScriptPath, filter.ToString(), ct).ConfigureAwait(false);

            string mapArgs = anyAudio ? "-map \"[vout]\" -map \"[aout]\"" : "-map \"[vout]\"";
            await RunWithCpuFallbackAsync(
                preferHw =>
                {
                    string enc = BuildContainerEncodeArgs(targetFmt, crf, anyAudio, preferHw);
                    return $"{inputs}-filter_complex_script \"{filterScriptPath}\" {mapArgs} {enc} -max_muxing_queue_size 1024 -y \"{request.OutputPath}\"";
                },
                ct, progress, totalDuration).ConfigureAwait(false);
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

    private static string BuildClipVideoFilter(int targetW, int targetH, VideoAspectFitMode aspectFit, string fpsStr)
    {
        string scaleAndFit = aspectFit == VideoAspectFitMode.FillCrop
            ? $"scale={targetW}:{targetH}:force_original_aspect_ratio=increase:flags=lanczos,crop={targetW}:{targetH}:(iw-ow)/2:(ih-oh)/2"
            : $"scale={targetW}:{targetH}:force_original_aspect_ratio=decrease:flags=lanczos,pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2:color=black";

        // Always place setsar=1 AFTER scale/pad/crop so concat sees uniform 1:1 SAR across mixed clips.
        return $"{scaleAndFit},setsar=1,fps={fpsStr},format=yuv420p";
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
                return $"-c:v h264_nvenc -cq {crf} -preset p4";
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
        value.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

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
    public async Task ConvertToGifAsync(string inputFile, string outputFile, CancellationToken ct = default,
                                        IProgress<double>? progress = null)
    {
        EnsureFFmpeg();
        // Two-pass palette generation for high-quality GIF output
        string args = $"-i \"{inputFile}\" -vf \"fps=10,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -loop 0 -y \"{outputFile}\"";
        await RunFFmpegAsync(args, ct, progress);
        progress?.Report(100);
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
            return $"-i \"{inputFile}\" -vf \"fps=10,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -loop 0 -y \"{outputFile}\"";
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

            var result = _processManager.RunSync(_config.FFmpegPath, "-encoders -hide_banner");
            string output = result.StdOut + result.StdErr;

            caps.HasNvenc = output.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase);
            caps.HasNvencHevc = output.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase);
            caps.HasQuickSync = output.Contains("h264_qsv", StringComparison.OrdinalIgnoreCase);
            caps.HasQuickSyncHevc = output.Contains("hevc_qsv", StringComparison.OrdinalIgnoreCase);
            caps.HasAmf = output.Contains("h264_amf", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Probe failure is non-fatal — we fall back to software encoding
        }

        return caps;
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
            return $"-c:v {encoder} -cq {cq} -preset p4";
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
