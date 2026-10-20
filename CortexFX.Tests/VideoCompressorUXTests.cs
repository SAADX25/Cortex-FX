using System.Windows.Controls;
using CortexFX.Views.Video;
using Xunit;

namespace CortexFX.Tests;

public sealed class VideoCompressorUXTests
{
    [Theory]
    [InlineData("PresetSocial", 28, "Res720", "720p")]
    [InlineData("PresetEmail", 32, "Res480", "480p")]
    [InlineData("PresetBalanced", 24, "Res1080", "1080p")]
    [InlineData("PresetHighQ", 20, "ResOriginal", "Original resolution")]
    public void PresetsApplyTogetherWithoutBecomingCustom(string preset, int crf, string resolution, string summary) => RunSta(() =>
    {
        var view = new VideoCompressorView();
        ((RadioButton)view.FindName(preset)).IsChecked = true;
        Assert.True(((RadioButton)view.FindName(preset)).IsChecked == true);
        Assert.False(((RadioButton)view.FindName("PresetCustom")).IsChecked == true);
        Assert.Equal(crf, ((Slider)view.FindName("CrfSlider")).Value);
        Assert.True(((RadioButton)view.FindName(resolution)).IsChecked == true);
        Assert.Contains(summary, ((TextBlock)view.FindName("OutputSummaryText")).Text);
    });

    [Fact]
    public void AdjustingQualitySelectsCustomAndKeepsTheOtherChoices() => RunSta(() =>
    {
        var view = new VideoCompressorView();
        ((RadioButton)view.FindName("PresetBalanced")).IsChecked = true;
        var slider = (Slider)view.FindName("CrfSlider");
        Assert.True(slider.IsEnabled);
        slider.Value = 27;
        Assert.True(((RadioButton)view.FindName("PresetCustom")).IsChecked == true);
        Assert.True(((RadioButton)view.FindName("Res1080")).IsChecked == true);
        Assert.True(((RadioButton)view.FindName("CodecH264")).IsChecked == true);
        Assert.Contains("Custom", ((TextBlock)view.FindName("OutputSummaryHint")).Text);
        Assert.Contains("27", ((TextBlock)view.FindName("OutputSummaryHint")).Text);
    });

    [Fact]
    public void ManualCodecAndResolutionAreReflectedInTheSummary() => RunSta(() =>
    {
        var view = new VideoCompressorView();
        ((RadioButton)view.FindName("CodecH265")).IsChecked = true;
        ((RadioButton)view.FindName("Res480")).IsChecked = true;
        Assert.True(((RadioButton)view.FindName("PresetCustom")).IsChecked == true);
        Assert.Equal("MP4 · H.265 · 480p", ((TextBlock)view.FindName("OutputSummaryText")).Text);
        Assert.Equal(28, ((Slider)view.FindName("CrfSlider")).Value);
    });

    [Fact]
    public void GoingBackKeepsTheCompressionPreferences() => RunSta(() =>
    {
        var view = new VideoCompressorView();
        ((RadioButton)view.FindName("PresetHighQ")).IsChecked = true;
        int closed = 0; view.CloseRequested += (_, _) => closed++;
        ((Button)view.FindName("BackButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, closed);
        Assert.True(((RadioButton)view.FindName("PresetHighQ")).IsChecked == true);
        Assert.Equal(20, ((Slider)view.FindName("CrfSlider")).Value);
    });

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF interaction did not finish.");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
