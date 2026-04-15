using System.IO;
using ImageMagick;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services;

/// <summary>
/// ImageMagick service using a hybrid approach:
///   - Magick.NET managed library for operations that benefit from direct memory access
///     (merging, metadata reads, format detection)
///   - CLI magick.exe for heavy conversions (leverages disk streaming, avoids OOM on large files)
///
/// Memory optimization: For images > 50MB, delegates to CLI to avoid managed heap pressure.
/// </summary>
public sealed class MagickService : IMagickService
{
    private readonly IAppConfiguration _config;
    private readonly IProcessManager _processManager;

    /// <summary>
    /// Files larger than this threshold (bytes) will be processed via CLI
    /// instead of the managed Magick.NET library to avoid excessive memory use.
    /// Default: 50 MB.
    /// </summary>
    private const long LargeFileThreshold = 50 * 1024 * 1024;

    public MagickService(IAppConfiguration config, IProcessManager processManager)
    {
        _config = config;
        _processManager = processManager;
    }

    // ------------------------------------------------------------------
    // IIMagickService implementation
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task ConvertImageAsync(string inputFile, string outputFile,
                                         ImageConversionOptions options,
                                         CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var fileInfo = new FileInfo(inputFile);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"Input file not found: {inputFile}");

        // Route: Large files go through CLI to avoid OOM
        if (fileInfo.Length > LargeFileThreshold)
        {
            await ConvertViaCliAsync(inputFile, outputFile, options, ct);
            return;
        }

        // Small/medium files: use managed Magick.NET for speed
        await Task.Run(() => ConvertManaged(inputFile, outputFile, options, ct), ct);
    }

    /// <inheritdoc />
    public async Task MergeImagesToPdfAsync(IReadOnlyList<string> imagePaths, string outputPath,
                                             CancellationToken ct = default)
    {
        if (imagePaths.Count == 0)
            throw new ArgumentException("No images to merge.", nameof(imagePaths));

        await Task.Run(() =>
        {
            using var collection = new MagickImageCollection();

            foreach (var imgPath in imagePaths)
            {
                ct.ThrowIfCancellationRequested();

                var image = new MagickImage(imgPath);

                // Optimize memory for large collections: reduce to print resolution
                if (image.Width > 2480 || image.Height > 3508) // > A4 at 300 DPI
                {
                    var size = new MagickGeometry(2480, 3508)
                    {
                        IgnoreAspectRatio = false
                    };
                    image.Resize(size);
                }

                image.Format = MagickFormat.Pdf;
                collection.Add(image);
            }

            collection.Write(outputPath);
        }, ct);
    }

    // ------------------------------------------------------------------
    // Public helpers (used by ConversionRouter for smart suggestions)
    // ------------------------------------------------------------------

    /// <summary>
    /// Read basic image metadata without loading the full pixel data.
    /// Returns null if the file cannot be identified.
    /// </summary>
    public ImageMetadata? ReadMetadata(string filePath)
    {
        try
        {
            var info = new MagickImageInfo(filePath);
            return new ImageMetadata
            {
                Width = (int)info.Width,
                Height = (int)info.Height,
                Format = info.Format.ToString(),
                FileSizeBytes = new FileInfo(filePath).Length
            };
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Managed Magick.NET conversion (files < 50 MB)
    // ------------------------------------------------------------------

    private void ConvertManaged(string inputFile, string outputFile,
                                 ImageConversionOptions options, CancellationToken ct)
    {
        using var image = new MagickImage(inputFile);
        ct.ThrowIfCancellationRequested();

        // Quality
        image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);

        // Resize
        if (options.ResizeWidth.HasValue && options.ResizeHeight.HasValue)
        {
            var geometry = new MagickGeometry((uint)options.ResizeWidth.Value, (uint)options.ResizeHeight.Value)
            {
                IgnoreAspectRatio = !options.MaintainAspectRatio
            };
            image.Resize(geometry);
        }
        else if (options.Quality < 30)
        {
            // Low quality → auto-downscale to 80% for smaller file size
            image.Resize(new Percentage(80));
        }

        // DPI
        if (options.Dpi.HasValue)
        {
            image.Density = new Density(options.Dpi.Value, options.Dpi.Value, DensityUnit.PixelsPerInch);
        }

        // Sharpen
        if (options.Sharpen)
        {
            image.Sharpen(0, 1);
        }

        // Grayscale
        if (options.Grayscale)
        {
            image.Grayscale();
        }

        // Auto-enhance
        if (options.AutoEnhance)
        {
            image.Normalize();
            image.AutoLevel();
        }

        // ICO special handling: enforce 256×256 maximum
        string ext = Path.GetExtension(outputFile).ToLowerInvariant();
        if (ext == ".ico")
        {
            image.Resize(new MagickGeometry(256, 256) { IgnoreAspectRatio = false });
        }

        ct.ThrowIfCancellationRequested();
        image.Write(outputFile);
    }

    // ------------------------------------------------------------------
    // CLI conversion (files > 50 MB — avoids managed heap pressure)
    // ------------------------------------------------------------------

    private async Task ConvertViaCliAsync(string inputFile, string outputFile,
                                           ImageConversionOptions options, CancellationToken ct)
    {
        EnsureMagickCli();

        var args = new System.Text.StringBuilder();
        args.Append($"\"{inputFile}\" ");

        // Quality
        int q = Math.Clamp(options.Quality, 1, 100);
        args.Append($"-quality {q} ");

        if (options.Quality < 30)
        {
            args.Append("-resize 80% ");
        }

        // Resize (explicit overrides quality-based resize)
        if (options.ResizeWidth.HasValue && options.ResizeHeight.HasValue)
        {
            args.Append($"-resize {options.ResizeWidth}x{options.ResizeHeight}");
            if (!options.MaintainAspectRatio) args.Append('!');
            args.Append(' ');
        }

        // DPI
        if (options.Dpi.HasValue)
        {
            args.Append($"-density {options.Dpi.Value} -units PixelsPerInch ");
        }

        // Effects
        if (options.Sharpen) args.Append("-sharpen 0x1 ");
        if (options.Grayscale) args.Append("-colorspace Gray ");
        if (options.AutoEnhance) args.Append("-normalize -auto-level ");

        // ICO
        string ext = Path.GetExtension(outputFile).ToLowerInvariant();
        if (ext == ".ico") args.Append("-resize 256x256 ");

        args.Append($"\"{outputFile}\"");

        await _processManager.RunAsync(_config.MagickPath, args.ToString(), ct);
    }

    private void EnsureMagickCli()
    {
        if (!File.Exists(_config.MagickPath))
            throw new FileNotFoundException($"ImageMagick CLI not found at: {_config.MagickPath}");
    }
}

/// <summary>
/// Lightweight image metadata for smart routing decisions.
/// </summary>
public class ImageMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string Format { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public double MegaPixels => (Width * Height) / 1_000_000.0;
    public bool IsLarge => FileSizeBytes > 50 * 1024 * 1024;
}
