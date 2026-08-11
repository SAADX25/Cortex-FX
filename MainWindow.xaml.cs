using System;
using System.Linq;
using System.Text;
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
using CortexFX.Core.Constants;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Dialogs;
using CortexFX.Models;

namespace CortexFX;

/// <summary>
/// Main window code-behind: dashboard, convert flow, and audio cutter.
/// </summary>
public partial class MainWindow : Window
{
    #region Fields

    private readonly IAppConfiguration _config;
    private readonly IConversionRouter _conversionRouter;
    private readonly IProcessManager _processManager;
    private readonly IMagickService _magickService;
    private readonly IResourceValidationService _resourceValidator;
    private ObservableCollection<FileModel> _filesToConvert = new ObservableCollection<FileModel>();
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isConverting;
    private bool _formatWasEnabledBeforeConversion;
    private int _activeConversionTotalFiles;
    private int _activeConversionCompletedFiles;
    private string? _lastOutputFolder;
    private const int MaxBatchFiles = 100;
    // Audio Editor State
    private string? _currentAudioFile;
    private string? _pendingAudioFile;
    private AudioFileReader? _audioReader;
    private WaveOutEvent? _waveOut;
    private LiveSpectrumAnalyzer? _spectrumAnalyzer;
    private readonly float[] _spectrumBands = new float[48];
    private TimeSpan _selectionStart = TimeSpan.Zero;
    private TimeSpan _selectionEnd = TimeSpan.Zero;
    private System.Windows.Threading.DispatcherTimer? _playbackTimer;
    private bool _isDraggingAudioSelection;
    private double _audioSelectionDragStartX;
    private TimeSpan _audioSelectionDragStartTime = TimeSpan.Zero;
    private CancellationTokenSource? _waveformRenderCts;
    private string? _waveformCacheFile;
    private int _waveformCacheBuckets;
    private double[]? _waveformCachePeaks;
    private bool _isSavingAudioSelection;
    private bool _suppressVolumePersist;
    private TrayIconService? _trayIcon;

    // FFME
    private string _ffmpegBinPath = string.Empty;

    #endregion

    #region Construction & Startup

