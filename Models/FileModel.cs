using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using CortexFX.Core.Constants;

namespace CortexFX.Models;

/// <summary>
/// One file in the conversion list (name, path, status text/color).
/// </summary>
public class FileModel : INotifyPropertyChanged
{
    private string _status = "Pending";
    private string _statusColor = "#AAAAAA"; // pending = gray

    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string FileDetails { get; set; } = string.Empty;
    public string FileIcon { get; set; } = "\uE8A5";
    public string? ThumbnailPath { get; set; }

    /// <summary>Compact, color-coded type badge for the conversion file list.</summary>
    public string FileBadgeText
    {
        get
        {
            string extension = Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant();
            return extension.Length > 4 ? extension[..4] : extension.Length > 0 ? extension : "FILE";
        }
    }

    public string FileBadgeColor
    {
        get
        {
            string extension = Path.GetExtension(FullPath).ToLowerInvariant();
            if (extension == ".pdf") return "#B93143";
            if (extension is ".doc" or ".docx" or ".odt" or ".rtf") return "#2861AD";
            if (extension is ".xls" or ".xlsx") return "#278159";
            if (extension is ".ppt" or ".pptx") return "#C36936";
            if (MediaTypes.DocumentExtensions.Contains(extension)) return "#2861AD";
            if (MediaTypes.ImageExtensions.Contains(extension)) return "#247EAC";
            if (MediaTypes.VideoExtensions.Contains(extension)) return "#7453A9";
            if (MediaTypes.AudioExtensions.Contains(extension)) return "#188B82";
            if (MediaTypes.ArchiveExtensions.Contains(extension)) return "#997229";
            if (MediaTypes.EbookExtensions.Contains(extension)) return "#688342";
            return "#586474";
        }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
