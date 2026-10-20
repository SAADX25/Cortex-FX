using System.Drawing.Imaging;
using System.IO;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CortexFX.Core.Radial;

/// <summary>A requested maximum is verified before any file is published.</summary>
public static class RadialTargetCompression
{
    public static void Image(string source, string destination, RadialCompressionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        RadialImageTools.RequireStillImage(source);
        long limit = options.TargetBytes(new FileInfo(source).Length);
        if (new FileInfo(source).Length <= limit) { File.Copy(source, destination, true); return; }
        string extension = Path.GetExtension(source).ToLowerInvariant();
        int[] qualities = [92, 82, 70, 58, 45, 30, 15, 3];
        uint[] colors = [256, 192, 128, 96, 64, 32, 16, 8];
        long smallest = long.MaxValue;
        for (int index = 0; index < qualities.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            using var image = new MagickImage(source);
            image.AutoOrient(); image.Strip(); image.Quality = (uint)qualities[index];
            if (extension is ".png" or ".gif" or ".bmp")
            {
                image.Quantize(new QuantizeSettings { Colors = colors[index] });
                image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            }
            image.Write(destination);
            smallest = Math.Min(smallest, new FileInfo(destination).Length);
            progress?.Report((index + 1d) / qualities.Length * 98);
            if (new FileInfo(destination).Length <= limit) return;
        }
        throw new InvalidOperationException($"The requested size could not be reached while keeping the image dimensions. Smallest attempt: {Size(smallest)}. Choose a larger target or resize first.");
    }

    public static void Pdf(string source, string destination, RadialCompressionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        long limit = options.TargetBytes(new FileInfo(source).Length);
        ct.ThrowIfCancellationRequested();
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            document.Options.CompressContentStreams = true;
            document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            document.Save(destination);
        }
        if (new FileInfo(source).Length <= limit && new FileInfo(source).Length < new FileInfo(destination).Length)
            File.Copy(source, destination, true);
        if (new FileInfo(destination).Length <= limit) return;
        if (!options.RasterizePdf)
            throw new InvalidOperationException("The target cannot be reached while preserving this PDF's text and interactive content. Choose a larger target, or allow rasterized pages in Compress options.");

        using var input = PdfiumViewer.PdfDocument.Load(source);
        (int Dpi, int Quality)[] attempts = [(150, 82), (120, 72), (96, 60), (72, 45), (50, 30), (36, 20), (24, 10)];
        long smallest = new FileInfo(destination).Length;
        for (int attempt = 0; attempt < attempts.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var document = new PdfDocument();
            for (int index = 0; index < input.PageCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                var size = input.PageSizes[index];
                double scale = attempts[attempt].Dpi / 72d;
                // Cap per-page raster memory while preserving the page's physical dimensions.
                scale = Math.Min(scale, Math.Sqrt(12_000_000d / Math.Max(1, size.Width * (double)size.Height)));
                int width = Math.Max(1, (int)Math.Round(size.Width * scale)), height = Math.Max(1, (int)Math.Round(size.Height * scale));
                using var bitmap = input.Render(index, width, height, (float)(scale * 72), (float)(scale * 72), true);
                using var png = new MemoryStream(); bitmap.Save(png, ImageFormat.Png);
                using var image = new MagickImage(png.ToArray());
                image.BackgroundColor = MagickColors.White; image.Alpha(AlphaOption.Remove); image.Quality = (uint)attempts[attempt].Quality;
                using var bytes = new MemoryStream(image.ToByteArray(MagickFormat.Jpeg));
                using var picture = XImage.FromStream(bytes);
                var page = document.AddPage(); page.Width = XUnit.FromPoint(size.Width); page.Height = XUnit.FromPoint(size.Height);
                using var drawing = XGraphics.FromPdfPage(page);
                drawing.DrawImage(picture, 0, 0, page.Width.Point, page.Height.Point);
                progress?.Report((attempt + (index + 1d) / input.PageCount) / attempts.Length * 98);
            }
            document.Save(destination);
            smallest = Math.Min(smallest, new FileInfo(destination).Length);
            if (new FileInfo(destination).Length <= limit) return;
        }
        throw new InvalidOperationException($"The PDF cannot fit the requested size. Smallest attempt: {Size(smallest)}. Choose a larger target.");
    }

    public static string Size(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576d:0.##} MB" : $"{bytes / 1024d:0.##} KB";
}
