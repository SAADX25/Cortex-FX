using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Video;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    private MontageClipItem? _activeAudioTargetClip;

    private MontageClipItem? GetEffectiveAudioTargetClip()
    {
        if (_activeAudioTargetClip != null && _project.AllClips.Contains(_activeAudioTargetClip))
        {
            if (_activeAudioTargetClip.IsAudioClip)
                return _activeAudioTargetClip;
            if (_activeAudioTargetClip.IsVideoClip)
                return _project.FindLinkedAudioClip(_activeAudioTargetClip) ?? _activeAudioTargetClip;
        }

        if (_project.SelectedClip is { } sel)
        {
            if (sel.IsAudioClip) return sel;
            if (sel.IsVideoClip) return _project.FindLinkedAudioClip(sel) ?? sel;
        }

        return _project.GetActiveAudioClipOnTrack(0, _montagePosition).Clip
            ?? _project.AllAudioClips.FirstOrDefault()
            ?? _project.AllVideoClips.FirstOrDefault();
    }

    private void HandleTimelineClipLeftClick(MontageClipItem clickedClip)
    {
        if (clickedClip.IsVideoClip)
        {
            _project.SelectClip(clickedClip);
            ApplyActiveFrameTransforms();
            MontageClipItem? pairedAudio = _project.FindAlignedAudioClip(clickedClip);
            _activeAudioTargetClip = pairedAudio ?? clickedClip;

            if (clickedClip.HasAudio || pairedAudio != null)
            {
                if (AudioGainStrip != null && AudioGainStrip.Visibility != Visibility.Visible)
                    AudioGainStrip.Visibility = Visibility.Visible;
                SyncAudioGainStripUi();
                ShowTimelineToast(pairedAudio != null
                    ? $"Audio ready · {pairedAudio.TrackLabel} ({pairedAudio.AudioVolumeDbLabel})"
                    : $"{clickedClip.TrackLabel} audio removed — click + Restore Audio");
            }
            else if (AudioGainStrip?.Visibility == Visibility.Visible)
            {
                SyncAudioGainStripUi();
            }
            return;
        }

        _activeAudioTargetClip = clickedClip;
        OpenAudioControlForClip(clickedClip);
    }

    private void OpenAudioControlForClip(MontageClipItem clip)
    {
        MontageClipItem target = clip.IsVideoClip
            ? (_project.FindLinkedAudioClip(clip) ?? clip)
            : clip;

        _activeAudioTargetClip = target;
        _project.SelectClip(target);
        if (AudioGainStrip != null)
            AudioGainStrip.Visibility = Visibility.Visible;
        SyncAudioGainStripUi();
    }

    private void AudioTrackBadge_Click(object sender, MouseButtonEventArgs e)
    {
        int trackIdx = ParseElementLayerTag(sender, 0);
        if (trackIdx == 0)
        {
            SetAudioTrackExpanded(!_isAudioExpanded);
        }
        else
        {
            MontageClipItem? clip = _project.GetAudioTrack(trackIdx).FirstOrDefault();
            if (clip != null)
            {
                OpenAudioControlForClip(clip);
                RenderTimeline();
            }
            else if (AudioGainStrip != null)
            {
                AudioGainStrip.Visibility = AudioGainStrip.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                SyncAudioGainStripUi();
            }
        }
        e.Handled = true;
    }

    private void AudioStripToggle_Click(object sender, RoutedEventArgs e) =>
        SetAudioTrackExpanded(AudioStripToggle?.IsChecked == true);

    private void AudioStripClose_Click(object sender, RoutedEventArgs e)
    {
        if (_isAudioExpanded)
            SetAudioTrackExpanded(false);
        else if (AudioGainStrip != null)
            AudioGainStrip.Visibility = Visibility.Collapsed;
    }

    private void AudioRestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? videoTarget = _project.SelectedClip is { IsVideoClip: true } selV
            ? selV
            : _project.AllVideoClips.FirstOrDefault(c => ReferenceEquals(c, _activeAudioTargetClip));
        if (videoTarget == null) return;

        MontageClipItem? restored = _project.EnsureSeparatedAudioForVideoClip(videoTarget, recordUndo: true);
        if (restored != null)
        {
            _activeAudioTargetClip = restored;
            _pendingEntranceClipIds.Add(restored.Id);
            SyncAudioGainStripUi();
            ApplyPlayerAudioAndSpeed();
            _ = SyncAudioChannelAsync(restored.TrackLayer, _montagePosition, autoPlay: _isPlaying, forceSeek: true);
            UpdateProjectHeaderAndHud();
            UpdateExportSummaryHint();
            RenderTimeline();
            ShowTimelineToast($"Restored audio to {restored.TrackLabel}");
        }
    }

    private void AudioUnlinkBtn_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? target = GetEffectiveAudioTargetClip();
        if (target == null) return;
        if (_project.UnlinkClip(target, recordUndo: true))
        {
            SyncAudioGainStripUi();
            ApplyPlayerAudioAndSpeed();
            SyncAllAuxiliaryChannels(_montagePosition, autoPlay: _isPlaying, forceSeek: true);
            RenderTimeline();
            ShowTimelineToast($"Unlinked {target.TrackLabel} audio for independent editing");
        }
    }

    private void ResetClipAudioVolume(MontageClipItem clip)
    {
        MontageClipItem target = clip.IsVideoClip ? (_project.FindLinkedAudioClip(clip) ?? clip) : clip;
        if (_project.SetClipAudioVolume(target, 1.0, recordUndo: true))
        {
            RefreshClipAudioBlockVisual(target, highlightActive: false);
            SyncAudioGainStripUi();
            ApplyPlayerAudioAndSpeed();
            ShowTimelineToast($"Audio reset 0.0 dB · {target.TrackLabel}");
        }
    }

    private void SyncAudioGainStripUi()
    {
        if (AudioGainStrip == null) return;
        MontageClipItem? clip = GetEffectiveAudioTargetClip();

        if (clip == null)
        {
            if (AudioGainTrackBadgeText != null) AudioGainTrackBadgeText.Text = "AUDIO LEVEL";
            if (AudioGainClipTitle != null) AudioGainClipTitle.Text = "No clip";
            if (AudioGainControlsHost != null) AudioGainControlsHost.Visibility = Visibility.Visible;
            if (AudioRestoreBtn != null) AudioRestoreBtn.Visibility = Visibility.Collapsed;
            if (AudioUnlinkBtn != null) AudioUnlinkBtn.Visibility = Visibility.Collapsed;
            if (AudioGainSlider != null) AudioGainSlider.IsEnabled = false;
            if (AudioClipMuteBtn != null) AudioClipMuteBtn.IsEnabled = false;
            return;
        }

        if (clip.IsVideoClip)
        {
            // Video clip whose separated audio clip was deleted or not created yet
            if (AudioGainTrackBadgeText != null) AudioGainTrackBadgeText.Text = $"{clip.TrackLabel} AUDIO";
            if (AudioGainClipTitle != null) AudioGainClipTitle.Text = $"#{clip.OrderIndex} · {clip.SourceFileName}";
            bool canRestore = clip.HasAudio;
            if (AudioRestoreBtn != null) AudioRestoreBtn.Visibility = canRestore ? Visibility.Visible : Visibility.Collapsed;
            if (AudioUnlinkBtn != null) AudioUnlinkBtn.Visibility = Visibility.Collapsed;
            if (AudioGainControlsHost != null) AudioGainControlsHost.Visibility = canRestore ? Visibility.Collapsed : Visibility.Visible;
            if (AudioGainSlider != null) AudioGainSlider.IsEnabled = false;
            if (AudioClipMuteBtn != null) AudioClipMuteBtn.IsEnabled = false;
            if (AudioGainDbText != null) AudioGainDbText.Text = clip.HasAudio ? "Removed" : "Silent";
            return;
        }

        if (AudioGainControlsHost != null) AudioGainControlsHost.Visibility = Visibility.Visible;
        if (AudioRestoreBtn != null) AudioRestoreBtn.Visibility = Visibility.Collapsed;
        if (AudioUnlinkBtn != null) AudioUnlinkBtn.Visibility = clip.LinkedClipId.HasValue ? Visibility.Visible : Visibility.Collapsed;

        if (AudioGainTrackBadgeText != null)
            AudioGainTrackBadgeText.Text = $"{clip.TrackLabel} LEVEL";
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
        MontageClipItem? clip = GetEffectiveAudioTargetClip();
        if (clip == null || clip.IsVideoClip || !clip.HasAudio) return;

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
        ShowTimelineToast(target <= 0.005 ? $"Muted {clip.TrackLabel} Clip #{clip.OrderIndex}" : $"{clip.TrackLabel} Audio {clip.AudioVolumeDbLabel}");
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
        MontageClipItem? clip = GetEffectiveAudioTargetClip();
        if (clip == null || clip.IsVideoClip || !clip.HasAudio) return;

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
        MontageClipItem? clip = GetEffectiveAudioTargetClip();
        if (clip != null && clip.IsAudioClip)
            RefreshClipAudioBlockVisual(clip, highlightActive: false);
    }

    private void AudioPresetDb_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? clip = GetEffectiveAudioTargetClip();
        if (clip == null || clip.IsVideoClip || !clip.HasAudio) return;

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
        ShowTimelineToast($"{clip.TrackLabel} Audio → {clip.AudioVolumeDbLabel}");
    }

    private void ProcessAudioVolumeDrag(MouseEventArgs e)
    {
        if (_dragVolumeHandle == null) return;
        MontageClipItem clip = _dragVolumeHandle.Clip;
        double origLineY = ComputeVolumeLineY(_dragOrigClipVolume, _dragVolumeHandle.WaveHeight);
        double deltaY = e.GetPosition(TimelineSurface).Y - _dragStartY;
        double vol = LineYToVolume(origLineY + deltaY, _dragVolumeHandle.WaveHeight);
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

    private static string GetVolumeLineColorHex(MontageClipItem clip)
    {
        if (clip.AudioVolume <= 0.005) return "#F87171";
        if (clip.IsAudioAdjusted) return "#FBBF24";
        return "#EAF6FA";
    }

    private void RefreshClipAudioBlockVisual(MontageClipItem clip, bool highlightActive = false)
    {
        RenderedTimelineClip? item = _renderedTimelineClips.FirstOrDefault(r => ReferenceEquals(r.Clip, clip));
        ClipAudioVolumeHandle? handle = item?.AudioHandle;
        if (handle == null) return;

        double lineY = ComputeVolumeLineY(clip.AudioVolume, handle.WaveHeight);
        handle.VolumeLine.Y1 = lineY;
        handle.VolumeLine.Y2 = lineY;
        handle.VolumeLine.Stroke = BrushFrom(highlightActive ? "#FFFFFF" : GetVolumeLineColorHex(clip));
        handle.VolumeLine.StrokeThickness = highlightActive ? 2.2 : 1.7;
        Canvas.SetTop(handle.VolumeHitStrip, Math.Clamp(lineY - 4, 0, Math.Max(0, handle.WaveHeight - 8)));
        handle.VolumeHitStrip.ToolTip = $"Clip Audio Level: {clip.AudioVolumeDbLabel} ({Math.Round(clip.AudioVolume * 100)}%) — Drag up/down or double-click to reset 0 dB";

        if (handle.HeaderDbText != null)
        {
            handle.HeaderDbText.Text = clip.AudioVolumeDbLabel;
            handle.HeaderDbText.Foreground = BrushFrom(clip.AudioVolume <= 0.005
                ? "#FCA5A5"
                : clip.IsAudioAdjusted ? "#FDE68A" : "#CBEBF2");
        }

        if (handle.VolumeBadge != null && handle.VolumeBadgeText != null)
        {
            handle.VolumeBadgeText.Text = clip.AudioVolumeDbLabel;
            handle.VolumeBadgeText.Foreground = BrushFrom(clip.AudioVolume <= 0.005 ? "#FCA5A5" : "#EBF7FA");
            handle.VolumeBadge.BorderBrush = BrushFrom(clip.IsAudioAdjusted ? "#D97706" : "#356C7E");
            handle.VolumeBadge.Visibility = highlightActive || _isAudioExpanded || clip.IsAudioAdjusted
                ? Visibility.Visible
                : Visibility.Collapsed;
            Canvas.SetTop(handle.VolumeBadge, Math.Max(2, lineY > handle.WaveHeight * 0.45 ? lineY - 15 : lineY + 3));
        }

        string activeWaveHex = clip.TrackLayer switch
        {
            1 => "#67E8F9",
            2 => "#7DD3FC",
            3 => "#5EEAD4",
            _ => "#7CE2F7"
        };

        if (handle.WavePath != null && handle.Peaks is { Length: > 0 } && handle.VisWidth > 0)
        {
            handle.WavePath.Fill = BrushFrom(clip.AudioVolume <= 0.005 ? "#496975" : activeWaveHex);
            DateTime now = DateTime.UtcNow;
            if (!highlightActive || (now - _lastWaveformRebuildUtc).TotalMilliseconds >= 40)
            {
                _lastWaveformRebuildUtc = now;
                handle.WavePath.Data = BuildClipWaveformGeometry(
                    handle.Peaks,
                    clip,
                    handle.VisWidth,
                    handle.WaveHeight - 2,
                    handle.VisStartNorm,
                    handle.VisEndNorm);
            }
        }
    }

    // Context Menu

    private ContextMenu BuildClipContextMenu(MontageClipItem clip)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("StudioContextMenu") };

        MenuItem MakeItem(string header, string shortcut, Action action, bool enabled = true)
        {
            var mi = new MenuItem
            {
                Header = header,
                InputGestureText = shortcut,
                Style = (Style)FindResource("StudioMenuItem"),
                IsEnabled = enabled
            };
            mi.Click += (_, _) => action();
            return mi;
        }

        menu.Items.Add(MakeItem(
            "Split at Playhead",
            "S / Ctrl+B",
            () => _ = SplitAtPlayheadAsync(clip.TrackKind, clip.TrackLayer)));
        menu.Items.Add(MakeItem(
            "Ripple Trim Start to Playhead",
            "Q",
            () =>
            {
                _project.SelectClip(clip);
                _ = RippleTrimToPlayheadAsync(trimStart: true);
            }));
        menu.Items.Add(MakeItem(
            "Ripple Trim End to Playhead",
            "W",
            () =>
            {
                _project.SelectClip(clip);
                _ = RippleTrimToPlayheadAsync(trimStart: false);
            }));
        if (_project.CanHealSplitWithNext(clip))
        {
            menu.Items.Add(MakeItem(
                "Heal / Rejoin Split",
                "",
                () => _ = HealSplitClipAsync(clip)));
        }

        if (clip.IsVideoClip)
        {
            menu.Items.Add(MakeItem(
                "Frame / Motion Controls...",
                "Shift+M",
                () =>
                {
                    _project.SelectClip(clip);
                    SetTransformMode(true);
                    RenderTimeline();
                }));

            for (int targetLayer = 0; targetLayer < MontageProject.MaxVideoLayers; targetLayer++)
            {
                if (targetLayer == clip.TrackLayer) continue;
                int layerCapture = targetLayer;
                string label = layerCapture switch
                {
                    0 => "Move to V1 Main Track",
                    1 => "Move to V2 Overlay Track",
                    _ => "Move to V3 Top Overlay Layer"
                };
                menu.Items.Add(MakeItem(label, "", () =>
                {
                    if (_project.MoveVideoClipTrack(clip, layerCapture, clip.TimelineStart, recordUndo: true))
                    {
                        _pendingEntranceClipIds.Add(clip.Id);
                        if (_project.FindLinkedAudioClip(clip) is { } pairedAudio)
                            _pendingEntranceClipIds.Add(pairedAudio.Id);
                        _ = SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
                        UpdateProjectHeaderAndHud();
                        UpdateExportSummaryHint();
                        RenderTimeline();
                        ShowTimelineToast($"Moved to V{layerCapture + 1}");
                    }
                }));
            }

            MontageClipItem? pairedAudio = _project.FindLinkedAudioClip(clip);
            menu.Items.Add(MakeItem("Open Audio Controls", "Click", () => OpenAudioControlForClip(pairedAudio ?? clip), clip.HasAudio || pairedAudio != null));
            if (pairedAudio == null && clip.HasAudio)
            {
                menu.Items.Add(MakeItem("Separate / Restore Audio Track", "", () =>
                {
                    if (_project.EnsureSeparatedAudioForVideoClip(clip) is { } restored)
                    {
                        _activeAudioTargetClip = restored;
                        _pendingEntranceClipIds.Add(restored.Id);
                        SyncAudioGainStripUi();
                        UpdateExportSummaryHint();
                        RenderTimeline();
                        ShowTimelineToast($"Audio separated to {restored.TrackLabel}");
                    }
                }));
            }
            else if (pairedAudio != null)
            {
                menu.Items.Add(MakeItem("Unlink Audio Track", "", () =>
                {
                    if (_project.UnlinkClip(clip, recordUndo: true))
                    {
                        SyncAudioGainStripUi();
                        SyncAllAuxiliaryChannels(_montagePosition, autoPlay: _isPlaying, forceSeek: true);
                        RenderTimeline();
                        ShowTimelineToast($"Unlinked {pairedAudio.TrackLabel} audio");
                    }
                }));
            }
        }
        else
        {
            menu.Items.Add(MakeItem("Audio Level Strip", "dB", () => OpenAudioControlForClip(clip), clip.HasAudio));
            menu.Items.Add(MakeItem("Reset Audio Level (0 dB)", "", () => ResetClipAudioVolume(clip), clip.HasAudio && clip.IsAudioAdjusted));
            if (clip.LinkedClipId.HasValue)
            {
                menu.Items.Add(MakeItem("Unlink from Video", "", () =>
                {
                    if (_project.UnlinkClip(clip, recordUndo: true))
                    {
                        SyncAudioGainStripUi();
                        RenderTimeline();
                        ShowTimelineToast($"Unlinked {clip.TrackLabel} audio");
                    }
                }));
            }
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("StudioMenuSeparator") });
        menu.Items.Add(MakeItem("Restore Full Clip Edges", "", () => _ = RestoreClipBoundsAsync(clip), clip.IsTrimmed));
        menu.Items.Add(MakeItem("Duplicate Clip", "Ctrl+D", () => DuplicateClipItem(clip)));
        if (_project.GetGaps(0).Count > 0)
            menu.Items.Add(MakeItem("Close All Gaps / Join Clips", "Ctrl+Shift+G", () => _ = CloseAllGapsAsync()));
        menu.Items.Add(new Separator { Style = (Style)FindResource("StudioMenuSeparator") });
        menu.Items.Add(MakeItem("Delete Clip (Keep Gap)", "Del", () => _ = RemoveClipWithUiRefreshAsync(clip, ripple: false)));
        menu.Items.Add(MakeItem("Ripple Delete Clip", "Shift+Del", () => _ = RemoveClipWithUiRefreshAsync(clip, ripple: true)));
        if (clip.IsVideoClip && _project.FindLinkedAudioClip(clip) != null)
        {
            menu.Items.Add(MakeItem("Delete Video Only (Keep Audio)", "", () =>
                _ = RemoveClipWithUiRefreshAsync(clip, ripple: false, removeLinkedAudio: false)));
        }

        return menu;
    }

    // Program Monitor Frame / Motion Controls (Scale, Position, Opacity + Interactive Box for V1/V2/V3)

    private void TransformToggle_Click(object sender, RoutedEventArgs e) =>
        SetTransformMode(TransformToggleBtn?.IsChecked == true);

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
        if (sel is { IsVideoClip: true } &&
            _montagePosition >= sel.TimelineStart && _montagePosition <= sel.TimelineEnd)
            return sel;

        for (int layer = MontageProject.MaxVideoLayers - 1; layer >= 1; layer--)
        {
            if (!_isVideoTrackMuted[layer] && _videoOverlayChannels[layer - 1].PlayingClip is { } ov)
                return ov;
        }
        if (_playingClip != null)
            return _playingClip;
        if (sel is { IsVideoClip: true })
            return sel;

        return _project.AllVideoClips.LastOrDefault();
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
            MontageClipItem? refClip = _playingClip
                ?? _project.Clips.FirstOrDefault()
                ?? _project.AllVideoClips.FirstOrDefault();
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
        ApplyHostTransform(V1TransformHost, PreviewMedia, v1, frameW, frameH);

        for (int layer = 1; layer <= 2; layer++)
        {
            MontageClipItem? ov = _videoOverlayChannels[layer - 1].PlayingClip
                ?? _project.GetActiveVideoClipOnLayer(layer, _montagePosition).Clip;
            ApplyHostTransform(GetVideoTransformHost(layer), GetVideoOverlayMediaElement(layer), ov, frameW, frameH);
        }

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
            TransformBoxLabel.Text = $"{clip.TrackLabel} · {clip.ScalePercentLabel}";

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
                ? $"{clip.TrackLabel} #{clip.OrderIndex}"
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
}
