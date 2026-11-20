using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;

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
    public string Heading => Core.IsReady ? Loc.T("Loc_Res_HeadingReady") : Loc.T("Loc_Res_HeadingMissing");
    public string Description => Core.IsReady
        ? Loc.T("Loc_Res_DescReady")
        : Loc.T("Loc_Res_DescMissing");
    public string Count => Loc.F("Loc_Res_Count", CoreTools.Count(tool => tool.State == ResourceToolState.Ready), CoreTools.Count);
    public string Details => $"Tools folder:\n{Core.ResourcesDirectory}\n\nPlayback libraries:\n{Core.FFmpegLibsDirectory}\n\n" +
        (!Core.ResourcesDirectoryExists ? "The tools folder is missing.\n" : "") +
        (Core.MissingTools.Count > 0 ? "Missing conversion files:\n" + string.Join("\n", Core.MissingTools) + "\n\n" : "") +
        (Core.MissingFFmpegDlls.Count > 0 ? "Missing playback files:\n" + string.Join("\n", Core.MissingFFmpegDlls) : "");

    public static ResourceStatusReport Create(ResourceValidationResult core, bool wordPdf, bool powerPointPdf, bool audioAi)
    {
        bool Found(string tool) => core.ResourcesDirectoryExists && !core.MissingTools.Contains(tool, StringComparer.OrdinalIgnoreCase);
        ResourceToolStatus Required(string name, string description, bool found) => new(name, description,
            found ? Loc.T("Loc_Res_StateReady") : Loc.T("Loc_Res_StateMissing"),
            found ? ResourceToolState.Ready : ResourceToolState.Missing);
        ResourceToolStatus Document(string name, string app, bool found) => new(name,
            found ? Loc.T("Loc_Res_DocDetected") : Loc.F("Loc_Res_DocRequires", app),
            found ? Loc.T("Loc_Res_StateDetected") : Loc.T("Loc_Res_StateNotInstalled"),
            found ? ResourceToolState.Ready : ResourceToolState.Optional);
        return new(core,
        [
            Required(Loc.T("Loc_Res_ToolVideoAudio"), Loc.T("Loc_Res_ToolVideoAudioDesc"), Found("ffmpeg.exe")),
            Required(Loc.T("Loc_Res_ToolPreview"), Loc.T("Loc_Res_ToolPreviewDesc"), core.ResourcesDirectoryExists && core.MissingFFmpegDlls.Count == 0),
            Required(Loc.T("Loc_Res_ToolImage"), Loc.T("Loc_Res_ToolImageDesc"), Found("Magick.Native-Q8-x64.dll") && Found("magick.exe")),
            Required(Loc.T("Loc_Res_ToolPdfImg"), Loc.T("Loc_Res_ToolPdfImgDesc"), Found("pdfium.dll") && Found("pdftocairo.exe"))
        ],
        [
            Document(Loc.T("Loc_Tool_WordToPdf"), "Microsoft Word", wordPdf),
            Document(Loc.T("Loc_Tool_PptToPdf"), "Microsoft PowerPoint", powerPointPdf),
            new(Loc.T("Loc_Res_ToolAi"), audioAi ? Loc.T("Loc_Res_ToolAiInstalled") : Loc.T("Loc_Res_ToolAiOptional"),
                audioAi ? Loc.T("Loc_Res_StateInstalled") : Loc.T("Loc_Res_StateOptional"),
                audioAi ? ResourceToolState.Ready : ResourceToolState.Optional)
        ]);
    }
}
