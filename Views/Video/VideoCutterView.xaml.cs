using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CortexFX.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Unosquare.FFME.Common;

namespace CortexFX.Views.Video;

public partial class VideoCutterView : UserControl
{
    public event EventHandler? CloseRequested;

    public bool HasActiveSession =>
        !string.IsNullOrEmpty(_inputFilePath) || IsExporting || ResultCard.Visibility == Visibility.Visible;

    public bool IsExporting => _exportTask is { IsCompleted: false };

    private string _ffmpegPath = string.Empty;
    private IFFmpegService? _ffmpegService;
    private string? _inputFilePath;
    private string? _outputFolderOverride;
    private string? _lastOutputPath;
    private TimeSpan _duration = TimeSpan.Zero;
    private TimeSpan _inPoint = TimeSpan.Zero;
    private TimeSpan _outPoint = TimeSpan.Zero;
    private TimeSpan _playSelectionEnd = TimeSpan.Zero;
    private TimeSpan _fallbackPosition = TimeSpan.Zero;
    private bool _isPlayingSelection;
    private bool _timelineDragging;
    private TimelineDragTarget _timelineDragTarget;
    private double _timelineHandleOffsetX;
    private bool _mediaReady;
    private bool _previewFocused;
    private CancellationTokenSource? _cts;
    private Task? _exportTask;
    private readonly DispatcherTimer _uiTimer;
    private readonly ObservableCollection<SegmentItem> _segments = new();

    private enum TimelineDragTarget { None, Playhead, In, Out }

