using CortexFX.Core.Interfaces;

namespace CortexFX.ViewModels;

public enum ResourceToolState { Ready, Missing, Optional }

public sealed record ResourceToolStatus(string Name, string Description, string Status, ResourceToolState State)
{
    public string Foreground => State switch { ResourceToolState.Ready => "#8ADBB2", ResourceToolState.Missing => "#FFB38A", _ => "#BBA7E1" };
    public string Background => State switch { ResourceToolState.Ready => "#20392F", ResourceToolState.Missing => "#432C25", _ => "#30283E" };
    public string Icon => State switch { ResourceToolState.Ready => "\uE73E", ResourceToolState.Missing => "\uE7BA", _ => "\uE896" };
}

public sealed record ResourceStatusReport(ResourceValidationResult Core, IReadOnlyList<ResourceToolStatus> CoreTools,
    IReadOnlyList<ResourceToolStatus> OptionalTools)
{
    public string Heading => Core.IsReady ? "Core tools are ready" : "Some tools need attention";
    public string Description => Core.IsReady
        ? "The tools included with Cortex FX were found on this computer."
        : "Some included files are missing. Reinstall the latest Cortex FX package to restore them.";
    public string Count => $"{CoreTools.Count(tool => tool.State == ResourceToolState.Ready)} of {CoreTools.Count} ready";
    public string Details => $"Tools folder:\n{Core.ResourcesDirectory}\n\nPlayback libraries:\n{Core.FFmpegLibsDirectory}\n\n" +
        (!Core.ResourcesDirectoryExists ? "The tools folder is missing.\n" : "") +
        (Core.MissingTools.Count > 0 ? "Missing conversion files:\n" + string.Join("\n", Core.MissingTools) + "\n\n" : "") +
        (Core.MissingFFmpegDlls.Count > 0 ? "Missing playback files:\n" + string.Join("\n", Core.MissingFFmpegDlls) : "");

    public static ResourceStatusReport Create(ResourceValidationResult core, bool wordPdf, bool powerPointPdf, bool audioAi)
    {
        bool Found(string tool) => core.ResourcesDirectoryExists && !core.MissingTools.Contains(tool, StringComparer.OrdinalIgnoreCase);
        ResourceToolStatus Required(string name, string description, bool found) => new(name, description,
            found ? "Ready" : "Missing files", found ? ResourceToolState.Ready : ResourceToolState.Missing);
        ResourceToolStatus Document(string name, string app, bool found) => new(name,
            found ? "A compatible desktop converter was detected." : $"Requires desktop {app} or LibreOffice.",
            found ? "Detected" : "Not installed", found ? ResourceToolState.Ready : ResourceToolState.Optional);
        return new(core,
        [
            Required("Video & audio", "Convert, cut and compress your media files.", Found("ffmpeg.exe")),
            Required("Video preview", "Play videos inside the editors.", core.ResourcesDirectoryExists && core.MissingFFmpegDlls.Count == 0),
            Required("Image tools", "Convert and edit image files.", Found("magick.exe")),
            Required("PDF to images", "Export PDF pages as image files.", Found("pdftocairo.exe"))
        ],
        [
            Document("Word to PDF", "Microsoft Word", wordPdf),
            Document("PowerPoint to PDF", "Microsoft PowerPoint", powerPointPdf),
            new("AI audio separation", audioAi ? "The local vocals and music engine is installed." : "Set up the optional engine in AI Audio Separator.",
                audioAi ? "Installed" : "Optional setup", audioAi ? ResourceToolState.Ready : ResourceToolState.Optional)
        ]);
    }
}
