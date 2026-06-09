using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CortexFX.Models;

/// <summary>
/// Represents a file in the conversion queue with observable status tracking.
/// Moved from MainWindow.xaml.cs to enforce proper separation of concerns.
/// </summary>
public class FileModel : INotifyPropertyChanged
{
    private string _status = "Pending";
    private string _statusColor = "#AAAAAA"; // Gray for Pending

    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string FileDetails { get; set; } = string.Empty;
    public string FileIcon { get; set; } = "\uE8A5";
    public string? ThumbnailPath { get; set; }

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
