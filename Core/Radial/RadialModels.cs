using System.IO;
using CortexFX.Core.Constants;

namespace CortexFX.Core.Radial;

public enum RadialMode { Convert, Tools, Quick }
public enum RadialOperation
{
    Convert, CompressImage, StripMetadata, ResizeHalf, CompressVideo,
    ExtractAudio, AnimatedGif, CompressPdf, SplitPdf, MergePdf, PdfImages,
    Zip, CopyPaths, Sha256, CropImage
}

public sealed record RadialAction(string Label, RadialOperation Operation, string Target = "",
    bool Enabled = true, string? UnavailableReason = null)
{
    public RadialCompressionOptions? Compression { get; init; }
    public RadialCropOptions? Crop { get; init; }
}
public sealed record RadialProgress(double Percent, string Message);
public sealed record RadialResult(IReadOnlyList<string> Outputs, string Message, string? ClipboardText = null);

public sealed record RadialSelection(IReadOnlyList<string> Paths, long Bytes, string Category)
{
    public string Title => Paths.Count == 1 ? Path.GetFileName(Paths[0]) : $"{Paths.Count} files";
    public string SizeLabel => Bytes >= 1048576 ? $"{Bytes / 1048576d:0.0} MB" : $"{Bytes / 1024d:0.0} KB";

    public static RadialSelection FromPaths(IEnumerable<string> paths)
    {
        string[] files = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0 || files.Length > 100)
            throw new ArgumentException("Choose between 1 and 100 local files.");
        if (files.Any(path => !File.Exists(path)))
            throw new FileNotFoundException("Only existing local files can be dropped on the wheel.");
        string[] categories = files.Select(GetCategory).Distinct().ToArray();
        return new(files, files.Sum(path => new FileInfo(path).Length), categories.Length == 1 ? categories[0] : "Mixed");
    }

    public static string GetCategory(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".avif" => "Image",
        _ => MediaTypes.GetCategory(Path.GetExtension(path))
    };
}

public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public sealed class RadialExecutionException(string message, IReadOnlyList<string> completed, Exception inner)
    : InvalidOperationException(message + (completed.Count > 0
        ? $"\n{completed.Count} earlier output(s) were saved successfully." : ""), inner)
{
    public IReadOnlyList<string> CompletedOutputs { get; } = completed.ToArray();
}
