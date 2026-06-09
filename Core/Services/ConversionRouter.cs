using System.IO;
using CortexFX.Core.Configuration;
using CortexFX.Core.Constants;
using CortexFX.Core.Interfaces;
using CortexFX.Models;

namespace CortexFX.Core.Services;

/// <summary>
/// Central conversion router with:
///   - Smart intent detection (metadata-aware suggestions)
///   - True parallel batch processing with configurable concurrency
///   - Unified engine selection: determines which service handles each conversion
///
/// Replaces the legacy document/media routing that used to live in MainWindow.
/// </summary>
public sealed class ConversionRouter : IConversionRouter
{
    private readonly IAppConfiguration _config;
    private readonly IFFmpegService _ffmpeg;
    private readonly IMagickService _magick;
    private readonly IOfficeInteropService _office;
    private readonly IPdfRenderService _pdfRenderer;
    private readonly IOptionalConversionService _optional;
    private readonly FFmpegService _ffmpegConcrete; // For smart argument builders
    private readonly MagickService _magickConcrete;  // For metadata reads

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

    // ------------------------------------------------------------------
    // IConversionRouter
    // ------------------------------------------------------------------

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

            // --- Route to the correct engine ---

            // 1. Document → PDF (Office COM)
            if (IsDocumentToPdf(ext, target))
            {
                int engineQuality = job.QualityLevel < 30 ? 0 : 1;
                await _office.ConvertToPdfAsync(job.InputPath, outputPath, engineQuality, ct, progress);
                return ConversionResult.Ok(outputPath);
            }

            // 2. PDF → Office (via COM bridge)
            if (ext == ".pdf" && IsOfficeTarget(target))
            {
                return await HandlePdfToOfficeAsync(job.InputPath, outputPath, target, ct, progress);
            }

            // 3. Document bridge: Word ↔ PPT
            if (IsDocumentBridge(ext, target))
            {
                return await HandleDocumentBridgeAsync(job.InputPath, outputPath, ext, target, ct, progress);
            }

            // 3b. Optional local engines: LibreOffice, 7-Zip, Calibre
            if (_optional.CanConvert(ext, target))
            {
                return await _optional.ConvertAsync(job.InputPath, outputPath, ext, target, ct, progress);
            }

            // 4. Image → PDF (Magick native)
            if (MediaTypes.RasterImageExtensions.Contains(ext) && target == "pdf")
            {
                await _magick.ConvertImageAsync(job.InputPath, outputPath,
                    job.ImageOptions ?? new ImageConversionOptions(Quality: (int)job.QualityLevel), ct);
                progress?.Report(100);
                return ConversionResult.Ok(outputPath);
            }

            // 5. PDF → Image (Pdfium renderer)
            if (ext == ".pdf" && MediaTypes.MagickOutputFormats.Contains(target))
            {
                int dpi = PdfRenderService.QualityToDpi(job.QualityLevel, job.ImageOptions?.Dpi);
                await _pdfRenderer.RenderPdfToImagesAsync(job.InputPath, outputDir, target, dpi, ct, progress);
                return ConversionResult.Ok(outputDir); // Multiple output files
            }

            // 6. Image -> Image (Magick). Keep still images off the FFmpeg video/GIF path.
            if (MediaTypes.RasterImageExtensions.Contains(ext) && MediaTypes.MagickOutputFormats.Contains(target))
            {
                var options = job.ImageOptions ?? new ImageConversionOptions(Quality: (int)job.QualityLevel);
                await _magick.ConvertImageAsync(job.InputPath, outputPath, options, ct);
                progress?.Report(100);
                return ConversionResult.Ok(outputPath);
            }

            // 7. Video/Audio → GIF (special FFmpeg pipeline)
            if (target == "gif" && (MediaTypes.VideoExtensions.Contains(ext) || MediaTypes.AudioExtensions.Contains(ext)))
            {
                await _ffmpeg.ConvertToGifAsync(job.InputPath, outputPath, ct);
                progress?.Report(100);
                return ConversionResult.Ok(outputPath);
            }

            // 8. Media → Audio extraction (FFmpeg -vn)
            if (MediaTypes.VideoExtensions.Contains(ext) && MediaTypes.AudioOutputFormats.Contains(target))
            {
                await _ffmpeg.ExtractAudioAsync(job.InputPath, outputPath, ct);
                progress?.Report(100);
                return ConversionResult.Ok(outputPath);
            }

            // 9. FFmpeg media conversion (video↔video, audio↔audio)
            if (MediaTypes.FFmpegOutputFormats.Contains(target))
            {
                string args = _ffmpegConcrete.BuildVideoArguments(job.InputPath, outputPath,
                    job.QualityLevel, target);
                await _ffmpeg.ConvertAsync(job.InputPath, outputPath, args, ct, progress);
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

    // ------------------------------------------------------------------
    // Batch processing with true parallelism
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // Smart intent detection
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // Private routing helpers
    // ------------------------------------------------------------------

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

// ------------------------------------------------------------------
// Supporting types
// ------------------------------------------------------------------

/// <summary>Progress report for batch conversion.</summary>
public record BatchProgress(int TotalFiles, int CompletedFiles, int CurrentFileIndex, double CurrentFilePercent);

/// <summary>Smart suggestion from the conversion router's intent analysis.</summary>
public class ConversionSuggestion
{
    public required string Warning { get; init; }
    public required string Recommendation { get; init; }

    /// <summary>If true, Cortex FX applies the optimization automatically.</summary>
    public bool AutoApplied { get; init; }
}
