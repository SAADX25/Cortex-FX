using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Video;

public enum TimelineTrackKind
{
    Video = 0,
    Audio = 1
}

/// <summary>Single clip placed on the multi-track NLE montage timeline (V1..V3 video or A1..A4 audio).</summary>
public sealed class MontageClipItem : INotifyPropertyChanged
{
    private int _orderIndex;
    private TimeSpan _start;
    private TimeSpan _end;
    private TimeSpan _timelineStart;
    private TimeSpan _timelineEnd;
    private ImageSource? _thumbnail;
    private bool _isSelected;
    private double _audioVolume = 1.0;
    private double _videoScale = 1.0;
    private double _videoOffsetX;
    private double _videoOffsetY;
    private double _videoOpacity = 1.0;
    private TimelineTrackKind _trackKind;
    private int _trackLayer;
    private Guid? _linkedClipId;

    public MontageClipItem(
        string sourcePath,
        string sourceFileName,
        TimeSpan sourceDuration,
        TimeSpan start,
        TimeSpan end,
        int sourceWidth,
        int sourceHeight,
        double sourceFps,
        bool hasAudio,
        string aspectRatioLabel,
        string colorHex,
        Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        SourcePath = sourcePath;
        SourceFileName = sourceFileName;
        SourceDuration = sourceDuration;
        _start = start;
        _end = end;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        SourceFps = sourceFps;
        HasAudio = hasAudio;
        AspectRatioLabel = aspectRatioLabel;
        ColorHex = colorHex;
    }

