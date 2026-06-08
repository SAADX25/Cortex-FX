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
using PdfiumViewer;
using ImageMagick;
using System.Drawing.Imaging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using System.Threading;

using System.ComponentModel;
using System.Runtime.CompilerServices;

using CortexFX.Models;

namespace CortexFX;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private ObservableCollection<FileModel> _filesToConvert = new ObservableCollection<FileModel>();
    private CancellationTokenSource? _cancellationTokenSource;
    // Audio Editor State
    private string? _currentAudioFile;
    private string? _pendingAudioFile;
    private AudioFileReader? _audioReader;
    private WaveOutEvent? _waveOut;
    private TimeSpan _selectionStart = TimeSpan.Zero;
    private TimeSpan _selectionEnd = TimeSpan.Zero;
    private System.Windows.Threading.DispatcherTimer? _playbackTimer;

    // FFME
    private string _ffmpegBinPath;
    private string ResourcesDirectory
    {
        get
        {
            string baseDirResources = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
            if (Directory.Exists(baseDirResources))
            {
                return baseDirResources;
            }

            string absoluteResources = @"E:\Code-Setup\Cortex FX\Resources";
            return Directory.Exists(absoluteResources) ? absoluteResources : baseDirResources;
        }
    }

    private string FFmpegPath => System.IO.Path.Combine(ResourcesDirectory, "ffmpeg.exe");

    public MainWindow(string? startupFile = null)
    {
        try
        {
            InitializeComponent();
            
            // --- SMART FFMPEG LOADING START ---
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // 1. Try standard path first
            string targetFolder = System.IO.Path.Combine(baseDir, "Resources", "ffmpeg_libs");

            // 2. Auto-Discovery: If not found, search for the DLL recursively
            if (!File.Exists(System.IO.Path.Combine(targetFolder, "avcodec-58.dll")))
            {
                var foundFile = Directory.GetFiles(baseDir, "avcodec-58.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (foundFile != null)
                {
                    targetFolder = System.IO.Path.GetDirectoryName(foundFile)!;
                }
            }

            // 3. Attempt to Load
            _ffmpegBinPath = targetFolder;
            try
            {
                Unosquare.FFME.Library.FFmpegDirectory = _ffmpegBinPath;
                Unosquare.FFME.Library.LoadFFmpeg();
            }
            catch (Exception ffmpegEx)
            {
                // Detailed Debug Info if it still fails
                string folderContent = "";
                if (Directory.Exists(targetFolder))
                {
                    var files = Directory.GetFiles(targetFolder).Select(System.IO.Path.GetFileName).Take(5);
                    folderContent = string.Join(", ", files);
                }
                else
                {
                    folderContent = "Folder does not exist!";
                }
                MessageBox.Show($"FFmpeg Critical Error!\n\nTarget Path: {targetFolder}\nError: {ffmpegEx.Message}\n\nFiles found in folder: {folderContent}", "Startup Error");
            }
            // --- SMART FFMPEG LOADING END ---

            // Initialize the Video Compressor view
            VideoCompressorEditor.Initialize(FFmpegPath);
            VideoCompressorEditor.CloseRequested += (s, e) => UpdateUIMode(false);

            FilesList.ItemsSource = _filesToConvert;
            // Trigger initial population
            PopulateFormats("Document");

            // Set Version
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v0.6.0";

            // Handle context menu startup file
            if (!string.IsNullOrEmpty(startupFile) && File.Exists(startupFile))
            {
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
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup Error: {ex}", "Cortex FX Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            // Ensure the app shuts down cleanly if startup fails
            Application.Current.Shutdown();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(ResourcesDirectory))
        {
            MessageBox.Show($"Resources folder not found.\nPlace tools in:\n{ResourcesDirectory}", "Resources Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string[] requiredFiles = { "ffmpeg.exe", "magick.exe", "pdftocairo.exe" };
        var missing = requiredFiles.Where(file => !File.Exists(System.IO.Path.Combine(ResourcesDirectory, file))).ToList();
        if (missing.Count > 0)
        {
            string missingList = string.Join("\n", missing);
            MessageBox.Show($"Missing tools in Resources:\n{missingList}\n\nPath:\n{ResourcesDirectory}", "Resources Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    // 1. The Master Capabilities Dictionary
    private readonly Dictionary<string, List<string>> _conversionRules = new()
    {
        // Documents
        { ".pdf",  new List<string> { "DOCX", "PPTX", "XLSX", "JPG", "PNG" } },
        { ".docx", new List<string> { "PDF", "PPTX" } },
        { ".doc",  new List<string> { "PDF", "PPTX" } },
        { ".pptx", new List<string> { "PDF", "DOCX" } },
        { ".ppt",  new List<string> { "PDF", "DOCX" } },
        { ".xlsx", new List<string> { "PDF" } },
        { ".xls",  new List<string> { "PDF" } },
        
        // Images
        { ".jpg",  new List<string> { "PNG", "BMP", "WEBP", "ICO", "PDF" } },
        { ".jpeg", new List<string> { "PNG", "BMP", "WEBP", "ICO", "PDF" } },
        { ".png",  new List<string> { "JPG", "BMP", "WEBP", "ICO", "PDF" } },
        { ".bmp",  new List<string> { "JPG", "PNG", "WEBP", "ICO", "PDF" } },
        { ".webp", new List<string> { "JPG", "PNG", "PDF" } },
        { ".ico",  new List<string> { "PNG", "JPG" } },

        // Audio
        { ".mp3",  new List<string> { "WAV", "AAC", "FLAC", "M4A", "OGG" } },
        { ".wav",  new List<string> { "MP3", "AAC", "FLAC", "M4A", "OGG" } },
        { ".flac", new List<string> { "MP3", "WAV", "AAC", "M4A", "OGG" } },
        { ".m4a",  new List<string> { "MP3", "WAV", "AAC", "FLAC", "OGG" } },
        { ".aac",  new List<string> { "MP3", "WAV", "FLAC", "M4A", "OGG" } },
        { ".ogg",  new List<string> { "MP3", "WAV", "AAC", "FLAC", "M4A" } },
        
        // Video
        { ".mp4",  new List<string> { "MP3", "AVI", "MOV", "GIF", "WEBM", "MKV" } },
        { ".mov",  new List<string> { "MP4", "AVI", "GIF", "MP3" } },
        { ".avi",  new List<string> { "MP4", "MOV", "GIF", "MP3" } },
        { ".mkv",  new List<string> { "MP4", "AVI", "MOV", "MP3" } },
        { ".webm", new List<string> { "MP4", "AVI", "MOV", "MP3" } }
    };

    private AppMode _currentMode = AppMode.Dashboard;

    private string? _universalFilterMode = null; // null = All, or "Video", "Audio", "Image", "Document"

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
            if(RadioImage != null) RadioImage.IsChecked = false;
            if(RadioVideo != null) RadioVideo.IsChecked = false;
            if(RadioAudio != null) RadioAudio.IsChecked = false;
            if(RadioDocument != null) RadioDocument.IsChecked = false;
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
        // Check if file already exists in the list
        bool exists = false;
        foreach(var f in _filesToConvert)
        {
            if(f.FullPath == file)
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
                    if (new[] { ".mp3", ".wav", ".m4a", ".ogg" }.Contains(ext))
                    {
                        // NEW LOGIC: Show Choice Overlay
                        _pendingAudioFile = items[0];
                        AudioChoiceOverlay.Visibility = Visibility.Visible;
                        return; // Stop normal processing
                    }
                }

                bool invalidFound = false;
                
                // Universal Mode Logic
                if (_currentMode == AppMode.Universal && items.Length > 0)
                {
                    // Take the first file's extension to determine capabilities
                    string firstFile = items[0];
                    string ext = System.IO.Path.GetExtension(firstFile).ToLower();

                    // Reject Documents in Universal Mode
                    var docExtensions = new List<string> { ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls" };
                    if (docExtensions.Contains(ext))
                    {
                        MessageBox.Show("Please use the Main Dashboard for Documents. The '+' tool is for Media (Video/Audio/Image) only.", "Wrong Tool", MessageBoxButton.OK, MessageBoxImage.Information);
                        return; // Reject drop
                    }
                    
                    if (_conversionRules.ContainsKey(ext))
                    {
                        // Found supported type!
                        var formats = _conversionRules[ext];
                        FormatComboBox.Items.Clear();
                        foreach(var fmt in formats)
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
                    var supported = new[] { 
                        ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".pdf", 
                        ".png", ".jpg", ".jpeg", ".ico", ".webp",
                        ".mp4", ".avi", ".mkv", ".mov", ".gif", ".webm", 
                        ".mp3", ".wav", ".m4a", ".ogg" 
                    };
                    
                    if (supported.Contains(ext))
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
        catch { /* Access denied or other errors, skip */ }
    }

    private string GetFilterForCurrentMode()
    {
        if (_currentMode == AppMode.Universal && _universalFilterMode != null)
        {
            return _universalFilterMode switch
            {
                "Video" => "Video Files (*.mp4;*.avi;*.mov)|*.mp4;*.avi;*.mov;*.mkv;*.webm",
                "Audio" => "Audio Files (*.mp3;*.wav)|*.mp3;*.wav;*.flac;*.m4a;*.aac",
                "Image" => "Image Files (*.jpg;*.png)|*.jpg;*.png;*.jpeg;*.bmp;*.webp;*.ico",
                "Document" => "Documents (*.pdf;*.docx)|*.pdf;*.docx;*.doc;*.pptx;*.ppt;*.xlsx;*.xls",
                _ => "All Files|*.*"
            };
        }

        return _currentMode switch
        {
            AppMode.PdfToWord or AppMode.PdfToPpt or AppMode.PdfToImage => "PDF Files (*.pdf)|*.pdf",
            AppMode.WordToPdf => "Word Documents (*.docx;*.doc)|*.docx;*.doc",
            AppMode.PptToPdf => "PowerPoint Presentations (*.pptx;*.ppt)|*.pptx;*.ppt",
            AppMode.ExcelToPdf => "Excel Workbooks (*.xlsx;*.xls)|*.xlsx;*.xls",
            _ => "All Supported Files|*.pdf;*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt;*.png;*.jpg;*.jpeg;*.mp4;*.mp3"
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
                     "Video" => new List<string> { ".mp4", ".avi", ".mov", ".mkv", ".webm" }.Contains(ext),
                     "Audio" => new List<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac" }.Contains(ext),
                     "Image" => new List<string> { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".ico" }.Contains(ext),
                     "Document" => new List<string> { ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls" }.Contains(ext),
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
                if (new[] { ".mp3", ".wav", ".m4a", ".ogg" }.Contains(ext))
                {
                    _pendingAudioFile = selectedFiles[0]; // Store for later
                    AudioChoiceOverlay.Visibility = Visibility.Visible; // Show the Choice Screen
                    return; // STOP here, don't load into list yet
                }
            }

            foreach (var file in dialog.FileNames)
            {
                AddFileToList(file);
            }
        }
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string fullPath)
        {
            FileModel? itemToRemove = null;
            foreach(var f in _filesToConvert)
            {
                if(f.FullPath == fullPath)
                {
                    itemToRemove = f;
                    break;
                }
            }

            if (itemToRemove != null)
            {
                _filesToConvert.Remove(itemToRemove);
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
                baseFormats = new[] { "JPG", "PNG", "ICO", "WEBP", "PDF" };
                break;
            case "Video":
                baseFormats = new[] { "MP4", "AVI", "MKV", "MOV", "GIF", "WEBM" };
                break;
            case "Audio":
                baseFormats = new[] { "MP3", "WAV", "M4A", "OGG" };
                break;
            case "Document":
                baseFormats = new[] { "PDF" };
                break;
        }

        // Add base formats first
        foreach (var f in baseFormats) AddFormat(f);

        if (category == "Document")
        {
            // 2. Distinct Logic: Check Input Types
            bool hasPdf = _filesToConvert.Any(f => System.IO.Path.GetExtension(f.FullPath).ToLower() == ".pdf");
            bool hasWord = _filesToConvert.Any(f => {
                string ext = System.IO.Path.GetExtension(f.FullPath).ToLower();
                return ext == ".docx" || ext == ".doc";
            });
            bool hasPpt = _filesToConvert.Any(f => {
                string ext = System.IO.Path.GetExtension(f.FullPath).ToLower();
                return ext == ".pptx" || ext == ".ppt";
            });
            bool hasExcel = _filesToConvert.Any(f => {
                string ext = System.IO.Path.GetExtension(f.FullPath).ToLower();
                return ext == ".xlsx" || ext == ".xls";
            });

            // Rule: If PDF is present -> Add Office Targets
            if (hasPdf)
            {
                AddFormat("DOCX");
                AddFormat("XLSX");
                AddFormat("PPTX");
            }

            // Rule: Bridge Logic (Word <-> PPT)
            if (hasWord) AddFormat("PPTX");
            if (hasPpt) AddFormat("DOCX");
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
        }
    }

    private void CloseOverlay_Click(object sender, RoutedEventArgs e)
    {
        SuccessOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = "Ready";
        ConversionProgress.Value = 0;
        _filesToConvert.Clear();
        ConvertButton.IsEnabled = true;
    }

    private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
    {
        e.Handled = new System.Text.RegularExpressions.Regex("[^0-9]+").IsMatch(e.Text);
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
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
            MessageBox.Show("Please drop some files first.", "No Files", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (FormatComboBox.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Content == null)
            return;

        // Cancel existing conversion if running
        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        string targetFormat = selectedItem.Content.ToString()!.ToLower();
        string outputDir = OutputPathBox.Text;
        double qualityLevel = QualitySlider.Value;

        // Advanced Settings
        string resizeW = ResizeWidthBox.Text;
        string resizeH = ResizeHeightBox.Text;
        bool maintainAspect = AspectRatioCheckBox.IsChecked == true;
        string dpi = DpiBox.Text;
        bool sharpen = SharpenCheckBox.IsChecked == true;
        bool grayscale = GrayscaleCheckBox.IsChecked == true;
        bool autoEnhance = AutoEnhanceCheckBox.IsChecked == true;

        // Tool Paths
        string resourcesPath = ResourcesDirectory;
        string magickPath = System.IO.Path.Combine(resourcesPath, "magick.exe");
        string ffmpegPath = FFmpegPath;
        string pdftocairoPath = System.IO.Path.Combine(resourcesPath, "pdftocairo.exe");

        bool useFFmpeg = new List<string> { "mp4", "mp3", "avi", "wav", "mkv", "mov", "gif", "webm", "m4a", "ogg", "flac", "aac" }.Contains(targetFormat);
        bool useMagick = new List<string> { "jpg", "jpeg", "png", "bmp", "webp", "ico" }.Contains(targetFormat);

        // Routing Logic
        // Determine which engine to use based on input and output
        // If Universal Mode, we need strict routing based on extension
        
        ConvertButton.IsEnabled = false;
        
        // Use 0-100 range for the overall batch progress, but we need per-file granularity
        ConversionProgress.Value = 0;
        ConversionProgress.Maximum = _filesToConvert.Count * 100; // 100 points per file for smooth updates
        StatusText.Text = "Converting...";

        try
        {
            await Task.Run(async () =>
            {
                int processedFiles = 0;
                int successCount = 0;
                int errorCount = 0;
                var files = new List<FileModel>(_filesToConvert);

                // 1. Collect all file paths
                var filePaths = files.Select(f => f.FullPath).ToList();

                // 2. Check for Merge Condition: Multiple Images -> PDF
                bool isMergeChecked = false;
                Dispatcher.Invoke(() => isMergeChecked = chkMergePdf.IsChecked == true && chkMergePdf.Visibility == Visibility.Visible);
                
                // Strict check: Multiple files + All Images + Target PDF + Merge Checked
                bool areAllImages = filePaths.All(f => {
                    string ext = System.IO.Path.GetExtension(f).ToLower();
                    return new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Contains(ext);
                });

                if (filePaths.Count > 1 && areAllImages && targetFormat == "pdf" && isMergeChecked)
                {
                    // --- MERGE MODE START ---
                    Dispatcher.Invoke(() => StatusText.Text = "Merging all images...");
                    
                    string outputName = $"Merged_Images_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                    
                    // Handle Output Path
                    string outputFolder = string.IsNullOrWhiteSpace(outputDir) ? (System.IO.Path.GetDirectoryName(filePaths[0]) ?? "") : outputDir;
                    
                    bool createSubfolder = false;
                    Dispatcher.Invoke(() => createSubfolder = chkCreateSubfolder.IsChecked == true);
                    
                    if (createSubfolder)
                    {
                        outputFolder = System.IO.Path.Combine(outputFolder, "Cortex FX");
                        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
                    }
                    
                    string finalPath = System.IO.Path.Combine(outputFolder, outputName);

                    // Update UI to show working
                    foreach (var f in files) UpdateFileStatus(f, "Merging...", "#FF8C00");

                    try
                    {
                        MergeImagesToOnePdf(filePaths, finalPath);
                        
                        // Update UI to Done
                        foreach (var f in files) UpdateFileStatus(f, "Merged!", "#4CAF50");
                        successCount = files.Count;
                    }
                    catch (Exception)
                    {
                        foreach (var f in files) UpdateFileStatus(f, "Error", "#FF5555");
                        errorCount = files.Count;
                    }
                    
                    Dispatcher.Invoke(() => 
                    {
                        ConversionProgress.Value = ConversionProgress.Maximum;
                        StatusText.Text = "Merge Complete!";
                        
                        var overlayStack = FindVisualChild<StackPanel>(SuccessOverlay);
                        if (overlayStack != null)
                        {
                                var textBlocks = new List<TextBlock>();
                                FindVisualChildren(overlayStack, textBlocks);
                                if (textBlocks.Count >= 2)
                                {
                                    var resultText = textBlocks[1];
                                    resultText.Text = $"Successfully merged {successCount} images.";
                                }
                        }
                        SuccessOverlay.Visibility = Visibility.Visible;
                    });

                    // CRITICAL: STOP HERE! DO NOT RUN THE REST OF THE FUNCTION
                    return;
                    // --- MERGE MODE END ---
                }

                // 3. FALLBACK LOOP: Single File Conversions
                // This will ONLY run if the above block did NOT execute (i.e., not a batch merge)
                foreach (var fileItem in files)
                {
                    if (token.IsCancellationRequested) break;

                    string file = fileItem.FullPath;
                    string extension = System.IO.Path.GetExtension(file).ToLower();
                    
                    UpdateFileStatus(fileItem, "Processing...", "#007ACC"); // Blue

                    try 
                    {
                        Dispatcher.Invoke(() => StatusText.Text = $"Converting {System.IO.Path.GetFileName(file)}...");

                        string? dirName = System.IO.Path.GetDirectoryName(file);
                        string userOutputDir = string.IsNullOrWhiteSpace(outputDir) ? (dirName ?? "") : outputDir;
                        
                        string finalOutputDir = userOutputDir;
                        
                        // Checkbox Logic: Use "Cortex FX" subfolder only if checked
                        bool createSubfolder = false;
                        Dispatcher.Invoke(() => createSubfolder = chkCreateSubfolder.IsChecked == true);
                        
                        if (createSubfolder)
                        {
                            finalOutputDir = System.IO.Path.Combine(finalOutputDir, "Cortex FX");
                            if (!Directory.Exists(finalOutputDir)) Directory.CreateDirectory(finalOutputDir);
                        }

                        string fileNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(file);
                        string outputFileName = fileNameWithoutExt + "." + targetFormat;
                        string fullOutputPath = System.IO.Path.Combine(finalOutputDir, outputFileName);
                        
                        // Avoid overwriting input if same path (though we force subfolder now so less likely)
                        if (string.Equals(file, fullOutputPath, StringComparison.OrdinalIgnoreCase))
                        {
                            fileNameWithoutExt += "_optimized";
                            outputFileName = fileNameWithoutExt + "." + targetFormat;
                        }

                        string newFileName = System.IO.Path.Combine(finalOutputDir, outputFileName);

                        // Progress Reporter for this specific file
                        var fileProgress = new Progress<double>(p => 
                        {
                            double totalProgress = (processedFiles * 100) + p;
                            Dispatcher.Invoke(() => ConversionProgress.Value = totalProgress);
                        });

                        // --- INTELLIGENT ROUTING START ---
                        
                        // 1. Document Routing (CortexEngine)
                        if (extension == ".docx" || extension == ".doc" || extension == ".xlsx" || extension == ".xls" || extension == ".pptx" || extension == ".ppt" || (extension == ".pdf" && (targetFormat == "docx" || targetFormat == "pptx" || targetFormat == "xlsx")))
                        {
                             // ... (Document Logic)
                             if (targetFormat == "pdf")
                             {
                                 int engineQuality = qualityLevel < 30 ? 0 : 1;
                                 await DocumentConverter.ConvertDocumentAsync(file, newFileName, "pdf", engineQuality, token, fileProgress);
                             }
                             else if (targetFormat == "pptx" && (extension == ".docx" || extension == ".doc"))
                             {
                                 // ... (Word to PPT Logic)
                                 // Note: finalOutputDir is already set to ".../Cortex FX" or ".../Cortex FX/Filename"
                                 // We don't need to append Cortex FX again inside here
                                 
                                 string pptxFileName = $"{fileNameWithoutExt}.pptx";
                                 string pptxOutputPath = System.IO.Path.Combine(finalOutputDir, pptxFileName);
                                 await DocumentConverter.ConvertWordToPowerPointAsync(file, pptxOutputPath, fileProgress);
                             }
                             else if (targetFormat == "docx" && (extension == ".pptx" || extension == ".ppt"))
                             {
                                 // ... (PPT to Word Logic)
                                 string docxFileName = $"{fileNameWithoutExt}.docx";
                                 string docxOutputPath = System.IO.Path.Combine(finalOutputDir, docxFileName);
                                 await DocumentConverter.ConvertPowerPointToWordAsync(file, docxOutputPath, fileProgress);
                             }
                             else if (extension == ".pdf")
                             {
                                 string officeFileName = $"{fileNameWithoutExt}.{targetFormat}";
                                 string officeOutputPath = System.IO.Path.Combine(finalOutputDir, officeFileName);
                                 await DocumentConverter.ConvertPdfToOfficeAsync(file, officeOutputPath, targetFormat, fileProgress);
                             }
                        }
                        // 2. JPG/PNG -> PDF Conversion Logic
                        else if ((new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(extension)) && targetFormat == "pdf")
                        {
                             Dispatcher.Invoke(() => StatusText.Text = $"Converting {System.IO.Path.GetFileName(file)} to PDF...");
                             UpdateFileStatus(fileItem, "Converting to PDF...", "#FF8C00"); // Orange

                             // Use Magick to convert Image to PDF
                             string arguments = $"\"{file}\" \"{newFileName}\"";
                             if (!File.Exists(magickPath)) throw new FileNotFoundException($"Magick not found at: {magickPath}");
                             RunExternalProcess(magickPath, arguments);
                             
                             ((IProgress<double>)fileProgress).Report(100);
                             UpdateFileStatus(fileItem, "Done", "#4CAF50");
                             successCount++;
                        }
                        // 3. PDF -> JPG/PNG Conversion Logic (Explicit)
                        else if (extension == ".pdf" && (targetFormat == "jpg" || targetFormat == "png"))
                        {
                             Dispatcher.Invoke(() => StatusText.Text = $"Rendering {System.IO.Path.GetFileName(file)} to {targetFormat.ToUpper()}...");
                             UpdateFileStatus(fileItem, $"Rendering to {targetFormat.ToUpper()}...", "#FF8C00");

                             using (var document = PdfiumViewer.PdfDocument.Load(file))
                             {
                                 int dpiValue = 150; 
                                 if (!string.IsNullOrWhiteSpace(dpi) && int.TryParse(dpi, out int customDpi)) dpiValue = customDpi;
                                 else dpiValue = 72 + (int)((qualityLevel / 100.0) * (300 - 72));

                                 for (int i = 0; i < document.PageCount; i++)
                                 {
                                     if (token.IsCancellationRequested) break;
                                     string pageFileName = $"{fileNameWithoutExt}_Page{i + 1}.{targetFormat}";
                                     string pageOutputPath = System.IO.Path.Combine(finalOutputDir, pageFileName);

                                     var pageSize = document.PageSizes[i];
                                     int width = (int)(pageSize.Width / 72.0 * dpiValue);
                                     int height = (int)(pageSize.Height / 72.0 * dpiValue);

                                     using (var image = document.Render(i, width, height, dpiValue, dpiValue, false))
                                     {
                                         ImageFormat format = targetFormat == "png" ? ImageFormat.Png : ImageFormat.Jpeg;
                                         image.Save(pageOutputPath, format);
                                     }
                                     
                                     double pagePercent = ((double)(i + 1) / document.PageCount) * 100;
                                     ((IProgress<double>)fileProgress).Report(pagePercent);
                                 }
                             }
                             
                             UpdateFileStatus(fileItem, "Done", "#4CAF50");
                             successCount++;
                        }
                        // 4. Other PDF to Image (Fallback for BMP, WEBP, ICO)
                        else if (extension == ".pdf" && useMagick) 
                        {
                             // PDF to Image Logic (Pdfium)
                             // Use finalOutputDir directly
                             
                             using (var document = PdfiumViewer.PdfDocument.Load(file))
                             {
                                 int dpiValue = 150; 
                                 if (!string.IsNullOrWhiteSpace(dpi) && int.TryParse(dpi, out int customDpi)) dpiValue = customDpi;
                                 else dpiValue = 72 + (int)((qualityLevel / 100.0) * (300 - 72));

                                 for (int i = 0; i < document.PageCount; i++)
                                 {
                                     if (token.IsCancellationRequested) break;
                                     string pageFileName = $"{fileNameWithoutExt}_Page{i + 1}.{targetFormat}";
                                     string pageOutputPath = System.IO.Path.Combine(finalOutputDir, pageFileName);

                                     var pageSize = document.PageSizes[i];
                                     int width = (int)(pageSize.Width / 72.0 * dpiValue);
                                     int height = (int)(pageSize.Height / 72.0 * dpiValue);

                                     using (var image = document.Render(i, width, height, dpiValue, dpiValue, false))
                                     {
                                         ImageFormat format = targetFormat == "png" ? ImageFormat.Png : ImageFormat.Jpeg; // Default to Jpeg if others
                                         if (targetFormat == "bmp") format = ImageFormat.Bmp;
                                         else if (targetFormat == "gif") format = ImageFormat.Gif;
                                         
                                         image.Save(pageOutputPath, format);
                                     }
                                     
                                     double pagePercent = ((double)(i + 1) / document.PageCount) * 100;
                                     ((IProgress<double>)fileProgress).Report(pagePercent);
                                 }
                             }
                        }
                        // 3. Media Conversion (FFmpeg)
                        else if (useFFmpeg || (useMagick && new List<string> { ".mp4", ".mov", ".avi", ".mkv", ".webm" }.Contains(extension)))
                        {
                            // FFmpeg Logic (Video/Audio)
                            string arguments = "";
                            int crf = 28 - (int)((qualityLevel / 100.0) * 10);
                            string preset = qualityLevel < 40 ? "fast" : (qualityLevel < 80 ? "medium" : "slow");
                            string qualityArgs = $"-crf {crf} -preset {preset}";   

                            if (targetFormat == "gif")
                                arguments = $"-i \"{file}\" -vf \"fps=10,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -loop 0 -y \"{newFileName}\"";
                            else if (new List<string> { "mp3", "wav", "aac", "flac", "m4a" }.Contains(targetFormat))
                                arguments = $"-i \"{file}\" -vn -y \"{newFileName}\""; // Audio extraction
                            else
                                arguments = $"-i \"{file}\" {qualityArgs} -y \"{newFileName}\"";
                                
                            if (!File.Exists(ffmpegPath)) throw new FileNotFoundException($"FFmpeg not found at: {ffmpegPath}");
                            RunExternalProcess(ffmpegPath, arguments);
                            ((IProgress<double>)fileProgress).Report(100);
                        }
                        // 4. Image Conversion (Magick)
                        else if (useMagick)
                        {
                            // Magick Logic (Image to Image AND Image to PDF)
                            int q = Math.Max(40, (int)qualityLevel);
                            string qualityArgs = $"-quality {q}";
                            if (qualityLevel < 30) qualityArgs += " -resize 80%";       

                            StringBuilder advancedArgs = new StringBuilder();
                            // ... existing args logic ...
                            if (!string.IsNullOrWhiteSpace(resizeW) && !string.IsNullOrWhiteSpace(resizeH))
                            {
                                advancedArgs.Append($" -resize {resizeW}x{resizeH}");
                                if (!maintainAspect) advancedArgs.Append("!");
                                advancedArgs.Append(" ");
                                if (qualityLevel < 30) qualityArgs = qualityArgs.Replace("-resize 80%", "");
                            }
                            if (!string.IsNullOrWhiteSpace(dpi)) advancedArgs.Append($" -density {dpi} -units PixelsPerInch ");
                            if (sharpen) advancedArgs.Append("-sharpen 0x1 ");
                            if (grayscale) advancedArgs.Append("-colorspace Gray ");
                            if (autoEnhance) advancedArgs.Append("-normalize -auto-level ");

                            string arguments = $"\"{file}\" {qualityArgs} {advancedArgs} ";
                            if (targetFormat == "ico") arguments += "-resize 256x256 ";
                            arguments += $"\"{newFileName}\"";
                            
                            // Image to PDF specific (Magick handles this natively, but we ensure args are clean)
                            if (targetFormat == "pdf")
                            {
                                // For PDF, we might want to ensure page size or just default
                                arguments = $"\"{file}\" {qualityArgs} {advancedArgs} \"{newFileName}\"";
                            }
                            
                            if (!File.Exists(magickPath)) throw new FileNotFoundException($"Magick not found at: {magickPath}");
                            RunExternalProcess(magickPath, arguments);
                            ((IProgress<double>)fileProgress).Report(100);
                        }
                        else
                        {
                            throw new Exception($"No suitable engine found for converting {extension} to {targetFormat}");
                        }

                        UpdateFileStatus(fileItem, "Done", "#4CAF50"); // Green
                        successCount++;
                    }
                    catch (Exception)
                    {
                         UpdateFileStatus(fileItem, "Error", "#FF5555"); // Red
                         errorCount++;
                         // Optional: Store error message in model
                         
                         Dispatcher.Invoke(() => 
                         {
                             StatusText.Text = $"Error: {System.IO.Path.GetFileName(file)}";
                             // Don't popup for every error in batch, maybe just log or show at end
                             // MessageBox.Show($"Error converting {System.IO.Path.GetFileName(file)}:\n\n{ex.Message}", "Conversion Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                         });
                    }
                    
                    processedFiles++;
                    // Ensure we hit the exact checkpoint for this file
                    Dispatcher.Invoke(() => ConversionProgress.Value = processedFiles * 100);
                }

                Dispatcher.Invoke(() => {
                    StatusText.Text = "Conversion Complete!";
                    
                    // Final Report
                    var overlayStack = FindVisualChild<StackPanel>(SuccessOverlay);
                    if (overlayStack != null)
                    {
                         var textBlocks = new List<TextBlock>();
                         FindVisualChildren(overlayStack, textBlocks);
                         if (textBlocks.Count >= 2)
                         {
                             var resultText = textBlocks[1];
                             resultText.Text = $"Successfully converted {successCount}/{_filesToConvert.Count} files.";
                             if (errorCount > 0)
                             {
                                 resultText.Text += $"\n({errorCount} failed)";
                             }
                         }
                    }
                    
                    SuccessOverlay.Visibility = Visibility.Visible;
                });
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Critical Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
             ConvertButton.IsEnabled = true;
        }
    }

    private void RunExternalProcess(string exePath, string arguments)
    {
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using (Process? process = Process.Start(psi))
        {
            if (process == null)
            {
                 throw new Exception("Failed to start external process.");
            }

            // Read standard error before waiting for exit to prevent deadlocks
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            
            if (process.ExitCode != 0)
            {
                throw new Exception($"Process exited with code {process.ExitCode}:\n{error}");
            }
        }
    }

    // Helper to prevent crashes 
    private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject 
    { 
        if (parent == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) 
        { 
            var child = VisualTreeHelper.GetChild(parent, i); 
            if (child is T result) return result; 
            var descendant = FindVisualChild<T>(child); 
            if (descendant != null) return descendant; 
        } 
        return null; 
    }

    // Recursive helper to find all children of a specific type
    private void FindVisualChildren<T>(DependencyObject depObj, List<T> results) where T : DependencyObject 
    { 
        if (depObj == null) return; 
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++) 
        { 
            var child = VisualTreeHelper.GetChild(depObj, i); 
            if (child is T t) results.Add(t); 
            FindVisualChildren(child, results); 
        } 
    }

    private void UpdateFileStatus(FileModel file, string status, string color)
    {
        // 1. Update Model (MVVM Persistence)
        file.Status = status;
        file.StatusColor = color;

        // 2. Safe UI Update (FINAL FIX)
        Dispatcher.Invoke(() => 
        { 
            try 
            { 
                var container = FilesList.ItemContainerGenerator.ContainerFromItem(file) as ListBoxItem;
                if (container == null) return; 

                // Use the safe helper to find the TextBlock holding the status
                // We assume it's one of the TextBlocks in the template
                var allTextBlocks = new List<TextBlock>(); 
                FindVisualChildren(container, allTextBlocks); 
                
                // Robustness: Identify the correct block. 
                // If your XAML template has 3 TextBlocks (Icon, Name, Status), use the last one.
                // Or try to match by current status text if needed, but last is usually safe for this simple template.
                var statusBlock = allTextBlocks.LastOrDefault(); 

                if (statusBlock != null) 
                { 
                    statusBlock.Text = status; 
                    statusBlock.Foreground = (Brush)new BrushConverter().ConvertFromString(color); 
                }
            } 
            catch (Exception ex) 
            { 
                // Log silent failure but do not crash
                Debug.WriteLine($"UI Update Failed: {ex.Message}"); 
            } 
        }); 
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
    }

    private void MergeImagesToOnePdf(List<string> imagePaths, string outputPath)
    {
        // Using Magick.NET to merge images into a single PDF
        // This is preferred over PdfSharp as we already have Magick.NET and it handles more formats
        using (var collection = new MagickImageCollection())
        {
            foreach (var imgPath in imagePaths)
            {
                var img = new MagickImage(imgPath);
                collection.Add(img);
            }
            
            // Write to PDF
            collection.Write(outputPath);
        }
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
            CloseAudioEditor_Click(null, null);
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

            if (!File.Exists(FFmpegPath))
            {
                 MessageBox.Show($"FFmpeg not found.\nPlace ffmpeg.exe in:\n{FFmpegPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                 return;
            }
            
            // 5. Construct Arguments with Escaped Quotes
            string start = _selectionStart.ToString(@"hh\:mm\:ss\.fff");
            string end = _selectionEnd.ToString(@"hh\:mm\:ss\.fff");
            
            // IMPORTANT: Wrap paths in escaped quotes to handle spaces and special chars
            string args = $"-i \"{_currentAudioFile}\" -ss {start} -to {end} -c copy -y \"{outputFile}\"";
            
            try 
            {
                RunExternalProcess(FFmpegPath, args);
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
