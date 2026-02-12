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

namespace CortexFX;

public class FileModel
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
}

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private ObservableCollection<FileModel> _filesToConvert = new ObservableCollection<FileModel>();

    public MainWindow(string? startupFile = null)
    {
        try
        {
            InitializeComponent();
            FilesList.ItemsSource = _filesToConvert;
            // Trigger initial population
            PopulateFormats("Image");

            // Set Version
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v1.0.0";

            // Handle context menu startup file
            if (!string.IsNullOrEmpty(startupFile) && File.Exists(startupFile))
            {
                AddFileToList(startupFile);
            }

            // Check registry state for checkbox
            if (ContextMenuCheckBox != null)
            {
                ContextMenuCheckBox.IsChecked = RegistryManager.IsRegistered();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup Error: {ex}", "Cortex FX Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            // Ensure the app shuts down cleanly if startup fails
            Application.Current.Shutdown();
        }
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
            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null)
            {
                foreach (var file in files)
                {
                    AddFileToList(file);
                }
            }
        }
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Files to Convert",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
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

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.IsChecked == true && FormatComboBox != null)
        {
            if (rb.Content == null) return;
            string category = rb.Content.ToString()!.Replace("🖼️ ", "").Replace("🎥 ", "").Replace("🎵 ", "");
            PopulateFormats(category);
        }
    }

    private void PopulateFormats(string category)
    {
        if (FormatComboBox == null) return;

        FormatComboBox.Items.Clear();
        string[] formats = new string[] { };

        switch (category)
        {
            case "Image":
                formats = new[] { "JPG", "PNG", "ICO", "WEBP", "PDF" };
                break;
            case "Video":
                formats = new[] { "MP4", "AVI", "MKV", "MOV", "GIF", "WEBM" };
                break;
            case "Audio":
                formats = new[] { "MP3", "WAV", "M4A", "OGG" };
                break;
        }

        foreach (var format in formats)
        {
            FormatComboBox.Items.Add(new ComboBoxItem { Content = format });
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
        string resourcesPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
        string magickPath = System.IO.Path.Combine(resourcesPath, "magick.exe");
        string ffmpegPath = System.IO.Path.Combine(resourcesPath, "ffmpeg.exe");
        string pdftocairoPath = System.IO.Path.Combine(resourcesPath, "pdftocairo.exe");

        // Tool Selection Logic
        bool useFFmpeg = new List<string> { "mp4", "mp3", "avi", "wav", "mkv", "mov", "gif", "webm", "m4a", "ogg" }.Contains(targetFormat);
        
        string selectedToolPath;
        string toolName;

        // Determine tool based on input file type and target format
        // Logic will be handled per file in the loop, but we need to check existence of all potential tools here or inside the loop.
        // Simplified: Check all tools that might be needed or just the one for the general case? 
        // Better: Check inside the loop for specific tool, or pre-check all.
        // For now, let's pre-check based on likely usage.
        
        // Since we process a list, different files might need different tools (e.g. PDF vs PNG input).
        // Let's validate tools inside the loop to be accurate, or check all relevant ones now.
        // Re-design: Let's check existence just before running for that specific file type to be safe.
        
        ConvertButton.IsEnabled = false;
        ConversionProgress.Value = 0;
        ConversionProgress.Maximum = _filesToConvert.Count;
        StatusText.Text = "Converting...";

        try
        {
            await Task.Run(() =>
            {
                int count = 0;
                // Create a copy to iterate safely
                var files = new List<FileModel>(_filesToConvert);

                foreach (var fileItem in files)
                {
                    string file = fileItem.FullPath;
                    try 
                    {
                        // Determine Output Path
                        string? dirName = System.IO.Path.GetDirectoryName(file);
                        string finalOutputDir = string.IsNullOrWhiteSpace(outputDir) ? (dirName ?? "") : outputDir;
                        string fileNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(file);
                        
                        // Safety Check: Avoid overwriting input file
                        string outputFileName = fileNameWithoutExt + "." + targetFormat;
                        string fullOutputPath = System.IO.Path.Combine(finalOutputDir, outputFileName);
                        
                        if (string.Equals(file, fullOutputPath, StringComparison.OrdinalIgnoreCase))
                        {
                            fileNameWithoutExt += "_optimized";
                            outputFileName = fileNameWithoutExt + "." + targetFormat;
                        }

                        string newFileName = System.IO.Path.Combine(finalOutputDir, outputFileName);
                        string outputNoExt = System.IO.Path.Combine(finalOutputDir, fileNameWithoutExt);

                        string arguments = "";
                        string currentToolPath = "";

                        // Determine Tool and Arguments
                        string extension = System.IO.Path.GetExtension(file).ToLower();

                        if (extension == ".pdf" && (targetFormat == "png" || targetFormat == "jpg"))
                        {
                            // PDF Conversion using pdftocairo
                            currentToolPath = pdftocairoPath;
                            string formatFlag = targetFormat == "png" ? "-png" : "-jpeg";
                            
                            string qualityArgs = "";
                            if (qualityLevel == 1) qualityArgs = "-r 72";       // Small
                            else if (qualityLevel == 2) qualityArgs = "-r 150"; // Balanced
                            else if (qualityLevel == 3) qualityArgs = "-r 300"; // High

                            // pdftocairo appends extension automatically
                            arguments = $"{formatFlag} {qualityArgs} -singlefile \"{file}\" \"{outputNoExt}\"";
                        }
                        else if (useFFmpeg)
                        {
                            // FFmpeg
                            currentToolPath = ffmpegPath;
                            
                            string qualityArgs = "";
                            if (qualityLevel == 1) qualityArgs = "-crf 28 -preset fast";      // Small
                            else if (qualityLevel == 2) qualityArgs = "-crf 23 -preset medium"; // Balanced
                            else if (qualityLevel == 3) qualityArgs = "-crf 18 -preset slow";   // High

                            if (targetFormat == "gif")
                            {
                                arguments = $"-i \"{file}\" -vf \"fps=10,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -loop 0 -y \"{newFileName}\"";
                            }
                            else
                            {
                                arguments = $"-i \"{file}\" {qualityArgs} -y \"{newFileName}\"";
                            }
                        }
                        else
                        {
                            // Magick
                            currentToolPath = magickPath;
                            
                            string qualityArgs = "";
                            if (qualityLevel == 1) qualityArgs = "-quality 40 -resize 80%"; // Small
                            else if (qualityLevel == 2) qualityArgs = "-quality 75";        // Balanced
                            else if (qualityLevel == 3) qualityArgs = "-quality 100";       // High

                            // Advanced Image Settings
                            StringBuilder advancedArgs = new StringBuilder();
                            
                            // Resize logic
                            if (!string.IsNullOrWhiteSpace(resizeW) && !string.IsNullOrWhiteSpace(resizeH))
                            {
                                advancedArgs.Append($" -resize {resizeW}x{resizeH}");
                                if (!maintainAspect) advancedArgs.Append("!");
                                advancedArgs.Append(" ");
                                
                                // Override default quality resize if custom resize is set
                                if (qualityLevel == 1) qualityArgs = qualityArgs.Replace("-resize 80%", "");
                            }
                            
                            // DPI
                            if (!string.IsNullOrWhiteSpace(dpi))
                            {
                                advancedArgs.Append($" -density {dpi} -units PixelsPerInch ");
                            }
                            
                            // Effects
                            if (sharpen) advancedArgs.Append("-sharpen 0x1 ");
                            if (grayscale) advancedArgs.Append("-colorspace Gray ");
                            if (autoEnhance) advancedArgs.Append("-normalize -auto-level ");

                            arguments = $"\"{file}\" {qualityArgs} {advancedArgs} ";
                            if (targetFormat == "ico")
                            {
                                arguments += "-resize 256x256 ";
                            }
                            arguments += $"\"{newFileName}\"";
                        }

                        // Check if tool exists
                        if (!File.Exists(currentToolPath))
                        {
                            throw new FileNotFoundException($"Tool not found: {System.IO.Path.GetFileName(currentToolPath)}");
                        }

                        RunExternalProcess(currentToolPath, arguments);
                    }
                    catch (Exception ex)
                    {
                         Dispatcher.Invoke(() => 
                         {
                             StatusText.Text = $"Error: {System.IO.Path.GetFileName(file)}";
                             MessageBox.Show($"Error converting {System.IO.Path.GetFileName(file)}:\n\n{ex.Message}", "Conversion Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                         });
                    }
                    
                    count++;
                    Dispatcher.Invoke(() => ConversionProgress.Value = count);
                }
            });

            StatusText.Text = "Conversion Complete!";
            SuccessOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Critical Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
}
