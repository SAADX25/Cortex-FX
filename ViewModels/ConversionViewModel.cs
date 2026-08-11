using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CortexFX.Core.Configuration;
using CortexFX.Core.Constants;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Documents;
using CortexFX.Models;

namespace CortexFX.ViewModels;

/// <summary>
/// Conversion screen state: file list, format, quality, progress, Convert.
/// </summary>
public partial class ConversionViewModel : ObservableObject
{
    private readonly IConversionRouter _router;
    private readonly ConversionRouter _routerConcrete;
    private readonly IMagickService _magickService;
    private CancellationTokenSource? _cts;

    public ConversionViewModel(IConversionRouter router, IMagickService magickService)
    {
        _router = router;
        _routerConcrete = (router as ConversionRouter)!;
        _magickService = magickService;
    }

    /// <summary>Queued input files.</summary>
    public ObservableCollection<FileModel> Files { get; } = [];

    /// <summary>Formats the UI can offer for the current files.</summary>
    public ObservableCollection<string> AvailableFormats { get; } = [];

    [ObservableProperty]
    private string? _selectedFormat;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private double _qualityLevel = 75;

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private double _progressMaximum = 100;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isConverting;

    [ObservableProperty]
    private bool _showSuccessOverlay;

    [ObservableProperty]
    private string _successMessage = string.Empty;

    [ObservableProperty]
    private bool _createSubfolder = true;

    [ObservableProperty]
    private bool _mergePdf;

    [ObservableProperty]
    private bool _showMergePdfOption;

    [ObservableProperty]
    private string? _smartSuggestionText;

    // Advanced image options
    [ObservableProperty]
    private string _resizeWidth = string.Empty;

    [ObservableProperty]
    private string _resizeHeight = string.Empty;

    [ObservableProperty]
    private bool _maintainAspectRatio = true;

    [ObservableProperty]
    private string _dpi = "300";

    [ObservableProperty]
    private bool _sharpen;

    [ObservableProperty]
    private bool _grayscale;

    [ObservableProperty]
    private bool _autoEnhance;

    /// <summary>Category filter; null means mixed / universal.</summary>
    [ObservableProperty]
    private string? _categoryFilter;

    // Commands

