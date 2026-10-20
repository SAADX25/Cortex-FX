using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CortexFX.Core.Radial;
using CortexFX.Core.Services.Documents;
using CortexFX.Core.Services.Media;
using CortexFX.Views.Radial;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace CortexFX.Tests;

public sealed class RadialToolTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CortexFX_Tools_" + Guid.NewGuid().ToString("N"));
    public RadialToolTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);
    private string Image(string name = "صورة.png")
    {
        string file = Path.Combine(_folder, name);
        using var image = new MagickImage(new MagickColor("#D9296080"), 120, 80); image.Write(file); return file;
    }
    private static RadialConversionService Service()
    {
        var config = new FakeConfig("."); var processes = new FakeProcesses();
        return new(new FakeEngines().Router, config, processes, new MagickService(config, processes), new PdfRenderService());
    }
    [Theory]
    [InlineData(1, 9900)] [InlineData(50, 5000)] [InlineData(75, 2500)] [InlineData(95, 500)]
    public void PercentageMeansReductionRatherThanRemainingSize(double percent, long expected) =>
        Assert.Equal(expected, new RadialCompressionOptions(ReductionPercent: percent).TargetBytes(10000));

    [Theory]
    [InlineData(0)] [InlineData(96)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidCompressionRequestsAreRejected(double percent) =>
        Assert.Throws<ArgumentException>(() => new RadialCompressionOptions(ReductionPercent: percent).TargetBytes(10000));

    [Fact]
    public void MaximumSizeUsesBytesAndRejectsTinyRequests()
    {
        Assert.Equal(2048, new RadialCompressionOptions(CompressionTargetMode.MaximumSize, MaximumBytes: 2048).TargetBytes(100000));
        Assert.Throws<ArgumentException>(() => new RadialCompressionOptions(MaximumBytes: 10).TargetBytes(100000));
    }
    [Theory]
    [InlineData(1)] [InlineData(1.777777777777778)] [InlineData(.5625)] [InlineData(1.3333333333333333)] [InlineData(.75)]
    public void CropHandlesKeepTheRatioAndStayInsideTheImage(double ratio)
    {
        var random = new Random(71);
        foreach (var handle in Enum.GetValues<CropHandle>())
            for (int index = 0; index < 100; index++)
            {
                var start = CropGeometry.Fit(800, 600, ratio, new(100, 90, 500, 400));
                var result = CropGeometry.Resize(start, handle, random.Next(-1500, 1500), random.Next(-1500, 1500), 800, 600, ratio);
                Assert.InRange(result.X, 0, 800); Assert.InRange(result.Y, 0, 600);
                Assert.True(result.Right <= 800.000001 && result.Bottom <= 600.000001);
                if (result.Width > 1.1 && result.Height > 1.1) Assert.InRange(Math.Abs(result.Width / result.Height - ratio), 0, .000001);
                var pixels = CropGeometry.ToPixels(result, 800, 600); pixels.Validate(800, 600);
            }
    }
    [Fact]
    public void CropsRejectChangedImagesAndOutOfBoundsRectangles()
    {
        Assert.Throws<ArgumentException>(() => new RadialCropOptions(0, 0, 10, 10, 100, 80).Validate(80, 100));
        Assert.Throws<ArgumentException>(() => new RadialCropOptions(99, 0, int.MaxValue, 10, 100, 80).Validate(100, 80));
        Assert.Throws<ArgumentException>(() => CropGeometry.Fit(100, 80, double.NaN));
    }
    [Fact]
    public async Task CropPreservesOriginalAlphaAndAvoidsNameCollisions()
    {
        string source = Image(); byte[] before = SHA256.HashData(File.ReadAllBytes(source));
        var selection = RadialSelection.FromPaths([source]);
        var action = new RadialAction("Crop", RadialOperation.CropImage) { Crop = new(10, 5, 50, 30, 120, 80) };
        var first = await Service().ExecuteAsync(selection, action, new());
        var second = await Service().ExecuteAsync(selection, action, new());
        Assert.NotEqual(first.Outputs[0], second.Outputs[0]);
        using var image = new MagickImage(first.Outputs[0]);
        Assert.Equal(50u, image.Width); Assert.Equal(30u, image.Height); Assert.True(image.HasAlpha);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.Empty(Directory.GetDirectories(_folder, ".cortex-wheel-*"));
    }
    [Fact]
    public void PreviewAndCropUseExifOrientedDimensions()
    {
        string source = Path.Combine(_folder, "rotated.jpg");
        using (var image = new MagickImage(MagickColors.SteelBlue, 120, 80))
        {
            var profile = new ExifProfile(); profile.SetValue(ExifTag.Orientation, (ushort)6); image.SetProfile(profile); image.Orientation = OrientationType.RightTop; image.Write(source);
        }
        var preview = RadialImageTools.LoadPreview(source);
        Assert.Equal(80, preview.Width); Assert.Equal(120, preview.Height);
        string output = Path.Combine(_folder, "cropped.jpg");
        RadialImageTools.Crop(source, output, new(0, 0, 40, 60, 80, 120), default);
        using var crop = new MagickImage(output); Assert.Equal(40u, crop.Width); Assert.Equal(60u, crop.Height);
    }
    [Fact]
    public void AnimatedInputsAreNotSilentlyFlattenedByCrop()
    {
        string source = Path.Combine(_folder, "animated.gif");
        using (var frames = new MagickImageCollection()) { frames.Add(new MagickImage(MagickColors.Red, 30, 20)); frames.Add(new MagickImage(MagickColors.Blue, 30, 20)); frames.Write(source); }
        Assert.Throws<NotSupportedException>(() => RadialImageTools.LoadPreview(source));
    }
    [Fact]
    public async Task TargetImageCompressionMeetsTheLimitAndKeepsDimensions()
    {
        string source = Path.Combine(_folder, "noisy.jpg");
        using (var image = new MagickImage(MagickColors.Gray, 640, 480)) { image.AddNoise(NoiseType.Random); image.Quality = 100; image.Write(source); }
        byte[] original = File.ReadAllBytes(source);
        var options = new RadialCompressionOptions(ReductionPercent: 50);
        var result = await Service().ExecuteAsync(RadialSelection.FromPaths([source]), new("Compress", RadialOperation.CompressImage) { Compression = options }, new());
        Assert.True(new FileInfo(result.Outputs[0]).Length <= options.TargetBytes(original.Length));
        using var output = new MagickImage(result.Outputs[0]); Assert.Equal(640u, output.Width); Assert.Equal(480u, output.Height);
        Assert.Equal(original, File.ReadAllBytes(source));
    }
    [Fact]
    public async Task UnreachableTargetLeavesNoMisleadingOutputOrTemporaryFolder()
    {
        string source = Image();
        var error = await Assert.ThrowsAsync<RadialExecutionException>(() => Service().ExecuteAsync(RadialSelection.FromPaths([source]),
            new("Compress", RadialOperation.CompressImage) { Compression = new(ReductionPercent: 95) }, new()));
        Assert.Empty(error.CompletedOutputs); Assert.Single(Directory.GetFiles(_folder)); Assert.Empty(Directory.GetDirectories(_folder));
    }
    [Fact]
    public async Task PdfTargetRequiresConsentBeforeFlatteningAndPreservesPageSizes()
    {
        string source = Path.Combine(_folder, "scan.pdf");
        using (var image = new MagickImage(MagickColors.Gray, 1100, 1100))
        {
            image.AddNoise(NoiseType.Random);
            using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
            using var picture = XImage.FromStream(stream); using var document = new PdfDocument();
            for (int i = 0; i < 2; i++) { var page = document.AddPage(); page.Width = XUnit.FromPoint(400); page.Height = XUnit.FromPoint(400); using var drawing = XGraphics.FromPdfPage(page); drawing.DrawImage(picture, 0, 0, 400, 400); }
            document.Save(source);
        }
        byte[] before = SHA256.HashData(File.ReadAllBytes(source));
        var selection = RadialSelection.FromPaths([source]);
        var options = new RadialCompressionOptions(ReductionPercent: 75);
        var failure = await Assert.ThrowsAsync<RadialExecutionException>(() => Service().ExecuteAsync(selection, new("Compress", RadialOperation.CompressPdf) { Compression = options }, new()));
        Assert.Contains("preserving", failure.Message);
        var result = await Service().ExecuteAsync(selection, new("Compress", RadialOperation.CompressPdf) { Compression = options with { RasterizePdf = true } }, new());
        Assert.True(new FileInfo(result.Outputs[0]).Length <= options.TargetBytes(selection.Bytes));
        using var pdf = PdfReader.Open(result.Outputs[0], PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount); Assert.Equal(400, pdf.Pages[0].Width.Point, 1);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }
    [Fact]
    public void CatalogOffersCropForOneImageAndExplainsTheBatchRestriction()
    {
        var catalog = new RadialActionCatalog(new FakeEngines().Router, new FakeConfig("."));
        Assert.Contains(catalog.GetActions(RadialSelection.FromPaths([Image()]), RadialMode.Tools, new()), a => a.Operation == RadialOperation.CropImage && a.Enabled);
        Assert.Contains(catalog.GetActions(RadialSelection.FromPaths([Image(), Image("second.png")]), RadialMode.Tools, new()), a => a.Operation == RadialOperation.CropImage && !a.Enabled);
    }
    [Fact]
    public void IconWheelKeepsAccessibleActionNames() => RunSta(() =>
    {
        var surface = new RadialSurface(); surface.SetActions([new("Crop image", RadialOperation.CropImage), new("Compress", RadialOperation.CompressImage)]);
        var peer = UIElementAutomationPeer.CreatePeerForElement(surface)!;
        Assert.Equal(new[] { "Crop image", "Compress" }, peer.GetChildren().Select(p => p.GetName()));
        surface.Hover = 0; Assert.Contains("Crop image", surface.ToolTip!.ToString());
    });
    private static void RunSta(Action action)
    {
        Exception? error = null; var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
