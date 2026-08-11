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
    private static readonly Regex DurationRegex = new(@"Duration:\s*(?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StatusTimeRegex = new(@"\btime=(?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Probed once per process
    private readonly Lazy<HardwareCapabilities> _hwCaps;

    public FFmpegService(IAppConfiguration config, IProcessManager processManager)
    {
        _config = config;
        _processManager = processManager;
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

        var valid = segments
            .Where(s => s.End > s.Start && s.Duration.TotalMilliseconds >= 40)
            .OrderBy(s => s.Start)
            .ToList();

        if (valid.Count == 0)
            throw new ArgumentException("No valid cut segments (end must be after start).", nameof(segments));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);

        if (valid.Count == 1)
        {
            progress?.Report(5);
            await ExtractSegmentAsync(inputFile, outputFile, valid[0], preferCopy: true, ct);
            progress?.Report(100);
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "CortexFX_Cut_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var partFiles = new List<string>(valid.Count);

        try
        {
            for (int i = 0; i < valid.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string partPath = Path.Combine(tempRoot, $"part_{i:D3}.mp4");
                await ExtractSegmentAsync(inputFile, partPath, valid[i], preferCopy: true, ct);
                partFiles.Add(partPath);
                progress?.Report(Math.Clamp((i + 1) * 70.0 / valid.Count, 1, 70));
            }

            string listPath = Path.Combine(tempRoot, "concat.txt");
            await File.WriteAllLinesAsync(listPath, partFiles.Select(ToConcatFileLine), ct);

            try
            {
                string concatCopy =
                    $"-f concat -safe 0 -i \"{listPath}\" -c copy -movflags +faststart -y \"{outputFile}\"";
                await RunFFmpegAsync(concatCopy, ct);
            }
            catch (ProcessExecutionException)
            {
                // Concat-copy can fail across odd codecs — re-encode the stitched result.
                string concatEncode =
                    $"-f concat -safe 0 -i \"{listPath}\" {BuildFastReencodeArgs()} -movflags +faststart -y \"{outputFile}\"";
                await RunFFmpegAsync(concatEncode, ct);
            }

            progress?.Report(100);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private async Task ExtractSegmentAsync(string inputFile, string outputFile, VideoCutSegment segment,
                                           bool preferCopy, CancellationToken ct)
    {
        string ss = FormatTs(segment.Start);
        string dur = FormatTs(segment.Duration);

        if (preferCopy)
        {
            try
            {
                // Input seek + duration + stream copy = fast cut on SSD / HDD.
                string copyArgs =
                    $"-ss {ss} -i \"{inputFile}\" -t {dur} -map 0 -c copy -avoid_negative_ts make_zero -y \"{outputFile}\"";
                await RunFFmpegAsync(copyArgs, ct);
                if (File.Exists(outputFile) && new FileInfo(outputFile).Length > 0)
                    return;
            }
            catch (ProcessExecutionException)
            {
                // Fall through to re-encode.
            }
        }

        string encodeArgs =
            $"-ss {ss} -i \"{inputFile}\" -t {dur} {BuildFastReencodeArgs()} -movflags +faststart -y \"{outputFile}\"";
        await RunFFmpegAsync(encodeArgs, ct);
    }

    private string BuildFastReencodeArgs()
    {
        var caps = HwCapabilities;
        if (caps.HasNvenc)
            return "-c:v h264_nvenc -cq 20 -preset p4 -c:a aac -b:a 160k";
        if (caps.HasQuickSync)
            return "-c:v h264_qsv -global_quality 20 -preset very_fast -c:a aac -b:a 160k";
        if (caps.HasAmf)
            return "-c:v h264_amf -quality speed -rc cqp -qp_i 20 -qp_p 20 -c:a aac -b:a 160k";
        return "-c:v libx264 -preset veryfast -crf 20 -c:a aac -b:a 160k";
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
        string args = BuildAudioArguments(inputFile, outputFile, targetFormat);
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

        bool keepCover = target is "mp3" or "m4a" or "aac" or "flac" or "ogg";
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
                                                     IProgress<double>? progress = null)
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
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        TimeSpan? duration = null;
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

            stdout.AppendLine(e.Data);
            if (TryParseProgressTimestamp(e.Data, out var position))
            {
                ReportProgress(position);
            }
            else if (!duration.HasValue && e.Data.StartsWith("progress=", StringComparison.OrdinalIgnoreCase))
            {
                ReportProgress(null);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            stderr.AppendLine(e.Data);
            duration ??= TryParseDuration(e.Data);
            if (TryParseStatusTimestamp(e.Data, out var position))
            {
                ReportProgress(position);
            }
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
        }
        catch (OperationCanceledException)
        {
            KillSafe(process);
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
