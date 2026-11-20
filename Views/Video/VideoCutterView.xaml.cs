using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Core.Services.Media;
using CortexFX.Core.Video;
using Microsoft.Extensions.DependencyInjection;

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
        public DateTime LastSeekUtc = DateTime.MinValue;

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
            LastSeekUtc = DateTime.MinValue;
        }
    }

    // Video overlay channels: [0] = V2, [1] = V3
    private readonly OverlayChannelState[] _videoOverlayChannels = { new(), new() };
    // Audio channels: [0] = A1, [1] = A2, [2] = A3, [3] = A4 Music
    private readonly OverlayChannelState[] _audioChannels = { new(), new(), new(), new() };

    private readonly bool[] _isVideoTrackMuted = new bool[MontageProject.MaxVideoLayers];
    private readonly bool[] _isAudioTrackMuted = new bool[MontageProject.MaxAudioTracks];

    // Program Monitor Frame / Motion transform interaction state
    private bool _isTransformMode;
    private bool _isUpdatingTransformSliders;
    private bool _isDraggingTransformSlider;
    private string? _activeTransformHandle;
    private bool _didTransformDrag;
    private MontageClipItem? _transformDragClip;
    private Point _transformStartPt;
    private double _transformOrigOffX;
    private double _transformOrigOffY;

    // Media Bin drag state
    private Point _binDragStartPoint;
    private SourceMediaAsset? _binDragAsset;

    // Single-flight coalesced V1 FFME seek coordinator
    private bool _seekLoopRunning;
    private bool _hasQueuedSeek;
    private TimeSpan _queuedSeekTarget;
    private bool _queuedAutoPlay;
    private Task? _activeSeekLoopTask;

    // Extraction & Timeline rendering / animation state
    private readonly Dictionary<string, AssetExtractionJob> _assetExtractionJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RenderedTimelineClip> _renderedTimelineClips = new();
    private DispatcherTimer? _timelineSettleTimer;
    private DispatcherTimer? _viewportRefineTimer;
    private CancellationTokenSource? _viewportRefineCts;
    private double _renderedMinX;
    private double _renderedMaxX;
    private double _renderedTotalWidth;

    private List<MoveClipDragSnapshot>? _moveDragSnapshots;
    private TimeSpan _moveResolvedTargetTime;
    private int _moveTargetLayer;
    private double _movePxPerSec = 10.0;
    private double _currentTimelinePxPerSec = 10.0;
    private double _currentTimelineSurfaceWidth = 100.0;
    private double _moveMouseOffsetX;
    private int _moveSettleGeneration;
    private bool _revealV2Drop;
    private bool _revealV3Drop;
    private bool _revealA2Drop;
    private bool _revealA3Drop;
    private bool _targetV2Visible;
    private bool _targetV3Visible;
    private bool _targetA2Visible;
    private bool _targetA3Visible;
    private readonly HashSet<Guid> _pendingEntranceClipIds = new();

    // Timeline interaction state
    private TimelineDragMode _dragMode = TimelineDragMode.None;
    private MontageClipItem? _dragTargetClip;
    private MontageClipItem? _pressedTimelineClip;
    private Point _pressedTimelineOrigin;
    private ClipAudioVolumeHandle? _dragVolumeHandle;
    private double _dragStartX;
    private double _dragStartY;
    private double _dragPxPerSec = 10.0;
    private double _dragOrigClipVolume = 1.0;
    private TimeSpan _dragOrigStart;
    private TimeSpan _dragOrigEnd;
    private TimeSpan _dragOrigTimelineStart;
    private TimeSpan _preTrimMontagePosition;

    // Audio gain strip state
    private bool _isAudioExpanded;
    private bool _isUpdatingAudioSlider;
    private bool _isDraggingAudioSlider;
    private double _lastUnmutedClipVolume = 1.0;
    private DateTime _lastWaveformRebuildUtc = DateTime.MinValue;

    // Jog wheel state
    private bool _isDraggingJog;
    private double _jogLastX;
    private double _jogAccumPx;
    private double _jogVisualOffset;

    private CancellationTokenSource? _exportCts;
    private Task? _exportTask;

    private enum TimelineToolMode { Select, Razor, Move }
    private enum TimelineDragMode { None, ScrubPlayhead, TrimStart, TrimEnd, PendingMoveOrClick, MoveClip, PendingAudioVolumeDrag, DragAudioVolume }

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
        SyncAllDynamicTrackRows(animate: false);
        InitializeDockSystem();
        ClipsList.ItemsSource = _project.MediaBin;
        InitializePersistence();

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
                else
                {
                    RefreshSavedProjectsList();
                }
                Focus();
            }
            else
            {
                _ = PausePlaybackAsync();
            }
        };

        Unloaded += (_, _) => CancelAllAssetExtractions();
        _project.HistoryChanged += (_, _) => UpdateUndoRedoUi();
        UpdateExportSummaryHint();
        UpdateUndoRedoUi();
    }

    public void Initialize(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
        _ffmpegService = App.Services?.GetService<IFFmpegService>();
        _ = RefreshEncoderStatusAsync();
    }

    public void Shutdown()
    {
        FlushAutoSaveSync();
        CancelExportIfRunning();
        CancelAllAssetExtractions();

        _uiTimer.Stop();
        _toastTimer?.Stop();
        _timelineSettleTimer?.Stop();
        _viewportRefineTimer?.Stop();
        _autoSaveTimer?.Stop();

        try { _ = PreviewMedia.Close(); } catch { }
        for (int layer = 1; layer <= 2; layer++)
            try { if (GetVideoOverlayMediaElement(layer) is { } m) _ = m.Close(); } catch { }
        for (int a = 0; a < MontageProject.MaxAudioTracks; a++)
            try { if (GetAudioMediaElement(a) is { } m) _ = m.Close(); } catch { }
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
        if (SaveProgressContainer != null)
            SaveProgressContainer.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        HeaderClearButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (HeaderActionDivider != null)
            HeaderActionDivider.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (ExportButton != null)
            ExportButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (HeaderOpenProjectButton != null)
            HeaderOpenProjectButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        HeaderReadyPill.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        BackgroundJobBadge.Visibility = IsExporting ? Visibility.Visible : Visibility.Collapsed;
        UpdateSaveButtonUi();

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

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

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

    private Task ResetViewAsync(bool cancelExport) =>
        ResetViewCoreAsync(cancelExport, clearAutoSave: true, returnToEmptyState: true);

    private async Task ResetViewCoreAsync(bool cancelExport, bool clearAutoSave, bool returnToEmptyState)
    {
        CancelAllAssetExtractions();
        if (cancelExport)
            CancelExportIfRunning();

        await PausePlaybackAsync();
        try { await PreviewMedia.Close(); } catch { }
        for (int layer = 1; layer <= 2; layer++)
            try { if (GetVideoOverlayMediaElement(layer) is { } m) await m.Close(); } catch { }
        for (int a = 0; a < MontageProject.MaxAudioTracks; a++)
            try { if (GetAudioMediaElement(a) is { } m) await m.Close(); } catch { }

        _activePlayerSourcePath = null;
        _playingClip = null;
        _playingClipIndex = -1;
        _mediaReady = false;
        _activeAudioTargetClip = null;
        foreach (OverlayChannelState ch in _videoOverlayChannels.Concat(_audioChannels))
            ch.Reset();

        Array.Clear(_isVideoTrackMuted);
        Array.Clear(_isAudioTrackMuted);
        if (TrackV2MuteButton != null) TrackV2MuteButton.IsChecked = false;
        if (TrackV3MuteButton != null) TrackV3MuteButton.IsChecked = false;
        SyncMuteUi();

        PreviewMedia.Visibility = Visibility.Visible;
        if (V2TransformHost != null) V2TransformHost.Visibility = Visibility.Collapsed;
        if (V3TransformHost != null) V3TransformHost.Visibility = Visibility.Collapsed;
        SetTransformMode(false);
        _outputFolderOverride = null;
        _lastOutputPath = null;
        _montagePosition = TimeSpan.Zero;
        _zoomFactor = 1.0;
        SetZoomSliderSilently(0.0);
        SetAudioTrackExpanded(false);
        SetActiveTool(TimelineToolMode.Select);
        _pendingEntranceClipIds.Clear();

        _suppressDirtyTracking = true;
        try
        {
            _project.Clear();
            ResetPersistenceTracking(clearAutoSave: clearAutoSave);
        }
        finally
        {
            _suppressDirtyTracking = false;
        }
        ClearAllDropReveals(animate: false);

        if (returnToEmptyState)
        {
            _speedRatio = 1.0;
            if (SpeedButton != null) SpeedButton.Content = "1.0×";
            if (LoopToggle != null) LoopToggle.IsChecked = false;

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
            RefreshSavedProjectsList();
            UpdateProjectHeaderAndHud();
            UpdateTimeLabels();
            UpdateAspectOverlay();
            RenderTimeline();
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

    private static Dictionary<string, SolidColorBrush>? _frozenBrushCache;

    private static SolidColorBrush BrushFrom(string hex)
    {
        _frozenBrushCache ??= new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);
        if (_frozenBrushCache.TryGetValue(hex, out SolidColorBrush? cached))
            return cached;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        _frozenBrushCache[hex] = brush;
        return brush;
    }

    private sealed class InlineFfmpegConfig(string ffmpegPath) : IAppConfiguration
    {
        public string ResourcesDirectory => Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
        public string FFmpegPath => ffmpegPath;
        public string MagickPath => string.Empty;
        public string PdfToCairoPath => string.Empty;
        public string FFmpegLibsDirectory => Path.Combine(ResourcesDirectory, "ffmpeg_libs");
    }
}