    public Guid Id { get; }
    public string SourcePath { get; }
    public string SourceFileName { get; }
    public TimeSpan SourceDuration { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public double SourceFps { get; set; }
    public bool HasAudio { get; set; }
    public string AspectRatioLabel { get; set; }
    public string ColorHex { get; }

    public TimelineTrackKind TrackKind
    {
        get => _trackKind;
        set
        {
            if (_trackKind == value) return;
            _trackKind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAudioClip));
            OnPropertyChanged(nameof(IsVideoClip));
            OnPropertyChanged(nameof(IsAudioOverlay));
            OnPropertyChanged(nameof(IsVideoOverlay));
            OnPropertyChanged(nameof(TrackLabel));
        }
    }

    /// <summary>Zero-based track index: Video (0=V1, 1=V2, 2=V3) or Audio (0=A1, 1=A2, 2=A3, 3=A4 Music).</summary>
    public int TrackLayer
    {
        get => _trackLayer;
        set
        {
            int maxLayer = _trackKind == TimelineTrackKind.Video
                ? MontageProject.MaxVideoLayers - 1
                : MontageProject.MaxAudioTracks - 1;
            int clamped = Math.Clamp(value, 0, maxLayer);
            if (_trackLayer == clamped) return;
            _trackLayer = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVideoOverlay));
            OnPropertyChanged(nameof(TrackLabel));
        }
    }

    public Guid? LinkedClipId
    {
        get => _linkedClipId;
        set
        {
            if (_linkedClipId == value) return;
            _linkedClipId = value;
            OnPropertyChanged();
        }
    }

    public bool IsVideoClip => _trackKind == TimelineTrackKind.Video;
    public bool IsAudioClip => _trackKind == TimelineTrackKind.Audio;
    public bool IsAudioOverlay => _trackKind == TimelineTrackKind.Audio;
    public bool IsVideoOverlay => _trackKind == TimelineTrackKind.Video && _trackLayer > 0;
    public string TrackLabel => _trackKind == TimelineTrackKind.Video ? $"V{_trackLayer + 1}" : $"A{_trackLayer + 1}";

    public int OrderIndex
    {
        get => _orderIndex;
        set
        {
            if (_orderIndex == value) return;
            _orderIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(DisplayBadge));
        }
    }

    public TimeSpan Start
    {
        get => _start;
        set
        {
            if (_start == value) return;
            _start = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(IsTrimmed));
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(DisplayRange));
            OnPropertyChanged(nameof(DisplayDuration));
            OnPropertyChanged(nameof(DisplayBadge));
        }
    }

    public TimeSpan End
    {
        get => _end;
        set
        {
            if (_end == value) return;
            _end = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(IsTrimmed));
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(DisplayRange));
            OnPropertyChanged(nameof(DisplayDuration));
            OnPropertyChanged(nameof(DisplayBadge));
        }
    }

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
    public bool IsTrimmed => Start > TimeSpan.FromMilliseconds(20) ||
                             (SourceDuration > TimeSpan.Zero && End < SourceDuration - TimeSpan.FromMilliseconds(20));

    public TimeSpan TimelineStart
    {
        get => _timelineStart;
        set
        {
            if (_timelineStart == value) return;
            _timelineStart = value;
            OnPropertyChanged();
        }
    }

    public TimeSpan TimelineEnd
    {
        get => _timelineEnd;
        set
        {
            if (_timelineEnd == value) return;
            _timelineEnd = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Per-clip linear audio volume multiplier (0.0 = mute, 1.0 = 0 dB, 2.0 = +6 dB).</summary>
    public double AudioVolume
    {
        get => _audioVolume;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 2.0);
            if (Math.Abs(_audioVolume - clamped) < 0.0005) return;
            _audioVolume = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioVolumeDbLabel));
            OnPropertyChanged(nameof(IsAudioAdjusted));
        }
    }

    public bool IsAudioAdjusted => Math.Abs(_audioVolume - 1.0) > 0.015;
    public string AudioVolumeDbLabel => MontageProject.FormatVolumeDb(_audioVolume);

    public double VideoScale
    {
        get => _videoScale;
        set
        {
            double clamped = Math.Clamp(value, 0.15, 3.0);
            if (Math.Abs(_videoScale - clamped) < 0.0005) return;
            _videoScale = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomTransform));
            OnPropertyChanged(nameof(ScalePercentLabel));
        }
    }

    public double VideoOffsetX
    {
        get => _videoOffsetX;
        set
        {
            double clamped = Math.Clamp(value, -1.5, 1.5);
            if (Math.Abs(_videoOffsetX - clamped) < 0.0005) return;
            _videoOffsetX = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomTransform));
        }
    }

    public double VideoOffsetY
    {
        get => _videoOffsetY;
        set
        {
            double clamped = Math.Clamp(value, -1.5, 1.5);
            if (Math.Abs(_videoOffsetY - clamped) < 0.0005) return;
            _videoOffsetY = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomTransform));
        }
    }

    public double VideoOpacity
    {
        get => _videoOpacity;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_videoOpacity - clamped) < 0.0005) return;
            _videoOpacity = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomTransform));
        }
    }

    public bool HasCustomTransform =>
        Math.Abs(_videoScale - 1.0) > 0.005 ||
        Math.Abs(_videoOffsetX) > 0.003 ||
        Math.Abs(_videoOffsetY) > 0.003 ||
        Math.Abs(_videoOpacity - 1.0) > 0.005;

    public string ScalePercentLabel => $"{Math.Round(_videoScale * 100)}%";

    public string DisplayTitle => $"#{OrderIndex} · {SourceFileName}";
    public string DisplayBadge => $"#{OrderIndex} · {MontageProject.FormatShort(Duration)}";
    public string DisplayDuration => MontageProject.FormatShort(Duration);
    public string DisplayRange => $"{MontageProject.FormatSmpteTimecode(Start, SourceFps)} → {MontageProject.FormatSmpteTimecode(End, SourceFps)} · {DisplayResolution}";
    public string DisplayResolution => SourceWidth > 0 && SourceHeight > 0 ? $"{SourceWidth}×{SourceHeight}" : AspectRatioLabel;

    public VideoMontageClip ToExportClip(bool? overrideHasAudio = null, double? overrideVolume = null) =>
        new(SourcePath, Start, End, overrideHasAudio ?? HasAudio, SourceWidth, SourceHeight, SourceFps,
            overrideVolume ?? AudioVolume, TimelineStart, VideoScale, VideoOffsetX, VideoOffsetY, VideoOpacity);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed record ClipSnapshot(
    Guid ClipId,
    Guid? LinkedClipId,
    string SourcePath,
    TimeSpan Start,
    TimeSpan End,
    double AudioVolume,
    TimeSpan TimelineStart,
    TimelineTrackKind TrackKind,
    int TrackLayer,
    double VideoScale,
    double VideoOffsetX,
    double VideoOffsetY,
    double VideoOpacity);

internal sealed record ProjectSnapshot(
    IReadOnlyList<ClipSnapshot>[] VideoTracks,
    IReadOnlyList<ClipSnapshot>[] AudioTracks,
    TimelineTrackKind SelectedTrackKind,
    int SelectedTrackLayer,
    int SelectedIndex);
