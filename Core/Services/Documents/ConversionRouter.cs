using System.IO;
using CortexFX.Core.Configuration;
using CortexFX.Core.Constants;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Core.Services.Media;
using CortexFX.Models;

namespace CortexFX.Core.Services.Documents;

/// <summary>
/// Picks the right engine (Office, Magick, FFmpeg, …) for each conversion job.
/// </summary>
public sealed class ConversionRouter : IConversionRouter
{
    private readonly IAppConfiguration _config;
    private readonly IFFmpegService _ffmpeg;
    private readonly IMagickService _magick;
    private readonly IOfficeInteropService _office;
    private readonly IPdfRenderService _pdfRenderer;
    private readonly IOptionalConversionService _optional;
    private readonly FFmpegService _ffmpegConcrete; // typed helpers (HW retry, video args)
    private readonly MagickService _magickConcrete;  // image metadata for suggestions

    /// <summary>
    /// Maximum parallel conversions. Defaults to CPU core count, capped at 8
    /// to avoid thrashing on machines with many hyper-threaded cores.
    /// Office COM operations always run serially (enforced by OfficeInteropService).
    /// </summary>
    public int MaxParallelism { get; set; } = Math.Min(Environment.ProcessorCount, 8);

    public ConversionRouter(
        IAppConfiguration config,
        IFFmpegService ffmpeg,
        IMagickService magick,
        IOfficeInteropService office,
        IPdfRenderService pdfRenderer,
        IOptionalConversionService optional)
    {
        _config = config;
        _ffmpeg = ffmpeg;
        _magick = magick;
        _office = office;
        _pdfRenderer = pdfRenderer;
        _optional = optional;

        // Store concrete types for access to non-interface methods
        // (smart builders, metadata reader). Safe because DI wires the same instance.
        _ffmpegConcrete = (ffmpeg as FFmpegService)!;
        _magickConcrete = (magick as MagickService)!;
    }

    // IConversionRouter

    /// <inheritdoc />
    public IReadOnlyList<string> GetSupportedFormats(string inputExtension)
    {
        if (MediaTypes.ConversionRules.TryGetValue(inputExtension, out var formats))
        {
            return formats;
        }
        return Array.Empty<string>();
    }

