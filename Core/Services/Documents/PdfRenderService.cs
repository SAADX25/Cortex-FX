using System.Drawing.Imaging;
using System.IO;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services.Documents;

/// <summary>
/// Turns PDF pages into image files (Pdfium). DPI controls sharpness.
/// </summary>
public sealed class PdfRenderService : IPdfRenderService
{
    /// <inheritdoc />
    public async Task RenderPdfToImagesAsync(string pdfPath, string outputDirectory, string targetFormat,
                                              int dpi = 150, CancellationToken ct = default,
                                              IProgress<double>? progress = null)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException($"PDF file not found: {pdfPath}");

        Directory.CreateDirectory(outputDirectory);
        string baseName = Path.GetFileNameWithoutExtension(pdfPath);

        await Task.Run(() =>
        {
            using var document = PdfiumViewer.PdfDocument.Load(pdfPath);

            for (int i = 0; i < document.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                string pageFileName = $"{baseName}_Page{i + 1}.{targetFormat}";
                string pageOutputPath = Path.Combine(outputDirectory, pageFileName);

                var pageSize = document.PageSizes[i];
                int width = (int)(pageSize.Width / 72.0 * dpi);
                int height = (int)(pageSize.Height / 72.0 * dpi);

                using var image = document.Render(i, width, height, dpi, dpi, false);

                ImageFormat format = ResolveImageFormat(targetFormat);
                image.Save(pageOutputPath, format);

                double pagePercent = ((double)(i + 1) / document.PageCount) * 100;
                progress?.Report(pagePercent);
            }
        }, ct);
    }

    /// <summary>
    /// Calculate optimal DPI from a quality level (0-100).
    /// Maps linearly from 72 DPI (minimum legible) to 300 DPI (print quality).
    /// </summary>
    public static int QualityToDpi(double qualityLevel, int? customDpi = null)
    {
        if (customDpi.HasValue && customDpi.Value > 0)
            return customDpi.Value;

        return 72 + (int)((qualityLevel / 100.0) * (300 - 72));
    }

    private static ImageFormat ResolveImageFormat(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "png" => ImageFormat.Png,
            "bmp" => ImageFormat.Bmp,
            "gif" => ImageFormat.Gif,
            "tiff" or "tif" => ImageFormat.Tiff,
            "jpg" or "jpeg" => ImageFormat.Jpeg,
            // WebP is handled upstream via Magick; refuse silent JPEG mislabeling.
            "webp" => throw new NotSupportedException("WebP encoding is not available in PdfRenderService. Use ConversionRouter PDF→WebP path."),
            _ => ImageFormat.Jpeg,
        };
    }
}
