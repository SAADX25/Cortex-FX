using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CortexFX.Core.Video;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    private static readonly SolidColorBrush TileBgBrush = BrushFrom("#1A2029");
    private static readonly SolidColorBrush TileSeparatorBrush = BrushFrom("#10141A");
    private static readonly SolidColorBrush TrimGripIdleBrush = BrushFrom("#66FFFFFF");
    private static readonly SolidColorBrush TrimGripHoverBrush = BrushFrom("#FBBF24");

    internal enum TrackRevealSlot { V2, V3, A2, A3 }

    public static readonly DependencyProperty V3TrackAnimatedHeightProperty =
        DependencyProperty.Register(nameof(V3TrackAnimatedHeight), typeof(double), typeof(VideoCutterView),
            new PropertyMetadata(0.0, OnDynamicTrackHeightChanged));

    public static readonly DependencyProperty V2TrackAnimatedHeightProperty =
        DependencyProperty.Register(nameof(V2TrackAnimatedHeight), typeof(double), typeof(VideoCutterView),
            new PropertyMetadata(0.0, OnDynamicTrackHeightChanged));

    public static readonly DependencyProperty A2TrackAnimatedHeightProperty =
        DependencyProperty.Register(nameof(A2TrackAnimatedHeight), typeof(double), typeof(VideoCutterView),
            new PropertyMetadata(0.0, OnDynamicTrackHeightChanged));

    public static readonly DependencyProperty A3TrackAnimatedHeightProperty =
        DependencyProperty.Register(nameof(A3TrackAnimatedHeight), typeof(double), typeof(VideoCutterView),
            new PropertyMetadata(0.0, OnDynamicTrackHeightChanged));

    public double V3TrackAnimatedHeight { get => (double)GetValue(V3TrackAnimatedHeightProperty); set => SetValue(V3TrackAnimatedHeightProperty, value); }
    public double V2TrackAnimatedHeight { get => (double)GetValue(V2TrackAnimatedHeightProperty); set => SetValue(V2TrackAnimatedHeightProperty, value); }
    public double A2TrackAnimatedHeight { get => (double)GetValue(A2TrackAnimatedHeightProperty); set => SetValue(A2TrackAnimatedHeightProperty, value); }
    public double A3TrackAnimatedHeight { get => (double)GetValue(A3TrackAnimatedHeightProperty); set => SetValue(A3TrackAnimatedHeightProperty, value); }

    private static void OnDynamicTrackHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VideoCutterView view)
            view.ApplyTimelineVerticalMetrics();
    }

    private sealed record ClipEdgeHandle(MontageClipItem Clip, bool IsStart);
    private sealed record ClipMoveGrip(MontageClipItem Clip);
    private sealed record ClipAudioStripTrigger(MontageClipItem Clip);

    private sealed class ClipAudioVolumeHandle
    {
        public ClipAudioVolumeHandle(
            MontageClipItem clip,
            double waveHeight,
            Canvas waveHost,
            float[]? peaks,
            System.Windows.Shapes.Path? wavePath,
            Line volumeLine,
            Border volumeHitStrip,
            Border? volumeBadge,
            TextBlock? volumeBadgeText,
            TextBlock? headerDbText,
            double visStartNorm,
            double visEndNorm,
            double visWidth)
        {
            Clip = clip;
            WaveHeight = waveHeight;
            WaveHost = waveHost;
            Peaks = peaks;
            WavePath = wavePath;
            VolumeLine = volumeLine;
            VolumeHitStrip = volumeHitStrip;
            VolumeBadge = volumeBadge;
            VolumeBadgeText = volumeBadgeText;
            HeaderDbText = headerDbText;
            VisStartNorm = visStartNorm;
            VisEndNorm = visEndNorm;
            VisWidth = visWidth;
        }

        public MontageClipItem Clip { get; }
        public double WaveHeight { get; }
        public Canvas WaveHost { get; }
        public float[]? Peaks { get; }
        public System.Windows.Shapes.Path? WavePath { get; }
        public Line VolumeLine { get; }
        public Border VolumeHitStrip { get; }
        public Border? VolumeBadge { get; }
        public TextBlock? VolumeBadgeText { get; }
        public TextBlock? HeaderDbText { get; }
        public double VisStartNorm { get; }
        public double VisEndNorm { get; }
        public double VisWidth { get; }
        public bool IsDraggingOrHover { get; set; }
    }

    private sealed record MoveClipDragSnapshot(
        MontageClipItem Clip,
        Border? VideoBlock,
        Border? AudioBlock,
        double OrigLeft,
        double Width);

    private sealed class FilmstripTileSlot
    {
        public FilmstripTileSlot(double sampleSeconds, double spanSeconds, double timelineLeftX, Border border)
        {
            SampleSeconds = sampleSeconds;
            SpanSeconds = spanSeconds;
            TimelineLeftX = timelineLeftX;
            Border = border;
        }

        public double SampleSeconds { get; }
        public double SpanSeconds { get; }
        public double TimelineLeftX { get; }
        public Border Border { get; }
        public ImageSource? CurrentFrame { get; set; }
        public Image? ImageControl { get; set; }
    }

    private sealed record RenderedTimelineClip(
        MontageClipItem Clip,
        Border? VideoBlock,
        Border? AudioBlock,
        IReadOnlyList<FilmstripTileSlot> TileSlots,
        ClipAudioVolumeHandle? AudioHandle);

    private const double TimelineTrailingRunwayPx = 110.0;
    private const double TimelineRulerHeight = 34.0;
    private const double TimelineOverlayVideoRowHeight = 72.0;
    private const double TimelineV1RowHeight = 96.0;
    private const double TimelineOverlayAudioRowHeight = 58.0;
    private const double TimelineA4RowHeight = 54.0;

    private double CurrentV3RowHeight => Math.Clamp(V3TrackAnimatedHeight, 0.0, TimelineOverlayVideoRowHeight);
    private double CurrentV2RowHeight => Math.Clamp(V2TrackAnimatedHeight, 0.0, TimelineOverlayVideoRowHeight);
    private double CurrentA2RowHeight => Math.Clamp(A2TrackAnimatedHeight, 0.0, TimelineOverlayAudioRowHeight);
    private double CurrentA3RowHeight => Math.Clamp(A3TrackAnimatedHeight, 0.0, TimelineOverlayAudioRowHeight);

    private double V3TopY => TimelineRulerHeight;
    private double V2TopY => V3TopY + CurrentV3RowHeight;
    private double V1TopY => V2TopY + CurrentV2RowHeight;
    private double A1TopY => V1TopY + TimelineV1RowHeight;
    private double A2TopY => A1TopY + GetAudioTrackRowHeight();
    private double A3TopY => A2TopY + CurrentA2RowHeight;
    private double A4TopY => A3TopY + CurrentA3RowHeight;
    private double TotalTimelineContentHeight => A4TopY + TimelineA4RowHeight;

    private double GetVideoTrackTopY(int layer) => layer switch
    {
        2 => V3TopY,
        1 => V2TopY,
        _ => V1TopY
    };

    private double GetAudioTrackTopY(int layer) => layer switch
    {
        0 => A1TopY,
        1 => A2TopY,
        2 => A3TopY,
        _ => A4TopY
    };

    private double GetAudioTrackRowHeightForLayer(int layer) => layer switch
    {
        0 => GetAudioTrackRowHeight(),
        1 or 2 => TimelineOverlayAudioRowHeight,
        _ => TimelineA4RowHeight
    };

    private bool IsVideoLayerVisible(int layer) => layer switch
    {
        2 => _project.VideoOverlay3Clips.Count > 0 || _revealV3Drop || CurrentV3RowHeight > 4.0,
        1 => _project.VideoOverlayClips.Count > 0 || _revealV2Drop || CurrentV2RowHeight > 4.0,
        _ => true
    };

    private bool IsAudioTrackVisible(int trackLayer) => trackLayer switch
    {
        1 => _project.Audio2Clips.Count > 0 || _revealA2Drop || CurrentA2RowHeight > 4.0,
        2 => _project.Audio3Clips.Count > 0 || _revealA3Drop || CurrentA3RowHeight > 4.0,
        _ => true
    };

    private bool HasAnyTimelineClips() => _project.HasAnyClips;
    private int TotalTimelineClipCount() => _project.TotalClipCount;

    internal void SetTrackDropReveal(TrackRevealSlot slot, bool active, bool animate = true)
    {
        bool changed = slot switch
        {
            TrackRevealSlot.V2 => _revealV2Drop != active && ((_revealV2Drop = active) || true),
            TrackRevealSlot.V3 => _revealV3Drop != active && ((_revealV3Drop = active) || true),
            TrackRevealSlot.A2 => _revealA2Drop != active && ((_revealA2Drop = active) || true),
            TrackRevealSlot.A3 => _revealA3Drop != active && ((_revealA3Drop = active) || true),
            _ => false
        };
        if (changed)
            SyncAllDynamicTrackRows(animate);
    }

    internal void ClearAllDropReveals(bool animate = true)
    {
        _revealV2Drop = false;
        _revealV3Drop = false;
        _revealA2Drop = false;
        _revealA3Drop = false;
        SyncAllDynamicTrackRows(animate);
    }

    internal void SyncAllDynamicTrackRows(bool animate = true)
    {
        bool showV2 = _project.VideoOverlayClips.Count > 0 || _revealV2Drop;
        bool showV3 = _project.VideoOverlay3Clips.Count > 0 || _revealV3Drop;
        bool showA2 = _project.Audio2Clips.Count > 0 || _revealA2Drop;
        bool showA3 = _project.Audio3Clips.Count > 0 || _revealA3Drop;

        if (TimelineV2DropHint != null)
            TimelineV2DropHint.Visibility = _revealV2Drop && _project.VideoOverlayClips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (TimelineV3DropHint != null)
            TimelineV3DropHint.Visibility = _revealV3Drop && _project.VideoOverlay3Clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AnimateDynamicTrackHeight(V3TrackAnimatedHeightProperty, V3TrackAnimatedHeight, ref _targetV3Visible, showV3, TimelineOverlayVideoRowHeight, animate);
        AnimateDynamicTrackHeight(V2TrackAnimatedHeightProperty, V2TrackAnimatedHeight, ref _targetV2Visible, showV2, TimelineOverlayVideoRowHeight, animate);
        AnimateDynamicTrackHeight(A2TrackAnimatedHeightProperty, A2TrackAnimatedHeight, ref _targetA2Visible, showA2, TimelineOverlayAudioRowHeight, animate);
        AnimateDynamicTrackHeight(A3TrackAnimatedHeightProperty, A3TrackAnimatedHeight, ref _targetA3Visible, showA3, TimelineOverlayAudioRowHeight, animate);
        ApplyTimelineVerticalMetrics();
    }

    private void AnimateDynamicTrackHeight(
        DependencyProperty prop,
        double currentVal,
        ref bool targetVisibleState,
        bool shouldShow,
        double expandedHeight,
        bool animate)
    {
        double targetHeight = shouldShow ? expandedHeight : 0.0;
        if (targetVisibleState == shouldShow && Math.Abs(currentVal - targetHeight) < 0.5)
            return;

        targetVisibleState = shouldShow;
        if (!animate || !IsLoaded)
        {
            BeginAnimation(prop, null);
            SetValue(prop, targetHeight);
            return;
        }

        var anim = new DoubleAnimation(currentVal, targetHeight, TimeSpan.FromMilliseconds(shouldShow ? 185 : 155))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(prop, anim);
    }

    private void ApplyTimelineVerticalMetrics()
    {
        double v3H = CurrentV3RowHeight;
        double v2H = CurrentV2RowHeight;
        double a1H = GetAudioTrackRowHeight();
        double a2H = CurrentA2RowHeight;
        double a3H = CurrentA3RowHeight;
        double totalTrackHeight = TimelineRulerHeight + v3H + v2H + TimelineV1RowHeight + a1H + a2H + a3H + TimelineA4RowHeight;
        double overlayTrackSpan = Math.Max(0.0, totalTrackHeight - TimelineRulerHeight);

        static void ApplyRowVis(double h, double maxH, RowDefinition? hdrRow, RowDefinition? tlRow, Border? hdrBorder, Border? bgBorder)
        {
            if (hdrRow != null) hdrRow.Height = new GridLength(h);
            if (tlRow != null) tlRow.Height = new GridLength(h);
            double ratio = maxH > 0 ? Math.Clamp(h / maxH, 0.0, 1.0) : 0.0;
            Visibility vis = h > 1.0 ? Visibility.Visible : Visibility.Collapsed;
            if (hdrBorder != null) { hdrBorder.Visibility = vis; hdrBorder.Opacity = ratio; }
            if (bgBorder != null) { bgBorder.Visibility = vis; bgBorder.Opacity = ratio; }
        }

        ApplyRowVis(v3H, TimelineOverlayVideoRowHeight, TrackHeaderV3Row, TimelineV3Row, V3TrackHeaderBorder, TimelineV3Bg);
        ApplyRowVis(v2H, TimelineOverlayVideoRowHeight, TrackHeaderV2Row, TimelineV2Row, V2TrackHeaderBorder, TimelineV2Bg);
        ApplyRowVis(a2H, TimelineOverlayAudioRowHeight, TrackHeaderA2Row, TimelineA2Row, A2TrackHeaderBorder, TimelineA2Bg);
        ApplyRowVis(a3H, TimelineOverlayAudioRowHeight, TrackHeaderA3Row, TimelineA3Row, A3TrackHeaderBorder, TimelineA3Bg);

        if (TrackHeaderA1Row != null) TrackHeaderA1Row.Height = new GridLength(a1H);
        if (TimelineA1Row != null) TimelineA1Row.Height = new GridLength(a1H);
        if (TrackHeadersGrid != null) TrackHeadersGrid.MinHeight = totalTrackHeight;
        if (TimelineSurface != null) TimelineSurface.Height = totalTrackHeight;
        if (RazorGuideLine != null) RazorGuideLine.Y2 = totalTrackHeight;
        if (DropInsertionLine != null) DropInsertionLine.Height = overlayTrackSpan;
        if (PlayheadLine != null) PlayheadLine.Height = overlayTrackSpan + 2.0;

        if (MoveSlotPreviewRect != null && _dragMode == TimelineDragMode.MoveClip && _moveDragSnapshots is { Count: > 0 })
            ApplyMoveSlotVerticalBounds(_moveDragSnapshots[0].Clip);
    }

    private void UpdateVideoDropTrackRevealFromY(double y)
    {
        if (!_revealV2Drop && _project.VideoOverlayClips.Count == 0 && y <= V1TopY + 24.0)
            SetTrackDropReveal(TrackRevealSlot.V2, true, animate: true);

        bool v2Available = _project.VideoOverlayClips.Count > 0 || _revealV2Drop;
        if (!_revealV3Drop && _project.VideoOverlay3Clips.Count == 0 && v2Available && y <= V2TopY + 22.0)
            SetTrackDropReveal(TrackRevealSlot.V3, true, animate: true);
    }

    private int ResolveVideoDropLayerFromY(double y)
    {
        if (IsVideoLayerVisible(2) && y < V2TopY + 4.0)
            return 2;
        if (IsVideoLayerVisible(1) && y < V1TopY + 4.0)
            return 1;
        return 0;
    }

    private int ResolveAudioDropLayerFromY(double y)
    {
        if (y >= A4TopY - 4.0) return 3;
        if (IsAudioTrackVisible(2) && y >= A3TopY - 4.0) return 2;
        if (IsAudioTrackVisible(1) && y >= A2TopY - 4.0) return 1;
        return 0;
    }

    // Zoom & Viewport Geometry

    private double GetTimelineViewportWidth()
    {
        double w = TimelineScrollViewer.ViewportWidth;
        if (w <= 0) w = TimelineScrollViewer.ActualWidth;
        if (w <= 0) w = 640;
        return w;
    }

    private double ComputeMaxZoomFactor(double viewportW)
    {
        double totalSec = Math.Max(1.0, _project.TotalDuration.TotalSeconds);
        double fps = Math.Clamp(_project.SequenceFps, 12.0, 120.0);
        double maxPxPerSec = fps * 36.0;
        double baseW = Math.Max(220.0, viewportW - TimelineTrailingRunwayPx);
        double raw = (totalSec * maxPxPerSec) / baseW;
        return Math.Clamp(raw, 12.0, 240.0);
    }

    private double SliderToZoomFactor(double sliderValue, double viewportW)
    {
        if (sliderValue <= 0.05) return 1.0;
        double maxZoom = ComputeMaxZoomFactor(viewportW);
        return Math.Pow(maxZoom, Math.Clamp(sliderValue, 0, 100) / 100.0);
    }

    private double ZoomFactorToSlider(double zoomFactor, double viewportW)
    {
        if (zoomFactor <= 1.001) return 0.0;
        double maxZoom = ComputeMaxZoomFactor(viewportW);
        return Math.Clamp(100.0 * Math.Log(Math.Clamp(zoomFactor, 1.0, maxZoom)) / Math.Log(maxZoom), 0, 100);
    }

    private (double MinX, double MaxX, double ViewLeft, double ViewRight) GetVisiblePixelWindow(double totalWidth)
    {
        double viewLeft = TimelineScrollViewer.HorizontalOffset;
        double viewW = GetTimelineViewportWidth();
        double viewRight = viewLeft + viewW;
        double buffer = Math.Max(260, viewW * 0.55);
        double minX = Math.Max(0, viewLeft - buffer);
        double maxX = Math.Min(totalWidth, viewRight + buffer);
        return (minX, maxX, viewLeft, viewRight);
    }

    private (double PixelsPerSecond, double SequenceWidth, double SurfaceWidth) GetTimelineMetrics()
    {
        double viewportW = GetTimelineViewportWidth();
        double maxZoom = ComputeMaxZoomFactor(viewportW);
        if (_zoomFactor > maxZoom)
            _zoomFactor = maxZoom;

        double totalSec = Math.Max(0.1, _project.TotalDuration.TotalSeconds);
        double baseW = Math.Max(120.0, viewportW - TimelineTrailingRunwayPx);
        double seqW = Math.Max(120.0, baseW * _zoomFactor);
        double surfaceW = Math.Max(viewportW, seqW + TimelineTrailingRunwayPx);
        double pxPerSec = _project.TotalDuration > TimeSpan.Zero ? seqW / totalSec : 12.0;
        return (pxPerSec, seqW, surfaceW);
    }

    private double ComputeTimelineSurfaceWidth()
    {
        bool zoomed = _zoomFactor > 1.01;
        ScrollBarVisibility desiredHScroll = zoomed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (TimelineScrollViewer.HorizontalScrollBarVisibility != desiredHScroll)
            TimelineScrollViewer.HorizontalScrollBarVisibility = desiredHScroll;

        var (pxPerSec, _, surfaceW) = GetTimelineMetrics();
        _currentTimelinePxPerSec = pxPerSec;
        _currentTimelineSurfaceWidth = surfaceW;

        TimelineSurface.Width = surfaceW;
        TimelineRulerCanvas.Width = surfaceW;
        if (TimelineV3Canvas != null) TimelineV3Canvas.Width = surfaceW;
        if (TimelineV2Canvas != null) TimelineV2Canvas.Width = surfaceW;
        TimelineClipsCanvas.Width = surfaceW;
        TimelineAudioCanvas.Width = surfaceW;
        if (TimelineAudio2Canvas != null) TimelineAudio2Canvas.Width = surfaceW;
        if (TimelineAudio3Canvas != null) TimelineAudio3Canvas.Width = surfaceW;
        if (TimelineAudio4Canvas != null) TimelineAudio4Canvas.Width = surfaceW;
        TimelineOverlayCanvas.Width = surfaceW;

        UpdateZoomReadout(pxPerSec);
        return surfaceW;
    }

    private void UpdateZoomReadout(double pxPerSec)
    {
        if (ZoomReadoutText == null) return;
        double fps = _project.SequenceFps;
        double pxPerFrame = pxPerSec / Math.Max(1.0, fps);

        if (_zoomFactor <= 1.02)
            ZoomReadoutText.Text = "1.0×";
        else if (pxPerFrame >= 28.0)
            ZoomReadoutText.Text = "Frames";
        else if (_zoomFactor >= 10)
            ZoomReadoutText.Text = $"{_zoomFactor:F0}×";
        else
            ZoomReadoutText.Text = $"{_zoomFactor:F1}×";
    }

    private void UpdateTimelineLayoutInPlace()
    {
        double totalWidth = ComputeTimelineSurfaceWidth();

        if (_project.TotalDuration <= TimeSpan.Zero || !HasAnyTimelineClips())
        {
            UpdatePlayheadVisual();
            RenderProgramCutMarkers();
            return;
        }

        if (_renderedTimelineClips.Count != TotalTimelineClipCount())
        {
            RenderTimeline();
            return;
        }

        double pxPerSec = _currentTimelinePxPerSec;
        var (minX, maxX, _, _) = GetVisiblePixelWindow(totalWidth);
        TimelineRulerCanvas.Children.Clear();
        RenderTimeRuler(totalWidth, pxPerSec, minX, maxX);

        foreach (RenderedTimelineClip item in _renderedTimelineClips)
        {
            double left = item.Clip.TimelineStart.TotalSeconds * pxPerSec;
            double width = Math.Max(4, item.Clip.Duration.TotalSeconds * pxPerSec);
            if (item.VideoBlock != null)
            {
                Canvas.SetLeft(item.VideoBlock, left);
                item.VideoBlock.Width = width;
            }
            if (item.AudioBlock != null)
            {
                Canvas.SetLeft(item.AudioBlock, left);
                item.AudioBlock.Width = width;
            }
        }

        UpdatePlayheadVisual();
        RenderProgramCutMarkers();
        ScheduleTimelineSettleRefresh();
    }

    private void UpdateTrimVisualInPlace(MontageClipItem trimmedClip, double pxPerSec)
    {
        void UpdateClipBlock(MontageClipItem clip)
        {
            double left = clip.TimelineStart.TotalSeconds * pxPerSec;
            double width = Math.Max(4, clip.Duration.TotalSeconds * pxPerSec);
            foreach (RenderedTimelineClip item in _renderedTimelineClips)
            {
                if (!ReferenceEquals(item.Clip, clip)) continue;
                if (item.VideoBlock != null) { Canvas.SetLeft(item.VideoBlock, left); item.VideoBlock.Width = width; }
                if (item.AudioBlock != null) { Canvas.SetLeft(item.AudioBlock, left); item.AudioBlock.Width = width; }
            }
        }

        UpdateClipBlock(trimmedClip);
        if (_project.FindLinkedPartner(trimmedClip) is { } linkedPartner)
            UpdateClipBlock(linkedPartner);
    }

    private void ScheduleTimelineSettleRefresh()
    {
        if (_timelineSettleTimer == null)
        {
            _timelineSettleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(75)
            };
            _timelineSettleTimer.Tick += (_, _) =>
            {
                _timelineSettleTimer.Stop();
                if (_dragMode == TimelineDragMode.None && EditorPanel.Visibility == Visibility.Visible)
                    RenderTimeline();
            };
        }

        _timelineSettleTimer.Stop();
        _timelineSettleTimer.Start();
    }

    private Canvas? GetVideoTrackCanvas(int layer) => layer switch
    {
        2 => TimelineV3Canvas,
        1 => TimelineV2Canvas,
        _ => TimelineClipsCanvas
    };

    private Canvas? GetAudioTrackCanvas(int trackLayer) => trackLayer switch
    {
        0 => TimelineAudioCanvas,
        1 => TimelineAudio2Canvas,
        2 => TimelineAudio3Canvas,
        _ => TimelineAudio4Canvas
    };

    private void RenderTimeline()
    {
        _moveSettleGeneration++;
        _timelineSettleTimer?.Stop();
        SyncAllDynamicTrackRows(animate: true);
        double totalWidth = ComputeTimelineSurfaceWidth();

        TimelineRulerCanvas.Children.Clear();
        TimelineV3Canvas?.Children.Clear();
        TimelineV2Canvas?.Children.Clear();
        TimelineClipsCanvas.Children.Clear();
        TimelineAudioCanvas.Children.Clear();
        TimelineAudio2Canvas?.Children.Clear();
        TimelineAudio3Canvas?.Children.Clear();
        TimelineAudio4Canvas?.Children.Clear();
        _renderedTimelineClips.Clear();

        TimeSpan totalDur = _project.TotalDuration;
        if (totalDur <= TimeSpan.Zero || !HasAnyTimelineClips())
        {
            _pendingEntranceClipIds.Clear();
            _renderedMinX = 0;
            _renderedMaxX = totalWidth;
            _renderedTotalWidth = totalWidth;
            RenderTimeRuler(totalWidth, _currentTimelinePxPerSec, 0, totalWidth);
            UpdatePlayheadVisual();
            RenderProgramCutMarkers();
            return;
        }

        double pxPerSec = _currentTimelinePxPerSec;
        var (renderMinX, renderMaxX, _, _) = GetVisiblePixelWindow(totalWidth);
        _renderedMinX = renderMinX;
        _renderedMaxX = renderMaxX;
        _renderedTotalWidth = totalWidth;

        RenderTimeRuler(totalWidth, pxPerSec, renderMinX, renderMaxX);

        foreach (var (gapStart, gapEnd) in _project.GetGaps(0))
        {
            double gapLeft = gapStart.TotalSeconds * pxPerSec;
            double gapWidth = (gapEnd - gapStart).TotalSeconds * pxPerSec;
            if (gapWidth < 4) continue;
            Border gapBlock = BuildTimelineGapBlock(gapStart, gapEnd, gapWidth);
            Canvas.SetLeft(gapBlock, gapLeft);
            Canvas.SetTop(gapBlock, 5);
            TimelineClipsCanvas.Children.Add(gapBlock);
        }

        bool multiSource = _project.AllClips
            .Select(c => c.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;

        for (int vLayer = 0; vLayer < MontageProject.MaxVideoLayers; vLayer++)
        {
            Canvas? canvas = GetVideoTrackCanvas(vLayer);
            if (canvas == null) continue;
            var track = _project.GetVideoTrack(vLayer);
            for (int i = 0; i < track.Count; i++)
            {
                MontageClipItem vClip = track[i];
                double left = vClip.TimelineStart.TotalSeconds * pxPerSec;
                double width = Math.Max(4, vClip.Duration.TotalSeconds * pxPerSec);
                double visLeft = Math.Max(0, renderMinX - left);
                double visRight = Math.Min(width, renderMaxX - left);

                var (vBlock, tileSlots) = BuildPremiereVideoBlock(vClip, left, width, pxPerSec, visLeft, visRight, multiSource);
                Canvas.SetLeft(vBlock, left);
                Canvas.SetTop(vBlock, 3);
                canvas.Children.Add(vBlock);

                if (_pendingEntranceClipIds.Contains(vClip.Id))
                {
                    string accent = vLayer switch { 2 => "#0EA5E9", 1 => "#38BDF8", _ => "#4EA1FF" };
                    AnimateClipEntrance(vBlock, slideFromY: vLayer > 0 ? -12.0 : -8.0, accentHex: accent);
                }

                _renderedTimelineClips.Add(new RenderedTimelineClip(vClip, vBlock, null, tileSlots, null));
            }
        }

        for (int aLayer = 0; aLayer < MontageProject.MaxAudioTracks; aLayer++)
        {
            Canvas? canvas = GetAudioTrackCanvas(aLayer);
            if (canvas == null) continue;
            var track = _project.GetAudioTrack(aLayer);
            for (int i = 0; i < track.Count; i++)
            {
                MontageClipItem aClip = track[i];
                double left = aClip.TimelineStart.TotalSeconds * pxPerSec;
                double width = Math.Max(4, aClip.Duration.TotalSeconds * pxPerSec);
                double visLeft = Math.Max(0, renderMinX - left);
                double visRight = Math.Min(width, renderMaxX - left);

                var (aBlock, audioHandle) = BuildPremiereAudioBlock(aClip, width, visLeft, visRight, multiSource);
                Canvas.SetLeft(aBlock, left);
                Canvas.SetTop(aBlock, 3);
                canvas.Children.Add(aBlock);

                if (_pendingEntranceClipIds.Contains(aClip.Id))
                    AnimateClipEntrance(aBlock, slideFromY: 8.0, accentHex: "#2DD4BF");

                _renderedTimelineClips.Add(new RenderedTimelineClip(aClip, null, aBlock, Array.Empty<FilmstripTileSlot>(), audioHandle));
            }
        }

        _pendingEntranceClipIds.Clear();
        UpdatePlayheadVisual();
        RenderProgramCutMarkers();
        if (_dragMode == TimelineDragMode.None)
            ScheduleViewportFrameRefinement();
    }

    private Border BuildTimelineGapBlock(TimeSpan gapStart, TimeSpan gapEnd, double width)
    {
        TimeSpan gapDur = gapEnd - gapStart;

        void CloseThisGap()
        {
            if (_project.RippleDeleteGap(gapStart, gapEnd, videoLayer: 0))
            {
                _montagePosition = ClampMontageTime(_montagePosition >= gapEnd ? _montagePosition - gapDur : gapStart);
                _ = SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
                UpdateProjectHeaderAndHud();
                UpdateTimeLabels();
                UpdateExportSummaryHint();
                RenderTimeline();
                ShowTimelineToast($"Closed gap ({MontageProject.FormatShort(gapDur)}) — clips joined");
            }
        }

        var gapMenu = new ContextMenu { Style = (Style)FindResource("StudioContextMenu") };
        var rippleItem = new MenuItem
        {
            Header = $"Ripple Delete Gap ({MontageProject.FormatShort(gapDur)})",
            Style = (Style)FindResource("StudioMenuItem")
        };
        rippleItem.Click += (_, _) => CloseThisGap();
        gapMenu.Items.Add(rippleItem);

        var gapBox = new Border
        {
            Width = width,
            Height = TimelineV1RowHeight - 10.0,
            CornerRadius = new CornerRadius(3),
            Background = BrushFrom("#0F131A"),
            BorderBrush = BrushFrom("#2A3648"),
            BorderThickness = new Thickness(1),
            ContextMenu = gapMenu,
            ToolTip = $"Empty black gap ({MontageProject.FormatShort(gapDur)}) — Double-click or click Close Gap to join clips"
        };

        gapBox.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                CloseThisGap();
                e.Handled = true;
            }
        };

        if (width >= 44)
        {
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (width >= 60)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = width >= 104 ? $"GAP · {MontageProject.FormatShort(gapDur)}" : "GAP",
                    Foreground = BrushFrom("#7C8DA6"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            var closePill = new Border
            {
                Background = BrushFrom("#14332A"),
                BorderBrush = BrushFrom("#2E8B64"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(width >= 82 ? 7 : 5, 2, width >= 82 ? 7 : 5, 2),
                Margin = new Thickness(0, width >= 60 ? 5 : 0, 0, 0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "Close this gap and snap adjacent clips together",
                Child = new TextBlock
                {
                    Text = width >= 82 ? "⇄ Close Gap" : "⇄",
                    Foreground = BrushFrom("#6EE7B7"),
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold
                }
            };
            closePill.MouseLeftButtonDown += (_, e) =>
            {
                CloseThisGap();
                e.Handled = true;
            };
            stack.Children.Add(closePill);
            gapBox.Child = stack;
        }
        return gapBox;
    }

    private void RenderTimeRuler(double totalWidth, double pxPerSec, double minX, double maxX)
    {
        double fps = Math.Clamp(_project.SequenceFps, 12.0, 120.0);
        double frameDur = 1.0 / fps;
        double pxPerFrame = pxPerSec * frameDur;
        int roundedFps = Math.Max(1, (int)Math.Round(fps));

        var tickBrush = BrushFrom("#3E4352");
        var majorBrush = BrushFrom("#7C8598");
        var frameTickBrush = BrushFrom("#46658D");
        var textBrush = BrushFrom("#C5CEDB");

        double startSec = Math.Max(0, minX / Math.Max(1.0, pxPerSec));
        double endSec = Math.Max(startSec, maxX / Math.Max(1.0, pxPerSec));

        if (pxPerFrame >= 12.0)
        {
            int[] frameCandidates = { 1, 2, 5, 10, 15, roundedFps, roundedFps * 2 };
            int majorFrames = frameCandidates[^1];
            foreach (int fc in frameCandidates)
            {
                if (fc * pxPerFrame >= 90) { majorFrames = fc; break; }
            }

            long firstFrame = (long)Math.Floor(startSec * fps);
            long lastFrame = (long)Math.Ceiling(endSec * fps);
            for (long f = firstFrame; f <= lastFrame; f++)
            {
                double x = f * pxPerFrame;
                if (x > totalWidth) break;
                bool isMajor = (f % majorFrames) == 0;
                var tick = new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = isMajor ? 17 : 25,
                    Y2 = 33,
                    Stroke = isMajor ? majorBrush : frameTickBrush,
                    StrokeThickness = isMajor ? 1.2 : 1.0
                };
                TimelineRulerCanvas.Children.Add(tick);

                if (isMajor && x < totalWidth - 44)
                {
                    var label = new TextBlock
                    {
                        Text = MontageProject.FormatSmpteTimecode(TimeSpan.FromSeconds(f / fps), fps),
                        Foreground = textBrush,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold
                    };
                    Canvas.SetLeft(label, x + 4);
                    Canvas.SetTop(label, 2);
                    TimelineRulerCanvas.Children.Add(label);
                }
            }
            return;
        }

        double[] candidates = { 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double majorStep = candidates[^1];
        foreach (double c in candidates)
        {
            if (c * pxPerSec >= 86) { majorStep = c; break; }
        }
        double minorStep = majorStep >= 2 ? majorStep / 5.0 : majorStep / 4.0;
        double alignedStart = Math.Floor(startSec / minorStep) * minorStep;

        for (double s = alignedStart; s <= endSec + 0.001; s += minorStep)
        {
            double x = s * pxPerSec;
            if (x > totalWidth) break;
            double rem = Math.Abs(s % majorStep);
            bool isMajor = rem < 0.005 || Math.Abs(rem - majorStep) < 0.005;

            var tick = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = isMajor ? 17 : 25,
                Y2 = 33,
                Stroke = isMajor ? majorBrush : tickBrush,
                StrokeThickness = isMajor ? 1.2 : 1.0
            };
            TimelineRulerCanvas.Children.Add(tick);

            if (isMajor && x < totalWidth - 44)
            {
                TimeSpan ts = TimeSpan.FromSeconds(Math.Round(s, 3));
                var label = new TextBlock
                {
                    Text = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss\.f"),
                    Foreground = textBrush,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                };
                Canvas.SetLeft(label, x + 4);
                Canvas.SetTop(label, 2);
                TimelineRulerCanvas.Children.Add(label);
            }
        }
    }

    private (Border Block, List<FilmstripTileSlot> TileSlots) BuildPremiereVideoBlock(
        MontageClipItem clip,
        double clipTimelineLeft,
        double clipWidth,
        double pxPerSec,
        double visClipLeft,
        double visClipRight,
        bool multiSource)
    {
        int layer = clip.TrackLayer;
        double blockHeight = layer > 0 ? TimelineOverlayVideoRowHeight - 6.0 : TimelineV1RowHeight - 6.0;
        const double headerHeight = 20.0;
        double filmHeight = blockHeight - headerHeight;
        bool layerMuted = _isVideoTrackMuted[layer];

        var (idleBorder, selHeader, idleHeader, bgHex, pillFg) = layer switch
        {
            2 => ("#1B4D78", "#38BDF8", "#0369A1", "#0F1E2E", "#BAE6FD"),
            1 => ("#1E4D73", "#38BDF8", "#0284C7", "#13202E", "#7DD3FC"),
            _ => ("#203044", "#5B88BD", "#436793", "#1F2630", "#7CE2F7")
        };

        var outer = new Border
        {
            Width = clipWidth,
            Height = blockHeight,
            CornerRadius = new CornerRadius(3),
            Background = BrushFrom(bgHex),
            BorderBrush = clip.IsSelected ? BrushFrom("#E6E6E6") : BrushFrom(idleBorder),
            BorderThickness = new Thickness(clip.IsSelected ? 1.5 : 1),
            Opacity = layerMuted ? 0.45 : 1.0,
            ClipToBounds = true,
            Tag = clip,
            ContextMenu = BuildClipContextMenu(clip)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(filmHeight) });

        var header = new Border
        {
            Background = BrushFrom(clip.IsSelected ? selHeader : idleHeader),
            Padding = new Thickness(7, 0, 7, 0),
            Cursor = Cursors.SizeAll,
            Tag = new ClipMoveGrip(clip),
            ToolTip = $"[{clip.TrackLabel}] {clip.SourceFileName} · Left-click to open audio controls · Drag up/down to switch V1/V2/V3 layers"
        };

        if (clipWidth >= 32)
        {
            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (clipWidth >= 56)
            {
                var grip = new TextBlock
                {
                    Text = "⋮⋮",
                    FontSize = 10,
                    Foreground = BrushFrom("#C2FFFFFF"),
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(grip, 0);
                headerRow.Children.Add(grip);
            }

            string trackPrefix = layer > 0 ? $"[{clip.TrackLabel}] " : (multiSource ? "" : $"#{clip.OrderIndex} ");
            var titleTb = new TextBlock
            {
                Text = $"{trackPrefix}{clip.SourceFileName} · {clip.DisplayDuration}",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleTb, 1);
            headerRow.Children.Add(titleTb);

            if ((clip.HasCustomTransform || clip.IsTrimmed) && clipWidth >= 112)
            {
                string pillText = clip.HasCustomTransform ? clip.ScalePercentLabel : "TRIM";
                var pill = new Border
                {
                    Background = BrushFrom("#3B000000"),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(5, 1, 0, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = pillText,
                        FontSize = 8.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushFrom(clip.HasCustomTransform ? pillFg : "#FDE68A")
                    }
                };
                Grid.SetColumn(pill, 2);
                headerRow.Children.Add(pill);
            }

            header.Child = headerRow;
        }
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var stripCanvas = new Canvas
        {
            Width = clipWidth,
            Height = filmHeight,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        var tileSlots = new List<FilmstripTileSlot>();

        if (visClipRight > visClipLeft)
        {
            _project.Assets.TryGetValue(clip.SourcePath, out SourceMediaAsset? asset);
            double fps = clip.SourceFps > 1 ? clip.SourceFps : 30.0;
            double pxPerFrame = pxPerSec / fps;
            double clipStartSec = clip.Start.TotalSeconds;
            double clipDurSec = Math.Max(0.05, clip.Duration.TotalSeconds);
            bool extracting = IsFramesExtracting(clip.SourcePath);
            double activeMaxDist = extracting ? GetActiveSnapDistance(clip.SourcePath) : double.MaxValue;

            if (pxPerFrame >= 30.0)
            {
                double tileW = pxPerFrame;
                int totalFrameTiles = Math.Max(1, (int)Math.Ceiling(clipDurSec * fps));
                int firstTile = Math.Max(0, (int)Math.Floor(visClipLeft / tileW));
                int lastTile = Math.Min(totalFrameTiles - 1, (int)Math.Ceiling(visClipRight / tileW));
                long baseFrameIdx = MontageProject.TimeToFrameIndex(clip.Start, fps);

                for (int t = firstTile; t <= lastTile; t++)
                {
                    double x = t * tileW;
                    double w = Math.Min(tileW, clipWidth - x);
                    if (w <= 1) continue;

                    double sampleSec = Math.Clamp(clipStartSec + (t + 0.5) / fps, clipStartSec, clip.End.TotalSeconds);
                    double tileMaxDist = extracting ? activeMaxDist : Math.Max(0.03, (1.0 / fps) * 0.65);
                    ImageSource? frame = asset?.FindNearestFrame(sampleSec, tileMaxDist) ??
                                         asset?.FindNearestFrame(sampleSec, double.MaxValue) ??
                                         clip.Thumbnail;

                    var tileContainer = new Grid();
                    var tileBorder = new Border
                    {
                        Width = w,
                        Height = filmHeight,
                        Background = TileBgBrush,
                        BorderBrush = TileSeparatorBrush,
                        BorderThickness = new Thickness(0, 0, 1, 0),
                        ClipToBounds = true
                    };
                    var imageSlotBorder = new Border { ClipToBounds = true };
                    var slot = new FilmstripTileSlot(sampleSec, 1.0 / fps, clipTimelineLeft + x, imageSlotBorder);
                    if (frame != null)
                    {
                        var img = CreateTileImage(frame);
                        imageSlotBorder.Child = img;
                        slot.ImageControl = img;
                        slot.CurrentFrame = frame;
                    }
                    tileContainer.Children.Add(imageSlotBorder);

                    if (w >= 30)
                    {
                        var frameTag = new Border
                        {
                            Background = BrushFrom("#B00D1016"),
                            CornerRadius = new CornerRadius(2),
                            Padding = new Thickness(3, 0, 3, 0),
                            Margin = new Thickness(2),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Child = new TextBlock
                            {
                                Text = $"f{baseFrameIdx + t}",
                                FontFamily = new FontFamily("Consolas"),
                                FontSize = 7.5,
                                Foreground = BrushFrom("#B7D4F5")
                            }
                        };
                        tileContainer.Children.Add(frameTag);
                    }

                    tileBorder.Child = tileContainer;
                    Canvas.SetLeft(tileBorder, x);
                    Canvas.SetTop(tileBorder, 0);
                    stripCanvas.Children.Add(tileBorder);
                    tileSlots.Add(slot);
                }
            }
            else
            {
                double aspect = clip.SourceWidth > 0 && clip.SourceHeight > 0
                    ? Math.Clamp(clip.SourceWidth / (double)clip.SourceHeight, 0.52, 1.85)
                    : 16.0 / 9.0;
                double idealTileW = Math.Clamp(filmHeight * aspect * 0.78, 54, 108);
                int totalTiles = Math.Max(1, (int)Math.Ceiling(clipWidth / idealTileW));
                double tileW = clipWidth / totalTiles;
                double slotSpanSec = clipDurSec / totalTiles;

                int firstTile = Math.Max(0, (int)Math.Floor(visClipLeft / tileW));
                int lastTile = Math.Min(totalTiles - 1, (int)Math.Ceiling(visClipRight / tileW));

                for (int t = firstTile; t <= lastTile; t++)
                {
                    double x = t * tileW;
                    double sampleSec = clipStartSec + clipDurSec * ((t + 0.5) / totalTiles);
                    double tileMaxDist = extracting ? activeMaxDist : double.MaxValue;
                    ImageSource? frame = asset?.FindNearestFrame(sampleSec, tileMaxDist);
                    if (frame == null && !extracting)
                        frame = clip.Thumbnail;

                    var tileBorder = new Border
                    {
                        Width = tileW + 0.5,
                        Height = filmHeight,
                        Background = TileBgBrush,
                        BorderBrush = TileSeparatorBrush,
                        BorderThickness = new Thickness(0, 0, t < totalTiles - 1 ? 1 : 0, 0),
                        ClipToBounds = true
                    };
                    var slot = new FilmstripTileSlot(sampleSec, slotSpanSec, clipTimelineLeft + x, tileBorder);
                    if (frame != null)
                    {
                        var img = CreateTileImage(frame);
                        tileBorder.Child = img;
                        slot.ImageControl = img;
                        slot.CurrentFrame = frame;
                    }

                    Canvas.SetLeft(tileBorder, x);
                    Canvas.SetTop(tileBorder, 0);
                    stripCanvas.Children.Add(tileBorder);
                    tileSlots.Add(slot);
                }
            }
        }
        Grid.SetRow(stripCanvas, 1);
        root.Children.Add(stripCanvas);

        if (clipWidth >= 6)
        {
            double handleW = Math.Clamp(clipWidth * 0.22, 3.0, 8.0);
            root.Children.Add(CreateEdgeTrimHandle(clip, isStart: true, spanRows: 2, handleWidth: handleW));
            root.Children.Add(CreateEdgeTrimHandle(clip, isStart: false, spanRows: 2, handleWidth: handleW));
        }

        outer.Child = root;
        return (outer, tileSlots);
    }

    internal void SetAudioTrackExpanded(bool expanded)
    {
        _isAudioExpanded = expanded;
        if (TrackA1ScaleHint != null) TrackA1ScaleHint.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (AudioStripToggle != null) AudioStripToggle.IsChecked = expanded;
        if (AudioGainStrip != null) AudioGainStrip.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        ApplyTimelineVerticalMetrics();

        if (expanded)
            SyncAudioGainStripUi();

        if (EditorPanel?.Visibility == Visibility.Visible)
            RenderTimeline();
    }

    private double GetAudioTrackRowHeight() => _isAudioExpanded ? 102.0 : 68.0;

    private const double AudioUnityBottomRatio = 0.65;

    private static double VolumeToLineBottomRatio(double volume)
    {
        double clamped = Math.Clamp(volume, 0.0, 2.0);
        return clamped <= 1.0
            ? clamped * AudioUnityBottomRatio
            : AudioUnityBottomRatio + (clamped - 1.0) * 0.31;
    }

    private static double LineBottomRatioToVolume(double bottomRatio)
    {
        double clamped = Math.Clamp(bottomRatio, 0.0, 0.96);
        return clamped <= AudioUnityBottomRatio
            ? Math.Clamp(clamped / AudioUnityBottomRatio, 0.0, 1.0)
            : Math.Clamp(1.0 + (clamped - AudioUnityBottomRatio) / 0.31, 1.0, 2.0);
    }

    private static double ComputeVolumeLineY(double volume, double waveHeight)
    {
        double ratio = VolumeToLineBottomRatio(volume);
        double usable = Math.Max(10.0, waveHeight - 6.0);
        return Math.Clamp((waveHeight - 3.0) - ratio * usable, 3.0, Math.Max(3.0, waveHeight - 3.0));
    }

    private static double LineYToVolume(double localY, double waveHeight)
    {
        double usable = Math.Max(10.0, waveHeight - 6.0);
        double bottomRatio = ((waveHeight - 3.0) - localY) / usable;
        return LineBottomRatioToVolume(bottomRatio);
    }

    private (Border Block, ClipAudioVolumeHandle? AudioHandle) BuildPremiereAudioBlock(
        MontageClipItem clip,
        double clipWidth,
        double visClipLeft,
        double visClipRight,
        bool multiSource)
    {
        int trackLayer = clip.TrackLayer;
        double blockHeight = GetAudioTrackRowHeightForLayer(trackLayer) - 6.0;
        const double headerHeight = 18;
        double waveHeight = Math.Max(18, blockHeight - headerHeight);

        bool trackMuted = _isAudioTrackMuted[trackLayer];
        bool isActiveHighlight = clip.IsSelected || ReferenceEquals(clip, _activeAudioTargetClip);

        var (idleBorder, selHeader, idleHeader, bgHex, waveHex) = trackLayer switch
        {
            1 => ("#1E4D60", "#22D3EE", "#0E7490", "#142730", "#67E8F9"),
            2 => ("#1B486B", "#38BDF8", "#0369A1", "#122330", "#7DD3FC"),
            3 => ("#1B433F", "#2E9E93", "#216E68", "#162C2B", "#5EEAD4"),
            _ => ("#1B363F", "#458C9E", "#326C7B", "#1B2D35", "#7CE2F7")
        };

        var outer = new Border
        {
            Width = clipWidth,
            Height = blockHeight,
            CornerRadius = new CornerRadius(3),
            Background = BrushFrom(bgHex),
            BorderBrush = isActiveHighlight
                ? BrushFrom(clip.IsSelected ? "#E6E6E6" : "#FBBF24")
                : BrushFrom(idleBorder),
            BorderThickness = new Thickness(isActiveHighlight ? 1.5 : 1),
            Opacity = trackMuted ? 0.45 : 1.0,
            ClipToBounds = true,
            Tag = clip,
            ContextMenu = BuildClipContextMenu(clip)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(waveHeight) });

        var header = new Border
        {
            Background = BrushFrom(isActiveHighlight ? selHeader : idleHeader),
            Padding = new Thickness(7, 0, 6, 0),
            Cursor = Cursors.SizeAll,
            Tag = new ClipMoveGrip(clip),
            ToolTip = $"[{clip.TrackLabel}] {clip.SourceFileName} · Drag up/down between A1..A4 or left/right · Trim edges freely"
        };

        TextBlock? headerDbText = null;
        if (clipWidth >= 42)
        {
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleBlock = new TextBlock
            {
                Text = multiSource ? $"{clip.SourceFileName} [{clip.TrackLabel}]" : $"#{clip.OrderIndex} {clip.SourceFileName} [{clip.TrackLabel}]",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFrom("#EBF7FA"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleBlock, 0);
            headerGrid.Children.Add(titleBlock);

            if (clip.HasAudio && clipWidth >= 88)
            {
                headerDbText = new TextBlock
                {
                    Text = clip.AudioVolumeDbLabel,
                    FontSize = 9,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushFrom(clip.AudioVolume <= 0.005
                        ? "#FCA5A5"
                        : clip.IsAudioAdjusted ? "#FDE68A" : "#CBEBF2")
                };
                var dbTriggerPill = new Border
                {
                    Background = BrushFrom("#450A141A"),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(5, 1, 0, 1),
                    Cursor = Cursors.Hand,
                    Tag = new ClipAudioStripTrigger(clip),
                    ToolTip = $"Click to edit {clip.TrackLabel} audio volume strip",
                    Child = headerDbText
                };
                Grid.SetColumn(dbTriggerPill, 1);
                headerGrid.Children.Add(dbTriggerPill);
            }

            header.Child = headerGrid;
        }
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var waveContainer = new Border
        {
            Background = BrushFrom("#14222B"),
            ClipToBounds = true
        };

        ClipAudioVolumeHandle? audioHandle = null;
        _project.Assets.TryGetValue(clip.SourcePath, out SourceMediaAsset? asset);
        if (clip.HasAudio)
        {
            var waveHost = new Canvas
            {
                Height = waveHeight,
                Width = clipWidth,
                Background = Brushes.Transparent,
                ClipToBounds = true
            };

            float[]? peaks = asset?.AudioPeaks;
            System.Windows.Shapes.Path? wavePath = null;
            double visW = 0, visStartNorm = 0, visEndNorm = 1;
            if (peaks is { Length: > 0 } && visClipRight > visClipLeft)
            {
                visW = Math.Max(4, visClipRight - visClipLeft);
                visStartNorm = Math.Clamp(visClipLeft / clipWidth, 0, 1);
                visEndNorm = Math.Clamp(visClipRight / clipWidth, visStartNorm, 1);

                wavePath = new System.Windows.Shapes.Path
                {
                    Fill = BrushFrom(clip.AudioVolume <= 0.005 ? "#496975" : waveHex),
                    Data = BuildClipWaveformGeometry(peaks, clip, visW, waveHeight - 2, visStartNorm, visEndNorm),
                    Width = visW,
                    Height = waveHeight - 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(wavePath, visClipLeft);
                Canvas.SetTop(wavePath, 2);
                waveHost.Children.Add(wavePath);
            }

            double unityY = ComputeVolumeLineY(1.0, waveHeight);
            waveHost.Children.Add(new Line
            {
                X1 = 0,
                X2 = clipWidth,
                Y1 = unityY,
                Y2 = unityY,
                Stroke = BrushFrom("#3896B5"),
                StrokeThickness = 0.8,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                IsHitTestVisible = false
            });

            double lineY = ComputeVolumeLineY(clip.AudioVolume, waveHeight);
            var volumeLine = new Line
            {
                X1 = 0,
                X2 = clipWidth,
                Y1 = lineY,
                Y2 = lineY,
                Stroke = BrushFrom(GetVolumeLineColorHex(clip)),
                StrokeThickness = 1.7,
                IsHitTestVisible = false
            };
            waveHost.Children.Add(volumeLine);

            TextBlock? volumeBadgeText = null;
            Border? volumeBadge = null;
            if (clipWidth >= 56)
            {
                volumeBadgeText = new TextBlock
                {
                    Text = clip.AudioVolumeDbLabel,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushFrom(clip.AudioVolume <= 0.005 ? "#FCA5A5" : "#EBF7FA")
                };
                volumeBadge = new Border
                {
                    Background = BrushFrom("#CC111C22"),
                    BorderBrush = BrushFrom(clip.IsAudioAdjusted ? "#D97706" : "#356C7E"),
                    BorderThickness = new Thickness(0.8),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(4, 0, 4, 0),
                    IsHitTestVisible = false,
                    Visibility = _isAudioExpanded || clip.IsAudioAdjusted ? Visibility.Visible : Visibility.Collapsed,
                    Child = volumeBadgeText
                };
                Canvas.SetLeft(volumeBadge, 6);
                Canvas.SetTop(volumeBadge, Math.Max(2, lineY > waveHeight * 0.45 ? lineY - 15 : lineY + 3));
                waveHost.Children.Add(volumeBadge);
            }

            var hitStrip = new Border
            {
                Width = clipWidth,
                Height = 8,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeNS,
                ToolTip = $"Clip Audio Level: {clip.AudioVolumeDbLabel} ({Math.Round(clip.AudioVolume * 100)}%) — Drag up/down or double-click to reset 0 dB"
            };
            Canvas.SetLeft(hitStrip, 0);
            Canvas.SetTop(hitStrip, Math.Clamp(lineY - 4, 0, Math.Max(0, waveHeight - 8)));
            waveHost.Children.Add(hitStrip);

            audioHandle = new ClipAudioVolumeHandle(
                clip, waveHeight, waveHost, peaks, wavePath, volumeLine, hitStrip,
                volumeBadge, volumeBadgeText, headerDbText, visStartNorm, visEndNorm, visW);
            hitStrip.Tag = audioHandle;
            hitStrip.MouseEnter += (_, _) =>
            {
                audioHandle.IsDraggingOrHover = true;
                volumeLine.Stroke = BrushFrom("#FFFFFF");
                volumeLine.StrokeThickness = 2.2;
                if (volumeBadge != null) volumeBadge.Visibility = Visibility.Visible;
            };
            hitStrip.MouseLeave += (_, _) =>
            {
                if (_dragMode == TimelineDragMode.DragAudioVolume && ReferenceEquals(_dragVolumeHandle, audioHandle))
                    return;
                audioHandle.IsDraggingOrHover = false;
                volumeLine.Stroke = BrushFrom(GetVolumeLineColorHex(clip));
                volumeLine.StrokeThickness = 1.7;
                if (volumeBadge != null && !_isAudioExpanded && !clip.IsAudioAdjusted)
                    volumeBadge.Visibility = Visibility.Collapsed;
            };

            waveContainer.Child = waveHost;
        }
        else if (clipWidth > 44)
        {
            waveContainer.IsHitTestVisible = false;
            waveContainer.Child = new TextBlock
            {
                Text = "Silent",
                FontSize = 8.5,
                Foreground = BrushFrom("#64828E"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
        }
        Grid.SetRow(waveContainer, 1);
        root.Children.Add(waveContainer);

        if (clipWidth >= 6)
        {
            double handleW = Math.Clamp(clipWidth * 0.22, 3.0, 8.0);
            root.Children.Add(CreateEdgeTrimHandle(clip, isStart: true, spanRows: 2, handleWidth: handleW));
            root.Children.Add(CreateEdgeTrimHandle(clip, isStart: false, spanRows: 2, handleWidth: handleW));
        }

        outer.Child = root;
        return (outer, audioHandle);
    }

    private Border CreateEdgeTrimHandle(MontageClipItem clip, bool isStart, int spanRows, double handleWidth = 8.0)
    {
        var gripBar = new Border
        {
            Width = handleWidth < 5.0 ? 1.5 : 2.0,
            Margin = new Thickness(isStart ? 1 : 0, 6, isStart ? 0 : 1, 6),
            CornerRadius = new CornerRadius(1),
            Background = TrimGripIdleBrush,
            HorizontalAlignment = isStart ? HorizontalAlignment.Left : HorizontalAlignment.Right
        };

        var handle = new Border
        {
            Width = handleWidth,
            Background = Brushes.Transparent,
            HorizontalAlignment = isStart ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Cursor = Cursors.SizeWE,
            Tag = new ClipEdgeHandle(clip, isStart),
            ToolTip = isStart
                ? "Drag left edge to trim Clip Start (In)"
                : "Drag right edge to trim Clip End (Out)",
            Child = gripBar
        };
        handle.MouseEnter += (_, _) => gripBar.Background = TrimGripHoverBrush;
        handle.MouseLeave += (_, _) => gripBar.Background = TrimGripIdleBrush;
        Grid.SetRowSpan(handle, spanRows);
        return handle;
    }

    private static Geometry BuildClipWaveformGeometry(
        float[] peaks,
        MontageClipItem clip,
        double width,
        double height,
        double clipVisStartNorm,
        double clipVisEndNorm)
    {
        var geom = new StreamGeometry();
        double srcSec = Math.Max(0.1, clip.SourceDuration.TotalSeconds);
        double clipStartRatio = Math.Clamp(clip.Start.TotalSeconds / srcSec, 0, 1);
        double clipEndRatio = Math.Clamp(clip.End.TotalSeconds / srcSec, clipStartRatio, 1);

        double rangeStart = clipStartRatio + (clipEndRatio - clipStartRatio) * clipVisStartNorm;
        double rangeEnd = clipStartRatio + (clipEndRatio - clipStartRatio) * clipVisEndNorm;

        int barCount = Math.Clamp((int)(width / 2.4), 8, 420);
        double barStep = width / barCount;
        double barW = Math.Max(1.1, barStep * 0.72);
        double volScale = Math.Clamp(clip.AudioVolume, 0.0, 2.0);
        bool isMuted = clip.AudioVolume <= 0.005;

        using (StreamGeometryContext ctx = geom.Open())
        {
            for (int b = 0; b < barCount; b++)
            {
                double r = rangeStart + (rangeEnd - rangeStart) * ((b + 0.5) / barCount);
                int idx = Math.Clamp((int)(r * (peaks.Length - 1)), 0, peaks.Length - 1);
                double amp = peaks[idx] * volScale;
                double barH = isMuted ? 1.5 : Math.Clamp(amp * height, 2.0, height);
                double x = b * barStep;
                double yTop = height - barH;

                ctx.BeginFigure(new Point(x, height), true, true);
                ctx.LineTo(new Point(x, yTop), false, false);
                ctx.LineTo(new Point(x + barW, yTop), false, false);
                ctx.LineTo(new Point(x + barW, height), false, false);
            }
        }
        geom.Freeze();
        return geom;
    }

    private void RenderJogTicks(double offsetPx)
    {
        if (JogTicksCanvas == null || JogWheelBorder == null) return;
        JogTicksCanvas.Children.Clear();

        double w = JogWheelBorder.ActualWidth > 0 ? JogWheelBorder.ActualWidth : JogWheelBorder.Width;
        double h = JogWheelBorder.ActualHeight > 0 ? JogWheelBorder.ActualHeight : JogWheelBorder.Height;
        if (w <= 0) w = 226;
        if (h <= 0) h = 28;

        const double spacing = 9.0;
        double phase = ((offsetPx % spacing) + spacing) % spacing;
        var minorTick = BrushFrom("#383E4F");
        var majorTick = BrushFrom("#5B657E");

        int index = 0;
        for (double x = phase; x < w; x += spacing)
        {
            bool isMajor = (index % 3) == 0;
            JogTicksCanvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = isMajor ? 3 : 7,
                Y2 = h - (isMajor ? 3 : 7),
                Stroke = isMajor ? majorTick : minorTick,
                StrokeThickness = isMajor ? 1.2 : 1.0
            });
            index++;
        }
    }

    private void RenderProgramCutMarkers()
    {
        if (ProgramClipMarkersCanvas == null) return;
        ProgramClipMarkersCanvas.Children.Clear();

        double w = ProgramClipMarkersCanvas.ActualWidth;
        if (w <= 20) w = ProgramScrubSlider?.ActualWidth ?? 0;
        double totalSec = _project.TotalDuration.TotalSeconds;
        if (w <= 20 || totalSec <= 0 || !_project.AllVideoClips.Any()) return;

        var markerBrush = BrushFrom("#C8E2FF");
        foreach (MontageClipItem c in _project.AllVideoClips)
        {
            if (c.TimelineStart > TimeSpan.FromMilliseconds(20))
                AddMarker(c.TimelineStart.TotalSeconds);
            if (c.TimelineEnd < _project.TotalDuration - TimeSpan.FromMilliseconds(20))
                AddMarker(c.TimelineEnd.TotalSeconds);
        }

        void AddMarker(double sec)
        {
            double x = Math.Clamp((sec / totalSec) * w, 2, w - 2);
            var tick = new Rectangle
            {
                Width = 2.0,
                Height = 12,
                Fill = markerBrush,
                Opacity = 0.78
            };
            Canvas.SetLeft(tick, x - 1.0);
            Canvas.SetTop(tick, 1);
            ProgramClipMarkersCanvas.Children.Add(tick);
        }
    }
}
