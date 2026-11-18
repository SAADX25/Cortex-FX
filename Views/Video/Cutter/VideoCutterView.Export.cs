using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Media;
using CortexFX.Core.Video;
using CortexFX.Dialogs;
using Microsoft.Win32;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    private enum ExportStage { Options, Progress, Result }

    // Undo / Redo & Clip Editing Commands

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
        _playingClip = _project.SelectedClip is { IsVideoClip: true, TrackLayer: 0 } sel
            ? sel
            : _project.Clips.FirstOrDefault();
        _playingClipIndex = _playingClip != null ? _project.Clips.IndexOf(_playingClip) : -1;
        _montagePosition = HasAnyTimelineClips() ? ClampMontageTime(_montagePosition) : TimeSpan.Zero;
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

    private async void SplitAtPlayhead_Click(object sender, RoutedEventArgs e) =>
        await SplitAtPlayheadAsync();

    private async Task SplitAtPlayheadAsync(TimelineTrackKind? explicitKind = null, int? explicitLayer = null)
    {
        if (!HasAnyTimelineClips()) return;
        await PausePlaybackAsync();

        MontageClipItem? sel = _project.SelectedClip;
        TimelineTrackKind kind = explicitKind ?? sel?.TrackKind ?? TimelineTrackKind.Video;
        int layer = explicitLayer ?? sel?.TrackLayer ?? 0;

        TimeSpan snapped = MontageProject.SnapToFrame(_montagePosition, _project.SequenceFps);
        var (ok, second) = _project.SplitAtMontageTime(snapped, kind, layer);
        if (!ok || second == null)
            (ok, second) = _project.SplitAtMontageTime(_montagePosition, kind, layer);

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
        ShowTimelineToast($"Split [{second.TrackLabel}] → Clip #{second.OrderIndex - 1} & #{second.OrderIndex}");
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
        ShowTimelineToast($"Restored {clip.TrackLabel} Clip #{clip.OrderIndex} full range");
    }

    private void DuplicateSelectedOrAddSlice_Click(object sender, RoutedEventArgs e)
    {
        MontageClipItem? target = _project.SelectedClip ?? _playingClip ?? _project.AllClips.FirstOrDefault();
        if (target != null)
            DuplicateClipItem(target);
    }

    private void DuplicateClipItem(MontageClipItem target)
    {
        MontageClipItem? copy = _project.DuplicateClip(target);
        if (copy == null) return;

        _pendingEntranceClipIds.Add(copy.Id);
        if (copy.IsVideoClip && _project.FindLinkedAudioClip(copy) is { } pairedAudio)
            _pendingEntranceClipIds.Add(pairedAudio.Id);

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        RenderTimeline();
        ShowTimelineToast($"Duplicated → {copy.TrackLabel} Clip #{copy.OrderIndex}");
    }

    private async void DeleteSelectedClip_Click(object sender, RoutedEventArgs e)
    {
        bool ripple = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        MontageClipItem? target = _project.SelectedClip ?? _playingClip;
        if (target != null)
            await RemoveClipWithUiRefreshAsync(target, ripple);
    }

    private async Task RemoveClipWithUiRefreshAsync(MontageClipItem clip, bool ripple = false, bool removeLinkedAudio = true)
    {
        await PausePlaybackAsync();
        TimeSpan preservePos = _montagePosition;
        _project.RemoveClip(clip, recordUndo: true, ripple: ripple, removeLinkedAudio: removeLinkedAudio);

        if (_project.TotalDuration <= TimeSpan.Zero || !HasAnyTimelineClips())
        {
            _activePlayerSourcePath = null;
            _playingClip = null;
            _playingClipIndex = -1;
            _mediaReady = false;
            foreach (OverlayChannelState ch in _videoOverlayChannels.Concat(_audioChannels))
                ch.Reset();

            if (V2TransformHost != null) V2TransformHost.Visibility = Visibility.Collapsed;
            if (V3TransformHost != null) V3TransformHost.Visibility = Visibility.Collapsed;
            try { await PreviewMedia.Close(); } catch { }
            for (int layer = 1; layer <= 2; layer++)
                try { if (GetVideoOverlayMediaElement(layer) is { } m) await m.Close(); } catch { }
            for (int a = 0; a < MontageProject.MaxAudioTracks; a++)
                try { if (GetAudioMediaElement(a) is { } m) await m.Close(); } catch { }

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
        SyncAudioGainStripUi();
        RenderTimeline();
        ShowTimelineToast(ripple ? "Ripple deleted clip" : "Deleted clip");
    }

    // HUD, Labels & Aspect Overlay

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

        MontageClipItem? v2Active = _videoOverlayChannels[0].PlayingClip ?? _project.GetActiveVideoClipOnLayer(1, _montagePosition).Clip;
        MontageClipItem? v3Active = _videoOverlayChannels[1].PlayingClip ?? _project.GetActiveVideoClipOnLayer(2, _montagePosition).Clip;
        MontageClipItem? v1Active = _playingClip ?? _project.MapMontageToSource(_montagePosition).Clip;

        if (v1Active == null && v2Active == null && v3Active == null && _project.TotalDuration > TimeSpan.Zero)
        {
            MonitorMediaHudText.Text = "GAP · Black Frame";
        }
        else
        {
            MontageClipItem? active = _project.SelectedClip ?? v3Active ?? v2Active ?? v1Active ?? _project.AllClips.FirstOrDefault();
            if (active != null)
            {
                string trackTag = active.IsAudioClip
                    ? $"Audio {active.TrackLabel}"
                    : (active.TrackLayer > 0 ? $"{active.TrackLabel} · {active.DisplayResolution}" : active.DisplayResolution);
                string fps = active.SourceFps > 0 ? $"{active.SourceFps:0.##}fps" : "";
                string codec = _project.Assets.TryGetValue(active.SourcePath, out SourceMediaAsset? asset) &&
                               !string.IsNullOrEmpty(asset.Info.VideoCodec)
                    ? asset.Info.VideoCodec.ToUpperInvariant()
                    : "";
                string audio = active.IsVideoClip
                    ? (_project.FindLinkedAudioClip(active) != null ? "Audio" : (active.HasAudio ? "Audio Off" : "Silent"))
                    : "Audio";
                string scaleTag = active.IsVideoClip && active.HasCustomTransform ? active.ScalePercentLabel : "";
                MonitorMediaHudText.Text = string.Join(" · ",
                    new[] { $"Clip #{active.OrderIndex}", trackTag, active.AspectRatioLabel, fps, codec, audio, scaleTag }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
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
        VideoCutMode mode = ModeFastCopy?.IsChecked == true ? VideoCutMode.FastLossless : VideoCutMode.AccurateEncode;
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

    private (List<VideoMontageClip> V1, List<VideoMontageClip> VideoOverlays, List<VideoMontageClip> AudioOverlays, bool IsSingleLosslessEligible) BuildExportTrackLists()
    {
        // Filter out muted V2/V3 layers if muted by eye/M toggle
        var v2Active = _isVideoTrackMuted[1] ? Enumerable.Empty<MontageClipItem>() : _project.VideoOverlayClips;
        var v3Active = _isVideoTrackMuted[2] ? Enumerable.Empty<MontageClipItem>() : _project.VideoOverlay3Clips;
        var videoOverlayItems = v2Active.Concat(v3Active).ToList();

        var activeAudioList = new List<MontageClipItem>();
        for (int aLayer = 0; aLayer < MontageProject.MaxAudioTracks; aLayer++)
        {
            if (_isAudioTrackMuted[aLayer]) continue;
            foreach (MontageClipItem aClip in _project.GetAudioTrack(aLayer))
            {
                if (aClip.HasAudio && aClip.AudioVolume > 0.005)
                    activeAudioList.Add(aClip);
            }
        }

        // Fast-copy eligibility check: single V1 clip aligned 1:1 with single untouched A1 clip (no V2/V3 or extra audio)
        bool simpleSingleV1AudioMatch =
            _project.Clips.Count == 1 &&
            videoOverlayItems.Count == 0 &&
            _project.Audio2Clips.Count == 0 &&
            _project.Audio3Clips.Count == 0 &&
            _project.AudioOverlayClips.Count == 0 &&
            _project.AudioClips.Count == 1 &&
            !_isAudioTrackMuted[0] &&
            MontageProject.AreAudioVideoAligned(_project.Clips[0], _project.AudioClips[0]) &&
            Math.Abs((_project.Clips[0].End - _project.AudioClips[0].End).TotalMilliseconds) < 35 &&
            Math.Abs(_project.AudioClips[0].AudioVolume - 1.0) <= 0.01;

        bool simpleSingleV1Muted =
            _project.Clips.Count == 1 &&
            videoOverlayItems.Count == 0 &&
            activeAudioList.Count == 0;

        if (simpleSingleV1AudioMatch)
        {
            MontageClipItem c = _project.Clips[0];
            MontageClipItem a = _project.AudioClips[0];
            return (
                new List<VideoMontageClip> { c.ToExportClip(overrideHasAudio: a.HasAudio, overrideVolume: a.AudioVolume) },
                new List<VideoMontageClip>(),
                new List<VideoMontageClip>(),
                true);
        }

        if (simpleSingleV1Muted)
        {
            return (
                new List<VideoMontageClip> { _project.Clips[0].ToExportClip(overrideHasAudio: false, overrideVolume: 0.0) },
                new List<VideoMontageClip>(),
                new List<VideoMontageClip>(),
                true);
        }

        // Multi-layer / separated audio tracks: video streams export silent, all A1..A4 clips mix accurately
        var v1Export = _project.Clips.Select(c => c.ToExportClip(overrideHasAudio: false, overrideVolume: 0.0)).ToList();
        var vOvExport = videoOverlayItems.Select(c => c.ToExportClip(overrideHasAudio: false, overrideVolume: 0.0)).ToList();
        var aOvExport = activeAudioList.Select(a => a.ToExportClip(overrideHasAudio: true, overrideVolume: a.AudioVolume)).ToList();

        return (v1Export, vOvExport, aOvExport, false);
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

        bool hasCustomTransform = _project.AllVideoClips.Any(c => c.HasCustomTransform);
        bool hasMultiTrackOrGaps =
            _project.VideoOverlayClips.Count > 0 ||
            _project.VideoOverlay3Clips.Count > 0 ||
            _project.Audio2Clips.Count > 0 ||
            _project.Audio3Clips.Count > 0 ||
            _project.AudioOverlayClips.Count > 0 ||
            _project.GetGaps(0).Count > 0 ||
            hasCustomTransform;

        if (mode == VideoCutMode.FastLossless)
        {
            ExportSummaryHint.Text = multiSource || hasMultiTrackOrGaps || res != VideoResolutionPreset.Original
                ? $"{fmt.ToUpperInvariant()} · {resLabel} ({fitLabel}) · Auto smart-render for layers/transforms/audio"
                : $"{fmt.ToUpperInvariant()} · Original · Instant stream copy (keyframe snapped)";
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

        MontageClipItem? active = _project.SelectedClip is { IsVideoClip: true } sel ? sel : (_playingClip ?? _project.Clips.FirstOrDefault());
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

    // Export Execution & Modal

    private void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select output folder for exported video" };
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
        var (exportClips, videoOverlayClips, overlayClips, losslessEligible) = BuildExportTrackLists();

        string firstSource = exportClips.FirstOrDefault()?.SourcePath
            ?? videoOverlayClips.FirstOrDefault()?.SourcePath
            ?? overlayClips.FirstOrDefault()?.SourcePath
            ?? _project.AllClips.First().SourcePath;
        string outDir = _outputFolderOverride ?? Path.GetDirectoryName(firstSource)!;
        string baseName = Path.GetFileNameWithoutExtension(firstSource);
        int totalClipCount = Math.Max(1, exportClips.Count + videoOverlayClips.Count + overlayClips.Count);
        string suffix = totalClipCount > 1 ? "montage" : "cut";
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
        bool simpleLossless = mode == VideoCutMode.FastLossless && losslessEligible && !_project.Clips.Any(c => c.HasCustomTransform);
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
                if (_isTransformMode) { SetTransformMode(false); e.Handled = true; }
                else if (_activeTool != TimelineToolMode.Select) { SetActiveTool(TimelineToolMode.Select); e.Handled = true; }
                else if (_previewFocused) { _previewFocused = false; ApplyPreviewFocusState(); e.Handled = true; }
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
}
