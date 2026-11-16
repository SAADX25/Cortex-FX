using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CortexFX.Core.Radial;
using CortexFX.Views.Radial;
using Xunit;

namespace CortexFX.Tests;

public sealed class RadialSettingsWindowTests
{
    [Fact]
    public void SettingsPreserveCustomValuesAndRejectConflictingKeys()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "CortexFX-shortcuts-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                string engine = Path.Combine(folder, "ffmpeg.exe");
                File.WriteAllText(engine, "test engine");
                var settings = new RadialSettings { ConvertKey = "Ctrl", QuickKey = "Shift", PdfDpi = 240, Quality = 93, FFmpegPath = engine };
                var window = new RadialSettingsWindow(settings, engine);
                Render(window, "file-shortcuts.png", 740);
                Assert.False(window.AdvancedOptions.IsExpanded);
                Assert.Equal(240, window.DpiBox.SelectedValue);
                Assert.Equal("93%", window.QualityValue.Text);
                bool notified = false;
                window.SettingsSaved += () => notified = true;
                string path = Path.Combine(folder, "settings.json");
                window.ToolsKeyBox.SelectedItem = "Ctrl";
                Assert.False(window.SaveSettings(path));
                Assert.False(File.Exists(path));
                Assert.False(notified);
                Assert.Equal("Alt", settings.ToolsKey);
                window.ToolsKeyBox.SelectedItem = "Alt";
                window.NotificationsToggle.IsChecked = false;
                Assert.True(window.SaveSettings(path));
                Assert.True(notified);
                var saved = RadialSettings.Load(path);
                Assert.Equal(240, saved.PdfDpi);
                Assert.Equal(93, saved.Quality);
                Assert.Equal(engine, saved.FFmpegPath);
                Assert.False(saved.Notifications);
                RadialMode? requested = null;
                window.PreviewRequested += mode => requested = mode;
                window.EnabledToggle.IsChecked = false;
                for (int index = 0; index < 3; index++)
                {
                    window.ModeBox.SelectedIndex = index;
                    window.ChooseFilesButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert.Equal((RadialMode)index, requested);
                }
                window.AdvancedOptions.IsExpanded = true;
                Render(window, "file-shortcuts-advanced.png", 740);
                window.AdvancedOptions.IsExpanded = false;
                Render(window, "file-shortcuts-compact.png", 520);
                Assert.True(window.SettingsScroll.ScrollableHeight > 0);
                Assert.True(window.SaveButton.TranslatePoint(new Point(0, 0), window.DialogSurface).Y + window.SaveButton.ActualHeight <= 520);
                window.Close();
                var defaults = new RadialSettingsWindow(new RadialSettings(), engine);
                Render(defaults, "file-shortcuts.png", 740);
                Assert.Equal(0, defaults.SettingsScroll.ScrollableHeight);
                Assert.Equal("85%", defaults.QualityValue.Text);
                Assert.Equal(200, defaults.DpiBox.SelectedValue);
                defaults.Close();
            }
            catch (Exception ex) { error = ex; }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Render(RadialSettingsWindow dialog, string name, int height)
    {
        var view = dialog.DialogSurface;
        view.Measure(new Size(820, height));
        view.Arrange(new Rect(0, 0, 820, height));
        view.UpdateLayout();
        dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        string? folder = Environment.GetEnvironmentVariable("CORTEXFX_TEST_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var bitmap = new RenderTargetBitmap(820, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var output = File.Create(Path.Combine(folder, name));
        encoder.Save(output);
    }
}
