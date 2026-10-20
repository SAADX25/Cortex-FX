using System.Reflection;
using System.Windows;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Radial;
using CortexFX.Core.Services.Documents;
using CortexFX.Models;
using CortexFX.Views.Radial;
using Xunit;

namespace CortexFX.Tests;

public sealed class DocumentConversionTests
{
    [Theory]
    [InlineData(".docx", "txt", "word-txt")]
    [InlineData(".docx", "rtf", "word-rtf")]
    [InlineData(".docx", "odt", "word-odt")]
    [InlineData(".doc", "docx", "word-docx")]
    [InlineData(".odt", "docx", "word-docx")]
    [InlineData(".rtf", "txt", "word-txt")]
    [InlineData(".txt", "docx", "word-docx")]
    [InlineData(".odt", "pdf", "office-pdf")]
    [InlineData(".rtf", "pdf", "office-pdf")]
    [InlineData(".txt", "pdf", "office-pdf")]
    public async Task InstalledWordHandlesDocumentExportsWithoutLibreOffice(string ext, string target, string engine)
    {
        var fake = new FakeEngines { OptionalSupported = true, OptionalInstalled = false };
        Assert.Null(fake.Router.GetUnavailableReason(ext.ToUpperInvariant(), target.ToUpperInvariant()));
        var result = await fake.Router.ConvertAsync(Job(ext, target));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(engine, fake.Called);
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("pdf")]
    public async Task LibreOfficeIsUsedWhenWordIsMissing(string target)
    {
        var fake = new FakeEngines { WordInstalled = false, OptionalSupported = true, OptionalInstalled = true };
        Assert.Null(fake.Router.GetUnavailableReason(".docx", target));
        Assert.True((await fake.Router.ConvertAsync(Job(".docx", target))).Success);
        Assert.Equal("optional", fake.Called);
    }

    [Theory]
    [InlineData(".docx", "txt", "Word")]
    [InlineData(".docx", "pdf", "Word")]
    [InlineData(".pdf", "docx", "Word")]
    [InlineData(".xlsx", "pdf", "Excel")]
    [InlineData(".pptx", "pdf", "PowerPoint")]
    public async Task MissingDocumentEnginesFailBeforeStartingWork(string ext, string target, string requirement)
    {
        var fake = new FakeEngines { WordInstalled = false, ExcelInstalled = false, PowerPointInstalled = false,
            OptionalSupported = true, OptionalInstalled = false };
        var result = await fake.Router.ConvertAsync(Job(ext, target));
        Assert.False(result.Success);
        Assert.Contains(requirement, result.ErrorMessage);
        Assert.Null(fake.Called);
    }

    [Theory]
    [InlineData(".pdf", "pptx")]
    [InlineData(".docx", "pptx")]
    [InlineData(".pptx", "docx")]
    public void WordInstallationDoesNotImplyPowerPointInstallation(string ext, string target)
    {
        var fake = new FakeEngines { PowerPointInstalled = false };
        Assert.Contains("PowerPoint", fake.Router.GetUnavailableReason(ext, target));
        Assert.Null(fake.Router.GetUnavailableReason(".pdf", "docx"));
    }

    [Fact]
    public void MissingCalibreDisablesEbooksWhilePdfImagesRemainAvailable()
    {
        var fake = new FakeEngines { WordInstalled = false };
        var catalog = new RadialActionCatalog(new EbookRouter(fake.Router), new FakeConfig("."));
        var actions = catalog.GetActions(new(["paper.pdf"], 1048576, "Document"), RadialMode.Convert, new());
        Assert.All(actions.Where(a => a.Target is "epub" or "mobi" or "azw3"), a =>
        { Assert.False(a.Enabled); Assert.Contains("Calibre", a.UnavailableReason); });
        Assert.All(actions.Where(a => a.Target is "png" or "jpg" or "webp" or "tiff"), a => Assert.True(a.Enabled));
        Assert.False(actions.Single(a => a.Target == "docx").Enabled);
    }