    private static readonly string[] SupportedExtensions =
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv", ".m4v", ".ts", ".mts", ".mpeg", ".mpg", ".3gp"
    };

    public VideoCutterView()
    {
        InitializeComponent();
        SegmentsList.ItemsSource = _segments;
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _uiTimer.Tick += UiTimer_Tick;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshSessionChrome();
                if (EditorPanel.Visibility == Visibility.Visible &&
                    !string.IsNullOrEmpty(_inputFilePath) &&
                    _duration > TimeSpan.Zero &&
                    TimelineFrames.Children.Count == 0)
                    StartTimelineFrames(_inputFilePath);
                Focus();
            }
            else
            {
                _ = PausePreviewAsync();
                CancelTimelineFrames();
            }
        };
        Unloaded += (_, _) => CancelTimelineFrames();
        PreviewMedia.PositionChanged += PreviewMedia_PositionChanged;
    }

    public void Initialize(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
        _ffmpegService = App.Services.GetService<IFFmpegService>();
    }

    private void RefreshSessionChrome()
    {
        PreviewFocusButton.Visibility = EditorPanel.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        BackgroundJobBadge.Visibility = IsExporting ? Visibility.Visible : Visibility.Collapsed;
        if (IsExporting)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            ExportButton.IsEnabled = false;
        }
    }

    private void PreviewFocusButton_Click(object sender, RoutedEventArgs e)
    {
        _previewFocused = !_previewFocused;
        ApplyPreviewFocusState();
    }

    private void ApplyPreviewFocusState()
    {
        CutterSidePanel.Visibility = _previewFocused ? Visibility.Collapsed : Visibility.Visible;
        CutterTimelinePanel.Visibility = _previewFocused ? Visibility.Collapsed : Visibility.Visible;
        CutterExportDock.Visibility = _previewFocused ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(EditorLeftPanel, _previewFocused ? 2 : 1);
        EditorLeftPanel.Margin = _previewFocused ? new Thickness(0) : new Thickness(0, 0, 10, 0);
        PreviewFocusButtonLabel.Text = _previewFocused ? "Exit focus" : "Focus preview";
        PreviewFocusButton.ToolTip = _previewFocused
            ? "Return to trim controls" : "Expand the video preview";
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        // Keep session + background export alive.
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // Drop / open

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropZoneBorder.BorderBrush = BrushFrom("#22D3EE");
        }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = BrushFrom("#35505C");
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _ = TryLoadFileAsync(files[0]);
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        OpenFilePicker();
        e.Handled = true;
    }

    public void OpenFilePicker()
    {
        if (IsExporting) return;

        var dlg = new OpenFileDialog
        {
            Title = "Select a Video File",
            Filter = "Video Files|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.mpeg;*.mpg;*.3gp|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
            _ = TryLoadFileAsync(dlg.FileName);
    }

    private async Task TryLoadFileAsync(string path)
    {
        if (IsExporting)
        {
            MessageBox.Show("Finish or cancel the current export before loading another video.",
                "Export in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show("The selected video file could not be found.",
                "File not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(SupportedExtensions, ext) < 0)
        {
            MessageBox.Show($"Unsupported format: {ext}\n\nFFmpeg can still try many containers — pick a common video file.",
                "Unsupported File", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await PausePreviewAsync();
        CancelTimelineFrames();
        _mediaReady = false;
        _segments.Clear();
        UpdateSegmentsSummary();

        _inputFilePath = path;
        _outputFolderOverride = null;
        _lastOutputPath = null;
        FileNameText.Text = Path.GetFileName(path);
        OutputPathText.Text = Path.GetDirectoryName(path) ?? path;

        DropZoneBorder.Visibility = Visibility.Collapsed;
        EditorPanel.Visibility = Visibility.Visible;
        PreviewFocusButton.Visibility = Visibility.Visible;
        _previewFocused = false;
        ApplyPreviewFocusState();
        ResultCard.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "Opening preview...";

        try
        {
            await PreviewMedia.Open(new Uri(path));
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            PreviewPlaceholder.Text = "Preview unavailable — you can still mark times and export.";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            // Probe duration via ffmpeg if preview fails.
            await ProbeDurationAsync(path);
            MessageBox.Show($"Preview engine could not open this file.\nYou can still cut/export.\n\n{ex.Message}",
                "Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Focus();
    }

    private async void PreviewMedia_MediaOpened(object? sender, MediaOpenedEventArgs e)
    {
        _mediaReady = true;
        _duration = PreviewMedia.NaturalDuration ?? TimeSpan.Zero;
        if (_duration <= TimeSpan.Zero)
            _duration = TimeSpan.FromSeconds(1);

        _inPoint = TimeSpan.Zero;
        _outPoint = _duration;
        _fallbackPosition = TimeSpan.Zero;

        UpdateTimeLabels();
        UpdateTimelineVisuals();
        if (!string.IsNullOrEmpty(_inputFilePath))
            StartTimelineFrames(_inputFilePath);
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        await PreviewMedia.Pause();
        UpdatePlayPauseUi(false);
    }

    private void PreviewMedia_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        PreviewPlaceholder.Text = "Preview failed — cutting/export still works.";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        if (!string.IsNullOrEmpty(_inputFilePath))
            _ = ProbeDurationAsync(_inputFilePath);
    }

    private async Task ProbeDurationAsync(string path)
    {
        if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath)) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-i \"{path}\" -hide_banner",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var match = System.Text.RegularExpressions.Regex.Match(
                stderr, @"Duration:\s*(\d{2}):(\d{2}):(\d{2}\.\d+)");
            if (match.Success)
            {
                string stamp = $"{match.Groups[1].Value}:{match.Groups[2].Value}:{match.Groups[3].Value}";
                if (!TimeSpan.TryParseExact(stamp, @"hh\:mm\:ss\.FFF", CultureInfo.InvariantCulture, out var dur) &&
                    !TimeSpan.TryParse(stamp, CultureInfo.InvariantCulture, out dur))
                {
                    return;
                }

                _duration = dur;
                _inPoint = TimeSpan.Zero;
                _outPoint = dur;
                _fallbackPosition = TimeSpan.Zero;
                UpdateTimeLabels();
                UpdateTimelineVisuals();
                if (string.Equals(_inputFilePath, path, StringComparison.OrdinalIgnoreCase))
                    StartTimelineFrames(path);
            }
        }
        catch { }
    }

    // Transport

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await TogglePlayPauseAsync();

    private async Task TogglePlayPauseAsync()
    {
        if (!_mediaReady) return;

        if (PreviewMedia.IsPlaying)
        {
            await PreviewMedia.Pause();
            _uiTimer.Stop();
            _isPlayingSelection = false;
            UpdatePlayPauseUi(false);
        }
        else
        {
            if (_duration > TimeSpan.Zero && PreviewMedia.Position >= _duration - TimeSpan.FromMilliseconds(50))
                await SeekToAsync(TimeSpan.Zero);
            await PreviewMedia.Play();
            _uiTimer.Start();
            UpdatePlayPauseUi(true);
        }
    }

    private async Task PausePreviewAsync()
    {
        try
        {
            if (_mediaReady && PreviewMedia.IsPlaying)
                await PreviewMedia.Pause();
        }
        catch { }

        _uiTimer.Stop();
        _isPlayingSelection = false;
        UpdatePlayPauseUi(false);
    }

    private void UpdatePlayPauseUi(bool playing)
    {
        PlayPauseIcon.Text = playing ? "\uE769" : "\uE768";
        PlayPauseLabel.Text = playing ? "Pause" : "Play";
    }

    private async void NudgeBack_Click(object sender, RoutedEventArgs e) => await SeekRelativeAsync(-1);

    private async void NudgeForward_Click(object sender, RoutedEventArgs e) => await SeekRelativeAsync(1);

    private async void NudgeFineBack_Click(object sender, RoutedEventArgs e) => await SeekRelativeAsync(-0.1);

    private async void NudgeFineForward_Click(object sender, RoutedEventArgs e) => await SeekRelativeAsync(0.1);

    private async Task SeekRelativeAsync(double seconds)
    {
        if (_duration <= TimeSpan.Zero) return;
        var target = ClampTime(GetCurrentPosition() + TimeSpan.FromSeconds(seconds));
        await SeekToAsync(target);
    }

    private TimeSpan GetCurrentPosition()
    {
        if (_mediaReady && !_timelineDragging)
            return PreviewMedia.Position;
        return _fallbackPosition;
    }

    private async Task SeekToAsync(TimeSpan position)
    {
        position = ClampTime(position);
        _fallbackPosition = position;

        if (_mediaReady)
        {
            try { await PreviewMedia.Seek(position); }
            catch { PreviewMedia.Position = position; }
        }

        UpdateTimeLabels();
        UpdateTimelineVisuals();
    }

    private void PreviewMedia_PositionChanged(object? sender, PositionChangedEventArgs e)
    {
        if (_timelineDragging) return;
        _fallbackPosition = e.Position;
        UpdateTimeLabels();
        UpdateTimelineVisuals();

        if (_isPlayingSelection && e.Position >= _playSelectionEnd)
            _ = PausePreviewAsync();
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (!_mediaReady || _timelineDragging) return;
        _fallbackPosition = PreviewMedia.Position;
        UpdateTimeLabels();
        UpdateTimelineVisuals();

        if (_isPlayingSelection && PreviewMedia.Position >= _playSelectionEnd)
            _ = PausePreviewAsync();
    }

    private async void PlaySelection_Click(object sender, RoutedEventArgs e)
    {
        if (_outPoint <= _inPoint) return;
        await SeekToAsync(_inPoint);
        _playSelectionEnd = _outPoint;
        _isPlayingSelection = true;
        if (_mediaReady)
        {
            await PreviewMedia.Play();
            _uiTimer.Start();
            UpdatePlayPauseUi(true);
        }
    }

    private void SetIn_Click(object sender, RoutedEventArgs e)
    {
        _inPoint = GetCurrentPosition();
        if (_inPoint >= _outPoint)
            _outPoint = ClampTime(_inPoint + TimeSpan.FromSeconds(1));
        UpdateTimeLabels();
        UpdateTimelineVisuals();
    }

    private void SetOut_Click(object sender, RoutedEventArgs e)
    {
        _outPoint = GetCurrentPosition();
        if (_outPoint <= _inPoint)
            _inPoint = ClampTime(_outPoint - TimeSpan.FromSeconds(1));
        UpdateTimeLabels();
        UpdateTimelineVisuals();
    }

    private void AddSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_outPoint <= _inPoint)
        {
            MessageBox.Show("Out point must be after In point.", "Invalid range", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _segments.Add(new SegmentItem(_inPoint, _outPoint, _segments.Count + 1));
        RenumberSegments();
        UpdateSegmentsSummary();
        EmptySegmentsHint.Visibility = Visibility.Collapsed;
    }

    // Timeline mouse

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineVisuals();

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= TimeSpan.Zero || TimelineHitArea.ActualWidth <= 0) return;
        double x = e.GetPosition(TimelineHitArea).X;
        _timelineDragTarget = HitTestTimelineHandle(x);
        double scale = TimelineHitArea.ActualWidth / _duration.TotalSeconds;
        _timelineHandleOffsetX = _timelineDragTarget switch
        {
            TimelineDragTarget.In => _inPoint.TotalSeconds * scale - x,
            TimelineDragTarget.Out => _outPoint.TotalSeconds * scale - x,
            _ => 0
        };
        _timelineDragging = true;
        TimelineHitArea.CaptureMouse();
        if (_timelineDragTarget == TimelineDragTarget.Playhead)
            UpdateTimelineDrag(x);
        e.Handled = true;
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        double x = e.GetPosition(TimelineHitArea).X;
        if (!_timelineDragging)
        {
            TimelineHitArea.Cursor = HitTestTimelineHandle(x) is TimelineDragTarget.In or TimelineDragTarget.Out
                ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
            UpdateTimelineDrag(x);
    }

    private void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_timelineDragging)
            UpdateTimelineDrag(e.GetPosition(TimelineHitArea).X);
        _timelineDragging = false;
        _timelineDragTarget = TimelineDragTarget.None;
        if (TimelineHitArea.IsMouseCaptured)
            TimelineHitArea.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Timeline_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _timelineDragging = false;
            _timelineDragTarget = TimelineDragTarget.None;
            if (TimelineHitArea.IsMouseCaptured)
                TimelineHitArea.ReleaseMouseCapture();
        }
    }

    private TimelineDragTarget HitTestTimelineHandle(double x)
    {
        if (_duration <= TimeSpan.Zero || TimelineHitArea.ActualWidth <= 0)
            return TimelineDragTarget.Playhead;

        double scale = TimelineHitArea.ActualWidth / _duration.TotalSeconds;
        double inDistance = Math.Abs(x - _inPoint.TotalSeconds * scale);
        double outDistance = Math.Abs(x - _outPoint.TotalSeconds * scale);
        const double handleRadius = 11;
        if (inDistance <= handleRadius || outDistance <= handleRadius)
            return inDistance <= outDistance ? TimelineDragTarget.In : TimelineDragTarget.Out;
        return TimelineDragTarget.Playhead;
    }

    private void UpdateTimelineDrag(double x)
    {
        if (_duration <= TimeSpan.Zero || TimelineHitArea.ActualWidth <= 0) return;
        double effectiveX = _timelineDragTarget is TimelineDragTarget.In or TimelineDragTarget.Out
            ? x + _timelineHandleOffsetX : x;
        var time = TimeSpan.FromSeconds(
            _duration.TotalSeconds * Math.Clamp(effectiveX / TimelineHitArea.ActualWidth, 0, 1));
        var minimumRange = TimeSpan.FromMilliseconds(Math.Min(100, _duration.TotalMilliseconds));
        switch (_timelineDragTarget)
        {
            case TimelineDragTarget.In:
                _inPoint = ClampTime(time <= _outPoint - minimumRange
                    ? time : _outPoint - minimumRange);
                UpdateTimeLabels();
                UpdateTimelineVisuals();
                break;
            case TimelineDragTarget.Out:
                _outPoint = ClampTime(time >= _inPoint + minimumRange
                    ? time : _inPoint + minimumRange);
                UpdateTimeLabels();
                UpdateTimelineVisuals();
                break;
            case TimelineDragTarget.Playhead:
                _ = SeekFromTimelineAsync(x);
                break;
        }
    }

    private async Task SeekFromTimelineAsync(double x)
    {
        if (_duration <= TimeSpan.Zero || TimelineHitArea.ActualWidth <= 0) return;
        double ratio = Math.Clamp(x / TimelineHitArea.ActualWidth, 0, 1);
        await SeekToAsync(TimeSpan.FromSeconds(_duration.TotalSeconds * ratio));
    }

    private void UpdateTimelineVisuals()
    {
        double width = TimelineHitArea.ActualWidth;
        if (width <= 0 || _duration <= TimeSpan.Zero) return;

        double inX = (_inPoint.TotalSeconds / _duration.TotalSeconds) * width;
        double outX = (_outPoint.TotalSeconds / _duration.TotalSeconds) * width;
        double playX = (GetCurrentPosition().TotalSeconds / _duration.TotalSeconds) * width;

        SelectionBand.Margin = new Thickness(Math.Min(inX, outX), 2, 0, 2);
        SelectionBand.Width = Math.Max(2, Math.Abs(outX - inX));
        InMarker.Margin = new Thickness(Math.Clamp(inX, 0, Math.Max(0, width - InMarker.Width)), 1, 0, 1);
        OutMarker.Margin = new Thickness(Math.Clamp(outX, 0, Math.Max(0, width - OutMarker.Width)), 1, 0, 1);
        Playhead.Margin = new Thickness(Math.Clamp(playX, 0, Math.Max(0, width - Playhead.Width)), 2, 0, 2);
    }

    private void UpdateTimeLabels()
    {
        PositionText.Text = $"{FormatShort(GetCurrentPosition())} / {FormatShort(_duration)}";
        InPointText.Text = FormatShort(_inPoint);
        OutPointText.Text = FormatShort(_outPoint);
    }

    private void UpdateSegmentsSummary()
    {
        var total = TimeSpan.FromSeconds(_segments.Sum(s => s.Duration.TotalSeconds));
        SegmentsSummaryText.Text = $"{_segments.Count} segment{(_segments.Count == 1 ? "" : "s")} · {FormatShort(total)} total";
        EmptySegmentsHint.Visibility = _segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenumberSegments()
    {
        for (int i = 0; i < _segments.Count; i++)
            _segments[i].Index = i + 1;
    }

    private void SegmentMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SegmentItem item) return;
        int idx = _segments.IndexOf(item);
        if (idx <= 0) return;
        _segments.Move(idx, idx - 1);
        RenumberSegments();
    }

    private void SegmentMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SegmentItem item) return;
        int idx = _segments.IndexOf(item);
        if (idx < 0 || idx >= _segments.Count - 1) return;
        _segments.Move(idx, idx + 1);
        RenumberSegments();
    }

    private void SegmentRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SegmentItem item) return;
        _segments.Remove(item);
        RenumberSegments();
        UpdateSegmentsSummary();
    }

    private void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select output folder for cut video"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _outputFolderOverride = dlg.SelectedPath;
            OutputPathText.Text = _outputFolderOverride;
        }
    }

    private async void ClearFile_Click(object sender, RoutedEventArgs e)
    {
        if (IsExporting)
        {
            var answer = MessageBox.Show("Cancel the export and clear this video?",
                "Clear video?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            CancelExportIfRunning();
        }

        await ResetViewAsync(cancelExport: true);
    }

    // Export

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsExporting) return;

        if (string.IsNullOrEmpty(_inputFilePath) || !File.Exists(_inputFilePath))
        {
            MessageBox.Show("No input file selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_ffmpegService == null)
        {
            MessageBox.Show("FFmpeg service is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var exportSegments = _segments.Count > 0
            ? _segments.Select(s => new VideoCutSegment(s.Start, s.End)).ToList()
            : new List<VideoCutSegment> { new(_inPoint, _outPoint) };

        if (exportSegments.Any(s => s.Duration <= TimeSpan.Zero))
        {
            MessageBox.Show("Each keep segment needs Out after In.", "Invalid segments", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string outDir = _outputFolderOverride ?? Path.GetDirectoryName(_inputFilePath)!;
        string baseName = Path.GetFileNameWithoutExtension(_inputFilePath);
        string outPath = Path.Combine(outDir, $"{baseName}_cut.mp4");
        int counter = 1;
        while (File.Exists(outPath))
            outPath = Path.Combine(outDir, $"{baseName}_cut_{counter++}.mp4");

        _lastOutputPath = outPath;
        ExportButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ResultCard.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ProgressPercentText.Text = "0%";
        ProgressStatusText.Text = "Exporting cut (stream copy when possible)...";

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<double>(p =>
        {
            ExportProgress.Value = p;
            ProgressPercentText.Text = $"{p:F0}%";
        });

        await PausePreviewAsync();

        _exportTask = Task.Run(async () =>
        {
            try
            {
                await _ffmpegService.CutVideoSegmentsAsync(_inputFilePath, outPath, exportSegments, token, progress);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        ProgressStatusText.Text = "Cancelled";
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    }
                    else
                    {
                        ShowResult(outPath, exportSegments.Count);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ProgressStatusText.Text = "Cancelled";
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Export failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ProgressStatusText.Text = "Failed";
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ExportButton.IsEnabled = true;
                    BackgroundJobBadge.Visibility = Visibility.Collapsed;
                    _exportTask = null;
                });
            }
        }, token);

        await _exportTask;
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        CancelExportIfRunning();
        ProgressStatusText.Text = "Cancelling...";
    }

    private void CancelExportIfRunning()
    {
        try { _cts?.Cancel(); } catch { }
    }

    private void ShowResult(string outputPath, int segmentCount)
    {
        if (!File.Exists(outputPath)) return;
        var size = new FileInfo(outputPath).Length;
        ResultTitle.Text = "Cut exported";
        ResultDetailText.Text = $"{Path.GetFileName(outputPath)} · {FormatBytes(size)} · {segmentCount} segment{(segmentCount == 1 ? "" : "s")}";
        ResultCard.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Collapsed;
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastOutputPath) && File.Exists(_lastOutputPath))
            Process.Start("explorer.exe", $"/select,\"{_lastOutputPath}\"");
    }

    private async void CutAnother_Click(object sender, RoutedEventArgs e)
    {
        await ResetViewAsync(cancelExport: true);
    }

    private async Task ResetViewAsync(bool cancelExport)
    {
        CancelTimelineFrames();
        if (cancelExport)
            CancelExportIfRunning();

        await PausePreviewAsync();
        try { await PreviewMedia.Close(); } catch { }

        _inputFilePath = null;
        _outputFolderOverride = null;
        _lastOutputPath = null;
        _duration = TimeSpan.Zero;
        _inPoint = TimeSpan.Zero;
        _outPoint = TimeSpan.Zero;
        _fallbackPosition = TimeSpan.Zero;
        _mediaReady = false;
        _segments.Clear();
        UpdateSegmentsSummary();

        DropZoneBorder.Visibility = Visibility.Visible;
        EditorPanel.Visibility = Visibility.Collapsed;
        PreviewFocusButton.Visibility = Visibility.Collapsed;
        _previewFocused = false;
        ApplyPreviewFocusState();
        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = true;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "Preview ready";
        FileNameText.Text = "Open a video to begin";
        OutputPathText.Text = "";
        ExportProgress.Value = 0;
    }

    private async void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (EditorPanel.Visibility != Visibility.Visible) return;

        switch (e.Key)
        {
            case Key.K:
            case Key.Space:
                await TogglePlayPauseAsync();
                e.Handled = true;
                break;
            case Key.J:
                await SeekRelativeAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -0.1 : -1);
                e.Handled = true;
                break;
            case Key.L:
                await SeekRelativeAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1);
                e.Handled = true;
                break;
            case Key.I:
                SetIn_Click(sender, e);
                e.Handled = true;
                break;
            case Key.O:
                SetOut_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    private TimeSpan ClampTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        if (_duration > TimeSpan.Zero && value > _duration) return _duration;
        return value;
    }

    private static string FormatShort(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return value.ToString(@"h\:mm\:ss\.f");
        return value.ToString(@"mm\:ss\.f");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static SolidColorBrush BrushFrom(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    public sealed class SegmentItem : INotifyPropertyChanged
    {
        private int _index;

        public SegmentItem(TimeSpan start, TimeSpan end, int index)
        {
            Start = start;
            End = end;
            _index = index;
        }

        public TimeSpan Start { get; }
        public TimeSpan End { get; }
        public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

        public int Index
        {
            get => _index;
            set
            {
                if (_index == value) return;
                _index = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }

        public string DisplayTitle => $"Segment {Index} · {FormatShort(Duration)}";
        public string DisplayRange => $"{FormatShort(Start)} → {FormatShort(End)}";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
