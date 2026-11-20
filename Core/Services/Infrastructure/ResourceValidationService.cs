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

        var missing = new List<string>();

        if (!File.Exists(_config.FFmpegPath))
            missing.Add("ffmpeg.exe");

        string baseDir = AppContext.BaseDirectory;
        bool hasMagickEngine =
            File.Exists(Path.Combine(baseDir, "Magick.Native-Q8-x64.dll")) ||
            File.Exists(Path.Combine(baseDir, "runtimes", "win-x64", "native", "Magick.Native-Q8-x64.dll")) ||
            File.Exists(_config.MagickPath);
        if (!hasMagickEngine)
            missing.Add("Magick.Native-Q8-x64.dll");

        bool hasPdfEngine =
            File.Exists(Path.Combine(baseDir, "x64", "pdfium.dll")) ||
            File.Exists(Path.Combine(baseDir, "pdfium.dll")) ||
            File.Exists(_config.PdfToCairoPath);
        if (!hasPdfEngine)
            missing.Add("pdfium.dll");

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
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ValidateCoreResources());
    }
}
