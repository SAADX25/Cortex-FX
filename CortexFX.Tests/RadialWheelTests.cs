using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CortexFX.Core.Radial;
using CortexFX.Core.Services.Documents;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Core.Services.Media;
using CortexFX.Views.Radial;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace CortexFX.Tests;

public sealed class RadialWheelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CortexFX_Radial_" + Guid.NewGuid().ToString("N"), "ملفات مع مسافات");
    public RadialWheelTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_folder)!, true);
    private string FileName(string name) => Path.Combine(_folder, name);
    private string ImageFile(string name = "صورة اختبار.png")
    {
        string path = FileName(name);
        using var image = new MagickImage(new MagickColor("#DC143C80"), 120, 80);
        image.Write(path); return path;
    }
    private string PdfFile(string name = "مستند sample.pdf", int pages = 3)
    {
        string path = FileName(name);
        using var document = new PdfDocument();
        for (int index = 0; index < pages; index++) document.AddPage();
        document.Save(path); return path;
    }
    private static RadialConversionService Service()
    {
        var config = new FakeConfig("."); var processes = new FakeProcesses();
        return new(new FakeEngines().Router, config, processes, new MagickService(config, processes), new PdfRenderService());
    }

    [Theory]
    [InlineData(".jpg", "jpg")]
    [InlineData("JPEG", "jpg")]
    [InlineData(".tif", "tiff")]
    [InlineData("HEIF", "heic")]
    public void ExtensionAliasesAreNormalized(string input, string expected) => Assert.Equal(expected, RadialActionCatalog.Normalize(input));

    [Fact]
    public void CatalogExcludesEveryOriginalFormatInABatch()
    {
        string first = ImageFile(); string second = ImageFile("second.jpg");
        var selection = RadialSelection.FromPaths([first, second, first]);
        Assert.Equal(2, selection.Paths.Count);
        var actions = new RadialActionCatalog(new FakeEngines().Router, new FakeConfig(".")).GetActions(selection, RadialMode.Convert, new());
        Assert.DoesNotContain(actions, action => action.Target is "png" or "jpg");
        Assert.Contains(actions, action => action.Target == "avif");
        Assert.Contains(actions, action => action.Target == "pdf");
    }

    [Fact]
    public void MissingFFmpegDisablesMediaButAllowsQuickActions()
    {
        string source = FileName("movie.mp4"); File.WriteAllText(source, "video");
        var selection = RadialSelection.FromPaths([source]);
        var catalog = new RadialActionCatalog(new FakeEngines().Router, new FakeConfig(_folder));
        Assert.All(catalog.GetActions(selection, RadialMode.Tools, new()), action =>
        { Assert.False(action.Enabled); Assert.Contains("FFmpeg", action.UnavailableReason); });
        Assert.All(catalog.GetActions(selection, RadialMode.Quick, new()), action => Assert.True(action.Enabled));
    }

    [Fact]
    public void MergeOnlyAppearsForSeveralPdfs()
    {
        var catalog = new RadialActionCatalog(new FakeEngines().Router, new FakeConfig("."));
        string first = PdfFile(), second = PdfFile("second.pdf");
        Assert.DoesNotContain(catalog.GetActions(RadialSelection.FromPaths([first]), RadialMode.Tools, new()), action => action.Operation == RadialOperation.MergePdf);
        Assert.Contains(catalog.GetActions(RadialSelection.FromPaths([first, second]), RadialMode.Tools, new()), action => action.Operation == RadialOperation.MergePdf);
    }

    [Fact]
    public void GestureRequiresMovementAndOnlyReleasesAnActualGesture()
    {
        var state = new DragGestureState();
        Assert.Null(state.Update(true, new(0, 0), RadialMode.Convert, true).Triggered);
        Assert.Equal(RadialMode.Convert, state.Update(true, new(20, 20), RadialMode.Convert, true).Triggered);
        Assert.Null(state.Update(true, new(25, 25), RadialMode.Convert, true).Triggered);
        Assert.Equal(RadialMode.Tools, state.Update(true, new(25, 25), RadialMode.Tools, true).Triggered);
        Assert.True(state.Update(false, new(25, 25), null, true).Released);
        state.Update(true, new(0, 0), null, true);
        Assert.False(state.Update(false, new(0, 0), null, true).Released);
        state.Update(true, new(0, 0), RadialMode.Convert, false);
        Assert.Null(state.Update(true, new(100, 100), RadialMode.Convert, false).Triggered);
    }

    [Theory]
    [InlineData(3)] [InlineData(6)] [InlineData(9)]
    public void PolarSegmentsMatchTheirVisualCentres(int count)
    {
        for (int index = 0; index < count; index++)
        {
            double angle = (-90 + index * 360d / count) * Math.PI / 180;
            Assert.Equal(index, RadialGeometry.Hit(270 + Math.Cos(angle) * 170, 270 + Math.Sin(angle) * 170, count));
        }
        Assert.Equal(-1, RadialGeometry.Hit(270, 270, count));
        Assert.Equal(-1, RadialGeometry.Hit(0, 0, count));
    }

    [Fact]
    public async Task OutputReservationIsAtomicAndKeepsOriginals()
    {
        string source = FileName("same.txt"); File.WriteAllText(source, "original");
        var outputs = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            using var output = new RadialOutput(source);
            File.WriteAllText(output.FilePath("txt"), index.ToString());
            return output.CommitFile(output.FilePath("txt"), "txt");
        })));
        Assert.Equal(8, outputs.Distinct().Count());
        Assert.Equal("original", File.ReadAllText(source));
        Assert.Empty(Directory.GetDirectories(_folder, ".cortex-wheel-*"));
    }

    [Fact]
    public void EmptyAndUnexpectedOutputsAreRejectedAndCleaned()
    {
        string source = ImageFile();
        using (var output = new RadialOutput(source))
        {
            Assert.Throws<IOException>(() => output.CommitFile(source, "png"));
            Assert.Throws<InvalidDataException>(() => output.CommitDirectory("empty"));
        }
        Assert.Empty(Directory.GetDirectories(_folder, ".cortex-wheel-*"));
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData("jpg")] [InlineData("png")] [InlineData("webp")] [InlineData("heic")]
    [InlineData("avif")] [InlineData("bmp")] [InlineData("tiff")] [InlineData("ico")]
    public async Task RealImageConversionsPreserveTheSource(string target)
    {
        string source = ImageFile(); byte[] before = File.ReadAllBytes(source);
        var result = await Service().ExecuteAsync(RadialSelection.FromPaths([source]), new(target, RadialOperation.Convert, target), new());
        Assert.Single(result.Outputs);
        Assert.NotEqual(source, result.Outputs[0]);
        Assert.Equal(before, File.ReadAllBytes(source));
        using var converted = new MagickImage(result.Outputs[0]);
        Assert.True(converted.Width > 0 && converted.Height > 0);
    }

    [Theory]
    [InlineData(RadialOperation.CompressImage)]
    [InlineData(RadialOperation.StripMetadata)]
    [InlineData(RadialOperation.ResizeHalf)]
    public async Task RealImageTools(RadialOperation operation)
    {
        string source = ImageFile();
        using (var image = new MagickImage(source)) { image.SetAttribute("comment", "Private metadata"); image.Write(source); }
        var result = await Service().ExecuteAsync(RadialSelection.FromPaths([source]), new("tool", operation), new());
        using var converted = new MagickImage(result.Outputs[0]);
        if (operation == RadialOperation.ResizeHalf) { Assert.Equal(60u, converted.Width); Assert.Equal(40u, converted.Height); }
        if (operation == RadialOperation.CompressImage) Assert.True(new FileInfo(result.Outputs[0]).Length <= new FileInfo(source).Length);
        if (operation == RadialOperation.StripMetadata) Assert.Null(converted.GetAttribute("comment"));
    }

    [Fact]
    public async Task RealPdfToolsAndImagesMerge()
    {
        string pdf = PdfFile(); var service = Service(); var selection = RadialSelection.FromPaths([pdf]);
        var split = await service.ExecuteAsync(selection, new("split", RadialOperation.SplitPdf), new());
        Assert.Equal(3, split.Outputs.Count);
        foreach (string page in split.Outputs) { using var document = PdfReader.Open(page, PdfDocumentOpenMode.Import); Assert.Equal(1, document.PageCount); }
        var merged = await service.ExecuteAsync(RadialSelection.FromPaths(split.Outputs), new("merge", RadialOperation.MergePdf), new());
        using (var document = PdfReader.Open(merged.Outputs[0], PdfDocumentOpenMode.Import)) Assert.Equal(3, document.PageCount);
        var compressed = await service.ExecuteAsync(selection, new("compress", RadialOperation.CompressPdf), new());
        Assert.True(new FileInfo(compressed.Outputs[0]).Length <= new FileInfo(pdf).Length);
        string first = ImageFile(), second = ImageFile("second.png");
        var imagePdf = await service.ExecuteAsync(RadialSelection.FromPaths([first, second]), new("PDF", RadialOperation.Convert, "pdf"), new());
        using var imageDocument = PdfReader.Open(imagePdf.Outputs[0], PdfDocumentOpenMode.Import);
        Assert.Equal(2, imageDocument.PageCount);
    }

    [Fact]
    public async Task RealPdfPageRendering()
    {
        var result = await Service().ExecuteAsync(RadialSelection.FromPaths([PdfFile(pages: 2)]), new("PNG", RadialOperation.PdfImages, "png"), new() { PdfDpi = 150 });
        Assert.Equal(2, result.Outputs.Count);
        using var image = new MagickImage(result.Outputs[0]);
        Assert.True(image.Width >= 1200);
    }

    [Fact]
    public async Task ZipAndHashesSupportArabicPathsAndDuplicateNames()
    {
        string first = ImageFile(); string otherFolder = Path.Combine(_folder, "second"); Directory.CreateDirectory(otherFolder);
        string second = Path.Combine(otherFolder, Path.GetFileName(first)); File.WriteAllText(second, "another file");
        var selection = RadialSelection.FromPaths([first, second]);
        var zipped = await Service().ExecuteAsync(selection, new("ZIP", RadialOperation.Zip), new());
        using (var zip = ZipFile.OpenRead(zipped.Outputs[0]))
        { Assert.Equal(2, zip.Entries.Count); Assert.Equal(2, zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()); }
        var hash = await Service().ExecuteAsync(selection, new("hash", RadialOperation.Sha256), new());
        Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first))).ToLowerInvariant(), hash.ClipboardText);
        Assert.Contains(first, hash.ClipboardText); Assert.Contains(second, hash.ClipboardText);
        Assert.Equal(hash.ClipboardText, File.ReadAllText(hash.Outputs[0]));
        var paths = await Service().ExecuteAsync(selection, new("paths", RadialOperation.CopyPaths), new());
        Assert.Equal(string.Join(Environment.NewLine, selection.Paths), paths.ClipboardText);
    }

    [Fact]
    public async Task CancellationAndPartialFailureCleanStagingAndReportCompletedFiles()
    {
        string first = ImageFile(), broken = FileName("broken.png"); File.WriteAllText(broken, "invalid");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ExecuteAsync(RadialSelection.FromPaths([first]), new("WEBP", RadialOperation.Convert, "webp"), new(), ct: cancellation.Token));
        var error = await Assert.ThrowsAsync<RadialExecutionException>(() => Service().ExecuteAsync(RadialSelection.FromPaths([first, broken]), new("WEBP", RadialOperation.Convert, "webp"), new()));
        Assert.Single(error.CompletedOutputs); Assert.True(File.Exists(error.CompletedOutputs[0]));
        Assert.Empty(Directory.GetDirectories(_folder, ".cortex-wheel-*"));
    }

    [Fact]
    public void SettingsRoundTripValidationAndCorruptionRecovery()
    {
        string path = FileName("preferences.json");
        new RadialSettings { Quality = 71, ConvertKey = "Alt", ToolsKey = "Shift" }.Save(path);
        Assert.Equal(71, RadialSettings.Load(path).Quality);
        Assert.Equal("Alt", RadialSettings.Load(path).ConvertKey);
        Assert.Throws<ArgumentException>(() => new RadialSettings { ConvertKey = "Alt" }.Save(path));
        File.WriteAllText(path, "corrupt"); Assert.Equal(85, RadialSettings.Load(path).Quality);
    }

    [Fact]
    public void NativeWheelRendersAndUsesARealWpfDropTarget() => RunSta(() =>
    {
        using var window = new RadialWheelWindow(new(new FakeEngines().Router, new FakeConfig(".")), new());
        Assert.True(window.AllowDrop); Assert.True(window.Topmost); Assert.True(window.AllowsTransparency);
        Assert.False(window.ShowInTaskbar); Assert.Equal(WindowStyle.None, window.WindowStyle);
        var surface = Assert.IsType<RadialSurface>(window.Content);
        surface.Selection = RadialSelection.FromPaths([ImageFile()]);
        surface.SetActions(new RadialActionCatalog(new FakeEngines().Router, new FakeConfig(".")).GetActions(surface.Selection, RadialMode.Convert, new()));
        surface.Hover = 1;
        for (int index = 0; index < 20; index++) surface.Tick();
        surface.Measure(new(540, 540)); surface.Arrange(new Rect(0, 0, 540, 540)); surface.UpdateLayout();
        var image = new RenderTargetBitmap(540, 540, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string destination = Environment.GetEnvironmentVariable("CORTEXFX_RADIAL_ARTIFACTS") ?? _folder;
        Directory.CreateDirectory(destination);
        using (var output = File.Create(Path.Combine(destination, "cortex-fx-wheel.png"))) encoder.Save(output);
        surface.State = "working"; surface.Progress = 67; surface.Tick(); surface.UpdateLayout();
        image = new RenderTargetBitmap(540, 540, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
        encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(Path.Combine(destination, "cortex-fx-wheel-progress.png"))) encoder.Save(output);
        var settingsWindow = new RadialSettingsWindow(new(), "ffmpeg.exe");
        var settingsContent = Assert.IsAssignableFrom<FrameworkElement>(settingsWindow.Content);
        settingsContent.Measure(new(570, 730)); settingsContent.Arrange(new Rect(0, 0, 570, 730)); settingsContent.UpdateLayout();
        image = new RenderTargetBitmap(570, 730, 96, 96, PixelFormats.Pbgra32); image.Render(settingsContent);
        encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(Path.Combine(destination, "cortex-fx-radial-settings.png"))) encoder.Save(output);
        settingsWindow.Close();
    });

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20))); Assert.Null(error);
    }
}

