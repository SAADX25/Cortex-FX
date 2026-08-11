using System.IO;
using CortexFX.Core.Configuration;
using CortexFX.Core.Constants;
using CortexFX.Core.Interfaces;
using CortexFX.Models;

namespace CortexFX.Core.Services.Documents;

/// <summary>
/// Extra converters if the user installed them: LibreOffice, 7-Zip, Calibre.
/// Not required for the core app.
/// </summary>
public sealed class OptionalConversionService : IOptionalConversionService
{
    private readonly IAppConfiguration _config;
    private readonly IProcessManager _processManager;

    public OptionalConversionService(IAppConfiguration config, IProcessManager processManager)
    {
        _config = config;
        _processManager = processManager;
    }

    public bool CanConvert(string inputExtension, string targetFormat)
    {
        string target = targetFormat.ToLowerInvariant();

        if (MediaTypes.DocumentExtensions.Contains(inputExtension) &&
            MediaTypes.LibreOfficeOutputFormats.Contains(target))
        {
            return true;
        }

        if (MediaTypes.ArchiveExtensions.Contains(inputExtension) &&
            MediaTypes.ArchiveOutputFormats.Contains(target))
        {
            return true;
        }

        if ((MediaTypes.EbookExtensions.Contains(inputExtension) || inputExtension == ".pdf") &&
            MediaTypes.EbookOutputFormats.Contains(target))
        {
            return true;
        }

        return false;
    }

    public async Task<ConversionResult> ConvertAsync(
        string inputFile,
        string outputPath,
        string inputExtension,
        string targetFormat,
        CancellationToken ct = default,
        IProgress<double>? progress = null)
    {
        string target = targetFormat.ToLowerInvariant();

        if (MediaTypes.DocumentExtensions.Contains(inputExtension) &&
            MediaTypes.LibreOfficeOutputFormats.Contains(target))
        {
            return await ConvertWithLibreOfficeAsync(inputFile, outputPath, target, ct, progress);
        }

        if (MediaTypes.ArchiveExtensions.Contains(inputExtension) &&
            MediaTypes.ArchiveOutputFormats.Contains(target))
        {
            return await ConvertArchiveAsync(inputFile, outputPath, target, ct, progress);
        }

        if ((MediaTypes.EbookExtensions.Contains(inputExtension) || inputExtension == ".pdf") &&
            MediaTypes.EbookOutputFormats.Contains(target))
        {
            return await ConvertEbookAsync(inputFile, outputPath, ct, progress);
        }

        return ConversionResult.Fail($"No optional engine found for {inputExtension} to {targetFormat}.");
    }

    private async Task<ConversionResult> ConvertWithLibreOfficeAsync(
        string inputFile,
        string outputPath,
        string target,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        string? soffice = FindLibreOffice();
        if (soffice == null)
        {
            return ConversionResult.Fail("LibreOffice is required for this document conversion. Install LibreOffice or place soffice.exe in Resources\\LibreOffice\\program.");
        }

        string outputDir = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
        Directory.CreateDirectory(outputDir);

        string args = $"--headless --nologo --nodefault --nofirststartwizard --convert-to {target} --outdir \"{outputDir}\" \"{inputFile}\"";
        await _processManager.RunAsync(soffice, args, ct);

        string producedPath = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(inputFile)}.{target}");
        if (!File.Exists(producedPath))
        {
            return ConversionResult.Fail("LibreOffice finished, but the converted file was not found.");
        }

        if (!producedPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(producedPath, outputPath);
        }

        progress?.Report(100);
        return ConversionResult.Ok(outputPath);
    }

    private async Task<ConversionResult> ConvertArchiveAsync(
        string inputFile,
        string outputPath,
        string target,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        string? sevenZip = FindSevenZip();
        if (sevenZip == null)
        {
            return ConversionResult.Fail("7-Zip is required for archive conversion. Install 7-Zip or place 7z.exe/7za.exe in Resources.");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "CortexFX", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Path.GetTempPath());

        try
        {
            await _processManager.RunAsync(sevenZip, $"x \"{inputFile}\" -o\"{tempDir}\" -y", ct);

            string typeArg = target switch
            {
                "zip" => "-tzip",
                "7z" => "-t7z",
                "tar" => "-ttar",
                _ => ""
            };

            if (File.Exists(outputPath)) File.Delete(outputPath);
            await _processManager.RunAsync(sevenZip, $"a {typeArg} \"{outputPath}\" \"{Path.Combine(tempDir, "*")}\" -y", ct);

            progress?.Report(100);
            return ConversionResult.Ok(outputPath);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private async Task<ConversionResult> ConvertEbookAsync(
        string inputFile,
        string outputPath,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        string? ebookConvert = FindCalibre();
        if (ebookConvert == null)
        {
            return ConversionResult.Fail("Calibre is required for e-book conversion. Install Calibre or place ebook-convert.exe in Resources\\Calibre.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Path.GetTempPath());
        if (File.Exists(outputPath)) File.Delete(outputPath);

        await _processManager.RunAsync(ebookConvert, $"\"{inputFile}\" \"{outputPath}\"", ct);

        progress?.Report(100);
        return File.Exists(outputPath)
            ? ConversionResult.Ok(outputPath)
            : ConversionResult.Fail("Calibre finished, but the converted file was not found.");
    }

    private string? FindLibreOffice()
    {
        return FirstExisting(
            Path.Combine(_config.ResourcesDirectory, "LibreOffice", "program", "soffice.exe"),
            Path.Combine(_config.ResourcesDirectory, "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"));
    }

    private string? FindSevenZip()
    {
        return FirstExisting(
            Path.Combine(_config.ResourcesDirectory, "7z.exe"),
            Path.Combine(_config.ResourcesDirectory, "7za.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"));
    }

    private string? FindCalibre()
    {
        return FirstExisting(
            Path.Combine(_config.ResourcesDirectory, "Calibre", "ebook-convert.exe"),
            Path.Combine(_config.ResourcesDirectory, "ebook-convert.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2", "ebook-convert.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Calibre2", "ebook-convert.exe"));
    }

    private static string? FirstExisting(params string[] paths)
    {
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
