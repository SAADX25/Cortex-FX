using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CortexFX.Core.Interfaces;
using CortexFX.Dialogs;
using CortexFX.ViewModels;
using Xunit;

namespace CortexFX.Tests;

public sealed class ResourceStatusTests
{
    private static ResourceValidationResult Core(bool exists = true, string[]? missing = null, string[]? libraries = null) =>
        new(AppContext.BaseDirectory, exists, missing ?? [], Path.Combine(AppContext.BaseDirectory, "Resources", "ffmpeg_libs"), libraries ?? []);

    [Fact]
    public void MissingOptionalToolsDoNotMakeIncludedToolsAppearBroken()
    {
        var report = ResourceStatusReport.Create(Core(), false, false, false);
        Assert.Equal("Core tools are ready", report.Heading);
        Assert.Equal("4 of 4 ready", report.Count);
        Assert.All(report.CoreTools, tool => Assert.Equal(ResourceToolState.Ready, tool.State));
        Assert.All(report.OptionalTools, tool => Assert.Equal(ResourceToolState.Optional, tool.State));
        Assert.DoesNotContain(report.Core.ResourcesDirectory, report.Description);
        Assert.Contains(report.Core.ResourcesDirectory, report.Details);
    }

    [Fact]
    public void MissingFolderMarksAllIncludedToolsUnavailable()
    {
        var report = ResourceStatusReport.Create(Core(exists: false), true, true, false);
        Assert.Equal("0 of 4 ready", report.Count);
        Assert.All(report.CoreTools, tool => Assert.Equal(ResourceToolState.Missing, tool.State));
        Assert.Equal("Some tools need attention", report.Heading);
    }

    [Fact]
    public void MissingSpecificFilesOnlyAffectTheirFeatures()
    {
        var report = ResourceStatusReport.Create(Core(missing: ["MAGICK.EXE"], libraries: ["avcodec-58.dll"]), false, true, true);
        Assert.Equal("2 of 4 ready", report.Count);
        Assert.Equal(ResourceToolState.Ready, report.CoreTools.Single(t => t.Name == "Video & audio").State);
        Assert.Equal(ResourceToolState.Missing, report.CoreTools.Single(t => t.Name == "Image tools").State);
        Assert.Contains("MAGICK.EXE", report.Details);
        Assert.Contains("avcodec-58.dll", report.Details);
    }

    [Fact]
    public void DialogShowsFriendlyStatusAndCanRecheckWithoutOpeningTechnicalDetails()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var report = ResourceStatusReport.Create(Core(), true, false, false);
                var window = new ResourceStatusDialog(() => report);
                Render(window, "tool-status-ready.png");
                Assert.False(window.TechnicalDetails.IsExpanded);
                Assert.False(window.AdditionalFeatures.IsExpanded);
                Assert.Equal(4, window.CoreToolList.Items.Count);
                Assert.Equal(3, window.OptionalToolList.Items.Count);
                report = ResourceStatusReport.Create(Core(missing: ["magick.exe"]), true, false, false);
                window.RefreshStatus();
                Assert.Equal("3 of 4 ready", window.Report.Count);
                Assert.False(window.TechnicalDetails.IsExpanded);
                Render(window, "tool-status-missing.png");
                window.AdditionalFeatures.IsExpanded = true;
                Render(window, "tool-status-additional.png");
                window.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(error);
    }

    private static void Render(ResourceStatusDialog dialog, string name)
    {
        var view = dialog.DialogSurface;
        view.Measure(new Size(650, 640));
        view.Arrange(new Rect(0, 0, 650, 640));
        view.UpdateLayout();
        dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        string? directory = Environment.GetEnvironmentVariable("CORTEXFX_TEST_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var bitmap = new RenderTargetBitmap(650, 640, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }
}
