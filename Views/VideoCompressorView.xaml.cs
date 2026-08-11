using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CortexFX.Views;

/// <summary>
/// Self-contained Smart Video Compressor view.
/// Call <see cref="Initialize"/> once to supply the resolved FFmpeg path,
/// then subscribe to <see cref="CloseRequested"/> to navigate back to the dashboard.
/// </summary>
public partial class VideoCompressorView : UserControl
{
    // ------------------------------------------------------------------
    // Public surface used by MainWindow.
    // ------------------------------------------------------------------

    /// <summary>Raised when the user clicks the back arrow.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Supply the resolved FFmpeg executable path (from MainWindow).</summary>
    public void Initialize(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
    }

    // ------------------------------------------------------------------
    // Private state
    // ------------------------------------------------------------------

    private string _ffmpegPath = string.Empty;
    private string? _inputFilePath;
    private string? _outputFolderOverride; // null = same folder as input
    private string? _lastOutputPath;
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private TimeSpan _totalDuration = TimeSpan.Zero;

    // Supported extensions
    private static readonly string[] SupportedExtensions =
        { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv", ".m4v", ".ts", ".mts" };

    public VideoCompressorView()
    {
        InitializeComponent();
    }

    // ------------------------------------------------------------------
    // Back / Close
    // ------------------------------------------------------------------

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ResetView();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------
    // Drop Zone
    // ------------------------------------------------------------------

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropZoneBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF3348"));
        }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#473039"));
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            TryLoadFile(files[0]);
        }
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        OpenFilePicker();
        e.Handled = true;
    }

    public void OpenFilePicker()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a Video File",
            Filter = "Video Files|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            TryLoadFile(dlg.FileName);
        }
    }

    // ------------------------------------------------------------------
    // File Loading
    // ------------------------------------------------------------------

    private async void TryLoadFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(SupportedExtensions, ext) < 0)
        {
            MessageBox.Show($"Unsupported format: {ext}\n\nSupported: MP4, MOV, AVI, MKV, WEBM, FLV, WMV",
                "Unsupported File", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _inputFilePath = path;
        _outputFolderOverride = null;
        _lastOutputPath = null;

        FileNameText.Text = Path.GetFileName(path);

        var fi = new FileInfo(path);
        FileSizeText.Text = FormatBytes(fi.Length);

        // Probe video metadata via FFmpeg
        await ProbeVideoAsync(path);

        // Generate thumbnail
        await GenerateThumbnailAsync(path);

        // Show UI sections
        DropZoneBorder.Visibility = Visibility.Collapsed;
        FileInfoCard.Visibility = Visibility.Visible;
        PresetsPanel.Visibility = Visibility.Visible;
        QualityPanel.Visibility = Visibility.Visible;
        OutputPanel.Visibility = Visibility.Visible;
        CompressButton.Visibility = Visibility.Visible;
        ResultCard.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;

        // Apply default preset
        Preset_Checked(PresetSocial, new RoutedEventArgs());

        OutputPathText.Text = $"Output: {Path.GetDirectoryName(path)}";
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        ResetView();
    }

    // ------------------------------------------------------------------
    // Presets
    // ------------------------------------------------------------------

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        // Guard: fired during InitializeComponent() before child elements exist
        if (CrfSlider is null) return;

        if (sender is not RadioButton rb) return;
        string tag = rb.Tag?.ToString() ?? "";

        // Preset CRF values & resolution defaults
        switch (tag)
        {
            case "Social Media":
                CrfSlider.Value = 28;
                Res720.IsChecked = true;
                CodecH264.IsChecked = true;
                break;
            case "Email Ready":
                CrfSlider.Value = 32;
                Res480.IsChecked = true;
                CodecH264.IsChecked = true;
                break;
            case "Balanced":
                CrfSlider.Value = 24;
                Res1080.IsChecked = true;
                CodecH264.IsChecked = true;
                break;
            case "High Quality":
                CrfSlider.Value = 20;
                ResOriginal.IsChecked = true;
                CodecH265.IsChecked = true;
                break;
            case "Custom":
                // Unlock — don't override values
                break;
        }

        // Enable/disable custom controls
        bool isCustom = tag == "Custom";
        CrfSlider.IsEnabled = isCustom;
        // Resolution & codec radios always enabled for custom
    }

    private void CrfSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CrfValueText != null)
            CrfValueText.Text = ((int)CrfSlider.Value).ToString();
    }

    // ------------------------------------------------------------------
    // Output path
    // ------------------------------------------------------------------

    private void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select output folder for compressed video"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _outputFolderOverride = dlg.SelectedPath;
            OutputPathText.Text = $"Output: {_outputFolderOverride}";
        }
    }

    // ------------------------------------------------------------------
    // Compression
    // ------------------------------------------------------------------

    private async void CompressButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_inputFilePath) || !File.Exists(_inputFilePath))
        {
            MessageBox.Show("No input file selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath))
        {
            MessageBox.Show("FFmpeg not found. Ensure ffmpeg.exe is in the Resources folder.",
                "FFmpeg Missing", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Build output path
        string outDir = _outputFolderOverride ?? Path.GetDirectoryName(_inputFilePath)!;
        string baseName = Path.GetFileNameWithoutExtension(_inputFilePath);
        string outExt = ".mp4"; // Always output MP4
        string outPath = Path.Combine(outDir, $"{baseName}_compressed{outExt}");

        // Avoid overwriting
        int counter = 1;
        while (File.Exists(outPath))
        {
            outPath = Path.Combine(outDir, $"{baseName}_compressed_{counter++}{outExt}");
        }

        _lastOutputPath = outPath;

        // Build FFmpeg arguments
        int crf = (int)CrfSlider.Value;
        string codec = CodecH265.IsChecked == true ? "libx265" : "libx264";
        string scaleFilter = GetScaleFilter();

        string args = $"-i \"{_inputFilePath}\" -c:v {codec} -crf {crf} -preset medium";
        if (!string.IsNullOrEmpty(scaleFilter))
            args += $" -vf \"{scaleFilter}\"";
        args += $" -c:a aac -b:a 128k -movflags +faststart -y \"{outPath}\"";

        // UI state
        CompressButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ResultCard.Visibility = Visibility.Collapsed;
        CompressionProgress.Value = 0;
        ProgressPercentText.Text = "0%";
        ProgressStatusText.Text = "Starting...";
        ProgressDetailText.Text = "";

        _cts = new CancellationTokenSource();

        try
        {
            await RunFFmpegAsync(args, _cts.Token);

            if (_cts.IsCancellationRequested)
            {
                ProgressStatusText.Text = "Cancelled";
                // Clean up partial file
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            }
            else
            {
                // Show results
                ShowResult(outPath);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Compression failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ProgressStatusText.Text = "Failed";
        }
        finally
        {
            CompressButton.IsEnabled = true;
        }
    }

    private void CancelCompress_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                _ffmpegProcess.Kill();
            }
        }
        catch { }
    }

    private string GetScaleFilter()
    {
        if (Res1080.IsChecked == true) return "scale=-2:1080";
        if (Res720.IsChecked == true) return "scale=-2:720";
        if (Res480.IsChecked == true) return "scale=-2:480";
        return ""; // Original
    }

    // ------------------------------------------------------------------
    // FFmpeg execution with progress parsing
    // ------------------------------------------------------------------

    private Task RunFFmpegAsync(string arguments, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();

        _ffmpegProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };

        _ffmpegProcess.ErrorDataReceived += (s, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            // Parse duration from initial metadata
            // "Duration: 00:05:32.10"
            var durMatch = Regex.Match(e.Data, @"Duration:\s*(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
            if (durMatch.Success)
            {
                _totalDuration = new TimeSpan(0,
                    int.Parse(durMatch.Groups[1].Value),
                    int.Parse(durMatch.Groups[2].Value),
                    int.Parse(durMatch.Groups[3].Value),
                    int.Parse(durMatch.Groups[4].Value) * 10);
            }

            // Parse progress: "time=00:02:15.45"
            var timeMatch = Regex.Match(e.Data, @"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
            if (timeMatch.Success && _totalDuration.TotalSeconds > 0)
            {
                var current = new TimeSpan(0,
                    int.Parse(timeMatch.Groups[1].Value),
                    int.Parse(timeMatch.Groups[2].Value),
                    int.Parse(timeMatch.Groups[3].Value),
                    int.Parse(timeMatch.Groups[4].Value) * 10);

                double pct = Math.Min(100, (current.TotalSeconds / _totalDuration.TotalSeconds) * 100);

                // Parse speed
                var speedMatch = Regex.Match(e.Data, @"speed=\s*([\d.]+)x");
                string speedInfo = speedMatch.Success ? $"Speed: {speedMatch.Groups[1].Value}x" : "";

                // Parse bitrate
                var bitrateMatch = Regex.Match(e.Data, @"bitrate=\s*([\d.]+)kbits/s");
                string bitrateInfo = bitrateMatch.Success ? $"Bitrate: {bitrateMatch.Groups[1].Value} kbps" : "";

                Dispatcher.BeginInvoke(() =>
                {
                    CompressionProgress.Value = pct;
                    ProgressPercentText.Text = $"{pct:F0}%";
                    ProgressStatusText.Text = $"Compressing... {current:mm\\:ss} / {_totalDuration:mm\\:ss}";
                    ProgressDetailText.Text = $"{speedInfo}   {bitrateInfo}".Trim();
                });
            }
        };

        _ffmpegProcess.Exited += (s, e) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                CompressionProgress.Value = 100;
                ProgressPercentText.Text = "100%";
                ProgressStatusText.Text = "Done";
            });
            tcs.TrySetResult(true);
        };

        ct.Register(() =>
        {
            try { if (!_ffmpegProcess.HasExited) _ffmpegProcess.Kill(); } catch { }
        });

        _ffmpegProcess.Start();
        _ffmpegProcess.BeginErrorReadLine();

        return tcs.Task;
    }

    // ------------------------------------------------------------------
    // Result display
    // ------------------------------------------------------------------

    private void ShowResult(string outputPath)
    {
        if (!File.Exists(outputPath)) return;

        var inputSize = new FileInfo(_inputFilePath!).Length;
        var outputSize = new FileInfo(outputPath).Length;
        double savingsPercent = inputSize > 0 ? (1.0 - (double)outputSize / inputSize) * 100 : 0;

        ResultBeforeSize.Text = FormatBytes(inputSize);
        ResultAfterSize.Text = FormatBytes(outputSize);

        if (savingsPercent > 0)
        {
            ResultTitle.Text = "✅ Compression Complete!";
            ResultSavingsText.Text = $"Saved {savingsPercent:F0}% — {FormatBytes(inputSize - outputSize)} smaller!";
        }
        else
        {
            ResultTitle.Text = "⚠️ File got larger";
            ResultSavingsText.Text = "Try a higher CRF value or lower resolution for better compression.";
        }

        ResultCard.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastOutputPath) && File.Exists(_lastOutputPath))
        {
            Process.Start("explorer.exe", $"/select,\"{_lastOutputPath}\"");
        }
    }

    private void CompressAnother_Click(object sender, RoutedEventArgs e)
    {
        ResetView();
    }

    // ------------------------------------------------------------------
    // Video probing (metadata)
    // ------------------------------------------------------------------

    private async Task ProbeVideoAsync(string path)
    {
        if (string.IsNullOrEmpty(_ffmpegPath)) return;

        // Use ffprobe-style output from ffmpeg
        string ffprobePath = Path.Combine(Path.GetDirectoryName(_ffmpegPath)!, "ffprobe.exe");
        string probeTool = File.Exists(ffprobePath) ? ffprobePath : _ffmpegPath;
        string probeArgs = probeTool == _ffmpegPath
            ? $"-i \"{path}\" -hide_banner"
            : $"-v error -show_entries format=duration -show_entries stream=width,height -of csv=p=0 \"{path}\"";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = probeTool,
                Arguments = probeArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var proc = Process.Start(psi);
            if (proc == null) return;

            string stderr = await proc.StandardError.ReadToEndAsync();
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            string combined = stderr + "\n" + stdout;

            // Parse duration
            var durMatch = Regex.Match(combined, @"Duration:\s*(\d{2}:\d{2}:\d{2}\.\d{2})");
            if (durMatch.Success)
            {
                if (TimeSpan.TryParseExact(durMatch.Groups[1].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture, out var dur))
                {
                    FileDurationText.Text = dur.TotalHours >= 1 ? dur.ToString(@"h\:mm\:ss") : dur.ToString(@"mm\:ss");
                    _totalDuration = dur;
                }
            }

            // Parse resolution
            var resMatch = Regex.Match(combined, @"(\d{2,5})x(\d{2,5})");
            if (resMatch.Success)
            {
                FileResolutionText.Text = $"{resMatch.Groups[1].Value}×{resMatch.Groups[2].Value}";
            }
        }
        catch { /* Probing is best-effort */ }
    }

    private async Task GenerateThumbnailAsync(string path)
    {
        if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath)) return;

        try
        {
            string tempThumb = Path.Combine(Path.GetTempPath(), $"cortexfx_thumb_{Guid.NewGuid():N}.jpg");
            string args = $"-i \"{path}\" -ss 00:00:02 -vframes 1 -q:v 5 -y \"{tempThumb}\"";

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            var proc = Process.Start(psi);
            if (proc == null) return;
            await proc.WaitForExitAsync();

            if (File.Exists(tempThumb))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(tempThumb);
                bmp.DecodePixelWidth = 200;
                bmp.EndInit();
                bmp.Freeze();
                VideoThumbnail.Source = bmp;

                // Clean up temp file
                try { File.Delete(tempThumb); } catch { }
            }
        }
        catch { /* Thumbnail is best-effort */ }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void ResetView()
    {
        _inputFilePath = null;
        _outputFolderOverride = null;
        _lastOutputPath = null;

        DropZoneBorder.Visibility = Visibility.Visible;
        FileInfoCard.Visibility = Visibility.Collapsed;
        PresetsPanel.Visibility = Visibility.Collapsed;
        QualityPanel.Visibility = Visibility.Collapsed;
        OutputPanel.Visibility = Visibility.Collapsed;
        CompressButton.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Collapsed;

        VideoThumbnail.Source = null;
        CompressionProgress.Value = 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
