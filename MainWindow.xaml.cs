using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Win32;
using System.Collections.ObjectModel;

using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using System.Threading;

using System.ComponentModel;
using System.Runtime.CompilerServices;

using CortexFX.Core.Audio;
using CortexFX.Core.Configuration;
using CortexFX.Core.Services.Media;
using CortexFX.Core.Constants;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Dialogs;
using CortexFX.Models;

namespace CortexFX;

/// <summary>
/// Main window shell, navigation, and conversion flow.
/// </summary>
public partial class MainWindow : Window
{
    #region Fields

    private readonly IAppConfiguration _config;
    private readonly IConversionRouter _conversionRouter;
    private readonly IProcessManager _processManager;
    private readonly IMagickService _magickService;
    private readonly IResourceValidationService _resourceValidator;
    private readonly UpdateService _updateService;
    private ObservableCollection<FileModel> _filesToConvert = new ObservableCollection<FileModel>();
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isConverting;
    private bool _formatWasEnabledBeforeConversion;
    private int _activeConversionTotalFiles;
    private int _activeConversionCompletedFiles;
    private string? _lastOutputFolder;
    private const int MaxBatchFiles = 100;
    private TrayIconService? _trayIcon;
    private IInputElement? _settingsReturnFocus;

    // FFME
    private string _ffmpegBinPath = string.Empty;
    private string? _pendingStartupProjectFile;

    #endregion

    #region Construction & Startup

    public MainWindow(
        IAppConfiguration config,
        IConversionRouter conversionRouter,
        IProcessManager processManager,
        IMagickService magickService,
        IResourceValidationService resourceValidator,
        UpdateService updateService,
        string? startupFile = null)
    {
        _config = config;
        _conversionRouter = conversionRouter;
        _processManager = processManager;
        _magickService = magickService;
        _resourceValidator = resourceValidator;
        _updateService = updateService;

        try
        {
            InitializeComponent();
            AudioEditorGrid.Owner = this;
            AudioSeparatorEditor.Owner = this;
            AudioSeparatorEditor.SeekRequested += SeekSeparationAudio;
            AudioSeparatorEditor.VolumeRequested += SetSeparationPreviewVolume;
            AudioSeparatorEditor.ToggleMuteRequested += () => PreviewSound_Click(this, new RoutedEventArgs());

            VideoCompressorEditor.Initialize(_config.FFmpegPath);
            VideoCompressorEditor.CloseRequested += (s, e) => SwitchToMode(AppMode.Dashboard);
            VideoCutterEditor.Initialize(_config.FFmpegPath);
            VideoCutterEditor.CloseRequested += (s, e) => SwitchToMode(AppMode.Dashboard);
            VideoCutterEditor.WorkspaceActivated += (_, _) => MaximizeWindowForStudio();

            FilesList.ItemsSource = _filesToConvert;
            PopulateFormats("Document");

            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v1.9.5";
            ConsoleLogger.Info("UI", $"Version label set to {VersionText.Text}.");

            // File passed from Explorer context menu or .cfxproj launch
            if (!string.IsNullOrEmpty(startupFile) && File.Exists(startupFile))
            {
                ConsoleLogger.Info("Startup", $"Startup file detected: {ConsoleLogger.ShortPath(startupFile)}");
                if (IsVideoProjectFile(startupFile))
                {
                    _pendingStartupProjectFile = startupFile;
                    SwitchToMode(AppMode.VideoCutter);
                }
                else
                {
                    AddFileToList(startupFile);
                    ShowConversionView();
                }
            }

            if (ContextMenuCheckBox != null)
            {
                ContextMenuCheckBox.IsChecked = RegistryManager.IsRegistered();
            }
            RegistryManager.EnsureProjectFileAssociation();

            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;
            SyncLanguageSelectorUi();
            Loc.LanguageChanged += OnLanguageChanged;
            RefreshDynamicLocalizedText();
            UpdateConvertButtonAvailability();

            _trayIcon = new TrayIconService(this);
            _trayIcon.Attach();
            InitializeRadialWheel();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Startup", ex.Message);
            try
            {
                AppDialog.ShowError(null, "Cortex FX Crash", $"Startup Error: {ex}");
            }
            catch
            {
                MessageBox.Show($"Startup Error: {ex}", "Cortex FX Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// Called when the user launches Cortex FX again (desktop shortcut, Explorer, etc.)
    /// while it is already running in the tray.
    /// </summary>
    internal void ActivateFromExternalLaunch(string? filePath)
    {
        _trayIcon?.RestoreFromTray();

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        ConsoleLogger.Info("Startup", $"File received from another launch: {ConsoleLogger.ShortPath(filePath)}");
        if (IsVideoProjectFile(filePath))
        {
            SwitchToMode(AppMode.VideoCutter);
            VideoCutterEditor?.OpenProjectFile(filePath);
            return;
        }

        AddFileToList(filePath);
        ShowConversionView();
    }

    private static bool IsVideoProjectFile(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), CortexFX.Core.Video.MontageProjectStorage.FileExtension, StringComparison.OrdinalIgnoreCase);

    private bool _cleanedUpOnExit;

    internal void FlushSessionStateOnExit() => CleanupResourcesOnExit();

    internal void CleanupResourcesOnExit()
    {
        if (_cleanedUpOnExit)
            return;
        _cleanedUpOnExit = true;

        try { VideoCutterEditor?.Shutdown(); } catch { }
        try { VideoCompressorEditor?.Shutdown(); } catch { }

        try
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
        catch { }

        try
        {
            StopStemPreview();
            CancelAudioSeparation();
            CancelWaveformRender();
            _playbackTimer?.Stop();
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
            if (_audioReader != null)
            {
                _audioReader.Dispose();
                _audioReader = null;
            }
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        CleanupResourcesOnExit();
        base.OnClosed(e);
    }

    private void SyncLanguageSelectorUi()
    {
        if (LangEnRadio != null) LangEnRadio.IsChecked = !Loc.IsArabic;
        if (LangArRadio != null) LangArRadio.IsChecked = Loc.IsArabic;
    }

    private void LanguageOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string langCode)
        {
            Loc.SetLanguage(langCode, save: true);
        }
    }

    private void OnLanguageChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnLanguageChanged);
            return;
        }

