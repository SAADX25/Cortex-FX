namespace CortexFX.Core.Interfaces;

/// <summary>Time range to keep when cutting a video.</summary>
public readonly record struct VideoCutSegment(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>Single clip inside a multi-clip or multi-segment montage sequence.</summary>
public sealed record VideoMontageClip(
    string SourcePath,
    TimeSpan Start,
    TimeSpan End,
    bool HasAudio = true,
    int SourceWidth = 0,
    int SourceHeight = 0,
    double SourceFps = 0,
    double AudioVolume = 1.0,
    TimeSpan? TimelineStart = null,
    double VideoScale = 1.0,
    double VideoOffsetX = 0.0,
    double VideoOffsetY = 0.0,
    double VideoOpacity = 1.0)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>Probed video stream and container metadata.</summary>
public sealed record VideoMediaInfo(
    string FilePath,
    TimeSpan Duration,
    int CodedWidth,
    int CodedHeight,
    int DisplayWidth,
    int DisplayHeight,
    int RotationDegrees,
    double Fps,
    string VideoCodec,
    string AudioCodec,
    bool HasVideo,
    bool HasAudio,
    string ContainerFormat,
    long FileSizeBytes)
{
    public string AspectRatioLabel
    {
        get
        {
            if (DisplayWidth <= 0 || DisplayHeight <= 0) return "Unknown";
            int gcd = GreatestCommonDivisor(DisplayWidth, DisplayHeight);
            int w = DisplayWidth / gcd;
            int h = DisplayHeight / gcd;
            double ratio = DisplayWidth / (double)DisplayHeight;
            if (Math.Abs(ratio - 16.0 / 9.0) < 0.03) return "16:9";
            if (Math.Abs(ratio - 9.0 / 16.0) < 0.03) return "9:16";
            if (Math.Abs(ratio - 1.0) < 0.03) return "1:1";
            if (Math.Abs(ratio - 4.0 / 3.0) < 0.03) return "4:3";
            if (Math.Abs(ratio - 3.0 / 4.0) < 0.03) return "3:4";
            if (Math.Abs(ratio - 4.0 / 5.0) < 0.03) return "4:5";
            if (Math.Abs(ratio - 21.0 / 9.0) < 0.05) return "21:9";
            return w <= 32 && h <= 32 ? $"{w}:{h}" : $"{ratio:0.##}:1";
        }
    }

    public string ResolutionLabel =>
        DisplayWidth > 0 && DisplayHeight > 0 ? $"{DisplayWidth}×{DisplayHeight}" : "Unknown";

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            int t = a % b;
            a = b;
            b = t;
        }
        return Math.Max(1, a);
    }
}

public enum VideoCutMode
{
    SmartAuto,
    FastLossless,
    AccurateEncode
}

public enum VideoResolutionPreset
{
    Original,
    Uhd4K,
    Qhd1440,
    Fhd1080,
    Hd720,
    Sd480,
    Vertical1080x1920,
    Square1080x1080
}

public enum VideoAspectFitMode
{
    FitPad,
    FillCrop
}

/// <summary>Export specification for cutting and assembling a video montage.</summary>
public sealed record VideoMontageExportRequest(
    IReadOnlyList<VideoMontageClip> Clips,
    string OutputPath,
    string OutputFormat = "mp4",
    VideoCutMode CutMode = VideoCutMode.AccurateEncode,
    VideoResolutionPreset Resolution = VideoResolutionPreset.Original,
    VideoAspectFitMode AspectFitMode = VideoAspectFitMode.FitPad,
    int QualityCrf = 20,
    IReadOnlyList<VideoMontageClip>? AudioOverlayClips = null,
    IReadOnlyList<VideoMontageClip>? VideoOverlayClips = null);

/// <summary>
/// FFmpeg jobs: convert, trim, cut segments, montage export, extract audio, make GIF.
/// </summary>
public interface IFFmpegService
{
    string BuildVideoArguments(string inputFile, string outputFile, double qualityLevel, string targetFormat, bool preferHardware = true);
    string BuildAudioArguments(string inputFile, string outputFile, string? targetFormat = null, double qualityLevel = 75);
    bool UsesHardwareEncoder(string arguments);
    /// <summary>Run FFmpeg with the given argument string.</summary>
    Task ConvertAsync(string inputFile, string outputFile, string arguments,
                      CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>Trim audio between two times (stream copy when possible).</summary>
    Task TrimAudioAsync(string inputFile, string outputFile,
                        TimeSpan start, TimeSpan end, CancellationToken ct = default);

    /// <summary>
    /// Keep one or more segments and join them into one file in the caller's exact order.
    /// Tries stream copy first; re-encodes if copy fails.
    /// </summary>
    Task CutVideoSegmentsAsync(string inputFile, string outputFile,
                               IReadOnlyList<VideoCutSegment> segments,
                               CancellationToken ct = default,
                               IProgress<double>? progress = null);

    /// <summary>Probe video duration, resolution, rotation, FPS, and codecs via FFmpeg.</summary>
    Task<VideoMediaInfo> ProbeVideoAsync(string inputFile, CancellationToken ct = default);

    /// <summary>
    /// Cut and assemble one or more montage clips across any mix of resolutions, aspect ratios,
    /// audio streams, and formats into the target format and resolution.
    /// </summary>
    Task ExportMontageAsync(VideoMontageExportRequest request,
                            CancellationToken ct = default,
                            IProgress<double>? progress = null);

    /// <summary>Pull audio out of a video.</summary>
    Task ExtractAudioAsync(string inputFile, string outputFile, CancellationToken ct = default,
                           IProgress<double>? progress = null);

    /// <summary>Make a GIF (palette pass included).</summary>
    Task ConvertToGifAsync(string inputFile, string outputFile, CancellationToken ct = default,
                           IProgress<double>? progress = null);
}
