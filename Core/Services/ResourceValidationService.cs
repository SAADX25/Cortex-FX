using System.IO;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services;

public sealed class ResourceValidationService : IResourceValidationService
{
    private readonly IAppConfiguration _config;

    public ResourceValidationService(IAppConfiguration config)
    {
        _config = config;
    }

    public ResourceValidationResult ValidateCoreResources()
    {
        if (!Directory.Exists(_config.ResourcesDirectory))
        {
            return new ResourceValidationResult(
                _config.ResourcesDirectory,
                ResourcesDirectoryExists: false,
                MissingTools: Array.Empty<string>());
        }

        var requiredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ffmpeg.exe"] = _config.FFmpegPath,
            ["magick.exe"] = _config.MagickPath,
            ["pdftocairo.exe"] = _config.PdfToCairoPath
        };

        var missing = requiredTools
            .Where(tool => !File.Exists(tool.Value))
            .Select(tool => tool.Key)
            .ToList();

        return new ResourceValidationResult(
            _config.ResourcesDirectory,
            ResourcesDirectoryExists: true,
            MissingTools: missing);
    }
}
