using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

using CortexFX.Core.Configuration;
using CortexFX.Core.Constants;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services;
using CortexFX.Models;

namespace CortexFX;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly IAppConfiguration _config;
    private readonly IConversionRouter _conversionRouter;
    private readonly IProcessManager _processManager;
    private readonly IMagickService _magickService;
    private readonly IResourceValidationService _resourceValidator;
    private ObservableCollection<FileModel> _filesToConvert = new ObservableCollection<FileModel>();
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isConverting;
    private bool _formatWasEnabledBeforeConversion;
    private string? _lastOutputFolder;
    private const int MaxBatchFiles = 100;
    // Audio Editor State
    private string? _currentAudioFile;
    private string? _pendingAudioFile;
    private AudioFileReader? _audioReader;
    private WaveOutEvent? _waveOut;
    private TimeSpan _selectionStart = TimeSpan.Zero;
    private TimeSpan _selectionEnd = TimeSpan.Zero;
    private System.Windows.Threading.DispatcherTimer? _playbackTimer;

    // FFME
    private string _ffmpegBinPath = string.Empty;
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

            // Initialize the Video Compressor view
            VideoCompressorEditor.Initialize(_config.FFmpegPath);
            VideoCompressorEditor.CloseRequested += (s, e) => UpdateUIMode(false);

            FilesList.ItemsSource = _filesToConvert;
            // Trigger initial population
            PopulateFormats("Document");

            // Set Version
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v1.0.0";
            ConsoleLogger.Info("UI", $"Version label set to {VersionText.Text}.");

            // Handle context menu startup file
            if (!string.IsNullOrEmpty(startupFile) && File.Exists(startupFile))
            {
                ConsoleLogger.Info("Startup", $"Startup file detected: {ConsoleLogger.ShortPath(startupFile)}");
                AddFileToList(startupFile);
                ShowConversionView();
            }

            // Check registry state for checkbox
            if (ContextMenuCheckBox != null)
            {
                ContextMenuCheckBox.IsChecked = RegistryManager.IsRegistered();
            }

            // Explicitly hide TopNav on startup
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;
            UpdateConvertButtonAvailability();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Startup", ex.Message);
            MessageBox.Show($"Startup Error: {ex}", "Cortex FX Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            // Ensure the app shuts down cleanly if startup fails
            Application.Current.Shutdown();
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
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

    private void ShowConversionView(string? toolTag = null)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        ConversionView.Visibility = Visibility.Visible;
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
        Unknown
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> _conversionRules => MediaTypes.ConversionRules;

    private AppMode _currentMode = AppMode.Dashboard;

    private string? _universalFilterMode = null; // null = All, or "Video", "Audio", "Image", "Document", "Archive", "Ebook"

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
            if (DashboardView != null) DashboardView.Visibility = Visibility.Collapsed;
            if (ConversionView != null) ConversionView.Visibility = Visibility.Collapsed;
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;

            if (VideoCompressorEditor != null) VideoCompressorEditor.Visibility = Visibility.Collapsed;

            // Show the requested tool
            if (tool == "VideoCompressor")
            {
                if (VideoCompressorEditor != null) VideoCompressorEditor.Visibility = Visibility.Visible;
                CurrentToolTitle.Text = "Video Compressor";
            }
        }
        else
        {
            if (VideoCompressorEditor != null) VideoCompressorEditor.Visibility = Visibility.Collapsed;
            if (DashboardView != null) DashboardView.Visibility = Visibility.Visible;
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;
            CurrentToolTitle.Text = "Select Tool";
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
            DashboardView.Visibility = Visibility.Visible;
            ConversionView.Visibility = Visibility.Collapsed;
            if (VideoCompressorEditor != null) VideoCompressorEditor.Visibility = Visibility.Collapsed;
            CurrentToolTitle.Text = "Select Tool";
            FormatComboBox.IsEnabled = true;

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
            DashboardView.Visibility = Visibility.Collapsed;
            ConversionView.Visibility = Visibility.Visible;
            if (VideoCompressorEditor != null) VideoCompressorEditor.Visibility = Visibility.Collapsed;

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

            // _filesToConvert.Clear(); // PERSISTENCE FIX: Do not clear files when switching tabs
        }
        else if (mode == AppMode.VideoCompressor)
        {
            UpdateUIMode(true, "VideoCompressor");
        }
        else
        {
            DashboardView.Visibility = Visibility.Collapsed;
            ConversionView.Visibility = Visibility.Visible;

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

    private void ToolCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is AppMode mode)
        {
            SwitchToMode(mode);
        }
        // Fallback for XAML Tag strings if binding failed or old XAML
        else if (sender is FrameworkElement el && el.Tag is string tagStr)
        {
            ConfigureTool(tagStr);
        }
    }

    private void ConfigureTool(string tag)
    {
        // Legacy bridge: Map tag to mode and call SwitchToMode
        var mode = tag switch
        {
            "PDF_DOCX" => AppMode.PdfToWord,
            "DOCX_PDF" => AppMode.WordToPdf,
            "PDF_PPTX" => AppMode.PdfToPpt,
            "PPTX_PDF" => AppMode.PptToPdf,
            "XLSX_PDF" => AppMode.ExcelToPdf,
            "PDF_JPG" => AppMode.PdfToImage,
            "VIDEO_COMPRESSOR" => AppMode.VideoCompressor,
            "MORE_TOOLS" => AppMode.Universal, // Changed from AdvancedGallery to Universal
            _ => AppMode.Unknown
        };

        if (mode != AppMode.Unknown)
        {
            SwitchToMode(mode);
        }
    }

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
        SwitchToMode(AppMode.Dashboard);
        _filesToConvert.Clear();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
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
                FullPath = file
            });

            RefreshFormatsFromSelectedFiles();
            UpdateConvertButtonAvailability();
        }
    }

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
        }
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
                FileName = folderPath,
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
        DropZone.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#444444"));
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#444444"));
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? items = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (items != null)
            {
                // 1. Single Audio File Detection (Audio Editor Mode)
                if (items.Length == 1 && File.Exists(items[0]))
                {
                    string ext = System.IO.Path.GetExtension(items[0]).ToLower();
                    if (MediaTypes.AudioEditorExtensions.Contains(ext))
                    {
                        // NEW LOGIC: Show Choice Overlay
                        _pendingAudioFile = items[0];
                        AudioChoiceOverlay.Visibility = Visibility.Visible;
                        return; // Stop normal processing
                    }
                }

                bool invalidFound = false;

                // Universal Mode Logic
                if (_currentMode == AppMode.Universal && items.Any(File.Exists))
                {
                    // Take the first file's extension to determine capabilities
                    string firstFile = items.First(File.Exists);
                    string ext = System.IO.Path.GetExtension(firstFile).ToLower();

                    if (_conversionRules.ContainsKey(ext))
                    {
                        // Found supported type!
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
        var dialog = new OpenFileDialog
        {
            Title = "Select Files to Convert",
            Multiselect = true,
            Filter = GetFilterForCurrentMode()
        };

        if (dialog.ShowDialog() == true)
        {
            string[] selectedFiles = dialog.FileNames;

            // Check if Single Audio File -> Show Overlay
            if (selectedFiles.Length == 1)
            {
                string ext = System.IO.Path.GetExtension(selectedFiles[0]).ToLower();
                if (MediaTypes.AudioEditorExtensions.Contains(ext))
                {
                    _pendingAudioFile = selectedFiles[0]; // Store for later
                    AudioChoiceOverlay.Visibility = Visibility.Visible; // Show the Choice Screen
                    return; // STOP here, don't load into list yet
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

    private void PopulateFormats(string category, string? strictTarget = null)
    {
        if (FormatComboBox == null) return;

        // 1. Clear items first to avoid duplication
        FormatComboBox.Items.Clear();

        // If strict target is provided (Standard Mode), only add that and return
        if (strictTarget != null)
        {
            FormatComboBox.Items.Add(new ComboBoxItem { Content = strictTarget.ToUpper() });
            FormatComboBox.SelectedIndex = 0;
            return;
        }

        // Use a set to track added formats and prevent duplicates
        HashSet<string> addedFormats = new HashSet<string>();

        // Helper to add unique items
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
            // 2. Distinct Logic: Check Input Types
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

        // 3. Default Selection
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
        ConversionProgress.Value = 0;
        _filesToConvert.Clear();
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

    private void UpdateConvertButtonAvailability()
    {
        if (ConvertButton == null || _isConverting)
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
        }

        _isConverting = isConverting;
        ConvertButton.IsEnabled = true;
        ConvertButton.Content = isConverting ? "CANCEL" : "CONVERT";
        BrowseButton.IsEnabled = !isConverting;
        BackBtn.IsEnabled = !isConverting;
        DropZone.IsEnabled = !isConverting;
        FormatComboBox.IsEnabled = isConverting ? false : _formatWasEnabledBeforeConversion;

        if (!isConverting)
        {
            UpdateConvertButtonAvailability();
        }
    }

    private static string FriendlyErrorMessage(string filePath, string message)
    {
        string name = System.IO.Path.GetFileName(filePath);

        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Executable", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}: a required local tool is missing. Open Settings > Resource Status for the expected Resources layout.";
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
            return $"{name}: this conversion route is not supported yet.";
        }

        if (message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}: conversion was cancelled.";
        }

        return $"{name}: {message}";
    }

    private void ShowConversionSummary(int successCount, int errorCount, int totalCount, string? details = null)
    {
        bool hadErrors = errorCount > 0;
        SuccessTitleText.Text = hadErrors ? "Finished with warnings" : "All Tasks Finished!";
        SuccessMessageText.Text = details ?? (hadErrors
            ? $"Converted {successCount}/{totalCount} files. {errorCount} failed. See the log for details."
            : $"Successfully converted {successCount}/{totalCount} files.");
        SuccessOverlay.Visibility = Visibility.Visible;
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            _cancellationTokenSource?.Cancel();
            StatusText.Text = "Cancelling...";
            ConvertButton.IsEnabled = false;
            return;
        }

        // 1. Validate Output Path
        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            OutputPathBox.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF5555"));
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

        ConversionProgress.Value = 0;
        ConversionProgress.Maximum = files.Count * 100;
        StatusText.Text = "Converting...";
        ConsoleLogger.Info("Conversion", $"Starting batch: {_filesToConvert.Count} file(s) -> {targetFormat.ToUpperInvariant()}.");
        SetConversionUiState(true);

        try
        {
            bool isMergeChecked = chkMergePdf.IsChecked == true && chkMergePdf.Visibility == Visibility.Visible;
            bool areAllImages = filePaths.All(f => MediaTypes.RasterImageExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));

            if (filePaths.Count > 1 && areAllImages && targetFormat == "pdf" && isMergeChecked)
            {
                StatusText.Text = "Merging all images...";
                ConsoleLogger.Info("Conversion", $"Merging {filePaths.Count} image(s) -> PDF.");

                string outputFolder = _lastOutputFolder ?? outputDir;
                Directory.CreateDirectory(outputFolder);
                string finalPath = System.IO.Path.Combine(outputFolder, $"Merged_Images_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                foreach (var f in files)
                {
                    UpdateFileStatus(f, "Merging...", "#FF8C00");
                }

                await _magickService.MergeImagesToPdfAsync(filePaths, finalPath, token);
                foreach (var f in files)
                {
                    UpdateFileStatus(f, "Merged", "#4CAF50");
                }

                ConversionProgress.Value = ConversionProgress.Maximum;
                StatusText.Text = "Merge complete.";
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
                    UpdateFileStatus(fileItem, "Missing", "#FF5555");
                    errorCount++;
                    failedMessages.Add($"{fileItem.FileName}: file was moved or deleted before conversion.");
                    processedFiles++;
                    ConversionProgress.Value = processedFiles * 100;
                    continue;
                }

                ConsoleLogger.Info("Conversion", $"Converting {ConsoleLogger.ShortPath(file)} -> {targetFormat.ToUpperInvariant()}.");
                UpdateFileStatus(fileItem, "Processing...", "#007ACC");
                StatusText.Text = $"Converting {System.IO.Path.GetFileName(file)}...";

                try
                {
                    var fileProgress = new Progress<double>(p =>
                    {
                        double totalProgress = (processedFiles * 100) + Math.Clamp(p, 0, 100);
                        ConversionProgress.Value = Math.Min(totalProgress, ConversionProgress.Maximum);
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

                    UpdateFileStatus(fileItem, "Done", "#4CAF50");
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
                    UpdateFileStatus(fileItem, "Error", "#FF5555");
                    errorCount++;
                    string friendly = FriendlyErrorMessage(file, ex.Message);
                    failedMessages.Add(friendly);
                    ConsoleLogger.Error("Conversion", $"Failed {ConsoleLogger.ShortPath(file)}: {ex}");
                    StatusText.Text = $"Error: {System.IO.Path.GetFileName(file)}";
                }

                processedFiles++;
                ConversionProgress.Value = processedFiles * 100;
            }

            StatusText.Text = errorCount > 0 ? "Finished with warnings." : "Conversion complete.";
            ConsoleLogger.Success("Conversion", $"Batch complete: {successCount} succeeded, {errorCount} failed.");

            string? detail = failedMessages.Count > 0
                ? $"Converted {successCount}/{files.Count} files.\n\n{string.Join("\n", failedMessages.Take(3))}" +
                  (failedMessages.Count > 3 ? $"\n...and {failedMessages.Count - 3} more. See logs for details." : "")
                : null;
            ShowConversionSummary(successCount, errorCount, files.Count, detail);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Conversion cancelled.";
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

    // --- Audio Editor Implementation ---

    private void LoadAudioEditor(string filePath)
    {
        try
        {
            _currentAudioFile = filePath;

            // Switch UI
            DropZone.Visibility = Visibility.Collapsed;
            FilesList.Visibility = Visibility.Collapsed;
            AudioEditorGrid.Visibility = Visibility.Visible;
            CurrentToolTitle.Text = $"Audio Editor - {System.IO.Path.GetFileName(filePath)}";

            // Initialize Audio
            if (_audioReader != null) { _audioReader.Dispose(); }
            if (_waveOut != null) { _waveOut.Dispose(); }

            _audioReader = new AudioFileReader(filePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioReader);

            // Reset Volume Slider
            if (VolumeSlider != null)
            {
                VolumeSlider.Value = 1;
            }

            // Wait for layout update to draw waveform correctly
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                DrawWaveform();
                UpdateSelectionVisuals();
                UpdateTimeDisplay();
            }));

            // Reset Selection
            _selectionStart = TimeSpan.Zero;
            _selectionEnd = _audioReader.TotalTime;

            // Setup Timer
            if (_playbackTimer == null)
            {
                _playbackTimer = new System.Windows.Threading.DispatcherTimer();
                _playbackTimer.Interval = TimeSpan.FromMilliseconds(30);
                _playbackTimer.Tick += (s, e) => UpdatePlaybackCursor();
            }
            _playbackTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading audio: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            CloseAudioEditor();
        }
    }

    private void DrawWaveform()
    {
        if (_audioReader == null || WaveformContainer.ActualWidth <= 0) return;

        WaveformLine.Points.Clear();

        double width = WaveformContainer.ActualWidth;
        double height = WaveformContainer.ActualHeight > 0 ? WaveformContainer.ActualHeight : 100; // Fallback
        double mid = height / 2;

        var points = new PointCollection();
        Random r = new Random();

        points.Add(new Point(0, mid));

        int steps = (int)width / 2;
        for (int i = 0; i <= steps; i++)
        {
            double x = i * 2;
            double amplitude = (r.NextDouble() * 0.8 + 0.1) * (mid * 0.9);
            points.Add(new Point(x, mid - amplitude));
            points.Add(new Point(x, mid + amplitude));
        }

        points.Add(new Point(width, mid));
        WaveformLine.Points = points;
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

        if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
        {
            if (_selectionEnd > TimeSpan.Zero && _audioReader.CurrentTime >= _selectionEnd)
            {
                _waveOut.Pause();
                _audioReader.CurrentTime = _selectionEnd;
            }
        }
    }

    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_audioReader == null) return;

        Point p = e.GetPosition(WaveformContainer);
        double width = WaveformContainer.ActualWidth;
        if (width <= 0) return;

        double progress = p.X / width;
        if (progress < 0) progress = 0;
        if (progress > 1) progress = 1;

        TimeSpan newTime = TimeSpan.FromSeconds(progress * _audioReader.TotalTime.TotalSeconds);
        _audioReader.CurrentTime = newTime;
        UpdatePlaybackCursor();
    }

    private void UpdateTimeDisplay()
    {
        if (TimeDisplay == null) return;

        TimeSpan duration = _selectionEnd - _selectionStart;
        // Ensure duration isn't negative
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        TimeDisplay.Text = $"Start: {_selectionStart:mm\\:ss\\.fff}  |  End: {_selectionEnd:mm\\:ss\\.fff}  |  Duration: {duration:mm\\:ss\\.fff}";

        // Optional: Change color to Red if duration is 0, Green if valid.
        TimeDisplay.Foreground = (duration.TotalMilliseconds > 0) ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E5FF")) : Brushes.Gray;
    }

    private void SetStart_Click(object sender, RoutedEventArgs e)
    {
        if (_audioReader != null)
        {
            _selectionStart = _audioReader.CurrentTime;
            if (_selectionStart > _selectionEnd) _selectionEnd = _audioReader.TotalTime;
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
        }
    }

    private void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_audioReader != null)
        {
            _selectionEnd = _audioReader.CurrentTime;
            if (_selectionEnd < _selectionStart) _selectionStart = TimeSpan.Zero;
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
        }
    }

    private void UpdateSelectionVisuals()
    {
        if (_audioReader == null || WaveformContainer.ActualWidth <= 0) return;

        double totalSeconds = _audioReader.TotalTime.TotalSeconds;
        if (totalSeconds <= 0) return;

        double startX = (_selectionStart.TotalSeconds / totalSeconds) * WaveformContainer.ActualWidth;
        double endX = (_selectionEnd.TotalSeconds / totalSeconds) * WaveformContainer.ActualWidth;
        double totalWidth = WaveformContainer.ActualWidth;

        // 1. Left Dimming (From 0 to Start)
        DimLeft.Width = startX;

        // 2. Right Dimming (From End to TotalWidth)
        double rightWidth = totalWidth - endX;
        if (rightWidth < 0) rightWidth = 0;
        DimRight.Width = rightWidth;
        DimRight.Margin = new Thickness(endX, 0, 0, 0);

        // 3. Markers
        StartMarker.Margin = new Thickness(startX, 0, 0, 0);
        EndMarker.Margin = new Thickness(endX, 0, 0, 0);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioReader != null)
        {
            _audioReader.Volume = (float)e.NewValue;
        }
    }

    private void PlaySelection_Click(object sender, RoutedEventArgs e)
    {
        if (_waveOut != null && _audioReader != null)
        {
            _audioReader.CurrentTime = _selectionStart;
            _waveOut.Play();
        }
    }

    private void SaveSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAudioFile == null) return;

        // 1. Pause Playback (Safety)
        if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
        {
            _waveOut.Pause();
        }

        // 2. Prepare Default Filename
        string dir = System.IO.Path.GetDirectoryName(_currentAudioFile) ?? "";
        string name = System.IO.Path.GetFileNameWithoutExtension(_currentAudioFile);
        string ext = System.IO.Path.GetExtension(_currentAudioFile);

        // 3. Open SaveFileDialog
        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Trimmed Audio",
            FileName = $"{name}_Trimmed{ext}",
            DefaultExt = ext,
            Filter = $"Audio File (*{ext})|*{ext}|All Files (*.*)|*.*",
            InitialDirectory = dir
        };

        if (saveDialog.ShowDialog() == true)
        {
            string outputFile = saveDialog.FileName;

            // 4. Validation: Prevent Overwriting Source
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

            // 5. Construct Arguments with Escaped Quotes
            string start = _selectionStart.ToString(@"hh\:mm\:ss\.fff");
            string end = _selectionEnd.ToString(@"hh\:mm\:ss\.fff");

            // IMPORTANT: Wrap paths in escaped quotes to handle spaces and special chars
            string args = $"-i \"{_currentAudioFile}\" -ss {start} -to {end} -c copy -y \"{outputFile}\"";

            try
            {
                _processManager.RunSync(_config.FFmpegPath, args);
                MessageBox.Show($"Saved Trimmed Audio:\n{outputFile}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CloseAudioEditor_Click(object sender, RoutedEventArgs e)
    {
        CloseAudioEditor();
    }

    private void CloseAudioEditor()
    {
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
        if (_playbackTimer != null)
        {
            _playbackTimer.Stop();
        }

        AudioEditorGrid.Visibility = Visibility.Collapsed;
        DropZone.Visibility = Visibility.Visible;
        FilesList.Visibility = Visibility.Visible;

        CurrentToolTitle.Text = "Select Tool";
        _currentAudioFile = null;
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

}
