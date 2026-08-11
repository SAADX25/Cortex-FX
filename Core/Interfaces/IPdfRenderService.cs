namespace CortexFX.Core.Interfaces;

/// <summary>
/// Contract for rendering PDF pages to image files (via PdfiumViewer).
/// </summary>
public interface IPdfRenderService
{
    /// <summary>
    /// Render all pages of a PDF as individual image files.
    /// Output filenames follow the pattern: {baseName}_Page{N}.{format}
    /// </summary>
    Task RenderPdfToImagesAsync(string pdfPath, string outputDirectory, string targetFormat,
                                int dpi = 150, CancellationToken ct = default,
                                IProgress<double>? progress = null);
}
