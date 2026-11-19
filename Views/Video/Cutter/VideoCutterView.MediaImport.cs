using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Video;
using CortexFX.Dialogs;
using Microsoft.Win32;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
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
            _ = LoadOrAppendFilesAsync(files, append: true, addToTimeline: true);
        e.Handled = true;
    }

    private void ProjectPanel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ProjectPanel_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _ = LoadOrAppendFilesAsync(files, append: true, addToTimeline: true);
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

    private void ImportMediaToBin_Click(object sender, RoutedEventArgs e) =>
        OpenMediaPicker(addToTimelineAlso: true);

    public void OpenFilePicker() =>
        OpenMediaPicker(addToTimelineAlso: true);

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
        TimeSpan? insertAtTime = null,
        int? targetVideoLayer = null,
        int? targetAudioLayer = null) =>
        ImportFilesCoreAsync(paths, append, addToTimeline, insertAtTime, targetVideoLayer, targetAudioLayer);

    private async Task ImportFilesCoreAsync(
        string[] paths,
        bool append,
        bool addToTimeline,
        TimeSpan? insertAtTime,
        int? explicitVideoLayer,
        int? explicitAudioLayer)
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
        int addedToOverlayLayer = -1;
        TimeSpan? explicitCursor = insertAtTime;

        foreach (string path in validPaths)
        {
            VideoMediaInfo info;
            try
            {
                info = service != null ? await service.ProbeVideoAsync(path) : FallbackProbe(path);
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
                bool isAudio = asset.IsAudioOnly;
                int destVideoLayer = 0;
                int? destAudioLayer = explicitAudioLayer;

                if (!isAudio)
                {
                    destVideoLayer = explicitVideoLayer.HasValue
                        ? Math.Clamp(explicitVideoLayer.Value, 0, MontageProject.MaxVideoLayers - 1)
                        : 0; // Append videos sequentially on V1 for clean cutting & merging (drag to V2/V3 for overlays)
                }
                else
                {
                    destAudioLayer ??= 3; // A4 music/audio overlay track
                }

                TimeSpan desiredTime;
                if (explicitCursor.HasValue)
                {
                    desiredTime = explicitCursor.Value;
                }
                else if (isAudio)
                {
                    var aTrack = _project.GetAudioTrack(destAudioLayer ?? 3);
                    desiredTime = aTrack.Count > 0 ? aTrack[^1].TimelineEnd : TimeSpan.Zero;
                }
                else
                {
                    var vTrack = _project.GetVideoTrack(destVideoLayer);
                    desiredTime = vTrack.Count > 0 ? vTrack[^1].TimelineEnd : TimeSpan.Zero;
                }

                MontageClipItem clip = _project.AddClipAtTime(
                    asset,
                    desiredTime,
                    select: firstAdded == null,
                    recordUndo: false,
                    targetVideoLayer: destVideoLayer,
                    targetAudioLayer: isAudio ? destAudioLayer : null);

                _pendingEntranceClipIds.Add(clip.Id);
                if (_project.FindLinkedAudioClip(clip) is { } pairedAudio)
                    _pendingEntranceClipIds.Add(pairedAudio.Id);

                if (!isAudio && destVideoLayer > 0)
                    addedToOverlayLayer = Math.Max(addedToOverlayLayer, destVideoLayer);

                firstAdded ??= clip;
                if (explicitCursor.HasValue)
                    explicitCursor = clip.TimelineEnd;
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
            if (addedToOverlayLayer > 0)
            {
                string layerName = $"V{addedToOverlayLayer + 1}";
                string audioName = $"A{addedToOverlayLayer + 1}";
                ShowTimelineToast(firstAdded.HasAudio
                    ? $"Added video to {layerName} · Audio separated to {audioName}"
                    : $"Added video to {layerName} Overlay");
            }
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

    private async void MediaBinCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _binDragAsset = null;
        if ((sender as FrameworkElement)?.DataContext is not SourceMediaAsset asset) return;
        SelectAssetInBin(asset);

        if (e.ClickCount == 2)
        {
            if (asset.IsAudioOnly)
            {
                var a4 = _project.GetAudioTrack(3);
                TimeSpan desiredAudio = a4.Count > 0 ? a4[^1].TimelineEnd : _montagePosition;
                await InsertAssetAtTimelinePositionAsync(asset, desiredAudio, targetAudioLayer: 3);
            }
            else
            {
                TimeSpan desiredVideo = _project.Clips.Count > 0 ? _project.Clips[^1].TimelineEnd : TimeSpan.Zero;
                await InsertAssetAtTimelinePositionAsync(asset, desiredVideo, targetVideoLayer: 0);
            }
        }
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

        if (asset.IsAudioOnly)
        {
            menu.Items.Add(MakeItem("Insert on A4 at Playhead", () => _ = InsertAssetAtTimelinePositionAsync(asset, _montagePosition, targetAudioLayer: 3)));
            menu.Items.Add(MakeItem("Append to A4 End", () => _ = InsertAssetAtTimelinePositionAsync(asset, _project.TotalDuration, targetAudioLayer: 3)));
        }
        else
        {
            menu.Items.Add(MakeItem("Insert on V1 at Playhead", () => _ = InsertAssetAtTimelinePositionAsync(asset, _montagePosition, targetVideoLayer: 0)));
            menu.Items.Add(MakeItem("Stack on V2 Overlay at Playhead", () => _ = InsertAssetAtTimelinePositionAsync(asset, _montagePosition, targetVideoLayer: 1)));
            menu.Items.Add(MakeItem("Stack on V3 Top Layer at Playhead", () => _ = InsertAssetAtTimelinePositionAsync(asset, _montagePosition, targetVideoLayer: 2)));
        }
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
        int usedCount = _project.AllClips.Count(c =>
            string.Equals(c.SourcePath, asset.FilePath, StringComparison.OrdinalIgnoreCase));

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
        int targetVideoLayer = 0,
        int? targetAudioLayer = null)
    {
        await PausePlaybackAsync();
        bool isAudio = asset.IsAudioOnly || targetAudioLayer.HasValue;
        MontageClipItem added = _project.AddClipAtTime(
            asset,
            desiredTime,
            select: true,
            recordUndo: true,
            targetVideoLayer: targetVideoLayer,
            targetAudioLayer: isAudio ? (targetAudioLayer ?? 3) : null);

        _pendingEntranceClipIds.Add(added.Id);
        if (_project.FindLinkedAudioClip(added) is { } pairedAudio)
            _pendingEntranceClipIds.Add(pairedAudio.Id);

        _montagePosition = added.TimelineStart;
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdateExportSummaryHint();
        UpdateAspectOverlay();
        ApplyActiveFrameTransforms();
        RenderTimeline();
        EnsurePlayheadVisible();
        ShowTimelineToast($"Placed {added.TrackLabel} at {MontageProject.FormatShort(added.TimelineStart)}");
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
        if (!isAudioOnly)
        {
            UpdateVideoDropTrackRevealFromY(pos.Y);
        }
        else
        {
            int hoveredAudio = ResolveAudioDropLayerFromY(pos.Y);
            if (hoveredAudio == 1) SetTrackDropReveal(TrackRevealSlot.A2, true);
            else if (hoveredAudio == 2) SetTrackDropReveal(TrackRevealSlot.A3, true);
        }

        double previewW = isAsset && e.Data.GetData("CortexFxMediaAsset") is SourceMediaAsset asset
            ? Math.Clamp(asset.Info.Duration.TotalSeconds * pxPerSec, 18, Math.Max(48, totalW * 0.45))
            : 84;

        Canvas.SetLeft(DropInsertionLine, Math.Clamp(snapX, 0, Math.Max(0, totalW - 2.5)));
        DropInsertionLine.Visibility = Visibility.Visible;

        double targetTop;
        double targetHeight;
        string dropTrackLabel;
        if (isAudioOnly)
        {
            int aLayer = ResolveAudioDropLayerFromY(pos.Y);
            targetTop = GetAudioTrackTopY(aLayer) + 3.0;
            targetHeight = GetAudioTrackRowHeightForLayer(aLayer) - 6.0;
            dropTrackLabel = $"A{aLayer + 1}";
        }
        else
        {
            int vLayer = ResolveVideoDropLayerFromY(pos.Y);
            targetTop = GetVideoTrackTopY(vLayer) + 3.0;
            targetHeight = vLayer > 0 ? TimelineOverlayVideoRowHeight - 6.0 : TimelineV1RowHeight - 6.0;
            dropTrackLabel = $"V{vLayer + 1}";
        }

        MoveSlotPreviewRect.BeginAnimation(OpacityProperty, null);
        MoveSlotPreviewRect.BeginAnimation(Canvas.LeftProperty, null);
        MoveSlotPreviewRect.BeginAnimation(Canvas.TopProperty, null);
        MoveSlotPreviewRect.BeginAnimation(HeightProperty, null);
        MoveSlotPreviewRect.Width = previewW;
        Canvas.SetLeft(MoveSlotPreviewRect, snapX);
        Canvas.SetTop(MoveSlotPreviewRect, targetTop);
        MoveSlotPreviewRect.Height = Math.Max(28, targetHeight);
        MoveSlotPreviewRect.Opacity = 1;
        MoveSlotPreviewRect.Visibility = Visibility.Visible;

        ShowDragBadge($"+ DROP {dropTrackLabel} @ {MontageProject.FormatShort(snapped)}", snapX, totalW);
        e.Handled = true;
    }

    private void Timeline_DragLeave(object sender, DragEventArgs e)
    {
        if (_dragMode == TimelineDragMode.None)
        {
            DropInsertionLine.Visibility = Visibility.Collapsed;
            MoveSlotPreviewRect.Visibility = Visibility.Collapsed;
            TimelineDragBadge.Visibility = Visibility.Collapsed;
            ClearAllDropReveals(animate: true);
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

        bool isAssetDrop = e.Data.GetData("CortexFxMediaAsset") is SourceMediaAsset;
        int resolvedVideoLayer = ResolveVideoDropLayerFromY(pos.Y);
        int resolvedAudioLayer = ResolveAudioDropLayerFromY(pos.Y);
        ClearAllDropReveals(animate: false);

        if (isAssetDrop && e.Data.GetData("CortexFxMediaAsset") is SourceMediaAsset asset)
        {
            if (asset.IsAudioOnly)
                _ = InsertAssetAtTimelinePositionAsync(asset, snapped, targetAudioLayer: resolvedAudioLayer);
            else
                _ = InsertAssetAtTimelinePositionAsync(asset, snapped, targetVideoLayer: resolvedVideoLayer);
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _ = LoadOrAppendFilesAsync(
                files,
                append: true,
                addToTimeline: true,
                insertAtTime: snapped,
                targetVideoLayer: resolvedVideoLayer,
                targetAudioLayer: pos.Y >= A1TopY ? resolvedAudioLayer : null);
            e.Handled = true;
        }
        else
        {
            SyncAllDynamicTrackRows(animate: true);
        }
    }
}
