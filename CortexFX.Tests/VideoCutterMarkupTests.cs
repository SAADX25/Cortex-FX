using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using CortexFX.Views.Video;
using Xunit;

namespace CortexFX.Tests;

public class VideoCutterMarkupTests
{
    [Fact]
    public void VideoCutterViewLoadsOnStaThread()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new VideoCutterView();
                var focusButton = Assert.IsType<Button>(view.FindName("PreviewFocusButton"));
                var sidePanel = Assert.IsType<Grid>(view.FindName("CutterSidePanel"));
                var timeline = Assert.IsType<Border>(view.FindName("CutterTimelinePanel"));

                focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, sidePanel.Visibility);
                Assert.Equal(Visibility.Collapsed, timeline.Visibility);

                focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, sidePanel.Visibility);
                Assert.Equal(Visibility.Visible, timeline.Visibility);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Video cutter view initialization timed out.");
        Assert.Null(error);
    }

    [Fact]
    public void VideoCutterSpacingValuesParseAtRuntime()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Views", "Video", "VideoCutterView.xaml"));
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
}
