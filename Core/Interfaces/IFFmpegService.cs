namespace CortexFX.Core.Interfaces;

/// <summary>Time range to keep when cutting a video.</summary>
public readonly record struct VideoCutSegment(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>
/// FFmpeg jobs: convert, trim, cut segments, extract audio, make GIF.
/// </summary>
public interface IFFmpegService
{
    /// <summary>Run FFmpeg with the given argument string.</summary>
    Task ConvertAsync(string inputFile, string outputFile, string arguments,
                      CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>Trim audio between two times (stream copy when possible).</summary>
    Task TrimAudioAsync(string inputFile, string outputFile,
                        TimeSpan start, TimeSpan end, CancellationToken ct = default);

    /// <summary>
    /// Keep one or more segments and join them into one file.
    /// Tries stream copy first; re-encodes if copy fails.
    /// </summary>
    Task CutVideoSegmentsAsync(string inputFile, string outputFile,
                               IReadOnlyList<VideoCutSegment> segments,
                               CancellationToken ct = default,
                               IProgress<double>? progress = null);

    /// <summary>Pull audio out of a video.</summary>
    Task ExtractAudioAsync(string inputFile, string outputFile, CancellationToken ct = default,
                           IProgress<double>? progress = null);

    /// <summary>Make a GIF (palette pass included).</summary>
    Task ConvertToGifAsync(string inputFile, string outputFile, CancellationToken ct = default,
                           IProgress<double>? progress = null);
}
