using System.IO;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Radial;

public sealed class RadialActionCatalog(IConversionRouter router, IAppConfiguration configuration)
{
    public IReadOnlyList<RadialAction> GetActions(RadialSelection selection, RadialMode mode, RadialSettings settings)
    {
        string ffmpeg = string.IsNullOrWhiteSpace(settings.FFmpegPath) ? configuration.FFmpegPath : settings.FFmpegPath.Trim('"');
        bool mediaReady = File.Exists(ffmpeg);
        RadialAction Media(string label, RadialOperation operation, string target) => new(label, operation, target,
            mediaReady, mediaReady ? null : "FFmpeg is missing. Open radial wheel settings to choose ffmpeg.exe.");
        if (mode == RadialMode.Quick)
            return [new("ZIP", RadialOperation.Zip), new("Copy paths", RadialOperation.CopyPaths), new("SHA-256", RadialOperation.Sha256)];
        if (mode == RadialMode.Tools)
        {
            if (selection.Category == "Image")
                return [new("Compress", RadialOperation.CompressImage),
                    new("Crop image", RadialOperation.CropImage, Enabled: selection.Paths.Count == 1,
                        UnavailableReason: selection.Paths.Count == 1 ? null : "Choose one image to open the crop editor."),
                    new("Strip EXIF", RadialOperation.StripMetadata), new("Resize 50%", RadialOperation.ResizeHalf)];
            if (selection.Category == "Video")
                return [Media("Compress", RadialOperation.CompressVideo, "mp4"), Media("Extract MP3", RadialOperation.ExtractAudio, "mp3"), Media("GIF", RadialOperation.AnimatedGif, "gif")];
            if (selection.Paths.All(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                List<RadialAction> actions = [new("Compress", RadialOperation.CompressPdf), new("Split pages", RadialOperation.SplitPdf), new("PNG pages", RadialOperation.PdfImages, "png")];
                if (selection.Paths.Count > 1) actions.Add(new("Merge PDFs", RadialOperation.MergePdf, "pdf"));
                return actions;
            }
            return [];
        }
        var originals = selection.Paths.Select(path => Normalize(Path.GetExtension(path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> targets = selection.Category switch
        {
            "Image" => ["jpg", "png", "webp", "heic", "avif", "bmp", "tiff", "ico", "pdf"],
            "Video" => ["mp4", "mkv", "mov", "webm", "avi", "gif", "mp3"],
            "Audio" => ["mp3", "wav", "aac", "flac", "ogg", "m4a"],
            _ => selection.Paths.Select(path => router.GetSupportedFormats(Path.GetExtension(path)).Select(Normalize))
                .Aggregate((IEnumerable<string>?)null, (common, next) => common is null ? next : common.Intersect(next, StringComparer.OrdinalIgnoreCase)) ?? []
        };
        return targets.Where(target => !originals.Contains(target)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(target => selection.Category is "Video" or "Audio"
                ? Media(target.ToUpperInvariant(), RadialOperation.Convert, target)
                : Conversion(target)).ToArray();

        RadialAction Conversion(string target)
        {
            string? reason = selection.Paths.Select(path => router.GetUnavailableReason(Path.GetExtension(path), target))
                .FirstOrDefault(reason => reason is not null);
            return new(target.ToUpperInvariant(), RadialOperation.Convert, target, reason is null, reason);
        }
    }

    public static string Normalize(string format) => format.TrimStart('.').ToLowerInvariant() switch
    {
        "jpeg" => "jpg", "tif" => "tiff", "heif" => "heic", var other => other
    };
}
