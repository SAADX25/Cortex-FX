namespace CortexFX.Core.Interfaces;

/// <summary>Inclusive start / exclusive-ish end range for a keep-segment cut.</summary>
public readonly record struct VideoCutSegment(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>
/// Contract for all FFmpeg-based operations: video/audio conversion, trimming, extraction.
/// </summary>
public interface IFFmpegService
{
    /// <summary>Convert media using raw FFmpeg arguments.</summary>
    Task ConvertAsync(string inputFile, string outputFile, string arguments,
                      CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>Trim an audio file between start and end timestamps using stream copy.</summary>
    Task TrimAudioAsync(string inputFile, string outputFile,
                        TimeSpan start, TimeSpan end, CancellationToken ct = default);

    /// <summary>
    /// Cut one or more keep-segments from a video and concatenate into a single file.
    /// Prefers stream copy for speed; falls back to a fast re-encode when copy fails.
    /// </summary>
    Task CutVideoSegmentsAsync(string inputFile, string outputFile,
                               IReadOnlyList<VideoCutSegment> segments,
                               CancellationToken ct = default,
                               IProgress<double>? progress = null);

    /// <summary>Extract audio from a video file (strips video stream).</summary>
    Task ExtractAudioAsync(string inputFile, string outputFile, CancellationToken ct = default,
                           IProgress<double>? progress = null);

    /// <summary>Convert video to GIF with palette generation.</summary>
    Task ConvertToGifAsync(string inputFile, string outputFile, CancellationToken ct = default,
                           IProgress<double>? progress = null);
}
