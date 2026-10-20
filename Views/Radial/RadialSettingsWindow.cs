using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CortexFX.Core.Radial;
using Microsoft.Win32;

namespace CortexFX.Views.Radial;

public sealed class RadialSettingsWindow : Window
{
    private readonly RadialSettings _settings;
    private readonly CheckBox _enabled = new() { Content = "Enable global drag gestures" };
    private readonly CheckBox _notifications = new() { Content = "Show completion and error notifications" };
    private readonly Slider _quality = new() { Minimum = 10, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly ComboBox _dpi = new();
    private readonly ComboBox[] _keys = [new(), new(), new()];
    private readonly TextBox _ffmpeg = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "Format conversion", "Advanced tools", "Quick actions" }, SelectedIndex = 0 };
    public event Action? SettingsSaved;
    public event Action<RadialMode>? PreviewRequested;

    public RadialSettingsWindow(RadialSettings settings, string bundledFFmpeg)
    {
        _settings = settings;
        Title = "Cortex FX · Radial wheel";
        Width = 570; Height = 760; MinWidth = 500; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
        Foreground = Brushes.White; FontFamily = new("Segoe UI"); FontSize = 13;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/CortexFX;component/Views/Radial/RadialTheme.xaml", UriKind.Relative) });
        var panel = new StackPanel { Margin = new(28) };
        var shell = new DockPanel { Background = Background };
        var footer = new StackPanel { Margin = new(28, 6, 28, 15) };
        DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer);
        shell.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = shell;
        panel.Children.Add(new TextBlock { Text = "RADIAL WORKSPACE", Foreground = Brushes.Crimson, FontSize = 11, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Drag. Hold a key. Drop.", FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new(0, 9, 0, 7) });
        AddText(panel, "Drag from Explorer or your desktop. Move onto a segment and release. Your original files stay in place.", 0);
        _enabled.IsChecked = settings.Enabled; _notifications.IsChecked = settings.Notifications;
        _enabled.Margin = new(0, 17, 0, 12); _notifications.Margin = new(0, 0, 0, 12);
        panel.Children.Add(_enabled); panel.Children.Add(_notifications);
        string[] current = [settings.ConvertKey, settings.ToolsKey, settings.QuickKey];
        string[] titles = ["Format conversion", "Advanced tools", "Quick actions"];
        for (int index = 0; index < _keys.Length; index++)
        {
            var row = new DockPanel { Margin = new(0, 0, 0, 9) };
            _keys[index].ItemsSource = new[] { "Shift", "Alt", "Ctrl" }; _keys[index].SelectedItem = current[index];
            _keys[index].Width = 130; DockPanel.SetDock(_keys[index], Dock.Right); row.Children.Add(_keys[index]);
            row.Children.Add(new TextBlock { Text = titles[index], VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(row);
        }
        var qualityLabel = new TextBlock { Margin = new(0, 12, 0, 8) };
        _quality.Value = settings.Quality;
        qualityLabel.Text = $"Output quality · {settings.Quality}%";
        _quality.ValueChanged += (_, _) => qualityLabel.Text = $"Output quality · {(int)_quality.Value}%";
        panel.Children.Add(qualityLabel); panel.Children.Add(_quality);
        AddText(panel, "PDF page image resolution", 16);
        _dpi.ItemsSource = new[] { 72, 150, 200, 300, 600, settings.PdfDpi }.Distinct().Order().ToArray();
        _dpi.SelectedItem = settings.PdfDpi; _dpi.Margin = new(0, 7, 0, 0); panel.Children.Add(_dpi);
        AddText(panel, "FFmpeg override (blank uses Cortex FX's bundled engine)", 17);
        var engine = new DockPanel { Margin = new(0, 7, 0, 0) };
        var browse = Button("Browse…"); DockPanel.SetDock(browse, Dock.Right); engine.Children.Add(browse);
        _ffmpeg.Text = settings.FFmpegPath; _ffmpeg.Margin = new(0, 0, 8, 0); engine.Children.Add(_ffmpeg); panel.Children.Add(engine);
        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "Select FFmpeg", Filter = "FFmpeg|ffmpeg*.exe|Programs|*.exe" };
            if (picker.ShowDialog(this) == true) _ffmpeg.Text = picker.FileName;
        };
        var engineStatus = new TextBlock { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new(0, 7, 0, 0) };
        void UpdateEngineStatus() => engineStatus.Text = File.Exists(string.IsNullOrWhiteSpace(_ffmpeg.Text) ? bundledFFmpeg : _ffmpeg.Text.Trim('"'))
            ? "● FFmpeg available · conversion stays local" : "● FFmpeg is missing · media segments will explain how to enable it";
        _ffmpeg.TextChanged += (_, _) => UpdateEngineStatus(); UpdateEngineStatus(); panel.Children.Add(engineStatus);
        AddText(panel, "Open the wheel without a drag gesture", 20);
        var preview = new DockPanel { Margin = new(0, 8, 0, 0) };
        var choose = Button("Choose files…"); DockPanel.SetDock(choose, Dock.Right); preview.Children.Add(choose);
        _mode.Margin = new(0, 0, 8, 0); preview.Children.Add(_mode); panel.Children.Add(preview);
        choose.Click += (_, _) => PreviewRequested?.Invoke((RadialMode)_mode.SelectedIndex);
        AddText(panel, "Outputs are saved beside the originals with unique names. Several images become one PDF; PDF merging is available under advanced tools. Esc cancels a conversion.", 18);
        var save = Button("Save preferences"); save.Background = Brushes.Crimson; save.Foreground = Brushes.White;
        save.HorizontalAlignment = HorizontalAlignment.Right; footer.Children.Add(save);
        save.Click += (_, _) => Save(); footer.Children.Add(_status);
    }

    private void Save()
    {
        var candidate = new RadialSettings
        {
            Enabled = _enabled.IsChecked == true, Notifications = _notifications.IsChecked == true,
            Quality = (int)_quality.Value, PdfDpi = (int)_dpi.SelectedItem,
            ConvertKey = (string)_keys[0].SelectedItem, ToolsKey = (string)_keys[1].SelectedItem,
            QuickKey = (string)_keys[2].SelectedItem, FFmpegPath = _ffmpeg.Text.Trim()
        };
        try
        {
            candidate.Save();
            _settings.Enabled = candidate.Enabled; _settings.Notifications = candidate.Notifications;
            _settings.Quality = candidate.Quality; _settings.PdfDpi = candidate.PdfDpi;
            _settings.ConvertKey = candidate.ConvertKey; _settings.ToolsKey = candidate.ToolsKey;
            _settings.QuickKey = candidate.QuickKey; _settings.FFmpegPath = candidate.FFmpegPath;
            _status.Foreground = Brushes.LightGray; _status.Text = "Preferences saved."; SettingsSaved?.Invoke();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { _status.Foreground = Brushes.Salmon; _status.Text = error.Message; }
    }
    private static Button Button(string text) => new() { Content = text, Padding = new(15, 9, 15, 9), MinHeight = 34 };
    private static void AddText(Panel panel, string text, int top) => panel.Children.Add(new TextBlock
    { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(160, 155, 160)), TextWrapping = TextWrapping.Wrap, Margin = new(0, top, 0, 0) });
}
