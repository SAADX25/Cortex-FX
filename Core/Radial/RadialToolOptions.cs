namespace CortexFX.Core.Radial;

public enum CompressionTargetMode { Percentage, MaximumSize }

public sealed record RadialCompressionOptions(CompressionTargetMode Mode = CompressionTargetMode.Percentage,
    double ReductionPercent = 50, long MaximumBytes = 512 * 1024, bool RasterizePdf = false)
{
    public long TargetBytes(long sourceBytes)
    {
        if (!Enum.IsDefined(Mode) || !double.IsFinite(ReductionPercent) || ReductionPercent is < 1 or > 95 || MaximumBytes < 1024)
            throw new ArgumentException("Choose a reduction from 1–95% or a maximum size of at least 1 KB.");
        if (sourceBytes < 1) throw new ArgumentException("The input file is empty.");
        return Mode == CompressionTargetMode.Percentage ? Math.Max(1, (long)Math.Floor(sourceBytes * (1 - ReductionPercent / 100))) : MaximumBytes;
    }
}

/// <summary>Coordinates refer to the full image after EXIF orientation has been applied.</summary>
public sealed record RadialCropOptions(int X, int Y, int Width, int Height, int SourceWidth, int SourceHeight)
{
    public void Validate(int actualWidth, int actualHeight)
    {
        if (SourceWidth != actualWidth || SourceHeight != actualHeight)
            throw new ArgumentException("The image dimensions changed. Reopen the crop editor.");
        if (X < 0 || Y < 0 || Width < 1 || Height < 1 || (long)X + Width > actualWidth || (long)Y + Height > actualHeight)
            throw new ArgumentException("The crop must stay inside the image and contain at least one pixel.");
    }
}

public readonly record struct CropRectangle(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}
public enum CropHandle { Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

public static class CropGeometry
{
    public static CropRectangle Fit(int width, int height, double? ratio, CropRectangle? within = null)
    {
        Validate(width, height, ratio);
        var box = Clamp(within ?? new(0, 0, width, height), width, height);
        if (ratio is not double r) return box;
        double w = Math.Min(box.Width, box.Height * r), h = w / r;
        return Clamp(new(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h), width, height);
    }

    public static CropRectangle Clamp(CropRectangle box, int width, int height)
    {
        Validate(width, height, null);
        if (!double.IsFinite(box.X + box.Y + box.Width + box.Height)) throw new ArgumentException("Invalid crop coordinates.");
        double w = Math.Clamp(box.Width, 1, width), h = Math.Clamp(box.Height, 1, height);
        return new(Math.Clamp(box.X, 0, width - w), Math.Clamp(box.Y, 0, height - h), w, h);
    }

    public static CropRectangle Resize(CropRectangle start, CropHandle handle, double dx, double dy,
        int width, int height, double? ratio = null)
    {
        Validate(width, height, ratio);
        start = Clamp(start, width, height);
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) throw new ArgumentException("Invalid crop movement.");
        if (handle == CropHandle.Move) return Clamp(start with { X = start.X + dx, Y = start.Y + dy }, width, height);
        bool left = handle is CropHandle.Left or CropHandle.TopLeft or CropHandle.BottomLeft;
        bool right = handle is CropHandle.Right or CropHandle.TopRight or CropHandle.BottomRight;
        bool top = handle is CropHandle.Top or CropHandle.TopLeft or CropHandle.TopRight;
        bool bottom = handle is CropHandle.Bottom or CropHandle.BottomLeft or CropHandle.BottomRight;
        double x1 = left ? Math.Clamp(start.X + dx, 0, start.Right - 1) : start.X;
        double x2 = right ? Math.Clamp(start.Right + dx, start.X + 1, width) : start.Right;
        double y1 = top ? Math.Clamp(start.Y + dy, 0, start.Bottom - 1) : start.Y;
        double y2 = bottom ? Math.Clamp(start.Bottom + dy, start.Y + 1, height) : start.Bottom;
        if (ratio is not double r) return new(x1, y1, x2 - x1, y2 - y1);
        double anchorX = left ? start.Right : start.X, anchorY = top ? start.Bottom : start.Y;
        double w, h;
        if ((left || right) && (top || bottom))
        {
            w = Math.Abs(dx) >= Math.Abs(dy) * r ? x2 - x1 : (y2 - y1) * r;
            w = Math.Clamp(w, Math.Min(1, r), Math.Min(left ? anchorX : width - anchorX, (top ? anchorY : height - anchorY) * r));
            h = w / r;
            return Clamp(new(left ? anchorX - w : anchorX, top ? anchorY - h : anchorY, w, h), width, height);
        }
        if (left || right)
        {
            double cy = start.Y + start.Height / 2;
            w = Math.Min(x2 - x1, Math.Min(left ? anchorX : width - anchorX, 2 * Math.Min(cy, height - cy) * r));
            h = w / r;
            return Clamp(new(left ? anchorX - w : anchorX, cy - h / 2, w, h), width, height);
        }
        double cx = start.X + start.Width / 2;
        h = Math.Min(y2 - y1, Math.Min(top ? anchorY : height - anchorY, 2 * Math.Min(cx, width - cx) / r));
        w = h * r;
        return Clamp(new(cx - w / 2, top ? anchorY - h : anchorY, w, h), width, height);
    }

    public static RadialCropOptions ToPixels(CropRectangle box, int width, int height)
    {
        box = Clamp(box, width, height);
        int x = Math.Clamp((int)Math.Round(box.X), 0, width - 1), y = Math.Clamp((int)Math.Round(box.Y), 0, height - 1);
        return new(x, y, Math.Clamp((int)Math.Round(box.Width), 1, width - x), Math.Clamp((int)Math.Round(box.Height), 1, height - y), width, height);
    }

    private static void Validate(int width, int height, double? ratio)
    {
        if (width < 1 || height < 1 || (ratio.HasValue && (!double.IsFinite(ratio.Value) || ratio.Value <= 0)))
            throw new ArgumentException("Invalid image dimensions or aspect ratio.");
    }
}
