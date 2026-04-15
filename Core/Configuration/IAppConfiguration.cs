namespace CortexFX.Core.Configuration;

/// <summary>
/// Centralized configuration contract. Replaces all hardcoded paths 
/// and scattered ResourcesDirectory property lookups.
/// </summary>
public interface IAppConfiguration
{
    /// <summary>Root Resources directory containing external tools.</summary>
    string ResourcesDirectory { get; }

    /// <summary>Full path to ffmpeg.exe.</summary>
    string FFmpegPath { get; }

    /// <summary>Full path to magick.exe (ImageMagick).</summary>
    string MagickPath { get; }

    /// <summary>Full path to pdftocairo.exe.</summary>
    string PdfToCairoPath { get; }

    /// <summary>Directory for temporary thumbnails.</summary>
    string ThumbnailsDirectory { get; }

    /// <summary>Directory containing FFmpeg shared libraries (avcodec-58.dll etc.).</summary>
    string FFmpegLibsDirectory { get; }
}
