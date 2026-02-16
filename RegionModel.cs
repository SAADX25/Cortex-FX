using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace CortexFX;

public class RegionModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public Brush Stroke { get; set; }
    public Brush Fill { get; set; }

    /// <summary>Whether this region is currently selected on the canvas.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>Start time for temporal masking. Default: TimeSpan.Zero (beginning of video).</summary>
    public TimeSpan StartTime { get; set; } = TimeSpan.Zero;

    /// <summary>End time for temporal masking. Default: TimeSpan.MaxValue (entire video).</summary>
    public TimeSpan EndTime { get; set; } = TimeSpan.MaxValue;

    /// <summary>Formatted time range for display in the overlay.</summary>
    public string TimeLabel =>
        EndTime == TimeSpan.MaxValue
            ? $"{StartTime:hh\\:mm\\:ss\\.f} → END"
            : $"{StartTime:hh\\:mm\\:ss\\.f} → {EndTime:hh\\:mm\\:ss\\.f}";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
