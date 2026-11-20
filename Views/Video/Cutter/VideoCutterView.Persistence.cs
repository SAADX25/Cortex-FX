using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Core.Video;
using CortexFX.Dialogs;
using Microsoft.Win32;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    private string? _currentProjectId;
    private string? _currentProjectName;
    private string? _currentExternalProjectPath;
    private DateTime? _projectCreatedUtc;
    private DateTime? _lastSavedUtc;
    private bool _hasUnsavedChanges;
    private bool _suppressDirtyTracking;
    private bool _isPersistenceBusy;
    private DispatcherTimer? _autoSaveTimer;

    public bool HasSavedProjects => MontageProjectStorage.ListSavedProjects().Count > 0;
    public bool ShouldAutoOpenFilePicker => !HasActiveSession && !HasSavedProjects;

    private void InitializePersistence()
    {
        _project.HistoryChanged += (_, _) => OnProjectHistoryChanged();
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _autoSaveTimer.Tick += async (_, _) =>
        {
            _autoSaveTimer.Stop();
            await SaveAutoDraftAsync();
        };

        RefreshSavedProjectsList();
        UpdateSaveButtonUi();
    }

    private void OnProjectHistoryChanged()
    {
        if (_suppressDirtyTracking)
            return;
        MarkProjectDirty(scheduleAutoSave: true);
    }

    internal void MarkProjectDirty(bool scheduleAutoSave = true)
    {
        if (_suppressDirtyTracking)
            return;
        if (EditorPanel.Visibility != Visibility.Visible)
            return;
        if (_project.MediaBin.Count == 0 && !HasAnyTimelineClips())
            return;

        _hasUnsavedChanges = true;
        UpdateSaveButtonUi();
        UpdateProjectHeaderAndHud();

        if (scheduleAutoSave && _autoSaveTimer != null)
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
    }

    private void ResetPersistenceTracking(bool clearAutoSave)
    {
        _autoSaveTimer?.Stop();
        _currentProjectId = null;
        _currentProjectName = null;
        _currentExternalProjectPath = null;
        _projectCreatedUtc = null;
        _lastSavedUtc = null;
        _hasUnsavedChanges = false;
        if (clearAutoSave)
            MontageProjectStorage.DeleteAutoSave();
        UpdateSaveButtonUi();
    }

    internal void RefreshSavedProjectsList()
    {
        if (SavedProjectsSection == null || SavedProjectsItemsControl == null)
            return;

        IReadOnlyList<SavedProjectSummary> list = MontageProjectStorage.ListSavedProjects();
        SavedProjectsItemsControl.ItemsSource = list;
        if (SavedProjectsCountText != null)
            SavedProjectsCountText.Text = list.Count.ToString();
        SavedProjectsSection.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSaveButtonUi()
    {
        if (SaveProgressButton == null || SaveProgressText == null || SaveProgressIcon == null)
            return;

        bool hasMedia = _project.MediaBin.Count > 0 || HasAnyTimelineClips();
        SaveProgressButton.IsEnabled = hasMedia && !_isPersistenceBusy;
        if (SaveOptionsButton != null)
            SaveOptionsButton.IsEnabled = !_isPersistenceBusy;

        if (SaveUnsavedDot != null)
            SaveUnsavedDot.Visibility = _hasUnsavedChanges && hasMedia ? Visibility.Visible : Visibility.Collapsed;

        if (_lastSavedUtc is { } savedUtc && !_hasUnsavedChanges && hasMedia)
        {
            SaveProgressText.Text = Loc.T("Loc_VC_Saved", "Saved");
            SaveProgressText.Foreground = BrushFrom("#A7F3D0");
            SaveProgressIcon.Stroke = BrushFrom("#34D399");
            SaveProgressButton.Background = BrushFrom("#142A23");
            SaveProgressButton.BorderBrush = BrushFrom("#2A6B52");
            if (SaveOptionsButton != null)
            {
                SaveOptionsButton.Background = BrushFrom("#142A23");
                SaveOptionsButton.BorderBrush = BrushFrom("#2A6B52");
            }
            SaveProgressButton.ToolTip = Loc.F("Loc_VC_SavedTip", savedUtc.ToLocalTime().ToString("HH:mm:ss"));
        }
        else
        {
            SaveProgressText.Text = Loc.T("Loc_VC_Save", "Save");
            SaveProgressText.Foreground = BrushFrom("#E2E8F0");
            SaveProgressIcon.Stroke = BrushFrom("#4EA1FF");
            SaveProgressButton.ClearValue(BackgroundProperty);
            SaveProgressButton.ClearValue(BorderBrushProperty);
            if (SaveOptionsButton != null)
            {
                SaveOptionsButton.ClearValue(BackgroundProperty);
                SaveOptionsButton.ClearValue(BorderBrushProperty);
            }
            SaveProgressButton.ToolTip = _hasUnsavedChanges
                ? Loc.T("Loc_VC_UnsavedTip")
                : Loc.T("Loc_VC_SaveTip");
        }
    }

    private MontageProjectDocument BuildProjectDocument(bool isAutoSave, string? overrideId = null, string? overrideName = null)
    {
        var (assets, clips, selectedClipId) = _project.ExportStateRecords();

        ImageSource? coverImage = _project.AllVideoClips
            .Select(c => c.Thumbnail)
            .FirstOrDefault(t => t != null)
            ?? _project.MediaBin
                .Select(a => a.BinThumbnail)
                .FirstOrDefault(t => t != null);

        var (_, res, fit, mode) = ReadExportSettings();
        string rawFormat = (FormatCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mp4";

        return new MontageProjectDocument
        {
            Version = 1,
            ProjectId = overrideId ?? _currentProjectId ?? Guid.NewGuid().ToString("N"),
            ProjectName = MontageProjectStorage.SanitizeDisplayName(
                overrideName ?? _currentProjectName ?? MontageProjectStorage.BuildDefaultProjectName(_project.MediaBin)),
            CreatedUtc = _projectCreatedUtc ?? DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            IsAutoSave = isAutoSave,
            LinkedProjectId = isAutoSave ? _currentProjectId : null,
            CoverThumbnailBase64 = MontageProjectStorage.EncodeThumbnailBase64(coverImage),

            Assets = assets,
            Clips = clips,
            SelectedClipId = selectedClipId,

            PlayheadPositionTicks = _montagePosition.Ticks,
            VideoTrackMuted = (bool[])_isVideoTrackMuted.Clone(),
            AudioTrackMuted = (bool[])_isAudioTrackMuted.Clone(),
            IsMuted = _isMuted,
            Volume = _volume,
            SpeedRatio = _speedRatio,
            IsSnapEnabled = _isSnapEnabled,
            IsLoopEnabled = LoopToggle?.IsChecked == true,
            IsAudioExpanded = _isAudioExpanded,
            IsTransformMode = _isTransformMode,
            ZoomSliderValue = ZoomSlider?.Value ?? 0.0,
            TimelineHorizontalOffset = TimelineScrollViewer?.HorizontalOffset ?? 0.0,

            RenderMode = mode.ToString(),
            OutputFormat = rawFormat,
            ResolutionPreset = res.ToString(),
            AspectFitMode = fit.ToString(),
            OutputFolderOverride = _outputFolderOverride
        };
    }

    private async Task SaveAutoDraftAsync()
    {
        if (_isPersistenceBusy || EditorPanel.Visibility != Visibility.Visible || !_hasUnsavedChanges)
            return;
        if (_project.MediaBin.Count == 0 && !HasAnyTimelineClips())
            return;

        try
        {
            MontageProjectDocument doc = BuildProjectDocument(isAutoSave: true);
            await MontageProjectStorage.SaveDocumentAsync(MontageProjectStorage.AutoSaveFilePath, doc);
        }
        catch { }
    }

    public void FlushAutoSaveSync()
    {
        _autoSaveTimer?.Stop();
        if (EditorPanel.Visibility != Visibility.Visible || !_hasUnsavedChanges)
            return;
        if (_project.MediaBin.Count == 0 && !HasAnyTimelineClips())
            return;

        try
        {
            MontageProjectDocument doc = BuildProjectDocument(isAutoSave: true);
            MontageProjectStorage.SaveDocumentSync(MontageProjectStorage.AutoSaveFilePath, doc);
        }
        catch { }
    }

    private async void SaveProgressButton_Click(object sender, RoutedEventArgs e) =>
        await SaveProjectProgressAsync(showToast: true);

    private void SaveProgressOptions_Click(object sender, MouseButtonEventArgs e)
    {
        ShowSaveOptionsMenu(sender as UIElement ?? SaveProgressButton);
        e.Handled = true;
    }

    private void SaveOptionsButton_Click(object sender, RoutedEventArgs e) =>
        ShowSaveOptionsMenu(SaveProgressContainer ?? (sender as UIElement));

    private void ShowSaveOptionsMenu(UIElement? target)
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

        bool hasContent = _project.MediaBin.Count > 0 || HasAnyTimelineClips();
        menu.Items.Add(MakeItem("Save Progress", "Ctrl+S", () => _ = SaveProjectProgressAsync(showToast: true), hasContent));
        menu.Items.Add(MakeItem("Save As New Project...", "", () => _ = SaveProjectAsNewAsync(), hasContent));
        menu.Items.Add(MakeItem("Rename Project...", "", () => _ = RenameCurrentProjectAsync(), hasContent));
        menu.Items.Add(MakeItem("Save Project File (.cfxproj)...", "Ctrl+Shift+S", () => _ = SaveProjectToFileDialogAsync(), hasContent));

        menu.Items.Add(new Separator { Style = (Style)FindResource("StudioMenuSeparator") });
        menu.Items.Add(MakeItem("Open Project File (.cfxproj)...", "Ctrl+O", () => _ = OpenProjectFromFileDialogAsync(), !IsExporting));

        IReadOnlyList<SavedProjectSummary> saved = MontageProjectStorage.ListSavedProjects();
        if (saved.Count > 0)
        {
            var resumeMenu = new MenuItem
            {
                Header = $"Resume Saved Project ({saved.Count})",
                Style = (Style)FindResource("StudioMenuItem"),
                IsEnabled = !IsExporting
            };

            foreach (SavedProjectSummary item in saved.Take(10))
            {
                string prefix = item.IsAutoSave ? "[Draft] " : string.Empty;
                var subItem = new MenuItem
                {
                    Header = $"{prefix}{item.ProjectName}",
                    InputGestureText = $"{item.DisplayDuration} · {item.DisplayTime}",
                    Style = (Style)FindResource("StudioMenuItem")
                };
                subItem.Click += (_, _) => _ = LoadProjectFromFileAsync(item.FilePath);
                resumeMenu.Items.Add(subItem);
            }
            menu.Items.Add(resumeMenu);
        }

        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private async Task<bool> SaveProjectProgressAsync(bool showToast = true)
    {
        if (EditorPanel.Visibility != Visibility.Visible)
            return false;
        if (_project.MediaBin.Count == 0 && !HasAnyTimelineClips())
            return false;
        if (_isPersistenceBusy)
            return false;

        _isPersistenceBusy = true;
        _autoSaveTimer?.Stop();
        UpdateSaveButtonUi();

        try
        {
            _currentProjectId ??= Guid.NewGuid().ToString("N");
            _currentProjectName ??= MontageProjectStorage.BuildDefaultProjectName(_project.MediaBin);
            _projectCreatedUtc ??= DateTime.UtcNow;

            MontageProjectDocument doc = BuildProjectDocument(isAutoSave: false);
            string managedPath = MontageProjectStorage.GetManagedProjectPath(doc.ProjectId);
            await MontageProjectStorage.SaveDocumentAsync(managedPath, doc);

            if (!string.IsNullOrWhiteSpace(_currentExternalProjectPath))
            {
                await MontageProjectStorage.SaveDocumentAsync(_currentExternalProjectPath, doc);
                MontageProjectStorage.RegisterRecentExternalProject(_currentExternalProjectPath);
            }

            MontageProjectStorage.DeleteAutoSave();

            _hasUnsavedChanges = false;
            _lastSavedUtc = doc.UpdatedUtc;
            UpdateProjectHeaderAndHud();
            RefreshSavedProjectsList();

            if (showToast)
                ShowTimelineToast($"Saved progress · {doc.ProjectName}");

            return true;
        }
        catch (Exception ex)
        {
            AppDialog.ShowError(this, Loc.T("Loc_VC_SaveFailedTitle"), Loc.F("Loc_VC_SaveFailedMsg", ex.Message));
            return false;
        }
        finally
        {
            _isPersistenceBusy = false;
            UpdateSaveButtonUi();
        }
    }

    private async Task SaveProjectAsNewAsync()
    {
        if (_project.MediaBin.Count == 0 && !HasAnyTimelineClips())
            return;

        string baseName = _currentProjectName ?? MontageProjectStorage.BuildDefaultProjectName(_project.MediaBin);
        string? newName = AppDialog.PromptText(
            this,
            Loc.T("Loc_VC_SaveAsTitle"),
            Loc.T("Loc_VC_SaveAsMsg"),
            $"{baseName} (Copy)",
            confirmText: Loc.T("Loc_VC_SaveCopyBtn"));

        if (string.IsNullOrWhiteSpace(newName))
            return;

        _currentProjectId = Guid.NewGuid().ToString("N");
        _currentProjectName = MontageProjectStorage.SanitizeDisplayName(newName);
        _currentExternalProjectPath = null;
        _projectCreatedUtc = DateTime.UtcNow;

        await SaveProjectProgressAsync(showToast: true);
    }

    private async Task RenameCurrentProjectAsync()
    {
        if (_project.MediaBin.Count == 0 && !HasAnyTimelineClips())
            return;

        string currentName = _currentProjectName ?? MontageProjectStorage.BuildDefaultProjectName(_project.MediaBin);
        string? newName = AppDialog.PromptText(
            this,
            Loc.T("Loc_VC_RenameTitle"),
            Loc.T("Loc_VC_RenameMsg"),
            currentName,
            confirmText: Loc.T("Loc_VC_RenameBtn"));

        if (string.IsNullOrWhiteSpace(newName))
            return;

        _currentProjectName = MontageProjectStorage.SanitizeDisplayName(newName);
        if (_lastSavedUtc.HasValue)
        {
            if (await SaveProjectProgressAsync(showToast: false))
                ShowTimelineToast($"Renamed project → {_currentProjectName}");
        }
        else
        {
            MarkProjectDirty(scheduleAutoSave: true);
            ShowTimelineToast($"Project named → {_currentProjectName}");
        }
    }

    private async Task SaveProjectToFileDialogAsync()
    {
        if (_project.MediaBin.Count == 0 && !HasAnyTimelineClips())
            return;

        string suggestedName = MontageProjectStorage.SanitizeDisplayName(
            _currentProjectName ?? MontageProjectStorage.BuildDefaultProjectName(_project.MediaBin));
        foreach (char bad in Path.GetInvalidFileNameChars())
            suggestedName = suggestedName.Replace(bad, '_');

        string? initialDir = _outputFolderOverride;
        if (string.IsNullOrWhiteSpace(initialDir) && _project.MediaBin.FirstOrDefault() is { } firstAsset)
            initialDir = Path.GetDirectoryName(firstAsset.FilePath);

        var dlg = new SaveFileDialog
        {
            Title = "Save Video Editing Project",
            Filter = "Cortex FX Project (*.cfxproj)|*.cfxproj",
            DefaultExt = MontageProjectStorage.FileExtension,
            FileName = suggestedName + MontageProjectStorage.FileExtension,
            InitialDirectory = !string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir)
                ? initialDir
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName))
            return;

        _currentExternalProjectPath = dlg.FileName;
        if (string.IsNullOrWhiteSpace(_currentProjectName))
            _currentProjectName = MontageProjectStorage.SanitizeDisplayName(Path.GetFileNameWithoutExtension(dlg.FileName));

        if (await SaveProjectProgressAsync(showToast: false))
            ShowTimelineToast($"Saved file · {Path.GetFileName(dlg.FileName)}");
    }

    private async void OpenProjectFile_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await OpenProjectFromFileDialogAsync();
    }

    private async Task OpenProjectFromFileDialogAsync()
    {
        if (IsExporting)
        {
            AppDialog.ShowInfo(this, Loc.T("Loc_VC_ExportBusyTitle"), Loc.T("Loc_VC_ExportBusyMsg"));
            return;
        }

        string initialDir = Directory.Exists(MontageProjectStorage.ProjectsDirectory)
            ? MontageProjectStorage.ProjectsDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        var dlg = new OpenFileDialog
        {
            Title = "Open Saved Video Project",
            Filter = "Cortex FX Project (*.cfxproj)|*.cfxproj|All Files (*.*)|*.*",
            InitialDirectory = initialDir
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            await LoadProjectFromFileAsync(dlg.FileName);
    }

    private async void SavedProjectCard_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SavedProjectSummary summary)
            return;
        e.Handled = true;
        await LoadProjectFromFileAsync(summary.FilePath);
    }

    private void SavedProjectsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.ScrollableWidth > 0)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.65);
            e.Handled = true;
        }
    }

    public void OpenProjectFile(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            _ = LoadProjectFromFileAsync(filePath);
    }

    private void SavedProjectDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not SavedProjectSummary summary)
            return;

        string msg = summary.IsExternalFile
            ? $"Remove \"{summary.ProjectName}\" from the recent list?\n\nThe .cfxproj file on disk will not be deleted."
            : $"Delete saved progress \"{summary.ProjectName}\"?";

        bool confirm = AppDialog.Confirm(
            this,
            summary.IsExternalFile ? "Remove from Recent?" : "Delete Saved Project?",
            msg,
            confirmText: summary.IsExternalFile ? "Remove" : "Delete",
            cancelText: "Keep",
            tone: DialogTone.Warning);

        if (!confirm) return;

        MontageProjectStorage.DeleteSavedProject(summary);
        if (string.Equals(_currentProjectId, summary.ProjectId, StringComparison.OrdinalIgnoreCase) && !summary.IsAutoSave)
        {
            _lastSavedUtc = null;
            _hasUnsavedChanges = _project.MediaBin.Count > 0 || HasAnyTimelineClips();
            UpdateSaveButtonUi();
        }
        RefreshSavedProjectsList();
    }

    private async Task LoadProjectFromFileAsync(string filePath)
    {
        if (IsExporting)
        {
            AppDialog.ShowInfo(this, "Export in progress", "Finish or cancel the active export before loading a project.");
            return;
        }

        if (_isPersistenceBusy)
            return;

        if (!File.Exists(filePath))
        {
            AppDialog.ShowWarning(this, "Project Not Found", $"Could not find project file:\n{filePath}");
            RefreshSavedProjectsList();
            return;
        }

        if (EditorPanel.Visibility == Visibility.Visible &&
            _hasUnsavedChanges &&
            (_project.MediaBin.Count > 0 || HasAnyTimelineClips()))
        {
            bool discard = AppDialog.Confirm(
                this,
                "Unsaved editing progress",
                "You have unsaved changes in the current project. Open the selected project anyway?",
                confirmText: "Open Project",
                cancelText: "Keep Editing",
                tone: DialogTone.Warning);
            if (!discard) return;
        }

        _isPersistenceBusy = true;
        UpdateSaveButtonUi();

        try
        {
            MontageProjectDocument? doc = await MontageProjectStorage.LoadDocumentAsync(filePath);
            if (doc == null || (doc.Assets.Count == 0 && doc.Clips.Count == 0))
            {
                AppDialog.ShowWarning(this, "Empty or Invalid Project", "This project file contains no media or timeline clips.");
                return;
            }

            // Resolve missing or moved source media files BEFORE tearing down current session
            var pathRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var skippedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool modifiedDuringRelocate = false;
            IFFmpegService? ffmpegService = ResolveFfmpegService();

            foreach (SavedAssetRecord asset in doc.Assets)
            {
                if (File.Exists(asset.FilePath))
                    continue;

                string missingName = Path.GetFileName(asset.FilePath);
                bool locate = AppDialog.Confirm(
                    this,
                    "Missing Media File",
                    $"Could not find \"{missingName}\" at:\n{asset.FilePath}\n\nWould you like to locate this file?",
                    confirmText: "Locate File",
                    cancelText: "Skip Asset",
                    tone: DialogTone.Warning);

                if (locate)
                {
                    var locateDlg = new OpenFileDialog
                    {
                        Title = $"Locate {missingName}",
                        FileName = missingName,
                        Filter = "All Media (Video & Audio)|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.ogv;*.mp3;*.wav;*.aac;*.m4a;*.flac;*.ogg;*.opus;*.wma|All Files|*.*"
                    };
                    if (locateDlg.ShowDialog() == true && File.Exists(locateDlg.FileName))
                    {
                        string oldPath = asset.FilePath;
                        string newPath = locateDlg.FileName;
                        pathRemap[oldPath] = newPath;
                        asset.FilePath = newPath;
                        modifiedDuringRelocate = true;

                        if (ffmpegService != null)
                        {
                            try
                            {
                                VideoMediaInfo probed = await ffmpegService.ProbeVideoAsync(newPath);
                                if (probed.Duration > TimeSpan.Zero)
                                {
                                    asset.DurationTicks = probed.Duration.Ticks;
                                    asset.CodedWidth = probed.CodedWidth;
                                    asset.CodedHeight = probed.CodedHeight;
                                    asset.DisplayWidth = probed.DisplayWidth;
                                    asset.DisplayHeight = probed.DisplayHeight;
                                    asset.RotationDegrees = probed.RotationDegrees;
                                    asset.Fps = probed.Fps;
                                    asset.VideoCodec = probed.VideoCodec;
                                    asset.AudioCodec = probed.AudioCodec;
                                    asset.HasVideo = probed.HasVideo;
                                    asset.HasAudio = probed.HasAudio;
                                    asset.ContainerFormat = probed.ContainerFormat;
                                    asset.FileSizeBytes = probed.FileSizeBytes;
                                }
                            }
                            catch { }
                        }
                        continue;
                    }
                }

                skippedPaths.Add(asset.FilePath);
                modifiedDuringRelocate = true;
            }

            if (skippedPaths.Count > 0)
            {
                doc.Assets.RemoveAll(a => skippedPaths.Contains(a.FilePath));
                doc.Clips.RemoveAll(c => skippedPaths.Contains(c.SourcePath));
            }

            if (pathRemap.Count > 0)
            {
                foreach (SavedClipRecord clip in doc.Clips)
                {
                    if (pathRemap.TryGetValue(clip.SourcePath, out string? mapped))
                        clip.SourcePath = mapped;
                }
            }

            if (doc.Assets.Count == 0)
            {
                AppDialog.ShowWarning(this, "Cannot Open Project", "None of the media files referenced by this project were found.");
                return;
            }

            // Close active playback & reset current editor state cleanly
            await ResetViewCoreAsync(cancelExport: false, clearAutoSave: false, returnToEmptyState: false);

            _suppressDirtyTracking = true;
            try
            {
                _project.RestoreFromStateRecords(doc.Assets, doc.Clips, doc.SelectedClipId);

                RestoreTrackMuteAndPlaybackSettings(doc);
                RestoreExportSettings(doc);

                _montagePosition = ClampMontageTime(TimeSpan.FromTicks(doc.PlayheadPositionTicks));
                SyncAllDynamicTrackRows(animate: false);

                EmptyStatePanel.Visibility = Visibility.Collapsed;
                EditorPanel.Visibility = Visibility.Visible;
                ExportModalOverlay.Visibility = Visibility.Collapsed;
                WorkspaceActivated?.Invoke(this, EventArgs.Empty);

                _previewFocused = false;
                ApplyPreviewFocusState();

                SetAudioTrackExpanded(doc.IsAudioExpanded);
                SetTransformMode(doc.IsTransformMode);

                _zoomFactor = SliderToZoomFactor(doc.ZoomSliderValue, GetTimelineViewportWidth());
                SetZoomSliderSilently(doc.ZoomSliderValue);

                if (doc.IsAutoSave)
                {
                    _currentProjectId = string.IsNullOrWhiteSpace(doc.LinkedProjectId)
                        ? Guid.NewGuid().ToString("N")
                        : doc.LinkedProjectId;
                    _currentProjectName = MontageProjectStorage.SanitizeDisplayName(doc.ProjectName);
                    _currentExternalProjectPath = null;
                    _projectCreatedUtc = doc.CreatedUtc;
                    _lastSavedUtc = null;
                    _hasUnsavedChanges = true;
                }
                else
                {
                    _currentProjectId = string.IsNullOrWhiteSpace(doc.ProjectId)
                        ? Guid.NewGuid().ToString("N")
                        : doc.ProjectId;
                    _currentProjectName = MontageProjectStorage.SanitizeDisplayName(doc.ProjectName);
                    _projectCreatedUtc = doc.CreatedUtc;
                    _lastSavedUtc = doc.UpdatedUtc;
                    _hasUnsavedChanges = modifiedDuringRelocate;

                    string managedDir = Path.GetFullPath(MontageProjectStorage.ProjectsDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string fullLoaded = Path.GetFullPath(filePath);
                    if (!fullLoaded.StartsWith(managedDir, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentExternalProjectPath = fullLoaded;
                        MontageProjectStorage.RegisterRecentExternalProject(fullLoaded);
                    }
                    else
                    {
                        _currentExternalProjectPath = null;
                    }
                }
            }
            finally
            {
                _suppressDirtyTracking = false;
            }

            RefreshSessionChrome();
            SelectAssetInBin(_project.MediaBin.FirstOrDefault());
            SyncAllDynamicTrackRows(animate: false);

            foreach (SourceMediaAsset asset in _project.Assets.Values)
                StartAssetVisualExtraction(asset);

            if (HasAnyTimelineClips())
            {
                await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
            }
            else
            {
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewPlaceholder.Text = "Timeline empty — Drag media from Project Bin";
            }

            UpdateProjectHeaderAndHud();
            UpdateTimeLabels();
            UpdateExportSummaryHint();
            UpdateAspectOverlay();
            ApplyActiveFrameTransforms();
            ApplyPlayerAudioAndSpeed();
            RenderTimeline();
            RenderJogTicks(_jogVisualOffset);
            UpdateUndoRedoUi();
            UpdateSaveButtonUi();

            if (doc.ZoomSliderValue > 0.01 || doc.TimelineHorizontalOffset > 0)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _zoomFactor = SliderToZoomFactor(doc.ZoomSliderValue, GetTimelineViewportWidth());
                    UpdateTimelineLayoutInPlace();
                    if (doc.TimelineHorizontalOffset > 0)
                        TimelineScrollViewer?.ScrollToHorizontalOffset(doc.TimelineHorizontalOffset);
                }, DispatcherPriority.Loaded);
            }

            ShowTimelineToast(doc.IsAutoSave
                ? $"Recovered draft · {_currentProjectName}"
                : $"Loaded project · {_currentProjectName}");
            Focus();
        }
        catch (Exception ex)
        {
            AppDialog.ShowError(this, "Open Project Failed", $"Could not load this project:\n{ex.Message}");
        }
        finally
        {
            _isPersistenceBusy = false;
            UpdateSaveButtonUi();
        }
    }

    private void RestoreTrackMuteAndPlaybackSettings(MontageProjectDocument doc)
    {
        for (int v = 0; v < MontageProject.MaxVideoLayers; v++)
            _isVideoTrackMuted[v] = doc.VideoTrackMuted != null && v < doc.VideoTrackMuted.Length && doc.VideoTrackMuted[v];

        for (int a = 0; a < MontageProject.MaxAudioTracks; a++)
            _isAudioTrackMuted[a] = doc.AudioTrackMuted != null && a < doc.AudioTrackMuted.Length && doc.AudioTrackMuted[a];

        if (TrackV2MuteButton != null) TrackV2MuteButton.IsChecked = _isVideoTrackMuted[1];
        if (TrackV3MuteButton != null) TrackV3MuteButton.IsChecked = _isVideoTrackMuted[2];

        _volume = Math.Clamp(doc.Volume, 0.0, 1.0);
        if (VolumeSlider != null) VolumeSlider.Value = _volume;
        _isMuted = doc.IsMuted;

        _speedRatio = doc.SpeedRatio > 0 ? Math.Clamp(doc.SpeedRatio, 0.25, 2.0) : 1.0;
        if (SpeedButton != null) SpeedButton.Content = $"{_speedRatio:0.##}×";

        SyncMuteUi();

        _isSnapEnabled = doc.IsSnapEnabled;
        if (SnapToggle != null) SnapToggle.IsChecked = _isSnapEnabled;
        if (LoopToggle != null) LoopToggle.IsChecked = doc.IsLoopEnabled;
    }

    private void RestoreExportSettings(MontageProjectDocument doc)
    {
        bool fastCopy = string.Equals(doc.RenderMode, nameof(VideoCutMode.FastLossless), StringComparison.OrdinalIgnoreCase);
        if (ModeFastCopy != null) ModeFastCopy.IsChecked = fastCopy;
        if (ModeSmartRender != null) ModeSmartRender.IsChecked = !fastCopy;

        SelectComboBoxItemByTag(FormatCombo, doc.OutputFormat);
        SelectComboBoxItemByTag(ResolutionCombo, doc.ResolutionPreset);
        SelectComboBoxItemByTag(AspectFitCombo, doc.AspectFitMode);

        _outputFolderOverride = !string.IsNullOrWhiteSpace(doc.OutputFolderOverride) && Directory.Exists(doc.OutputFolderOverride)
            ? doc.OutputFolderOverride
            : null;
        string fallbackDir = _project.MediaBin.FirstOrDefault() is { } firstAsset
            ? (Path.GetDirectoryName(firstAsset.FilePath) ?? string.Empty)
            : string.Empty;
        OutputPathText.Text = _outputFolderOverride ?? fallbackDir;
    }

    private static void SelectComboBoxItemByTag(ComboBox? combo, string? tag)
    {
        if (combo == null || string.IsNullOrWhiteSpace(tag)) return;
        foreach (object item in combo.Items)
        {
            if (item is ComboBoxItem cbi &&
                string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
    }
}