public sealed class BundledFFmpegFactAttribute : FactAttribute
{
    public static string Executable => Environment.GetEnvironmentVariable("CORTEXFX_TEST_FFMPEG") ?? Path.Combine(AppContext.BaseDirectory, "Resources", "ffmpeg.exe");
    public BundledFFmpegFactAttribute() { if (!File.Exists(Executable)) Skip = "Requires the optional bundled FFmpeg; set CORTEXFX_TEST_FFMPEG to enable."; }
}

public class RadialMediaIntegrationTests
{
    [BundledFFmpegFact]
    public async Task ConvertsRealAudioAndVideoWithActualProgress()
    {
        string folder = Path.Combine(Path.GetTempPath(), "CortexFX_Media_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            using var processes = new ProcessManager();
            string ffmpeg = BundledFFmpegFactAttribute.Executable;
            string audio = Path.Combine(folder, "صوت sample.wav");
            string video = Path.Combine(folder, "فيديو sample.mp4");
            await processes.RunAsync(ffmpeg, $"-v error -f lavfi -i sine=frequency=440:duration=0.5 -y \"{audio}\"");
            await processes.RunAsync(ffmpeg, $"-v error -f lavfi -i testsrc=size=128x96:rate=12:duration=0.5 -i \"{audio}\" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest -y \"{video}\"");
            var config = new FakeConfig(Path.GetDirectoryName(ffmpeg)!);
            var service = new RadialConversionService(new FakeEngines().Router, config, processes, new MagickService(config, processes), new PdfRenderService());
            var settings = new RadialSettings { FFmpegPath = ffmpeg };
            foreach (string target in new[] { "mp3", "wav", "aac", "flac", "ogg", "m4a" })
            {
                var progress = new List<double>();
                var result = await service.ExecuteAsync(RadialSelection.FromPaths([audio]), new(target, RadialOperation.Convert, target), settings,
                    new InlineProgress<RadialProgress>(update => progress.Add(update.Percent)));
                Assert.True(new FileInfo(result.Outputs[0]).Length > 0);
                Assert.Contains(100d, progress); Assert.All(progress, value => Assert.InRange(value, -1, 100));
            }
            foreach (string target in new[] { "mp4", "mkv", "mov", "webm", "avi", "gif" })
            {
                var result = await service.ExecuteAsync(RadialSelection.FromPaths([video]), new(target, RadialOperation.Convert, target), settings);
                Assert.True(new FileInfo(result.Outputs[0]).Length > 0);
            }
            var extracted = await service.ExecuteAsync(RadialSelection.FromPaths([video]), new("MP3", RadialOperation.ExtractAudio, "mp3"), settings);
            Assert.True(new FileInfo(extracted.Outputs[0]).Length > 0);
            var compressed = await service.ExecuteAsync(RadialSelection.FromPaths([video]), new("compress", RadialOperation.CompressVideo, "mp4"), settings);
            Assert.True(new FileInfo(compressed.Outputs[0]).Length > 0);
        }
        finally { Directory.Delete(folder, true); }
    }
}