    [RelayCommand]
    private void AddFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!MediaTypes.AllSupportedExtensions.Contains(ext)) return;

        if (Files.Any(f => f.FullPath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
            return;

        Files.Add(new FileModel
        {
            FileName = Path.GetFileName(filePath),
            FullPath = filePath
        });

        RefreshAvailableFormats();
        UpdateSmartSuggestion();
    }

    public void AddFiles(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            AddFile(path);
        }
    }

    [RelayCommand]
    private void RemoveFile(FileModel file)
    {
        Files.Remove(file);
        RefreshAvailableFormats();
    }

    [RelayCommand]
    private void ClearFiles()
    {
        Files.Clear();
        AvailableFormats.Clear();
        SelectedFormat = null;
        SmartSuggestionText = null;
    }

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAsync()
    {
        if (Files.Count == 0 || string.IsNullOrWhiteSpace(OutputPath) || SelectedFormat == null)
            return;

        IsConverting = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        string targetFormat = SelectedFormat.ToLowerInvariant();
        int successCount = 0;
        int errorCount = 0;

        OverallProgress = 0;
        ProgressMaximum = Files.Count * 100;
        StatusText = "Converting...";

        try
        {
            // Check merge mode
            bool shouldMerge = MergePdf && targetFormat == "pdf" && Files.Count > 1
                && Files.All(f => MediaTypes.RasterImageExtensions.Contains(
                    Path.GetExtension(f.FullPath).ToLowerInvariant()));

            if (shouldMerge)
            {
                await HandleMergeModeAsync(ct);
                return;
            }

            // Build jobs
            var jobs = Files.Select(f => new ConversionJob
            {
                InputPath = f.FullPath,
                OutputDirectory = string.IsNullOrWhiteSpace(OutputPath)
                    ? (Path.GetDirectoryName(f.FullPath) ?? "")
                    : OutputPath,
                TargetFormat = targetFormat,
                QualityLevel = QualityLevel,
                CreateSubfolder = CreateSubfolder,
                ImageOptions = BuildImageOptions()
            }).ToList();

            // Process each file with progress tracking
            int processedFiles = 0;
            foreach (var (job, file) in jobs.Zip(Files))
            {
                if (ct.IsCancellationRequested) break;

                file.Status = "Processing...";
                file.StatusColor = "#E11D2E";
                StatusText = $"Converting {Path.GetFileName(job.InputPath)}...";

                var fileProgress = new Progress<double>(p =>
                {
                    OverallProgress = (processedFiles * 100) + p;
                });

                var result = await _router.ConvertAsync(job, ct, fileProgress);

                if (result.Success)
                {
                    file.Status = "Done";
                    file.StatusColor = "#E11D2E";
                    successCount++;
                }
                else
                {
                    file.Status = "Error";
                    file.StatusColor = "#FF4D5E";
                    errorCount++;
                }

                processedFiles++;
                OverallProgress = processedFiles * 100;
            }

            // Show results
            SuccessMessage = errorCount > 0
                ? $"Converted {successCount}/{Files.Count} files. ({errorCount} failed)"
                : $"Successfully converted {successCount} files.";
            ShowSuccessOverlay = true;
            StatusText = "Conversion Complete!";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsConverting = false;
        }
    }

    private bool CanConvert() =>
        Files.Count > 0 && !string.IsNullOrWhiteSpace(OutputPath) && SelectedFormat != null && !IsConverting;

    [RelayCommand]
    private void CancelConversion()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    [RelayCommand]
    private void DismissSuccess()
    {
        ShowSuccessOverlay = false;
        StatusText = "Ready";
        OverallProgress = 0;
    }

    // Formats

    /// <summary>
    /// Rebuild the format list from the current files / category filter.
    /// </summary>
    public void RefreshAvailableFormats()
    {
        AvailableFormats.Clear();

        if (Files.Count == 0) return;

        // Determine category from filter or from files
        string? category = CategoryFilter;
        if (category == null && Files.Count > 0)
        {
            // Auto-detect: use the category of the first file
            string ext = Path.GetExtension(Files[0].FullPath);
            category = MediaTypes.GetCategory(ext);
        }

        var formats = GetFormatsForCategory(category ?? "Document");
        foreach (var fmt in formats)
        {
            AvailableFormats.Add(fmt);
        }

        // Additional smart formats for documents
        if (category == "Document")
        {
            AddSmartDocumentFormats();
        }

        if (AvailableFormats.Count > 0 && SelectedFormat == null)
        {
            SelectedFormat = AvailableFormats[0];
        }

        // Update merge PDF visibility
        ShowMergePdfOption = SelectedFormat?.Equals("PDF", StringComparison.OrdinalIgnoreCase) == true;

        ConvertCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFormatChanged(string? value)
    {
        ShowMergePdfOption = value?.Equals("PDF", StringComparison.OrdinalIgnoreCase) == true;
        UpdateSmartSuggestion();
    }

    partial void OnOutputPathChanged(string value)
    {
        ConvertCommand.NotifyCanExecuteChanged();
    }

    // Format hints from the first file

    private void UpdateSmartSuggestion()
    {
        if (Files.Count == 0 || SelectedFormat == null)
        {
            SmartSuggestionText = null;
            return;
        }

        // Check the first file for smart suggestions
        var suggestion = _routerConcrete?.GetSmartSuggestion(Files[0].FullPath, SelectedFormat);
        SmartSuggestionText = suggestion != null
            ? $"💡 {suggestion.Warning} {suggestion.Recommendation}"
            : null;
    }

    // Private Helpers

    private static IReadOnlyList<string> GetFormatsForCategory(string category)
    {
        return category switch
        {
            "Image" => new[] { "JPG", "PNG", "WEBP", "BMP", "ICO", "TIFF", "GIF", "HEIC", "PDF" },
            "Video" => new[] { "MP4", "AVI", "MKV", "MOV", "GIF", "WEBM" },
            "Audio" => new[] { "MP3", "WAV", "AAC", "FLAC", "M4A", "OGG" },
            "Document" => new[] { "PDF", "DOCX", "ODT", "RTF", "TXT" },
            "Archive" => new[] { "ZIP", "7Z", "TAR" },
            "Ebook" => new[] { "EPUB", "MOBI", "AZW3", "PDF" },
            _ => Array.Empty<string>()
        };
    }

    private void AddSmartDocumentFormats()
    {
        var uniqueFormats = new HashSet<string>(AvailableFormats, StringComparer.OrdinalIgnoreCase);

        bool hasPdf = Files.Any(f => MediaTypes.PdfExtensions.Contains(Path.GetExtension(f.FullPath)));
        bool hasWord = Files.Any(f => MediaTypes.WordExtensions.Contains(Path.GetExtension(f.FullPath)));
        bool hasPpt = Files.Any(f => MediaTypes.PowerPointExtensions.Contains(Path.GetExtension(f.FullPath)));

        void TryAdd(string fmt)
        {
            if (uniqueFormats.Add(fmt)) AvailableFormats.Add(fmt);
        }

        if (hasPdf) { TryAdd("DOCX"); TryAdd("PPTX"); TryAdd("EPUB"); TryAdd("MOBI"); TryAdd("AZW3"); }
        if (hasWord) TryAdd("PPTX");
        if (hasPpt) TryAdd("DOCX");
    }

    private ImageConversionOptions BuildImageOptions()
    {
        int? w = int.TryParse(ResizeWidth, out int rw) ? rw : null;
        int? h = int.TryParse(ResizeHeight, out int rh) ? rh : null;
        int? d = int.TryParse(Dpi, out int dp) ? dp : null;

        return new ImageConversionOptions(
            Quality: (int)QualityLevel,
            ResizeWidth: w,
            ResizeHeight: h,
            MaintainAspectRatio: MaintainAspectRatio,
            Dpi: d,
            Sharpen: Sharpen,
            Grayscale: Grayscale,
            AutoEnhance: AutoEnhance);
    }

    private async Task HandleMergeModeAsync(CancellationToken ct)
    {
        StatusText = "Merging all images...";
        foreach (var f in Files) { f.Status = "Merging..."; f.StatusColor = "#F59E0B"; }

        string outputFolder = string.IsNullOrWhiteSpace(OutputPath)
            ? (Path.GetDirectoryName(Files[0].FullPath) ?? "")
            : OutputPath;

        if (CreateSubfolder)
        {
            outputFolder = Path.Combine(outputFolder, "Cortex FX");
            Directory.CreateDirectory(outputFolder);
        }

        string outputName = $"Merged_Images_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
        string finalPath = Path.Combine(outputFolder, outputName);

        try
        {
            var imagePaths = Files.Select(f => f.FullPath).ToList();
            await _magickService.MergeImagesToPdfAsync(imagePaths, finalPath, ct);

            foreach (var f in Files) { f.Status = "Merged!"; f.StatusColor = "#E11D2E"; }
            SuccessMessage = $"Successfully merged {Files.Count} images.";
        }
        catch
        {
            foreach (var f in Files) { f.Status = "Error"; f.StatusColor = "#FF4D5E"; }
            SuccessMessage = "Merge failed.";
        }

        OverallProgress = ProgressMaximum;
        StatusText = "Merge Complete!";
        ShowSuccessOverlay = true;
        IsConverting = false;
    }
}
