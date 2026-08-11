namespace CortexFX.Core.Interfaces;

public sealed record ResourceValidationResult(
    string ResourcesDirectory,
    bool ResourcesDirectoryExists,
    IReadOnlyList<string> MissingTools,
    string FFmpegLibsDirectory,
    IReadOnlyList<string> MissingFFmpegDlls)
{
    public bool IsReady =>
        ResourcesDirectoryExists &&
        MissingTools.Count == 0 &&
        MissingFFmpegDlls.Count == 0;
}

public interface IResourceValidationService
{
    ResourceValidationResult ValidateCoreResources();

    Task<ResourceValidationResult> ValidateCoreResourcesAsync(CancellationToken ct = default);
}