        SyncLanguageSelectorUi();
        RefreshDynamicLocalizedText();
    }

    private static bool IsFormatPlaceholder(ComboBoxItem? item)
    {
        if (item == null) return true;
        if (Equals(item.Tag, "PLACEHOLDER")) return true;
        string text = item.Content?.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ||
               text.Contains("Ready", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("No common", StringComparison.OrdinalIgnoreCase);
    }

    private static ComboBoxItem CreateFormatPlaceholderItem(string resourceKey) =>
        new() { Content = Loc.T(resourceKey), Tag = "PLACEHOLDER" };

    private string GetLocalizedCategoryModeTitle(string category) => category switch
    {
        "Image" => Loc.T("Loc_Mode_Image"),
        "Video" => Loc.T("Loc_Mode_Video"),
        "Audio" => Loc.T("Loc_Mode_Audio"),
        "Document" => Loc.T("Loc_Mode_Document"),
        "Archive" => Loc.T("Loc_Mode_Archive"),
        "Ebook" => Loc.T("Loc_Mode_Ebook"),
        _ => Loc.T("Loc_Mode_Universal")
    };

    private string GetLocalizedModeTitle()
    {
        if (_currentMode == AppMode.Universal)
        {
            if (_universalFilterMode != null)
                return GetLocalizedCategoryModeTitle(_universalFilterMode);

            if (_filesToConvert.Count > 0)
            {
                string ext = System.IO.Path.GetExtension(_filesToConvert[0].FullPath).ToLowerInvariant();
                string category = MediaTypes.GetCategory(ext);
                if (category != "Unknown")
                    return GetLocalizedCategoryModeTitle(category);
                if (!string.IsNullOrEmpty(ext))
                    return Loc.F("Loc_Mode_ExtConverter", ext.TrimStart('.').ToUpperInvariant());
            }

            return Loc.T("Loc_Mode_Universal");
        }

        return _currentMode switch
        {
            AppMode.Dashboard => Loc.T("Loc_Mode_SelectTool"),
            AppMode.PdfToWord => Loc.T("Loc_Tool_PdfToWord"),
            AppMode.WordToPdf => Loc.T("Loc_Tool_WordToPdf"),
            AppMode.PdfToPpt => Loc.T("Loc_Tool_PdfToPpt"),
            AppMode.PptToPdf => Loc.T("Loc_Tool_PptToPdf"),
            AppMode.ExcelToPdf => Loc.T("Loc_Tool_ExcelToPdf"),
            AppMode.PdfToImage => Loc.T("Loc_Tool_PdfToImage"),
            AppMode.VideoCompressor => Loc.T("Loc_Tool_VideoCompressor"),
            AppMode.VideoCutter => Loc.T("Loc_Tool_VideoCutter"),
            AppMode.AudioTrimmer => _audioSeparationMode ? Loc.T("Loc_Tool_AudioSeparator") : Loc.T("Loc_Tool_AudioCutter"),
            _ => Loc.T("Loc_Mode_Universal")
        };
    }

    private void RefreshDynamicLocalizedText()
    {
        if (CurrentToolTitle != null)
            CurrentToolTitle.Text = GetLocalizedModeTitle();

        if (FormatComboBox?.Items.Count == 1 && FormatComboBox.Items[0] is ComboBoxItem placeholder && IsFormatPlaceholder(placeholder))
        {
            string key = _filesToConvert.Count == 0 ? "Loc_Format_ReadyAny" : "Loc_Format_NoCommon";
            placeholder.Content = Loc.T(key);
        }

        if (MaximizeButton != null)
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? Loc.T("Loc_Tip_Restore") : Loc.T("Loc_Tip_Maximize");

        if (!_isConverting && StatusText != null)
            StatusText.Text = Loc.T("Loc_Status_ToolsReady");

        if (UpdateStatusText != null && CheckForUpdatesButton?.IsEnabled == true)
            UpdateStatusText.Text = Loc.T("Loc_Settings_UpdatesDefault");

        if (ConvertButton != null)
        {
            ConvertButton.Tag = _isConverting ? "CANCEL" : "CONVERT";
            ConvertButton.Content = Loc.T(_isConverting ? "Loc_Conv_CancelBtn" : "Loc_Conv_ConvertBtn");
        }

        UpdateConversionSummary();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = Loc.T("Loc_Status_CheckingTools");
        ConsoleLogger.Info("Startup", "Background startup checks started.");

        try
        {
            var resourceStatus = await _resourceValidator.ValidateCoreResourcesAsync();
            if (!resourceStatus.IsReady)
            {
                ConsoleLogger.Warning("Resources", BuildResourceStatusMessage(resourceStatus));
                StatusText.Text = Loc.T("Loc_Status_MissingTools");
                ShowToolStatus();
            }
            else
            {
                ConsoleLogger.Success("Resources", $"Core tools ready at {ConsoleLogger.ShortPath(resourceStatus.ResourcesDirectory)}.");
                StatusText.Text = Loc.T("Loc_Status_ToolsReady");
            }

            await LoadFfmeAsync();

            if (!string.IsNullOrWhiteSpace(_pendingStartupProjectFile) && File.Exists(_pendingStartupProjectFile))
            {
                string projectPath = _pendingStartupProjectFile;
                _pendingStartupProjectFile = null;
                SwitchToMode(AppMode.VideoCutter);
                VideoCutterEditor?.OpenProjectFile(projectPath);
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Startup", $"Startup checks failed: {ex}");
            StatusText.Text = Loc.T("Loc_Status_StartupFailed");
        }
    }

    private async Task LoadFfmeAsync()
    {
        string targetFolder = _config.FFmpegLibsDirectory;
        _ffmpegBinPath = targetFolder;

        try
        {
            await Task.Run(() =>
            {
                Unosquare.FFME.Library.FFmpegDirectory = _ffmpegBinPath;
                Unosquare.FFME.Library.LoadFFmpeg();
            });
            ConsoleLogger.Success("Engine", $"FFME loaded from {ConsoleLogger.ShortPath(_ffmpegBinPath)}.");
        }
        catch (Exception ffmpegEx)
        {
            ConsoleLogger.Error("Engine", $"FFME load failed from {targetFolder}: {ffmpegEx.Message}");
            StatusText.Text = Loc.T("Loc_Status_FfmeUnavailable");
        }
    }

    private static string BuildResourceStatusMessage(ResourceValidationResult status)
    {
        var parts = new List<string>
        {
            $"Resources: {status.ResourcesDirectory}"
        };

        if (!status.ResourcesDirectoryExists)
        {
            parts.Add("Resources folder missing.");
        }

        if (status.MissingTools.Count > 0)
        {
            parts.Add($"Missing tools: {string.Join(", ", status.MissingTools)}");
        }

        if (status.MissingFFmpegDlls.Count > 0)
        {
            parts.Add($"Missing FFME DLLs in {status.FFmpegLibsDirectory}: {string.Join(", ", status.MissingFFmpegDlls)}");
        }

        return string.Join(" ", parts);
    }

    #endregion

    #region Dashboard / Navigation

    private void ShowConversionView(string? toolTag = null)
    {
        ShowMainContent(ConversionView);
        BackBtn.Visibility = Visibility.Visible;
    }

    private void ShowMainContent(FrameworkElement activeView)
    {
        BackBtn.Margin = ReferenceEquals(activeView, VideoCutterEditor) || ReferenceEquals(activeView, AudioSeparatorEditor)
            ? new Thickness(12, 3, 12, 3)
            : new Thickness(12, 8, 12, 8);
        CollapseMainContent(DashboardView, activeView);
        CollapseMainContent(ConversionView, activeView);
        if (AudioSeparatorEditor != null) CollapseMainContent(AudioSeparatorEditor, activeView);
        if (AudioEditorGrid != null)
        {
            CollapseMainContent(AudioEditorGrid, activeView);
        }

        if (VideoCompressorEditor != null)
        {
            CollapseMainContent(VideoCompressorEditor, activeView);
        }

        if (VideoCutterEditor != null)
        {
            CollapseMainContent(VideoCutterEditor, activeView);
        }

        bool wasVisible = activeView.Visibility == Visibility.Visible;
        activeView.Visibility = Visibility.Visible;

        if (!wasVisible)
        {
            AnimateContentIn(activeView);
            if (ReferenceEquals(activeView, ConversionView))
            {
                AnimateConversionColumnsIn();
            }
            else if (ReferenceEquals(activeView, DashboardView))
            {
                AnimateDashboardCardsIn();
            }
        }
    }

    private static void CollapseMainContent(FrameworkElement? view, FrameworkElement activeView)
    {
        if (view == null || ReferenceEquals(view, activeView))
        {
            return;
        }

        view.BeginAnimation(UIElement.OpacityProperty, null);
        view.Opacity = 1;

        if (view.RenderTransform is TranslateTransform transform)
        {
            if (transform.IsFrozen)
            {
                transform = transform.Clone();
                view.RenderTransform = transform;
            }
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0;
        }

        view.Visibility = Visibility.Collapsed;
    }

    private static void AnimateContentIn(FrameworkElement view)
    {
        if (view.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            view.RenderTransform = transform;
        }
        else if (transform.IsFrozen)
        {
            transform = transform.Clone();
            view.RenderTransform = transform;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            view.BeginAnimation(UIElement.OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            view.Opacity = 1;
            transform.Y = 0;
            return;
        }

        view.Opacity = 0;
        transform.Y = 10;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);

        view.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = easing
        });

        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration)
        {
            EasingFunction = easing
        });
    }

    private void AnimateConversionColumnsIn()
    {
        FrameworkElement?[] columns = [ConversionColLeft, ConversionColMid, ConversionColRight];
        if (!SystemParameters.ClientAreaAnimation)
        {
            foreach (FrameworkElement? col in columns)
            {
                if (col == null) continue;
                col.BeginAnimation(UIElement.OpacityProperty, null);
                col.Opacity = 1;
                if (col.RenderTransform is TranslateTransform t && !t.IsFrozen)
                {
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                    t.Y = 0;
                }
            }
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < columns.Length; i++)
        {
            FrameworkElement? col = columns[i];
            if (col == null) continue;

            if (col.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
            {
                translate = new TranslateTransform();
                col.RenderTransform = translate;
            }

            var delay = TimeSpan.FromMilliseconds(i * 55);
            var duration = TimeSpan.FromMilliseconds(260);

            col.Opacity = 0;
            translate.Y = 14;

            col.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
            {
                BeginTime = delay,
                EasingFunction = easing
            }, HandoffBehavior.SnapshotAndReplace);

            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration)
            {
                BeginTime = delay,
                EasingFunction = easing
            }, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void AnimateDashboardCardsIn()
    {
        if (PopularToolsPanel == null || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        int index = 0;
        foreach (UIElement child in PopularToolsPanel.Children)
        {
            if (child is not FrameworkElement card) continue;

            if (card.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
            {
                translate = new TranslateTransform();
                card.RenderTransform = translate;
            }

            var delay = TimeSpan.FromMilliseconds(Math.Min(index * 22, 220));
            var duration = TimeSpan.FromMilliseconds(240);

            card.Opacity = 0;
            translate.Y = 12;

            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
            {
                BeginTime = delay,
                EasingFunction = easing
            }, HandoffBehavior.SnapshotAndReplace);

            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, duration)
            {
                BeginTime = delay,
                EasingFunction = easing
            }, HandoffBehavior.SnapshotAndReplace);
            index++;
        }
    }

    public enum AppMode
    {
        Dashboard,
        Universal,       // New Smart Mode
        PdfToWord,
        WordToPdf,
        PdfToPpt,
        PptToPdf,
        ExcelToPdf,
        PdfToImage,
        VideoCompressor,
        VideoCutter,
        AudioTrimmer,
        Unknown
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> _conversionRules => MediaTypes.ConversionRules;

    private AppMode _currentMode = AppMode.Dashboard;

    private string? _universalFilterMode = null; // null = All, or "Video", "Audio", "Image", "Document", "Archive", "Ebook"
    private AppMode _audioReturnMode = AppMode.Universal;
    private string? _audioReturnFilterMode = null;

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.IsChecked == true)
        {
            if (rb.Tag == null) return;
            string category = rb.Tag.ToString()!;

            // Switch to Universal Mode with Filter
            _universalFilterMode = category;
            SwitchToMode(AppMode.Universal);

            // Update Title specifically for the category
            CurrentToolTitle.Text = GetLocalizedCategoryModeTitle(category);

            // Pre-populate formats for this category
            PopulateFormats(category);

            // Re-enable dropdown if it was disabled by Universal mode reset
            if (FormatComboBox.Items.Count > 0)
            {
                FormatComboBox.SelectedIndex = 0;
                FormatComboBox.IsEnabled = true;
            }

            UpdateConversionSummary();
        }
    }

    private void UpdateUIMode(bool isEditing, string tool = "VideoCompressor")
    {
        if (isEditing)
        {
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;

            // Show the requested tool
            if (tool == "VideoCompressor")
            {
                if (VideoCompressorEditor != null) ShowMainContent(VideoCompressorEditor);
                CurrentToolTitle.Text = Loc.T("Loc_Tool_VideoCompressor");
                BackBtn.Visibility = Visibility.Collapsed; // The compressor has its own session-aware Back button.
            }
            else if (tool == "VideoCutter")
            {
                if (VideoCutterEditor != null) ShowMainContent(VideoCutterEditor);
                CurrentToolTitle.Text = Loc.T("Loc_Tool_VideoCutter");
                BackBtn.Visibility = Visibility.Collapsed; // Video Cutter has its own session-aware Back button in the studio header.
            }
            else if (tool == "AudioCutter")
            {
                if (AudioEditorGrid != null) ShowMainContent(AudioEditorGrid);
                CurrentToolTitle.Text = Loc.T("Loc_Tool_AudioCutter");
                BackBtn.Visibility = Visibility.Visible;
            }
        }
        else
        {
            _currentMode = AppMode.Dashboard;
            ShowMainContent(DashboardView);
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;
            CurrentToolTitle.Text = Loc.T("Loc_Mode_SelectTool");
            BackBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void SwitchToMode(AppMode mode)
    {
        _currentMode = mode;

        if (mode != AppMode.Universal)
        {
            _universalFilterMode = null;
        }

        if (mode == AppMode.Dashboard)
        {
            ShowMainContent(DashboardView);
            CurrentToolTitle.Text = Loc.T("Loc_Mode_SelectTool");
            FormatComboBox.IsEnabled = true;
            BackBtn.Visibility = Visibility.Collapsed;

            // Hide Top Nav
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;

            // Uncheck categories
            if (RadioImage != null) RadioImage.IsChecked = false;
            if (RadioVideo != null) RadioVideo.IsChecked = false;
            if (RadioAudio != null) RadioAudio.IsChecked = false;
            if (RadioDocument != null) RadioDocument.IsChecked = false;
            if (RadioArchive != null) RadioArchive.IsChecked = false;
            if (RadioEbook != null) RadioEbook.IsChecked = false;
        }
        else if (mode == AppMode.Universal)
        {
            ShowMainContent(ConversionView);
            BackBtn.Visibility = Visibility.Visible;

            // Show Top Nav for Universal Mode
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Visible;

            if (_universalFilterMode == null)
            {
                CurrentToolTitle.Text = Loc.T("Loc_Mode_Universal");
                FormatComboBox.Items.Clear();
                FormatComboBox.Items.Add(CreateFormatPlaceholderItem("Loc_Format_ReadyAny"));
                FormatComboBox.SelectedIndex = 0;
                FormatComboBox.IsEnabled = false; // Disabled until file drop
            }
        }
        else if (mode == AppMode.VideoCompressor)
        {
            UpdateUIMode(true, "VideoCompressor");
        }
        else if (mode == AppMode.VideoCutter)
        {
            UpdateUIMode(true, "VideoCutter");
        }
        else if (mode == AppMode.AudioTrimmer)
        {
            // UI is shown after a file is chosen (or an active session is restored).
            _currentMode = AppMode.AudioTrimmer;
        }
        else
        {
            ShowMainContent(ConversionView);
            BackBtn.Visibility = Visibility.Visible;

            // Hide Top Nav for specific single-purpose tools (keep it clean)
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;

            // Configure UI based on mode
            string category = "Document";
            string? strictTarget = null;

            switch (mode)
            {
                case AppMode.PdfToWord: strictTarget = "DOCX"; break;
                case AppMode.WordToPdf: strictTarget = "PDF"; break;
                case AppMode.PdfToPpt: strictTarget = "PPTX"; break;
                case AppMode.PptToPdf: strictTarget = "PDF"; break;
                case AppMode.ExcelToPdf: strictTarget = "PDF"; break;
                case AppMode.PdfToImage: strictTarget = "JPG"; category = "Image"; break;
            }

            CurrentToolTitle.Text = GetLocalizedModeTitle();
            PopulateFormats(category, strictTarget);

            // Auto-select and lock (if standard format)
            if (FormatComboBox.Items.Count > 0)
            {
                FormatComboBox.SelectedIndex = 0;
            }

            FormatComboBox.IsEnabled = false;
            UpdateConversionSummary();
        }
    }

    private void ToolCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        AppMode mode = AppMode.Unknown;

        if (sender is FrameworkElement element && element.Tag is AppMode tagMode)
        {
            mode = tagMode;
        }
        // Fallback for XAML Tag strings if binding failed or old XAML
        else if (sender is FrameworkElement el && el.Tag is string tagStr)
        {
            mode = GetModeForToolTag(tagStr);
        }

        if (mode != AppMode.Unknown)
        {
            if (mode == AppMode.AudioTrimmer)
            {
                _audioSeparationMode = false;
            }
            SwitchToMode(mode);

            if (IsQuickFilePickerMode(mode))
            {
                Dispatcher.BeginInvoke(new Action(OpenFilesForCurrentMode), System.Windows.Threading.DispatcherPriority.Background);
            }
            else if (mode == AppMode.VideoCompressor)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Keep an in-progress / loaded compressor session — don't force a new file dialog.
                    if (VideoCompressorEditor != null && !VideoCompressorEditor.HasActiveSession)
                        VideoCompressorEditor.OpenFilePicker();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            else if (mode == AppMode.VideoCutter)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (VideoCutterEditor != null && VideoCutterEditor.ShouldAutoOpenFilePicker)
                        VideoCutterEditor.OpenFilePicker();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            else if (mode == AppMode.AudioTrimmer)
            {
                Dispatcher.BeginInvoke(new Action(OpenAudioCutterPickerIfNeeded), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        e.Handled = true;
    }

    private static bool IsQuickFilePickerMode(AppMode mode)
    {
        return mode is AppMode.PdfToWord
            or AppMode.WordToPdf
            or AppMode.PdfToPpt
            or AppMode.PptToPdf
            or AppMode.ExcelToPdf
            or AppMode.PdfToImage;
    }

    private void ConfigureTool(string tag)
    {
        // Dashboard tool tag → mode
        var mode = GetModeForToolTag(tag);

        if (mode != AppMode.Unknown)
        {
            SwitchToMode(mode);
        }
    }

    private static AppMode GetModeForToolTag(string tag)
    {
        return tag switch
        {
            "PDF_DOCX" => AppMode.PdfToWord,
            "DOCX_PDF" => AppMode.WordToPdf,
            "PDF_PPTX" => AppMode.PdfToPpt,
            "PPTX_PDF" => AppMode.PptToPdf,
            "XLSX_PDF" => AppMode.ExcelToPdf,
            "PDF_JPG" => AppMode.PdfToImage,
            "VIDEO_COMPRESSOR" => AppMode.VideoCompressor,
            "VIDEO_CUTTER" => AppMode.VideoCutter,
            "AUDIO_CUTTER" => AppMode.AudioTrimmer,
            "MORE_TOOLS" => AppMode.Universal, // Changed from AdvancedGallery to Universal
            _ => AppMode.Unknown
        };
    }

    private void OpenAudioCutterPickerIfNeeded()
    {
        // Keep an active trim session when returning from Back.
        if (!string.IsNullOrEmpty(_currentAudioFile) && File.Exists(_currentAudioFile))
        {
            ShowMainContent(_audioSeparationMode ? AudioSeparatorEditor : AudioEditorGrid);
            CurrentToolTitle.Text = _audioSeparationMode ? Loc.T("Loc_Tool_AudioSeparator") : Loc.T("Loc_Tool_AudioCutter");
            BackBtn.Visibility = Visibility.Visible;
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Loc_Picker_SelectAudio"),
            Filter = "Audio Files|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.wma;*.opus;*.aiff;*.aif|All Files|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            _audioReturnMode = AppMode.Dashboard;
            _audioReturnFilterMode = null;
            LoadAudioEditor(dlg.FileName);
        }
        else
        {
            SwitchToMode(AppMode.Dashboard);
        }
    }

    #endregion

    #region Files & Formats

    private void RefreshFormatsFromSelectedFiles()
    {
        if (_currentMode != AppMode.Universal || FormatComboBox == null)
        {
            return;
        }

        if (_filesToConvert.Count == 0)
        {
            if (_universalFilterMode != null)
            {
                PopulateFormats(_universalFilterMode);
            }
            else
            {
                FormatComboBox.Items.Clear();
                FormatComboBox.Items.Add(CreateFormatPlaceholderItem("Loc_Format_ReadyAny"));
                FormatComboBox.SelectedIndex = 0;
                FormatComboBox.IsEnabled = false;
                CurrentToolTitle.Text = Loc.T("Loc_Mode_Universal");
            }
            return;
        }

        var selectedExtensions = _filesToConvert
            .Select(f => System.IO.Path.GetExtension(f.FullPath).ToLowerInvariant())
            .Where(ext => _conversionRules.ContainsKey(ext))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedExtensions.Count == 0)
        {
            return;
        }

        var firstFormats = _conversionRules[selectedExtensions[0]];
        var commonFormats = new HashSet<string>(firstFormats, StringComparer.OrdinalIgnoreCase);

        foreach (string ext in selectedExtensions.Skip(1))
        {
            commonFormats.IntersectWith(_conversionRules[ext]);
        }

        var visibleFormats = firstFormats
            .Where(commonFormats.Contains)
            .Where(FormatMatchesCurrentUniversalFilter)
            .ToList();

        FormatComboBox.Items.Clear();

        if (visibleFormats.Count == 0)
        {
            FormatComboBox.Items.Add(CreateFormatPlaceholderItem("Loc_Format_NoCommon"));
            FormatComboBox.SelectedIndex = 0;
            FormatComboBox.IsEnabled = false;
            return;
        }

        foreach (string fmt in visibleFormats)
        {
            FormatComboBox.Items.Add(new ComboBoxItem { Content = fmt });
        }

        FormatComboBox.SelectedIndex = 0;
        FormatComboBox.IsEnabled = true;

        if (_universalFilterMode == null)
        {
            string category = MediaTypes.GetCategory(selectedExtensions[0]);
            CurrentToolTitle.Text = category == "Unknown"
                ? Loc.T("Loc_Mode_Universal")
                : GetLocalizedCategoryModeTitle(category);
        }
    }

    private bool FormatMatchesCurrentUniversalFilter(string format)
    {
        return _universalFilterMode switch
        {
            "Archive" => MediaTypes.ArchiveOutputFormats.Contains(format),
            "Ebook" => MediaTypes.EbookOutputFormats.Contains(format),
            _ => true
        };
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.AudioTrimmer)
        {
            CloseAudioEditor(returnToPrevious: false);
        }

        SwitchToMode(AppMode.Dashboard);
        _filesToConvert.Clear();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        // Normal minimize to the Windows taskbar (not the tray)
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindowForStudio()
    {
        if (WindowState != WindowState.Maximized)
        {
            MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
            WindowState = WindowState.Maximized;
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
        else
        {
            MaximizeWindowForStudio();
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        bool isMaximized = WindowState == WindowState.Maximized;
        MaxHeight = isMaximized
            ? SystemParameters.MaximizedPrimaryScreenHeight
            : double.PositiveInfinity;

        if (MainWindowBorder != null)
        {
            MainWindowBorder.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(10);
            MainWindowBorder.BorderThickness = isMaximized ? new Thickness(0) : new Thickness(1);
            MainWindowBorder.Margin = isMaximized ? new Thickness(7) : new Thickness(0);
        }

        if (MaximizeButton != null)
        {
            MaximizeButton.Content = isMaximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = isMaximized ? Loc.T("Loc_Tip_Restore") : Loc.T("Loc_Tip_Maximize");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // X → hide next to the clock; Exit is only from the tray menu
        _trayIcon?.HideToTray();
    }

    private void AddFileToList(string file)
    {
        if (!File.Exists(file))
        {
            ConsoleLogger.Warning("Files", $"Skipped missing file: {ConsoleLogger.ShortPath(file)}");
            return;
        }

        if (_filesToConvert.Count >= MaxBatchFiles)
        {
            StatusText.Text = Loc.F("Loc_Status_BatchLimit", MaxBatchFiles);
            AppDialog.ShowInfo(
                this,
                Loc.T("Loc_Alert_BatchLimitTitle"),
                Loc.F("Loc_Alert_BatchLimitMsg", MaxBatchFiles));
            return;
        }

        // Check if file already exists in the list
        bool exists = false;
        foreach (var f in _filesToConvert)
        {
            if (f.FullPath == file)
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            _filesToConvert.Add(new FileModel
            {
                FileName = System.IO.Path.GetFileName(file),
                FullPath = file,
                FileDetails = BuildFileDetails(file),
                FileIcon = GetFileIcon(file),
                ThumbnailPath = GetThumbnailPath(file)
            });

            RefreshFormatsFromSelectedFiles();
            UpdateConvertButtonAvailability();
        }
    }

    private static string BuildFileDetails(string file)
    {
        string ext = System.IO.Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
        string size = "";

        try
        {
            var info = new FileInfo(file);
            size = FormatFileSize(info.Length);
        }
        catch
        {
            // Best-effort display only.
        }

        return string.IsNullOrWhiteSpace(size) ? ext : $"{ext} - {size}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / (1024d * 1024d * 1024d):0.##} GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024d * 1024d):0.##} MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024d:0.##} KB";
        }

        return $"{bytes} B";
    }

    private static string GetFileIcon(string file)
    {
        string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();

        if (MediaTypes.ImageExtensions.Contains(ext)) return "\uE91B";
        if (MediaTypes.VideoExtensions.Contains(ext)) return "\uE714";
        if (MediaTypes.AudioExtensions.Contains(ext)) return "\uE8D6";
        if (MediaTypes.ArchiveExtensions.Contains(ext)) return "\uE7B8";
        if (MediaTypes.EbookExtensions.Contains(ext)) return "\uE82D";
        return "\uE8A5";
    }

    private static string? GetThumbnailPath(string file)
    {
        string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico"
            ? file
            : null;
    }

    #endregion

    #region Settings / Overlays / Shell

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsReturnFocus = Keyboard.FocusedElement;
        SettingsOverlay.Visibility = Visibility.Visible;
        Keyboard.Focus(ContextMenuCheckBox);
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        if (_settingsReturnFocus != null) Keyboard.Focus(_settingsReturnFocus);
        _settingsReturnFocus = null;
    }

    private void SettingsOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseSettings_Click(sender, e);
        e.Handled = true;
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            UpdateStatusText.Text = Loc.T("Loc_Upd_Busy");
            return;
        }

        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            UpdateStatusText.Text = Loc.T("Loc_Upd_Checking");
            Version installedVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 9, 5);
            UpdateRelease? release = await _updateService.CheckAsync(installedVersion);
            if (release == null)
            {
                UpdateStatusText.Text = Loc.T("Loc_Upd_UpToDate");
                return;
            }

            UpdateStatusText.Text = Loc.F("Loc_Upd_Available", release.Tag);
            if (!AppDialog.Confirm(
                    this,
                    Loc.T("Loc_Upd_DialogTitle"),
                    Loc.F("Loc_Upd_ConfirmMsg", release.Tag),
                    confirmText: Loc.T("Loc_Upd_InstallBtn"),
                    cancelText: Loc.T("Loc_Upd_LaterBtn"),
                    tone: DialogTone.Question))
                return;

            var progress = new Progress<double>(percent =>
                UpdateStatusText.Text = Loc.F("Loc_Upd_Downloading", release.Tag, $"{percent:0}"));
            string installer = await _updateService.DownloadInstallerAsync(release, progress);
            UpdateStatusText.Text = Loc.T("Loc_Upd_Starting");
            Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
            _trayIcon?.ExitForUpdate();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Update", ex.ToString());
            UpdateStatusText.Text = Loc.T("Loc_Upd_Failed");
            AppDialog.ShowWarning(this, Loc.T("Loc_Upd_DialogTitle"), Loc.F("Loc_Upd_FailedDialog", ex.Message));
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void ShowResourceStatus_Click(object sender, RoutedEventArgs e)
    {
        ShowToolStatus();
    }

    private void ShowToolStatus()
    {
        var dialog = new ResourceStatusDialog(() => CortexFX.ViewModels.ResourceStatusReport.Create(
            _resourceValidator.ValidateCoreResources(),
            _conversionRouter.GetUnavailableReason(".docx", "pdf") == null,
            _conversionRouter.GetUnavailableReason(".pptx", "pdf") == null,
            CreateSeparationService().IsAvailable)) { Owner = this };
        dialog.ShowDialog();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(ConsoleLogger.LogDirectory);
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputFolder))
        {
            OpenFolder(_lastOutputFolder);
            return;
        }

        OpenFolder(OutputPathBox?.Text);
    }

    private void OpenOutputFolderCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenOutputFolder_Click(sender, e);
    }

    private static void OpenFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            AppDialog.ShowInfo(null, Loc.T("Loc_Alert_FolderNotFoundTitle"), Loc.T("Loc_Alert_FolderNotFoundMsg"));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Shell", $"Could not open folder {ConsoleLogger.ShortPath(folderPath)}: {ex.Message}");
            AppDialog.ShowWarning(null, Loc.T("Loc_Alert_OpenFolderFailedTitle"), Loc.F("Loc_Alert_OpenFolderFailedMsg", folderPath));
        }
    }

    private void ContextMenuCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        try
        {
            RegistryManager.RegisterContextMenu();
        }
        catch (Exception ex)
        {
            AppDialog.ShowError(this, Loc.T("Loc_Alert_RegistryErrTitle"), Loc.F("Loc_Alert_RegAddErr", ex.Message));
            ContextMenuCheckBox.IsChecked = false;
        }
    }

    private void ContextMenuCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        try
        {
            RegistryManager.UnregisterContextMenu();
        }
        catch (Exception ex)
        {
            AppDialog.ShowError(this, Loc.T("Loc_Alert_RegistryErrTitle"), Loc.F("Loc_Alert_RegRemErr", ex.Message));
        }
    }

    #endregion

    #region Drag-Drop & File Picking

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            SetDropZoneDragActive(true);
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        SetDropZoneDragActive(false);
    }

    private void SetDropZoneDragActive(bool active)
    {
        if (DropZoneDashedRect != null)
        {
            DropZoneDashedRect.Stroke = active
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E11D2E"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C2532"));
        }

        double targetOverlay = active ? 1.0 : 0.0;
        double targetScale = active ? 1.14 : 1.0;
        double targetLift = active ? -5.0 : 0.0;

        if (!SystemParameters.ClientAreaAnimation)
        {
            DropZoneHoverBorder?.BeginAnimation(UIElement.OpacityProperty, null);
            if (DropZoneHoverBorder != null) DropZoneHoverBorder.Opacity = targetOverlay;
            if (DropZoneIconScale != null)
            {
                DropZoneIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                DropZoneIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                DropZoneIconScale.ScaleX = targetScale;
                DropZoneIconScale.ScaleY = targetScale;
            }
            if (DropZoneIconTranslate != null)
            {
                DropZoneIconTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                DropZoneIconTranslate.Y = targetLift;
            }
            return;
        }

        var duration = TimeSpan.FromMilliseconds(active ? 180 : 210);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        DropZoneHoverBorder?.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(targetOverlay, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);

        if (DropZoneIconScale != null)
        {
            DropZoneIconScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(targetScale, duration) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
            DropZoneIconScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(targetScale, duration) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
        }

        DropZoneIconTranslate?.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(targetLift, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        SetDropZoneDragActive(false);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? items = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (items != null)
            {
                // One audio file → ask trim vs convert
                if (items.Length == 1 && File.Exists(items[0]))
                {
                    string ext = System.IO.Path.GetExtension(items[0]).ToLower();
                    if (MediaTypes.AudioEditorExtensions.Contains(ext))
                    {
                        // Ask: trim or convert?
                        _pendingAudioFile = items[0];
                        AudioChoiceOverlay.Visibility = Visibility.Visible;
                        return; // don't add to the convert list yet
                    }
                }

                bool invalidFound = false;

                if (_currentMode == AppMode.Universal && items.Any(File.Exists))
                {
                    string firstFile = items.First(File.Exists);
                    string ext = System.IO.Path.GetExtension(firstFile).ToLower();

                    if (_conversionRules.ContainsKey(ext))
                    {
                        var formats = _conversionRules[ext];
                        FormatComboBox.Items.Clear();
                        foreach (var fmt in formats)
                        {
                            FormatComboBox.Items.Add(new ComboBoxItem { Content = fmt });
                        }
                        if (FormatComboBox.Items.Count > 0) FormatComboBox.SelectedIndex = 0;
                        FormatComboBox.IsEnabled = true;

                        // Update UI Title to reflect detection
                        CurrentToolTitle.Text = Loc.F("Loc_Mode_ExtConverter", ext.ToUpper().TrimStart('.'));
                    }
                    else
                    {
                        AppDialog.ShowInfo(this, Loc.T("Loc_Alert_NotSupportedTitle"), Loc.F("Loc_Alert_NotSupportedMsg", ext));
                        return; // Reject drop
                    }
                }

                foreach (var item in items)
                {
                    if (Directory.Exists(item))
                    {
                        ProcessDirectory(item);
                    }
                    else
                    {
                        if (IsFileValidForMode(item))
                        {
                            AddFileToList(item);
                        }
                        else
                        {
                            invalidFound = true;
                        }
                    }
                }

                if (invalidFound)
                {
                    AppDialog.ShowWarning(this, Loc.T("Loc_Alert_InvalidTypeTitle"), Loc.F("Loc_Alert_InvalidTypeMsg", GetLocalizedModeTitle()));
                }
            }
        }
    }

    private void ProcessDirectory(string dirPath)
    {
        try
        {
            // Add supported files in current directory
            foreach (var file in Directory.GetFiles(dirPath))
            {
                // Strict Mode Check inside directory recursion too
                if (IsFileValidForMode(file))
                {
                    string ext = System.IO.Path.GetExtension(file).ToLower();
                    // Basic supported check (global)
                    if (MediaTypes.AllSupportedExtensions.Contains(ext))
                    {
                        AddFileToList(file);
                    }
                }
            }

            // Recurse subdirectories
            foreach (var subDir in Directory.GetDirectories(dirPath))
            {
                ProcessDirectory(subDir);
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warning("Files", $"Skipped folder {ConsoleLogger.ShortPath(dirPath)}: {ex.Message}");
            StatusText.Text = Loc.T("Loc_Status_FolderReadErr");
        }
    }

    private string GetFilterForCurrentMode()
    {
        if (_currentMode == AppMode.Universal && _universalFilterMode != null)
        {
            return _universalFilterMode switch
            {
                "Video" => "Video Files (*.mp4;*.avi;*.mov;*.mkv;*.webm)|*.mp4;*.avi;*.mov;*.mkv;*.webm",
                "Audio" => "Audio Files (*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg)|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg",
                "Image" => "Image Files (*.jpg;*.png;*.webp;*.tiff;*.gif;*.heic)|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.ico;*.tif;*.tiff;*.gif;*.heic;*.heif",
                "Document" => "Documents (*.pdf;*.docx;*.doc;*.pptx;*.ppt;*.xlsx;*.xls;*.odt;*.rtf;*.txt)|*.pdf;*.docx;*.doc;*.pptx;*.ppt;*.xlsx;*.xls;*.odt;*.rtf;*.txt",
                "Archive" => "Archives (*.zip;*.rar;*.7z;*.tar)|*.zip;*.rar;*.7z;*.tar",
                "Ebook" => "E-books (*.epub;*.mobi;*.azw3;*.pdf)|*.epub;*.mobi;*.azw3;*.pdf",
                _ => "All Files|*.*"
            };
        }

        return _currentMode switch
        {
            AppMode.PdfToWord or AppMode.PdfToPpt or AppMode.PdfToImage => "PDF Files (*.pdf)|*.pdf",
            AppMode.WordToPdf => "Word Documents (*.docx;*.doc)|*.docx;*.doc",
            AppMode.PptToPdf => "PowerPoint Presentations (*.pptx;*.ppt)|*.pptx;*.ppt",
            AppMode.ExcelToPdf => "Excel Workbooks (*.xlsx;*.xls)|*.xlsx;*.xls",
            _ => "All Supported Files|*.pdf;*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt;*.odt;*.rtf;*.txt;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.ico;*.tif;*.tiff;*.gif;*.heic;*.heif;*.mp4;*.avi;*.mkv;*.mov;*.webm;*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.zip;*.rar;*.7z;*.tar;*.epub;*.mobi;*.azw3"
        };
    }

    private bool IsFileValidForMode(string filePath)
    {
        if (_currentMode == AppMode.Dashboard) return true;

        string ext = System.IO.Path.GetExtension(filePath).ToLower();

        if (_currentMode == AppMode.Universal)
        {
            // If Filter is active (Video, Audio, etc.), check strictly
            if (_universalFilterMode != null)
            {
                bool isValid = _universalFilterMode switch
                {
                    "Video" => MediaTypes.VideoExtensions.Contains(ext),
                    "Audio" => MediaTypes.AudioExtensions.Contains(ext),
                    "Image" => MediaTypes.ImageExtensions.Contains(ext),
                    "Document" => MediaTypes.DocumentExtensions.Contains(ext),
                    "Archive" => MediaTypes.ArchiveExtensions.Contains(ext),
                    "Ebook" => MediaTypes.EbookExtensions.Contains(ext) || ext == ".pdf",
                    _ => true
                };
                return isValid;
            }

            // In Universal Mode (No Filter), file is valid if it exists in our rules
            return _conversionRules.ContainsKey(ext);
        }

        return _currentMode switch
        {
            AppMode.PdfToWord or AppMode.PdfToPpt or AppMode.PdfToImage => ext == ".pdf",
            AppMode.WordToPdf => ext == ".docx" || ext == ".doc",
            AppMode.PptToPdf => ext == ".pptx" || ext == ".ppt",
            AppMode.ExcelToPdf => ext == ".xlsx" || ext == ".xls",
            _ => false
        };
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        OpenFilesForCurrentMode();
        e.Handled = true;
    }

    private void OpenFilesForCurrentMode()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Loc_Picker_SelectFiles"),
            Multiselect = true,
            Filter = GetFilterForCurrentMode()
        };

        if (dialog.ShowDialog() == true)
        {
            string[] selectedFiles = dialog.FileNames;

            // One audio file → ask trim vs convert first
            if (selectedFiles.Length == 1)
            {
                string ext = System.IO.Path.GetExtension(selectedFiles[0]).ToLower();
                if (MediaTypes.AudioEditorExtensions.Contains(ext))
                {
                    _pendingAudioFile = selectedFiles[0];
                    AudioChoiceOverlay.Visibility = Visibility.Visible;
                    return;
                }
            }

            foreach (var file in dialog.FileNames)
            {
                if (IsFileValidForMode(file))
                {
                    AddFileToList(file);
                }
            }

            RefreshFormatsFromSelectedFiles();
        }
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string fullPath)
        {
            FileModel? itemToRemove = null;
            foreach (var f in _filesToConvert)
            {
                if (f.FullPath == fullPath)
                {
                    itemToRemove = f;
                    break;
                }
            }

            if (itemToRemove != null)
            {
                _filesToConvert.Remove(itemToRemove);
                RefreshFormatsFromSelectedFiles();
                UpdateConvertButtonAvailability();
            }
        }
    }

    #endregion

    #region Universal Conversion

    private void PopulateFormats(string category, string? strictTarget = null)
    {
        if (FormatComboBox == null) return;

        FormatComboBox.Items.Clear();

        // One locked target (e.g. PDF→Word tool)
        if (strictTarget != null)
        {
            FormatComboBox.Items.Add(new ComboBoxItem { Content = strictTarget.ToUpper() });
            FormatComboBox.SelectedIndex = 0;
            return;
        }

        HashSet<string> addedFormats = new HashSet<string>();

        void AddFormat(string fmt)
        {
            if (!addedFormats.Contains(fmt))
            {
                FormatComboBox.Items.Add(new ComboBoxItem { Content = fmt });
                addedFormats.Add(fmt);
            }
        }

        string[] baseFormats = new string[] { };

        switch (category)
        {
            case "Image":
                baseFormats = new[] { "JPG", "PNG", "WEBP", "BMP", "ICO", "TIFF", "GIF", "HEIC", "PDF" };
                break;
            case "Video":
                baseFormats = new[] { "MP4", "AVI", "MKV", "MOV", "GIF", "WEBM" };
                break;
            case "Audio":
                baseFormats = new[] { "MP3", "WAV", "AAC", "FLAC", "M4A", "OGG" };
                break;
            case "Document":
                baseFormats = new[] { "PDF", "DOCX", "ODT", "RTF", "TXT" };
                break;
            case "Archive":
                baseFormats = new[] { "ZIP", "7Z", "TAR" };
                break;
            case "Ebook":
                baseFormats = new[] { "EPUB", "MOBI", "AZW3", "PDF" };
                break;
        }

        // Add base formats first
        foreach (var f in baseFormats) AddFormat(f);

        if (category == "Document")
        {
            // Extra targets based on what's in the list
            bool hasPdf = _filesToConvert.Any(f => System.IO.Path.GetExtension(f.FullPath).ToLower() == ".pdf");
            bool hasWord = _filesToConvert.Any(f =>
            {
                string ext = System.IO.Path.GetExtension(f.FullPath).ToLower();
                return ext == ".docx" || ext == ".doc";
            });
            bool hasPpt = _filesToConvert.Any(f =>
            {
                string ext = System.IO.Path.GetExtension(f.FullPath).ToLower();
                return ext == ".pptx" || ext == ".ppt";
            });
            bool hasExcel = _filesToConvert.Any(f =>
            {
                string ext = System.IO.Path.GetExtension(f.FullPath).ToLower();
                return ext == ".xlsx" || ext == ".xls";
            });

            // Rule: If PDF is present -> Add Office Targets
            if (hasPdf)
            {
                AddFormat("DOCX");
                AddFormat("PPTX");
                AddFormat("EPUB");
                AddFormat("MOBI");
                AddFormat("AZW3");
            }

            // Rule: Bridge Logic (Word <-> PPT)
            if (hasWord) AddFormat("PPTX");
            if (hasPpt) AddFormat("DOCX");
            if (hasExcel) AddFormat("XLSX");
        }

        if (FormatComboBox.Items.Count > 0)
            FormatComboBox.SelectedIndex = 0;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("Loc_Picker_SelectOutput"),
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            OutputPathBox.Text = dialog.FolderName;
            // Clear error state if valid
            OutputPathBox.BorderBrush = (Brush)FindResource("BorderColor");
            OutputWarningText.Visibility = Visibility.Collapsed;
            UpdateConvertButtonAvailability();
        }
    }

    private void OutputPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateConvertButtonAvailability();
    }

    private void CloseOverlay_Click(object sender, RoutedEventArgs e)
    {
        SuccessOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = Loc.T("Loc_Conv_Ready");
        UpdateConvertButtonAvailability();
    }

    private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
    {
        e.Handled = new System.Text.RegularExpressions.Regex("[^0-9]+").IsMatch(e.Text);
    }

    private static int? TryParseOptionalInt(string value)
    {
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
    }

    private void UpdateConversionSummary()
    {
        if (SelectedFilesCountText == null || SummaryModeText == null)
        {
            return;
        }

        int fileCount = _filesToConvert.Count;
        string outputFormat = "-";

        if (FormatComboBox?.SelectedItem is ComboBoxItem item && item.Content != null && !IsFormatPlaceholder(item))
        {
            outputFormat = (item.Content.ToString() ?? string.Empty).ToUpperInvariant();
        }

        SelectedFilesCountText.Text = fileCount.ToString();
        SummaryModeText.Text = string.IsNullOrWhiteSpace(CurrentToolTitle?.Text) ? "-" : CurrentToolTitle.Text;
        SummaryFilesText.Text = fileCount.ToString();
        SummaryOutputText.Text = outputFormat;

        bool isReady = fileCount > 0 && outputFormat != "-";

        if (GifOptionsCard?.Visibility == Visibility.Visible)
        {
            var gifOpts = BuildCurrentGifOptions();
            var profile = FFmpegService.ResolveGifProfile(gifOpts);
            SummaryEstimateText.Text = gifOpts.TargetMaxSizeMb is double cap and > 0
                ? $"≤ {cap:0.##} MB · {profile.Fps} fps"
                : $"{profile.MaxDimension}px · {profile.Fps} fps";
            SummaryEstimateText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B7E"));
        }
        else
        {
            SummaryEstimateText.Text = isReady ? Loc.T("Loc_Conv_Ready") : Loc.T("Loc_Conv_Pending");
            SummaryEstimateText.Foreground = isReady
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8C94A6"));
        }

        if (StatusIndicatorDot != null)
        {
            string dotHex = _isConverting ? "#E11D2E" : (isReady ? "#34D399" : "#6E7586");
            StatusIndicatorDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dotHex));
        }
    }

    private void UpdateConvertButtonAvailability()
    {
        if (ConvertButton == null)
        {
            return;
        }

        UpdateImageOptionVisibility();
        UpdateConversionSummary();

        if (_isConverting)
        {
            return;
        }

        bool hasFiles = _filesToConvert.Count > 0;
        bool hasOutput = !string.IsNullOrWhiteSpace(OutputPathBox?.Text);
        bool hasFormat = FormatComboBox?.SelectedItem is ComboBoxItem item &&
                         item.Content != null &&
                         !IsFormatPlaceholder(item);

        ConvertButton.IsEnabled = hasFiles && hasOutput && hasFormat;
        ConvertButton.Tag = "CONVERT";
        ConvertButton.Content = Loc.T("Loc_Conv_ConvertBtn");
    }

    private void UpdateImageOptionVisibility()
    {
        if (ImageOptionsCard == null || chkMergePdf == null || FormatComboBox == null)
            return;

        bool allRasterImages = _filesToConvert.Count > 0 && _filesToConvert.All(file =>
            MediaTypes.RasterImageExtensions.Contains(System.IO.Path.GetExtension(file.FullPath).ToLowerInvariant()));
        ImageOptionsCard.Visibility = allRasterImages ? Visibility.Visible : Visibility.Collapsed;

        bool mergeAvailable = allRasterImages && _filesToConvert.Count > 1 &&
            FormatComboBox.SelectedItem is ComboBoxItem selected &&
            string.Equals(selected.Content?.ToString(), "PDF", StringComparison.OrdinalIgnoreCase);
        chkMergePdf.Visibility = mergeAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (!mergeAvailable) chkMergePdf.IsChecked = false;

        if (GifOptionsCard != null)
        {
            bool isGifTarget = FormatComboBox.SelectedItem is ComboBoxItem gifSel &&
                string.Equals(gifSel.Content?.ToString(), "GIF", StringComparison.OrdinalIgnoreCase);
            bool hasVideoInput = _filesToConvert.Any(file =>
                MediaTypes.VideoExtensions.Contains(System.IO.Path.GetExtension(file.FullPath).ToLowerInvariant()));
            bool showGifOptions = isGifTarget && !allRasterImages && (RadioVideo?.IsChecked == true || hasVideoInput);
            GifOptionsCard.Visibility = showGifOptions ? Visibility.Visible : Visibility.Collapsed;
            if (showGifOptions)
                UpdateGifProfileSummary();
        }
    }

    private GifConversionOptions BuildCurrentGifOptions()
    {
        double quality = QualitySlider?.Value ?? 75;
        double? maxSizeMb = TryParseComboTagDouble(GifMaxSizeCombo);
        int? maxWidth = TryParseComboTagInt(GifResolutionCombo);
        int? fps = TryParseComboTagInt(GifFpsCombo);
        int? maxColors = TryParseComboTagInt(GifColorsCombo);
        bool extraCompression = GifExtraCompressionCheck?.IsChecked != false;

        return new GifConversionOptions(
            QualityLevel: quality,
            MaxWidth: maxWidth,
            Fps: fps,
            MaxColors: maxColors,
            TargetMaxSizeMb: maxSizeMb,
            ExtraCompression: extraCompression);
    }

    private static int? TryParseComboTagInt(ComboBox? combo)
    {
        if (combo?.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            int.TryParse(tag, out int value) && value > 0)
            return value;
        return null;
    }

    private static double? TryParseComboTagDouble(ComboBox? combo)
    {
        if (combo?.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0)
            return value;
        return null;
    }

    private void UpdateGifProfileSummary()
    {
        if (QualityValueText != null && QualitySlider != null)
            QualityValueText.Text = $"{Math.Round(QualitySlider.Value):0}%";

        if (GifProfileSummaryText == null)
            return;

        var opts = BuildCurrentGifOptions();
        var profile = FFmpegService.ResolveGifProfile(opts);
        string capText = opts.TargetMaxSizeMb is double cap and > 0 ? $" · ≤{cap:0.##}MB" : string.Empty;
        GifProfileSummaryText.Text = $"{profile.MaxDimension}px · {profile.Fps} fps · {profile.MaxColors}c{capText}";
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityValueText != null)
            QualityValueText.Text = $"{Math.Round(e.NewValue):0}%";
        if (GifOptionsCard?.Visibility == Visibility.Visible)
        {
            UpdateGifProfileSummary();
            UpdateConversionSummary();
        }
    }

    private void GifOption_Changed(object sender, RoutedEventArgs e)
    {
        if (GifOptionsCard?.Visibility == Visibility.Visible)
        {
            UpdateGifProfileSummary();
            UpdateConversionSummary();
        }
    }

    private static string FormatBytesReadable(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private void SetConversionUiState(bool isConverting)
    {
        if (isConverting)
        {
            _formatWasEnabledBeforeConversion = FormatComboBox.IsEnabled;
            ConversionOverlay.Visibility = Visibility.Visible;
            ConversionOverlayCancelButton.IsEnabled = true;
            UpdateConversionProgressUi(Loc.T("Loc_Status_Preparing"), 0, _activeConversionTotalFiles);
        }

        _isConverting = isConverting;
        ConvertButton.IsEnabled = true;
        ConvertButton.Tag = isConverting ? "CANCEL" : "CONVERT";
        ConvertButton.Content = Loc.T(isConverting ? "Loc_Conv_CancelBtn" : "Loc_Conv_ConvertBtn");
        BrowseButton.IsEnabled = !isConverting;
        BackBtn.IsEnabled = !isConverting;
        DropZone.IsHitTestVisible = !isConverting;
        FormatComboBox.IsEnabled = isConverting ? false : _formatWasEnabledBeforeConversion;
        if (QualitySlider != null) QualitySlider.IsEnabled = !isConverting;
        if (GifOptionsCard != null) GifOptionsCard.IsEnabled = !isConverting;

        if (!isConverting)
        {
            ConversionOverlay.Visibility = Visibility.Collapsed;
            ConversionOverlayCancelButton.IsEnabled = true;
            _activeConversionCompletedFiles = 0;
            _activeConversionTotalFiles = 0;
            UpdateConvertButtonAvailability();
        }
    }

    private void UpdateConversionProgressUi(string message, int completedFiles, int totalFiles, double currentFilePercent = 0)
    {
        StatusText.Text = message;
        _activeConversionCompletedFiles = Math.Max(0, completedFiles);
        _activeConversionTotalFiles = Math.Max(0, totalFiles);

        if (ConversionOverlayMessageText == null)
        {
            return;
        }

        double percent = 0;
        if (_activeConversionTotalFiles > 0)
        {
            double fileProgress = Math.Clamp(currentFilePercent, 0, 100);
            double completedUnits = (_activeConversionCompletedFiles * 100) + fileProgress;
            percent = Math.Clamp(completedUnits / (_activeConversionTotalFiles * 100) * 100, 0, 100);
        }

        ConversionOverlayMessageText.Text = message;
        ConversionOverlayPercentText.Text = $"{percent:0}%";
        ConversionOverlayDetailText.Text = _activeConversionTotalFiles > 0
            ? Loc.F("Loc_Status_FilesProgress", _activeConversionCompletedFiles, _activeConversionTotalFiles, $"{percent:0}")
            : Loc.T("Loc_Status_PreparingFiles");
    }

    private void RequestConversionCancel()
    {
        _cancellationTokenSource?.Cancel();
        ConvertButton.IsEnabled = false;
        ConversionOverlayCancelButton.IsEnabled = false;
        UpdateConversionProgressUi(Loc.T("Loc_Status_Cancelling"), _activeConversionCompletedFiles, _activeConversionTotalFiles);
        ConversionOverlayDetailText.Text = Loc.T("Loc_Status_StoppingCurrent");
    }

    private void CancelConversion_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            RequestConversionCancel();
        }
    }

    private static string FriendlyErrorMessage(string filePath, string targetFormat, string message)
    {
        string name = System.IO.Path.GetFileName(filePath);
        string target = targetFormat.ToUpperInvariant();

        if (message == CortexFX.Core.Services.Documents.EmptyDocumentException.UserMessage)
            return $"{name}: {Loc.T("Loc_Err_EmptyDoc")}";

        if (message.StartsWith("PDF conversion failed:", StringComparison.OrdinalIgnoreCase) ||
            (message.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) && message.Contains("required", StringComparison.OrdinalIgnoreCase)))
            return $"{name}: {message}";

        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Executable", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("Loc_Err_MissingTool", name);
        }

        if (message.Contains("invalid conversion", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid argument", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exit code -22", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("Loc_Err_InvalidArgs", name, target);
        }

        if (message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("magick", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exit code", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("Loc_Err_EngineFailed", name, target);
        }

        if (message.Contains("Microsoft Office is required", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("COM", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("RPC", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("Loc_Err_Office", name);
        }

        if (message.Contains("No engine found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("Loc_Err_UnsupportedRoute", name, target);
        }

        if (message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("Loc_Err_CancelledFile", name);
        }

        return Loc.F("Loc_Err_Generic", name, target);
    }

    private void ShowConversionSummary(int successCount, int errorCount, int totalCount, string? details = null)
    {
        bool hadErrors = errorCount > 0;
        SuccessTitleText.Text = hadErrors ? Loc.T("Loc_Overlay_WarningsTitle") : Loc.T("Loc_Overlay_SuccessTitle");
        SuccessMessageText.FlowDirection = Loc.TextFlowDirection;
        SuccessMessageText.Text = details ?? (hadErrors
            ? Loc.F("Loc_Alert_ConvPartialDetail", successCount, totalCount, string.Empty, string.Empty).Trim()
            : Loc.F("Loc_Alert_ConvSuccessDetail", successCount, totalCount, string.Empty, _lastOutputFolder ?? string.Empty).Trim());
        OpenLogsButton.Visibility = hadErrors ? Visibility.Visible : Visibility.Collapsed;
        SuccessOverlay.Visibility = Visibility.Visible;
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            RequestConversionCancel();
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            OutputPathBox.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4D5E"));
            OutputWarningText.Visibility = Visibility.Visible;
            return;
        }
        else
        {
            OutputPathBox.BorderBrush = (Brush)FindResource("BorderColor");
            OutputWarningText.Visibility = Visibility.Collapsed;
        }

        if (_filesToConvert.Count == 0)
        {
            AppDialog.ShowInfo(this, Loc.T("Loc_Alert_NoFilesTitle"), Loc.T("Loc_Alert_NoFilesMsg"));
            UpdateConvertButtonAvailability();
            return;
        }

        if (_filesToConvert.Count > MaxBatchFiles)
        {
            AppDialog.ShowWarning(this, Loc.T("Loc_Alert_BatchLargeTitle"), Loc.F("Loc_Alert_BatchLargeMsg", MaxBatchFiles));
            return;
        }

        if (FormatComboBox.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Content == null)
            return;

        if (IsFormatPlaceholder(selectedItem))
        {
            AppDialog.ShowInfo(this, Loc.T("Loc_Alert_FormatNeededTitle"), Loc.T("Loc_Alert_FormatNeededMsg"));
            return;
        }

        string targetFormat = selectedItem.Content.ToString()!.ToLowerInvariant();

        string outputDir = OutputPathBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Conversion", $"Output folder unavailable: {ex.Message}");
            AppDialog.ShowWarning(this, Loc.T("Loc_Alert_OutputUnavailTitle"), Loc.F("Loc_Alert_OutputUnavailMsg", outputDir));
            return;
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        double qualityLevel = QualitySlider.Value;

        // Advanced Settings
        string resizeW = ResizeWidthBox.Text;
        string resizeH = ResizeHeightBox.Text;
        bool maintainAspect = AspectRatioCheckBox.IsChecked == true;
        string dpi = DpiBox.Text;
        bool sharpen = SharpenCheckBox.IsChecked == true;
        bool grayscale = GrayscaleCheckBox.IsChecked == true;
        bool autoEnhance = AutoEnhanceCheckBox.IsChecked == true;

        var imageOptions = new ImageConversionOptions(
            Quality: Math.Clamp((int)qualityLevel, 1, 100),
            ResizeWidth: TryParseOptionalInt(resizeW),
            ResizeHeight: TryParseOptionalInt(resizeH),
            MaintainAspectRatio: maintainAspect,
            Dpi: TryParseOptionalInt(dpi),
            Sharpen: sharpen,
            Grayscale: grayscale,
            AutoEnhance: autoEnhance);

        bool createSubfolder = chkCreateSubfolder.IsChecked == true;
        _lastOutputFolder = createSubfolder ? System.IO.Path.Combine(outputDir, "Cortex FX") : outputDir;
        var files = new List<FileModel>(_filesToConvert);
        var filePaths = files.Select(f => f.FullPath).ToList();
        var failedMessages = new List<string>();

        _activeConversionCompletedFiles = 0;
        _activeConversionTotalFiles = files.Count;
        ConversionProgress.Value = 0;
        ConversionProgress.Maximum = files.Count * 100;
        UpdateConversionProgressUi(Loc.T("Loc_Status_Preparing"), 0, files.Count);
        ConsoleLogger.Info("Conversion", $"Starting batch: {_filesToConvert.Count} file(s) -> {targetFormat.ToUpperInvariant()}.");
        SetConversionUiState(true);

        try
        {
            bool isMergeChecked = chkMergePdf.IsChecked == true && chkMergePdf.Visibility == Visibility.Visible;
            bool areAllImages = filePaths.All(f => MediaTypes.RasterImageExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));

            if (filePaths.Count > 1 && areAllImages && targetFormat == "pdf" && isMergeChecked)
            {
                UpdateConversionProgressUi(Loc.T("Loc_Status_Merging"), 0, files.Count);
                ConsoleLogger.Info("Conversion", $"Merging {filePaths.Count} image(s) -> PDF.");

                string outputFolder = _lastOutputFolder ?? outputDir;
                Directory.CreateDirectory(outputFolder);
                string finalPath = System.IO.Path.Combine(outputFolder, $"Merged_Images_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                foreach (var f in files)
                {
                    UpdateFileStatus(f, Loc.T("Loc_Status_FileMerging"), "#F59E0B");
                }

                await _magickService.MergeImagesToPdfAsync(filePaths, finalPath, token);
                foreach (var f in files)
                {
                    UpdateFileStatus(f, Loc.T("Loc_Status_FileMerged"), "#34D399");
                }

                ConversionProgress.Value = ConversionProgress.Maximum;
                UpdateConversionProgressUi(Loc.T("Loc_Status_MergeComplete"), files.Count, files.Count, 100);
                ConsoleLogger.Success("Conversion", $"Merged images -> {ConsoleLogger.ShortPath(finalPath)}.");
                ShowConversionSummary(files.Count, 0, files.Count, Loc.F("Loc_Alert_MergedInto", files.Count, finalPath));
                return;
            }

            int processedFiles = 0;
            int successCount = 0;
            int errorCount = 0;
            long totalOutputBytes = 0;
            GifConversionOptions? gifOptions = targetFormat == "gif" ? BuildCurrentGifOptions() : null;

            foreach (var fileItem in files)
            {
                token.ThrowIfCancellationRequested();

                string file = fileItem.FullPath;
                if (!File.Exists(file))
                {
                    UpdateFileStatus(fileItem, Loc.T("Loc_Status_FileMissing"), "#FF4D5E");
                    errorCount++;
                    failedMessages.Add(Loc.F("Loc_Alert_FileMoved", fileItem.FileName));
                    processedFiles++;
                    ConversionProgress.Value = processedFiles * 100;
                    UpdateConversionProgressUi(Loc.F("Loc_Status_SkippedMissing", fileItem.FileName), processedFiles, files.Count);
                    continue;
                }

                ConsoleLogger.Info("Conversion", $"Converting {ConsoleLogger.ShortPath(file)} -> {targetFormat.ToUpperInvariant()}.");
                UpdateFileStatus(fileItem, Loc.T("Loc_Status_FileProcessing"), "#E11D2E");
                string currentFileMessage = Loc.F("Loc_Status_ConvertingFile", System.IO.Path.GetFileName(file));
                UpdateConversionProgressUi(currentFileMessage, processedFiles, files.Count);

                try
                {
                    var fileProgress = new Progress<double>(p =>
                    {
                        double totalProgress = (processedFiles * 100) + Math.Clamp(p, 0, 100);
                        ConversionProgress.Value = Math.Min(totalProgress, ConversionProgress.Maximum);
                        UpdateConversionProgressUi(currentFileMessage, processedFiles, files.Count, p);
                    });

                    var result = await _conversionRouter.ConvertAsync(new ConversionJob
                    {
                        InputPath = file,
                        OutputDirectory = outputDir,
                        TargetFormat = targetFormat,
                        QualityLevel = qualityLevel,
                        CreateSubfolder = createSubfolder,
                        ImageOptions = imageOptions,
                        GifOptions = gifOptions
                    }, token, fileProgress);

                    if (!result.Success)
                    {
                        if (result.ErrorMessage == CortexFX.Core.Services.Documents.EmptyDocumentException.UserMessage)
                            throw new CortexFX.Core.Services.Documents.EmptyDocumentException();
                        throw new InvalidOperationException(result.ErrorMessage ?? "Conversion failed.");
                    }

                    if (!string.IsNullOrWhiteSpace(result.OutputPath))
                    {
                        string resultFolder = Directory.Exists(result.OutputPath)
                            ? result.OutputPath
                            : (System.IO.Path.GetDirectoryName(result.OutputPath) ?? _lastOutputFolder ?? outputDir);
                        _lastOutputFolder = resultFolder;
                        if (File.Exists(result.OutputPath))
                        {
                            totalOutputBytes += new FileInfo(result.OutputPath).Length;
                        }
                    }

                    UpdateFileStatus(fileItem, Loc.T("Loc_Status_FileDone"), "#34D399");
                    successCount++;
                    ConsoleLogger.Success("Conversion", $"Done {ConsoleLogger.ShortPath(file)}.");
                }
                catch (OperationCanceledException)
                {
                    UpdateFileStatus(fileItem, Loc.T("Loc_Status_FileCancelled"), "#AAAAAA");
                    throw;
                }
                catch (CortexFX.Core.Services.Documents.EmptyDocumentException ex)
                {
                    UpdateFileStatus(fileItem, Loc.T("Loc_Status_FileEmpty"), "#F59E0B");
                    errorCount++;
                    failedMessages.Add(FriendlyErrorMessage(file, targetFormat, ex.Message));
                    ConsoleLogger.Warning("Conversion", $"Empty document skipped: {ConsoleLogger.ShortPath(file)}");
                }
                catch (Exception ex)
                {
                    UpdateFileStatus(fileItem, Loc.T("Loc_Status_FileError"), "#FF4D5E");
                    errorCount++;
                    string friendly = FriendlyErrorMessage(file, targetFormat, ex.Message);
                    failedMessages.Add(friendly);
                    ConsoleLogger.Error("Conversion", $"Failed {ConsoleLogger.ShortPath(file)}: {ex}");
                    UpdateConversionProgressUi(Loc.F("Loc_Status_ErrorFile", System.IO.Path.GetFileName(file)), processedFiles, files.Count);
                }

                processedFiles++;
                ConversionProgress.Value = processedFiles * 100;
                UpdateConversionProgressUi(Loc.F("Loc_Status_ProcessedCount", processedFiles, files.Count), processedFiles, files.Count, 0);
            }

            UpdateConversionProgressUi(Loc.T(errorCount > 0 ? "Loc_Status_FinishedWarnings" : "Loc_Status_ConversionComplete"), files.Count, files.Count, 100);
            ConsoleLogger.Success("Conversion", $"Batch complete: {successCount} succeeded, {errorCount} failed.");

            string sizeLine = totalOutputBytes > 0
                ? $"\n{(successCount == 1 ? Loc.T("Loc_Alert_OutputSize") : Loc.T("Loc_Alert_TotalOutputSize"))}: {FormatBytesReadable(totalOutputBytes)}"
                : string.Empty;
            string? detail = failedMessages.Count > 0
                ? Loc.F("Loc_Alert_ConvPartialDetail", successCount, files.Count, sizeLine, string.Join("\n", failedMessages.Take(3))) +
                  (failedMessages.Count > 3 ? Loc.F("Loc_Alert_MoreErrors", failedMessages.Count - 3) : "")
                : Loc.F("Loc_Alert_ConvSuccessDetail", successCount, files.Count, sizeLine, _lastOutputFolder ?? outputDir);
            ShowConversionSummary(successCount, errorCount, files.Count, detail);
        }
        catch (OperationCanceledException)
        {
            UpdateConversionProgressUi(Loc.T("Loc_Status_ConversionCancelled"), _activeConversionCompletedFiles, files.Count);
            ConsoleLogger.Warning("Conversion", "Batch cancelled by user.");
            ShowConversionSummary(0, files.Count, files.Count, Loc.T("Loc_Alert_CancelledSummary"));
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Conversion", $"Critical error: {ex}");
            AppDialog.ShowError(
                this,
                Loc.T("Loc_Alert_ConvFailedTitle"),
                Loc.F("Loc_Alert_ConvFailedMsg", ConsoleLogger.LogFilePath));
        }
        finally
        {
            SetConversionUiState(false);
        }
    }

    private void UpdateFileStatus(FileModel file, string status, string color)
    {
        file.Status = status;
        file.StatusColor = color;
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateConvertButtonAvailability();
    }

    #endregion


    #region Audio Choice Overlay

    private void SetCategoryRadioChecked(string category)
    {
        if (RadioImage != null) RadioImage.IsChecked = category == "Image";
        if (RadioVideo != null) RadioVideo.IsChecked = category == "Video";
        if (RadioAudio != null) RadioAudio.IsChecked = category == "Audio";
        if (RadioDocument != null) RadioDocument.IsChecked = category == "Document";
        if (RadioArchive != null) RadioArchive.IsChecked = category == "Archive";
        if (RadioEbook != null) RadioEbook.IsChecked = category == "Ebook";
    }

    private void TrimChoice_Click(object sender, RoutedEventArgs e)
    {
        AudioChoiceOverlay.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrEmpty(_pendingAudioFile))
        {
            LoadAudioEditor(_pendingAudioFile);
            _pendingAudioFile = null;
        }
    }

    private void ConvertChoice_Click(object sender, RoutedEventArgs e)
    {
        AudioChoiceOverlay.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrEmpty(_pendingAudioFile))
        {
            AddFileToList(_pendingAudioFile);
            _pendingAudioFile = null;
        }
    }

    private void CancelChoice_Click(object sender, RoutedEventArgs e)
    {
        AudioChoiceOverlay.Visibility = Visibility.Collapsed;
        _pendingAudioFile = null;
    }

    #endregion
}
