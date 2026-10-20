using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Media;
using CortexFX.Models;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CortexFX.Core.Radial;

public sealed class RadialConversionService(IConversionRouter router, IAppConfiguration configuration,
    IProcessManager processes, IMagickService magick, IPdfRenderService pdfRenderer)
{
    private readonly Dictionary<string, IFFmpegService> _media = new(StringComparer.OrdinalIgnoreCase);

    public Task<RadialResult> ExecuteAsync(RadialSelection selection, RadialAction action,
        RadialSettings settings, IProgress<RadialProgress>? progress = null, CancellationToken ct = default)
    {
        // Snapshot preferences: a settings save must not change an active job.
        settings = new RadialSettings
        {
            Quality = settings.Quality, PdfDpi = settings.PdfDpi, FFmpegPath = settings.FFmpegPath,
            ConvertKey = settings.ConvertKey, ToolsKey = settings.ToolsKey, QuickKey = settings.QuickKey
        };
        settings.Validate();
        if (!action.Enabled) throw new InvalidOperationException(action.UnavailableReason);
        return Task.Run(() => ExecuteCoreAsync(selection, action, settings, progress, ct), ct);
    }

    private async Task<RadialResult> ExecuteCoreAsync(RadialSelection selection, RadialAction action,
        RadialSettings settings, IProgress<RadialProgress>? progress, CancellationToken ct)
    {
        List<string> outputs = [];
        try
        {
            ct.ThrowIfCancellationRequested();
            if (selection.Paths.Any(path => !File.Exists(path))) throw new FileNotFoundException("A selected file no longer exists.");
            if (action.Operation == RadialOperation.CopyPaths)
                return new([], "Paths copied", string.Join(Environment.NewLine, selection.Paths));
            if (action.Operation == RadialOperation.Zip)
                return await CreateZipAsync(selection, progress, ct);
            if (action.Operation == RadialOperation.Sha256)
                return await HashAsync(selection, progress, ct);
            if (action.Operation == RadialOperation.MergePdf)
                return MergePdf(selection, progress, ct);
            if (action.Operation == RadialOperation.Convert && action.Target == "pdf" && selection.Category == "Image")
            {
                using var output = new RadialOutput(selection.Paths[0]);
                Report(progress, -1, "Combining images into PDF");
                await magick.MergeImagesToPdfAsync(selection.Paths, output.FilePath("pdf"), ct);
                ct.ThrowIfCancellationRequested();
                return new([output.CommitFile(output.FilePath("pdf"), "pdf", selection.Paths.Count > 1 ? "merged" : "")], "PDF saved");
            }
            for (int index = 0; index < selection.Paths.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                string source = selection.Paths[index];
                int position = index;
                var update = new InlineProgress<double>(percent => Report(progress,
                    percent < 0 ? -1 : (position + Math.Clamp(percent, 0, 100) / 100) / selection.Paths.Count * 100,
                    $"{position + 1}/{selection.Paths.Count} · {Path.GetFileName(source)}"));
                Report(progress, -1, $"{index + 1}/{selection.Paths.Count} · {action.Label}");
                using var output = new RadialOutput(source);
                string target = action.Target;
                string tag = "";
                string produced;
                bool folder = false;
                switch (action.Operation)
                {
                    case RadialOperation.CropImage:
                        if (selection.Paths.Count != 1 || selection.Category != "Image" || action.Crop is null)
                            throw new ArgumentException("Choose one image and a valid crop region.");
                        target = Path.GetExtension(source).TrimStart('.');
                        produced = output.FilePath(target);
                        RadialImageTools.Crop(source, produced, action.Crop, ct);
                        tag = "cropped";
                        break;
                    case RadialOperation.CompressImage:
                    case RadialOperation.StripMetadata:
                    case RadialOperation.ResizeHalf:
                        target = Path.GetExtension(source).TrimStart('.');
                        produced = output.FilePath(target);
                        if (action.Operation == RadialOperation.CompressImage && action.Compression is not null)
                            RadialTargetCompression.Image(source, produced, action.Compression, update, ct);
                        else ProcessImage(source, produced, settings.Quality, action.Operation, ct);
                        tag = action.Operation switch
                        {
                            RadialOperation.CompressImage => "compressed", RadialOperation.StripMetadata => "clean", _ => "half"
                        };
                        break;
                    case RadialOperation.CompressVideo:
                    case RadialOperation.ExtractAudio:
                    case RadialOperation.AnimatedGif:
                        produced = output.FilePath(target);
                        await ConvertMediaAsync(source, produced, target, settings, action.Operation == RadialOperation.CompressVideo, update, ct);
                        tag = action.Operation == RadialOperation.CompressVideo ? "compressed" : "";
                        break;
                    case RadialOperation.CompressPdf:
                        target = "pdf";
                        produced = output.FilePath(target);
                        if (action.Compression is not null)
                            RadialTargetCompression.Pdf(source, produced, action.Compression, update, ct);
                        else
                        {
                            using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
                            {
                                document.Options.CompressContentStreams = true;
                                document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
                                document.Save(produced);
                            }
                            if (new FileInfo(produced).Length >= new FileInfo(source).Length) File.Copy(source, produced, true);
                        }
                        tag = "compressed";
                        break;
                    case RadialOperation.SplitPdf:
                        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Import))
                        {
                            for (int page = 0; page < document.PageCount; page++)
                            {
                                ct.ThrowIfCancellationRequested();
                                using var single = new PdfDocument();
                                single.AddPage(document.Pages[page]);
                                single.Save(Path.Combine(output.WorkDirectory, $"page-{page + 1:0000}.pdf"));
                                update.Report((page + 1d) / document.PageCount * 99);
                            }
                        }
                        produced = output.WorkDirectory;
                        folder = true;
                        tag = "pages";
                        break;
                    case RadialOperation.PdfImages:
                        await pdfRenderer.RenderPdfToImagesAsync(source, output.WorkDirectory, target, settings.PdfDpi, ct, update);
                        produced = output.WorkDirectory;
                        folder = true;
                        tag = target;
                        break;
                    case RadialOperation.Convert:
                        if (RadialSelection.GetCategory(source) == "Image" && target != "pdf")
                        {
                            produced = output.FilePath(target);
                            ProcessImage(source, produced, settings.Quality, RadialOperation.Convert, ct);
                        }
                        else if (RadialSelection.GetCategory(source) is "Audio" or "Video")
                        {
                            produced = output.FilePath(target);
                            await ConvertMediaAsync(source, produced, target, settings, false, update, ct);
                        }
                        else
                        {
                            var result = await router.ConvertAsync(new ConversionJob
                            {
                                InputPath = source, OutputDirectory = output.WorkDirectory, TargetFormat = target,
                                QualityLevel = settings.Quality, CreateSubfolder = false,
                                ImageOptions = new ImageConversionOptions(Quality: settings.Quality, Dpi: settings.PdfDpi)
                            }, ct, update);
                            ct.ThrowIfCancellationRequested();
                            if (!result.Success || result.OutputPath is null)
                                throw new InvalidOperationException(result.ErrorMessage ?? "The conversion engine did not return an output.");
                            produced = result.OutputPath;
                            folder = Directory.Exists(produced);
                            tag = folder ? target : "";
                        }
                        break;
                    default: throw new NotSupportedException($"Unsupported radial action: {action.Operation}");
                }
                ct.ThrowIfCancellationRequested();
                if (folder) outputs.AddRange(output.CommitDirectory(tag));
                else outputs.Add(output.CommitFile(produced, target, tag));
                update.Report(100);
            }
            string message = $"Saved {outputs.Count} file(s)";
            if (action.Compression is not null && outputs.Count > 0)
            {
                long saved = outputs.Sum(path => new FileInfo(path).Length);
                double reduction = (1 - saved / (double)Math.Max(1, selection.Bytes)) * 100;
                message = $"Saved {outputs.Count} file(s) · {RadialTargetCompression.Size(saved)} · {Math.Max(0, reduction):0}% smaller";
            }
            return new(outputs, message);
        }
        catch (Exception error) when (error is not RadialExecutionException)
        {
            throw new RadialExecutionException(error is OperationCanceledException ? "Conversion cancelled." : error.Message, outputs, error);
        }
    }

    private static void ProcessImage(string source, string destination, int quality, RadialOperation operation, CancellationToken ct)
    {
        using var image = new MagickImage(source);
        image.AutoOrient();
        image.Quality = (uint)quality;
        if (operation == RadialOperation.StripMetadata) image.Strip();
        if (operation == RadialOperation.ResizeHalf) image.Resize(new Percentage(50));
        if (Path.GetExtension(destination).ToLowerInvariant() is ".jpg" or ".jpeg" or ".bmp")
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
        }
        if (Path.GetExtension(destination).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            image.Resize(new MagickGeometry(256, 256) { IgnoreAspectRatio = false });
        if (operation == RadialOperation.CompressImage)
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        ct.ThrowIfCancellationRequested();
        image.Write(destination);
        if (operation == RadialOperation.CompressImage && new FileInfo(destination).Length >= new FileInfo(source).Length)
            File.Copy(source, destination, true);
    }

    private async Task ConvertMediaAsync(string source, string destination, string target, RadialSettings settings,
        bool compress, IProgress<double> progress, CancellationToken ct)
    {
        string executable = string.IsNullOrWhiteSpace(settings.FFmpegPath) ? configuration.FFmpegPath : settings.FFmpegPath.Trim('"');
        if (!File.Exists(executable)) throw new FileNotFoundException("FFmpeg is missing. Choose ffmpeg.exe in radial wheel settings.");
        IFFmpegService engine;
        lock (_media)
        {
            if (!_media.TryGetValue(executable, out engine!))
                _media[executable] = engine = new FFmpegService(new WheelConfiguration(configuration, executable), processes, true);
        }
        if (target == "gif") { await engine.ConvertToGifAsync(source, destination, ct, progress); return; }
        if (RadialSelection.GetCategory(source) == "Video" && target == "mp3")
        { await engine.ExtractAudioAsync(source, destination, ct, progress); return; }
        double quality = compress ? Math.Min(45, settings.Quality) : settings.Quality;
        bool audio = RadialSelection.GetCategory(source) == "Audio";
        string arguments = audio ? engine.BuildAudioArguments(source, destination, target, quality)
            : engine.BuildVideoArguments(source, destination, quality, target);
        try { await engine.ConvertAsync(source, destination, arguments, ct, progress); }
        catch (ProcessExecutionException) when (!audio && engine.UsesHardwareEncoder(arguments))
        { await engine.ConvertAsync(source, destination, engine.BuildVideoArguments(source, destination, quality, target, false), ct, progress); }
    }

    private static async Task<RadialResult> CreateZipAsync(RadialSelection selection, IProgress<RadialProgress>? progress, CancellationToken ct)
    {
        using var output = new RadialOutput(selection.Paths[0]);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long copied = 0;
        using (var stream = File.Create(output.FilePath("zip")))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (string path in selection.Paths)
            {
                string name = Path.GetFileName(path);
                for (int suffix = 1; !names.Add(name); suffix++) name = $"{Path.GetFileNameWithoutExtension(path)} ({suffix}){Path.GetExtension(path)}";
                using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                using var input = File.OpenRead(path);
                byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await entry.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;
                    Report(progress, copied / (double)Math.Max(1, selection.Bytes) * 99, "Creating ZIP");
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        return new([output.CommitFile(output.FilePath("zip"), "zip", selection.Paths.Count > 1 ? "bundle" : "")], "ZIP saved");
    }

    private static async Task<RadialResult> HashAsync(RadialSelection selection, IProgress<RadialProgress>? progress, CancellationToken ct)
    {
        StringBuilder manifest = new();
        long processed = 0;
        foreach (string path in selection.Paths)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var input = File.OpenRead(path);
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                processed += read;
                Report(progress, processed / (double)Math.Max(1, selection.Bytes) * 99, "Calculating SHA-256");
            }
            manifest.Append(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()).Append("  ").AppendLine(path);
        }
        using var output = new RadialOutput(selection.Paths[0]);
        await File.WriteAllTextAsync(output.FilePath("sha256"), manifest.ToString(), Encoding.UTF8, ct);
        ct.ThrowIfCancellationRequested();
        return new([output.CommitFile(output.FilePath("sha256"), "sha256")], "SHA-256 saved and copied", manifest.ToString());
    }

    private static RadialResult MergePdf(RadialSelection selection, IProgress<RadialProgress>? progress, CancellationToken ct)
    {
        using var output = new RadialOutput(selection.Paths[0]);
        using (var result = new PdfDocument())
        {
            for (int index = 0; index < selection.Paths.Count; index++)
            {
                using var document = PdfReader.Open(selection.Paths[index], PdfDocumentOpenMode.Import);
                foreach (var page in document.Pages) { ct.ThrowIfCancellationRequested(); result.AddPage(page); }
                Report(progress, (index + 1d) / selection.Paths.Count * 95, "Merging PDF pages");
            }
            result.Save(output.FilePath("pdf"));
        }
        ct.ThrowIfCancellationRequested();
        return new([output.CommitFile(output.FilePath("pdf"), "pdf", "merged")], "Merged PDF saved");
    }

    private static void Report(IProgress<RadialProgress>? progress, double value, string message) => progress?.Report(new(value, message));

    private sealed class WheelConfiguration(IAppConfiguration original, string ffmpeg) : IAppConfiguration
    {
        public string ResourcesDirectory => original.ResourcesDirectory;
        public string FFmpegPath => ffmpeg;
        public string MagickPath => original.MagickPath;
        public string PdfToCairoPath => original.PdfToCairoPath;
        public string FFmpegLibsDirectory => original.FFmpegLibsDirectory;
    }
}
