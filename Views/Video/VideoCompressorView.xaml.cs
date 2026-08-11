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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CortexFX.Views.Video;

/// <summary>
/// Video compressor screen. Call Initialize with the FFmpeg path, then
/// subscribe to CloseRequested for Back. Session stays alive if the user leaves mid-job.
/// </summary>
public partial class VideoCompressorView : UserControl
{
    public event EventHandler? CloseRequested;

    /// <summary>File loaded or a compress job still running / finished in this session.</summary>
    public bool HasActiveSession =>
        !string.IsNullOrEmpty(_inputFilePath) || IsCompressing || ResultCard.Visibility == Visibility.Visible;

    public bool IsCompressing =>
        _compressTask is { IsCompleted: false } ||
        (_ffmpegProcess is { HasExited: false });

    public void Initialize(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
        _ = ProbeHardwareEncodersAsync();
    }

    private string _ffmpegPath = string.Empty;
    private string? _inputFilePath;
    private string? _outputFolderOverride;
    private string? _lastOutputPath;
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private Task? _compressTask;
    private TimeSpan _totalDuration = TimeSpan.Zero;
    private string _activeEncoderLabel = "CPU";
    private HardwareCaps _hwCaps = new();

    private static readonly string[] SupportedExtensions =
        { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv", ".m4v", ".ts", ".mts" };

    private sealed class HardwareCaps
    {
        public bool HasNvenc;
        public bool HasNvencHevc;
        public bool HasQuickSync;
        public bool HasQuickSyncHevc;
        public bool HasAmf;
        public bool HasAmfHevc;

        public bool HasAnyH264 => HasNvenc || HasQuickSync || HasAmf;
        public bool HasAnyHevc => HasNvencHevc || HasQuickSyncHevc || HasAmfHevc;
    }

    public VideoCompressorView()
    {
        InitializeComponent();
        IsVisibleChanged += VideoCompressorView_IsVisibleChanged;
    }

    private void VideoCompressorView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            RefreshSessionChrome();
    }