    /// <inheritdoc />
    public async Task<ConversionResult> ConvertAsync(ConversionJob job, CancellationToken ct = default,
                                                       IProgress<double>? progress = null)
    {
        try
        {
            string ext = Path.GetExtension(job.InputPath).ToLowerInvariant();
            string target = job.TargetFormat.ToLowerInvariant();

            // Resolve output directory (with optional subfolder)
            string outputDir = ResolveOutputDirectory(job);
            string baseName = Path.GetFileNameWithoutExtension(job.InputPath);
            string outputFileName = $"{baseName}.{target}";
            string outputPath = Path.Combine(outputDir, outputFileName);

            // Prevent self-overwrite
            if (string.Equals(job.InputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                baseName += "_converted";
                outputFileName = $"{baseName}.{target}";
                outputPath = Path.Combine(outputDir, outputFileName);
            }

            // Pick the right engine for this pair

            // Document → PDF
            if (IsDocumentToPdf(ext, target))
            {
                int engineQuality = job.QualityLevel < 30 ? 0 : 1;
                await _office.ConvertToPdfAsync(job.InputPath, outputPath, engineQuality, ct, progress);
                return ConversionResult.Ok(outputPath);
            }

            // PDF → Word / PowerPoint
            if (ext == ".pdf" && IsOfficeTarget(target))
            {
                return await HandlePdfToOfficeAsync(job.InputPath, outputPath, target, ct, progress);
            }

            // Word ↔ PowerPoint
            if (IsDocumentBridge(ext, target))
            {
                return await HandleDocumentBridgeAsync(job.InputPath, outputPath, ext, target, ct, progress);
            }

            // LibreOffice / 7-Zip / Calibre when present
            if (_optional.CanConvert(ext, target))
            {
                return await _optional.ConvertAsync(job.InputPath, outputPath, ext, target, ct, progress);
            }

            // Image → PDF
            if (MediaTypes.RasterImageExtensions.Contains(ext) && target == "pdf")
            {
                await _magick.ConvertImageAsync(job.InputPath, outputPath,
                    job.ImageOptions ?? new ImageConversionOptions(Quality: (int)job.QualityLevel), ct);
                progress?.Report(100);
                return ConversionResult.Ok(outputPath);
            }

            // PDF → images (WebP needs Magick after PNG)
            if (ext == ".pdf" && MediaTypes.MagickOutputFormats.Contains(target))
            {
                int dpi = PdfRenderService.QualityToDpi(job.QualityLevel, job.ImageOptions?.Dpi);

                if (target.Equals("webp", StringComparison.OrdinalIgnoreCase))
                {
                    // Pdfium can't write WebP — PNG first, then Magick.
                    string tempDir = Path.Combine(Path.GetTempPath(), "CortexFX_PdfWebP_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        await _pdfRenderer.RenderPdfToImagesAsync(job.InputPath, tempDir, "png", dpi, ct, progress);
                        Directory.CreateDirectory(outputDir);
                        var options = job.ImageOptions ?? new ImageConversionOptions(Quality: (int)job.QualityLevel);
                        string[] pages = Directory.GetFiles(tempDir, "*.png");
                        for (int i = 0; i < pages.Length; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            string webpName = Path.GetFileNameWithoutExtension(pages[i]) + ".webp";
                            string webpPath = Path.Combine(outputDir, webpName);
                            await _magick.ConvertImageAsync(pages[i], webpPath, options, ct);
                            progress?.Report(((double)(i + 1) / Math.Max(pages.Length, 1)) * 100);
                        }
                    }
                    finally
                    {
                        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* best effort */ }
                    }

                    return ConversionResult.Ok(outputDir);
                }

                await _pdfRenderer.RenderPdfToImagesAsync(job.InputPath, outputDir, target, dpi, ct, progress);
                return ConversionResult.Ok(outputDir); // folder of page images
            }

            // Image → image (keep still frames away from the video/GIF path)
            if (MediaTypes.RasterImageExtensions.Contains(ext) && MediaTypes.MagickOutputFormats.Contains(target))
            {
                var options = job.ImageOptions ?? new ImageConversionOptions(Quality: (int)job.QualityLevel);
                await _magick.ConvertImageAsync(job.InputPath, outputPath, options, ct);
                progress?.Report(100);
                return ConversionResult.Ok(outputPath);
            }

            // Video/audio → GIF
            if (target == "gif" && (MediaTypes.VideoExtensions.Contains(ext) || MediaTypes.AudioExtensions.Contains(ext)))
            {
                await _ffmpeg.ConvertToGifAsync(job.InputPath, outputPath, ct, progress);
                progress?.Report(100);
                return ConversionResult.Ok(outputPath);
            }

            // Video → audio only
            if (MediaTypes.VideoExtensions.Contains(ext) && MediaTypes.AudioOutputFormats.Contains(target))
            {
                await _ffmpeg.ExtractAudioAsync(job.InputPath, outputPath, ct, progress);
                progress?.Report(100);
                return ConversionResult.Ok(outputPath);
            }

            // Audio → audio
            if (MediaTypes.AudioExtensions.Contains(ext) && MediaTypes.AudioOutputFormats.Contains(target))
            {
                string args = _ffmpegConcrete.BuildAudioArguments(job.InputPath, outputPath, target, job.QualityLevel);
                await _ffmpeg.ConvertAsync(job.InputPath, outputPath, args, ct, progress);
                return ConversionResult.Ok(outputPath);
            }

            // Video → video
            if (MediaTypes.VideoExtensions.Contains(ext) && MediaTypes.VideoOutputFormats.Contains(target))
            {
                string args = _ffmpegConcrete.BuildVideoArguments(job.InputPath, outputPath,
                    job.QualityLevel, target);
                try
                {
                    await _ffmpeg.ConvertAsync(job.InputPath, outputPath, args, ct, progress);
                }
                catch (ProcessExecutionException ex) when (_ffmpegConcrete.UsesHardwareEncoder(args))
                {
                    ConsoleLogger.Warning("Conversion", $"Hardware video encoder failed ({ex.ExitCode}); retrying with software encoder.");
                    string softwareArgs = _ffmpegConcrete.BuildVideoArguments(job.InputPath, outputPath,
                        job.QualityLevel, target, preferHardware: false);
                    await _ffmpeg.ConvertAsync(job.InputPath, outputPath, softwareArgs, ct, progress);
                }
                return ConversionResult.Ok(outputPath);
            }

            return ConversionResult.Fail($"No engine found for {ext} → {target}");
        }
        catch (OperationCanceledException)
        {
            return ConversionResult.Fail("Conversion cancelled.");
        }
        catch (ProcessExecutionException ex) when (ex.ExecutableName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) && ex.ExitCode == -22)
        {
            return ConversionResult.Fail("Invalid conversion arguments. This file could not be converted with the selected output format.");
        }
        catch (ProcessExecutionException ex)
        {
            return ConversionResult.Fail($"{ex.ExecutableName} failed with exit code {ex.ExitCode}. See the log for technical details.");
        }
        catch (Exception ex)
        {
            return ConversionResult.Fail(ex.Message);
        }
    }

    // Batch processing with true parallelism

    /// <summary>
    /// Convert multiple files in parallel using the configured MaxParallelism.
    /// Returns results in the same order as the input jobs.
    /// Office COM conversions are automatically serialized by OfficeInteropService's
    /// internal semaphores, so they won't cause "Server Busy" errors even at
    /// high parallelism.
    /// </summary>
    public async Task<ConversionResult[]> ConvertBatchAsync(
        IReadOnlyList<ConversionJob> jobs,
        CancellationToken ct = default,
        IProgress<BatchProgress>? batchProgress = null)
    {
        var results = new ConversionResult[jobs.Count];
        int completed = 0;

        // Use SemaphoreSlim for throttling instead of Parallel.ForEach
        // to get proper async/await support
        using var throttle = new SemaphoreSlim(MaxParallelism, MaxParallelism);

        var tasks = jobs.Select(async (job, index) =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var fileProgress = new Progress<double>(p =>
                {
                    batchProgress?.Report(new BatchProgress(
                        TotalFiles: jobs.Count,
                        CompletedFiles: completed,
                        CurrentFileIndex: index,
                        CurrentFilePercent: p));
                });

                results[index] = await ConvertAsync(job, ct, fileProgress);
            }
            catch (OperationCanceledException)
            {
                results[index] = ConversionResult.Fail("Cancelled");
            }
            catch (Exception ex)
            {
                results[index] = ConversionResult.Fail(ex.Message);
            }
            finally
            {
                Interlocked.Increment(ref completed);
                throttle.Release();

                batchProgress?.Report(new BatchProgress(
                    TotalFiles: jobs.Count,
                    CompletedFiles: completed,
                    CurrentFileIndex: index,
                    CurrentFilePercent: 100));
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        return results;
    }

    // Suggest better defaults from file metadata

    /// <summary>
    /// Analyze a file and return smart conversion suggestions.
    /// For example: dropping a 4K video with target "GIF" → suggest downscaling.
    /// </summary>
    public ConversionSuggestion? GetSmartSuggestion(string filePath, string targetFormat)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string target = targetFormat.ToLowerInvariant();

        // Case 1: Large video → GIF (will produce enormous file)
        if (MediaTypes.VideoExtensions.Contains(ext) && target == "gif")
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 100 * 1024 * 1024) // > 100MB video
            {
                return new ConversionSuggestion
                {
                    Warning = "This video is very large. The resulting GIF could exceed 1GB.",
                    Recommendation = "Cortex FX will automatically downscale to 480px width and limit to 10fps for optimal quality/size.",
                    AutoApplied = true
                };
            }
        }

        // Case 2: Ultra-high-res image → ICO (ICO max is 256×256)
        if (MediaTypes.ImageExtensions.Contains(ext) && target == "ico")
        {
            var meta = _magickConcrete?.ReadMetadata(filePath);
            if (meta != null && (meta.Width > 1024 || meta.Height > 1024))
            {
                return new ConversionSuggestion
                {
                    Warning = $"Image is {meta.Width}×{meta.Height}. ICO format is limited to 256×256.",
                    Recommendation = "Cortex FX will automatically resize to 256×256 with aspect ratio preserved.",
                    AutoApplied = true
                };
            }
        }

        // Case 3: Large image → WEBP (suggest quality reduction)
        if (MediaTypes.ImageExtensions.Contains(ext) && target == "webp")
        {
            var meta = _magickConcrete?.ReadMetadata(filePath);
            if (meta != null && meta.FileSizeBytes > 20 * 1024 * 1024) // > 20MB image
            {
                return new ConversionSuggestion
                {
                    Warning = $"Image is {meta.FileSizeBytes / (1024 * 1024)}MB. WebP excels at compression.",
                    Recommendation = "Quality set to 80 for optimal size reduction with minimal visual loss.",
                    AutoApplied = false
                };
            }
        }

        return null;
    }

    // Private routing helpers

    private static bool IsDocumentToPdf(string ext, string target)
    {
        return target == "pdf" && (
            ext is ".docx" or ".doc" ||
            ext is ".xlsx" or ".xls" ||
            ext is ".pptx" or ".ppt");
    }

    private static bool IsOfficeTarget(string target)
    {
        return target is "docx" or "pptx" or "xlsx";
    }

    private static bool IsDocumentBridge(string ext, string target)
    {
        bool wordToPpt = ext is ".docx" or ".doc" && target == "pptx";
        bool pptToWord = ext is ".pptx" or ".ppt" && target == "docx";
        return wordToPpt || pptToWord;
    }

    private async Task<ConversionResult> HandlePdfToOfficeAsync(string inputFile, string outputPath,
        string target, CancellationToken ct, IProgress<double>? progress)
    {
        switch (target)
        {
            case "docx":
                await _office.ConvertPdfToWordAsync(inputFile, outputPath, ct, progress);
                break;
            case "pptx":
                await _office.ConvertPdfToPowerPointAsync(inputFile, outputPath, ct, progress);
                break;
            case "xlsx":
                return ConversionResult.Fail("PDF to XLSX is not supported yet. Convert the PDF to DOCX first, then extract tables manually.");
        }
        return ConversionResult.Ok(outputPath);
    }

    private async Task<ConversionResult> HandleDocumentBridgeAsync(string inputFile, string outputPath,
        string ext, string target, CancellationToken ct, IProgress<double>? progress)
    {
        if (ext is ".docx" or ".doc" && target == "pptx")
        {
            await _office.ConvertWordToPowerPointAsync(inputFile, outputPath, ct, progress);
        }
        else if (ext is ".pptx" or ".ppt" && target == "docx")
        {
            // Bridge: PPT → PDF → Word
            string tempPdf = Path.Combine(
                Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
                Guid.NewGuid() + ".pdf");
            try
            {
                var pdfProgress = new Progress<double>(p => progress?.Report(p * 0.5));
                await _office.ConvertToPdfAsync(inputFile, tempPdf, 1, ct, pdfProgress);

                var wordProgress = new Progress<double>(p => progress?.Report(50 + (p * 0.5)));
                await _office.ConvertPdfToWordAsync(tempPdf, outputPath, ct, wordProgress);
            }
            finally
            {
                try { if (File.Exists(tempPdf)) File.Delete(tempPdf); } catch { }
            }
        }
        return ConversionResult.Ok(outputPath);
    }

    private static string ResolveOutputDirectory(ConversionJob job)
    {
        string outputDir = job.OutputDirectory;

        if (job.CreateSubfolder)
        {
            outputDir = Path.Combine(outputDir, "Cortex FX");
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
        }

        return outputDir;
    }
}

// Shared result types

/// <summary>How far a batch convert has gotten.</summary>
public record BatchProgress(int TotalFiles, int CompletedFiles, int CurrentFileIndex, double CurrentFilePercent);

/// <summary>Optional tip shown in the convert UI.</summary>
public class ConversionSuggestion
{
    public required string Warning { get; init; }
    public required string Recommendation { get; init; }

    /// <summary>True when we already applied the tip for the user.</summary>
    public bool AutoApplied { get; init; }
}
