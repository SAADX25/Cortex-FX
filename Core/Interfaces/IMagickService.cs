namespace CortexFX.Core.Interfaces;

/// <summary>
/// Image convert options for Magick.
/// </summary>
public record ImageConversionOptions(
    int Quality = 75,
    int? ResizeWidth = null,
    int? ResizeHeight = null,
    bool MaintainAspectRatio = true,
    int? Dpi = null,
    bool Sharpen = false,
    bool Grayscale = false,
    bool AutoEnhance = false
);

/// <summary>
/// ImageMagick jobs: format convert and merge images to PDF.
/// </summary>
public interface IMagickService
{
    /// <summary>Convert one image to another format/size.</summary>
    Task ConvertImageAsync(string inputFile, string outputFile, ImageConversionOptions options,
                           CancellationToken ct = default);

    /// <summary>Combine images into one PDF.</summary>
    Task MergeImagesToPdfAsync(IReadOnlyList<string> imagePaths, string outputPath,
                               CancellationToken ct = default);
}
