using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using CortexFX.Core.Video;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    // Timeline Zoom, Scroll, Tools & Snapping

    private void TimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EditorPanel.Visibility == Visibility.Visible)
            UpdateTimelineLayoutInPlace();
    }

    private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) >= 0.5)
            TrackHeaderScrollViewer?.ScrollToVerticalOffset(e.VerticalOffset);

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

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBySliderDelta(-10.0, anchorPlayhead: true);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBySliderDelta(10.0, anchorPlayhead: true);

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
                        TimelineToolMode.Move => "Reorder (A) · Drag clip across time or tracks · Esc",
                        _ => string.Empty
                    };
                }
            }
        }

        if (tool != TimelineToolMode.Razor && RazorGuideLine != null)
            RazorGuideLine.Visibility = Visibility.Collapsed;
    }

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

        foreach (MontageClipItem c in _project.AllClips)
        {
            Consider(c.TimelineStart);
            Consider(c.TimelineEnd);
        }

        return best;
    }

    // Direct Timeline Pointer Interactions

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_project.TotalDuration <= TimeSpan.Zero || TimelineSurface.ActualWidth <= 0) return;

        Point pos = e.GetPosition(TimelineSurface);
        double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
        double totalSec = Math.Max(0.05, _project.TotalDuration.TotalSeconds);
        double pxPerSec = _currentTimelinePxPerSec > 0 ? _currentTimelinePxPerSec : (totalW / totalSec);
        bool inRuler = pos.Y <= TimelineRulerHeight;

        _pressedTimelineOrigin = pos;
        _pressedTimelineClip = null;

        if (inRuler)
        {
            StartScrubPlayhead(pos.X, totalW, totalSec);
            e.Handled = true;
            return;
        }

        if (_activeTool == TimelineToolMode.Razor)
        {
            HandleRazorClick(pos, pxPerSec, totalW, totalSec);
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
            _dragOrigStart = edgeHandle.Clip.Start;
            _dragOrigEnd = edgeHandle.Clip.End;
            _dragOrigTimelineStart = edgeHandle.Clip.TimelineStart;
            _preTrimMontagePosition = _montagePosition;
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
            _activeAudioTargetClip = volumeHandle.Clip;
            SyncAudioGainStripUi();
            if (e.ClickCount == 2)
            {
                ResetClipAudioVolume(volumeHandle.Clip);
                e.Handled = true;
                return;
            }

            _dragMode = TimelineDragMode.PendingAudioVolumeDrag;
            _dragVolumeHandle = volumeHandle;
            _dragTargetClip = volumeHandle.Clip;
            _dragStartY = pos.Y;
            _dragOrigClipVolume = volumeHandle.Clip.AudioVolume;
            TimelineSurface.CaptureMouse();
            e.Handled = true;
            return;
        }

        MontageClipItem? hitClip = FindAncestorTag<MontageClipItem>(source);
        ClipMoveGrip? moveGrip = FindAncestorTag<ClipMoveGrip>(source);
        _pressedTimelineClip = hitClip;

        if (hitClip != null && (_activeTool == TimelineToolMode.Move || moveGrip != null))
        {
            _dragMode = TimelineDragMode.PendingMoveOrClick;
            _dragTargetClip = hitClip;
            _dragStartX = pos.X;
            _dragStartY = pos.Y;
            _dragPxPerSec = pxPerSec;
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

    private void HandleRazorClick(Point pos, double pxPerSec, double totalW, double totalSec)
    {
        TimeSpan rawSplit = TimeSpan.FromSeconds(Math.Clamp(pos.X / pxPerSec, 0, totalSec));
        TimeSpan snapped = ApplyTimelineSnap(rawSplit, totalW, totalSec, includePlayhead: true);
        snapped = MontageProject.SnapToFrame(snapped, _project.SequenceFps);

        bool inAudioArea = pos.Y >= A1TopY;
        TimelineTrackKind kind = inAudioArea ? TimelineTrackKind.Audio : TimelineTrackKind.Video;
        int layer = inAudioArea ? ResolveAudioDropLayerFromY(pos.Y) : ResolveVideoDropLayerFromY(pos.Y);

        _ = PausePlaybackAsync();
        var (ok, second) = _project.SplitAtMontageTime(snapped, kind, layer);
        if (ok && second != null)
        {
            _montagePosition = second.TimelineStart;
            _ = SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
            UpdateExportSummaryHint();
            RenderTimeline();
            ShowTimelineToast($"Split [{second.TrackLabel}] → Clip #{second.OrderIndex - 1} & #{second.OrderIndex}");
        }
        else
        {
            ShowTimelineToast("Too close to clip edge to split");
        }
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
            EndTimelineDrag(pos);
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
                if ((Math.Abs(pos.X - _dragStartX) >= 6.0 || Math.Abs(pos.Y - _dragStartY) >= 6.0) && _dragTargetClip != null)
                {
                    _dragMode = TimelineDragMode.MoveClip;
                    BeginMoveClipAnimation(_dragTargetClip, _dragStartX, pos.Y, totalW, totalSec);
                    UpdateMoveClipAnimation(pos.X, pos.Y, totalW);
                }
                break;

            case TimelineDragMode.MoveClip:
                UpdateMoveClipAnimation(pos.X, pos.Y, totalW);
                break;

            case TimelineDragMode.PendingAudioVolumeDrag:
                if (Math.Abs(pos.Y - _dragStartY) >= 4.0 && _dragVolumeHandle != null)
                {
                    _dragMode = TimelineDragMode.DragAudioVolume;
                    _project.BeginUndoStep();
                    ProcessAudioVolumeDrag(e);
                }
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
            if (pos.Y > TimelineRulerHeight && totalSec > 0)
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
            EndTimelineDrag(e.GetPosition(TimelineSurface));
        e.Handled = true;
    }

    private void Timeline_MouseLeave(object sender, MouseEventArgs e)
    {
        if (RazorGuideLine != null)
            RazorGuideLine.Visibility = Visibility.Collapsed;
        if (_dragMode == TimelineDragMode.None && TimelineDragBadge != null)
            TimelineDragBadge.Visibility = Visibility.Collapsed;
        if (_dragMode != TimelineDragMode.None && e.LeftButton != MouseButtonState.Pressed)
            EndTimelineDrag(e.GetPosition(TimelineSurface));
    }

    private void Timeline_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragMode != TimelineDragMode.None)
            EndTimelineDrag(Mouse.GetPosition(TimelineSurface));
    }

    private void EndTimelineDrag(Point releasePos)
    {
        double totalW = Math.Max(1, TimelineSurface.ActualWidth > 0 ? TimelineSurface.ActualWidth : TimelineSurface.Width);
        double totalSec = Math.Max(0.05, _project.TotalDuration.TotalSeconds);
        TimelineDragMode finishedMode = _dragMode;
        bool wasSimpleClick =
            Math.Abs(releasePos.X - _pressedTimelineOrigin.X) < 5.0 &&
            Math.Abs(releasePos.Y - _pressedTimelineOrigin.Y) < 5.0;
        MontageClipItem? clickedClip = _pressedTimelineClip ?? _dragTargetClip;
        _pressedTimelineClip = null;

        switch (finishedMode)
        {
            case TimelineDragMode.ScrubPlayhead:
                ProcessScrubDrag(releasePos.X, totalW, totalSec, finalSeek: true);
                if (wasSimpleClick && clickedClip != null)
                    HandleTimelineClipLeftClick(clickedClip);
                break;

            case TimelineDragMode.TrimStart:
            case TimelineDragMode.TrimEnd:
                ProcessTrimDrag(releasePos.X, totalW, totalSec);
                _project.CommitUndoStep();
                if (_dragTargetClip != null)
                {
                    TimeSpan targetTime = _dragTargetClip.IsAudioClip
                        ? ClampMontageTime(_preTrimMontagePosition)
                        : (finishedMode == TimelineDragMode.TrimStart
                            ? _dragTargetClip.TimelineStart
                            : ClampMontageTime(_dragTargetClip.TimelineEnd - TimeSpan.FromMilliseconds(15)));

                    _ = SyncPlayerToMontagePositionAsync(targetTime, autoPlay: false, forceExactSeek: true);
                    ShowTimelineToast($"Trimmed {_dragTargetClip.TrackLabel} Clip #{_dragTargetClip.OrderIndex} ({_dragTargetClip.DisplayDuration})");
                }
                break;

            case TimelineDragMode.PendingMoveOrClick:
                ProcessScrubDrag(releasePos.X, totalW, totalSec, finalSeek: true);
                if (wasSimpleClick && clickedClip != null)
                    HandleTimelineClipLeftClick(clickedClip);
                break;

            case TimelineDragMode.MoveClip:
                if (_dragTargetClip != null)
                {
                    bool moved = FinishMoveClipAnimation(_dragTargetClip);
                    if (moved)
                    {
                        _montagePosition = _dragTargetClip.TimelineStart;
                        _ = SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
                        ShowTimelineToast($"Moved to {_dragTargetClip.TrackLabel} @ {MontageProject.FormatShort(_dragTargetClip.TimelineStart)}");
                    }
                    _dragMode = TimelineDragMode.None;
                    _dragTargetClip = null;
                    TimelineDragBadge.Visibility = Visibility.Collapsed;
                    if (TimelineSurface.IsMouseCaptured)
                        TimelineSurface.ReleaseMouseCapture();
                    UpdateProjectHeaderAndHud();
                    UpdateTimeLabels();
                    UpdateExportSummaryHint();
                    return;
                }
                break;

            case TimelineDragMode.PendingAudioVolumeDrag:
                _dragMode = TimelineDragMode.None;
                _dragVolumeHandle = null;
                _dragTargetClip = null;
                if (TimelineSurface.IsMouseCaptured)
                    TimelineSurface.ReleaseMouseCapture();
                if (clickedClip != null)
                    OpenAudioControlForClip(clickedClip);
                RenderTimeline();
                return;

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
                    ShowTimelineToast($"{volClip.TrackLabel} Audio → {volClip.AudioVolumeDbLabel}");
                }
                return;
        }

        _dragMode = TimelineDragMode.None;
        _dragTargetClip = null;
        _dragVolumeHandle = null;
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

    private bool TrySnapTrimTimelineEdge(MontageClipItem trimmedClip, TimeSpan rawTimelineEdge, double pxPerSec, out TimeSpan snappedEdge)
    {
        TimeSpan bestEdge = rawTimelineEdge;
        if (!_isSnapEnabled || pxPerSec <= 0)
        {
            snappedEdge = bestEdge;
            return false;
        }

        double thresholdSec = 8.0 / pxPerSec;
        double bestDist = thresholdSec;
        bool snapped = false;

        void Consider(TimeSpan candidate)
        {
            double d = Math.Abs((candidate - rawTimelineEdge).TotalSeconds);
            if (d < bestDist)
            {
                bestDist = d;
                bestEdge = candidate;
                snapped = true;
            }
        }

        Consider(TimeSpan.Zero);
        Consider(_montagePosition);
        foreach (MontageClipItem other in _project.AllClips)
        {
            if (ReferenceEquals(other, trimmedClip)) continue;
            Consider(other.TimelineStart);
            Consider(other.TimelineEnd);
        }

        snappedEdge = bestEdge;
        return snapped;
    }

    private void ProcessTrimDrag(double x, double totalW, double totalSec)
    {
        MontageClipItem? clip = _dragTargetClip;
        if (clip == null) return;

        double fps = clip.SourceFps > 1 ? clip.SourceFps : _project.SequenceFps;
        double pxPerSec = Math.Max(1.0, _dragPxPerSec);
        double deltaSec = (x - _dragStartX) / pxPerSec;
        bool isStart = _dragMode == TimelineDragMode.TrimStart;
        bool snappedLine = false;

        if (isStart)
        {
            TimeSpan candidate = MontageProject.SnapToFrame(_dragOrigStart + TimeSpan.FromSeconds(deltaSec), fps);
            TimeSpan rawTimelineEdge = _dragOrigTimelineStart + (candidate - _dragOrigStart);
            if (TrySnapTrimTimelineEdge(clip, rawTimelineEdge, pxPerSec, out TimeSpan snappedEdge))
            {
                candidate = _dragOrigStart + (snappedEdge - _dragOrigTimelineStart);
                snappedLine = true;
            }

            _project.TrimClipStart(clip, candidate, recordUndo: false);
            UpdateTrimVisualInPlace(clip, pxPerSec);

            TimeSpan delta = clip.Start - _dragOrigStart;
            string sign = delta >= TimeSpan.Zero ? "+" : "-";
            double edgeX = clip.TimelineStart.TotalSeconds * pxPerSec;
            UpdateTrimSnapGuide(snappedLine, edgeX, totalW);
            ShowDragBadge($"IN {MontageProject.FormatSmpteTimecode(clip.Start, fps)} ({sign}{MontageProject.FormatShort(delta.Duration())})", edgeX, totalW);
            if (!clip.IsAudioClip)
                _ = SyncPlayerToMontagePositionAsync(clip.TimelineStart, autoPlay: false, forceExactSeek: false);
        }
        else
        {
            TimeSpan candidate = MontageProject.SnapToFrame(_dragOrigEnd + TimeSpan.FromSeconds(deltaSec), fps);
            TimeSpan rawTimelineEdge = clip.TimelineStart + (candidate - clip.Start);
            if (TrySnapTrimTimelineEdge(clip, rawTimelineEdge, pxPerSec, out TimeSpan snappedEdge))
            {
                candidate = clip.Start + (snappedEdge - clip.TimelineStart);
                snappedLine = true;
            }

            _project.TrimClipEnd(clip, candidate, recordUndo: false);
            UpdateTrimVisualInPlace(clip, pxPerSec);

            TimeSpan delta = clip.End - _dragOrigEnd;
            string sign = delta >= TimeSpan.Zero ? "+" : "-";
            double edgeX = clip.TimelineEnd.TotalSeconds * pxPerSec;
            UpdateTrimSnapGuide(snappedLine, edgeX, totalW);
            ShowDragBadge($"OUT {MontageProject.FormatSmpteTimecode(clip.End, fps)} ({sign}{MontageProject.FormatShort(delta.Duration())})", edgeX, totalW);

            if (!clip.IsAudioClip)
            {
                TimeSpan previewEnd = ClampMontageTime(clip.TimelineEnd - TimeSpan.FromMilliseconds(10));
                _ = SyncPlayerToMontagePositionAsync(previewEnd, autoPlay: false, forceExactSeek: false);
            }
        }

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
    }

    private void UpdateTrimSnapGuide(bool snappedLine, double edgeX, double totalW)
    {
        if (DropInsertionLine == null) return;
        if (snappedLine)
        {
            Canvas.SetLeft(DropInsertionLine, Math.Clamp(edgeX - 1.0, 0, Math.Max(0, totalW - 2.5)));
            DropInsertionLine.Visibility = Visibility.Visible;
        }
        else
        {
            DropInsertionLine.Visibility = Visibility.Collapsed;
        }
    }

    // Free Multi-Track NLE Timeline Drag-Move (Across V1..V3 and A1..A4, Snap Aware)

    private void BeginMoveClipAnimation(MontageClipItem draggedClip, double mouseX, double mouseY, double totalW, double totalSec)
    {
        _moveSettleGeneration++;
        _movePxPerSec = _currentTimelinePxPerSec > 0 ? _currentTimelinePxPerSec : (totalSec > 0 ? totalW / totalSec : 10.0);

        RenderedTimelineClip? rendered = _renderedTimelineClips.FirstOrDefault(r => ReferenceEquals(r.Clip, draggedClip));
        double origLeft = draggedClip.TimelineStart.TotalSeconds * _movePxPerSec;
        double width = Math.Max(4, draggedClip.Duration.TotalSeconds * _movePxPerSec);

        _moveDragSnapshots = new List<MoveClipDragSnapshot>
        {
            new(draggedClip, rendered?.VideoBlock, rendered?.AudioBlock, origLeft, width)
        };
        _moveResolvedTargetTime = draggedClip.TimelineStart;
        _moveTargetLayer = draggedClip.TrackLayer;
        _moveMouseOffsetX = Math.Clamp(mouseX - origLeft, 0, width);

        ApplyLiftAnimation(rendered?.VideoBlock, liftY: -4.0);
        ApplyLiftAnimation(rendered?.AudioBlock, liftY: -2.0);

        MoveSlotPreviewRect.BeginAnimation(Canvas.LeftProperty, null);
        MoveSlotPreviewRect.BeginAnimation(Canvas.TopProperty, null);
        MoveSlotPreviewRect.BeginAnimation(HeightProperty, null);
        MoveSlotPreviewRect.Width = width;
        Canvas.SetLeft(MoveSlotPreviewRect, origLeft);
        ApplyMoveSlotVerticalBounds(draggedClip, animate: false);

        MoveSlotPreviewRect.Opacity = 0;
        MoveSlotPreviewRect.Visibility = Visibility.Visible;
        MoveSlotPreviewRect.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
    }

    private (double Top, double Height) GetMoveSlotVerticalBounds(MontageClipItem draggedClip)
    {
        if (draggedClip.IsAudioClip)
        {
            int aLayer = Math.Clamp(_moveTargetLayer, 0, MontageProject.MaxAudioTracks - 1);
            return (GetAudioTrackTopY(aLayer) + 3.0, Math.Max(26, GetAudioTrackRowHeightForLayer(aLayer) - 6.0));
        }

        int vLayer = Math.Clamp(_moveTargetLayer, 0, MontageProject.MaxVideoLayers - 1);
        double h = vLayer > 0 ? TimelineOverlayVideoRowHeight - 6.0 : TimelineV1RowHeight - 6.0;
        return (GetVideoTrackTopY(vLayer) + 3.0, h);
    }

    private void ApplyMoveSlotVerticalBounds(MontageClipItem draggedClip, bool animate = false)
    {
        var (targetTop, targetHeight) = GetMoveSlotVerticalBounds(draggedClip);
        if (!animate)
        {
            MoveSlotPreviewRect.BeginAnimation(Canvas.TopProperty, null);
            MoveSlotPreviewRect.BeginAnimation(HeightProperty, null);
            Canvas.SetTop(MoveSlotPreviewRect, targetTop);
            MoveSlotPreviewRect.Height = targetHeight;
            return;
        }

        double fromTop = Canvas.GetTop(MoveSlotPreviewRect);
        if (double.IsNaN(fromTop)) fromTop = targetTop;
        double fromH = double.IsNaN(MoveSlotPreviewRect.Height) || MoveSlotPreviewRect.Height <= 0
            ? targetHeight
            : MoveSlotPreviewRect.Height;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(130);
        MoveSlotPreviewRect.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(fromTop, targetTop, dur) { EasingFunction = ease });
        MoveSlotPreviewRect.BeginAnimation(HeightProperty, new DoubleAnimation(fromH, targetHeight, dur) { EasingFunction = ease });
    }

    private void UpdateMoveClipAnimation(double mouseX, double mouseY, double totalW)
    {
        if (_moveDragSnapshots == null || _moveDragSnapshots.Count == 0)
            return;

        MoveClipDragSnapshot dragged = _moveDragSnapshots[0];
        MontageClipItem clip = dragged.Clip;
        double pxPerSec = Math.Max(1.0, _movePxPerSec);
        double maxDragRight = Math.Max(totalW - dragged.Width + 80, dragged.OrigLeft + 120);
        double floatingLeft = Math.Clamp(mouseX - _moveMouseOffsetX, 0, Math.Max(0, maxDragRight));

        int nextLayer;
        if (clip.IsAudioClip)
        {
            int hoveredAudio = ResolveAudioDropLayerFromY(mouseY);
            if (hoveredAudio == 1) SetTrackDropReveal(TrackRevealSlot.A2, true);
            else if (hoveredAudio == 2) SetTrackDropReveal(TrackRevealSlot.A3, true);
            nextLayer = ResolveAudioDropLayerFromY(mouseY);
        }
        else
        {
            UpdateVideoDropTrackRevealFromY(mouseY);
            nextLayer = ResolveVideoDropLayerFromY(mouseY);
        }

        if (nextLayer != _moveTargetLayer)
        {
            _moveTargetLayer = nextLayer;
            ApplyMoveSlotVerticalBounds(clip, animate: true);
        }

        if (dragged.VideoBlock != null)
        {
            dragged.VideoBlock.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(dragged.VideoBlock, floatingLeft);
        }
        if (dragged.AudioBlock != null)
        {
            dragged.AudioBlock.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(dragged.AudioBlock, floatingLeft);
        }

        TimeSpan rawDesired = TimeSpan.FromSeconds(Math.Max(0.0, floatingLeft / pxPerSec));
        TimeSpan snappedDesired = SnapMovedClipStart(clip, rawDesired, pxPerSec);
        TimeSpan resolvedStart = _project.ResolveMoveTargetTime(clip, snappedDesired, targetTrackLayer: _moveTargetLayer);
        _moveResolvedTargetTime = resolvedStart;

        double previewLeft = resolvedStart.TotalSeconds * pxPerSec;
        MoveSlotPreviewRect.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(MoveSlotPreviewRect, previewLeft);
        Canvas.SetLeft(DropInsertionLine, Math.Clamp(previewLeft - 1.0, 0, Math.Max(0, totalW - 2.5)));
        DropInsertionLine.Visibility = Visibility.Visible;

        string trackLabel = clip.IsAudioClip ? $"A{_moveTargetLayer + 1}" : $"V{_moveTargetLayer + 1}";
        string timeText = MontageProject.FormatSmpteTimecode(resolvedStart, _project.SequenceFps);
        ShowDragBadge($"MOVE [{trackLabel}] → {timeText}", floatingLeft, totalW);
    }

    private TimeSpan SnapMovedClipStart(MontageClipItem clip, TimeSpan desiredStart, double pxPerSec)
    {
        if (!_isSnapEnabled || pxPerSec <= 0)
            return desiredStart;

        double thresholdSec = 9.0 / pxPerSec;
        TimeSpan best = desiredStart;
        double bestDist = thresholdSec;
        TimeSpan dur = clip.Duration;

        void TrySnapEdge(TimeSpan targetEdgeTime)
        {
            double dStart = Math.Abs((desiredStart - targetEdgeTime).TotalSeconds);
            if (dStart < bestDist) { bestDist = dStart; best = targetEdgeTime; }
            if (targetEdgeTime >= dur)
            {
                TimeSpan startForEndSnap = targetEdgeTime - dur;
                double dEnd = Math.Abs((desiredStart - startForEndSnap).TotalSeconds);
                if (dEnd < bestDist) { bestDist = dEnd; best = startForEndSnap; }
            }
        }

        TrySnapEdge(TimeSpan.Zero);
        TrySnapEdge(_montagePosition);
        foreach (MontageClipItem other in _project.AllClips)
        {
            if (ReferenceEquals(other, clip)) continue;
            TrySnapEdge(other.TimelineStart);
            TrySnapEdge(other.TimelineEnd);
        }

        return best < TimeSpan.Zero ? TimeSpan.Zero : best;
    }

    private bool FinishMoveClipAnimation(MontageClipItem draggedClip)
    {
        List<MoveClipDragSnapshot>? snapshots = _moveDragSnapshots;
        TimeSpan resolvedTime = _moveResolvedTargetTime;
        int targetLayer = _moveTargetLayer;
        double pxPerSec = Math.Max(1.0, _movePxPerSec);

        _moveDragSnapshots = null;
        ClearAllDropReveals(animate: false);

        MoveSlotPreviewRect.BeginAnimation(OpacityProperty, null);
        MoveSlotPreviewRect.BeginAnimation(Canvas.TopProperty, null);
        MoveSlotPreviewRect.BeginAnimation(HeightProperty, null);
        MoveSlotPreviewRect.Visibility = Visibility.Collapsed;
        DropInsertionLine.Visibility = Visibility.Collapsed;

        if (snapshots == null || snapshots.Count == 0)
        {
            SyncAllDynamicTrackRows(animate: true);
            return false;
        }

        MoveClipDragSnapshot dragged = snapshots[0];
        bool switchedTrack = targetLayer != draggedClip.TrackLayer;
        bool moved = switchedTrack
            ? (draggedClip.IsAudioClip
                ? _project.MoveAudioClipTrack(draggedClip, targetLayer, resolvedTime, recordUndo: true)
                : _project.MoveVideoClipTrack(draggedClip, targetLayer, resolvedTime, recordUndo: true))
            : _project.MoveClipToTime(draggedClip, resolvedTime, recordUndo: true);

        if (switchedTrack)
        {
            _pendingEntranceClipIds.Add(draggedClip.Id);
            if (draggedClip.IsVideoClip && _project.FindLinkedAudioClip(draggedClip) is { } pairedAudio)
                _pendingEntranceClipIds.Add(pairedAudio.Id);
            RenderTimeline();
            return moved;
        }

        SyncAllDynamicTrackRows(animate: true);
        double targetLeft = draggedClip.TimelineStart.TotalSeconds * pxPerSec;

        int settleGen = ++_moveSettleGeneration;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(140);

        Border? activeBlock = dragged.VideoBlock ?? dragged.AudioBlock;
        AnimateSettleClip(activeBlock, targetLeft, dur, ease, onCompleted: () =>
        {
            if (settleGen == _moveSettleGeneration && _dragMode == TimelineDragMode.None)
                RenderTimeline();
        });

        return moved;
    }

    private static void AnimateClipEntrance(Border block, double slideFromY, string accentHex)
    {
        if (block == null) return;
        Panel.SetZIndex(block, 90);
        block.RenderTransformOrigin = new Point(0.5, 0.5);

        var scale = new ScaleTransform(0.95, 0.95);
        var translate = new TranslateTransform(0, slideFromY);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        block.RenderTransform = group;

        Brush normalBorder = block.BorderBrush;
        Thickness normalThickness = block.BorderThickness;
        block.BorderBrush = BrushFrom(accentHex);
        block.BorderThickness = new Thickness(1.8);
        block.Opacity = 0.0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(220);

        var opacityAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
        opacityAnim.Completed += (_, _) =>
        {
            block.BorderBrush = normalBorder;
            block.BorderThickness = normalThickness;
            Panel.SetZIndex(block, 0);
        };
        block.BeginAnimation(OpacityProperty, opacityAnim);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1.0, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1.0, dur) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slideFromY, 0.0, dur) { EasingFunction = ease });
    }

    private static void ApplyLiftAnimation(Border? block, double liftY)
    {
        if (block == null) return;
        Panel.SetZIndex(block, 100);
        block.RenderTransformOrigin = new Point(0.5, 0.5);

        var scale = new ScaleTransform(1.0, 1.0);
        var translate = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        block.RenderTransform = group;

        block.BorderBrush = BrushFrom("#4EA1FF");
        block.BorderThickness = new Thickness(1.8);
        block.Effect = new DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 3,
            Opacity = 0.65,
            Color = Colors.Black
        };

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(115);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.03, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.03, dur) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, liftY, dur) { EasingFunction = ease });
        block.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.94, dur) { EasingFunction = ease });
    }

    private static void AnimateSettleClip(
        Border? block, double targetLeft, TimeSpan dur, IEasingFunction ease, Action? onCompleted)
    {
        if (block == null)
        {
            onCompleted?.Invoke();
            return;
        }

        double fromLeft = Canvas.GetLeft(block);
        if (double.IsNaN(fromLeft)) fromLeft = targetLeft;
        var leftAnim = new DoubleAnimation(fromLeft, targetLeft, dur) { EasingFunction = ease };
        if (onCompleted != null)
            leftAnim.Completed += (_, _) => onCompleted();
        block.BeginAnimation(Canvas.LeftProperty, leftAnim);
        block.BeginAnimation(OpacityProperty, new DoubleAnimation(block.Opacity, 1.0, dur) { EasingFunction = ease });

        if (block.RenderTransform is TransformGroup group && group.Children.Count >= 2)
        {
            if (group.Children[0] is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 1.0, dur) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 1.0, dur) { EasingFunction = ease });
            }
            if (group.Children[1] is TranslateTransform translate)
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(translate.Y, 0, dur) { EasingFunction = ease });
        }
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

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T typed) return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
