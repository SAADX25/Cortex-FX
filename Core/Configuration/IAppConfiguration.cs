namespace CortexFX.Core.Configuration;

/// <summary>
/// Paths to bundled tools under Resources/ (ffmpeg, magick, …).
/// </summary>
public interface IAppConfiguration
{
    /// <summary>Folder that holds the external tools.</summary>
    string ResourcesDirectory { get; }

    /// <summary>Path to ffmpeg.exe.</summary>
    string FFmpegPath { get; }

    /// <summary>Path to magick.exe.</summary>
    string MagickPath { get; }

    /// <summary>Path to pdftocairo.exe.</summary>
    string PdfToCairoPath { get; }

    /// <summary>Folder with FFmpeg DLLs used by FFME (avcodec, …).</summary>
    string FFmpegLibsDirectory { get; }
}
