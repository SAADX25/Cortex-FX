using System.Windows;
using System.Windows.Media;
using CortexFX.Core.Radial;

namespace CortexFX.Views.Radial;

public static class RadialActionIcons
{
    private static readonly Dictionary<string, Geometry> Shapes = new()
    {
        ["compress"] = Geometry.Parse("M2,2 L9,9 M9,3 V9 H3 M22,22 L15,15 M15,21 V15 H21"),
        ["crop"] = Geometry.Parse("M7,1 V17 H23 M1,7 H17 V23 M10,4 H20 V14"),
        ["clean"] = Geometry.Parse("M12,2 L21,6 V12 C21,17 16,21 12,23 C8,21 3,17 3,12 V6 Z M8,8 L16,16 M16,8 L8,16"),
        ["resize"] = Geometry.Parse("M3,3 H21 V21 H3 Z M6,18 L18,6 M12,6 H18 V12 M6,12 V18 H12"),
        ["audio"] = Geometry.Parse("M9,18 V5 L20,2 V15 M9,8 L20,5 M9,18 C9,22 2,23 2,19 C2,15 9,14 9,18 M20,15 C20,19 13,20 13,16 C13,12 20,11 20,15"),
        ["video"] = Geometry.Parse("M2,4 H17 V20 H2 Z M17,9 L23,5 V19 L17,15 M7,8 L13,12 L7,16 Z"),
        ["gif"] = Geometry.Parse("M3,3 H21 V21 H3 Z M3,8 H21 M3,16 H21 M8,3 V8 M16,3 V8 M8,16 V21 M16,16 V21 M10,10 L14,12 L10,14 Z"),
        ["split"] = Geometry.Parse("M4,2 H15 L20,7 V10 M4,2 V10 M4,14 V22 H20 V14 M3,12 L7,10 L11,14 L15,10 L21,12"),
        ["merge"] = Geometry.Parse("M3,3 H14 V17 H3 Z M10,9 H21 V23 H10 M14,14 H18 M16,12 V16"),
        ["photo"] = Geometry.Parse("M2,3 H22 V21 H2 Z M2,17 L8,11 L13,16 L17,12 L22,17 M15,7 A2,2 0 1 0 19,7 A2,2 0 1 0 15,7"),
        ["zip"] = Geometry.Parse("M2,6 H10 L13,9 H22 V22 H2 Z M14,9 V12 H17 V15 H14 V18 H17 V21"),
        ["paths"] = Geometry.Parse("M9,7 L12,4 C18,-2 26,6 20,12 L17,15 M7,9 L4,12 C-2,18 6,26 12,20 L15,17 M7,17 L17,7"),
        ["hash"] = Geometry.Parse("M9,2 L5,22 M19,2 L15,22 M3,8 H23 M1,16 H21"),
        ["pdf"] = Geometry.Parse("M5,2 H15 L21,8 V22 H5 Z M15,2 V8 H21 M8,18 L11,11 L14,18 M9,16 H13 M16,12 V18 H19"),
        ["file"] = Geometry.Parse("M4,2 H15 L21,8 V22 H4 Z M15,2 V8 H21 M8,13 H17 M8,17 H14"),
        ["camera"] = Geometry.Parse("M2,7 H7 L9,3 H15 L17,7 H22 V21 H2 Z M7,14 A5,5 0 1 0 17,14 A5,5 0 1 0 7,14"),
        ["grid"] = Geometry.Parse("M3,3 H21 V21 H3 Z M9,3 V21 M15,3 V21 M3,9 H21 M3,15 H21"),
        ["layers"] = Geometry.Parse("M12,2 L23,8 L12,14 L1,8 Z M2,13 L12,19 L22,13 M2,18 L12,24 L22,18"),
        ["spark"] = Geometry.Parse("M12,1 L15,9 L23,12 L15,15 L12,23 L9,15 L1,12 L9,9 Z"),
        ["window"] = Geometry.Parse("M2,3 H22 V21 H2 Z M2,8 H22 M8,8 V21 M5,5 H6 M9,5 H10")
    };

    public static (string Shape, string Color) Descriptor(RadialAction action) => action.Operation switch
    {
        RadialOperation.CompressImage or RadialOperation.CompressPdf or RadialOperation.CompressVideo => ("compress", "#FF8CA8"),
        RadialOperation.CropImage => ("crop", "#FFC17D"),
        RadialOperation.StripMetadata => ("clean", "#8BDBBF"),
        RadialOperation.ResizeHalf => ("resize", "#9CBFFF"),
        RadialOperation.ExtractAudio => ("audio", "#C1A4F5"),
        RadialOperation.AnimatedGif => ("gif", "#F4BE84"),
        RadialOperation.SplitPdf => ("split", "#BCA8F3"),
        RadialOperation.MergePdf => ("merge", "#9BC7F3"),
        RadialOperation.PdfImages => ("photo", "#9BD9BA"),
        RadialOperation.Zip => ("zip", "#E9BD7F"),
        RadialOperation.CopyPaths => ("paths", "#9BC8F5"),
        RadialOperation.Sha256 => ("hash", "#BAAFF0"),
        _ => action.Target.ToLowerInvariant() switch
        {
            "pdf" => ("pdf", "#F98E9F"), "jpg" or "jpeg" => ("photo", "#EBC687"),
            "png" => ("photo", "#91C7F7"), "webp" => ("photo", "#88D9BA"),
            "heic" or "heif" => ("camera", "#BD9DE8"), "avif" => ("spark", "#F7AA86"),
            "bmp" => ("grid", "#86C9D7"), "tiff" or "tif" => ("layers", "#C6AEDF"), "ico" => ("window", "#A8C7EE"),
            "mp3" or "wav" or "aac" or "flac" or "ogg" or "m4a" => ("audio", "#C6B1F0"),
            "mp4" or "mkv" or "avi" or "mov" or "webm" => ("video", "#97C4F0"), "gif" => ("gif", "#F4BE84"), _ => ("file", "#B7C3D8")
        }
    };
    public static void Draw(DrawingContext drawing, RadialAction action, Point center, double size = 32)
    {
        var descriptor = Descriptor(action);
        var color = (Color)ColorConverter.ConvertFromString(action.Enabled ? descriptor.Color : "#746A76");
        drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B)), null, center, size * .8, size * .8);
        drawing.PushTransform(new TranslateTransform(center.X - size / 2, center.Y - size / 2));
        drawing.PushTransform(new ScaleTransform(size / 24, size / 24));
        drawing.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Shapes[descriptor.Shape]);
        drawing.Pop(); drawing.Pop();
        if (!action.Enabled)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(224, 120, 143)), 1.5);
            Point badge = new(center.X + size * .6, center.Y + size * .5);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(27, 20, 29)), pen, badge, 6, 6);
            drawing.DrawLine(pen, new(badge.X - 3, badge.Y + 3), new(badge.X + 3, badge.Y - 3));
        }
    }
}