    public MainWindow(
        IAppConfiguration config,
        IConversionRouter conversionRouter,
        IProcessManager processManager,
        IMagickService magickService,
        IResourceValidationService resourceValidator,
        string? startupFile = null)
    {
        _config = config;
        _conversionRouter = conversionRouter;
        _processManager = processManager;
        _magickService = magickService;
        _resourceValidator = resourceValidator;

        try
        {
            InitializeComponent();

            VideoCompressorEditor.Initialize(_config.FFmpegPath);
            VideoCompressorEditor.CloseRequested += (s, e) => SwitchToMode(AppMode.Dashboard);
            VideoCutterEditor.Initialize(_config.FFmpegPath);
            VideoCutterEditor.CloseRequested += (s, e) => SwitchToMode(AppMode.Dashboard);

            FilesList.ItemsSource = _filesToConvert;
            PopulateFormats("Document");

            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v1.5.0";
            ConsoleLogger.Info("UI", $"Version label set to {VersionText.Text}.");

            // File passed from Explorer context menu
            if (!string.IsNullOrEmpty(startupFile) && File.Exists(startupFile))
            {
                ConsoleLogger.Info("Startup", $"Startup file detected: {ConsoleLogger.ShortPath(startupFile)}");
                AddFileToList(startupFile);
                ShowConversionView();
            }

            if (ContextMenuCheckBox != null)
            {
                ContextMenuCheckBox.IsChecked = RegistryManager.IsRegistered();
            }

            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;
            UpdateConvertButtonAvailability();

            _trayIcon = new TrayIconService(this);
            _trayIcon.Attach();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Startup", ex.Message);
            MessageBox.Show($"Startup Error: {ex}", "Cortex FX Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyPersistedAudioVolume();
        StatusText.Text = "Checking local tools...";
        ConsoleLogger.Info("Startup", "Background startup checks started.");

        try
        {
            var resourceStatus = await _resourceValidator.ValidateCoreResourcesAsync();
            if (!resourceStatus.IsReady)
            {
                ConsoleLogger.Warning("Resources", BuildResourceStatusMessage(resourceStatus));
                StatusText.Text = "Some local tools are missing. Open Settings for logs.";
                MessageBox.Show(
                    BuildResourceWarning(resourceStatus),
                    "Resource Status",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                ConsoleLogger.Success("Resources", $"Core tools ready at {ConsoleLogger.ShortPath(resourceStatus.ResourcesDirectory)}.");
                StatusText.Text = "Local tools ready.";
            }

            await LoadFfmeAsync();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Startup", $"Startup checks failed: {ex}");
            StatusText.Text = "Startup checks failed. Open Settings for logs.";
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
            StatusText.Text = "Video preview engine is unavailable. Conversion can still work if ffmpeg.exe is present.";
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

    private static string BuildResourceWarning(ResourceValidationResult status)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Cortex FX could not find every local tool it needs.");
        builder.AppendLine();

        if (!status.ResourcesDirectoryExists)
        {
            builder.AppendLine("Missing folder:");
            builder.AppendLine(status.ResourcesDirectory);
            builder.AppendLine();
        }

        if (status.MissingTools.Count > 0)
        {
            builder.AppendLine("Place these files in the Resources folder:");
            foreach (string tool in status.MissingTools)
            {
                builder.AppendLine($"- {tool}");
            }
            builder.AppendLine();
        }

        if (status.MissingFFmpegDlls.Count > 0)
        {
            builder.AppendLine("Place these FFME DLLs in:");
            builder.AppendLine(status.FFmpegLibsDirectory);
            foreach (string dll in status.MissingFFmpegDlls)
            {
                builder.AppendLine($"- {dll}");
            }
            builder.AppendLine();
        }

        builder.AppendLine("Expected core Resources layout:");
        builder.AppendLine("Resources\\ffmpeg.exe");
        builder.AppendLine("Resources\\magick.exe");
        builder.AppendLine("Resources\\pdftocairo.exe");
        builder.AppendLine("Resources\\ffmpeg_libs\\*.dll");
        return builder.ToString();
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
        CollapseMainContent(DashboardView, activeView);
        CollapseMainContent(ConversionView, activeView);
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

        view.Opacity = 0;
        transform.Y = 10;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(170);

        view.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = easing
        });

        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration)
        {
            EasingFunction = easing
        });
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
            CurrentToolTitle.Text = $"{category} Converter Mode";

            // Pre-populate formats for this category
            PopulateFormats(category);

            // Re-enable dropdown if it was disabled by Universal mode reset
            if (FormatComboBox.Items.Count > 0)
            {
                FormatComboBox.SelectedIndex = 0;
                FormatComboBox.IsEnabled = true;
            }
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
                CurrentToolTitle.Text = "Video Compressor";
                BackBtn.Visibility = Visibility.Visible;
            }
            else if (tool == "VideoCutter")
            {
                if (VideoCutterEditor != null) ShowMainContent(VideoCutterEditor);
                CurrentToolTitle.Text = "Video Cutter";
                BackBtn.Visibility = Visibility.Visible;
            }
            else if (tool == "AudioCutter")
            {
                if (AudioEditorGrid != null) ShowMainContent(AudioEditorGrid);
                CurrentToolTitle.Text = "Audio Cutter";
                BackBtn.Visibility = Visibility.Visible;
            }
        }
        else
        {
            _currentMode = AppMode.Dashboard;
            ShowMainContent(DashboardView);
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;
            CurrentToolTitle.Text = "Select Tool";
            BackBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void SwitchToMode(AppMode mode)
    {
        _currentMode = mode;
        // Reset filter if not switching via Category Click (which sets it before calling this)
        // But we need to be careful not to reset it if we just called it from Category_Click
        // Simple way: Only reset if mode is NOT Universal, or if we want to clear it when going back to dashboard.

        if (mode != AppMode.Universal)
        {
            _universalFilterMode = null;
        }

        if (mode == AppMode.Dashboard)
        {
            ShowMainContent(DashboardView);
            CurrentToolTitle.Text = "Select Tool";
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
                CurrentToolTitle.Text = "Universal Smart Converter";
                FormatComboBox.Items.Clear();
                FormatComboBox.Items.Add(new ComboBoxItem { Content = "Ready for any file..." });
                FormatComboBox.SelectedIndex = 0;
                FormatComboBox.IsEnabled = false; // Disabled until file drop
            }

            // Keep the file list when switching tabs
            // _filesToConvert.Clear();
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
            string from = "Unknown", to = "Unknown";
            string category = "Document";
            string? strictTarget = null;

            switch (mode)
            {
                case AppMode.PdfToWord: from = "PDF"; to = "DOCX"; strictTarget = "DOCX"; break;
                case AppMode.WordToPdf: from = "Word"; to = "PDF"; strictTarget = "PDF"; break;
                case AppMode.PdfToPpt: from = "PDF"; to = "PPTX"; strictTarget = "PPTX"; break;
                case AppMode.PptToPdf: from = "PowerPoint"; to = "PDF"; strictTarget = "PDF"; break;
                case AppMode.ExcelToPdf: from = "Excel"; to = "PDF"; strictTarget = "PDF"; break;
                case AppMode.PdfToImage: from = "PDF"; to = "JPG"; strictTarget = "JPG"; category = "Image"; break;
            }

            CurrentToolTitle.Text = $"{from} to {to}";
            PopulateFormats(category, strictTarget);

            // Auto-select and lock (if standard format)
            if (FormatComboBox.Items.Count > 0)
            {
                FormatComboBox.SelectedIndex = 0;
            }

            FormatComboBox.IsEnabled = false;
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
                    if (VideoCutterEditor != null && !VideoCutterEditor.HasActiveSession)
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
            ShowMainContent(AudioEditorGrid);
            CurrentToolTitle.Text = "Audio Cutter";
            BackBtn.Visibility = Visibility.Visible;
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Select an Audio File",
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
                FormatComboBox.Items.Add(new ComboBoxItem { Content = "Ready for any file..." });
                FormatComboBox.SelectedIndex = 0;
                FormatComboBox.IsEnabled = false;
                CurrentToolTitle.Text = "Universal Smart Converter";
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
            FormatComboBox.Items.Add(new ComboBoxItem { Content = "No common output" });
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
                ? "Universal Smart Converter"
                : $"{category} Converter";
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
            StatusText.Text = $"Batch limit reached ({MaxBatchFiles} files).";
            MessageBox.Show(
                $"Cortex FX can process up to {MaxBatchFiles} files in one batch. Start this batch first, then add more files.",
                "Batch Limit",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowResourceStatus_Click(object sender, RoutedEventArgs e)
    {
        var status = _resourceValidator.ValidateCoreResources();
        string message = status.IsReady
            ? $"All core resources are ready.\n\nResources folder:\n{status.ResourcesDirectory}\n\nFFME DLL folder:\n{status.FFmpegLibsDirectory}"
            : BuildResourceWarning(status);

        MessageBox.Show(message, "Resource Status", MessageBoxButton.OK,
            status.IsReady ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
            MessageBox.Show("The folder is not available yet.", "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show($"Could not open the folder.\n\n{folderPath}", "Open Folder Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show($"Error registering context menu: {ex.Message}", "Registry Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show($"Error unregistering context menu: {ex.Message}", "Registry Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Drag-Drop & File Picking

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropZone.BorderBrush = (Brush)FindResource("AccentColor");
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#473039"));
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#473039"));
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
                        CurrentToolTitle.Text = $"{ext.ToUpper().TrimStart('.')} Converter";
                    }
                    else
                    {
                        MessageBox.Show($"Universal Mode: Format '{ext}' is not supported yet.", "Not Supported", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    MessageBox.Show($"Some files were skipped because they don't match the current tool mode ({_currentMode}).", "Invalid File Type", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            StatusText.Text = "Some folders could not be read. See logs for details.";
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
            Title = "Select Files to Convert",
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
            Title = "Select Output Folder",
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
        StatusText.Text = "Ready";
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

        if (FormatComboBox?.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            string selectedFormat = item.Content.ToString() ?? string.Empty;
            if (!selectedFormat.Contains("Ready", StringComparison.OrdinalIgnoreCase) &&
                !selectedFormat.Contains("No common", StringComparison.OrdinalIgnoreCase))
            {
                outputFormat = selectedFormat.ToUpperInvariant();
            }
        }

        SelectedFilesCountText.Text = fileCount.ToString();
        SummaryModeText.Text = CurrentToolTitle?.Text ?? "-";
        SummaryFilesText.Text = fileCount.ToString();
        SummaryOutputText.Text = outputFormat;
        SummaryEstimateText.Text = fileCount > 0 && outputFormat != "-" ? "Ready" : "Pending";
    }

    private void UpdateConvertButtonAvailability()
    {
        if (ConvertButton == null)
        {
            return;
        }

        UpdateConversionSummary();

        if (_isConverting)
        {
            return;
        }

        bool hasFiles = _filesToConvert.Count > 0;
        bool hasOutput = !string.IsNullOrWhiteSpace(OutputPathBox?.Text);
        bool hasFormat = FormatComboBox?.SelectedItem is ComboBoxItem item &&
                         item.Content != null &&
                         !item.Content.ToString()!.Contains("Ready", StringComparison.OrdinalIgnoreCase) &&
                         !item.Content.ToString()!.Contains("No common", StringComparison.OrdinalIgnoreCase);

        ConvertButton.IsEnabled = hasFiles && hasOutput && hasFormat;
        ConvertButton.Content = "CONVERT";
    }

    private void SetConversionUiState(bool isConverting)
    {
        if (isConverting)
        {
            _formatWasEnabledBeforeConversion = FormatComboBox.IsEnabled;
            ConversionOverlay.Visibility = Visibility.Visible;
            ConversionOverlayCancelButton.IsEnabled = true;
            UpdateConversionProgressUi("Preparing conversion...", 0, _activeConversionTotalFiles);
        }

        _isConverting = isConverting;
        ConvertButton.IsEnabled = true;
        ConvertButton.Content = isConverting ? "CANCEL" : "CONVERT";
        BrowseButton.IsEnabled = !isConverting;
        BackBtn.IsEnabled = !isConverting;
        DropZone.IsHitTestVisible = !isConverting;
        FormatComboBox.IsEnabled = isConverting ? false : _formatWasEnabledBeforeConversion;

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
            ? $"{_activeConversionCompletedFiles}/{_activeConversionTotalFiles} files - {percent:0}%"
            : "Preparing files...";
    }

    private void RequestConversionCancel()
    {
        _cancellationTokenSource?.Cancel();
        ConvertButton.IsEnabled = false;
        ConversionOverlayCancelButton.IsEnabled = false;
        UpdateConversionProgressUi("Cancelling...", _activeConversionCompletedFiles, _activeConversionTotalFiles);
        ConversionOverlayDetailText.Text = "Stopping after the current file...";
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

        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Executable", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}: a required local tool is missing. Open Settings > Resource Status for the expected Resources layout.";
        }

        if (message.Contains("invalid conversion", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid argument", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exit code -22", StringComparison.OrdinalIgnoreCase))
        {
            return $"Conversion failed: {name} could not be converted to {target}. The conversion arguments were invalid for this file.";
        }

        if (message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("magick", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exit code", StringComparison.OrdinalIgnoreCase))
        {
            return $"Conversion failed: {name} could not be converted to {target}. See the log for technical details.";
        }

        if (message.Contains("Microsoft Office is required", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("COM", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("RPC", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}: Microsoft Office could not complete this conversion. Make sure Office is installed, activated, and able to open the file.";
        }

        if (message.Contains("No engine found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            return $"Conversion failed: {name} could not be converted to {target}. This conversion route is not supported yet.";
        }

        if (message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}: conversion was cancelled.";
        }

        return $"Conversion failed: {name} could not be converted to {target}.";
    }

    private void ShowConversionSummary(int successCount, int errorCount, int totalCount, string? details = null)
    {
        bool hadErrors = errorCount > 0;
        SuccessTitleText.Text = hadErrors ? "Finished with warnings" : "All Tasks Finished!";
        SuccessMessageText.Text = details ?? (hadErrors
            ? $"Converted {successCount}/{totalCount} files. {errorCount} failed. See the log for details."
            : $"Successfully converted {successCount}/{totalCount} files.");
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
            MessageBox.Show("Add at least one file before converting.", "No Files", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateConvertButtonAvailability();
            return;
        }

        if (_filesToConvert.Count > MaxBatchFiles)
        {
            MessageBox.Show($"Reduce the batch to {MaxBatchFiles} files or fewer.", "Batch Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (FormatComboBox.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Content == null)
            return;

        string targetFormat = selectedItem.Content.ToString()!.ToLowerInvariant();
        if (targetFormat.Contains("ready", StringComparison.OrdinalIgnoreCase) ||
            targetFormat.Contains("no common", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Choose a valid output format for the selected files.", "Output Format Needed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string outputDir = OutputPathBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Conversion", $"Output folder unavailable: {ex.Message}");
            MessageBox.Show($"Cortex FX could not use this output folder:\n{outputDir}\n\nChoose another folder and try again.", "Output Folder Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        UpdateConversionProgressUi("Preparing conversion...", 0, files.Count);
        ConsoleLogger.Info("Conversion", $"Starting batch: {_filesToConvert.Count} file(s) -> {targetFormat.ToUpperInvariant()}.");
        SetConversionUiState(true);

        try
        {
            bool isMergeChecked = chkMergePdf.IsChecked == true && chkMergePdf.Visibility == Visibility.Visible;
            bool areAllImages = filePaths.All(f => MediaTypes.RasterImageExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));

            if (filePaths.Count > 1 && areAllImages && targetFormat == "pdf" && isMergeChecked)
            {
                UpdateConversionProgressUi("Merging all images...", 0, files.Count);
                ConsoleLogger.Info("Conversion", $"Merging {filePaths.Count} image(s) -> PDF.");

                string outputFolder = _lastOutputFolder ?? outputDir;
                Directory.CreateDirectory(outputFolder);
                string finalPath = System.IO.Path.Combine(outputFolder, $"Merged_Images_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                foreach (var f in files)
                {
                    UpdateFileStatus(f, "Merging...", "#F59E0B");
                }

                await _magickService.MergeImagesToPdfAsync(filePaths, finalPath, token);
                foreach (var f in files)
                {
                    UpdateFileStatus(f, "Merged", "#E11D2E");
                }

                ConversionProgress.Value = ConversionProgress.Maximum;
                UpdateConversionProgressUi("Merge complete.", files.Count, files.Count, 100);
                ConsoleLogger.Success("Conversion", $"Merged images -> {ConsoleLogger.ShortPath(finalPath)}.");
                ShowConversionSummary(files.Count, 0, files.Count, $"Merged {files.Count} images into:\n{finalPath}");
                return;
            }

            int processedFiles = 0;
            int successCount = 0;
            int errorCount = 0;

            foreach (var fileItem in files)
            {
                token.ThrowIfCancellationRequested();

                string file = fileItem.FullPath;
                if (!File.Exists(file))
                {
                    UpdateFileStatus(fileItem, "Missing", "#FF4D5E");
                    errorCount++;
                    failedMessages.Add($"{fileItem.FileName}: file was moved or deleted before conversion.");
                    processedFiles++;
                    ConversionProgress.Value = processedFiles * 100;
                    UpdateConversionProgressUi($"Skipped missing file: {fileItem.FileName}", processedFiles, files.Count);
                    continue;
                }

                ConsoleLogger.Info("Conversion", $"Converting {ConsoleLogger.ShortPath(file)} -> {targetFormat.ToUpperInvariant()}.");
                UpdateFileStatus(fileItem, "Processing...", "#E11D2E");
                string currentFileMessage = $"Converting {System.IO.Path.GetFileName(file)}...";
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
                        ImageOptions = imageOptions
                    }, token, fileProgress);

                    if (!result.Success)
                    {
                        throw new InvalidOperationException(result.ErrorMessage ?? "Conversion failed.");
                    }

                    if (!string.IsNullOrWhiteSpace(result.OutputPath))
                    {
                        string resultFolder = Directory.Exists(result.OutputPath)
                            ? result.OutputPath
                            : (System.IO.Path.GetDirectoryName(result.OutputPath) ?? _lastOutputFolder ?? outputDir);
                        _lastOutputFolder = resultFolder;
                    }

                    UpdateFileStatus(fileItem, "Done", "#E11D2E");
                    successCount++;
                    ConsoleLogger.Success("Conversion", $"Done {ConsoleLogger.ShortPath(file)}.");
                }
                catch (OperationCanceledException)
                {
                    UpdateFileStatus(fileItem, "Cancelled", "#AAAAAA");
                    throw;
                }
                catch (Exception ex)
                {
                    UpdateFileStatus(fileItem, "Error", "#FF4D5E");
                    errorCount++;
                    string friendly = FriendlyErrorMessage(file, targetFormat, ex.Message);
                    failedMessages.Add(friendly);
                    ConsoleLogger.Error("Conversion", $"Failed {ConsoleLogger.ShortPath(file)}: {ex}");
                    UpdateConversionProgressUi($"Error: {System.IO.Path.GetFileName(file)}", processedFiles, files.Count);
                }

                processedFiles++;
                ConversionProgress.Value = processedFiles * 100;
                UpdateConversionProgressUi($"{processedFiles}/{files.Count} files processed.", processedFiles, files.Count, 0);
            }

            UpdateConversionProgressUi(errorCount > 0 ? "Finished with warnings." : "Conversion complete.", files.Count, files.Count, 100);
            ConsoleLogger.Success("Conversion", $"Batch complete: {successCount} succeeded, {errorCount} failed.");

            string? detail = failedMessages.Count > 0
                ? $"Converted {successCount}/{files.Count} files.\n\n{string.Join("\n", failedMessages.Take(3))}" +
                  (failedMessages.Count > 3 ? $"\n...and {failedMessages.Count - 3} more. See logs for details." : "")
                : null;
            ShowConversionSummary(successCount, errorCount, files.Count, detail);
        }
        catch (OperationCanceledException)
        {
            UpdateConversionProgressUi("Conversion cancelled.", _activeConversionCompletedFiles, files.Count);
            ConsoleLogger.Warning("Conversion", "Batch cancelled by user.");
            ShowConversionSummary(0, files.Count, files.Count, "Conversion was cancelled. Partial output files may exist in the output folder.");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Conversion", $"Critical error: {ex}");
            MessageBox.Show(
                "Cortex FX could not complete the batch.\n\n" +
                $"Details were written to:\n{ConsoleLogger.LogFilePath}",
                "Conversion Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
        if (chkMergePdf == null) return;

        if (FormatComboBox.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            string fmt = item.Content.ToString()!.ToUpper();
            if (fmt == "PDF")
            {
                chkMergePdf.Visibility = Visibility.Visible;
            }
            else
            {
                chkMergePdf.Visibility = Visibility.Collapsed;
                chkMergePdf.IsChecked = false;
            }
        }

        UpdateConvertButtonAvailability();
    }

    #endregion

    #region Audio Cutter

    // --- Audio Editor Implementation ---

    private void LoadAudioEditor(string filePath)
    {
        try
        {
            _currentAudioFile = filePath;
            CancelWaveformRender();
            _waveformCacheFile = null;
            _waveformCacheBuckets = 0;
            _waveformCachePeaks = null;

            // Switch UI
            if (_currentMode != AppMode.AudioTrimmer)
            {
                _audioReturnMode = _currentMode;
                _audioReturnFilterMode = _universalFilterMode;
            }

            _currentMode = AppMode.AudioTrimmer;
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;
            BackBtn.Visibility = Visibility.Visible;
            ShowMainContent(AudioEditorGrid);
            CurrentToolTitle.Text = "Audio Cutter";
            AudioEditorFileNameText.Text = System.IO.Path.GetFileName(filePath);

            // Initialize Audio + live spectrum analyzer in the playback chain
            _playbackTimer?.Stop();
            if (_audioReader != null) { _audioReader.Dispose(); }
            if (_waveOut != null) { _waveOut.Dispose(); }
            _spectrumAnalyzer = null;

            _audioReader = new AudioFileReader(filePath);
            _spectrumAnalyzer = new LiveSpectrumAnalyzer(_audioReader, bandCount: _spectrumBands.Length, fftLength: 1024);
            _waveOut = new WaveOutEvent { DesiredLatency = 80 };
            _waveOut.Init(_spectrumAnalyzer);
            ApplyPersistedAudioVolume();
            _spectrumAnalyzer.ResetLevels();
            if (SpectrumStatusText != null)
            {
                SpectrumStatusText.Visibility = Visibility.Visible;
                SpectrumStatusText.Text = "Play selection to see real-time spectrum";
            }

            // Reset Selection
            _selectionStart = TimeSpan.Zero;
            _selectionEnd = _audioReader.TotalTime;
            _isDraggingAudioSelection = false;
            _audioSelectionDragStartTime = TimeSpan.Zero;
            _audioSelectionDragStartX = 0;

            // Wait for layout update to draw waveform correctly
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                DrawWaveform();
                UpdateSelectionVisuals();
                UpdateTimeDisplay();
                UpdatePlaybackCursor();
                ClearSpectrumCanvas();
            }));

            // Setup / refresh playback timer (waveform cursor + live spectrum)
            if (_playbackTimer == null)
            {
                _playbackTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
            }

            _playbackTimer.Tick -= AudioPlaybackTimer_Tick;
            _playbackTimer.Tick += AudioPlaybackTimer_Tick;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading audio: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            CloseAudioEditor(returnToPrevious: true);
        }
    }

    private void AudioPlaybackTimer_Tick(object? sender, EventArgs e)
    {
        UpdatePlaybackCursor();
        UpdateLiveSpectrum();
    }

    private void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
        {
            UpdateLiveSpectrum();
        }
    }

    private void DrawWaveform()
    {
        if (_currentAudioFile == null || _audioReader == null || WaveformContainer.ActualWidth <= 0)
        {
            return;
        }

        double width = WaveformContainer.ActualWidth;
        double height = WaveformContainer.ActualHeight > 0 ? WaveformContainer.ActualHeight : 240;
        int buckets = GetWaveformBucketCount(width);

        if (_waveformCachePeaks != null &&
            _waveformCacheBuckets == buckets &&
            string.Equals(_waveformCacheFile, _currentAudioFile, StringComparison.OrdinalIgnoreCase))
        {
            ApplyWaveformPeaks(_waveformCachePeaks, width, height);
            return;
        }

        CancelWaveformRender();
        ClearWaveformCanvas();

        var cts = new CancellationTokenSource();
        _waveformRenderCts = cts;
        string filePath = _currentAudioFile;
        _ = RenderWaveformAsync(filePath, buckets, width, height, cts);
    }

    private async Task RenderWaveformAsync(string filePath, int buckets, double width, double height, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;

        try
        {
            double[] peaks = await Task.Run(() => BuildWaveformPeaks(filePath, buckets, token), token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested ||
                    !string.Equals(_currentAudioFile, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _waveformCacheFile = filePath;
                _waveformCacheBuckets = buckets;
                _waveformCachePeaks = peaks;
                ApplyWaveformPeaks(peaks, width, height);
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (string.Equals(_currentAudioFile, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    ClearWaveformCanvas();
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        finally
        {
            if (ReferenceEquals(_waveformRenderCts, cts))
            {
                _waveformRenderCts = null;
            }

            cts.Dispose();
        }
    }

    private void ApplyWaveformPeaks(double[] peaks, double width, double height)
    {
        DrawWaveformBars(peaks, width, height);
        UpdateSelectionVisuals();
        UpdatePlaybackCursor();
    }

    private static int GetWaveformBucketCount(double width)
    {
        return Math.Max(96, Math.Min(480, (int)(width / 2.5)));
    }

    private static double[] BuildWaveformPeaks(string filePath, int buckets, CancellationToken token)
    {
        using var waveformReader = new AudioFileReader(filePath);

        double totalSeconds = waveformReader.TotalTime.TotalSeconds;
        if (totalSeconds <= 0 || buckets <= 0)
        {
            return Array.Empty<double>();
        }

        int channels = Math.Max(1, waveformReader.WaveFormat.Channels);
        int sampleRate = Math.Max(1, waveformReader.WaveFormat.SampleRate);
        long totalFrames = Math.Max(1, (long)Math.Ceiling(totalSeconds * sampleRate));
        int bufferFrames = Math.Max(2048, sampleRate / 12);
        int bufferSize = bufferFrames * channels;
        float[] buffer = new float[bufferSize];
        double[] peaks = new double[buckets];
        double[] energy = new double[buckets];
        int[] counts = new int[buckets];
        long frameIndex = 0;
        int samplesRead;

        while ((samplesRead = waveformReader.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();

            int framesRead = samplesRead / channels;
            for (int frame = 0; frame < framesRead; frame++)
            {
                double framePeak = 0;
                int sampleOffset = frame * channels;

                for (int channel = 0; channel < channels; channel++)
                {
                    double sample = Math.Abs(buffer[sampleOffset + channel]);
                    if (sample > framePeak) framePeak = sample;
                }

                int bucket = (int)Math.Min(buckets - 1, (frameIndex * buckets) / totalFrames);
                if (framePeak > peaks[bucket])
                {
                    peaks[bucket] = framePeak;
                }

                energy[bucket] += framePeak * framePeak;
                counts[bucket]++;
                frameIndex++;
            }
        }

        double loudest = 0;
        for (int i = 0; i < peaks.Length; i++)
        {
            double rms = counts[i] > 0 ? Math.Sqrt(energy[i] / counts[i]) : 0;
            peaks[i] = Math.Min(1, (peaks[i] * 0.72) + (rms * 0.46));
            if (peaks[i] > loudest) loudest = peaks[i];
        }

        if (loudest > 0)
        {
            for (int i = 0; i < peaks.Length; i++)
            {
                double normalized = peaks[i] / loudest;
                peaks[i] = Math.Min(1, Math.Pow(normalized, 0.72));
            }
        }

        return peaks;
    }

    private void DrawWaveformBars(IReadOnlyList<double> peaks, double width, double height)
    {
        ClearWaveformCanvas();
        if (peaks.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        double mid = height / 2;
        double step = width / peaks.Count;
        double strokeThickness = Math.Max(1, Math.Min(2.4, step * 0.62));

        for (int i = 0; i < peaks.Count; i++)
        {
            double x = (i * step) + (step / 2);
            double amplitude = Math.Max(2, peaks[i] * mid * 0.9);
            var bar = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = mid - amplitude,
                Y2 = mid + amplitude,
                Stroke = CreateWaveformBrush(peaks[i]),
                StrokeThickness = strokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.96,
                SnapsToDevicePixels = true
            };

            WaveformCanvas.Children.Add(bar);
        }
    }

    private void ClearWaveformCanvas()
    {
        WaveformCanvas?.Children.Clear();
    }

    private void ClearSpectrumCanvas()
    {
        SpectrumCanvas?.Children.Clear();
    }

    private void UpdateLiveSpectrum()
    {
        if (SpectrumCanvas == null || _spectrumAnalyzer == null)
        {
            return;
        }

        bool playing = _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing;
        if (!playing)
        {
            return;
        }

        _spectrumAnalyzer.CopyBandLevels(_spectrumBands);
        DrawSpectrumBars(_spectrumBands);
    }

    private void DrawSpectrumBars(IReadOnlyList<float> bands)
    {
        if (SpectrumCanvas == null)
        {
            return;
        }

        double width = SpectrumHost?.ActualWidth > 1 ? SpectrumHost.ActualWidth : SpectrumCanvas.ActualWidth;
        double height = SpectrumHost?.ActualHeight > 1 ? SpectrumHost.ActualHeight : SpectrumCanvas.ActualHeight;
        if (width <= 1 || height <= 1 || bands.Count == 0)
        {
            return;
        }

        SpectrumCanvas.Width = width;
        SpectrumCanvas.Height = height;
        SpectrumCanvas.Children.Clear();
        if (SpectrumStatusText != null)
        {
            SpectrumStatusText.Visibility = Visibility.Collapsed;
        }

        double gap = 2;
        double barWidth = Math.Max(2, (width - gap * (bands.Count - 1)) / bands.Count);

        for (int i = 0; i < bands.Count; i++)
        {
            double level = Math.Clamp(bands[i], 0, 1);
            // Ease the visual curve a bit so quiet content still moves.
            level = Math.Pow(level, 0.85);
            double barHeight = Math.Max(2, level * height);
            double x = i * (barWidth + gap);
            double y = height - barHeight;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                RadiusX = 2,
                RadiusY = 2,
                Fill = CreateSpectrumBrush(level, i, bands.Count)
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            SpectrumCanvas.Children.Add(rect);
        }
    }

    private static Brush CreateSpectrumBrush(double level, int index, int total)
    {
        double t = total <= 1 ? 0 : (double)index / (total - 1);
        byte r = (byte)(56 + (225 - 56) * t);      // cyan -> coral
        byte g = (byte)(189 - (189 - 70) * t);
        byte b = (byte)(248 - (248 - 90) * t);
        byte a = (byte)(140 + 115 * Math.Clamp(level, 0, 1));
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateWaveformBrush(double peak)
    {
        var brush = new SolidColorBrush(GetWaveformColor(peak));
        brush.Freeze();
        return brush;
    }

    private static Color GetWaveformColor(double peak)
    {
        peak = Math.Max(0, Math.Min(1, peak));

        if (peak < 0.32)
        {
            return LerpColor(Color.FromRgb(0x1E, 0x3A, 0x8A), Color.FromRgb(0x25, 0x63, 0xEB), peak / 0.32);
        }

        if (peak < 0.7)
        {
            return LerpColor(Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x38, 0xBD, 0xF8), (peak - 0.32) / 0.38);
        }

        if (peak < 0.9)
        {
            return LerpColor(Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x7D, 0xD3, 0xFC), (peak - 0.7) / 0.2);
        }

        return LerpColor(Color.FromRgb(0x7D, 0xD3, 0xFC), Color.FromRgb(0xBA, 0xE6, 0xFD), (peak - 0.9) / 0.1);
    }

    private static Color LerpColor(Color from, Color to, double amount)
    {
        amount = Math.Max(0, Math.Min(1, amount));
        byte r = (byte)(from.R + ((to.R - from.R) * amount));
        byte g = (byte)(from.G + ((to.G - from.G) * amount));
        byte b = (byte)(from.B + ((to.B - from.B) * amount));
        return Color.FromRgb(r, g, b);
    }

    private void CancelWaveformRender()
    {
        CancellationTokenSource? cts = _waveformRenderCts;
        _waveformRenderCts = null;
        cts?.Cancel();
    }

    private void UpdatePlaybackCursor()
    {
        if (_audioReader == null || WaveformContainer.ActualWidth <= 0) return;

        double progress = 0;
        if (_audioReader.TotalTime.TotalSeconds > 0)
            progress = _audioReader.CurrentTime.TotalSeconds / _audioReader.TotalTime.TotalSeconds;

        double x = progress * WaveformContainer.ActualWidth;

        if (x < 0) x = 0;
        if (x > WaveformContainer.ActualWidth) x = WaveformContainer.ActualWidth;

        PlaybackCursor.X1 = x;
        PlaybackCursor.X2 = x;
        PlaybackCursor.Y2 = Math.Max(1, WaveformContainer.ActualHeight);
        UpdateCurrentTimeDisplay();

        if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
        {
            if (_selectionEnd > TimeSpan.Zero && _audioReader.CurrentTime >= _selectionEnd)
            {
                _waveOut.Pause();
                _audioReader.CurrentTime = _selectionEnd;
                _playbackTimer?.Stop();
                UpdateCurrentTimeDisplay();
            }
        }
    }

    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_audioReader == null) return;

        Point p = e.GetPosition(WaveformContainer);
        _audioSelectionDragStartX = p.X;
        _audioSelectionDragStartTime = GetAudioTimeFromWaveformPoint(p);
        _isDraggingAudioSelection = false;

        _audioReader.CurrentTime = _audioSelectionDragStartTime;
        UpdatePlaybackCursor();
        WaveformContainer.CaptureMouse();
        e.Handled = true;
    }

    private void Waveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (_audioReader == null || !WaveformContainer.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point p = e.GetPosition(WaveformContainer);
        TimeSpan currentTime = GetAudioTimeFromWaveformPoint(p);

        if (!_isDraggingAudioSelection && Math.Abs(p.X - _audioSelectionDragStartX) > 4)
        {
            _isDraggingAudioSelection = true;
        }

        if (_isDraggingAudioSelection)
        {
            _selectionStart = MinTime(_audioSelectionDragStartTime, currentTime);
            _selectionEnd = MaxTime(_audioSelectionDragStartTime, currentTime);
            NormalizeSelection();
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
        }

        UpdateCurrentTimeDisplay(currentTime);
        e.Handled = true;
    }

    private void Waveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_audioReader == null) return;

        if (WaveformContainer.IsMouseCaptured)
        {
            WaveformContainer.ReleaseMouseCapture();
        }

        Point p = e.GetPosition(WaveformContainer);
        TimeSpan currentTime = GetAudioTimeFromWaveformPoint(p);

        if (_isDraggingAudioSelection)
        {
            _selectionStart = MinTime(_audioSelectionDragStartTime, currentTime);
            _selectionEnd = MaxTime(_audioSelectionDragStartTime, currentTime);
            NormalizeSelection();
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
            _audioReader.CurrentTime = _selectionStart;
            UpdatePlaybackCursor();
        }
        else
        {
            _audioReader.CurrentTime = currentTime;
            UpdatePlaybackCursor();
        }

        _isDraggingAudioSelection = false;
        e.Handled = true;
    }

    private void WaveformContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawWaveform();
        UpdateSelectionVisuals();
        UpdatePlaybackCursor();
    }

    private TimeSpan GetAudioTimeFromWaveformPoint(Point point)
    {
        if (_audioReader == null || WaveformContainer.ActualWidth <= 0)
        {
            return TimeSpan.Zero;
        }

        double progress = point.X / WaveformContainer.ActualWidth;
        progress = Math.Max(0, Math.Min(1, progress));
        return TimeSpan.FromSeconds(_audioReader.TotalTime.TotalSeconds * progress);
    }

    private TimeSpan ClampAudioTime(TimeSpan value)
    {
        if (_audioReader == null) return TimeSpan.Zero;

        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        if (value > _audioReader.TotalTime) return _audioReader.TotalTime;
        return value;
    }

    private static TimeSpan MinTime(TimeSpan first, TimeSpan second) => first <= second ? first : second;

    private static TimeSpan MaxTime(TimeSpan first, TimeSpan second) => first >= second ? first : second;

    private static string FormatAudioTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"hh\:mm\:ss\.fff")
            : time.ToString(@"mm\:ss\.fff");
    }

    private void NormalizeSelection()
    {
        if (_audioReader == null) return;

        _selectionStart = ClampAudioTime(_selectionStart);
        _selectionEnd = ClampAudioTime(_selectionEnd);

        if (_selectionEnd < _selectionStart)
        {
            (_selectionStart, _selectionEnd) = (_selectionEnd, _selectionStart);
        }
    }

    private void UpdateCurrentTimeDisplay(TimeSpan? previewTime = null)
    {
        if (CurrentTimeDisplay == null) return;

        CurrentTimeDisplay.Text = _audioReader == null && previewTime == null
            ? "00:00.000"
            : FormatAudioTime(previewTime.HasValue ? ClampAudioTime(previewTime.Value) : ClampAudioTime(_audioReader!.CurrentTime));
    }

    private void UpdateTimeDisplay()
    {
        NormalizeSelection();

        TimeSpan duration = _selectionEnd - _selectionStart;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        StartTimeDisplay.Text = FormatAudioTime(_selectionStart);
        EndTimeDisplay.Text = FormatAudioTime(_selectionEnd);
        DurationTimeDisplay.Text = FormatAudioTime(duration);
        DurationTimeDisplay.Foreground = duration.TotalMilliseconds > 0
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7DD3FC"))
            : Brushes.Gray;
        UpdateCurrentTimeDisplay();
    }

    private void SetStart_Click(object sender, RoutedEventArgs e)
    {
        if (_audioReader != null)
        {
            _selectionStart = ClampAudioTime(_audioReader.CurrentTime);
            if (_selectionStart > _selectionEnd) _selectionEnd = _audioReader.TotalTime;
            NormalizeSelection();
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
        }
    }

    private void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_audioReader != null)
        {
            _selectionEnd = ClampAudioTime(_audioReader.CurrentTime);
            if (_selectionEnd < _selectionStart) _selectionStart = TimeSpan.Zero;
            NormalizeSelection();
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
        }
    }

    private void UpdateSelectionVisuals()
    {
        if (_audioReader == null || WaveformContainer.ActualWidth <= 0) return;

        NormalizeSelection();

        double totalSeconds = _audioReader.TotalTime.TotalSeconds;
        if (totalSeconds <= 0) return;

        double totalWidth = WaveformContainer.ActualWidth;
        double startX = (_selectionStart.TotalSeconds / totalSeconds) * totalWidth;
        double endX = (_selectionEnd.TotalSeconds / totalSeconds) * totalWidth;

        startX = Math.Max(0, Math.Min(totalWidth, startX));
        endX = Math.Max(0, Math.Min(totalWidth, endX));

        DimLeft.Width = startX;

        double rightWidth = totalWidth - endX;
        if (rightWidth < 0) rightWidth = 0;
        DimRight.Width = rightWidth;
        DimRight.Margin = new Thickness(endX, 0, 0, 0);

        SelectionBand.Margin = new Thickness(startX, 0, 0, 0);
        SelectionBand.Width = Math.Max(0, endX - startX);

        StartMarker.Margin = new Thickness(startX, 0, 0, 0);
        EndMarker.Margin = new Thickness(endX, 0, 0, 0);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double volume = Math.Clamp(e.NewValue, 0, 2);
        if (VolumePercentText != null)
            VolumePercentText.Text = $"{(int)Math.Round(volume * 100)}%";

        if (_audioReader != null)
            _audioReader.Volume = (float)volume;

        if (_suppressVolumePersist || !IsLoaded)
            return;

        UserPreferences.Current.AudioVolume = volume;
        UserPreferences.Current.Save();
    }

    private void ApplyPersistedAudioVolume()
    {
        double volume = Math.Clamp(UserPreferences.Current.AudioVolume, 0, 2);
        _suppressVolumePersist = true;
        try
        {
            if (VolumeSlider != null)
                VolumeSlider.Value = volume;
            if (_audioReader != null)
                _audioReader.Volume = (float)volume;
            if (VolumePercentText != null)
                VolumePercentText.Text = $"{(int)Math.Round(volume * 100)}%";
        }
        finally
        {
            _suppressVolumePersist = false;
        }
    }

    private static double GetExportVolumeGain()
    {
        return Math.Clamp(UserPreferences.Current.AudioVolume, 0.01, 2.0);
    }

    private static bool FormatSupportsEmbeddedCover(string extension) =>
        extension is ".mp3" or ".m4a" or ".aac" or ".flac" or ".ogg" or ".opus" or ".wma";

    private static string BuildAudioExportArgs(string inputFile, string outputFile, string start, string duration, double volumeGain)
    {
        string outExt = System.IO.Path.GetExtension(outputFile).ToLowerInvariant();
        string inExt = System.IO.Path.GetExtension(inputFile).ToLowerInvariant();
        bool volumeChanged = Math.Abs(volumeGain - 1.0) > 0.001;
        bool sameContainer = string.Equals(outExt, inExt, StringComparison.OrdinalIgnoreCase);
        bool keepCover = FormatSupportsEmbeddedCover(outExt);
        string id3 = outExt == ".mp3" ? "-id3v2_version 3 " : string.Empty;

        // Fast path: trim only — copy audio + embedded album art (do NOT use -vn).
        if (!volumeChanged && sameContainer)
        {
            if (keepCover)
            {
                return $"-ss {start} -i \"{inputFile}\" -t {duration} " +
                       $"-map_metadata 0 -map 0 -c copy {id3}-y \"{outputFile}\"";
            }

            return $"-ss {start} -i \"{inputFile}\" -t {duration} -map 0:a:0 -c copy -y \"{outputFile}\"";
        }

        string codecArgs = outExt switch
        {
            ".mp3" => "-c:a libmp3lame -q:a 2",
            ".wav" => "-c:a pcm_s16le",
            ".flac" => "-c:a flac",
            ".ogg" => "-c:a libvorbis -q:a 5",
            ".m4a" or ".aac" => "-c:a aac -b:a 192k",
            ".wma" => "-c:a wmav2 -b:a 192k",
            ".opus" => "-c:a libopus -b:a 128k",
            _ => "-c:a libmp3lame -q:a 2"
        };

        string volumeFilter = volumeChanged
            ? $"-af \"volume={volumeGain.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" "
            : string.Empty;

        // Keep album art thumbnail from the source when the output format supports it.
        string coverArgs = keepCover
            ? "-map_metadata 0 -map 0:a:0 -map 0:v? -c:v copy -disposition:v:0 attached_pic "
            : "-map_metadata 0 -map 0:a:0 ";

        return $"-ss {start} -i \"{inputFile}\" -t {duration} {coverArgs}{volumeFilter}{codecArgs} {id3}-y \"{outputFile}\"";
    }

    private void PlaySelection_Click(object sender, RoutedEventArgs e)
    {
        if (_waveOut != null && _audioReader != null)
        {
            try
            {
                if (_waveOut.PlaybackState == PlaybackState.Playing)
                {
                    _waveOut.Pause();
                    _playbackTimer?.Stop();
                }

                NormalizeSelection();
                if ((_selectionEnd - _selectionStart).TotalMilliseconds <= 0)
                {
                    MessageBox.Show("Select a valid audio range first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _audioReader.CurrentTime = _selectionStart;
                UpdatePlaybackCursor();
                _spectrumAnalyzer?.ResetLevels();
                if (SpectrumStatusText != null)
                {
                    SpectrumStatusText.Visibility = Visibility.Collapsed;
                }

                _waveOut.Play();
                _playbackTimer?.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not play audio:\n{ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void StopSelection_Click(object sender, RoutedEventArgs e)
    {
        StopAudioPlayback(resetToSelectionStart: true);
    }

    private void StopAudioPlayback(bool resetToSelectionStart)
    {
        if (_waveOut != null && _audioReader != null)
        {
            try
            {
                if (_waveOut.PlaybackState == PlaybackState.Playing)
                {
                    _waveOut.Pause();
                }

                _playbackTimer?.Stop();
                _spectrumAnalyzer?.ResetLevels();
                ClearSpectrumCanvas();
                if (SpectrumStatusText != null)
                {
                    SpectrumStatusText.Visibility = Visibility.Visible;
                    SpectrumStatusText.Text = "Play selection to see real-time spectrum";
                }

                if (resetToSelectionStart)
                {
                    _audioReader.CurrentTime = ClampAudioTime(_selectionStart);
                }

                UpdatePlaybackCursor();
                UpdateCurrentTimeDisplay();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not stop audio:\n{ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void SaveSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_isSavingAudioSelection)
        {
            return;
        }

        if (_currentAudioFile == null || _audioReader == null) return;

        NormalizeSelection();
        TimeSpan selectedDuration = _selectionEnd - _selectionStart;
        if (selectedDuration.TotalMilliseconds <= 0)
        {
            MessageBox.Show("Select a valid audio range before saving.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Pause first so FFmpeg isn't fighting the player
        if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
        {
            StopAudioPlayback(resetToSelectionStart: false);
        }

        string dir = System.IO.Path.GetDirectoryName(_currentAudioFile) ?? "";
        string name = System.IO.Path.GetFileNameWithoutExtension(_currentAudioFile);
        string ext = System.IO.Path.GetExtension(_currentAudioFile);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".wav";
        }

        // Let the user pick format + path
        string preferredExt = string.IsNullOrWhiteSpace(ext) ? ".mp3" : ext.ToLowerInvariant();
        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Trimmed Audio",
            FileName = $"{name}_Trimmed{preferredExt}",
            DefaultExt = preferredExt,
            Filter =
                "MP3 (*.mp3)|*.mp3|" +
                "WAV (*.wav)|*.wav|" +
                "M4A / AAC (*.m4a)|*.m4a|" +
                "FLAC (*.flac)|*.flac|" +
                "OGG (*.ogg)|*.ogg|" +
                "OPUS (*.opus)|*.opus|" +
                "WMA (*.wma)|*.wma|" +
                "All Files (*.*)|*.*",
            InitialDirectory = dir
        };

        // Match filter to the source extension when we can
        saveDialog.FilterIndex = preferredExt switch
        {
            ".mp3" => 1,
            ".wav" => 2,
            ".m4a" or ".aac" => 3,
            ".flac" => 4,
            ".ogg" => 5,
            ".opus" => 6,
            ".wma" => 7,
            _ => 1
        };

        if (saveDialog.ShowDialog() == true)
        {
            string outputFile = saveDialog.FileName;

            if (string.Equals(outputFile, _currentAudioFile, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Cannot overwrite the source file while it is open. Please choose a different name.", "File Locked", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(_config.FFmpegPath))
            {
                MessageBox.Show($"FFmpeg not found.\nExpected location:\n{_config.FFmpegPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Bake preferred volume into the export (all formats)
            string start = _selectionStart.ToString(@"hh\:mm\:ss\.fff");
            string duration = selectedDuration.ToString(@"hh\:mm\:ss\.fff");
            double volumeGain = GetExportVolumeGain();
            string args = BuildAudioExportArgs(_currentAudioFile, outputFile, start, duration, volumeGain);

            try
            {
                _isSavingAudioSelection = true;
                SaveSelectionBtn.IsEnabled = false;
                SaveSelectionBtn.Content = "Saving...";

                await _processManager.RunAsync(_config.FFmpegPath, args);

                _lastOutputFolder = System.IO.Path.GetDirectoryName(outputFile);
                SaveSelectionBtn.Content = "Save Cut";

                int percent = (int)Math.Round(volumeGain * 100);
                string volumeNote = Math.Abs(volumeGain - 1.0) > 0.001
                    ? $"Volume preference ({percent}%) was baked into the file."
                    : "Your trimmed audio is ready.";

                var success = new ModernSuccessDialog(
                    "Export complete",
                    volumeNote,
                    outputFile)
                {
                    Owner = this
                };
                success.ShowDialog();
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Audio save was cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isSavingAudioSelection = false;
                SaveSelectionBtn.IsEnabled = true;
                SaveSelectionBtn.Content = "Save Cut";
            }
        }
    }

    private void CloseAudioEditor_Click(object sender, RoutedEventArgs e)
    {
        CloseAudioEditor(returnToPrevious: true);
    }

    private void CloseAudioEditor(bool returnToPrevious)
    {
        CancelWaveformRender();

        if (_waveOut != null)
        {
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }
        _spectrumAnalyzer = null;
        if (_audioReader != null)
        {
            _audioReader.Dispose();
            _audioReader = null;
        }
        if (_playbackTimer != null)
        {
            _playbackTimer.Stop();
        }
        if (WaveformContainer != null && WaveformContainer.IsMouseCaptured)
        {
            WaveformContainer.ReleaseMouseCapture();
        }

        AudioEditorGrid.Visibility = Visibility.Collapsed;
        DropZone.Visibility = Visibility.Visible;
        FilesList.Visibility = Visibility.Visible;

        _currentAudioFile = null;
        _isDraggingAudioSelection = false;
        _waveformCacheFile = null;
        _waveformCacheBuckets = 0;
        _waveformCachePeaks = null;
        ClearWaveformCanvas();
        ClearSpectrumCanvas();
        SelectionBand.Width = 0;

        if (returnToPrevious)
        {
            RestoreAudioReturnView();
        }
    }

    private void RestoreAudioReturnView()
    {
        AppMode returnMode = _audioReturnMode is AppMode.AudioTrimmer or AppMode.Unknown
            ? AppMode.Universal
            : _audioReturnMode;

        if (returnMode == AppMode.Universal)
        {
            _universalFilterMode = _audioReturnFilterMode;
            SwitchToMode(AppMode.Universal);

            if (!string.IsNullOrWhiteSpace(_universalFilterMode))
            {
                SetCategoryRadioChecked(_universalFilterMode);
                CurrentToolTitle.Text = $"{_universalFilterMode} Converter Mode";
                PopulateFormats(_universalFilterMode);

                if (FormatComboBox.Items.Count > 0)
                {
                    FormatComboBox.SelectedIndex = 0;
                    FormatComboBox.IsEnabled = true;
                }
            }

            return;
        }

        SwitchToMode(returnMode);
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
