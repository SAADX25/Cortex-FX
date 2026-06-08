namespace CortexFX.Core.Interfaces;

public sealed record ResourceValidationResult(
    string ResourcesDirectory,
    bool ResourcesDirectoryExists,
    IReadOnlyList<string> MissingTools);

public interface IResourceValidationService
{
    ResourceValidationResult ValidateCoreResources();
}
