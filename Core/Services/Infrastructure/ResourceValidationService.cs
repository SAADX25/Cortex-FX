using System.IO;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services.Infrastructure;

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
                MissingTools: Array.Empty<string>(),
                FFmpegLibsDirectory: _config.FFmpegLibsDirectory,
                MissingFFmpegDlls: Array.Empty<string>());
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

        string[] requiredFfmpegDlls =
        [
            "avcodec-58.dll",
            "avformat-58.dll",
            "avutil-56.dll",
            "swresample-3.dll",
            "swscale-5.dll"
        ];

        var missingFfmpegDlls = requiredFfmpegDlls
            .Where(dll => !File.Exists(Path.Combine(_config.FFmpegLibsDirectory, dll)))
            .ToList();

        return new ResourceValidationResult(
            _config.ResourcesDirectory,
            ResourcesDirectoryExists: true,
            MissingTools: missing,
            FFmpegLibsDirectory: _config.FFmpegLibsDirectory,
            MissingFFmpegDlls: missingFfmpegDlls);
    }

    public Task<ResourceValidationResult> ValidateCoreResourcesAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return ValidateCoreResources();
        }, ct);
    }
}
