namespace CortexFX.Core.Interfaces;

/// <summary>
/// Options for image conversion via ImageMagick.
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
/// Contract for all ImageMagick-based operations: image conversion and PDF merging.
/// </summary>
public interface IMagickService
{
    /// <summary>Convert a single image between formats.</summary>
    Task ConvertImageAsync(string inputFile, string outputFile, ImageConversionOptions options,
                           CancellationToken ct = default);

    /// <summary>Merge multiple images into a single PDF document.</summary>
    Task MergeImagesToPdfAsync(IReadOnlyList<string> imagePaths, string outputPath,
                               CancellationToken ct = default);
}
