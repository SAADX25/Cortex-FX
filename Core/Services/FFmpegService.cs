using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services;

/// <summary>
/// Production FFmpeg service with hardware acceleration detection,
/// smart argument generation, and async progress reporting.
/// </summary>
public sealed class FFmpegService : IFFmpegService
{
    private readonly IAppConfiguration _config;
    private readonly IProcessManager _processManager;

    // Cached HW encoder probe result (lazy, thread-safe)
    private readonly Lazy<HardwareCapabilities> _hwCaps;

    public FFmpegService(IAppConfiguration config, IProcessManager processManager)
    {
        _config = config;
        _processManager = processManager;
        _hwCaps = new Lazy<HardwareCapabilities>(() => ProbeHardwareEncoders());
    }

    /// <summary>Current machine's hardware encoding capabilities.</summary>
    public HardwareCapabilities HwCapabilities => _hwCaps.Value;

    // ------------------------------------------------------------------
    // IFFmpegService implementation
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task ConvertAsync(string inputFile, string outputFile, string arguments,
                                    CancellationToken ct = default, IProgress<double>? progress = null)
    {
        EnsureFFmpeg();
        await _processManager.RunAsync(_config.FFmpegPath, arguments, ct);
        progress?.Report(100);
    }

    /// <inheritdoc />
    public async Task TrimAudioAsync(string inputFile, string outputFile,
                                      TimeSpan start, TimeSpan end, CancellationToken ct = default)
    {
        EnsureFFmpeg();
        string ss = start.ToString(@"hh\:mm\:ss\.fff");
        string to = end.ToString(@"hh\:mm\:ss\.fff");
        string args = $"-i \"{inputFile}\" -ss {ss} -to {to} -c copy -y \"{outputFile}\"";
        await _processManager.RunAsync(_config.FFmpegPath, args, ct);
    }

    /// <inheritdoc />
    public async Task ExtractAudioAsync(string inputFile, string outputFile, CancellationToken ct = default)
    {
        EnsureFFmpeg();
        string args = $"-i \"{inputFile}\" -vn -y \"{outputFile}\"";
        await _processManager.RunAsync(_config.FFmpegPath, args, ct);
    }

    /// <inheritdoc />
    public async Task ConvertToGifAsync(string inputFile, string outputFile, CancellationToken ct = default)
    {
        EnsureFFmpeg();
        // Two-pass palette generation for high-quality GIF output
        string args = $"-i \"{inputFile}\" -vf \"fps=10,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -loop 0 -y \"{outputFile}\"";
        await _processManager.RunAsync(_config.FFmpegPath, args, ct);
    }

    // ------------------------------------------------------------------
    // Smart argument builders (used by ConversionRouter)
    // ------------------------------------------------------------------

    /// <summary>
    /// Build FFmpeg arguments for a video conversion with quality settings
    /// and optional hardware acceleration.
    /// </summary>
    public string BuildVideoArguments(string inputFile, string outputFile,
                                       double qualityLevel, string targetFormat)
    {
        if (targetFormat.Equals("gif", StringComparison.OrdinalIgnoreCase))
        {
            return $"-i \"{inputFile}\" -vf \"fps=10,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -loop 0 -y \"{outputFile}\"";
        }

        int crf = 28 - (int)((qualityLevel / 100.0) * 10);
        string preset = qualityLevel < 40 ? "fast" : (qualityLevel < 80 ? "medium" : "slow");

        // Try hardware-accelerated encoding for H.264/H.265 targets
        string encoder = SelectEncoder(targetFormat);
        string qualityArgs;

        if (encoder.Contains("nvenc"))
        {
            // NVENC uses -cq (constant quality) instead of -crf
            int cq = Math.Max(18, 35 - (int)((qualityLevel / 100.0) * 17));
            qualityArgs = $"-c:v {encoder} -cq {cq} -preset p4";
        }
        else if (encoder.Contains("qsv"))
        {
            qualityArgs = $"-c:v {encoder} -global_quality {crf} -preset medium";
        }
        else
        {
            // Software fallback (libx264/libx265)
            qualityArgs = $"-crf {crf} -preset {preset}";
        }

        return $"-i \"{inputFile}\" {qualityArgs} -y \"{outputFile}\"";
    }

    /// <summary>
    /// Build FFmpeg arguments for audio conversion (format change, not extraction).
    /// </summary>
    public string BuildAudioArguments(string inputFile, string outputFile)
    {
        return $"-i \"{inputFile}\" -vn -y \"{outputFile}\"";
    }

    // ------------------------------------------------------------------
    // Hardware acceleration probing
    // ------------------------------------------------------------------

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

    /// <summary>Select the best available encoder for the target format.</summary>
    private string SelectEncoder(string targetFormat)
    {
        var caps = HwCapabilities;
        bool isHevc = targetFormat.Equals("mkv", StringComparison.OrdinalIgnoreCase);

        if (isHevc)
        {
            if (caps.HasNvencHevc) return "hevc_nvenc";
            if (caps.HasQuickSyncHevc) return "hevc_qsv";
        }

        // H.264 targets (mp4, avi, mov, webm)
        if (caps.HasNvenc) return "h264_nvenc";
        if (caps.HasQuickSync) return "h264_qsv";
        if (caps.HasAmf) return "h264_amf";

        // Software fallback
        return isHevc ? "libx265" : "libx264";
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
