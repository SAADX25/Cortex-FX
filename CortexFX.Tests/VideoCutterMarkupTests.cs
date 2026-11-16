using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using CortexFX.Views.Video;
using Xunit;

namespace CortexFX.Tests;

public class VideoCutterMarkupTests
{
    [Fact]
    public void VideoCutterViewLoadsOnStaThread() => RunSta(() =>
    {
        var view = new VideoCutterView();
        var focusButton = Assert.IsType<Button>(view.FindName("PreviewFocusButton"));
        var sidePanel = Assert.IsType<Border>(view.FindName("CutterSidePanel"));
        var timeline = Assert.IsType<Grid>(view.FindName("CutterTimelinePanel"));

        Assert.IsType<ComboBox>(view.FindName("FormatCombo"));
        Assert.IsType<ComboBox>(view.FindName("ResolutionCombo"));
        Assert.IsType<ComboBox>(view.FindName("AspectFitCombo"));
        Assert.IsType<RadioButton>(view.FindName("ModeSmartRender"));
        Assert.IsType<RadioButton>(view.FindName("ModeFastCopy"));
        Assert.IsType<ItemsControl>(view.FindName("ClipsList"));
        Assert.IsType<Grid>(view.FindName("TimelineSurface"));
        Assert.IsType<Canvas>(view.FindName("TimelineAudioCanvas"));
        var selectTool = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(view.FindName("SelectToolBtn"));
        var razorTool = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(view.FindName("RazorToolBtn"));
        Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(view.FindName("TrackMuteButton"));
        var undoBtn = Assert.IsType<Button>(view.FindName("UndoButton"));
        var redoBtn = Assert.IsType<Button>(view.FindName("RedoButton"));
        Assert.IsType<Button>(view.FindName("WatchOutputButton"));
        Assert.IsType<Button>(view.FindName("MonitorExpandButton"));
        Assert.IsType<Slider>(view.FindName("ProgramScrubSlider"));
        Assert.IsType<TextBlock>(view.FindName("VolumePercentText"));
        Assert.IsType<TextBlock>(view.FindName("SelectedClipDurationText"));
        Assert.Null(view.FindName("InPointText"));
        Assert.Null(view.FindName("OutPointText"));
        var razorHint = Assert.IsType<Border>(view.FindName("RazorModeHint"));

        Assert.False(undoBtn.IsEnabled);
        Assert.False(redoBtn.IsEnabled);
        Assert.Equal(Visibility.Collapsed, razorHint.Visibility);

        razorTool.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Visible, razorHint.Visibility);
        selectTool.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Collapsed, razorHint.Visibility);

        focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Collapsed, sidePanel.Visibility);
        Assert.Equal(Visibility.Collapsed, timeline.Visibility);

        focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Visible, sidePanel.Visibility);
        Assert.Equal(Visibility.Visible, timeline.Visibility);
    });

    [Fact]
    public void VideoCutterBackButtonTriggersCloseRequestedAndSummaryUpdates() => RunSta(() =>
    {
        var view = new VideoCutterView();
        int closed = 0;
        view.CloseRequested += (_, _) => closed++;

        var back = Assert.IsType<Button>(view.FindName("BackButton"));
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, closed);

        var formatCombo = Assert.IsType<ComboBox>(view.FindName("FormatCombo"));
        var resCombo = Assert.IsType<ComboBox>(view.FindName("ResolutionCombo"));
        var hint = Assert.IsType<TextBlock>(view.FindName("ExportSummaryHint"));

        formatCombo.SelectedIndex = 3; // MKV
        resCombo.SelectedIndex = 6;    // 9:16 Vertical
        Assert.Contains("MKV", hint.Text);
        Assert.Contains("9:16", hint.Text);
    });

    [Fact]
    public void MainWindowMarkupIncludesMaximizeCaptionButton()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "MainWindow.xaml"));
        Assert.True(File.Exists(path));
        var document = XDocument.Load(path);
        bool hasMaximizeButton = document.Descendants()
            .Any(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "MaximizeButton"));
        Assert.True(hasMaximizeButton, "MainWindow.xaml must define MaximizeButton on the top caption bar.");
    }

    [Theory]
    [InlineData("VideoCutterView.xaml")]
    [InlineData("CutterTheme.xaml")]
    public void VideoCutterSpacingValuesParseAtRuntime(string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Views", "Video", fileName));
        Assert.True(File.Exists(path), $"XAML file not found: {path}");

        var converter = new ThicknessConverter();
        var document = XDocument.Load(path);
        foreach (var attribute in document.Descendants().Attributes())
        {
            if (attribute.Name.LocalName is not ("Margin" or "Padding" or "BorderThickness"))
                continue;
            if (attribute.Value.StartsWith('{'))
                continue;

            Exception? error = Record.Exception(
                () => converter.ConvertFromInvariantString(attribute.Value));
            Assert.Null(error);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF STA test timed out.");
        if (error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
