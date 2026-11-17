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
using CortexFX.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Unosquare.FFME.Common;

namespace CortexFX.Views.Video;

public partial class VideoCutterView : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? WorkspaceActivated;

    public static readonly DependencyProperty ProjectTileWidthProperty =
        DependencyProperty.Register(
            nameof(ProjectTileWidth),
            typeof(double),
            typeof(VideoCutterView),
            new PropertyMetadata(128.0));

    public double ProjectTileWidth
    {
        get => (double)GetValue(ProjectTileWidthProperty);
        set => SetValue(ProjectTileWidthProperty, value);
    }

    public bool HasActiveSession =>
        _project.MediaBin.Count > 0 || HasAnyTimelineClips() ||
        IsExporting || ExportResultStage.Visibility == Visibility.Visible;

    public bool IsExporting => _exportTask is { IsCompleted: false };

    private string _ffmpegPath = string.Empty;
    private IFFmpegService? _ffmpegService;
    private readonly MontageProject _project = new();
    private readonly DispatcherTimer _uiTimer;
    private DispatcherTimer? _toastTimer;

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
    private bool _isA1Muted;
    private bool _isSnapEnabled = true;
    private TimelineToolMode _activeTool = TimelineToolMode.Select;
    private bool _isUpdatingScrubSlider;
    private bool _isUpdatingZoomSlider;
    private bool _isScrubbingMonitor;
    private bool _wasPlayingBeforeScrub;
    private double _volume = 1.0;
    private double _speedRatio = 1.0;
    private double _zoomFactor = 1.0;

    private DateTime _transitionGuardUntil = DateTime.MinValue;
    private DateTime _lastGapPlaybackTickUtc = DateTime.MinValue;

    private sealed class OverlayChannelState
    {
        public string? ActiveSourcePath;
        public MontageClipItem? PlayingClip;
        public bool MediaReady;
        public bool HasPendingSeekOnOpen;
        public TimeSpan PendingSeekOnOpen;
        public bool PendingPlayOnOpen;
        public bool LoopRunning;
        public bool HasQueuedSync;
        public TimeSpan QueuedMontageTime;
        public bool QueuedAutoPlay;
        public bool QueuedForceSeek;
        public Task? ActiveSyncTask;

        public void Reset()
        {
            ActiveSourcePath = null;
            PlayingClip = null;
            MediaReady = false;
            HasPendingSeekOnOpen = false;
            PendingSeekOnOpen = TimeSpan.Zero;
            PendingPlayOnOpen = false;
            HasQueuedSync = false;
            QueuedAutoPlay = false;
            QueuedForceSeek = false;
        }
    }

    // A2 Audio Overlay & V2 Video Overlay player states (independent decoders with coalesced sync)
    private readonly OverlayChannelState _a2Channel = new();
    private readonly OverlayChannelState _v2Channel = new();
    private MontageClipItem? _playingOverlayClip => _a2Channel.PlayingClip;
    private MontageClipItem? _playingV2Clip => _v2Channel.PlayingClip;
    private bool _isOverlayMuted;
    private bool _isV2Muted;

    // Program Monitor Frame / Motion transform interaction state
    private bool _isTransformMode;
    private bool _isUpdatingTransformSliders;
    private bool _isDraggingTransformSlider;
    private string? _activeTransformHandle;
    private bool _didTransformDrag;
    private MontageClipItem? _transformDragClip;
    private Point _transformStartPt;
    private double _transformOrigScale = 1.0;
    private double _transformOrigOffX;
    private double _transformOrigOffY;

    // Media Bin drag state
    private Point _binDragStartPoint;
    private SourceMediaAsset? _binDragAsset;

    // Single-flight coalesced FFME seek coordinator (prevents seek queue lag on rapid scrubbing)
    private bool _seekLoopRunning;
    private bool _hasQueuedSeek;
    private TimeSpan _queuedSeekTarget;
    private bool _queuedAutoPlay;
    private bool _queuedExactSeek;
    private Task? _activeSeekLoopTask;

    // Timeline interaction state
    private TimelineDragMode _dragMode = TimelineDragMode.None;
    private MontageClipItem? _dragTargetClip;
    private ClipAudioVolumeHandle? _dragVolumeHandle;
    private double _dragStartX;
    private double _dragPxPerSec = 10.0;
    private double _dragOrigTotalSec = 1.0;
    private TimeSpan _dragOrigStart;
    private TimeSpan _dragOrigEnd;
    private TimeSpan _dragOrigTimelineStart;
    private int _dragDropInsertIndex = -1;

    // Audio gain strip state
    private bool _isUpdatingAudioSlider;
    private bool _isDraggingAudioSlider;
    private double _lastUnmutedClipVolume = 1.0;

    // Jog wheel state
    private bool _isDraggingJog;
    private double _jogLastX;
    private double _jogAccumPx;
    private double _jogVisualOffset;

    private CancellationTokenSource? _exportCts;
    private Task? _exportTask;

    private enum TimelineToolMode { Select, Razor, Move }
    private enum TimelineDragMode { None, ScrubPlayhead, TrimStart, TrimEnd, PendingMoveOrClick, MoveClip, DragAudioVolume }

    private static readonly double[] SpeedSteps = { 0.25, 0.5, 1.0, 1.5, 2.0 };

    private static readonly string[] SupportedExtensions =
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv",
        ".m4v", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp",
        ".3g2", ".ogv", ".vob", ".asf", ".f4v",
        ".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".opus", ".wma"
    };

    public VideoCutterView()
    {
        InitializeComponent();
        InitializeDockSystem();
        ClipsList.ItemsSource = _project.MediaBin;
        _project.HistoryChanged += (_, _) => UpdateUndoRedoUi();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += UiTimer_Tick;

        PreviewMedia.PositionChanged += PreviewMedia_PositionChanged;
        SizeChanged += (_, _) =>
        {
            UpdateAspectOverlay();
            RenderProgramCutMarkers();
        };
        Loaded += (_, _) => RenderJogTicks(_jogVisualOffset);

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshSessionChrome();
                if (EditorPanel.Visibility == Visibility.Visible)
                {
                    RenderTimeline();
                    RenderJogTicks(_jogVisualOffset);
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
        if (WorkspaceLayoutButton != null)
            WorkspaceLayoutButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PreviewFocusButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        HeaderAddClipButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        HeaderClearButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (HeaderActionDivider != null)
            HeaderActionDivider.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (ExportButton != null)
            ExportButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        HeaderReadyPill.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        BackgroundJobBadge.Visibility = IsExporting ? Visibility.Visible : Visibility.Collapsed;

        if (IsExporting)
        {
            ShowExportModalStage(ExportStage.Progress);
            if (ExportButton != null) ExportButton.IsEnabled = false;
        }
    }

    private void ShowTimelineToast(string message)
    {
        if (TimelineToastBanner == null || TimelineToastText == null) return;
        TimelineToastText.Text = message;
        TimelineToastBanner.Visibility = Visibility.Visible;

        if (_toastTimer == null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2100) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                TimelineToastBanner.Visibility = Visibility.Collapsed;
            };
        }

        _toastTimer.Stop();
        _toastTimer.Start();
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
        RebuildDockLayout();

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
        if (PreviewFocusIcon != null)
            PreviewFocusIcon.Text = _previewFocused ? "\uE73F" : "\uE740";
        PreviewFocusButton.ToolTip = _previewFocused
            ? "Return to Premiere dock layout (F)"
            : "Focus preview (F)";
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
            _ = LoadOrAppendFilesAsync(files, append: false, addToTimeline: true);
    }

    private void EditorPanel_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _ = LoadOrAppendFilesAsync(files, append: true, addToTimeline: false);
        e.Handled = true;
    }

    private void ProjectPanel_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void ProjectPanel_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _ = LoadOrAppendFilesAsync(files, append: true, addToTimeline: false);
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

    private void ImportMediaToBin_Click(object sender, RoutedEventArgs e)
    {
        OpenMediaPicker(addToTimelineAlso: !HasAnyTimelineClips() && _project.MediaBin.Count == 0);
    }

    private void AddClipVideo_Click(object sender, RoutedEventArgs e)
    {
        OpenFilePicker(appendToProject: true);
    }

    public void OpenFilePicker() =>
        OpenMediaPicker(addToTimelineAlso: !HasAnyTimelineClips() && _project.MediaBin.Count == 0);

    private void OpenFilePicker(bool appendToProject) =>
        OpenMediaPicker(addToTimelineAlso: !appendToProject || !HasAnyTimelineClips());

    private void OpenMediaPicker(bool addToTimelineAlso)
    {
        if (IsExporting)
        {
            AppDialog.ShowInfo(
                this,
                "Export in progress",
                "Finish or cancel the active export before adding or replacing media.");
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = addToTimelineAlso ? "Open Media in Montage Studio" : "Import Video or Audio to Project Bin",
            Multiselect = true,
            Filter = "All Media (Video & Audio)|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.ogv;*.mp3;*.wav;*.aac;*.m4a;*.flac;*.ogg;*.opus;*.wma|Video Files|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.ogv|Audio Files|*.mp3;*.wav;*.aac;*.m4a;*.flac;*.ogg;*.opus;*.wma|All Files|*.*"
        };

        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
            _ = LoadOrAppendFilesAsync(dlg.FileNames, append: _project.MediaBin.Count > 0 || !addToTimelineAlso, addToTimeline: addToTimelineAlso);
    }

    private Task LoadOrAppendFilesAsync(
        string[] paths,
        bool append,
        bool addToTimeline = true,
        TimeSpan? insertAtTime = null) =>
        ImportFilesCoreAsync(paths, append, addToTimeline, insertAtTime);

    private async Task ImportFilesCoreAsync(
        string[] paths,
        bool append,
        bool addToTimeline,
        TimeSpan? insertAtTime)
    {
        if (IsExporting)
        {
            AppDialog.ShowInfo(
                this,
                "Export in progress",
                "Finish or cancel the current export before changing media.");
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
                : "No valid video or audio files selected.";
            AppDialog.ShowWarning(this, "Unsupported File", msg);
            return;
        }

        await PausePlaybackAsync();

        bool wasEmpty = _project.MediaBin.Count == 0 && !HasAnyTimelineClips();
        if (!append || wasEmpty)
        {
            CancelAllAssetExtractions();
            _project.Clear();
            _outputFolderOverride = null;
            _lastOutputPath = null;
            _montagePosition = TimeSpan.Zero;
            _zoomFactor = 1.0;
            SetZoomSliderSilently(0.0);
            _previewFocused = false;
            ApplyPreviewFocusState();
        }
        else if (addToTimeline)
        {
            _project.BeginUndoStep();
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        EditorPanel.Visibility = Visibility.Visible;
        ExportModalOverlay.Visibility = Visibility.Collapsed;
        RefreshSessionChrome();
        if (!append || wasEmpty)
            WorkspaceActivated?.Invoke(this, EventArgs.Empty);

        if (string.IsNullOrEmpty(OutputPathText.Text))
            OutputPathText.Text = _outputFolderOverride ?? (Path.GetDirectoryName(validPaths[0]) ?? validPaths[0]);

        if (addToTimeline && _project.Clips.Count == 0)
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewPlaceholder.Text = "Inspecting media...";
        }

        IFFmpegService? service = ResolveFfmpegService();
        MontageClipItem? firstAdded = null;
        TimeSpan dropCursor = insertAtTime ?? TimeSpan.Zero;

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
            SelectAssetInBin(asset);

            if (addToTimeline)
            {
                MontageClipItem clip = insertAtTime.HasValue
                    ? _project.AddClipAtTime(asset, dropCursor, select: firstAdded == null)
                    : _project.AddClipFromAsset(asset, select: firstAdded == null);
                firstAdded ??= clip;
                dropCursor = clip.TimelineEnd;
            }

            StartAssetVisualExtraction(asset);
        }

        if (append && !wasEmpty && addToTimeline)
            _project.CommitUndoStep();
        else if (!append || wasEmpty)
            _project.ClearHistory();

        if (firstAdded != null)
        {
            _project.SelectClip(firstAdded);
            _montagePosition = firstAdded.TimelineStart;
            await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
        }
        else
        {
            if (!HasAnyTimelineClips())
            {
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewPlaceholder.Text = "Drag media from Project onto Timeline";
            }
            ShowTimelineToast($"Added {validPaths.Count} file{(validPaths.Count == 1 ? "" : "s")} to Project Bin — drag to Timeline");
        }

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        UpdateAspectOverlay();
        ApplyActiveFrameTransforms();
        RenderTimeline();
        RenderJogTicks(_jogVisualOffset);
        Focus();
    }

    private static VideoMediaInfo FallbackProbe(string path)
    {
        long size = File.Exists(path) ? new FileInfo(path).Length : 0;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool audioOnly = ext is ".mp3" or ".wav" or ".aac" or ".m4a" or ".flac" or ".ogg" or ".opus" or ".wma";
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
            HasVideo: !audioOnly,
            HasAudio: true,
            ContainerFormat: ext.TrimStart('.'),
            FileSizeBytes: size);
    }

    // FFME Player & Single-Flight Coalesced Synchronization

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
            await SafeSeekMediaAsync(PreviewMedia, seekTarget, skipIfNearCurrent: true);

            if (shouldPlay && _isPlaying)
            {
                await PreviewMedia.Play();
                _isPlaying = true;
                _uiTimer.Start();
                UpdatePlayPauseUi(true);
                _ = SyncOverlayVideoPlayerToMontagePositionAsync(_montagePosition, autoPlay: true, forceSeek: true);
                _ = SyncAudioOverlayPlayerToMontagePositionAsync(_montagePosition, autoPlay: true, forceSeek: true);
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
        _activePlayerSourcePath = null;
        _isPlaying = false;
        _uiTimer.Stop();
        UpdatePlayPauseUi(false);
        PreviewPlaceholder.Text = "Live preview unavailable for this codec — timeline cutting & export still work.";
        PreviewPlaceholder.Visibility = Visibility.Visible;
    }

    private Task SyncPlayerToMontagePositionAsync(
        TimeSpan montageTime,
        bool autoPlay,
        bool forceExactSeek)
    {
        if (!HasAnyTimelineClips())
            return Task.CompletedTask;

        montageTime = ClampMontageTime(montageTime);
        _montagePosition = montageTime;

        if (autoPlay)
        {
            _isPlaying = true;
            UpdatePlayPauseUi(true);
        }

        var (clip, clipIndex, _) = _project.MapMontageToSource(montageTime);
        var (v2Clip, _, _) = _project.GetActiveVideoOverlayClip(montageTime);
        if (clip != null)
        {
            _playingClip = clip;
            _playingClipIndex = clipIndex;
            if (PreviewMedia.Visibility != Visibility.Visible)
                PreviewMedia.Visibility = Visibility.Visible;
            bool v2ExplicitSelected = _project.SelectedClip != null && ReferenceEquals(_project.SelectedClip, v2Clip);
            if (!v2ExplicitSelected && !ReferenceEquals(_project.SelectedClip, clip))
                _project.SelectClip(clip);
        }
        else
        {
            _playingClip = null;
            _playingClipIndex = -1;
            if (PreviewMedia.Visibility != Visibility.Hidden)
                PreviewMedia.Visibility = Visibility.Hidden;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            try
            {
                if (_mediaReady && (PreviewMedia.MediaState == MediaPlaybackState.Play || PreviewMedia.IsPlaying))
                    _ = PreviewMedia.Pause();
            }
            catch { }

            if (v2Clip != null && _project.SelectedClip == null)
                _project.SelectClip(v2Clip);

            if (autoPlay)
            {
                _lastGapPlaybackTickUtc = DateTime.UtcNow;
                _uiTimer.Start();
            }
        }

        _ = SyncOverlayVideoPlayerToMontagePositionAsync(montageTime, autoPlay, forceSeek: true);
        _ = SyncAudioOverlayPlayerToMontagePositionAsync(montageTime, autoPlay, forceSeek: true);

        ApplyActiveFrameTransforms();
        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdatePlayheadVisual();

        if (clip == null)
            return Task.CompletedTask;

        _queuedSeekTarget = montageTime;
        _queuedAutoPlay = autoPlay;
        _queuedExactSeek |= forceExactSeek;
        _hasQueuedSeek = true;

        if (_seekLoopRunning && _activeSeekLoopTask != null)
            return _activeSeekLoopTask;

        _seekLoopRunning = true;
        _activeSeekLoopTask = RunCoalescedSeekLoopAsync();
        return _activeSeekLoopTask;
    }

    private static TimeSpan ClampMediaSeekTarget(Unosquare.FFME.MediaElement media, TimeSpan target)
    {
        TimeSpan minPos = media.PlaybackStartTime ?? TimeSpan.Zero;
        if (target < minPos)
            target = minPos;

        TimeSpan? endLimit = media.PlaybackEndTime ?? media.NaturalDuration;
        if (endLimit.HasValue && endLimit.Value > minPos + TimeSpan.FromMilliseconds(40))
        {
            TimeSpan maxPos = endLimit.Value - TimeSpan.FromMilliseconds(20);
            if (target > maxPos)
                target = maxPos;
        }

        return target;
    }

    private static async Task SafeSeekMediaAsync(
        Unosquare.FFME.MediaElement media,
        TimeSpan target,
        bool skipIfNearCurrent = false)
    {
        TimeSpan safeTarget = ClampMediaSeekTarget(media, target);
        if (skipIfNearCurrent)
        {
            if (media.MediaState == MediaPlaybackState.Stop &&
                Math.Abs(((media.PlaybackStartTime ?? TimeSpan.Zero) - safeTarget).TotalMilliseconds) < 35)
                return;

            if (media.MediaState != MediaPlaybackState.Stop &&
                Math.Abs((media.Position - safeTarget).TotalMilliseconds) < 30)
                return;
        }

        try { await media.Seek(safeTarget); }
        catch { try { media.Position = safeTarget; } catch { } }
    }

    private void OverlayVideoMedia_MediaOpening(object? sender, MediaOpeningEventArgs e)
    {
        e.Options.IsSubtitleDisabled = true;
        e.Options.UseParallelDecoding = true;
        e.Options.UseParallelRendering = true;
    }

    private async void OverlayVideoMedia_MediaOpened(object? sender, MediaOpenedEventArgs e)
    {
        await HandleOverlayChannelMediaOpenedAsync(
            _v2Channel,
            OverlayVideoMedia,
            resolveSourceTime: () =>
            {
                var (v2, _, src) = _project.GetActiveVideoOverlayClip(_montagePosition);
                return (v2 != null && !_isV2Muted, src);
            },
            afterOpened: () =>
            {
                if (V2TransformHost != null)
                    V2TransformHost.Visibility = _v2Channel.PlayingClip != null && !_isV2Muted
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                ApplyActiveFrameTransforms();
            });
    }

    private void OverlayVideoMedia_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        _v2Channel.MediaReady = false;
        _v2Channel.ActiveSourcePath = null;
        _v2Channel.PlayingClip = null;
    }

    private Task SyncOverlayVideoPlayerToMontagePositionAsync(
        TimeSpan montageTime,
        bool autoPlay,
        bool forceSeek = false)
    {
        if (OverlayVideoMedia == null || V2TransformHost == null)
            return Task.CompletedTask;

        return QueueOverlayChannelSyncAsync(
            _v2Channel,
            OverlayVideoMedia,
            montageTime,
            autoPlay,
            forceSeek,
            resolveClip: t =>
            {
                var (v2Clip, _, sourceTime) = _project.GetActiveVideoOverlayClip(t);
                return (v2Clip, sourceTime, _isV2Muted);
            },
            onActiveChanged: active =>
            {
                V2TransformHost.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                if (active) PreviewPlaceholder.Visibility = Visibility.Collapsed;
                ApplyActiveFrameTransforms();
            });
    }

    private void AudioOverlayMedia_MediaOpening(object? sender, MediaOpeningEventArgs e)
    {
        e.Options.IsSubtitleDisabled = true;
        e.Options.IsVideoDisabled = true;
        e.Options.UseParallelDecoding = true;
    }

    private async void AudioOverlayMedia_MediaOpened(object? sender, MediaOpenedEventArgs e)
    {
        await HandleOverlayChannelMediaOpenedAsync(
            _a2Channel,
            AudioOverlayMedia,
            resolveSourceTime: () =>
            {
                var (ov, _, src) = _project.GetActiveAudioOverlayClip(_montagePosition);
                return (ov != null, src);
            },
            afterOpened: null);
    }

    private void AudioOverlayMedia_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        _a2Channel.MediaReady = false;
        _a2Channel.ActiveSourcePath = null;
        _a2Channel.PlayingClip = null;
    }

    private Task SyncAudioOverlayPlayerToMontagePositionAsync(
        TimeSpan montageTime,
        bool autoPlay,
        bool forceSeek = false)
    {
        if (AudioOverlayMedia == null)
            return Task.CompletedTask;

        return QueueOverlayChannelSyncAsync(
            _a2Channel,
            AudioOverlayMedia,
            montageTime,
            autoPlay,
            forceSeek,
            resolveClip: t =>
            {
                var (ovClip, _, sourceTime) = _project.GetActiveAudioOverlayClip(t);
                return (ovClip, sourceTime, false);
            },
            onActiveChanged: null);
    }

    private async Task HandleOverlayChannelMediaOpenedAsync(
        OverlayChannelState channel,
        Unosquare.FFME.MediaElement media,
        Func<(bool IsActive, TimeSpan SourceTime)> resolveSourceTime,
        Action? afterOpened)
    {
        channel.MediaReady = true;
        ApplyPlayerAudioAndSpeed();

        if (channel.HasPendingSeekOnOpen)
        {
            channel.HasPendingSeekOnOpen = false;
            TimeSpan seekTarget = channel.PendingSeekOnOpen;
            bool shouldPlay = channel.PendingPlayOnOpen;
            channel.PendingPlayOnOpen = false;

            if (_isPlaying)
            {
                var (active, currentSourceTime) = resolveSourceTime();
                if (active)
                {
                    seekTarget = currentSourceTime;
                    shouldPlay = true;
                }
            }

            await SafeSeekMediaAsync(media, seekTarget, skipIfNearCurrent: true);

            try
            {
                if (shouldPlay && _isPlaying)
                    await media.Play();
                else
                    await media.Pause();
            }
            catch { }
        }
        else if (_isPlaying)
        {
            var (active, currentSourceTime) = resolveSourceTime();
            if (active)
            {
                await SafeSeekMediaAsync(media, currentSourceTime, skipIfNearCurrent: true);
                try { await media.Play(); } catch { }
            }
        }

        afterOpened?.Invoke();
    }

    private Task QueueOverlayChannelSyncAsync(
        OverlayChannelState channel,
        Unosquare.FFME.MediaElement media,
        TimeSpan montageTime,
        bool autoPlay,
        bool forceSeek,
        Func<TimeSpan, (MontageClipItem? Clip, TimeSpan SourceTime, bool Suppressed)> resolveClip,
        Action<bool>? onActiveChanged)
    {
        channel.QueuedMontageTime = montageTime;
        channel.QueuedAutoPlay = autoPlay;
        channel.QueuedForceSeek |= forceSeek;
        channel.HasQueuedSync = true;

        if (channel.LoopRunning && channel.ActiveSyncTask != null)
            return channel.ActiveSyncTask;

        channel.LoopRunning = true;
        channel.ActiveSyncTask = RunCoalescedOverlayChannelLoopAsync(channel, media, resolveClip, onActiveChanged);
        return channel.ActiveSyncTask;
    }

    private async Task RunCoalescedOverlayChannelLoopAsync(
        OverlayChannelState channel,
        Unosquare.FFME.MediaElement media,
        Func<TimeSpan, (MontageClipItem? Clip, TimeSpan SourceTime, bool Suppressed)> resolveClip,
        Action<bool>? onActiveChanged)
    {
        try
        {
            while (channel.HasQueuedSync)
            {
                channel.HasQueuedSync = false;
                TimeSpan targetMontage = channel.QueuedMontageTime;
                bool autoPlay = channel.QueuedAutoPlay;
                bool forceSeek = channel.QueuedForceSeek;
                channel.QueuedForceSeek = false;

                var (clip, sourceTime, suppressed) = resolveClip(targetMontage);
                if (clip == null || suppressed)
                {
                    channel.PlayingClip = clip;
                    onActiveChanged?.Invoke(false);
                    try
                    {
                        if (channel.MediaReady && (media.MediaState == MediaPlaybackState.Play || media.IsPlaying))
                            await media.Pause();
                    }
                    catch { }
                    continue;
                }

                bool switchedClip = !ReferenceEquals(channel.PlayingClip, clip);
                channel.PlayingClip = clip;
                onActiveChanged?.Invoke(true);
                ApplyPlayerAudioAndSpeed();

                bool needOpen = !media.IsOpen ||
                    !string.Equals(channel.ActiveSourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase);
                if (needOpen)
                {
                    channel.ActiveSourcePath = clip.SourcePath;
                    channel.MediaReady = false;
                    channel.PendingSeekOnOpen = sourceTime;
                    channel.HasPendingSeekOnOpen = true;
                    channel.PendingPlayOnOpen = autoPlay;
                    try
                    {
                        bool opened = await media.Open(new Uri(clip.SourcePath));
                        if (!opened)
                            channel.ActiveSourcePath = null;
                    }
                    catch
                    {
                        channel.MediaReady = false;
                        channel.ActiveSourcePath = null;
                    }
                    continue;
                }

                if (!channel.MediaReady)
                {
                    channel.PendingSeekOnOpen = sourceTime;
                    channel.HasPendingSeekOnOpen = true;
                    channel.PendingPlayOnOpen = autoPlay;
                    continue;
                }

                if (forceSeek || switchedClip)
                    await SafeSeekMediaAsync(media, sourceTime, skipIfNearCurrent: true);

                bool effectivePlay = autoPlay && _isPlaying;
                try
                {
                    if (effectivePlay && media.MediaState != MediaPlaybackState.Play)
                        await media.Play();
                    else if (!effectivePlay && (media.MediaState == MediaPlaybackState.Play || media.IsPlaying))
                        await media.Pause();
                }
                catch { }
            }
        }
        finally
        {
            channel.LoopRunning = false;
        }
    }

    private async Task RunCoalescedSeekLoopAsync()
    {
        try
        {
            while (_hasQueuedSeek && _project.Clips.Count > 0)
            {
                _hasQueuedSeek = false;
                TimeSpan targetMontage = _queuedSeekTarget;
                bool autoPlay = _queuedAutoPlay;
                _queuedExactSeek = false;

                var (clip, clipIndex, sourceTime) = _project.MapMontageToSource(targetMontage);
                if (clip == null) break;

                _playingClip = clip;
                _playingClipIndex = clipIndex;
                ApplyPlayerAudioAndSpeed();

                bool needOpen = !PreviewMedia.IsOpen ||
                    !string.Equals(_activePlayerSourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase);

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
                        _activePlayerSourcePath = null;
                        PreviewPlaceholder.Text = "Live preview unavailable — export still works.";
                        PreviewPlaceholder.Visibility = Visibility.Visible;
                    }
                }
                else if (!_mediaReady)
                {
                    _pendingSeekOnOpen = sourceTime;
                    _hasPendingSeekOnOpen = true;
                    _pendingPlayOnOpen = autoPlay;
                }
                else
                {
                    _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(180);
                    await SafeSeekMediaAsync(PreviewMedia, sourceTime, skipIfNearCurrent: false);

                    if (autoPlay && _isPlaying && PreviewMedia.MediaState != MediaPlaybackState.Play)
                    {
                        await PreviewMedia.Play();
                        _isPlaying = true;
                        _uiTimer.Start();
                        UpdatePlayPauseUi(true);
                    }
                }
            }
        }
        finally
        {
            _seekLoopRunning = false;
        }
    }

    private void PreviewMedia_PositionChanged(object? sender, PositionChangedEventArgs e)
    {
        if (!_isPlaying || (_dragMode != TimelineDragMode.None && _dragMode != TimelineDragMode.DragAudioVolume)) return;
        if (DateTime.UtcNow < _transitionGuardUntil) return;
        if (_playingClip == null) return;
        HandleActivePlaybackPosition(e.Position);
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isPlaying || (_dragMode != TimelineDragMode.None && _dragMode != TimelineDragMode.DragAudioVolume)) return;
        if (DateTime.UtcNow < _transitionGuardUntil) return;

        if (_playingClip == null)
        {
            HandleGapOrOverlayPlaybackTick();
            return;
        }

        if (!_mediaReady) return;
        HandleActivePlaybackPosition(PreviewMedia.Position);
    }

    private void HandleGapOrOverlayPlaybackTick()
    {
        DateTime now = DateTime.UtcNow;
        double dt = _lastGapPlaybackTickUtc == DateTime.MinValue
            ? 0.033
            : Math.Clamp((now - _lastGapPlaybackTickUtc).TotalSeconds, 0.005, 0.15);
        _lastGapPlaybackTickUtc = now;

        _montagePosition += TimeSpan.FromSeconds(dt * _speedRatio);
        if (_montagePosition >= _project.TotalDuration - TimeSpan.FromMilliseconds(35))
        {
            if (LoopToggle.IsChecked == true && _project.TotalDuration > TimeSpan.Zero)
            {
                _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(220);
                _ = SyncPlayerToMontagePositionAsync(TimeSpan.Zero, autoPlay: true, forceExactSeek: true);
            }
            else
            {
                _montagePosition = _project.TotalDuration;
                _ = PausePlaybackAsync();
                UpdateTimeLabels();
                UpdatePlayheadVisual();
            }
            return;
        }

        var (enteredV1, _, _) = _project.MapMontageToSource(_montagePosition);
        if (enteredV1 != null)
        {
            _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(220);
            _ = SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: true, forceExactSeek: true);
            return;
        }

        EnsureOverlayPlayersSyncedDuringPlayback();

        UpdateTimeLabels();
        UpdatePlayheadVisual();
    }

    private void EnsureOverlayPlayersSyncedDuringPlayback()
    {
        var (activeV2, _, _) = _project.GetActiveVideoOverlayClip(_montagePosition);
        bool v2NeedSync = !ReferenceEquals(activeV2, _v2Channel.PlayingClip);
        if (!v2NeedSync && activeV2 != null && !_isV2Muted && _v2Channel.MediaReady && !_v2Channel.LoopRunning &&
            OverlayVideoMedia != null && OverlayVideoMedia.MediaState != MediaPlaybackState.Play)
        {
            v2NeedSync = true;
        }
        if (v2NeedSync)
            _ = SyncOverlayVideoPlayerToMontagePositionAsync(_montagePosition, autoPlay: true, forceSeek: true);

        var (activeOv, _, ovSourceTime) = _project.GetActiveAudioOverlayClip(_montagePosition);
        bool ovNeedSync = !ReferenceEquals(activeOv, _a2Channel.PlayingClip);
        bool ovForceSeek = ovNeedSync;
        if (!ovNeedSync && activeOv != null && _a2Channel.MediaReady && !_a2Channel.LoopRunning && AudioOverlayMedia != null)
        {
            TimeSpan expected = ClampMediaSeekTarget(AudioOverlayMedia, ovSourceTime);
            double driftMs = Math.Abs((AudioOverlayMedia.Position - expected).TotalMilliseconds);
            if (AudioOverlayMedia.MediaState != MediaPlaybackState.Play)
            {
                ovNeedSync = true;
                ovForceSeek = driftMs > 150;
            }
            else if (driftMs > 280)
            {
                ovNeedSync = true;
                ovForceSeek = true;
            }
        }
        if (ovNeedSync)
            _ = SyncAudioOverlayPlayerToMontagePositionAsync(_montagePosition, autoPlay: true, forceSeek: ovForceSeek);
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

        EnsureOverlayPlayersSyncedDuringPlayback();

        if (sourcePos >= clip.End - TimeSpan.FromMilliseconds(35))
        {
            int nextIndex = _playingClipIndex + 1;
            if (nextIndex >= 0 && nextIndex < _project.Clips.Count)
            {
                MontageClipItem nextClip = _project.Clips[nextIndex];
                if (nextClip.TimelineStart - clip.TimelineEnd > TimeSpan.FromMilliseconds(45))
                {
                    _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(180);
                    _montagePosition = clip.TimelineEnd;
                    _playingClip = null;
                    _playingClipIndex = -1;
                    _lastGapPlaybackTickUtc = DateTime.UtcNow;
                    if (PreviewMedia.Visibility != Visibility.Hidden)
                        PreviewMedia.Visibility = Visibility.Hidden;
                    try { if (_mediaReady && PreviewMedia.IsPlaying) _ = PreviewMedia.Pause(); } catch { }
                    MonitorMediaHudText.Text = $"GAP · {MontageProject.FormatShort(nextClip.TimelineStart - clip.TimelineEnd)}";
                    _uiTimer.Start();
                }
                else
                {
                    _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(260);
                    _ = SyncPlayerToMontagePositionAsync(nextClip.TimelineStart, autoPlay: true, forceExactSeek: true);
                }
            }
            else if (_project.TotalDuration - clip.TimelineEnd > TimeSpan.FromMilliseconds(45))
            {
                _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(180);
                _montagePosition = clip.TimelineEnd;
                _playingClip = null;
                _playingClipIndex = -1;
                _lastGapPlaybackTickUtc = DateTime.UtcNow;
                if (PreviewMedia.Visibility != Visibility.Hidden)
                    PreviewMedia.Visibility = Visibility.Hidden;
                try { if (_mediaReady && PreviewMedia.IsPlaying) _ = PreviewMedia.Pause(); } catch { }
                MonitorMediaHudText.Text = "A2 Audio · Black";
                _uiTimer.Start();
            }
            else if (LoopToggle.IsChecked == true && _project.TotalDuration > TimeSpan.Zero)
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

    // Transport, Jog Wheel & Frame Stepping

    private void MonitorSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeTransformHandle != null)
        {
            CommitProgramTransformDrag();
            e.Handled = true;
            return;
        }

        if (_didTransformDrag)
        {
            _didTransformDrag = false;
            return;
        }

        if (_isTransformMode && e.OriginalSource is DependencyObject dep &&
            FindAncestorTag<string>(dep) != null)
            return;

        if (EditorPanel.Visibility == Visibility.Visible && HasAnyTimelineClips())
            _ = TogglePlayPauseAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await TogglePlayPauseAsync();

    private async Task TogglePlayPauseAsync()
    {
        if (!HasAnyTimelineClips()) return;

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
        _queuedAutoPlay = false;
        _v2Channel.PendingPlayOnOpen = false;
        _v2Channel.QueuedAutoPlay = false;
        _a2Channel.PendingPlayOnOpen = false;
        _a2Channel.QueuedAutoPlay = false;
        _uiTimer.Stop();

        static async Task SafePauseAsync(Unosquare.FFME.MediaElement? media, bool ready)
        {
            if (media == null || !ready) return;
            try
            {
                if (media.MediaState == MediaPlaybackState.Play || media.IsPlaying)
                    await media.Pause();
            }
            catch { }
        }

        await SafePauseAsync(PreviewMedia, _mediaReady);
        await SafePauseAsync(OverlayVideoMedia, _v2Channel.MediaReady);
        await SafePauseAsync(AudioOverlayMedia, _a2Channel.MediaReady);
        UpdatePlayPauseUi(false);
    }

    private void UpdatePlayPauseUi(bool playing)
    {
        PlayPauseIcon.Text = playing ? "\uE769" : "\uE768";
    }

    private async void StepBackFrame_Click(object sender, RoutedEventArgs e) =>
        await StepFrameAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -5 : -1);

    private async void StepForwardFrame_Click(object sender, RoutedEventArgs e) =>
        await StepFrameAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1);

    private async Task StepFrameAsync(int frameDelta)
    {
        if (!HasAnyTimelineClips() || frameDelta == 0) return;
        await PausePlaybackAsync();

        double fps = _playingClip?.SourceFps > 1 ? _playingClip.SourceFps : _project.SequenceFps;
        fps = Math.Clamp(fps, 12.0, 120.0);
        double frameDur = 1.0 / fps;

        TimeSpan snappedBase = MontageProject.SnapToFrame(_montagePosition, fps);
        TimeSpan target = ClampMontageTime(snappedBase + TimeSpan.FromSeconds(frameDelta * frameDur));

        // Guard against frame rounding returning the exact same timestamp.
        if (Math.Abs((target - _montagePosition).TotalSeconds) < frameDur * 0.25)
            target = ClampMontageTime(_montagePosition + TimeSpan.FromSeconds(frameDelta * frameDur));

        _jogVisualOffset += frameDelta * 7.0;
        RenderJogTicks(_jogVisualOffset);

        await SyncPlayerToMontagePositionAsync(target, autoPlay: false, forceExactSeek: true);
        EnsurePlayheadVisible();
    }

    private void JogWheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasAnyTimelineClips()) return;
        _isDraggingJog = true;
        _jogLastX = e.GetPosition(JogWheelBorder).X;
        _jogAccumPx = 0;
        _ = PausePlaybackAsync();
        JogWheelBorder.CaptureMouse();
        JogWheelBorder.BorderBrush = BrushFrom("#2D8CFF");
        e.Handled = true;
    }

    private void JogWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingJog) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndJogDrag();
            return;
        }

        double x = e.GetPosition(JogWheelBorder).X;
        double dx = x - _jogLastX;
        _jogLastX = x;
        _jogVisualOffset += dx;
        RenderJogTicks(_jogVisualOffset);

        _jogAccumPx += dx;
        const double pxPerFrameStep = 7.0;
        if (Math.Abs(_jogAccumPx) >= pxPerFrameStep)
        {
            int steps = (int)(_jogAccumPx / pxPerFrameStep);
            _jogAccumPx -= steps * pxPerFrameStep;
            _ = StepFrameAsync(steps);
        }
    }

    private void JogWheel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingJog)
            EndJogDrag();
        e.Handled = true;
    }

    private void JogWheel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasAnyTimelineClips()) return;
        int steps = e.Delta > 0 ? 1 : -1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            steps *= 5;
        _ = StepFrameAsync(steps);
        e.Handled = true;
    }

    private void EndJogDrag()
    {
        _isDraggingJog = false;
        JogWheelBorder.BorderBrush = BrushFrom("#353945");
        if (JogWheelBorder.IsMouseCaptured)
            JogWheelBorder.ReleaseMouseCapture();
    }

    private async void NudgeBack_Click(object sender, RoutedEventArgs e) => await SeekMontageRelativeAsync(-1);

    private async void NudgeForward_Click(object sender, RoutedEventArgs e) => await SeekMontageRelativeAsync(1);

    private async Task SeekMontageRelativeAsync(double seconds)
    {
        if (_project.TotalDuration <= TimeSpan.Zero) return;
        TimeSpan target = ClampMontageTime(_montagePosition + TimeSpan.FromSeconds(seconds));
        await SyncPlayerToMontagePositionAsync(target, autoPlay: _isPlaying, forceExactSeek: true);
        EnsurePlayheadVisible();
    }

    private void CycleSpeed_Click(object sender, RoutedEventArgs e)
    {
        int idx = Array.FindIndex(SpeedSteps, s => Math.Abs(s - _speedRatio) < 0.05);
        _speedRatio = SpeedSteps[(idx + 1) % SpeedSteps.Length];
        SpeedButton.Content = $"{_speedRatio:0.##}×";
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
        _isA1Muted = TrackMuteButton?.IsChecked == true;
        SyncMuteUi();
        ApplyPlayerAudioAndSpeed();
        RenderTimeline();
    }

    private void TrackV2Mute_Click(object sender, RoutedEventArgs e)
    {
        _isV2Muted = TrackV2MuteButton?.IsChecked == true;
        _ = SyncOverlayVideoPlayerToMontagePositionAsync(_montagePosition, autoPlay: _isPlaying, forceSeek: true);
        ApplyPlayerAudioAndSpeed();
        RenderTimeline();
    }

    private void TrackA2Mute_Click(object sender, RoutedEventArgs e)
    {
        _isOverlayMuted = TrackA2MuteButton?.IsChecked == true;
        ApplyPlayerAudioAndSpeed();
        RenderTimeline();
    }

    private void AudioTrackA2Badge_Click(object sender, MouseButtonEventArgs e)
    {
        MontageClipItem? firstOv = _project.AudioOverlayClips.FirstOrDefault();
        if (firstOv != null)
            _project.SelectClip(firstOv);
        SetAudioTrackExpanded(!_isAudioExpanded);
        e.Handled = true;
    }

    private void SyncMuteUi()
    {
        if (MuteIcon != null)
            MuteIcon.Text = _isMuted ? "\uE74F" : "\uE767";
        if (TrackMuteButton != null)
            TrackMuteButton.IsChecked = _isA1Muted;
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

    private void ApplyChannelAudioAndSpeed(Unosquare.FFME.MediaElement? media, double clipGain, bool trackOrMasterMuted)
    {
        if (media == null) return;
        try
        {
            bool targetMuted = trackOrMasterMuted || clipGain <= 0.005;
            double targetVol = Math.Clamp(_volume * clipGain, 0.0, 1.0);

            if (media.IsMuted != targetMuted)
                media.IsMuted = targetMuted;
            if (Math.Abs(media.Volume - targetVol) > 0.003)
                media.Volume = targetVol;
            if (Math.Abs(media.SpeedRatio - _speedRatio) > 0.003)
                media.SpeedRatio = _speedRatio;
        }
        catch { }
    }

    private void ApplyPlayerAudioAndSpeed()
    {
        ApplyChannelAudioAndSpeed(
            PreviewMedia,
            _playingClip?.AudioVolume ?? 1.0,
            _isMuted || _isA1Muted);

        if (_a2Channel.MediaReady)
        {
            ApplyChannelAudioAndSpeed(
                AudioOverlayMedia,
                _a2Channel.PlayingClip?.AudioVolume ?? 1.0,
                _isMuted || _isOverlayMuted);
        }

        if (_v2Channel.MediaReady)
        {
            ApplyChannelAudioAndSpeed(
                OverlayVideoMedia,
                _v2Channel.PlayingClip?.AudioVolume ?? 1.0,
                _isMuted || _isV2Muted);
        }
    }

    // Project Bin Grid / List View & A1 Audio Control Strip

    internal void SetProjectBinViewMode(bool gridView)
    {
        if (ClipsList == null) return;
        ClipsList.ItemsPanel = (ItemsPanelTemplate)FindResource(gridView ? "ProjectWrapItemsPanel" : "ProjectStackItemsPanel");
        ClipsList.ItemTemplate = (DataTemplate)FindResource(gridView ? "ProjectTileTemplate" : "ProjectListTemplate");

        if (ProjectGridViewBtn != null) ProjectGridViewBtn.IsChecked = gridView;
        if (ProjectListViewBtn != null) ProjectListViewBtn.IsChecked = !gridView;
        if (ProjectTileSizeSlider != null)
            ProjectTileSizeSlider.Visibility = gridView ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProjectGridViewBtn_Click(object sender, RoutedEventArgs e) => SetProjectBinViewMode(gridView: true);

    private void ProjectListViewBtn_Click(object sender, RoutedEventArgs e) => SetProjectBinViewMode(gridView: false);

    private void ProjectTileSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        ProjectTileWidth = Math.Clamp(e.NewValue, 88.0, 220.0);
    }

    private void AudioTrackBadge_Click(object sender, MouseButtonEventArgs e)
    {
        SetAudioTrackExpanded(!_isAudioExpanded);
        e.Handled = true;
    }

    private void AudioStripToggle_Click(object sender, RoutedEventArgs e) =>
        SetAudioTrackExpanded(AudioStripToggle?.IsChecked == true);

    private void AudioStripClose_Click(object sender, RoutedEventArgs e) =>
        SetAudioTrackExpanded(false);

    private void OpenAudioControlForClip(MontageClipItem clip)
    {
        _project.SelectClip(clip);
        SetAudioTrackExpanded(true);
        SyncAudioGainStripUi();
    }

    private void ResetClipAudioVolume(MontageClipItem clip)
    {
        if (_project.SetClipAudioVolume(clip, 1.0, recordUndo: true))
        {
            RefreshClipAudioBlockVisual(clip, highlightActive: false);
            SyncAudioGainStripUi();
            ApplyPlayerAudioAndSpeed();
            ShowTimelineToast($"Audio reset 0.0 dB · Clip #{clip.OrderIndex}");
        }
    }

    private MontageClipItem? GetActiveAudioStripClip() =>
        _project.SelectedClip ?? _playingClip ?? _playingV2Clip ?? _playingOverlayClip
        ?? _project.Clips.FirstOrDefault() ?? _project.AudioOverlayClips.FirstOrDefault();

    private void SyncAudioGainStripUi()
    {
        if (AudioGainStrip == null) return;
        MontageClipItem? clip = GetActiveAudioStripClip();
        if (clip == null)
        {
            if (AudioGainClipTitle != null) AudioGainClipTitle.Text = "No clip";
            if (AudioGainSlider != null) AudioGainSlider.IsEnabled = false;
            if (AudioClipMuteBtn != null) AudioClipMuteBtn.IsEnabled = false;
            return;
        }

        if (AudioGainClipTitle != null)
            AudioGainClipTitle.Text = $"#{clip.OrderIndex} · {clip.SourceFileName}";

        bool hasAudio = clip.HasAudio;
        if (AudioGainSlider != null) AudioGainSlider.IsEnabled = hasAudio;
        if (AudioClipMuteBtn != null) AudioClipMuteBtn.IsEnabled = hasAudio;
        UpdateAudioGainReadout(clip);
    }

    private void UpdateAudioGainReadout(MontageClipItem clip)
    {
        if (AudioGainSlider != null)
        {
            _isUpdatingAudioSlider = true;
            AudioGainSlider.Value = Math.Clamp(clip.AudioVolume * 100.0, AudioGainSlider.Minimum, AudioGainSlider.Maximum);
            _isUpdatingAudioSlider = false;
        }

        if (AudioClipMuteBtn != null)
            AudioClipMuteBtn.IsChecked = clip.AudioVolume <= 0.005;

        if (AudioGainDbText != null)
        {
            int pct = (int)Math.Round(clip.AudioVolume * 100.0);
            AudioGainDbText.Text = $"{clip.AudioVolumeDbLabel} · {pct}%";
            AudioGainDbText.Foreground = clip.AudioVolume <= 0.005
                ? BrushFrom("#FCA5A5")
                : clip.IsAudioAdjusted ? BrushFrom("#FDE68A") : BrushFrom("#7CE2F7");
        }
    }

    private void AudioClipMuteBtn_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? clip = GetActiveAudioStripClip();
        if (clip == null || !clip.HasAudio) return;

        double target;
        if (clip.AudioVolume > 0.005)
        {
            _lastUnmutedClipVolume = clip.AudioVolume;
            target = 0.0;
        }
        else
        {
            target = Math.Clamp(_lastUnmutedClipVolume, 0.25, 2.0);
        }

        _project.SetClipAudioVolume(clip, target, recordUndo: true);
        RefreshClipAudioBlockVisual(clip);
        UpdateAudioGainReadout(clip);
        ApplyPlayerAudioAndSpeed();
        ShowTimelineToast(target <= 0.005 ? $"Muted Clip #{clip.OrderIndex}" : $"Audio {clip.AudioVolumeDbLabel} · Clip #{clip.OrderIndex}");
    }

    private void AudioGainSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasAnyTimelineClips()) return;
        _isDraggingAudioSlider = true;
        _project.BeginUndoStep();
    }

    private void AudioGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingAudioSlider || !IsInitialized) return;
        MontageClipItem? clip = GetActiveAudioStripClip();
        if (clip == null || !clip.HasAudio) return;

        double sliderPct = e.NewValue;
        if (_isDraggingAudioSlider && Math.Abs(sliderPct - 100.0) <= 3.0)
        {
            sliderPct = 100.0;
            _isUpdatingAudioSlider = true;
            AudioGainSlider.Value = 100.0;
            _isUpdatingAudioSlider = false;
        }

        double linear = Math.Clamp(sliderPct / 100.0, 0.0, 2.0);
        if (linear > 0.05) _lastUnmutedClipVolume = linear;

        _project.SetClipAudioVolume(clip, linear, recordUndo: false);
        RefreshClipAudioBlockVisual(clip, highlightActive: _isDraggingAudioSlider);
        UpdateAudioGainReadout(clip);
        ApplyPlayerAudioAndSpeed();
    }

    private void AudioGainSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndAudioSliderDrag();

    private void AudioGainSlider_LostMouseCapture(object sender, MouseEventArgs e) => EndAudioSliderDrag();

    private void EndAudioSliderDrag()
    {
        if (!_isDraggingAudioSlider) return;
        _isDraggingAudioSlider = false;
        _project.CommitUndoStep();
        MontageClipItem? clip = GetActiveAudioStripClip();
        if (clip != null)
            RefreshClipAudioBlockVisual(clip, highlightActive: false);
    }

    private void AudioPresetDb_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? clip = GetActiveAudioStripClip();
        if (clip == null || !clip.HasAudio) return;

        double db = 0.0;
        if ((sender as FrameworkElement)?.Tag is string raw &&
            double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            db = parsed;

        double linear = Math.Clamp(Math.Pow(10.0, db / 20.0), 0.0, 2.0);
        if (Math.Abs(db) < 0.05) linear = 1.0;
        if (linear > 0.05) _lastUnmutedClipVolume = linear;

        _project.SetClipAudioVolume(clip, linear, recordUndo: true);
        RefreshClipAudioBlockVisual(clip);
        UpdateAudioGainReadout(clip);
        ApplyPlayerAudioAndSpeed();
        ShowTimelineToast($"Clip #{clip.OrderIndex} Audio → {clip.AudioVolumeDbLabel}");
    }

    // Montage Editing Actions (Split, Trim Q/W, Restore, Duplicate, Reorder, Delete)

    private async void SplitAtPlayhead_Click(object sender, RoutedEventArgs e) => await SplitAtPlayheadAsync();

    private async Task SplitAtPlayheadAsync(bool audioOverlayTrack = false, bool videoOverlayTrack = false)
    {
        bool splitAudioOv = audioOverlayTrack || (!videoOverlayTrack && _project.SelectedClip?.IsAudioOverlay == true && _project.AudioOverlayClips.Contains(_project.SelectedClip));
        bool splitVideoOv = videoOverlayTrack || (!audioOverlayTrack && _project.SelectedClip?.IsVideoOverlay == true && _project.VideoOverlayClips.Contains(_project.SelectedClip));
        if (!HasAnyTimelineClips()) return;
        await PausePlaybackAsync();

        TimeSpan snapped = MontageProject.SnapToFrame(_montagePosition, _project.SequenceFps);
        var (ok, second) = _project.SplitAtMontageTime(snapped, audioOverlayTrack: splitAudioOv, videoOverlayTrack: splitVideoOv);
        if (!ok || second == null)
            (ok, second) = _project.SplitAtMontageTime(_montagePosition, audioOverlayTrack: splitAudioOv, videoOverlayTrack: splitVideoOv);

        if (!ok || second == null)
        {
            AppDialog.ShowInfo(
                this,
                "Cannot split here",
                "Move the playhead at least 2 frames away from clip edges to split.");
            return;
        }

        _montagePosition = second.TimelineStart;
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
        EnsurePlayheadVisible();
        ShowTimelineToast($"Split → Clip #{second.OrderIndex - 1} & #{second.OrderIndex}");
    }

    private async Task RestoreClipBoundsAsync(MontageClipItem clip)
    {
        await PausePlaybackAsync();
        if (!_project.RestoreClipBounds(clip))
            return;

        _montagePosition = clip.TimelineStart;
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
        UpdateExportSummaryHint();
        RenderTimeline();
        ShowTimelineToast($"Restored Clip #{clip.OrderIndex} full range");
    }

    private void DuplicateSelectedOrAddSlice_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? target = _project.SelectedClip ?? _playingClip ?? _playingV2Clip ?? _project.Clips.FirstOrDefault() ?? _project.VideoOverlayClips.FirstOrDefault() ?? _project.AudioOverlayClips.FirstOrDefault();
        if (target == null) return;
        DuplicateClipItem(target);
    }

    private void DuplicateClipItem(MontageClipItem target)
    {
        MontageClipItem? copy = _project.DuplicateClip(target);
        if (copy == null) return;

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
        ShowTimelineToast($"Duplicated → Clip #{copy.OrderIndex}");
    }

    private async void DeleteSelectedClip_Click(object sender, RoutedEventArgs e)
    {
        bool ripple = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        MontageClipItem? target = _project.SelectedClip ?? _playingClip;
        if (target == null) return;
        await RemoveClipWithUiRefreshAsync(target, ripple);
    }

    // Decoupled Project Media Bin Selection, Context Menu, and Drag-to-Timeline

    private void SelectAssetInBin(SourceMediaAsset? asset)
    {
        foreach (SourceMediaAsset item in _project.MediaBin)
            item.IsSelected = ReferenceEquals(item, asset);
    }

    private void MediaBinCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SourceMediaAsset asset) return;
        _binDragStartPoint = e.GetPosition(null);
        _binDragAsset = asset;
        SelectAssetInBin(asset);
    }

    private void MediaBinCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _binDragAsset == null)
            return;
        if (e.OriginalSource is DependencyObject dep && FindAncestor<Button>(dep) != null)
            return;

        Point pt = e.GetPosition(null);
        if (Math.Abs(pt.X - _binDragStartPoint.X) < 6 && Math.Abs(pt.Y - _binDragStartPoint.Y) < 6)
            return;

        SourceMediaAsset asset = _binDragAsset;
        _binDragAsset = null;

        var data = new DataObject();
        data.SetData("CortexFxMediaAsset", asset);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T typed) return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async void MediaBinCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _binDragAsset = null;
        if ((sender as FrameworkElement)?.DataContext is not SourceMediaAsset asset) return;
        SelectAssetInBin(asset);

        if (e.ClickCount == 2)
            await InsertAssetAtTimelinePositionAsync(asset, _project.Clips.Count == 0 && !asset.IsAudioOnly ? TimeSpan.Zero : _montagePosition);
    }

    private void MediaBinCard_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SourceMediaAsset asset) return;
        SelectAssetInBin(asset);

        var menu = new ContextMenu { Style = (Style)FindResource("StudioContextMenu") };
        MenuItem MakeItem(string header, Action action)
        {
            var mi = new MenuItem { Header = header, Style = (Style)FindResource("StudioMenuItem") };
            mi.Click += (_, _) => action();
            return mi;
        }

        menu.Items.Add(MakeItem("Insert on Timeline at Playhead", () => _ = InsertAssetAtTimelinePositionAsync(asset, _montagePosition)));
        menu.Items.Add(MakeItem("Append to Timeline End", () => _ = InsertAssetAtTimelinePositionAsync(asset, _project.TotalDuration)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("StudioMenuSeparator") });
        menu.Items.Add(MakeItem("Remove from Project Bin", () => _ = RemoveMediaAssetWithPromptAsync(asset)));

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void MediaBinRemove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not SourceMediaAsset asset) return;
        await RemoveMediaAssetWithPromptAsync(asset);
    }

    private async Task RemoveMediaAssetWithPromptAsync(SourceMediaAsset asset)
    {
        int usedCount = _project.Clips.Count(c => string.Equals(c.SourcePath, asset.FilePath, StringComparison.OrdinalIgnoreCase)) +
                        _project.VideoOverlayClips.Count(c => string.Equals(c.SourcePath, asset.FilePath, StringComparison.OrdinalIgnoreCase)) +
                        _project.AudioOverlayClips.Count(c => string.Equals(c.SourcePath, asset.FilePath, StringComparison.OrdinalIgnoreCase));

        if (usedCount > 0)
        {
            bool confirm = AppDialog.Confirm(
                this,
                "Remove media from Project?",
                $"Removing '{asset.FileName}' will also remove {usedCount} clip{(usedCount == 1 ? "" : "s")} from the timeline.",
                confirmText: "Remove Media",
                cancelText: "Keep Media",
                tone: DialogTone.Warning);
            if (!confirm) return;
        }

        if (_project.MediaBin.Count <= 1)
        {
            await ResetViewAsync(cancelExport: true);
            return;
        }

        await PausePlaybackAsync();
        CancelAssetExtraction(asset.FilePath);
        _project.RemoveAsset(asset, removeTimelineInstances: true);

        _montagePosition = ClampMontageTime(_montagePosition);
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
        ShowTimelineToast($"Removed {asset.FileName} from Project Bin");
    }

    private async Task InsertAssetAtTimelinePositionAsync(
        SourceMediaAsset asset,
        TimeSpan desiredTime,
        bool forceOverlayTrack = false,
        bool targetVideoOverlay = false)
    {
        await PausePlaybackAsync();
        bool targetAudioOv = forceOverlayTrack || asset.IsAudioOnly;
        bool targetVideoOv = !targetAudioOv && targetVideoOverlay;
        MontageClipItem added = _project.AddClipAtTime(
            asset,
            desiredTime,
            select: true,
            recordUndo: true,
            targetAudioOverlay: targetAudioOv,
            targetVideoOverlay: targetVideoOv);
        _montagePosition = added.TimelineStart;
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        UpdateAspectOverlay();
        ApplyActiveFrameTransforms();
        RenderTimeline();
        EnsurePlayheadVisible();
        string destTrack = added.IsAudioOverlay ? "A2" : (added.IsVideoOverlay ? "V2 Overlay" : "V1/A1");
        ShowTimelineToast($"Placed {destTrack} at {MontageProject.FormatShort(added.TimelineStart)}");
    }

    private void Timeline_DragOver(object sender, DragEventArgs e)
    {
        bool isAsset = e.Data.GetDataPresent("CortexFxMediaAsset");
        bool isFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
        if (!isAsset && !isFileDrop)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        Point pos = e.GetPosition(TimelineSurface);
        double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
        double pxPerSec = Math.Max(4.0, _currentTimelinePxPerSec);
        TimeSpan rawTime = TimeSpan.FromSeconds(Math.Max(0, pos.X / pxPerSec));
        if (_project.TotalDuration <= TimeSpan.Zero && rawTime.TotalSeconds < 2.0)
            rawTime = TimeSpan.Zero;

        TimeSpan snapped = ApplyTimelineSnap(rawTime, totalW, Math.Max(0.1, _project.TotalDuration.TotalSeconds), includePlayhead: true);
        double snapX = snapped.TotalSeconds * pxPerSec;

        bool isAudioOnly = isAsset && (e.Data.GetData("CortexFxMediaAsset") as SourceMediaAsset)?.IsAudioOnly == true;
        bool dropToV2 = !isAudioOnly && pos.Y < TimelineRulerHeight + TimelineV2RowHeight;
        double previewW = isAsset && e.Data.GetData("CortexFxMediaAsset") is SourceMediaAsset asset
            ? Math.Clamp(asset.Info.Duration.TotalSeconds * pxPerSec, 18, Math.Max(48, totalW * 0.45))
            : 84;

        Canvas.SetLeft(DropInsertionLine, Math.Clamp(snapX, 0, Math.Max(0, totalW - 2.5)));
        DropInsertionLine.Visibility = Visibility.Visible;

        MoveSlotPreviewRect.Width = previewW;
        Canvas.SetLeft(MoveSlotPreviewRect, snapX);
        if (isAudioOnly)
        {
            Canvas.SetTop(MoveSlotPreviewRect, TimelineRulerHeight + TimelineV2RowHeight + TimelineV1RowHeight + GetAudioTrackRowHeight() + 3.0);
            MoveSlotPreviewRect.Height = 42.0;
        }
        else if (dropToV2)
        {
            Canvas.SetTop(MoveSlotPreviewRect, TimelineRulerHeight + 3.0);
            MoveSlotPreviewRect.Height = 64.0;
        }
        else
        {
            Canvas.SetTop(MoveSlotPreviewRect, TimelineRulerHeight + TimelineV2RowHeight + 3.0);
            MoveSlotPreviewRect.Height = Math.Max(110, TimelineV1RowHeight + GetAudioTrackRowHeight() - 6.0);
        }
        MoveSlotPreviewRect.Opacity = 1;
        MoveSlotPreviewRect.Visibility = Visibility.Visible;

        string dropTrack = isAudioOnly ? "A2" : (dropToV2 ? "V2" : "V1");
        ShowDragBadge($"+ DROP {dropTrack} @ {MontageProject.FormatShort(snapped)}", snapX, totalW);
        e.Handled = true;
    }

    private void Timeline_DragLeave(object sender, DragEventArgs e)
    {
        if (_dragMode == TimelineDragMode.None)
        {
            DropInsertionLine.Visibility = Visibility.Collapsed;
            MoveSlotPreviewRect.Visibility = Visibility.Collapsed;
            TimelineDragBadge.Visibility = Visibility.Collapsed;
        }
        e.Handled = true;
    }

    private void Timeline_Drop(object sender, DragEventArgs e)
    {
        DropInsertionLine.Visibility = Visibility.Collapsed;
        MoveSlotPreviewRect.Visibility = Visibility.Collapsed;
        TimelineDragBadge.Visibility = Visibility.Collapsed;

        Point pos = e.GetPosition(TimelineSurface);
        double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
        double pxPerSec = Math.Max(4.0, _currentTimelinePxPerSec);
        TimeSpan rawTime = TimeSpan.FromSeconds(Math.Max(0, pos.X / pxPerSec));
        if (_project.TotalDuration <= TimeSpan.Zero && rawTime.TotalSeconds < 2.0)
            rawTime = TimeSpan.Zero;
        TimeSpan snapped = ApplyTimelineSnap(rawTime, totalW, Math.Max(0.1, _project.TotalDuration.TotalSeconds), includePlayhead: true);

        if (e.Data.GetData("CortexFxMediaAsset") is SourceMediaAsset asset)
        {
            bool dropToV2 = !asset.IsAudioOnly && pos.Y < TimelineRulerHeight + TimelineV2RowHeight;
            _ = InsertAssetAtTimelinePositionAsync(asset, snapped, forceOverlayTrack: asset.IsAudioOnly, targetVideoOverlay: dropToV2);
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _ = LoadOrAppendFilesAsync(files, append: true, addToTimeline: true, insertAtTime: snapped);
            e.Handled = true;
        }
    }

    private void MoveClipRelative(MontageClipItem clip, int offset)
    {
        bool moved = offset < 0 ? _project.MoveClipEarlier(clip) : _project.MoveClipLater(clip);
        if (!moved) return;

        _montagePosition = clip.TimelineStart;
        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        RenderTimeline();
        EnsurePlayheadVisible();
        ShowTimelineToast($"Moved to Clip #{clip.OrderIndex}");
    }

    private async Task RemoveClipWithUiRefreshAsync(MontageClipItem clip, bool ripple = false)
    {
        await PausePlaybackAsync();
        TimeSpan preservePos = _montagePosition;
        _project.RemoveClip(clip, recordUndo: true, ripple: ripple);

        if (_project.TotalDuration <= TimeSpan.Zero)
        {
            _activePlayerSourcePath = null;
            _playingClip = null;
            _playingClipIndex = -1;
            _mediaReady = false;
            _v2Channel.Reset();
            _a2Channel.Reset();
            if (V2TransformHost != null) V2TransformHost.Visibility = Visibility.Collapsed;
            try { await PreviewMedia.Close(); } catch { }
            try { if (OverlayVideoMedia != null) await OverlayVideoMedia.Close(); } catch { }
            try { if (AudioOverlayMedia != null) await AudioOverlayMedia.Close(); } catch { }
            _montagePosition = TimeSpan.Zero;
            ApplyActiveFrameTransforms();
            PreviewPlaceholder.Text = "Timeline empty — Drag media from Project Bin";
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            _montagePosition = ClampMontageTime(preservePos);
            await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
        }

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
        ShowTimelineToast(ripple ? "Ripple deleted clip" : "Deleted clip (gap preserved)");
    }

    // Timeline Zoom, Scroll & Tool Selection

    private void TimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EditorPanel.Visibility == Visibility.Visible)
            UpdateTimelineLayoutInPlace();
    }

    private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (EditorPanel.Visibility != Visibility.Visible || !HasAnyTimelineClips())
            return;
        if (Math.Abs(e.HorizontalChange) < 1.0)
            return;

        double viewLeft = TimelineScrollViewer.HorizontalOffset;
        double viewW = GetTimelineViewportWidth();
        double viewRight = viewLeft + viewW;

        bool approachingRenderedEdge =
            (_renderedMinX > 2 && viewLeft < _renderedMinX + 100) ||
            (_renderedMaxX < _renderedTotalWidth - 2 && viewRight > _renderedMaxX - 100);

        if (approachingRenderedEdge)
            RenderTimeline();
        else
            ScheduleViewportFrameRefinement();
    }

    private void TimelineScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double viewW = GetTimelineViewportWidth();
            double prevPxPerSec = Math.Max(1.0, _currentTimelinePxPerSec);
            double totalSec = Math.Max(0.1, _project.TotalDuration.TotalSeconds);
            double mouseX = e.GetPosition(TimelineSurface).X;
            double mouseOffsetInViewport = e.GetPosition(TimelineScrollViewer).X;
            double timeAtCursor = Math.Clamp(mouseX / prevPxPerSec, 0, totalSec);

            double deltaSlider = e.Delta > 0 ? 7.5 : -7.5;
            double newSliderVal = Math.Clamp(ZoomSlider.Value + deltaSlider, ZoomSlider.Minimum, ZoomSlider.Maximum);
            _zoomFactor = SliderToZoomFactor(newSliderVal, viewW);
            SetZoomSliderSilently(newSliderVal);

            UpdateTimelineLayoutInPlace();

            double newPxPerSec = Math.Max(1.0, _currentTimelinePxPerSec);
            double newCursorX = timeAtCursor * newPxPerSec;
            if (_zoomFactor > 1.01)
                TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newCursorX - mouseOffsetInViewport));
            else
                TimelineScrollViewer.ScrollToHorizontalOffset(0);

            e.Handled = true;
            return;
        }

        if (_zoomFactor > 1.01)
        {
            TimelineScrollViewer.ScrollToHorizontalOffset(TimelineScrollViewer.HorizontalOffset - e.Delta * 0.65);
            e.Handled = true;
        }
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomBySliderDelta(-10.0, anchorPlayhead: true);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomBySliderDelta(10.0, anchorPlayhead: true);

    private void ZoomBySliderDelta(double delta, bool anchorPlayhead)
    {
        double targetVal = Math.Clamp(ZoomSlider.Value + delta, ZoomSlider.Minimum, ZoomSlider.Maximum);
        ZoomSlider.Value = targetVal;
        if (anchorPlayhead)
            EnsurePlayheadVisible();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        ZoomSlider.Value = 0.0;
        TimelineScrollViewer.ScrollToHorizontalOffset(0);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingZoomSlider) return;
        double viewW = GetTimelineViewportWidth();
        _zoomFactor = SliderToZoomFactor(e.NewValue, viewW);
        if (IsInitialized && EditorPanel.Visibility == Visibility.Visible)
        {
            UpdateTimelineLayoutInPlace();
            EnsurePlayheadVisible();
        }
    }

    private void SetZoomSliderSilently(double value)
    {
        if (ZoomSlider == null) return;
        _isUpdatingZoomSlider = true;
        ZoomSlider.Value = Math.Clamp(value, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _isUpdatingZoomSlider = false;
    }

    private void SnapToggle_Click(object sender, RoutedEventArgs e)
    {
        _isSnapEnabled = SnapToggle?.IsChecked == true;
        ShowTimelineToast(_isSnapEnabled ? "Snapping ON" : "Snapping OFF");
    }

    private void SelectTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(TimelineToolMode.Select);
    private void RazorTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(TimelineToolMode.Razor);
    private void MoveTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(TimelineToolMode.Move);

    private void SetActiveTool(TimelineToolMode tool)
    {
        _activeTool = tool;
        if (SelectToolBtn != null) SelectToolBtn.IsChecked = tool == TimelineToolMode.Select;
        if (RazorToolBtn != null) RazorToolBtn.IsChecked = tool == TimelineToolMode.Razor;
        if (MoveToolBtn != null) MoveToolBtn.IsChecked = tool == TimelineToolMode.Move;

        if (TimelineSurface != null)
        {
            TimelineSurface.Cursor = tool switch
            {
                TimelineToolMode.Razor => Cursors.Cross,
                TimelineToolMode.Move => Cursors.SizeAll,
                _ => Cursors.Arrow
            };
        }

        if (RazorModeHint != null && TimelineSummaryPanel != null)
        {
            if (tool == TimelineToolMode.Select)
            {
                RazorModeHint.Visibility = Visibility.Collapsed;
                TimelineSummaryPanel.Visibility = Visibility.Visible;
            }
            else
            {
                RazorModeHint.Visibility = Visibility.Visible;
                TimelineSummaryPanel.Visibility = Visibility.Collapsed;
                if (ToolModeHintText != null)
                {
                    ToolModeHintText.Text = tool switch
                    {
                        TimelineToolMode.Razor => "Razor (C) · Click clip to cut · Esc",
                        TimelineToolMode.Move => "Reorder (A) · Drag clip left/right · Esc",
                        _ => string.Empty
                    };
                }
            }
        }

        if (tool != TimelineToolMode.Razor && RazorGuideLine != null)
            RazorGuideLine.Visibility = Visibility.Collapsed;
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
        _montagePosition = _playingClip != null ? ClampMontageTime(_montagePosition) : TimeSpan.Zero;
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        SyncAudioGainStripUi();
        ApplyActiveFrameTransforms();
        ApplyPlayerAudioAndSpeed();
        RenderTimeline();
        ShowTimelineToast(undo ? "Undone" : "Redone");
    }

    // Direct Timeline Manipulation (Ruler Scrub, Edge Trim, Drag Reorder, Slip, Razor Cut)

    private TimeSpan ApplyTimelineSnap(TimeSpan rawTime, double totalWidth, double totalSec, bool includePlayhead)
    {
        if (!_isSnapEnabled || totalWidth <= 0)
            return rawTime;

        double pxPerSec = _currentTimelinePxPerSec > 0 ? _currentTimelinePxPerSec : (totalSec > 0 ? totalWidth / totalSec : 12.0);
        double thresholdSec = 8.0 / Math.Max(1.0, pxPerSec);
        TimeSpan best = rawTime;
        double bestDist = thresholdSec;

        void Consider(TimeSpan candidate)
        {
            double d = Math.Abs((candidate - rawTime).TotalSeconds);
            if (d < bestDist)
            {
                bestDist = d;
                best = candidate;
            }
        }

        Consider(TimeSpan.Zero);
        if (_project.TotalDuration > TimeSpan.Zero)
            Consider(_project.TotalDuration);
        if (includePlayhead)
            Consider(_montagePosition);

        foreach (MontageClipItem c in _project.Clips.Concat(_project.VideoOverlayClips).Concat(_project.AudioOverlayClips))
        {
            Consider(c.TimelineStart);
            Consider(c.TimelineEnd);
        }

        return best;
    }

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_project.TotalDuration <= TimeSpan.Zero || TimelineSurface.ActualWidth <= 0) return;

        Point pos = e.GetPosition(TimelineSurface);
        double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
        double totalSec = Math.Max(0.05, _project.TotalDuration.TotalSeconds);
        double pxPerSec = _currentTimelinePxPerSec > 0 ? _currentTimelinePxPerSec : (totalW / totalSec);
        bool inRuler = pos.Y <= TimelineRulerHeight;

        // Ruler clicks always scrub playhead regardless of active tool.
        if (inRuler)
        {
            StartScrubPlayhead(pos.X, totalW, totalSec);
            e.Handled = true;
            return;
        }

        if (_activeTool == TimelineToolMode.Razor)
        {
            TimeSpan rawSplit = TimeSpan.FromSeconds(Math.Clamp(pos.X / pxPerSec, 0, totalSec));
            TimeSpan snapped = ApplyTimelineSnap(rawSplit, totalW, totalSec, includePlayhead: true);
            snapped = MontageProject.SnapToFrame(snapped, _project.SequenceFps);
            bool clickedV2 = pos.Y >= TimelineRulerHeight && pos.Y < TimelineRulerHeight + TimelineV2RowHeight;
            bool clickedA2 = pos.Y >= TimelineRulerHeight + TimelineV2RowHeight + TimelineV1RowHeight + GetAudioTrackRowHeight();

            _ = PausePlaybackAsync();
            var (ok, second) = _project.SplitAtMontageTime(snapped, audioOverlayTrack: clickedA2, videoOverlayTrack: clickedV2);
            if (ok && second != null)
            {
                _montagePosition = second.TimelineStart;
                _ = SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
                UpdateExportSummaryHint();
                RenderTimeline();
                ShowTimelineToast($"Split → Clip #{second.OrderIndex - 1} & #{second.OrderIndex}");
            }
            else
            {
                ShowTimelineToast("Too close to clip edge to split");
            }
            e.Handled = true;
            return;
        }

        DependencyObject? source = e.OriginalSource as DependencyObject;

        ClipAudioStripTrigger? audioTrigger = FindAncestorTag<ClipAudioStripTrigger>(source);
        if (audioTrigger != null)
        {
            OpenAudioControlForClip(audioTrigger.Clip);
            RenderTimeline();
            e.Handled = true;
            return;
        }

        ClipEdgeHandle? edgeHandle = _activeTool == TimelineToolMode.Select
            ? FindAncestorTag<ClipEdgeHandle>(source)
            : null;
        if (edgeHandle != null)
        {
            _dragMode = edgeHandle.IsStart ? TimelineDragMode.TrimStart : TimelineDragMode.TrimEnd;
            _dragTargetClip = edgeHandle.Clip;
            _dragStartX = pos.X;
            _dragPxPerSec = pxPerSec;
            _dragOrigTotalSec = totalSec;
            _dragOrigStart = edgeHandle.Clip.Start;
            _dragOrigEnd = edgeHandle.Clip.End;
            _dragOrigTimelineStart = edgeHandle.Clip.TimelineStart;
            _project.SelectClip(edgeHandle.Clip);
            _project.BeginUndoStep();
            _ = PausePlaybackAsync();
            TimelineSurface.CaptureMouse();
            e.Handled = true;
            return;
        }

        ClipAudioVolumeHandle? volumeHandle = _activeTool == TimelineToolMode.Select
            ? FindAncestorTag<ClipAudioVolumeHandle>(source)
            : null;
        if (volumeHandle != null)
        {
            _project.SelectClip(volumeHandle.Clip);
            SyncAudioGainStripUi();
            if (e.ClickCount == 2)
            {
                ResetClipAudioVolume(volumeHandle.Clip);
                e.Handled = true;
                return;
            }

            _dragMode = TimelineDragMode.DragAudioVolume;
            _dragVolumeHandle = volumeHandle;
            _dragTargetClip = volumeHandle.Clip;
            _dragOrigTotalSec = totalSec;
            _project.BeginUndoStep();
            TimelineSurface.CaptureMouse();
            ProcessAudioVolumeDrag(e);
            e.Handled = true;
            return;
        }

        MontageClipItem? hitClip = FindAncestorTag<MontageClipItem>(source);
        ClipMoveGrip? moveGrip = FindAncestorTag<ClipMoveGrip>(source);
        if (hitClip != null && (_activeTool == TimelineToolMode.Move || moveGrip != null))
        {
            _dragMode = TimelineDragMode.PendingMoveOrClick;
            _dragTargetClip = hitClip;
            _dragStartX = pos.X;
            _dragPxPerSec = pxPerSec;
            _dragDropInsertIndex = _project.Clips.IndexOf(hitClip);
            _project.SelectClip(hitClip);
            _ = PausePlaybackAsync();
            TimelineSurface.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (hitClip != null)
        {
            _project.SelectClip(hitClip);
            ApplyActiveFrameTransforms();
        }

        StartScrubPlayhead(pos.X, totalW, totalSec);
        e.Handled = true;
    }

    private void StartScrubPlayhead(double x, double totalW, double totalSec)
    {
        _dragMode = TimelineDragMode.ScrubPlayhead;
        _ = PausePlaybackAsync();
        TimelineSurface.CaptureMouse();
        ProcessScrubDrag(x, totalW, totalSec, finalSeek: false);
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(TimelineSurface);
        double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
        double totalSec = Math.Max(0.05, _project.TotalDuration.TotalSeconds);

        if (_dragMode == TimelineDragMode.None)
        {
            UpdateTimelineHoverFeedback(pos, totalW, totalSec);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTimelineDrag(pos.X);
            return;
        }

        switch (_dragMode)
        {
            case TimelineDragMode.ScrubPlayhead:
                ProcessScrubDrag(pos.X, totalW, totalSec, finalSeek: false);
                break;

            case TimelineDragMode.TrimStart:
            case TimelineDragMode.TrimEnd:
                ProcessTrimDrag(pos.X, totalW, totalSec);
                break;

            case TimelineDragMode.PendingMoveOrClick:
                if (Math.Abs(pos.X - _dragStartX) >= 6.0 && _dragTargetClip != null)
                {
                    _dragMode = TimelineDragMode.MoveClip;
                    BeginMoveClipAnimation(_dragTargetClip, _dragStartX, pos.Y, totalW, totalSec);
                    UpdateMoveClipAnimation(pos.X, pos.Y, totalW);
                }
                break;

            case TimelineDragMode.MoveClip:
                UpdateMoveClipAnimation(pos.X, pos.Y, totalW);
                break;

            case TimelineDragMode.DragAudioVolume:
                ProcessAudioVolumeDrag(e);
                break;
        }
    }

    private void UpdateTimelineHoverFeedback(Point pos, double totalW, double totalSec)
    {
        if (_activeTool == TimelineToolMode.Razor && RazorGuideLine != null)
        {
            if (pos.Y > 28.0 && totalSec > 0)
            {
                double pxPerSec = Math.Max(1.0, _currentTimelinePxPerSec);
                TimeSpan raw = TimeSpan.FromSeconds(Math.Clamp(pos.X / pxPerSec, 0, totalSec));
                TimeSpan snapped = ApplyTimelineSnap(raw, totalW, totalSec, includePlayhead: true);
                double snappedX = snapped.TotalSeconds * pxPerSec;
                RazorGuideLine.X1 = snappedX;
                RazorGuideLine.X2 = snappedX;
                RazorGuideLine.Visibility = Visibility.Visible;

                double fps = _project.SequenceFps;
                long frameIdx = MontageProject.TimeToFrameIndex(snapped, fps);
                ShowDragBadge($"✂ {MontageProject.FormatSmpteTimecode(snapped, fps)} · F{frameIdx}", snappedX, totalW);
            }
            else
            {
                RazorGuideLine.Visibility = Visibility.Collapsed;
                TimelineDragBadge.Visibility = Visibility.Collapsed;
            }
        }
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
        if (_dragMode == TimelineDragMode.None && TimelineDragBadge != null)
            TimelineDragBadge.Visibility = Visibility.Collapsed;
        if (_dragMode != TimelineDragMode.None && e.LeftButton != MouseButtonState.Pressed)
            EndTimelineDrag(e.GetPosition(TimelineSurface).X);
    }

    private void Timeline_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragMode != TimelineDragMode.None)
            EndTimelineDrag(Mouse.GetPosition(TimelineSurface).X);
    }

    private void EndTimelineDrag(double x)
    {
        double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
        double totalSec = Math.Max(0.05, _project.TotalDuration.TotalSeconds);
        TimelineDragMode finishedMode = _dragMode;

        switch (finishedMode)
        {
            case TimelineDragMode.ScrubPlayhead:
                ProcessScrubDrag(x, totalW, totalSec, finalSeek: true);
                break;

            case TimelineDragMode.TrimStart:
            case TimelineDragMode.TrimEnd:
                ProcessTrimDrag(x, totalW, totalSec);
                _project.CommitUndoStep();
                if (_dragTargetClip != null)
                {
                    TimeSpan targetTime = finishedMode == TimelineDragMode.TrimStart
                        ? _dragTargetClip.TimelineStart
                        : ClampMontageTime(_dragTargetClip.TimelineEnd - TimeSpan.FromMilliseconds(15));
                    _ = SyncPlayerToMontagePositionAsync(targetTime, autoPlay: false, forceExactSeek: true);
                    ShowTimelineToast($"Trimmed Clip #{_dragTargetClip.OrderIndex} ({_dragTargetClip.DisplayDuration})");
                }
                break;

            case TimelineDragMode.PendingMoveOrClick:
                ProcessScrubDrag(x, totalW, totalSec, finalSeek: true);
                break;

            case TimelineDragMode.MoveClip:
                if (_dragTargetClip != null)
                {
                    bool moved = FinishMoveClipAnimation(_dragTargetClip);
                    if (moved)
                    {
                        _montagePosition = _dragTargetClip.TimelineStart;
                        _ = SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
                        ShowTimelineToast($"Moved to {MontageProject.FormatShort(_dragTargetClip.TimelineStart)}");
                    }
                    _dragMode = TimelineDragMode.None;
                    _dragTargetClip = null;
                    _dragDropInsertIndex = -1;
                    TimelineDragBadge.Visibility = Visibility.Collapsed;
                    if (TimelineSurface.IsMouseCaptured)
                        TimelineSurface.ReleaseMouseCapture();
                    UpdateProjectHeaderAndHud();
                    UpdateTimeLabels();
                    UpdateExportSummaryHint();
                    return;
                }
                break;

            case TimelineDragMode.DragAudioVolume:
                _project.CommitUndoStep();
                MontageClipItem? volClip = _dragVolumeHandle?.Clip;
                _dragMode = TimelineDragMode.None;
                _dragVolumeHandle = null;
                _dragTargetClip = null;
                TimelineDragBadge.Visibility = Visibility.Collapsed;
                if (TimelineSurface.IsMouseCaptured)
                    TimelineSurface.ReleaseMouseCapture();
                if (volClip != null)
                {
                    RefreshClipAudioBlockVisual(volClip, highlightActive: false);
                    SyncAudioGainStripUi();
                    ApplyPlayerAudioAndSpeed();
                    ShowTimelineToast($"Clip #{volClip.OrderIndex} Audio → {volClip.AudioVolumeDbLabel}");
                }
                return;
        }

        _dragMode = TimelineDragMode.None;
        _dragTargetClip = null;
        _dragVolumeHandle = null;
        _dragDropInsertIndex = -1;
        DropInsertionLine.Visibility = Visibility.Collapsed;
        MoveSlotPreviewRect.Visibility = Visibility.Collapsed;
        TimelineDragBadge.Visibility = Visibility.Collapsed;
        if (TimelineSurface.IsMouseCaptured)
            TimelineSurface.ReleaseMouseCapture();

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
    }

    private void ProcessAudioVolumeDrag(MouseEventArgs e)
    {
        if (_dragVolumeHandle == null) return;
        MontageClipItem clip = _dragVolumeHandle.Clip;
        double localY = e.GetPosition(_dragVolumeHandle.WaveHost).Y;
        double vol = LineYToVolume(localY, _dragVolumeHandle.WaveHeight);
        if (Math.Abs(vol - 1.0) <= 0.045)
            vol = 1.0;

        if (vol > 0.05) _lastUnmutedClipVolume = vol;

        _project.SetClipAudioVolume(clip, vol, recordUndo: false);
        RefreshClipAudioBlockVisual(clip, highlightActive: true);
        SyncAudioGainStripUi();
        ApplyPlayerAudioAndSpeed();

        double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
        double posX = e.GetPosition(TimelineSurface).X;
        int pct = (int)Math.Round(clip.AudioVolume * 100.0);
        ShowDragBadge($"VOL {clip.AudioVolumeDbLabel} · {pct}%", Math.Clamp(posX, 4, totalW), totalW);
    }

    private void ProcessScrubDrag(double x, double totalW, double totalSec, bool finalSeek)
    {
        double pxPerSec = Math.Max(1.0, _currentTimelinePxPerSec);
        TimeSpan raw = TimeSpan.FromSeconds(Math.Clamp(x / pxPerSec, 0, totalSec));
        TimeSpan target = ApplyTimelineSnap(raw, totalW, totalSec, includePlayhead: false);
        double fps = _project.SequenceFps;
        if (finalSeek || pxPerSec > 20.0)
            target = MontageProject.SnapToFrame(target, fps);

        double badgeX = target.TotalSeconds * pxPerSec;
        long frameIdx = MontageProject.TimeToFrameIndex(target, fps);
        ShowDragBadge($"{MontageProject.FormatSmpteTimecode(target, fps)} · F{frameIdx}", badgeX, totalW);
        _ = SyncPlayerToMontagePositionAsync(target, autoPlay: false, forceExactSeek: finalSeek);
    }

    private void ProcessTrimDrag(double x, double totalW, double totalSec)
    {
        MontageClipItem? clip = _dragTargetClip;
        if (clip == null) return;

        double fps = clip.SourceFps > 1 ? clip.SourceFps : _project.SequenceFps;
        double pxPerSec = Math.Max(1.0, _dragPxPerSec);
        double deltaSec = (x - _dragStartX) / pxPerSec;
        bool isStart = _dragMode == TimelineDragMode.TrimStart;

        if (isStart)
        {
            TimeSpan candidate = MontageProject.SnapToFrame(_dragOrigStart + TimeSpan.FromSeconds(deltaSec), fps);
            if (_isSnapEnabled)
            {
                TimeSpan snapPlayheadTarget = _dragOrigStart + (_montagePosition - _dragOrigTimelineStart);
                if (Math.Abs((candidate - snapPlayheadTarget).TotalSeconds) <= 8.0 / pxPerSec)
                    candidate = snapPlayheadTarget;
            }

            _project.TrimClipStart(clip, candidate, recordUndo: false);
            UpdateTrimVisualInPlace(clip, isStart: true, pxPerSec);

            TimeSpan delta = clip.Start - _dragOrigStart;
            string sign = delta >= TimeSpan.Zero ? "+" : "-";
            double edgeX = clip.TimelineStart.TotalSeconds * pxPerSec;
            ShowDragBadge($"IN {MontageProject.FormatSmpteTimecode(clip.Start, fps)} ({sign}{MontageProject.FormatShort(delta.Duration())})", edgeX, totalW);
            _ = SyncPlayerToMontagePositionAsync(clip.TimelineStart, autoPlay: false, forceExactSeek: false);
        }
        else
        {
            TimeSpan candidate = MontageProject.SnapToFrame(_dragOrigEnd + TimeSpan.FromSeconds(deltaSec), fps);
            if (_isSnapEnabled)
            {
                TimeSpan snapPlayheadTarget = clip.Start + (_montagePosition - clip.TimelineStart);
                if (Math.Abs((candidate - snapPlayheadTarget).TotalSeconds) <= 8.0 / pxPerSec)
                    candidate = snapPlayheadTarget;
            }

            _project.TrimClipEnd(clip, candidate, recordUndo: false);
            UpdateTrimVisualInPlace(clip, isStart: false, pxPerSec);

            TimeSpan delta = clip.End - _dragOrigEnd;
            string sign = delta >= TimeSpan.Zero ? "+" : "-";
            double edgeX = clip.TimelineEnd.TotalSeconds * pxPerSec;
            ShowDragBadge($"OUT {MontageProject.FormatSmpteTimecode(clip.End, fps)} ({sign}{MontageProject.FormatShort(delta.Duration())})", edgeX, totalW);

            TimeSpan previewEnd = ClampMontageTime(clip.TimelineEnd - TimeSpan.FromMilliseconds(10));
            _ = SyncPlayerToMontagePositionAsync(previewEnd, autoPlay: false, forceExactSeek: false);
        }

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
    }

    private void ShowDragBadge(string text, double x, double totalW)
    {
        TimelineDragBadgeText.Text = text;
        TimelineDragBadge.Visibility = Visibility.Visible;
        double estWidth = Math.Max(104, text.Length * 6.5 + 16);
        double badgeLeft = Math.Clamp(x + 10, 4, Math.Max(4, totalW - estWidth));
        Canvas.SetLeft(TimelineDragBadge, badgeLeft);
    }

    private void UpdatePlayheadVisual()
    {
        double totalW = TimelineSurface.Width > 0 ? TimelineSurface.Width : TimelineSurface.ActualWidth;
        double pxPerSec = _currentTimelinePxPerSec;
        if (totalW <= 0 || pxPerSec <= 0) return;

        double x = _project.TotalDuration > TimeSpan.Zero
            ? Math.Clamp(_montagePosition.TotalSeconds * pxPerSec, 0, totalW)
            : 0;
        double capW = PlayheadCap.Width > 0 ? PlayheadCap.Width : 13.0;
        Canvas.SetLeft(PlayheadCap, Math.Clamp(x - capW / 2.0, 0, Math.Max(0, totalW - capW)));
        Canvas.SetLeft(PlayheadLine, Math.Clamp(x - PlayheadLine.Width / 2.0, 0, Math.Max(0, totalW - PlayheadLine.Width)));
    }

    private void EnsurePlayheadVisible()
    {
        if (_zoomFactor <= 1.01) return;
        double pxPerSec = _currentTimelinePxPerSec;
        if (pxPerSec <= 0 || _project.TotalDuration <= TimeSpan.Zero) return;

        double x = _montagePosition.TotalSeconds * pxPerSec;
        double left = TimelineScrollViewer.HorizontalOffset;
        double viewW = GetTimelineViewportWidth();
        double right = left + viewW;
        if (x < left + 24 || x > right - 24)
            TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, x - viewW * 0.4));
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
        int binCount = _project.MediaBin.Count;
        int clipCount = TotalTimelineClipCount();
        TimeSpan total = _project.TotalDuration;

        if (binCount == 0 && clipCount == 0)
        {
            FileNameText.Text = "No media loaded";
            SegmentsSummaryText.Text = "0 · 00:00.0";
            MonitorMediaHudText.Text = string.Empty;
            if (TrackHeaderFpsText != null) TrackHeaderFpsText.Text = "FPS";
            SyncTransformInspectorUi();
            return;
        }

        FileNameText.Text = clipCount > 0
            ? $"{binCount} media · {clipCount} clip{(clipCount == 1 ? "" : "s")} · {MontageProject.FormatShort(total)}"
            : $"{binCount} media in Project Bin · Drag onto Timeline";

        SegmentsSummaryText.Text = $"{binCount} · {MontageProject.FormatShort(total)}";

        double seqFps = _project.SequenceFps;
        if (TrackHeaderFpsText != null)
            TrackHeaderFpsText.Text = $"{seqFps:0.##}fps";

        if (_playingClip == null && _playingV2Clip == null && _project.TotalDuration > TimeSpan.Zero &&
            _project.MapMontageToSource(_montagePosition).Clip == null &&
            _project.GetActiveVideoOverlayClip(_montagePosition).Clip == null)
        {
            MonitorMediaHudText.Text = "GAP · Black Frame";
        }
        else
        {
            MontageClipItem? active = _project.SelectedClip ?? _playingV2Clip ?? _playingClip ?? _project.Clips.FirstOrDefault() ?? _project.VideoOverlayClips.FirstOrDefault() ?? _project.AudioOverlayClips.FirstOrDefault();
            if (active != null)
            {
                string trackTag = active.IsAudioOverlay ? "Audio A2" : (active.IsVideoOverlay ? $"V2 · {active.DisplayResolution}" : active.DisplayResolution);
                string fps = active.SourceFps > 0 ? $"{active.SourceFps:0.##}fps" : "";
                string codec = _project.Assets.TryGetValue(active.SourcePath, out SourceMediaAsset? asset) &&
                               !string.IsNullOrEmpty(asset.Info.VideoCodec)
                    ? asset.Info.VideoCodec.ToUpperInvariant()
                    : "";
                string audio = active.HasAudio ? "Audio" : "Silent";
                string scaleTag = !active.IsAudioOverlay && active.HasCustomTransform ? active.ScalePercentLabel : "";
                string hud = string.Join(" · ", new[] { $"Clip #{active.OrderIndex}", trackTag, active.AspectRatioLabel, fps, codec, audio, scaleTag }.Where(s => !string.IsNullOrWhiteSpace(s)));
                MonitorMediaHudText.Text = hud;
            }
        }
        SyncAudioGainStripUi();
        SyncTransformInspectorUi();
    }

    private void UpdateTimeLabels()
    {
        double fps = _playingClip?.SourceFps > 1 ? _playingClip.SourceFps : _project.SequenceFps;
        string current = MontageProject.FormatSmpteTimecode(_montagePosition, fps);
        string total = MontageProject.FormatSmpteTimecode(_project.TotalDuration, fps);
        long currentFrame = MontageProject.TimeToFrameIndex(_montagePosition, fps);

        PositionText.Text = current;
        if (FrameIndexText != null)
            FrameIndexText.Text = $"F {currentFrame} · {fps:0.##}fps";
        if (MonitorTotalText != null) MonitorTotalText.Text = total;
        if (TimelineTimecodeText != null) TimelineTimecodeText.Text = current;
        if (TimelineFrameBadge != null) TimelineFrameBadge.Text = $"F {currentFrame}";
        MontageTotalText.Text = total;

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
            SelectedClipDurationText.Text = active != null
                ? MontageProject.FormatSmpteTimecode(active.Duration, active.SourceFps > 1 ? active.SourceFps : fps)
                : "00:00:00";
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

        bool hasCustomTransform = _project.Clips.Any(c => c.HasCustomTransform) || _project.VideoOverlayClips.Any(c => c.HasCustomTransform);
        bool hasGapsOrOverlay = _project.VideoOverlayClips.Count > 0 || _project.AudioOverlayClips.Count > 0 || _project.GetGaps(false).Count > 0 || hasCustomTransform;
        if (mode == VideoCutMode.FastLossless)
        {
            if (multiSource || hasGapsOrOverlay || res != VideoResolutionPreset.Original)
                ExportSummaryHint.Text = $"{fmt.ToUpperInvariant()} · {resLabel} ({fitLabel}) · Auto smart-render for layers/transforms/audio";
            else
                ExportSummaryHint.Text = $"{fmt.ToUpperInvariant()} · Original · Instant stream copy (keyframe snapped)";
        }
        else
        {
            ExportSummaryHint.Text = $"{fmt.ToUpperInvariant()} · {resLabel} · {fitLabel} · Frame-accurate";
        }

        int totalClipCount = TotalTimelineClipCount();
        if (StartExportButtonLabel != null)
            StartExportButtonLabel.Text = totalClipCount > 1 ? "Export Montage" : "Export Cut";
        if (ExportButton != null)
            ExportButton.ToolTip = totalClipCount > 1 ? "Export Montage" : "Export Cut";
        if (ExportSequenceInfoText != null)
        {
            double fps = _project.SequenceFps;
            ExportSequenceInfoText.Text = totalClipCount > 0
                ? $"{totalClipCount} clip{(totalClipCount == 1 ? "" : "s")} · Total {MontageProject.FormatSmpteTimecode(_project.TotalDuration, fps)} · {fps:0.##} fps"
                : string.Empty;
        }
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
        var dlg = new OpenFolderDialog
        {
            Title = "Select output folder for exported video"
        };
        if (dlg.ShowDialog() == true)
        {
            _outputFolderOverride = dlg.FolderName;
            OutputPathText.Text = _outputFolderOverride;
        }
    }

    private async void ClearFile_Click(object sender, RoutedEventArgs e)
    {
        if (IsExporting)
        {
            bool confirm = AppDialog.Confirm(
                this,
                "Clear project?",
                "Cancel the running export and clear this project?",
                confirmText: "Cancel & Clear",
                cancelText: "Keep Exporting",
                tone: DialogTone.Danger);
            if (!confirm) return;
        }

        await ResetViewAsync(cancelExport: true);
    }

    // Centered Blocking Export Dialog & Execution

    private enum ExportStage { Options, Progress, Result }

    private void ShowExportModalStage(ExportStage stage)
    {
        ExportModalOverlay.Visibility = Visibility.Visible;
        ExportOptionsStage.Visibility = stage == ExportStage.Options ? Visibility.Visible : Visibility.Collapsed;
        ExportProgressStage.Visibility = stage == ExportStage.Progress ? Visibility.Visible : Visibility.Collapsed;
        ExportResultStage.Visibility = stage == ExportStage.Result ? Visibility.Visible : Visibility.Collapsed;
        ExportModalCloseBtn.Visibility = stage == ExportStage.Progress ? Visibility.Collapsed : Visibility.Visible;

        ExportModalTitleText.Text = stage switch
        {
            ExportStage.Options => TotalTimelineClipCount() > 1 ? "Export Montage Settings" : "Export Cut Settings",
            ExportStage.Progress => TotalTimelineClipCount() > 1 ? "Exporting Montage..." : "Exporting Cut...",
            ExportStage.Result => "Export Complete",
            _ => "Export Settings"
        };
    }

    private void CloseExportModal_Click(object sender, RoutedEventArgs e)
    {
        if (IsExporting) return;
        ExportModalOverlay.Visibility = Visibility.Collapsed;
        Focus();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsExporting)
        {
            ShowExportModalStage(ExportStage.Progress);
            return;
        }

        if (!HasAnyTimelineClips())
        {
            AppDialog.ShowWarning(this, "Nothing to export", "Drag at least one video or audio clip onto the Timeline first.");
            return;
        }

        await PausePlaybackAsync();
        UpdateExportSummaryHint();
        ShowExportModalStage(ExportStage.Options);
    }

    private async void StartExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsExporting) return;

        if (!HasAnyTimelineClips())
        {
            AppDialog.ShowWarning(this, "Nothing to export", "Drag at least one video or audio clip onto the Timeline first.");
            return;
        }

        IFFmpegService? service = ResolveFfmpegService();
        if (service == null)
        {
            AppDialog.ShowError(this, "Error", "FFmpeg service is not available.");
            return;
        }

        var (fmt, res, fit, mode) = ReadExportSettings();
        var exportClips = _project.Clips.Select(c => c.ToExportClip()).ToList();
        var videoOverlayClips = _project.VideoOverlayClips.Select(c => c.ToExportClip()).ToList();
        var overlayClips = _project.AudioOverlayClips.Select(c => c.ToExportClip()).ToList();

        string firstSource = exportClips.FirstOrDefault()?.SourcePath
            ?? videoOverlayClips.FirstOrDefault()?.SourcePath
            ?? overlayClips[0].SourcePath;
        string outDir = _outputFolderOverride ?? Path.GetDirectoryName(firstSource)!;
        string baseName = Path.GetFileNameWithoutExtension(firstSource);
        string suffix = (exportClips.Count + videoOverlayClips.Count + overlayClips.Count) > 1 ? "montage" : "cut";
        string outPath = Path.Combine(outDir, $"{baseName}_{suffix}.{fmt}");
        int counter = 1;
        while (File.Exists(outPath))
            outPath = Path.Combine(outDir, $"{baseName}_{suffix}_{counter++}.{fmt}");

        _lastOutputPath = outPath;
        ExportButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BackgroundJobBadge.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ProgressPercentText.Text = "0%";
        int totalClipCount = Math.Max(1, exportClips.Count + videoOverlayClips.Count + overlayClips.Count);
        bool simpleLossless = mode == VideoCutMode.FastLossless &&
            videoOverlayClips.Count == 0 &&
            overlayClips.Count == 0 &&
            _project.GetGaps(false).Count == 0 &&
            !_project.Clips.Any(c => c.HasCustomTransform);
        ProgressStatusText.Text = simpleLossless
            ? "Exporting lossless cut..."
            : $"Rendering {totalClipCount} clip{(totalClipCount == 1 ? "" : "s")}...";
        ExportTargetFileText.Text = outPath;

        ShowExportModalStage(ExportStage.Progress);

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
            AspectFitMode: fit,
            AudioOverlayClips: overlayClips,
            VideoOverlayClips: videoOverlayClips);

        _exportTask = Task.Run(async () =>
        {
            try
            {
                await service.ExportMontageAsync(request, token, progress);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                        ExportModalOverlay.Visibility = Visibility.Collapsed;
                        ShowTimelineToast("Export cancelled");
                    }
                    else
                    {
                        ShowResult(outPath, totalClipCount, fmt);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    ExportModalOverlay.Visibility = Visibility.Collapsed;
                    ShowTimelineToast("Export cancelled");
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ExportModalOverlay.Visibility = Visibility.Collapsed;
                    AppDialog.ShowError(this, "Export Error", $"Export failed:\n{ex.Message}");
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ExportButton.IsEnabled = true;
                    CancelButton.IsEnabled = true;
                    BackgroundJobBadge.Visibility = Visibility.Collapsed;
                    _exportTask = null;
                });
            }
        }, token);

        await _exportTask;
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        CancelExportIfRunning();
        ProgressStatusText.Text = "Cancelling export...";
    }

    private void CancelExportIfRunning()
    {
        try { _exportCts?.Cancel(); } catch { }
    }

    private void ShowResult(string outputPath, int clipCount, string fmt)
    {
        if (!File.Exists(outputPath))
        {
            ExportModalOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        long size = new FileInfo(outputPath).Length;
        ResultTitle.Text = clipCount > 1 ? "Montage exported" : "Cut exported";
        ResultDetailText.Text = $"{Path.GetFileName(outputPath)} · {FormatBytes(size)} · {fmt.ToUpperInvariant()} · {clipCount} clip{(clipCount == 1 ? "" : "s")}";
        BackgroundJobBadge.Visibility = Visibility.Collapsed;
        ShowExportModalStage(ExportStage.Result);
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
        try { if (OverlayVideoMedia != null) await OverlayVideoMedia.Close(); } catch { }
        try { if (AudioOverlayMedia != null) await AudioOverlayMedia.Close(); } catch { }

        _activePlayerSourcePath = null;
        _playingClip = null;
        _playingClipIndex = -1;
        _mediaReady = false;
        _v2Channel.Reset();
        _a2Channel.Reset();
        PreviewMedia.Visibility = Visibility.Visible;
        if (V2TransformHost != null) V2TransformHost.Visibility = Visibility.Collapsed;
        SetTransformMode(false);
        _outputFolderOverride = null;
        _lastOutputPath = null;
        _montagePosition = TimeSpan.Zero;
        _zoomFactor = 1.0;
        SetZoomSliderSilently(0.0);
        SetAudioTrackExpanded(false);
        SetActiveTool(TimelineToolMode.Select);
        _project.Clear();

        EmptyStatePanel.Visibility = Visibility.Visible;
        EditorPanel.Visibility = Visibility.Collapsed;
        ExportModalOverlay.Visibility = Visibility.Collapsed;
        _previewFocused = false;
        ApplyPreviewFocusState();
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

    // Keyboard Shortcuts (Adobe Premiere Pro layout)

    private async void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (EditorPanel.Visibility != Visibility.Visible) return;
        if (ExportModalOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape && !IsExporting)
            {
                ExportModalOverlay.Visibility = Visibility.Collapsed;
                Focus();
            }
            e.Handled = true;
            return;
        }
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
                await StepFrameAsync(shift ? -5 : -1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.OemPeriod:
                await StepFrameAsync(shift ? 5 : 1);
                e.Handled = true;
                break;
            case Key.V:
                SetActiveTool(TimelineToolMode.Select);
                e.Handled = true;
                break;
            case Key.C:
                SetActiveTool(TimelineToolMode.Razor);
                e.Handled = true;
                break;
            case Key.A:
                SetActiveTool(TimelineToolMode.Move);
                e.Handled = true;
                break;
            case Key.M when shift:
                SetTransformMode(!_isTransformMode);
                e.Handled = true;
                break;
            case Key.M:
                MuteToggle_Click(sender, e);
                e.Handled = true;
                break;
            case Key.N:
                if (SnapToggle != null)
                {
                    SnapToggle.IsChecked = !_isSnapEnabled;
                    SnapToggle_Click(sender, e);
                }
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isTransformMode)
                {
                    SetTransformMode(false);
                    e.Handled = true;
                }
                else if (_activeTool != TimelineToolMode.Select)
                {
                    SetActiveTool(TimelineToolMode.Select);
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
                EnsurePlayheadVisible();
                e.Handled = true;
                break;
            case Key.End:
                await SyncPlayerToMontagePositionAsync(_project.TotalDuration, autoPlay: false, forceExactSeek: true);
                EnsurePlayheadVisible();
                e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                ZoomBySliderDelta(10.0, anchorPlayhead: true);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                ZoomBySliderDelta(-10.0, anchorPlayhead: true);
                e.Handled = true;
                break;
            case Key.OemBackslash:
            case Key.OemPipe:
                ZoomFit_Click(sender, e);
                e.Handled = true;
                break;
            case Key.F:
                PreviewFocusButton_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    // Program Monitor Frame / Motion Controls (Scale, Position, Opacity + Interactive Box)

    private void TransformToggle_Click(object sender, RoutedEventArgs e)
    {
        SetTransformMode(TransformToggleBtn?.IsChecked == true);
    }

    internal void SetTransformMode(bool enabled)
    {
        _isTransformMode = enabled;
        if (TransformToggleBtn != null)
            TransformToggleBtn.IsChecked = enabled;
        if (ProgramTransformBar != null)
            ProgramTransformBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (ProgramTransformOverlayCanvas != null)
            ProgramTransformOverlayCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        ApplyActiveFrameTransforms();
    }

    private void ProgramStageGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsInitialized) return;
        ApplyActiveFrameTransforms();
        UpdateAspectOverlay();
    }

    private MontageClipItem? GetEditableTransformClip()
    {
        MontageClipItem? sel = _project.SelectedClip;
        if (sel != null && !sel.IsAudioOverlay &&
            _montagePosition >= sel.TimelineStart && _montagePosition <= sel.TimelineEnd)
            return sel;
        if (_playingV2Clip != null)
            return _playingV2Clip;
        if (_playingClip != null)
            return _playingClip;
        if (sel != null && !sel.IsAudioOverlay)
            return sel;
        return _project.VideoOverlayClips.FirstOrDefault() ?? _project.Clips.FirstOrDefault();
    }

    private (double StageW, double StageH, double FrameLeft, double FrameTop, double FrameW, double FrameH) GetProgramFrameRect()
    {
        double stageW = ProgramStageGrid?.ActualWidth ?? 0;
        double stageH = ProgramStageGrid?.ActualHeight ?? 0;
        if (stageW <= 10 || stageH <= 10)
            return (0, 0, 0, 0, 0, 0);

        var (_, res, _, _) = ReadExportSettings();
        double aspect;
        if (res == VideoResolutionPreset.Vertical1080x1920)
            aspect = 9.0 / 16.0;
        else if (res == VideoResolutionPreset.Square1080x1080)
            aspect = 1.0;
        else
        {
            MontageClipItem? refClip = _playingClip ?? _project.Clips.FirstOrDefault() ?? _playingV2Clip ?? _project.VideoOverlayClips.FirstOrDefault();
            aspect = refClip is { SourceWidth: > 0, SourceHeight: > 0 }
                ? refClip.SourceWidth / (double)refClip.SourceHeight
                : 16.0 / 9.0;
        }

        double frameW = stageW;
        double frameH = frameW / Math.Max(0.1, aspect);
        if (frameH > stageH)
        {
            frameH = stageH;
            frameW = frameH * aspect;
        }

        double frameLeft = (stageW - frameW) * 0.5;
        double frameTop = (stageH - frameH) * 0.5;
        return (stageW, stageH, frameLeft, frameTop, frameW, frameH);
    }

    private void ApplyActiveFrameTransforms()
    {
        if (!IsInitialized) return;

        var (stageW, stageH, _, _, frameW, frameH) = GetProgramFrameRect();
        MontageClipItem? v1 = _playingClip ?? _project.MapMontageToSource(_montagePosition).Clip;
        MontageClipItem? v2 = _playingV2Clip ?? _project.GetActiveVideoOverlayClip(_montagePosition).Clip;

        ApplyHostTransform(V1TransformHost, PreviewMedia, v1, frameW, frameH);
        ApplyHostTransform(V2TransformHost, OverlayVideoMedia, v2, frameW, frameH);

        UpdateTransformOverlayBox(stageW, stageH, frameW, frameH);
        SyncTransformInspectorUi();
    }

    private static void ApplyHostTransform(Grid? host, FrameworkElement? media, MontageClipItem? clip, double frameW, double frameH)
    {
        if (host == null) return;
        if (clip == null)
        {
            host.RenderTransform = Transform.Identity;
            if (media != null) media.Opacity = 1.0;
            return;
        }

        double scale = Math.Clamp(clip.VideoScale, 0.15, 2.5);
        double tx = frameW > 0 ? clip.VideoOffsetX * frameW : 0;
        double ty = frameH > 0 ? clip.VideoOffsetY * frameH : 0;

        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(scale, scale));
        group.Children.Add(new TranslateTransform(tx, ty));
        host.RenderTransform = group;

        if (media != null)
            media.Opacity = Math.Clamp(clip.VideoOpacity, 0.0, 1.0);
    }

    private void UpdateTransformOverlayBox(double stageW, double stageH, double frameW, double frameH)
    {
        if (ProgramTransformOverlayCanvas == null || TransformBoundingBox == null) return;
        MontageClipItem? clip = _isTransformMode ? GetEditableTransformClip() : null;

        if (!_isTransformMode || clip == null || stageW <= 10 || stageH <= 10 || frameW <= 10 || frameH <= 10)
        {
            TransformBoundingBox.Visibility = Visibility.Collapsed;
            SetHandleVisibility(Visibility.Collapsed);
            return;
        }

        TransformBoundingBox.Visibility = Visibility.Visible;
        SetHandleVisibility(Visibility.Visible);

        double boxW = Math.Max(18.0, frameW * clip.VideoScale);
        double boxH = Math.Max(18.0, frameH * clip.VideoScale);
        double cx = stageW * 0.5 + clip.VideoOffsetX * frameW;
        double cy = stageH * 0.5 + clip.VideoOffsetY * frameH;
        double left = cx - boxW * 0.5;
        double top = cy - boxH * 0.5;

        Canvas.SetLeft(TransformBoundingBox, left);
        Canvas.SetTop(TransformBoundingBox, top);
        TransformBoundingBox.Width = boxW;
        TransformBoundingBox.Height = boxH;

        if (TransformBoxLabel != null)
            TransformBoxLabel.Text = $"{(clip.IsVideoOverlay ? "V2" : "V1")} · {clip.ScalePercentLabel}";

        PositionHandle(HandleTL, left, top);
        PositionHandle(HandleTC, left + boxW * 0.5, top);
        PositionHandle(HandleTR, left + boxW, top);
        PositionHandle(HandleML, left, top + boxH * 0.5);
        PositionHandle(HandleMR, left + boxW, top + boxH * 0.5);
        PositionHandle(HandleBL, left, top + boxH);
        PositionHandle(HandleBC, left + boxW * 0.5, top + boxH);
        PositionHandle(HandleBR, left + boxW, top + boxH);
    }

    private static void PositionHandle(FrameworkElement? handle, double x, double y)
    {
        if (handle == null) return;
        Canvas.SetLeft(handle, x - 4.5);
        Canvas.SetTop(handle, y - 4.5);
    }

    private void SetHandleVisibility(Visibility vis)
    {
        if (HandleTL != null) HandleTL.Visibility = vis;
        if (HandleTC != null) HandleTC.Visibility = vis;
        if (HandleTR != null) HandleTR.Visibility = vis;
        if (HandleML != null) HandleML.Visibility = vis;
        if (HandleMR != null) HandleMR.Visibility = vis;
        if (HandleBL != null) HandleBL.Visibility = vis;
        if (HandleBC != null) HandleBC.Visibility = vis;
        if (HandleBR != null) HandleBR.Visibility = vis;
    }

    private void SyncTransformInspectorUi()
    {
        if (ProgramTransformBar == null) return;
        MontageClipItem? clip = GetEditableTransformClip();

        if (TransformClipBadgeText != null)
        {
            TransformClipBadgeText.Text = clip != null
                ? $"{(clip.IsVideoOverlay ? "V2" : "V1")} #{clip.OrderIndex}"
                : "No Video";
        }

        bool enabled = clip != null;
        if (FrameScaleSlider != null) FrameScaleSlider.IsEnabled = enabled;
        if (FrameOpacitySlider != null) FrameOpacitySlider.IsEnabled = enabled;

        if (clip == null)
        {
            if (FrameScaleText != null) FrameScaleText.Text = "100%";
            if (FrameOpacityText != null) FrameOpacityText.Text = "100%";
            if (TransformPosText != null) TransformPosText.Text = "X 0 · Y 0";
            return;
        }

        _isUpdatingTransformSliders = true;
        if (FrameScaleSlider != null)
            FrameScaleSlider.Value = Math.Clamp(Math.Round(clip.VideoScale * 100.0), FrameScaleSlider.Minimum, FrameScaleSlider.Maximum);
        if (FrameOpacitySlider != null)
            FrameOpacitySlider.Value = Math.Clamp(Math.Round(clip.VideoOpacity * 100.0), FrameOpacitySlider.Minimum, FrameOpacitySlider.Maximum);
        _isUpdatingTransformSliders = false;

        if (FrameScaleText != null) FrameScaleText.Text = $"{(int)Math.Round(clip.VideoScale * 100.0)}%";
        if (FrameOpacityText != null) FrameOpacityText.Text = $"{(int)Math.Round(clip.VideoOpacity * 100.0)}%";
        if (TransformPosText != null)
        {
            int px = (int)Math.Round(clip.VideoOffsetX * 100.0);
            int py = (int)Math.Round(clip.VideoOffsetY * 100.0);
            TransformPosText.Text = $"X {(px >= 0 ? "+" : "")}{px}% · Y {(py >= 0 ? "+" : "")}{py}%";
        }
    }

    private void ProgramStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isTransformMode || _project.TotalDuration <= TimeSpan.Zero)
            return;

        MontageClipItem? clip = GetEditableTransformClip();
        if (clip == null) return;

        string? tag = FindAncestorTag<string>(e.OriginalSource as DependencyObject);
        if (string.IsNullOrEmpty(tag)) return;

        _activeTransformHandle = tag;
        _transformDragClip = clip;
        _didTransformDrag = false;
        _transformStartPt = e.GetPosition(ProgramStageGrid);
        _transformOrigScale = clip.VideoScale;
        _transformOrigOffX = clip.VideoOffsetX;
        _transformOrigOffY = clip.VideoOffsetY;
        _project.BeginUndoStep();
        ProgramStageGrid?.CaptureMouse();
        e.Handled = true;
    }

    private void ProgramStage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_activeTransformHandle == null || _transformDragClip == null || ProgramStageGrid == null)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CommitProgramTransformDrag();
            return;
        }

        Point pt = e.GetPosition(ProgramStageGrid);
        if ((pt - _transformStartPt).Length >= 2.0)
            _didTransformDrag = true;

        var (stageW, stageH, _, _, frameW, frameH) = GetProgramFrameRect();
        if (frameW <= 10 || frameH <= 10) return;

        double scale = _transformDragClip.VideoScale;
        double offX = _transformDragClip.VideoOffsetX;
        double offY = _transformDragClip.VideoOffsetY;

        if (string.Equals(_activeTransformHandle, "Move", StringComparison.Ordinal))
        {
            offX = Math.Clamp(_transformOrigOffX + (pt.X - _transformStartPt.X) / frameW, -1.5, 1.5);
            offY = Math.Clamp(_transformOrigOffY + (pt.Y - _transformStartPt.Y) / frameH, -1.5, 1.5);

            bool snapX = Math.Abs(offX) < 0.015;
            bool snapY = Math.Abs(offY) < 0.015;
            if (snapX) offX = 0.0;
            if (snapY) offY = 0.0;

            if (TransformCenterGuideV != null)
            {
                TransformCenterGuideV.X1 = TransformCenterGuideV.X2 = stageW * 0.5;
                TransformCenterGuideV.Y1 = 0;
                TransformCenterGuideV.Y2 = stageH;
                TransformCenterGuideV.Visibility = snapX ? Visibility.Visible : Visibility.Collapsed;
            }
            if (TransformCenterGuideH != null)
            {
                TransformCenterGuideH.X1 = 0;
                TransformCenterGuideH.X2 = stageW;
                TransformCenterGuideH.Y1 = TransformCenterGuideH.Y2 = stageH * 0.5;
                TransformCenterGuideH.Visibility = snapY ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        else
        {
            double cx = stageW * 0.5 + _transformOrigOffX * frameW;
            double cy = stageH * 0.5 + _transformOrigOffY * frameH;
            double candX = (Math.Abs(pt.X - cx) * 2.0) / frameW;
            double candY = (Math.Abs(pt.Y - cy) * 2.0) / frameH;
            double rawScale = _activeTransformHandle switch
            {
                "ML" or "MR" => candX,
                "TC" or "BC" => candY,
                _ => Math.Max(candX, candY)
            };
            scale = Math.Clamp(rawScale, 0.15, 2.5);
            if (Math.Abs(scale - 1.0) < 0.02)
                scale = 1.0;
        }

        _project.SetClipTransform(_transformDragClip, scale, offX, offY, _transformDragClip.VideoOpacity, recordUndo: false);
        ApplyActiveFrameTransforms();

        if (TransformHudPill != null && TransformHudText != null)
        {
            TransformHudText.Text = $"Scale {(int)Math.Round(scale * 100.0)}% · X {(int)Math.Round(offX * 100.0):+0;-0;0}% Y {(int)Math.Round(offY * 100.0):+0;-0;0}%";
            Canvas.SetLeft(TransformHudPill, Math.Clamp(pt.X + 12, 8, Math.Max(8, stageW - 180)));
            Canvas.SetTop(TransformHudPill, Math.Clamp(pt.Y - 28, 8, Math.Max(8, stageH - 28)));
            TransformHudPill.Visibility = Visibility.Visible;
        }
    }

    private void ProgramStage_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_activeTransformHandle != null && e.LeftButton != MouseButtonState.Pressed)
            CommitProgramTransformDrag();
    }

    private void CommitProgramTransformDrag()
    {
        if (_activeTransformHandle == null) return;
        _activeTransformHandle = null;
        _transformDragClip = null;
        if (ProgramStageGrid?.IsMouseCaptured == true)
            ProgramStageGrid.ReleaseMouseCapture();
        if (TransformCenterGuideV != null) TransformCenterGuideV.Visibility = Visibility.Collapsed;
        if (TransformCenterGuideH != null) TransformCenterGuideH.Visibility = Visibility.Collapsed;
        if (TransformHudPill != null) TransformHudPill.Visibility = Visibility.Collapsed;
        _project.CommitUndoStep();
        UpdateProjectHeaderAndHud();
        UpdateExportSummaryHint();
        RenderTimeline();
    }

    private void FrameTransformSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (GetEditableTransformClip() == null) return;
        _isDraggingTransformSlider = true;
        _project.BeginUndoStep();
    }

    private void FrameTransformSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingTransformSlider) return;
        _isDraggingTransformSlider = false;
        _project.CommitUndoStep();
        UpdateExportSummaryHint();
        RenderTimeline();
    }

    private void FrameTransformSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTransformSlider) return;
        _isDraggingTransformSlider = false;
        _project.CommitUndoStep();
        UpdateExportSummaryHint();
        RenderTimeline();
    }

    private void FrameScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingTransformSliders || !IsInitialized) return;
        MontageClipItem? clip = GetEditableTransformClip();
        if (clip == null) return;

        double scale = Math.Clamp(e.NewValue / 100.0, 0.15, 2.5);
        _project.SetClipTransform(clip, scale, clip.VideoOffsetX, clip.VideoOffsetY, clip.VideoOpacity, recordUndo: !_isDraggingTransformSlider);
        ApplyActiveFrameTransforms();
        if (!_isDraggingTransformSlider)
        {
            UpdateExportSummaryHint();
            RenderTimeline();
        }
    }

    private void FrameOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingTransformSliders || !IsInitialized) return;
        MontageClipItem? clip = GetEditableTransformClip();
        if (clip == null) return;

        double opacity = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        _project.SetClipTransform(clip, clip.VideoScale, clip.VideoOffsetX, clip.VideoOffsetY, opacity, recordUndo: !_isDraggingTransformSlider);
        ApplyActiveFrameTransforms();
        if (!_isDraggingTransformSlider)
        {
            UpdateExportSummaryHint();
            RenderTimeline();
        }
    }

    private void FramePreset_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? clip = GetEditableTransformClip();
        if (clip == null || sender is not FrameworkElement fe) return;
        string mode = fe.Tag?.ToString() ?? "Reset";

        var (scale, offX, offY, opacity) = mode switch
        {
            "Fill" => (1.25, 0.0, 0.0, clip.VideoOpacity),
            "PiP" => (0.38, 0.28, -0.26, clip.VideoOpacity),
            _ => (1.0, 0.0, 0.0, mode == "Reset" ? 1.0 : clip.VideoOpacity)
        };

        _project.SetClipTransform(clip, scale, offX, offY, opacity, recordUndo: true);
        ApplyActiveFrameTransforms();
        UpdateProjectHeaderAndHud();
        UpdateExportSummaryHint();
        RenderTimeline();
        ShowTimelineToast($"Frame → {mode}");
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