    [Fact]
    public void BundledOptionalToolsAreRecognizedWithoutLaunchingThem()
    {
        string folder = Path.Combine(Path.GetTempPath(), "CortexFX_EngineDetection_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Calibre"));
            Directory.CreateDirectory(Path.Combine(folder, "LibreOffice", "program"));
            File.WriteAllText(Path.Combine(folder, "Calibre", "ebook-convert.exe"), "test placeholder - never executed");
            File.WriteAllText(Path.Combine(folder, "LibreOffice", "program", "soffice.exe"), "test placeholder - never executed");
            File.WriteAllText(Path.Combine(folder, "7z.exe"), "test placeholder - never executed");
            var service = new OptionalConversionService(new FakeConfig(folder), new FakeProcesses());
            Assert.Null(service.GetUnavailableReason("PDF", "EPUB"));
            Assert.Null(service.GetUnavailableReason(".docx", "txt"));
            Assert.Null(service.GetUnavailableReason(".rar", "zip"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void WheelShowsOnlyReadyFormatsAndInvokesTheDisplayedChoice()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                string folder = Path.Combine(Path.GetTempPath(), "CortexFX_WheelTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    string input = Path.Combine(folder, "paper.pdf"); File.WriteAllText(input, "fixture");
                    using var window = new RadialWheelWindow(new(new EbookRouter(new FakeEngines().Router), new FakeConfig(".")), new());
                    typeof(RadialWheelWindow).GetMethod("LoadPaths", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new[] { input }]);
                    var surface = (RadialSurface)window.Content;
                    int calls = 0; window.ActionInvoked += (_, _) => calls++;
                    Assert.Equal(new[] { "docx", "pptx", "jpg", "png", "webp", "tiff" }, surface.Actions.Select(a => a.Target));
                    Assert.All(surface.Actions, action => Assert.True(action.Enabled));
                    Assert.Equal("idle", surface.State); Assert.False(window.Busy); Assert.Equal(0, calls);
                    for (int index = 0; index < surface.Actions.Count; index++)
                    {
                        double angle = (-90 + index * 360d / surface.Actions.Count) * Math.PI / 180;
                        int hit = (int)typeof(RadialWheelWindow).GetMethod("Hit", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(window, [new Point(270 + Math.Cos(angle) * 167, 270 + Math.Sin(angle) * 167)])!;
                        Assert.Equal(index, hit);
                    }
                    string? invokedTarget = null;
                    window.ActionInvoked += (action, _) => invokedTarget = action.Target;
                    typeof(RadialWheelWindow).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [0]);
                    Assert.Equal("docx", invokedTarget);
                    Assert.True(window.Busy); Assert.Equal("working", surface.State); Assert.Equal(1, calls);
                    Assert.Equal("DOCX", surface.ActiveActionLabel);
                    window.Fail("Conversion test failure");
                    Assert.Equal("error", surface.State);
                    Assert.Equal("DOCX", surface.ActiveActionLabel);
                }
                finally { Directory.Delete(folder, true); }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static ConversionJob Job(string extension, string target) => new()
    { InputPath = Path.Combine(Path.GetTempPath(), "document" + extension), TargetFormat = target, OutputDirectory = Path.GetTempPath(), CreateSubfolder = false };

    private sealed class EbookRouter(IConversionRouter inner) : IConversionRouter
    {
        public IReadOnlyList<string> GetSupportedFormats(string extension) => inner.GetSupportedFormats(extension);
        public string? GetUnavailableReason(string extension, string target) => target is "epub" or "mobi" or "azw3" ?
            "Calibre is required for e-book conversion." : inner.GetUnavailableReason(extension, target);
        public Task<ConversionResult> ConvertAsync(ConversionJob job, CancellationToken ct = default, IProgress<double>? p = null) => inner.ConvertAsync(job, ct, p);
    }
}
