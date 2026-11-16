using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CortexFX.Core.Radial;
using Microsoft.Win32;

namespace CortexFX.Views.Radial;

public partial class RadialSettingsWindow : Window
{
    private readonly RadialSettings _settings;
    private readonly string _bundledFFmpeg;
    public event Action? SettingsSaved;
    public event Action<RadialMode>? PreviewRequested;

    public RadialSettingsWindow(RadialSettings settings, string bundledFFmpeg)
    {
        _settings = settings;
        _bundledFFmpeg = bundledFFmpeg;
        InitializeComponent();
        Height = Math.Min(740, Math.Max(480, SystemParameters.WorkArea.Height - 48));
        EnabledToggle.IsChecked = settings.Enabled;
        NotificationsToggle.IsChecked = settings.Notifications;
        foreach (var box in new[] { ConvertKeyBox, ToolsKeyBox, QuickKeyBox })
            box.ItemsSource = new[] { "Shift", "Alt", "Ctrl" };
        ConvertKeyBox.SelectedItem = settings.ConvertKey;
        ToolsKeyBox.SelectedItem = settings.ToolsKey;
        QuickKeyBox.SelectedItem = settings.QuickKey;
        QualitySlider.Value = settings.Quality;
        DpiBox.ItemsSource = new[] { 72, 150, 200, 300, 600, settings.PdfDpi }.Distinct().Order()
            .Select(value => new PdfDetail(value, value switch
            {
                72 => "Basic · smaller files (72 DPI)",
                150 => "Standard (150 DPI)",
                200 => "Balanced · recommended (200 DPI)",
                300 => "High detail (300 DPI)",
                600 => "Maximum detail (600 DPI)",
                _ => $"Custom ({value} DPI)"
            })).ToArray();
        DpiBox.SelectedValue = settings.PdfDpi;
        ModeBox.ItemsSource = new[] { "Convert files", "Edit & compress", "File actions" };
        ModeBox.SelectedIndex = 0;
        FfmpegBox.Text = settings.FFmpegPath;
        UpdateEngineStatus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private sealed record PdfDetail(int Value, string Label);
    private void Quality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityValue is not null) QualityValue.Text = $"{(int)e.NewValue}%";
    }
    private void Ffmpeg_TextChanged(object sender, TextChangedEventArgs e) => UpdateEngineStatus();
    private void UpdateEngineStatus()
    {
        if (EngineStatus is null) return;
        bool bundled = string.IsNullOrWhiteSpace(FfmpegBox.Text);
        bool available = File.Exists(bundled ? _bundledFFmpeg : FfmpegBox.Text.Trim().Trim('"'));
        EngineStatus.Text = available
            ? bundled ? "The included media engine is ready." : "Custom media engine found."
            : "Media engine not found. Choose ffmpeg.exe, or clear this field to use the included engine.";
        EngineStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(available ? "#8ADBB2" : "#FFB38A"));
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose a custom media engine", Filter = "FFmpeg|ffmpeg*.exe|Programs|*.exe" };
        if (picker.ShowDialog(this) == true) FfmpegBox.Text = picker.FileName;
    }
    private void ChooseFiles_Click(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke((RadialMode)ModeBox.SelectedIndex);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Save_Click(object sender, RoutedEventArgs e) => SaveSettings();

    internal bool SaveSettings(string? path = null)
    {
        var candidate = new RadialSettings
        {
            Enabled = EnabledToggle.IsChecked == true, Notifications = NotificationsToggle.IsChecked == true,
            Quality = (int)QualitySlider.Value, PdfDpi = (int)DpiBox.SelectedValue,
            ConvertKey = (string)ConvertKeyBox.SelectedItem, ToolsKey = (string)ToolsKeyBox.SelectedItem,
            QuickKey = (string)QuickKeyBox.SelectedItem, FFmpegPath = FfmpegBox.Text.Trim()
        };
        try
        {
            candidate.Save(path);
            _settings.Enabled = candidate.Enabled; _settings.Notifications = candidate.Notifications;
            _settings.Quality = candidate.Quality; _settings.PdfDpi = candidate.PdfDpi;
            _settings.ConvertKey = candidate.ConvertKey; _settings.ToolsKey = candidate.ToolsKey;
            _settings.QuickKey = candidate.QuickKey; _settings.FFmpegPath = candidate.FFmpegPath;
            SaveStatus.Foreground = new SolidColorBrush(Color.FromRgb(138, 219, 178));
            SaveStatus.Text = "Saved. Your file shortcuts are ready.";
            SettingsSaved?.Invoke();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SaveStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 138));
            SaveStatus.Text = error is ArgumentException ? "Choose a different shortcut key for each group." : "Could not save changes. Please try again.";
            return false;
        }
    }
}
