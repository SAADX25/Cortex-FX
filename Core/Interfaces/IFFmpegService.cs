namespace CortexFX.Core.Interfaces;

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

    /// <summary>Extract audio from a video file (strips video stream).</summary>
    Task ExtractAudioAsync(string inputFile, string outputFile, CancellationToken ct = default,
                           IProgress<double>? progress = null);

    /// <summary>Convert video to GIF with palette generation.</summary>
    Task ConvertToGifAsync(string inputFile, string outputFile, CancellationToken ct = default,
                           IProgress<double>? progress = null);
}
