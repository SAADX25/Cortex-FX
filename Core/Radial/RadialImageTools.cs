using System.IO;
using ImageMagick;

namespace CortexFX.Core.Radial;

public sealed record CropImagePreview(int Width, int Height, byte[] Png);

public static class RadialImageTools
{
    public static void RequireStillImage(string source)
    {
        using var frames = new MagickImageCollection();
        frames.Ping(source);
        if (frames.Count != 1) throw new NotSupportedException("This editor supports still images. Animated and multi-frame files are not changed.");
    }

    public static CropImagePreview LoadPreview(string source)
    {
        RequireStillImage(source);
        using var image = new MagickImage(source);
        image.AutoOrient();
        int width = checked((int)image.Width), height = checked((int)image.Height);
        if (image.Width > 1400 || image.Height > 1400) image.Resize(new MagickGeometry(1400, 1400));
        return new(width, height, image.ToByteArray(MagickFormat.Png));
    }

    public static void Crop(string source, string destination, RadialCropOptions options, CancellationToken ct)
    {
        RequireStillImage(source);
        ct.ThrowIfCancellationRequested();
        using var image = new MagickImage(source);
        image.AutoOrient();
        options.Validate(checked((int)image.Width), checked((int)image.Height));
        image.Crop(new MagickGeometry(options.X, options.Y, (uint)options.Width, (uint)options.Height));
        image.ResetPage();
        ct.ThrowIfCancellationRequested();
        image.Write(destination);
    }
}
