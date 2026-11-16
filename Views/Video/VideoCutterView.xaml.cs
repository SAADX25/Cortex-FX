using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Core.Services.Media;
using CortexFX.Core.Video;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Unosquare.FFME.Common;

namespace CortexFX.Views.Video;

public partial class VideoCutterView : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? WorkspaceActivated;

    public bool HasActiveSession =>
        _project.Clips.Count > 0 || IsExporting || ResultCard.Visibility == Visibility.Visible;

    public bool IsExporting => _exportTask is { IsCompleted: false };

    private string _ffmpegPath = string.Empty;
    private IFFmpegService? _ffmpegService;
    private readonly MontageProject _project = new();
    private readonly DispatcherTimer _uiTimer;

    private string? _outputFolderOverride;
    private string? _lastOutputPath;
    private string? _activePlayerSourcePath;
    private MontageClipItem? _playingClip;
    private int _playingClipIndex = -1;

    private TimeSpan _montagePosition = TimeSpan.Zero;
    private TimeSpan _pendingSeekOnOpen = TimeSpan.Zero;
    private bool _hasPendingSeekOnOpen;
    private bool _pendingPlayOnOpen;
    private bool _isPlaying;
    private bool _mediaReady;
    private bool _previewFocused;
    private bool _isMuted;
    private bool _isRazorMode;
    private bool _isUpdatingScrubSlider;
    private bool _isScrubbingMonitor;
    private bool _wasPlayingBeforeScrub;
    private double _volume = 1.0;
    private double _speedRatio = 1.0;
    private double _zoomFactor = 1.0;

    private DateTime _transitionGuardUntil = DateTime.MinValue;
    private DateTime _lastScrubSeekUtc = DateTime.MinValue;
    private int _seekSerial;

    // Timeline pointer scrub state
    private TimelineDragMode _dragMode = TimelineDragMode.None;

    private CancellationTokenSource? _exportCts;
    private Task? _exportTask;

    private enum TimelineDragMode { None, ScrubPlayhead }

    private static readonly double[] SpeedSteps = { 0.5, 1.0, 1.5, 2.0 };

    private static readonly string[] SupportedExtensions =
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv",
        ".m4v", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp",
        ".3g2", ".ogv", ".vob", ".asf", ".f4v"
    };

    public VideoCutterView()
    {
        InitializeComponent();
        ClipsList.ItemsSource = _project.Clips;
        _project.HistoryChanged += (_, _) => UpdateUndoRedoUi();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += UiTimer_Tick;

        PreviewMedia.PositionChanged += PreviewMedia_PositionChanged;
        SizeChanged += (_, _) => UpdateAspectOverlay();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshSessionChrome();
                if (EditorPanel.Visibility == Visibility.Visible)
                {
                    RenderTimeline();
                    foreach (SourceMediaAsset asset in _project.Assets.Values)
                        StartAssetVisualExtraction(asset);
                }
                Focus();
            }
            else
            {
                _ = PausePlaybackAsync();
            }
        };

        Unloaded += (_, _) => CancelAllAssetExtractions();
        UpdateExportSummaryHint();
        UpdateUndoRedoUi();
    }

    public void Initialize(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
        _ffmpegService = App.Services?.GetService<IFFmpegService>();
        _ = RefreshEncoderStatusAsync();
    }

    private IFFmpegService? ResolveFfmpegService()
    {
        if (_ffmpegService != null) return _ffmpegService;
        _ffmpegService = App.Services?.GetService<IFFmpegService>();
        if (_ffmpegService != null) return _ffmpegService;

        if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
        {
            var cfg = new InlineFfmpegConfig(_ffmpegPath);
            _ffmpegService = new FFmpegService(cfg, new ProcessManager());
        }
        return _ffmpegService;
    }

    private async Task RefreshEncoderStatusAsync()
    {
        IFFmpegService? service = ResolveFfmpegService();
        if (service is FFmpegService concrete)
        {
            string label = await Task.Run(() =>
            {
                var caps = concrete.HwCapabilities;
                if (caps.HasNvenc) return "GPU · NVENC";
                if (caps.HasQuickSync) return "GPU · QuickSync";
                if (caps.HasAmf) return "GPU · AMD AMF";
                return "CPU · x264";
            });
            await Dispatcher.InvokeAsync(() => EncoderStatusText.Text = label);
        }
    }

    private void RefreshSessionChrome()
    {
        bool active = EditorPanel.Visibility == Visibility.Visible;
        PreviewFocusButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        HeaderAddClipButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        HeaderClearButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        HeaderReadyPill.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        BackgroundJobBadge.Visibility = IsExporting ? Visibility.Visible : Visibility.Collapsed;

        if (IsExporting)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            ExportButton.IsEnabled = false;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        // Keep montage session & background export alive.
        CloseRequested?.Invoke(this, EventArgs.Empty);
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
        WorkspaceColSplitter.Visibility = _previewFocused ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceRowSplitter.Visibility = _previewFocused ? Visibility.Collapsed : Visibility.Visible;

        LeftDockCol.MinWidth = _previewFocused ? 0 : 268;
        LeftDockCol.Width = _previewFocused ? new GridLength(0) : new GridLength(314);
        BottomDockRow.MinHeight = _previewFocused ? 0 : 224;
        BottomDockRow.Height = _previewFocused ? new GridLength(0) : new GridLength(0.90, GridUnitType.Star);

        EditorLeftPanel.Margin = _previewFocused ? new Thickness(2) : new Thickness(0, 2, 2, 0);
        EditorLeftPanel.BorderThickness = _previewFocused ? new Thickness(0) : new Thickness(1);
        if (MonitorHeaderBar != null)
            MonitorHeaderBar.Visibility = _previewFocused ? Visibility.Collapsed : Visibility.Visible;
        if (MonitorExpandIcon != null)
            MonitorExpandIcon.Text = _previewFocused ? "\uE73F" : "\uE740";
        if (MonitorExpandLabel != null)
            MonitorExpandLabel.Text = _previewFocused ? "Exit full" : "Full view";
        if (MonitorExpandButton != null)
            MonitorExpandButton.ToolTip = _previewFocused
                ? "Exit full video view (F / Esc)"
                : "Expand video to full view (F)";
        PreviewFocusButtonLabel.Text = _previewFocused ? "Exit focus" : "Focus preview";
        PreviewFocusButton.ToolTip = _previewFocused
            ? "Return to Premiere dock layout (F)"
            : "Expand Program monitor (F)";
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateAspectOverlay));
    }

    // File Drop & Picking

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropZoneBorder.BorderBrush = BrushFrom("#2D8CFF");
        }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = BrushFrom("#363636");
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _ = LoadOrAppendFilesAsync(files, append: false);
    }

    private void EditorPanel_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _ = LoadOrAppendFilesAsync(files, append: true);
        e.Handled = true;
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        OpenFilePicker();
        e.Handled = true;
    }

    private void ChooseVideo_Click(object sender, RoutedEventArgs e)
    {
        OpenFilePicker();
        e.Handled = true;
    }

    private void DropZone_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        OpenFilePicker();
        e.Handled = true;
    }

    private void AddClipVideo_Click(object sender, RoutedEventArgs e)
    {
        OpenFilePicker(appendToProject: true);
    }

    public void OpenFilePicker() => OpenFilePicker(appendToProject: _project.Clips.Count > 0);

    private void OpenFilePicker(bool appendToProject)
    {
        if (IsExporting)
        {
            MessageBox.Show("Finish or cancel the active export before adding or replacing videos.",
                "Export in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = appendToProject ? "Add Video Clips to Montage" : "Open Video(s) in Montage Studio",
            Multiselect = true,
            Filter = "Video Files|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.3g2;*.ogv;*.vob|All Files|*.*"
        };

        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
            _ = LoadOrAppendFilesAsync(dlg.FileNames, append: appendToProject);
    }

    private async Task LoadOrAppendFilesAsync(string[] paths, bool append)
    {
        if (IsExporting)
        {
            MessageBox.Show("Finish or cancel the current export before changing clips.",
                "Export in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var validPaths = new List<string>();
        var skipped = new List<string>();

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(SupportedExtensions, ext) >= 0)
                validPaths.Add(path);
            else
                skipped.Add(Path.GetFileName(path));
        }

        if (validPaths.Count == 0)
        {
            string msg = skipped.Count > 0
                ? $"Unsupported format: {string.Join(", ", skipped)}"
                : "No valid video files selected.";
            MessageBox.Show(msg, "Unsupported File", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await PausePlaybackAsync();

        bool wasEmpty = _project.Clips.Count == 0;
        if (!append || wasEmpty)
        {
            CancelAllAssetExtractions();
            _project.Clear();
            _outputFolderOverride = null;
            _lastOutputPath = null;
            _montagePosition = TimeSpan.Zero;
            _zoomFactor = 1.0;
            if (ZoomSlider != null) ZoomSlider.Value = 1.0;
            _previewFocused = false;
            ApplyPreviewFocusState();
        }
        else
        {
            _project.BeginUndoStep();
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        EditorPanel.Visibility = Visibility.Visible;
        ResultCard.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        RefreshSessionChrome();
        if (!append || wasEmpty)
            WorkspaceActivated?.Invoke(this, EventArgs.Empty);

        if (string.IsNullOrEmpty(OutputPathText.Text))
            OutputPathText.Text = _outputFolderOverride ?? (Path.GetDirectoryName(validPaths[0]) ?? validPaths[0]);

        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "Inspecting media...";

        IFFmpegService? service = ResolveFfmpegService();
        MontageClipItem? firstAdded = null;

        foreach (string path in validPaths)
        {
            VideoMediaInfo info;
            try
            {
                info = service != null
                    ? await service.ProbeVideoAsync(path)
                    : FallbackProbe(path);
            }
            catch
            {
                info = FallbackProbe(path);
            }

            if (info.Duration <= TimeSpan.Zero)
                info = info with { Duration = TimeSpan.FromSeconds(1) };

            SourceMediaAsset asset = _project.RegisterAsset(info);
            MontageClipItem clip = _project.AddClipFromAsset(asset, select: firstAdded == null);
            firstAdded ??= clip;
            StartAssetVisualExtraction(asset);
        }

        if (append && !wasEmpty)
            _project.CommitUndoStep();
        else
            _project.ClearHistory();

        if (firstAdded != null)
        {
            _project.SelectClip(firstAdded);
            _montagePosition = firstAdded.TimelineStart;
            await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
        }

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        UpdateAspectOverlay();
        RenderTimeline();
        Focus();
    }

    private static VideoMediaInfo FallbackProbe(string path)
    {
        long size = File.Exists(path) ? new FileInfo(path).Length : 0;
        return new VideoMediaInfo(
            FilePath: path,
            Duration: TimeSpan.Zero,
            CodedWidth: 0,
            CodedHeight: 0,
            DisplayWidth: 0,
            DisplayHeight: 0,
            RotationDegrees: 0,
            Fps: 30,
            VideoCodec: string.Empty,
            AudioCodec: string.Empty,
            HasVideo: true,
            HasAudio: true,
            ContainerFormat: Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
            FileSizeBytes: size);
    }

    // FFME Player & Multi-Clip Synchronization

    private void PreviewMedia_MediaOpening(object? sender, MediaOpeningEventArgs e)
    {
        e.Options.UseParallelDecoding = true;
        e.Options.UseParallelRendering = true;
    }

    private async void PreviewMedia_MediaOpened(object? sender, MediaOpenedEventArgs e)
    {
        _mediaReady = true;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;

        string? openedPath = _activePlayerSourcePath;
        if (!string.IsNullOrEmpty(openedPath) && _project.Assets.TryGetValue(openedPath, out SourceMediaAsset? asset))
        {
            TimeSpan naturalDur = PreviewMedia.NaturalDuration ?? asset.Info.Duration;
            int w = PreviewMedia.NaturalVideoWidth > 0 ? PreviewMedia.NaturalVideoWidth : asset.Info.DisplayWidth;
            int h = PreviewMedia.NaturalVideoHeight > 0 ? PreviewMedia.NaturalVideoHeight : asset.Info.DisplayHeight;
            double fps = PreviewMedia.VideoFrameRate > 0 ? PreviewMedia.VideoFrameRate : asset.Info.Fps;

            bool durationChanged = asset.Info.Duration <= TimeSpan.FromSeconds(1) && naturalDur > TimeSpan.FromSeconds(1);
            bool needsRefresh = durationChanged || (asset.Info.DisplayWidth == 0 && w > 0);

            if (needsRefresh)
            {
                VideoMediaInfo updated = asset.Info with
                {
                    Duration = naturalDur > TimeSpan.Zero ? naturalDur : asset.Info.Duration,
                    DisplayWidth = w,
                    DisplayHeight = h,
                    Fps = fps,
                    HasAudio = PreviewMedia.HasAudio
                };
                _project.RefreshAssetMetadata(openedPath, updated);
                if (durationChanged)
                {
                    CancelAssetExtraction(openedPath);
                    asset.Frames.Clear();
                    asset.AudioPeaks = null;
                }
                StartAssetVisualExtraction(asset);
            }
        }

        ApplyPlayerAudioAndSpeed();

        if (_hasPendingSeekOnOpen)
        {
            _hasPendingSeekOnOpen = false;
            TimeSpan seekTarget = _pendingSeekOnOpen;
            bool shouldPlay = _pendingPlayOnOpen;
            _pendingPlayOnOpen = false;

            _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(250);
            try { await PreviewMedia.Seek(seekTarget); }
            catch { PreviewMedia.Position = seekTarget; }

            if (shouldPlay)
            {
                await PreviewMedia.Play();
                _isPlaying = true;
                _uiTimer.Start();
                UpdatePlayPauseUi(true);
            }
            else
            {
                await PreviewMedia.Pause();
                _isPlaying = false;
                UpdatePlayPauseUi(false);
            }
        }
        else
        {
            await PreviewMedia.Pause();
            UpdatePlayPauseUi(false);
        }

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        RenderTimeline();
        UpdateAspectOverlay();
    }

    private void PreviewMedia_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        _mediaReady = false;
        _isPlaying = false;
        _uiTimer.Stop();
        UpdatePlayPauseUi(false);
        PreviewPlaceholder.Text = "Live preview unavailable for this codec — timeline cutting & export still work.";
        PreviewPlaceholder.Visibility = Visibility.Visible;
    }

    private async Task SyncPlayerToMontagePositionAsync(
        TimeSpan montageTime,
        bool autoPlay,
        bool forceExactSeek)
    {
        if (_project.Clips.Count == 0) return;

        montageTime = ClampMontageTime(montageTime);
        _montagePosition = montageTime;

        var (clip, clipIndex, sourceTime) = _project.MapMontageToSource(montageTime);
        if (clip == null) return;

        _playingClip = clip;
        _playingClipIndex = clipIndex;

        if (!ReferenceEquals(_project.SelectedClip, clip))
            _project.SelectClip(clip);

        bool needOpen = !string.Equals(_activePlayerSourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase);

        if (needOpen)
        {
            _activePlayerSourcePath = clip.SourcePath;
            _mediaReady = false;
            _pendingSeekOnOpen = sourceTime;
            _hasPendingSeekOnOpen = true;
            _pendingPlayOnOpen = autoPlay;
            _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(400);

            PreviewPlaceholder.Text = "Loading clip...";
            PreviewPlaceholder.Visibility = Visibility.Visible;

            try
            {
                await PreviewMedia.Open(new Uri(clip.SourcePath));
            }
            catch
            {
                PreviewPlaceholder.Text = "Live preview unavailable — export still works.";
                PreviewPlaceholder.Visibility = Visibility.Visible;
            }
        }
        else if (_mediaReady)
        {
            if (forceExactSeek || (DateTime.UtcNow - _lastScrubSeekUtc).TotalMilliseconds >= 55)
            {
                _lastScrubSeekUtc = DateTime.UtcNow;
                _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(160);
                int serial = Interlocked.Increment(ref _seekSerial);
                try
                {
                    await PreviewMedia.Seek(sourceTime);
                }
                catch
                {
                    if (serial == _seekSerial)
                        PreviewMedia.Position = sourceTime;
                }
            }

            if (autoPlay && !PreviewMedia.IsPlaying)
            {
                await PreviewMedia.Play();
                _isPlaying = true;
                _uiTimer.Start();
                UpdatePlayPauseUi(true);
            }
        }

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdatePlayheadVisual();
    }

    private void PreviewMedia_PositionChanged(object? sender, PositionChangedEventArgs e)
    {
        if (!_isPlaying || _dragMode != TimelineDragMode.None) return;
        if (DateTime.UtcNow < _transitionGuardUntil) return;
        HandleActivePlaybackPosition(e.Position);
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (!_mediaReady || !_isPlaying || _dragMode != TimelineDragMode.None) return;
        if (DateTime.UtcNow < _transitionGuardUntil) return;
        HandleActivePlaybackPosition(PreviewMedia.Position);
    }

    private void HandleActivePlaybackPosition(TimeSpan sourcePos)
    {
        MontageClipItem? clip = _playingClip;
        if (clip == null || !string.Equals(clip.SourcePath, _activePlayerSourcePath, StringComparison.OrdinalIgnoreCase))
            return;

        if (sourcePos < clip.Start)
            sourcePos = clip.Start;

        _montagePosition = _project.MapSourceToMontage(clip, sourcePos);
        UpdateTimeLabels();
        UpdatePlayheadVisual();

        // Check if playback reached the end of this clip in the montage.
        if (sourcePos >= clip.End - TimeSpan.FromMilliseconds(35))
        {
            int nextIndex = _playingClipIndex + 1;
            if (nextIndex >= 0 && nextIndex < _project.Clips.Count)
            {
                MontageClipItem nextClip = _project.Clips[nextIndex];
                _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(260);
                _ = SyncPlayerToMontagePositionAsync(nextClip.TimelineStart, autoPlay: true, forceExactSeek: true);
            }
            else if (LoopToggle.IsChecked == true && _project.Clips.Count > 0)
            {
                _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(260);
                _ = SyncPlayerToMontagePositionAsync(TimeSpan.Zero, autoPlay: true, forceExactSeek: true);
            }
            else
            {
                _montagePosition = _project.TotalDuration;
                _ = PausePlaybackAsync();
                UpdateTimeLabels();
                UpdatePlayheadVisual();
            }
        }
    }

    // Transport Controls

    private void MonitorSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (EditorPanel.Visibility == Visibility.Visible && _project.Clips.Count > 0)
            _ = TogglePlayPauseAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await TogglePlayPauseAsync();

    private async Task TogglePlayPauseAsync()
    {
        if (_project.Clips.Count == 0) return;

        if (_isPlaying)
        {
            await PausePlaybackAsync();
            return;
        }

        if (_montagePosition >= _project.TotalDuration - TimeSpan.FromMilliseconds(60))
            _montagePosition = TimeSpan.Zero;

        _isPlaying = true;
        UpdatePlayPauseUi(true);
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: true, forceExactSeek: true);
    }

    private async Task PausePlaybackAsync()
    {
        _isPlaying = false;
        _pendingPlayOnOpen = false;
        _uiTimer.Stop();
        try
        {
            if (_mediaReady && PreviewMedia.IsPlaying)
                await PreviewMedia.Pause();
        }
        catch { }
        UpdatePlayPauseUi(false);
    }

    private void UpdatePlayPauseUi(bool playing)
    {
        PlayPauseIcon.Text = playing ? "\uE769" : "\uE768";
    }

    private async void StepBackFrame_Click(object sender, RoutedEventArgs e) => await StepFrameAsync(forward: false);

    private async void StepForwardFrame_Click(object sender, RoutedEventArgs e) => await StepFrameAsync(forward: true);

    private async Task StepFrameAsync(bool forward)
    {
        if (_project.Clips.Count == 0) return;
        await PausePlaybackAsync();

        if (_mediaReady && _playingClip != null)
        {
            double fps = _playingClip.SourceFps > 1 ? _playingClip.SourceFps : 30.0;
            TimeSpan step = TimeSpan.FromSeconds(1.0 / fps);
            try
            {
                if (forward) await PreviewMedia.StepForward();
                else await PreviewMedia.StepBackward();
            }
            catch
            {
                await SeekMontageRelativeAsync(forward ? step.TotalSeconds : -step.TotalSeconds);
                return;
            }

            TimeSpan pos = PreviewMedia.Position;
            if (pos < _playingClip.Start || pos > _playingClip.End)
            {
                await SeekMontageRelativeAsync(forward ? step.TotalSeconds : -step.TotalSeconds);
            }
            else
            {
                _montagePosition = _project.MapSourceToMontage(_playingClip, pos);
                UpdateTimeLabels();
                UpdatePlayheadVisual();
            }
        }
        else
        {
            await SeekMontageRelativeAsync(forward ? 1.0 / 30.0 : -1.0 / 30.0);
        }
    }

    private async void NudgeBack_Click(object sender, RoutedEventArgs e) => await SeekMontageRelativeAsync(-1);

    private async void NudgeForward_Click(object sender, RoutedEventArgs e) => await SeekMontageRelativeAsync(1);

    private async Task SeekMontageRelativeAsync(double seconds)
    {
        if (_project.TotalDuration <= TimeSpan.Zero) return;
        TimeSpan target = ClampMontageTime(_montagePosition + TimeSpan.FromSeconds(seconds));
        await SyncPlayerToMontagePositionAsync(target, autoPlay: _isPlaying, forceExactSeek: true);
    }

    private void CycleSpeed_Click(object sender, RoutedEventArgs e)
    {
        int idx = Array.FindIndex(SpeedSteps, s => Math.Abs(s - _speedRatio) < 0.05);
        _speedRatio = SpeedSteps[(idx + 1) % SpeedSteps.Length];
        SpeedButton.Content = $"{_speedRatio:0.0}×";
        ApplyPlayerAudioAndSpeed();
    }

    private void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        SyncMuteUi();
        ApplyPlayerAudioAndSpeed();
        RenderTimeline();
    }

    private void TrackMute_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = TrackMuteButton?.IsChecked == true;
        SyncMuteUi();
        ApplyPlayerAudioAndSpeed();
        RenderTimeline();
    }

    private void SyncMuteUi()
    {
        if (MuteIcon != null)
            MuteIcon.Text = _isMuted ? "\uE74F" : "\uE767";
        if (TrackMuteButton != null)
            TrackMuteButton.IsChecked = _isMuted;
        if (VolumePercentText != null)
            VolumePercentText.Text = _isMuted ? "Mute" : $"{(int)Math.Round(_volume * 100)}%";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _volume = e.NewValue;
        if (_isMuted && _volume > 0.01)
        {
            _isMuted = false;
            RenderTimeline();
        }
        SyncMuteUi();
        ApplyPlayerAudioAndSpeed();
    }

    private void ProgramScrubSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_project.TotalDuration <= TimeSpan.Zero) return;
        _isScrubbingMonitor = true;
        _wasPlayingBeforeScrub = _isPlaying;
        _ = PausePlaybackAsync();
    }

    private void ProgramScrubSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingScrubSlider || !IsInitialized || _project.TotalDuration <= TimeSpan.Zero) return;
        if (!_isScrubbingMonitor && !ProgramScrubSlider.IsMouseOver) return;
        TimeSpan target = TimeSpan.FromSeconds(_project.TotalDuration.TotalSeconds * (e.NewValue / 1000.0));
        _ = SyncPlayerToMontagePositionAsync(target, autoPlay: false, forceExactSeek: false);
    }

    private async void ProgramScrubSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        bool wasPlaying = _wasPlayingBeforeScrub;
        _isScrubbingMonitor = false;
        _wasPlayingBeforeScrub = false;
        if (_project.TotalDuration <= TimeSpan.Zero || ProgramScrubSlider == null) return;
        TimeSpan target = TimeSpan.FromSeconds(_project.TotalDuration.TotalSeconds * (ProgramScrubSlider.Value / 1000.0));
        await SyncPlayerToMontagePositionAsync(target, autoPlay: wasPlaying, forceExactSeek: true);
    }

    private void ApplyPlayerAudioAndSpeed()
    {
        if (PreviewMedia == null) return;
        try
        {
            PreviewMedia.IsMuted = _isMuted;
            PreviewMedia.Volume = _volume;
            PreviewMedia.SpeedRatio = _speedRatio;
        }
        catch { }
    }

    // Montage Editing Actions (Split, Trim In/Out, Reset, Duplicate, Reorder, Delete)

    private async void SplitAtPlayhead_Click(object sender, RoutedEventArgs e) => await SplitAtPlayheadAsync();

    private async Task SplitAtPlayheadAsync()
    {
        if (_project.Clips.Count == 0) return;
        await PausePlaybackAsync();

        var (ok, second) = _project.SplitAtMontageTime(_montagePosition);
        if (!ok || second == null)
        {
            MessageBox.Show("Move the playhead at least 0.1s away from clip edges to split.",
                "Cannot split here", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _playingClip = second;
        _playingClipIndex = _project.Clips.IndexOf(second);
        _montagePosition = second.TimelineStart;

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
        EnsurePlayheadVisible();
    }

    private void DuplicateSelectedOrAddSlice_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? target = _project.SelectedClip ?? _playingClip ?? _project.Clips.FirstOrDefault();
        if (target == null) return;

        MontageClipItem? copy = _project.DuplicateClip(target);
        if (copy == null) return;

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
    }

    private async void DeleteSelectedClip_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? target = _project.SelectedClip ?? _playingClip;
        if (target == null) return;
        await RemoveClipWithUiRefreshAsync(target);
    }

    private async void ClipCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MontageClipItem clip) return;
        _project.SelectClip(clip);
        _montagePosition = clip.TimelineStart;
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
        RenderTimeline();
        EnsurePlayheadVisible();
    }

    private void SegmentMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MontageClipItem clip) return;
        if (_project.MoveClipEarlier(clip))
        {
            _montagePosition = clip.TimelineStart;
            UpdateProjectHeaderAndHud();
            UpdateTimeLabels();
            RenderTimeline();
        }
        e.Handled = true;
    }

    private void SegmentMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MontageClipItem clip) return;
        if (_project.MoveClipLater(clip))
        {
            _montagePosition = clip.TimelineStart;
            UpdateProjectHeaderAndHud();
            UpdateTimeLabels();
            RenderTimeline();
        }
        e.Handled = true;
    }

    private async void SegmentRemove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not MontageClipItem clip) return;
        await RemoveClipWithUiRefreshAsync(clip);
    }

    private async Task RemoveClipWithUiRefreshAsync(MontageClipItem clip)
    {
        if (_project.Clips.Count <= 1)
        {
            var answer = MessageBox.Show(
                "Removing the last clip clears the project. Clear video?",
                "Clear project?", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
                await ResetViewAsync(cancelExport: true);
            return;
        }

        await PausePlaybackAsync();
        _project.RemoveClip(clip);
        MontageClipItem? next = _project.SelectedClip ?? _project.Clips.FirstOrDefault();
        if (next != null)
        {
            _montagePosition = next.TimelineStart;
            await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
        }

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
    }

    // Timeline Mouse Interaction (Scrub + Trim Handles + Zoom)

    private void TimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EditorPanel.Visibility == Visibility.Visible)
            UpdateTimelineLayoutInPlace();
    }

    private void TimelineScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double delta = e.Delta > 0 ? 0.45 : -0.45;
            ZoomSlider.Value = Math.Clamp(_zoomFactor + delta, ZoomSlider.Minimum, ZoomSlider.Maximum);
            e.Handled = true;
            return;
        }

        if (_zoomFactor > 1.01)
        {
            TimelineScrollViewer.ScrollToHorizontalOffset(TimelineScrollViewer.HorizontalOffset - e.Delta * 0.6);
            e.Handled = true;
        }
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, _zoomFactor - 0.6);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, _zoomFactor + 0.6);

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        ZoomSlider.Value = 1.0;
        TimelineScrollViewer.ScrollToHorizontalOffset(0);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _zoomFactor = Math.Max(1.0, e.NewValue);
        if (IsInitialized && EditorPanel.Visibility == Visibility.Visible)
        {
            UpdateTimelineLayoutInPlace();
            EnsurePlayheadVisible();
        }
    }

    private void SelectTool_Click(object sender, RoutedEventArgs e) => SetRazorMode(false);

    private void RazorTool_Click(object sender, RoutedEventArgs e) => SetRazorMode(true);

    private void SetRazorMode(bool razor)
    {
        _isRazorMode = razor;
        if (SelectToolBtn != null) SelectToolBtn.IsChecked = !razor;
        if (RazorToolBtn != null) RazorToolBtn.IsChecked = razor;
        if (TimelineSurface != null) TimelineSurface.Cursor = razor ? Cursors.Cross : Cursors.Arrow;
        if (RazorModeHint != null) RazorModeHint.Visibility = razor ? Visibility.Visible : Visibility.Collapsed;
        if (TimelineSummaryPanel != null) TimelineSummaryPanel.Visibility = razor ? Visibility.Collapsed : Visibility.Visible;
        if (!razor && RazorGuideLine != null) RazorGuideLine.Visibility = Visibility.Collapsed;
    }

    private void UpdateUndoRedoUi()
    {
        if (UndoButton != null) UndoButton.IsEnabled = _project.CanUndo;
        if (RedoButton != null) RedoButton.IsEnabled = _project.CanRedo;
    }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await ExecuteHistoryAsync(undo: true);

    private async void Redo_Click(object sender, RoutedEventArgs e) => await ExecuteHistoryAsync(undo: false);

    private async Task ExecuteHistoryAsync(bool undo)
    {
        bool changed = undo ? _project.Undo() : _project.Redo();
        if (!changed) return;

        await PausePlaybackAsync();
        _playingClip = _project.SelectedClip ?? _project.Clips.FirstOrDefault();
        _playingClipIndex = _playingClip != null ? _project.Clips.IndexOf(_playingClip) : -1;
        _montagePosition = _playingClip != null ? _playingClip.TimelineStart : TimeSpan.Zero;
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
    }

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_project.TotalDuration <= TimeSpan.Zero || TimelineSurface.ActualWidth <= 0) return;

        Point pos = e.GetPosition(TimelineSurface);

        if (_isRazorMode)
        {
            double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
            double ratio = Math.Clamp(pos.X / totalW, 0, 1);
            TimeSpan splitAt = TimeSpan.FromSeconds(_project.TotalDuration.TotalSeconds * ratio);
            _ = PausePlaybackAsync();
            var (ok, second) = _project.SplitAtMontageTime(splitAt);
            if (ok && second != null)
            {
                _playingClip = second;
                _playingClipIndex = _project.Clips.IndexOf(second);
                _montagePosition = second.TimelineStart;
                UpdateProjectHeaderAndHud();
                UpdateTimeLabels();
                UpdateExportSummaryHint();
                RenderTimeline();
            }
            e.Handled = true;
            return;
        }

        _dragMode = TimelineDragMode.ScrubPlayhead;
        MontageClipItem? clickedClip = FindAncestorTag<MontageClipItem>(e.OriginalSource as DependencyObject);
        if (clickedClip != null)
            _project.SelectClip(clickedClip);

        _ = PausePlaybackAsync();
        TimelineSurface.CaptureMouse();
        ProcessTimelineDrag(pos.X, finalSeek: false);
        e.Handled = true;
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(TimelineSurface);
        if (_isRazorMode && _dragMode == TimelineDragMode.None && RazorGuideLine != null)
        {
            RazorGuideLine.X1 = pos.X;
            RazorGuideLine.X2 = pos.X;
            RazorGuideLine.Visibility = Visibility.Visible;
            return;
        }

        if (_dragMode == TimelineDragMode.None) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTimelineDrag(pos.X);
            return;
        }
        ProcessTimelineDrag(pos.X, finalSeek: false);
    }

    private void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode != TimelineDragMode.None)
            EndTimelineDrag(e.GetPosition(TimelineSurface).X);
        e.Handled = true;
    }

    private void Timeline_MouseLeave(object sender, MouseEventArgs e)
    {
        if (RazorGuideLine != null)
            RazorGuideLine.Visibility = Visibility.Collapsed;
        if (_dragMode != TimelineDragMode.None && e.LeftButton != MouseButtonState.Pressed)
            EndTimelineDrag(e.GetPosition(TimelineSurface).X);
    }

    private void EndTimelineDrag(double x)
    {
        ProcessTimelineDrag(x, finalSeek: true);
        _dragMode = TimelineDragMode.None;
        TimelineDragBadge.Visibility = Visibility.Collapsed;
        if (TimelineSurface.IsMouseCaptured)
            TimelineSurface.ReleaseMouseCapture();
        RenderTimeline();
    }

    private void ProcessTimelineDrag(double x, bool finalSeek)
    {
        double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
        double totalSec = Math.Max(0.1, _project.TotalDuration.TotalSeconds);
        double ratio = Math.Clamp(x / totalW, 0, 1);
        TimeSpan target = TimeSpan.FromSeconds(totalSec * ratio);
        ShowDragBadge($"SEEK {MontageProject.FormatTimecode(target)}", x, totalW);
        _ = SyncPlayerToMontagePositionAsync(target, autoPlay: false, forceExactSeek: finalSeek);
    }

    private void ShowDragBadge(string text, double x, double totalW)
    {
        TimelineDragBadgeText.Text = text;
        TimelineDragBadge.Visibility = Visibility.Visible;
        double badgeLeft = Math.Clamp(x + 10, 4, Math.Max(4, totalW - 108));
        Canvas.SetLeft(TimelineDragBadge, badgeLeft);
    }

    private void UpdatePlayheadVisual()
    {
        double totalW = TimelineSurface.Width > 0 ? TimelineSurface.Width : TimelineSurface.ActualWidth;
        double totalSec = _project.TotalDuration.TotalSeconds;
        if (totalW <= 0 || totalSec <= 0) return;

        double x = Math.Clamp((_montagePosition.TotalSeconds / totalSec) * totalW, 0, totalW);
        Canvas.SetLeft(PlayheadCap, Math.Clamp(x - PlayheadCap.Width / 2.0, 0, Math.Max(0, totalW - PlayheadCap.Width)));
        Canvas.SetLeft(PlayheadLine, Math.Clamp(x - PlayheadLine.Width / 2.0, 0, Math.Max(0, totalW - PlayheadLine.Width)));
    }

    private void EnsurePlayheadVisible()
    {
        if (_zoomFactor <= 1.01) return;
        double totalW = TimelineSurface.Width;
        double totalSec = _project.TotalDuration.TotalSeconds;
        if (totalW <= 0 || totalSec <= 0) return;

        double x = (_montagePosition.TotalSeconds / totalSec) * totalW;
        double left = TimelineScrollViewer.HorizontalOffset;
        double right = left + TimelineScrollViewer.ViewportWidth;
        if (x < left + 20 || x > right - 20)
            TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, x - TimelineScrollViewer.ViewportWidth * 0.35));
    }

    private static T? FindAncestorTag<T>(DependencyObject? current) where T : class
    {
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.Tag is T typed)
                return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // Labels, Aspect Guide & Export Settings

    private void UpdateProjectHeaderAndHud()
    {
        int count = _project.Clips.Count;
        TimeSpan total = _project.TotalDuration;

        if (count == 0)
        {
            FileNameText.Text = "No media loaded";
            SegmentsSummaryText.Text = "0 · 00:00.0";
            MonitorMediaHudText.Text = string.Empty;
            return;
        }

        int uniqueSources = _project.Clips.Select(c => c.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        FileNameText.Text = uniqueSources == 1
            ? $"{_project.Clips[0].SourceFileName} · {count} clip{(count == 1 ? "" : "s")} · {MontageProject.FormatShort(total)}"
            : $"{uniqueSources} videos · {count} clips · {MontageProject.FormatShort(total)}";

        SegmentsSummaryText.Text = $"{count} · {MontageProject.FormatShort(total)}";

        MontageClipItem? active = _project.SelectedClip ?? _playingClip ?? _project.Clips.FirstOrDefault();
        if (active != null)
        {
            string res = active.DisplayResolution;
            string fps = active.SourceFps > 0 ? $"{active.SourceFps:0.##}fps" : "";
            string codec = _project.Assets.TryGetValue(active.SourcePath, out SourceMediaAsset? asset) &&
                           !string.IsNullOrEmpty(asset.Info.VideoCodec)
                ? asset.Info.VideoCodec.ToUpperInvariant()
                : "";
            string audio = active.HasAudio ? "Audio" : "Silent";
            string hud = string.Join(" · ", new[] { $"Clip #{active.OrderIndex}", res, active.AspectRatioLabel, fps, codec, audio }.Where(s => !string.IsNullOrWhiteSpace(s)));
            MonitorMediaHudText.Text = hud;
        }
    }

    private void UpdateTimeLabels()
    {
        string current = MontageProject.FormatTimecode(_montagePosition);
        string total = MontageProject.FormatTimecode(_project.TotalDuration);
        PositionText.Text = current;
        if (MonitorTotalText != null) MonitorTotalText.Text = total;
        if (TimelineTimecodeText != null) TimelineTimecodeText.Text = current;
        MontageTotalText.Text = MontageProject.FormatShort(_project.TotalDuration);

        if (ProgramScrubSlider != null && !_isScrubbingMonitor)
        {
            _isUpdatingScrubSlider = true;
            double totalSec = _project.TotalDuration.TotalSeconds;
            ProgramScrubSlider.Value = totalSec > 0
                ? Math.Clamp((_montagePosition.TotalSeconds / totalSec) * 1000.0, 0, 1000)
                : 0;
            _isUpdatingScrubSlider = false;
        }

        MontageClipItem? active = _project.SelectedClip ?? _playingClip;
        if (SelectedClipDurationText != null)
            SelectedClipDurationText.Text = active != null ? MontageProject.FormatShort(active.Duration) : "00:00.0";
    }

    private void ExportOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateExportSummaryHint();
        UpdateAspectOverlay();
    }

    private (string Format, VideoResolutionPreset Resolution, VideoAspectFitMode AspectFit, VideoCutMode CutMode) ReadExportSettings()
    {
        VideoCutMode mode = ModeFastCopy?.IsChecked == true
            ? VideoCutMode.FastLossless
            : VideoCutMode.AccurateEncode;

        string rawFmt = (FormatCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mp4";
        string fmt = rawFmt;
        if (string.Equals(rawFmt, "auto", StringComparison.OrdinalIgnoreCase))
        {
            string? srcExt = _project.Clips.FirstOrDefault() is { } first
                ? Path.GetExtension(first.SourcePath).TrimStart('.').ToLowerInvariant()
                : null;
            bool singleSource = _project.Clips.Select(c => c.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1;
            if (mode == VideoCutMode.FastLossless && singleSource && !string.IsNullOrWhiteSpace(srcExt))
                fmt = srcExt;
            else
                fmt = srcExt is "mp4" or "mov" or "mkv" or "webm" or "avi" ? srcExt : "mp4";
        }
        string resTag = (ResolutionCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Original";
        string fitTag = (AspectFitCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "FitPad";

        VideoResolutionPreset res = Enum.TryParse<VideoResolutionPreset>(resTag, out VideoResolutionPreset parsedRes)
            ? parsedRes
            : VideoResolutionPreset.Original;
        VideoAspectFitMode fit = Enum.TryParse<VideoAspectFitMode>(fitTag, out VideoAspectFitMode parsedFit)
            ? parsedFit
            : VideoAspectFitMode.FitPad;

        return (fmt, res, fit, mode);
    }

    private void UpdateExportSummaryHint()
    {
        if (ExportSummaryHint == null) return;
        var (fmt, res, fit, mode) = ReadExportSettings();

        bool multiSource = _project.Clips.Select(c => c.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        string resLabel = (ResolutionCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Original";
        if (_project.Clips.Count > 0)
        {
            var exportClips = _project.Clips
                .Select(c => new VideoMontageClip(c.SourcePath, c.Start, c.End, c.HasAudio, c.SourceWidth, c.SourceHeight, c.SourceFps))
                .ToList();
            var (outW, outH) = FFmpegService.ComputeTargetCanvas(exportClips, res);
            resLabel = res == VideoResolutionPreset.Original ? $"{outW}×{outH} (Orig)" : $"{outW}×{outH}";
        }
        string fitLabel = fit == VideoAspectFitMode.FillCrop ? "Crop Fill" : "Fit Pad";

        if (mode == VideoCutMode.FastLossless)
        {
            if (multiSource || res != VideoResolutionPreset.Original)
                ExportSummaryHint.Text = $"{fmt.ToUpperInvariant()} · {resLabel} ({fitLabel}) · Auto smart-render for mixed/scaled clips";
            else
                ExportSummaryHint.Text = $"{fmt.ToUpperInvariant()} · Original · Instant stream copy (keyframe snapped)";
        }
        else
        {
            ExportSummaryHint.Text = $"{fmt.ToUpperInvariant()} · {resLabel} · {fitLabel} · Frame-accurate";
        }

        if (ExportButtonLabel != null)
            ExportButtonLabel.Text = _project.Clips.Count > 1 ? "Export Montage" : "Export Cut";
    }

    private void UpdateAspectOverlay()
    {
        if (AspectOverlayRoot == null || AspectTargetFrame == null) return;
        if (EditorPanel.Visibility != Visibility.Visible || _project.Clips.Count == 0)
        {
            AspectOverlayRoot.Visibility = Visibility.Collapsed;
            return;
        }

        var (_, res, fit, _) = ReadExportSettings();
        double? targetRatio = res switch
        {
            VideoResolutionPreset.Vertical1080x1920 => 9.0 / 16.0,
            VideoResolutionPreset.Square1080x1080 => 1.0,
            _ => null
        };

        if (!targetRatio.HasValue)
        {
            AspectOverlayRoot.Visibility = Visibility.Collapsed;
            return;
        }

        MontageClipItem? active = _project.SelectedClip ?? _playingClip ?? _project.Clips.FirstOrDefault();
        if (active is { SourceWidth: > 0, SourceHeight: > 0 })
        {
            double srcRatio = active.SourceWidth / (double)active.SourceHeight;
            if (Math.Abs(srcRatio - targetRatio.Value) < 0.03)
            {
                AspectOverlayRoot.Visibility = Visibility.Collapsed;
                return;
            }
        }

        double hostW = PreviewMedia.ActualWidth;
        double hostH = PreviewMedia.ActualHeight;
        if (hostW <= 20 || hostH <= 20)
        {
            AspectOverlayRoot.Visibility = Visibility.Collapsed;
            return;
        }

        double boxW = hostW;
        double boxH = boxW / targetRatio.Value;
        if (boxH > hostH)
        {
            boxH = hostH;
            boxW = boxH * targetRatio.Value;
        }

        AspectTargetFrame.Width = Math.Max(40, boxW - 4);
        AspectTargetFrame.Height = Math.Max(40, boxH - 4);
        string ratioName = res == VideoResolutionPreset.Vertical1080x1920 ? "9:16" : "1:1";
        AspectOverlayLabel.Text = $"TARGET {ratioName} · {(fit == VideoAspectFitMode.FillCrop ? "CROP" : "FIT")}";
        AspectOverlayRoot.Visibility = Visibility.Visible;
    }

    // Export Execution

    private void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select output folder for exported video"
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
            var answer = MessageBox.Show("Cancel the running export and clear this project?",
                "Clear project?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        await ResetViewAsync(cancelExport: true);
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsExporting) return;

        if (_project.Clips.Count == 0)
        {
            MessageBox.Show("Add at least one video clip first.", "Nothing to export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IFFmpegService? service = ResolveFfmpegService();
        if (service == null)
        {
            MessageBox.Show("FFmpeg service is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var (fmt, res, fit, mode) = ReadExportSettings();
        var exportClips = _project.Clips.Select(c => c.ToExportClip()).ToList();

        string firstSource = exportClips[0].SourcePath;
        string outDir = _outputFolderOverride ?? Path.GetDirectoryName(firstSource)!;
        string baseName = Path.GetFileNameWithoutExtension(firstSource);
        string suffix = exportClips.Count > 1 ? "montage" : "cut";
        string outPath = Path.Combine(outDir, $"{baseName}_{suffix}.{fmt}");
        int counter = 1;
        while (File.Exists(outPath))
            outPath = Path.Combine(outDir, $"{baseName}_{suffix}_{counter++}.{fmt}");

        _lastOutputPath = outPath;
        ExportButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ResultCard.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ProgressPercentText.Text = "0%";
        ProgressStatusText.Text = mode == VideoCutMode.FastLossless
            ? "Exporting lossless cut..."
            : $"Rendering {exportClips.Count} clip{(exportClips.Count == 1 ? "" : "s")}...";

        _exportCts = new CancellationTokenSource();
        CancellationToken token = _exportCts.Token;
        var progress = new Progress<double>(p =>
        {
            ExportProgress.Value = p;
            ProgressPercentText.Text = $"{p:F0}%";
        });

        await PausePlaybackAsync();

        var request = new VideoMontageExportRequest(
            Clips: exportClips,
            OutputPath: outPath,
            OutputFormat: fmt,
            CutMode: mode,
            Resolution: res,
            AspectFitMode: fit);

        _exportTask = Task.Run(async () =>
        {
            try
            {
                await service.ExportMontageAsync(request, token, progress);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        ProgressStatusText.Text = "Cancelled";
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    }
                    else
                    {
                        ShowResult(outPath, exportClips.Count, fmt);
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
                    MessageBox.Show($"Export failed:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ProgressStatusText.Text = "Export failed";
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
        try { _exportCts?.Cancel(); } catch { }
    }

    private void ShowResult(string outputPath, int clipCount, string fmt)
    {
        if (!File.Exists(outputPath)) return;
        long size = new FileInfo(outputPath).Length;
        ResultTitle.Text = clipCount > 1 ? "Montage exported" : "Cut exported";
        ResultDetailText.Text = $"{Path.GetFileName(outputPath)} · {FormatBytes(size)} · {fmt.ToUpperInvariant()} · {clipCount} clip{(clipCount == 1 ? "" : "s")}";
        ResultCard.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Collapsed;
    }

    private void PlayOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastOutputPath) && File.Exists(_lastOutputPath))
            Process.Start(new ProcessStartInfo(_lastOutputPath) { UseShellExecute = true });
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastOutputPath) && File.Exists(_lastOutputPath))
            Process.Start("explorer.exe", $"/select,\"{_lastOutputPath}\"");
    }

    private async void CutAnother_Click(object sender, RoutedEventArgs e) => await ResetViewAsync(cancelExport: true);

    private async Task ResetViewAsync(bool cancelExport)
    {
        CancelAllAssetExtractions();
        if (cancelExport)
            CancelExportIfRunning();

        await PausePlaybackAsync();
        try { await PreviewMedia.Close(); } catch { }

        _activePlayerSourcePath = null;
        _playingClip = null;
        _playingClipIndex = -1;
        _mediaReady = false;
        _outputFolderOverride = null;
        _lastOutputPath = null;
        _montagePosition = TimeSpan.Zero;
        _zoomFactor = 1.0;
        _project.Clear();

        EmptyStatePanel.Visibility = Visibility.Visible;
        EditorPanel.Visibility = Visibility.Collapsed;
        _previewFocused = false;
        ApplyPreviewFocusState();
        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Collapsed;
        BackgroundJobBadge.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = true;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "Loading preview...";
        OutputPathText.Text = string.Empty;
        ExportProgress.Value = 0;

        RefreshSessionChrome();
        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateAspectOverlay();
        RenderTimeline();
    }

    // Keyboard Shortcuts

    private async void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (EditorPanel.Visibility != Visibility.Visible) return;
        if (e.OriginalSource is ComboBox || e.OriginalSource is ComboBoxItem) return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.K:
            case Key.Space:
                await TogglePlayPauseAsync();
                e.Handled = true;
                break;
            case Key.J:
                await SeekMontageRelativeAsync(shift ? -0.1 : -1);
                e.Handled = true;
                break;
            case Key.L:
                await SeekMontageRelativeAsync(shift ? 0.1 : 1);
                e.Handled = true;
                break;
            case Key.Left:
            case Key.OemComma:
                await StepFrameAsync(forward: false);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.OemPeriod:
                await StepFrameAsync(forward: true);
                e.Handled = true;
                break;
            case Key.V:
                SetRazorMode(false);
                e.Handled = true;
                break;
            case Key.C:
                SetRazorMode(true);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isRazorMode)
                {
                    SetRazorMode(false);
                    e.Handled = true;
                }
                else if (_previewFocused)
                {
                    _previewFocused = false;
                    ApplyPreviewFocusState();
                    e.Handled = true;
                }
                break;
            case Key.Z when ctrl:
                await ExecuteHistoryAsync(undo: !shift);
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                await ExecuteHistoryAsync(undo: false);
                e.Handled = true;
                break;
            case Key.S:
            case Key.B when ctrl:
                await SplitAtPlayheadAsync();
                e.Handled = true;
                break;
            case Key.D when ctrl:
                DuplicateSelectedOrAddSlice_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteSelectedClip_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Home:
                await SyncPlayerToMontagePositionAsync(TimeSpan.Zero, autoPlay: _isPlaying, forceExactSeek: true);
                e.Handled = true;
                break;
            case Key.End:
                await SyncPlayerToMontagePositionAsync(_project.TotalDuration, autoPlay: false, forceExactSeek: true);
                e.Handled = true;
                break;
            case Key.F:
                PreviewFocusButton_Click(sender, e);
                e.Handled = true;
                break;
            case Key.M:
                MuteToggle_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    // Helpers

    private TimeSpan ClampMontageTime(TimeSpan value) =>
        ClampTimeSpan(value, TimeSpan.Zero, _project.TotalDuration);

    private static TimeSpan ClampTimeSpan(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (max >= min && value > max) return max;
        return value;
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

    private sealed class InlineFfmpegConfig(string ffmpegPath) : IAppConfiguration
    {
        public string ResourcesDirectory => Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
        public string FFmpegPath => ffmpegPath;
        public string MagickPath => string.Empty;
        public string PdfToCairoPath => string.Empty;
        public string FFmpegLibsDirectory => Path.Combine(ResourcesDirectory, "ffmpeg_libs");
    }
}
