namespace CortexFX.Core.Interfaces;

public class ImageMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string Format { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public double MegaPixels => (Width * Height) / 1_000_000.0;
    public bool IsLarge => FileSizeBytes > 50 * 1024 * 1024;
}

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
    ImageMetadata? ReadMetadata(string filePath);
    /// <summary>Convert one image to another format/size.</summary>
    Task ConvertImageAsync(string inputFile, string outputFile, ImageConversionOptions options,
                           CancellationToken ct = default);

    /// <summary>Combine images into one PDF.</summary>
    Task MergeImagesToPdfAsync(IReadOnlyList<string> imagePaths, string outputPath,
                               CancellationToken ct = default);
}
