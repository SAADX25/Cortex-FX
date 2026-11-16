using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CortexFX.Views.Audio;
using Xunit;

namespace CortexFX.Tests;

public sealed class AudioToolViewTests
{
    [Fact]
    public void CutterAndSeparatorExposeTheirOwnControls()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new AudioCutterView();
                Assert.Equal("Audio Cutter", view.AudioToolHeading.Text);
                Assert.Null(view.FindName("SeparationPanel"));
                Assert.Equal(Visibility.Visible, view.SaveSelectionBtn.Visibility);
                Render(view, "audio-cutter.png");

                var separator = new AudioSeparatorView();
                Assert.Equal("Download AI engine", separator.SeparateAudioBtn.Content);
                Assert.False(separator.AdvancedOptions.IsExpanded);
                Assert.Null(separator.FindName("DownloadGpuBtn"));
                Assert.Null(separator.FindName("SetStartBtn"));
                Assert.False(separator.PreviewSourceBtn.IsEnabled);
                Assert.False(separator.OpenVocalsBtn.IsEnabled);
                Render(separator, "audio-separator-empty.png");
                separator.SourceFileName.Text = "Midnight session.wav";
                separator.SourceDetails.Text = "03:28.848 · WAV · Full song";
                separator.SetWaveform(Enumerable.Range(0, 100).Select(i => 0.2 + 0.75 * Math.Abs(Math.Sin(i * 1.7))).ToArray());
                separator.PreviewSourceBtn.IsEnabled = true;
                separator.SourceSeekSlider.IsEnabled = true;
                separator.SetPlaybackPosition(TimeSpan.FromSeconds(36), TimeSpan.FromSeconds(208.848));
                separator.SetPreviewVolume(65, false);
                Render(separator, "audio-separator.png");
                separator.Measure(new Size(960, 900));
                separator.Arrange(new Rect(0, 0, 960, 900));
                separator.UpdateLayout();
                Assert.Equal(876, separator.Workspace.ActualHeight);
                Assert.True(separator.SourceWaveform.ActualHeight > 200);

                separator.UpdateDownloadProgress(new("Download", 5 * 1048576, 10 * 1048576));
                Assert.False(separator.AiDownloadProgress.IsIndeterminate);
                Assert.Equal(50, separator.AiDownloadProgress.Value);
                Assert.Contains("50%", separator.AiDownloadProgressText.Text);
                separator.AiDownloadProgressPanel.Visibility = Visibility.Visible;
                separator.CancelAiDownloadBtn.Visibility = Visibility.Visible;
                separator.SeparateAudioBtn.Content = "Downloading AI engine…";
                Render(separator, "audio-separator-downloading.png");
                separator.UpdateDownloadProgress(new("Verifying AI files…"));
                Assert.True(separator.AiDownloadProgress.IsIndeterminate);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Audio view verification timed out.");
        Assert.Null(error);
    }

    [Fact]
    public void PlayheadFollowsPlaybackWithoutSeekingAndSupportsUserSeeking()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new AudioSeparatorView();
                var seeks = new List<TimeSpan>();
                view.SeekRequested += seeks.Add;
                view.Measure(new Size(960, 600));
                view.Arrange(new Rect(0, 0, 960, 600));
                view.UpdateLayout();
                view.SetPlaybackPosition(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120));
                Assert.Empty(seeks);
                Assert.Equal(30, view.SourceSeekSlider.Value);
                Assert.Equal("00:30.000", view.SourcePosition.Text);
                Assert.Equal(view.SourceWaveform.ActualWidth * 0.25, view.SourcePlayedBand.Width, 2);
                view.SourceSeekSlider.Value = 45;
                Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(seeks));
                view.SourceSeekSlider.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
                view.SourceSeekSlider.Value = 80;
                view.SetPlaybackPosition(TimeSpan.FromSeconds(46), TimeSpan.FromSeconds(120));
                Assert.Equal(80, view.SourceSeekSlider.Value);
                Assert.Equal("01:20.000", view.SourcePosition.Text);
                Assert.Single(seeks);
                view.SourceSeekSlider.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
                Assert.Equal(TimeSpan.FromSeconds(80), seeks.Last());
                view.SetPlaybackPosition(TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(120));
                Assert.Equal(120, view.SourceSeekSlider.Value);
                view.SetWaveform([]);
                Assert.Equal(Visibility.Collapsed, view.SourceSeekSlider.Visibility);
                Assert.Equal(0, view.SourcePlayedBand.Width);

                var volumes = new List<double>();
                int muteRequests = 0;
                view.VolumeRequested += volumes.Add;
                view.ToggleMuteRequested += () => muteRequests++;
                view.SetPreviewVolume(35, false);
                Assert.Empty(volumes);
                Assert.Equal("35%", view.PreviewVolumeText.Text);
                view.PreviewVolume.Value = 70;
                Assert.Equal(70, Assert.Single(volumes));
                view.SetPreviewVolume(70, true);
                Assert.Equal("Muted", view.PreviewVolumeText.Text);
                Assert.Equal("\uE74F", view.PreviewVolumeIcon.Text);
                view.PreviewMuteBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(1, muteRequests);
                view.SetPreviewVolume(0, false);
                Assert.Equal("0%", view.PreviewVolumeText.Text);
                Assert.Equal("\uE74F", view.PreviewVolumeIcon.Text);
                Assert.Single(volumes);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(error);
    }

    private static void Render(System.Windows.FrameworkElement view, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("CORTEXFX_TEST_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        view.Measure(new Size(960, 600));
        view.Arrange(new Rect(0, 0, 960, 600));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(960, 600, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }
}