    // Back / Close — keep session alive

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        // Do NOT reset or kill FFmpeg. User can leave and return to the same job.
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshSessionChrome()
    {
        BackgroundJobBadge.Visibility = IsCompressing ? Visibility.Visible : Visibility.Collapsed;

        if (IsCompressing)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            CompressButton.IsEnabled = false;
        }
    }

    // Drop Zone

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropZoneBorder.BorderBrush = BrushFrom("#FF5A3D");
            DropZoneBorder.BorderThickness = new Thickness(2);
        }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = BrushFrom("#4A3038");
        DropZoneBorder.BorderThickness = new Thickness(1.5);
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            TryLoadFile(files[0]);
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsCompressing)
        {
            MessageBox.Show(
                "A compression job is already running. Wait for it to finish or cancel it first.",
                "Compression in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Handled = true;
            return;
        }

        OpenFilePicker();
        e.Handled = true;
    }

    public void OpenFilePicker()
    {
        if (IsCompressing)
            return;

        var dlg = new OpenFileDialog
        {
            Title = "Select a Video File",
            Filter = "Video Files|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
            TryLoadFile(dlg.FileName);
    }

    // File Loading

    private async void TryLoadFile(string path)
    {
        if (IsCompressing)
        {
            MessageBox.Show(
                "Finish or cancel the current compression before loading another video.",
                "Compression in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

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
        FileSizeText.Text = FormatBytes(new FileInfo(path).Length);

        await ProbeVideoAsync(path);
        await GenerateThumbnailAsync(path);

        DropZoneBorder.Visibility = Visibility.Collapsed;
        FileInfoCard.Visibility = Visibility.Visible;
        PresetsPanel.Visibility = Visibility.Visible;
        QualityPanel.Visibility = Visibility.Visible;
        OutputPanel.Visibility = Visibility.Visible;
        CompressButton.Visibility = Visibility.Visible;
        CompressButton.IsEnabled = true;
        ResultCard.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Collapsed;

        PresetSocial.IsChecked = true;
        Preset_Checked(PresetSocial, new RoutedEventArgs());
        UpdateEncoderCapabilityText();

        OutputPathText.Text = Path.GetDirectoryName(path) ?? path;
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (IsCompressing)
        {
            var answer = MessageBox.Show(
                "Compression is still running. Cancel it and clear this video?",
                "Cancel compression?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        ResetView(cancelRunningJob: true);
    }

    // Presets

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (CrfSlider is null) return;
        if (sender is not RadioButton rb) return;

        string tag = rb.Tag?.ToString() ?? "";

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
                // Prefer H.264 (often GPU-accelerated). H.265 software is very slow.
                CrfSlider.Value = 20;
                ResOriginal.IsChecked = true;
                CodecH264.IsChecked = true;
                break;
            case "Custom":
                break;
        }

        if (PresetHintText != null)
        {
            PresetHintText.Text = tag switch
            {
                "Social Media" => "Social Media — sharp enough for feeds, much smaller size.",
                "Email Ready" => "Email Ready — tiny files that send quickly.",
                "Balanced" => "Balanced — good quality with a sensible file size.",
                "High Quality" => "High Quality — keeps detail. Uses fast H.264 (GPU when available).",
                "Custom" => "Custom — drag the slider and pick resolution & codec yourself.",
                _ => "Choose how aggressively you want to shrink the video."
            };
        }

        CrfSlider.IsEnabled = tag == "Custom";
        UpdateQualityLabel();
    }

    private void CrfSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CrfValueText != null)
            CrfValueText.Text = ((int)CrfSlider.Value).ToString();
        UpdateQualityLabel();
    }

    private void UpdateQualityLabel()
    {
        if (QualityLabelText is null || CrfSlider is null) return;

        int crf = (int)CrfSlider.Value;
        if (crf <= 20)
        {
            QualityLabelText.Text = "Near-original detail · larger file";
            QualityLabelText.Foreground = BrushFrom("#2ED47A");
        }
        else if (crf <= 26)
        {
            QualityLabelText.Text = "Balanced quality · recommended for most videos";
            QualityLabelText.Foreground = BrushFrom("#F59E0B");
        }
        else if (crf <= 32)
        {
            QualityLabelText.Text = "Smaller file · fine for social & sharing";
            QualityLabelText.Foreground = BrushFrom("#FF6B4A");
        }
        else
        {
            QualityLabelText.Text = "Maximum compression · visible quality loss";
            QualityLabelText.Foreground = BrushFrom("#FF4D5E");
        }
    }

    private void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select output folder for compressed video"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _outputFolderOverride = dlg.SelectedPath;
            OutputPathText.Text = _outputFolderOverride;
        }
    }

    // Compression

    private async void CompressButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsCompressing)
            return;

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

        string outDir = _outputFolderOverride ?? Path.GetDirectoryName(_inputFilePath)!;
        string baseName = Path.GetFileNameWithoutExtension(_inputFilePath);
        string outPath = Path.Combine(outDir, $"{baseName}_compressed.mp4");

        int counter = 1;
        while (File.Exists(outPath))
            outPath = Path.Combine(outDir, $"{baseName}_compressed_{counter++}.mp4");

        _lastOutputPath = outPath;

        int crf = (int)CrfSlider.Value;
        bool wantHevc = CodecH265.IsChecked == true;
        var (encoder, qualityArgs, encoderLabel) = ResolveEncoder(wantHevc, crf);
        _activeEncoderLabel = encoderLabel;

        string scaleFilter = GetScaleFilter();
        string args = $"-hide_banner -y -i \"{_inputFilePath}\" {qualityArgs}";
        if (!string.IsNullOrEmpty(scaleFilter))
            args += $" -vf \"{scaleFilter}\"";
        args += $" -c:a aac -b:a 128k -movflags +faststart \"{outPath}\"";

        CompressButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ResultCard.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Visible;
        CompressionProgress.Value = 0;
        ProgressPercentText.Text = "0%";
        ProgressStatusText.Text = "Starting...";
        ProgressDetailText.Text = $"Encoder: {encoderLabel}";

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _compressTask = Task.Run(async () =>
        {
            try
            {
                try
                {
                    await RunFFmpegAsync(args, token);
                }
                catch (Exception) when (!token.IsCancellationRequested && IsHardwareEncoder(encoder))
                {
                    // GPU encoder advertised but failed — fall back to software like ConversionRouter.
                    var (swEncoder, swQualityArgs, swLabel) = ResolveEncoder(wantHevc, crf, forceSoftware: true);
                    encoder = swEncoder;
                    encoderLabel = swLabel;
                    string swArgs = $"-hide_banner -y -i \"{_inputFilePath}\" {swQualityArgs}";
                    if (!string.IsNullOrEmpty(scaleFilter))
                        swArgs += $" -vf \"{scaleFilter}\"";
                    swArgs += $" -c:a aac -b:a 128k -movflags +faststart \"{outPath}\"";

                    await Dispatcher.InvokeAsync(() =>
                    {
                        ProgressDetailText.Text = $"Encoder: {swLabel} (GPU fallback)";
                        ProgressStatusText.Text = "Retrying with CPU...";
                    });

                    await RunFFmpegAsync(swArgs, token);
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        ProgressStatusText.Text = "Cancelled";
                        BackgroundJobBadge.Visibility = Visibility.Collapsed;
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    }
                    else
                    {
                        ShowResult(outPath);
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Compression failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ProgressStatusText.Text = "Failed";
                    ProgressDetailText.Text = encoderLabel;
                    BackgroundJobBadge.Visibility = Visibility.Collapsed;
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    CompressButton.IsEnabled = true;
                    BackgroundJobBadge.Visibility = Visibility.Collapsed;
                    _compressTask = null;
                });
            }
        }, token);

        await _compressTask;
    }

    private void CancelCompress_Click(object sender, RoutedEventArgs e)
    {
        CancelCompressIfRunning();
        ProgressStatusText.Text = "Cancelling...";
    }

    private void CancelCompressIfRunning()
    {
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_ffmpegProcess is { HasExited: false })
                _ffmpegProcess.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private string GetScaleFilter()
    {
        if (Res1080.IsChecked == true) return "scale=-2:1080";
        if (Res720.IsChecked == true) return "scale=-2:720";
        if (Res480.IsChecked == true) return "scale=-2:480";
        return "";
    }

    private static bool IsHardwareEncoder(string encoder) =>
        encoder.Contains("_nvenc", StringComparison.OrdinalIgnoreCase) ||
        encoder.Contains("_qsv", StringComparison.OrdinalIgnoreCase) ||
        encoder.Contains("_amf", StringComparison.OrdinalIgnoreCase);

    private (string Encoder, string QualityArgs, string Label) ResolveEncoder(bool wantHevc, int crf, bool forceSoftware = false)
    {
        if (wantHevc)
        {
            if (!forceSoftware)
            {
                if (_hwCaps.HasNvencHevc)
                    return ("hevc_nvenc", $"-c:v hevc_nvenc -cq {crf} -preset p4 -rc vbr", "HEVC · NVIDIA GPU");
                if (_hwCaps.HasQuickSyncHevc)
                    return ("hevc_qsv", $"-c:v hevc_qsv -global_quality {crf} -preset very_fast", "HEVC · Intel Quick Sync");
                if (_hwCaps.HasAmfHevc)
                    return ("hevc_amf", $"-c:v hevc_amf -quality speed -rc cqp -qp_i {crf} -qp_p {crf}", "HEVC · AMD AMF");
            }

            // Software HEVC is slow — use a fast preset so SSD users aren't CPU-starved longer than needed.
            return ("libx265", $"-c:v libx265 -crf {crf} -preset veryfast -threads 0 -x265-params log-level=error", "HEVC · CPU (slower)");
        }

        if (!forceSoftware)
        {
            if (_hwCaps.HasNvenc)
                return ("h264_nvenc", $"-c:v h264_nvenc -cq {crf} -preset p4 -rc vbr", "H.264 · NVIDIA GPU");
            if (_hwCaps.HasQuickSync)
                return ("h264_qsv", $"-c:v h264_qsv -global_quality {crf} -preset very_fast", "H.264 · Intel Quick Sync");
            if (_hwCaps.HasAmf)
                return ("h264_amf", $"-c:v h264_amf -quality speed -rc cqp -qp_i {crf} -qp_p {crf}", "H.264 · AMD AMF");
        }

        return ("libx264", $"-c:v libx264 -crf {crf} -preset veryfast -threads 0", "H.264 · CPU");
    }

    // Hardware probe

    private async Task ProbeHardwareEncodersAsync()
    {
        if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath))
        {
            UpdateEncoderCapabilityText();
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            string output = stdout + stderr;
            _hwCaps = new HardwareCaps
            {
                HasNvenc = output.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase),
                HasNvencHevc = output.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase),
                HasQuickSync = output.Contains("h264_qsv", StringComparison.OrdinalIgnoreCase),
                HasQuickSyncHevc = output.Contains("hevc_qsv", StringComparison.OrdinalIgnoreCase),
                HasAmf = output.Contains("h264_amf", StringComparison.OrdinalIgnoreCase),
                HasAmfHevc = output.Contains("hevc_amf", StringComparison.OrdinalIgnoreCase)
            };
        }
        catch
        {
            _hwCaps = new HardwareCaps();
        }

        await Dispatcher.InvokeAsync(UpdateEncoderCapabilityText);
    }

    private void UpdateEncoderCapabilityText()
    {
        if (EncoderCapabilityText is null) return;

        if (_hwCaps.HasAnyH264 || _hwCaps.HasAnyHevc)
        {
            string gpu =
                _hwCaps.HasNvenc || _hwCaps.HasNvencHevc ? "NVIDIA NVENC" :
                _hwCaps.HasQuickSync || _hwCaps.HasQuickSyncHevc ? "Intel Quick Sync" :
                "AMD AMF";

            EncoderCapabilityText.Text = $"GPU acceleration available ({gpu}). Compression uses the GPU when possible — much faster than CPU-only.";
            EncoderCapabilityText.Foreground = BrushFrom("#2ED47A");
        }
        else
        {
            EncoderCapabilityText.Text = "No GPU encoder detected. Using a fast CPU preset. Tip: SSD helps loading files, but encode speed depends on CPU/GPU — prefer H.264 for speed.";
            EncoderCapabilityText.Foreground = BrushFrom("#F59E0B");
        }
    }

    // FFmpeg execution with progress parsing

    private Task RunFFmpegAsync(string arguments, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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

        _ffmpegProcess.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var durMatch = Regex.Match(e.Data, @"Duration:\s*(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
            if (durMatch.Success)
            {
                _totalDuration = new TimeSpan(0,
                    int.Parse(durMatch.Groups[1].Value),
                    int.Parse(durMatch.Groups[2].Value),
                    int.Parse(durMatch.Groups[3].Value),
                    int.Parse(durMatch.Groups[4].Value) * 10);
            }

            var timeMatch = Regex.Match(e.Data, @"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
            if (timeMatch.Success && _totalDuration.TotalSeconds > 0)
            {
                var current = new TimeSpan(0,
                    int.Parse(timeMatch.Groups[1].Value),
                    int.Parse(timeMatch.Groups[2].Value),
                    int.Parse(timeMatch.Groups[3].Value),
                    int.Parse(timeMatch.Groups[4].Value) * 10);

                double pct = Math.Min(100, (current.TotalSeconds / _totalDuration.TotalSeconds) * 100);

                var speedMatch = Regex.Match(e.Data, @"speed=\s*([\d.]+)x");
                string speedInfo = speedMatch.Success ? $"Speed: {speedMatch.Groups[1].Value}x" : "";

                var bitrateMatch = Regex.Match(e.Data, @"bitrate=\s*([\d.]+)kbits/s");
                string bitrateInfo = bitrateMatch.Success ? $"Bitrate: {bitrateMatch.Groups[1].Value} kbps" : "";

                Dispatcher.BeginInvoke(() =>
                {
                    CompressionProgress.Value = pct;
                    ProgressPercentText.Text = $"{pct:F0}%";
                    ProgressStatusText.Text = $"Compressing... {current:mm\\:ss} / {_totalDuration:mm\\:ss}";
                    ProgressDetailText.Text = $"{_activeEncoderLabel}   {speedInfo}   {bitrateInfo}".Trim();
                    if (IsCompressing)
                        BackgroundJobBadge.Visibility = Visibility.Visible;
                });
            }
        };

        _ffmpegProcess.Exited += (_, _) =>
        {
            int code = -1;
            try { code = _ffmpegProcess.ExitCode; } catch { }

            Dispatcher.BeginInvoke(() =>
            {
                if (code == 0 && !ct.IsCancellationRequested)
                {
                    CompressionProgress.Value = 100;
                    ProgressPercentText.Text = "100%";
                    ProgressStatusText.Text = "Done";
                }
            });

            if (ct.IsCancellationRequested)
                tcs.TrySetResult(true); // treat cancel as completed; caller checks token
            else if (code != 0)
                tcs.TrySetException(new InvalidOperationException($"FFmpeg exited with code {code}."));
            else
                tcs.TrySetResult(true);
        };

        ct.Register(() =>
        {
            try
            {
                if (_ffmpegProcess is { HasExited: false })
                    _ffmpegProcess.Kill(entireProcessTree: true);
            }
            catch { }
        });

        if (!_ffmpegProcess.Start())
        {
            tcs.TrySetException(new InvalidOperationException("Failed to start FFmpeg."));
            return tcs.Task;
        }

        _ffmpegProcess.BeginErrorReadLine();
        return tcs.Task;
    }

    // Result display

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
            ResultTitle.Text = "Compression complete";
            ResultSavingsText.Text = $"Saved {savingsPercent:F0}% — {FormatBytes(inputSize - outputSize)} smaller";
        }
        else
        {
            ResultTitle.Text = "File got larger";
            ResultSavingsText.Text = "Try a higher CRF or a lower resolution for better compression.";
        }

        ResultCard.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Collapsed;
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastOutputPath) && File.Exists(_lastOutputPath))
            Process.Start("explorer.exe", $"/select,\"{_lastOutputPath}\"");
    }

    private void CompressAnother_Click(object sender, RoutedEventArgs e)
    {
        ResetView(cancelRunningJob: true);
    }

    // Video probing / thumbnail

    private async Task ProbeVideoAsync(string path)
    {
        if (string.IsNullOrEmpty(_ffmpegPath)) return;

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

            var durMatch = Regex.Match(combined, @"Duration:\s*(\d{2}:\d{2}:\d{2}\.\d{2})");
            if (durMatch.Success &&
                TimeSpan.TryParseExact(durMatch.Groups[1].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture, out var dur))
            {
                FileDurationText.Text = dur.TotalHours >= 1 ? dur.ToString(@"h\:mm\:ss") : dur.ToString(@"mm\:ss");
                _totalDuration = dur;
            }

            var resMatch = Regex.Match(combined, @"(\d{2,5})x(\d{2,5})");
            if (resMatch.Success)
                FileResolutionText.Text = $"{resMatch.Groups[1].Value}×{resMatch.Groups[2].Value}";
        }
        catch { }
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
                try { File.Delete(tempThumb); } catch { }
            }
        }
        catch { }
    }

    // Helpers

    private void ResetView(bool cancelRunningJob)
    {
        if (cancelRunningJob)
            CancelCompressIfRunning();

        _inputFilePath = null;
        _outputFolderOverride = null;
        _lastOutputPath = null;
        _compressTask = null;

        DropZoneBorder.Visibility = Visibility.Visible;
        FileInfoCard.Visibility = Visibility.Collapsed;
        PresetsPanel.Visibility = Visibility.Collapsed;
        QualityPanel.Visibility = Visibility.Collapsed;
        OutputPanel.Visibility = Visibility.Collapsed;
        CompressButton.Visibility = Visibility.Collapsed;
        CompressButton.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Collapsed;

        VideoThumbnail.Source = null;
        CompressionProgress.Value = 0;
    }

    private static SolidColorBrush BrushFrom(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
