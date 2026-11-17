using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Video;

public sealed record FilmstripFrame(double PositionSeconds, ImageSource Image);

/// <summary>Cached metadata, filmstrip thumbnails, and audio waveform peaks for a source video or audio file in the Project Bin.</summary>
public sealed class SourceMediaAsset : INotifyPropertyChanged
{
    private int _binOrderIndex;
    private ImageSource? _binThumbnail;
    private bool _isSelected;
    private VideoMediaInfo _info;

    public SourceMediaAsset(VideoMediaInfo info, string colorHex)
    {
        _info = info;
        ColorHex = colorHex;
    }

    public VideoMediaInfo Info
    {
        get => _info;
        set
        {
            _info = value;
            NotifyInfoChanged();
        }
    }

    public string FilePath => _info.FilePath;
    public string FileName => Path.GetFileName(_info.FilePath);
    public string ColorHex { get; }
    public List<FilmstripFrame> Frames { get; } = new();
    public float[]? AudioPeaks { get; set; }

    public int BinOrderIndex
    {
        get => _binOrderIndex;
        set
        {
            if (_binOrderIndex == value) return;
            _binOrderIndex = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? BinThumbnail
    {
        get => _binThumbnail;
        set
        {
            if (ReferenceEquals(_binThumbnail, value)) return;
            _binThumbnail = value;
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

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".alac"
    };

    public bool HasVideo => _info.HasVideo && !AudioExtensions.Contains(Path.GetExtension(_info.FilePath));
    public bool HasAudio => _info.HasAudio || AudioExtensions.Contains(Path.GetExtension(_info.FilePath));
    public bool IsAudioOnly => !HasVideo && HasAudio;
    public string FormatExtension
    {
        get
        {
            string ext = Path.GetExtension(_info.FilePath).TrimStart('.');
            return string.IsNullOrEmpty(ext) ? "MEDIA" : ext.ToUpperInvariant();
        }
    }
    public string AudioAccentHex => FormatExtension switch
    {
        "MP3" => "#34D399",
        "WAV" or "AIFF" => "#38BDF8",
        "FLAC" or "ALAC" => "#A78BFA",
        "AAC" or "M4A" => "#FBBF24",
        "OGG" or "OPUS" or "WMA" => "#F472B6",
        _ => "#5EEAD4"
    };
    public string AudioSymbolGlyph => FormatExtension switch
    {
        "MP3" => "\uEC4F",         // Headphones / Music MP3
        "WAV" or "AIFF" => "\uE93E", // Waveform bars Lossless WAV
        "FLAC" or "ALAC" => "\uE8D6",// Vinyl / Hi-Res Audio
        "AAC" or "M4A" => "\uE90B",  // Broadcast / AAC Audio
        _ => "\uE8D6"
    };
    public string DisplayDuration => MontageProject.FormatShort(_info.Duration);
    public string MediaBadgeLabel => IsAudioOnly ? FormatExtension : (HasAudio ? FormatExtension : "SILENT");
    public string DisplaySpec => IsAudioOnly
        ? $"{FormatExtension} Audio"
        : (_info.DisplayWidth > 0 && _info.DisplayHeight > 0 ? $"{_info.DisplayWidth}×{_info.DisplayHeight}" : _info.AspectRatioLabel);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void NotifyInfoChanged()
    {
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(HasAudio));
        OnPropertyChanged(nameof(IsAudioOnly));
        OnPropertyChanged(nameof(FormatExtension));
        OnPropertyChanged(nameof(AudioAccentHex));
        OnPropertyChanged(nameof(AudioSymbolGlyph));
        OnPropertyChanged(nameof(DisplayDuration));
        OnPropertyChanged(nameof(MediaBadgeLabel));
        OnPropertyChanged(nameof(DisplaySpec));
    }

    public ImageSource? FindNearestFrame(double seconds) =>
        FindNearestFrame(seconds, double.MaxValue);

    public ImageSource? FindNearestFrame(double seconds, double maxDistanceSeconds)
    {
        if (Frames.Count == 0) return null;
        int idx = LowerBound(seconds);
        ImageSource? best = null;
        double bestDist = double.MaxValue;

        if (idx < Frames.Count)
        {
            double d = Math.Abs(Frames[idx].PositionSeconds - seconds);
            if (d < bestDist)
            {
                bestDist = d;
                best = Frames[idx].Image;
            }
        }
        if (idx > 0)
        {
            double d = Math.Abs(Frames[idx - 1].PositionSeconds - seconds);
            if (d < bestDist)
            {
                bestDist = d;
                best = Frames[idx - 1].Image;
            }
        }
        return bestDist <= maxDistanceSeconds ? best : null;
    }

    public bool HasFrameNear(double seconds, double toleranceSeconds)
    {
        if (Frames.Count == 0) return false;
        int idx = LowerBound(seconds);
        if (idx < Frames.Count && Math.Abs(Frames[idx].PositionSeconds - seconds) <= toleranceSeconds)
            return true;
        if (idx > 0 && Math.Abs(Frames[idx - 1].PositionSeconds - seconds) <= toleranceSeconds)
            return true;
        return false;
    }

    public void AddOrUpdateFrame(FilmstripFrame frame, double replaceToleranceSeconds = 0.005)
    {
        int idx = LowerBound(frame.PositionSeconds);
        if (idx < Frames.Count && Math.Abs(Frames[idx].PositionSeconds - frame.PositionSeconds) <= replaceToleranceSeconds)
        {
            Frames[idx] = frame;
            return;
        }
        if (idx > 0 && Math.Abs(Frames[idx - 1].PositionSeconds - frame.PositionSeconds) <= replaceToleranceSeconds)
        {
            Frames[idx - 1] = frame;
            return;
        }

        Frames.Insert(idx, frame);
        if (BinThumbnail == null && Info.HasVideo)
        {
            double mid = Info.Duration > TimeSpan.Zero ? Info.Duration.TotalSeconds * 0.5 : 0.0;
            BinThumbnail = FindNearestFrame(mid) ?? frame.Image;
        }
    }

    public void TrimFrameCache(int maxFrames, double focusCenterSeconds)
    {
        if (Frames.Count <= maxFrames || maxFrames < 32) return;

        // Preserve evenly spaced anchor frames so zoomed-out views stay populated,
        // and keep dense frames nearest the active viewport focus.
        int anchorStep = Math.Max(1, Frames.Count / 36);
        var keep = new HashSet<int>();
        for (int i = 0; i < Frames.Count; i += anchorStep)
            keep.Add(i);

        var candidates = new List<(int Index, double Dist)>(Frames.Count);
        for (int i = 0; i < Frames.Count; i++)
        {
            if (!keep.Contains(i))
                candidates.Add((i, Math.Abs(Frames[i].PositionSeconds - focusCenterSeconds)));
        }
        candidates.Sort((a, b) => a.Dist.CompareTo(b.Dist));

        int remaining = Math.Max(0, maxFrames - keep.Count);
        for (int i = 0; i < remaining && i < candidates.Count; i++)
            keep.Add(candidates[i].Index);

        var trimmed = new List<FilmstripFrame>(keep.Count);
        for (int i = 0; i < Frames.Count; i++)
        {
            if (keep.Contains(i))
                trimmed.Add(Frames[i]);
        }
        Frames.Clear();
        Frames.AddRange(trimmed);
    }

    private int LowerBound(double seconds)
    {
        int lo = 0;
        int hi = Frames.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (Frames[mid].PositionSeconds < seconds)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    public static (double[] Wave1, double[] Wave2, double Wave1SpacingSeconds) BuildProgressiveSampleWaves(double durationSeconds)
    {
        double safeDuration = Math.Max(0.2, durationSeconds);
        int raw = Math.Clamp((int)Math.Ceiling(safeDuration / 2.0), 16, 28);
        int totalCount = raw % 2 == 0 ? raw : raw + 1;
        double safeMax = safeDuration > 0.5
            ? safeDuration - 0.20
            : Math.Max(0.02, safeDuration * 0.85);

        int half = totalCount / 2;
        var wave1 = new double[half];
        var wave2 = new double[half];
        for (int i = 0; i < totalCount; i++)
        {
            double pos = Math.Clamp(safeDuration * ((i + 0.5) / totalCount), 0.0, safeMax);
            if ((i & 1) == 0)
                wave1[i >> 1] = pos;
            else
                wave2[i >> 1] = pos;
        }

        return (wave1, wave2, safeDuration / half);
    }
}

/// <summary>Single clip placed on the sequential montage timeline.</summary>
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
        string colorHex)
    {
        Id = Guid.NewGuid();
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

    public bool IsAudioOverlay { get; set; }
    public bool IsVideoOverlay { get; set; }

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

    public VideoMontageClip ToExportClip() =>
        new(SourcePath, Start, End, HasAudio, SourceWidth, SourceHeight, SourceFps, AudioVolume, TimelineStart,
            VideoScale, VideoOffsetX, VideoOffsetY, VideoOpacity);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Pure NLE montage sequence state: multi-source asset registry, sequential clips,
/// non-destructive splitting, trimming, reordering, and montage-to-source time mapping.
/// </summary>
public sealed class MontageProject
{
    public static readonly TimeSpan MinimumClipDuration = TimeSpan.FromMilliseconds(60);

    private static readonly string[] AssetPalette =
    {
        "#38BDF8", "#34D399", "#A78BFA", "#FBBF24", "#F472B6", "#60A5FA"
    };

    private sealed record ClipSnapshot(
        string SourcePath,
        TimeSpan Start,
        TimeSpan End,
        double AudioVolume,
        TimeSpan TimelineStart,
        bool IsAudioOverlay,
        bool IsVideoOverlay,
        double VideoScale,
        double VideoOffsetX,
        double VideoOffsetY,
        double VideoOpacity);
    private sealed record ProjectSnapshot(
        IReadOnlyList<ClipSnapshot> Clips,
        IReadOnlyList<ClipSnapshot> AudioOverlayClips,
        IReadOnlyList<ClipSnapshot> VideoOverlayClips,
        int SelectedTrackKind,
        int SelectedIndex);

    private readonly Dictionary<string, SourceMediaAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProjectSnapshot> _undoHistory = new();
    private readonly List<ProjectSnapshot> _redoHistory = new();
    private ProjectSnapshot? _pendingUndoCheckpoint;
    private const int MaxUndoSteps = 50;

    public ObservableCollection<SourceMediaAsset> MediaBin { get; } = new();
    public ObservableCollection<MontageClipItem> Clips { get; } = new();
    public ObservableCollection<MontageClipItem> VideoOverlayClips { get; } = new();
    public ObservableCollection<MontageClipItem> AudioOverlayClips { get; } = new();
    public IReadOnlyDictionary<string, SourceMediaAsset> Assets => _assets;
    public MontageClipItem? SelectedClip { get; private set; }
    public TimeSpan TotalDuration { get; private set; }
    public double SequenceFps
    {
        get
        {
            if (SelectedClip is { SourceFps: > 1 }) return SelectedClip.SourceFps;
            MontageClipItem? first = Clips.FirstOrDefault(c => c.SourceFps > 1) ?? VideoOverlayClips.FirstOrDefault(c => c.SourceFps > 1);
            return first?.SourceFps ?? 30.0;
        }
    }
    public bool CanUndo => _undoHistory.Count > 0;
    public bool CanRedo => _redoHistory.Count > 0;

    public event EventHandler? HistoryChanged;

    public SourceMediaAsset RegisterAsset(VideoMediaInfo info)
    {
        if (_assets.TryGetValue(info.FilePath, out SourceMediaAsset? existing))
        {
            existing.Info = info;
            return existing;
        }

        string color = AssetPalette[_assets.Count % AssetPalette.Length];
        var asset = new SourceMediaAsset(info, color);
        _assets[info.FilePath] = asset;
        MediaBin.Add(asset);
        RenumberMediaBin();
        return asset;
    }

    public bool RemoveAsset(SourceMediaAsset asset, bool removeTimelineInstances = true)
    {
        if (!_assets.Remove(asset.FilePath))
            return false;

        MediaBin.Remove(asset);
        RenumberMediaBin();

        if (removeTimelineInstances)
        {
            ProjectSnapshot? checkpoint = (Clips.Count > 0 || VideoOverlayClips.Count > 0 || AudioOverlayClips.Count > 0)
                ? CaptureSnapshot()
                : null;
            bool removedAny = false;
            void RemoveFrom(ObservableCollection<MontageClipItem> track)
            {
                for (int i = track.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(track[i].SourcePath, asset.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        track.RemoveAt(i);
                        removedAny = true;
                    }
                }
            }
            RemoveFrom(Clips);
            RemoveFrom(VideoOverlayClips);
            RemoveFrom(AudioOverlayClips);

            if (removedAny)
            {
                RecalculateTimeline();
                if (SelectedClip != null && !Clips.Contains(SelectedClip) && !VideoOverlayClips.Contains(SelectedClip) && !AudioOverlayClips.Contains(SelectedClip))
                    SelectClip(Clips.FirstOrDefault() ?? VideoOverlayClips.FirstOrDefault() ?? AudioOverlayClips.FirstOrDefault());
                if (checkpoint != null)
                    PushUndoSnapshot(checkpoint);
            }
        }

        return true;
    }

    private void RenumberMediaBin()
    {
        for (int i = 0; i < MediaBin.Count; i++)
            MediaBin[i].BinOrderIndex = i + 1;
    }

    public MontageClipItem AddClipFromAsset(
        SourceMediaAsset asset,
        TimeSpan? start = null,
        TimeSpan? end = null,
        bool select = true,
        bool recordUndo = false)
    {
        ObservableCollection<MontageClipItem> targetList = asset.IsAudioOnly ? AudioOverlayClips : Clips;
        TimeSpan appendAt = targetList.Count > 0 ? targetList[^1].TimelineEnd : TimeSpan.Zero;
        return AddClipAtTime(asset, appendAt, start, end, select, recordUndo, targetAudioOverlay: asset.IsAudioOnly);
    }

    public MontageClipItem AddClipAtTime(
        SourceMediaAsset asset,
        TimeSpan desiredTimelineStart,
        TimeSpan? start = null,
        TimeSpan? end = null,
        bool select = true,
        bool recordUndo = false,
        bool targetAudioOverlay = false,
        bool targetVideoOverlay = false)
    {
        bool isAudioOverlay = targetAudioOverlay || asset.IsAudioOnly;
        bool isVideoOverlay = !isAudioOverlay && targetVideoOverlay;
        ObservableCollection<MontageClipItem> targetList = isAudioOverlay
            ? AudioOverlayClips
            : (isVideoOverlay ? VideoOverlayClips : Clips);
        ProjectSnapshot? checkpoint = recordUndo && (Clips.Count > 0 || VideoOverlayClips.Count > 0 || AudioOverlayClips.Count > 0)
            ? CaptureSnapshot()
            : null;

        TimeSpan dur = asset.Info.Duration > TimeSpan.Zero ? asset.Info.Duration : TimeSpan.FromSeconds(1);
        TimeSpan s = Clamp(start ?? TimeSpan.Zero, TimeSpan.Zero, dur);
        TimeSpan e = Clamp(end ?? dur, TimeSpan.Zero, dur);
        if (e <= s)
        {
            s = TimeSpan.Zero;
            e = dur;
        }

        TimeSpan clipDur = e - s;
        TimeSpan resolvedStart = ResolveFreeInsertStart(targetList, desiredTimelineStart, clipDur);
        MontageClipItem clip = CreateClipItem(asset, s, e, 1.0, resolvedStart, isAudioOverlay, isVideoOverlay);

        targetList.Add(clip);
        RecalculateTimeline();
        if (select || SelectedClip == null)
            SelectClip(clip);

        if (checkpoint != null)
            PushUndoSnapshot(checkpoint);

        return clip;
    }

    private static MontageClipItem CreateClipItem(
        SourceMediaAsset asset,
        TimeSpan start,
        TimeSpan end,
        double audioVolume = 1.0,
        TimeSpan? timelineStart = null,
        bool isAudioOverlay = false,
        bool isVideoOverlay = false,
        double videoScale = 1.0,
        double videoOffsetX = 0.0,
        double videoOffsetY = 0.0,
        double videoOpacity = 1.0)
    {
        TimeSpan dur = asset.Info.Duration > TimeSpan.Zero ? asset.Info.Duration : TimeSpan.FromSeconds(1);
        TimeSpan ts = timelineStart.HasValue && timelineStart.Value > TimeSpan.Zero
            ? timelineStart.Value
            : TimeSpan.Zero;
        return new MontageClipItem(
            sourcePath: asset.FilePath,
            sourceFileName: asset.FileName,
            sourceDuration: dur,
            start: start,
            end: end,
            sourceWidth: asset.Info.DisplayWidth,
            sourceHeight: asset.Info.DisplayHeight,
            sourceFps: asset.Info.Fps,
            hasAudio: asset.HasAudio,
            aspectRatioLabel: asset.Info.AspectRatioLabel,
            colorHex: asset.ColorHex)
        {
            AudioVolume = audioVolume,
            TimelineStart = ts,
            TimelineEnd = ts + (end > start ? end - start : TimeSpan.Zero),
            IsAudioOverlay = isAudioOverlay,
            IsVideoOverlay = isVideoOverlay,
            VideoScale = videoScale,
            VideoOffsetX = videoOffsetX,
            VideoOffsetY = videoOffsetY,
            VideoOpacity = videoOpacity,
            Thumbnail = asset.FindNearestFrame(start.TotalSeconds + (end - start).TotalSeconds * 0.5)
        };
    }

    public void SelectClip(MontageClipItem? clip)
    {
        foreach (MontageClipItem item in Clips)
            item.IsSelected = ReferenceEquals(item, clip);
        foreach (MontageClipItem item in VideoOverlayClips)
            item.IsSelected = ReferenceEquals(item, clip);
        foreach (MontageClipItem item in AudioOverlayClips)
            item.IsSelected = ReferenceEquals(item, clip);

        SelectedClip = clip != null && (Clips.Contains(clip) || VideoOverlayClips.Contains(clip) || AudioOverlayClips.Contains(clip))
            ? clip
            : null;
    }

    private ObservableCollection<MontageClipItem> GetTrackCollection(MontageClipItem clip)
    {
        if (clip.IsAudioOverlay || AudioOverlayClips.Contains(clip))
            return AudioOverlayClips;
        if (clip.IsVideoOverlay || VideoOverlayClips.Contains(clip))
            return VideoOverlayClips;
        return Clips;
    }

    public void RecalculateTimeline()
    {
        TimeSpan v1End = NormalizeTrackGapsPreserved(Clips);
        TimeSpan v2End = NormalizeTrackGapsPreserved(VideoOverlayClips);
        TimeSpan a2End = NormalizeTrackGapsPreserved(AudioOverlayClips);
        TimeSpan maxV = v1End > v2End ? v1End : v2End;
        TotalDuration = maxV > a2End ? maxV : a2End;
    }

    private static TimeSpan NormalizeTrackGapsPreserved(ObservableCollection<MontageClipItem> track)
    {
        if (track.Count == 0)
            return TimeSpan.Zero;

        var ordered = track.OrderBy(c => c.TimelineStart).ToList();
        for (int targetIdx = 0; targetIdx < ordered.Count; targetIdx++)
        {
            MontageClipItem target = ordered[targetIdx];
            if (!ReferenceEquals(track[targetIdx], target))
            {
                int curIdx = track.IndexOf(target);
                if (curIdx >= 0)
                    track.Move(curIdx, targetIdx);
            }
        }

        TimeSpan cursor = TimeSpan.Zero;
        for (int i = 0; i < track.Count; i++)
        {
            MontageClipItem c = track[i];
            c.OrderIndex = i + 1;
            TimeSpan start = c.TimelineStart < cursor ? cursor : c.TimelineStart;
            c.TimelineStart = start;
            c.TimelineEnd = start + c.Duration;
            cursor = c.TimelineEnd;
        }
        return cursor;
    }

    private static TimeSpan ResolveFreeInsertStart(
        IEnumerable<MontageClipItem> track,
        TimeSpan desiredStart,
        TimeSpan clipDuration)
    {
        TimeSpan candidate = desiredStart < TimeSpan.Zero ? TimeSpan.Zero : desiredStart;
        var sorted = track.OrderBy(c => c.TimelineStart).ToList();
        if (sorted.Count == 0)
            return candidate;

        TimeSpan cursor = TimeSpan.Zero;
        foreach (MontageClipItem other in sorted)
        {
            TimeSpan gapStart = cursor;
            TimeSpan gapEnd = other.TimelineStart;
            if (gapEnd - gapStart >= clipDuration - TimeSpan.FromMilliseconds(5))
            {
                TimeSpan maxSlotStart = gapEnd - clipDuration;
                if (maxSlotStart < gapStart) maxSlotStart = gapStart;
                if (candidate >= gapStart && candidate <= maxSlotStart)
                    return candidate;
                if (candidate < gapStart && gapEnd >= gapStart + clipDuration - TimeSpan.FromMilliseconds(5))
                    return gapStart;
            }
            if (other.TimelineEnd > cursor)
                cursor = other.TimelineEnd;
        }

        return candidate > cursor ? candidate : cursor;
    }

    public TimeSpan ResolveMoveTargetTime(MontageClipItem clip, TimeSpan desiredStart, bool? targetVideoOverlay = null)
    {
        ObservableCollection<MontageClipItem> track = targetVideoOverlay.HasValue && !clip.IsAudioOverlay
            ? (targetVideoOverlay.Value ? VideoOverlayClips : Clips)
            : GetTrackCollection(clip);
        var others = track.Where(c => !ReferenceEquals(c, clip)).OrderBy(c => c.TimelineStart).ToList();
        TimeSpan clampedDesired = desiredStart < TimeSpan.Zero ? TimeSpan.Zero : desiredStart;
        if (others.Count == 0)
            return clampedDesired;

        TimeSpan dur = clip.Duration;
        TimeSpan tolerance = TimeSpan.FromMilliseconds(5);
        TimeSpan bestStart = clampedDesired;
        double bestDistSec = double.MaxValue;

        void ConsiderSlot(TimeSpan slotStart, TimeSpan slotEnd)
        {
            if (slotEnd < TimeSpan.MaxValue && slotEnd - slotStart < dur - tolerance)
                return;

            TimeSpan maxAllowed = slotEnd == TimeSpan.MaxValue ? TimeSpan.MaxValue : slotEnd - dur;
            if (maxAllowed < slotStart) maxAllowed = slotStart;

            TimeSpan candidate = clampedDesired < slotStart
                ? slotStart
                : (clampedDesired > maxAllowed ? maxAllowed : clampedDesired);
            double dist = Math.Abs((candidate - clampedDesired).TotalSeconds);
            if (dist < bestDistSec)
            {
                bestDistSec = dist;
                bestStart = candidate;
            }
        }

        TimeSpan cursor = TimeSpan.Zero;
        foreach (MontageClipItem other in others)
        {
            if (other.TimelineStart > cursor)
                ConsiderSlot(cursor, other.TimelineStart);
            if (other.TimelineEnd > cursor)
                cursor = other.TimelineEnd;
        }
        ConsiderSlot(cursor, TimeSpan.MaxValue);

        return bestStart;
    }

    public bool MoveClipToTime(MontageClipItem clip, TimeSpan desiredTimelineStart, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        if (!track.Contains(clip)) return false;

        TimeSpan targetStart = ResolveMoveTargetTime(clip, desiredTimelineStart);
        if ((targetStart - clip.TimelineStart).Duration() < TimeSpan.FromMilliseconds(2))
            return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        clip.TimelineStart = targetStart;
        RecalculateTimeline();
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool MoveVideoClipTrack(MontageClipItem clip, bool toVideoOverlay, TimeSpan? desiredTimelineStart = null, bool recordUndo = true)
    {
        if (clip.IsAudioOverlay) return false;
        if (clip.IsVideoOverlay == toVideoOverlay)
            return desiredTimelineStart.HasValue && MoveClipToTime(clip, desiredTimelineStart.Value, recordUndo);

        ObservableCollection<MontageClipItem> srcTrack = clip.IsVideoOverlay ? VideoOverlayClips : Clips;
        ObservableCollection<MontageClipItem> dstTrack = toVideoOverlay ? VideoOverlayClips : Clips;
        if (!srcTrack.Contains(clip)) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan desiredStart = desiredTimelineStart ?? clip.TimelineStart;
        TimeSpan resolvedStart = ResolveFreeInsertStart(dstTrack, desiredStart, clip.Duration);

        srcTrack.Remove(clip);
        clip.IsVideoOverlay = toVideoOverlay;
        clip.TimelineStart = resolvedStart;
        dstTrack.Add(clip);
        RecalculateTimeline();
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool SetClipTransform(
        MontageClipItem clip,
        double scale,
        double offsetX,
        double offsetY,
        double opacity,
        bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        if (!track.Contains(clip)) return false;

        double s = Math.Clamp(scale, 0.15, 3.0);
        double ox = Math.Clamp(offsetX, -1.5, 1.5);
        double oy = Math.Clamp(offsetY, -1.5, 1.5);
        double op = Math.Clamp(opacity, 0.0, 1.0);

        if (Math.Abs(clip.VideoScale - s) < 0.001 &&
            Math.Abs(clip.VideoOffsetX - ox) < 0.001 &&
            Math.Abs(clip.VideoOffsetY - oy) < 0.001 &&
            Math.Abs(clip.VideoOpacity - op) < 0.001)
            return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        clip.VideoScale = s;
        clip.VideoOffsetX = ox;
        clip.VideoOffsetY = oy;
        clip.VideoOpacity = op;
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> GetGaps(bool audioOverlayTrack = false)
    {
        var track = audioOverlayTrack ? AudioOverlayClips : Clips;
        var gaps = new List<(TimeSpan, TimeSpan)>();
        TimeSpan cursor = TimeSpan.Zero;
        foreach (MontageClipItem c in track)
        {
            if (c.TimelineStart - cursor >= TimeSpan.FromMilliseconds(40))
                gaps.Add((cursor, c.TimelineStart));
            if (c.TimelineEnd > cursor)
                cursor = c.TimelineEnd;
        }
        return gaps;
    }

    public bool RippleDeleteGap(TimeSpan gapStart, TimeSpan gapEnd, bool audioOverlayTrack = false, bool recordUndo = true)
    {
        TimeSpan delta = gapEnd - gapStart;
        if (delta < TimeSpan.FromMilliseconds(20)) return false;
        var track = audioOverlayTrack ? AudioOverlayClips : Clips;
        if (track.Count == 0) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        bool moved = false;
        foreach (MontageClipItem c in track)
        {
            if (c.TimelineStart >= gapEnd - TimeSpan.FromMilliseconds(15))
            {
                c.TimelineStart = c.TimelineStart > delta ? c.TimelineStart - delta : TimeSpan.Zero;
                moved = true;
            }
        }
        if (!moved) return false;

        RecalculateTimeline();
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public (bool Success, MontageClipItem? SecondClip) SplitAtMontageTime(
        TimeSpan montageTime,
        bool recordUndo = true,
        bool audioOverlayTrack = false,
        bool videoOverlayTrack = false)
    {
        ObservableCollection<MontageClipItem> track = audioOverlayTrack
            ? AudioOverlayClips
            : (videoOverlayTrack ? VideoOverlayClips : Clips);
        MontageClipItem? clip = null;
        int index = -1;
        TimeSpan sourceTime = TimeSpan.Zero;

        TimeSpan clamped = Clamp(montageTime, TimeSpan.Zero, TotalDuration);
        for (int i = 0; i < track.Count; i++)
        {
            MontageClipItem c = track[i];
            if (clamped > c.TimelineStart && clamped < c.TimelineEnd)
            {
                clip = c;
                index = i;
                sourceTime = c.Start + (clamped - c.TimelineStart);
                break;
            }
        }

        if (clip == null || index < 0)
            return (false, null);

        if (sourceTime - clip.Start < MinimumClipDuration || clip.End - sourceTime < MinimumClipDuration)
            return (false, null);

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;

        TimeSpan originalEnd = clip.End;
        TimeSpan splitTimelinePos = clip.TimelineStart + (sourceTime - clip.Start);
        clip.End = sourceTime;

        var second = new MontageClipItem(
            sourcePath: clip.SourcePath,
            sourceFileName: clip.SourceFileName,
            sourceDuration: clip.SourceDuration,
            start: sourceTime,
            end: originalEnd,
            sourceWidth: clip.SourceWidth,
            sourceHeight: clip.SourceHeight,
            sourceFps: clip.SourceFps,
            hasAudio: clip.HasAudio,
            aspectRatioLabel: clip.AspectRatioLabel,
            colorHex: clip.ColorHex)
        {
            AudioVolume = clip.AudioVolume,
            TimelineStart = splitTimelinePos,
            TimelineEnd = splitTimelinePos + (originalEnd - sourceTime),
            IsAudioOverlay = clip.IsAudioOverlay,
            IsVideoOverlay = clip.IsVideoOverlay,
            VideoScale = clip.VideoScale,
            VideoOffsetX = clip.VideoOffsetX,
            VideoOffsetY = clip.VideoOffsetY,
            VideoOpacity = clip.VideoOpacity
        };

        if (_assets.TryGetValue(clip.SourcePath, out SourceMediaAsset? asset))
        {
            clip.Thumbnail = asset.FindNearestFrame(clip.Start.TotalSeconds + clip.Duration.TotalSeconds * 0.5);
            second.Thumbnail = asset.FindNearestFrame(second.Start.TotalSeconds + second.Duration.TotalSeconds * 0.5);
        }

        track.Insert(index + 1, second);
        RecalculateTimeline();
        SelectClip(second);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return (true, second);
    }

    public MontageClipItem? DuplicateClip(MontageClipItem clip, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0) return null;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan insertAt = ResolveFreeInsertStart(track, clip.TimelineEnd, clip.Duration);
        var copy = new MontageClipItem(
            sourcePath: clip.SourcePath,
            sourceFileName: clip.SourceFileName,
            sourceDuration: clip.SourceDuration,
            start: clip.Start,
            end: clip.End,
            sourceWidth: clip.SourceWidth,
            sourceHeight: clip.SourceHeight,
            sourceFps: clip.SourceFps,
            hasAudio: clip.HasAudio,
            aspectRatioLabel: clip.AspectRatioLabel,
            colorHex: clip.ColorHex)
        {
            AudioVolume = clip.AudioVolume,
            TimelineStart = insertAt,
            TimelineEnd = insertAt + clip.Duration,
            IsAudioOverlay = clip.IsAudioOverlay,
            IsVideoOverlay = clip.IsVideoOverlay,
            VideoScale = clip.VideoScale,
            VideoOffsetX = clip.VideoOffsetX,
            VideoOffsetY = clip.VideoOffsetY,
            VideoOpacity = clip.VideoOpacity,
            Thumbnail = clip.Thumbnail
        };

        track.Add(copy);
        RecalculateTimeline();
        SelectClip(copy);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return copy;
    }

    public bool RemoveClip(MontageClipItem clip, bool recordUndo = true, bool ripple = false)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan removedEnd = clip.TimelineEnd;
        TimeSpan removedDur = clip.Duration;
        track.RemoveAt(idx);

        if (ripple && removedDur > TimeSpan.Zero)
        {
            foreach (MontageClipItem c in track)
            {
                if (c.TimelineStart >= removedEnd - TimeSpan.FromMilliseconds(15))
                    c.TimelineStart = c.TimelineStart > removedDur ? c.TimelineStart - removedDur : TimeSpan.Zero;
            }
        }

        RecalculateTimeline();

        if (track.Count == 0)
        {
            SelectClip(Clips.FirstOrDefault() ?? VideoOverlayClips.FirstOrDefault() ?? AudioOverlayClips.FirstOrDefault());
        }
        else
        {
            int nextIdx = Math.Clamp(idx, 0, track.Count - 1);
            SelectClip(track[nextIdx]);
        }
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool MoveClipEarlier(MontageClipItem clip, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx <= 0) return false;

        MontageClipItem prev = track[idx - 1];
        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan origPrevStart = prev.TimelineStart;
        prev.TimelineStart = origPrevStart + clip.Duration;
        clip.TimelineStart = origPrevStart;
        RecalculateTimeline();
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool MoveClipLater(MontageClipItem clip, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0 || idx >= track.Count - 1) return false;

        MontageClipItem next = track[idx + 1];
        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan origStart = clip.TimelineStart;
        next.TimelineStart = origStart;
        clip.TimelineStart = origStart + next.Duration;
        RecalculateTimeline();
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool MoveClipToIndex(MontageClipItem clip, int targetIndex, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0 || track.Count <= 1) return false;
        int clamped = Math.Clamp(targetIndex, 0, track.Count - 1);
        if (clamped == idx) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan cursor = TimeSpan.Zero;
        track.Move(idx, clamped);
        for (int i = 0; i < track.Count; i++)
        {
            track[i].TimelineStart = cursor;
            cursor += track[i].Duration;
        }
        RecalculateTimeline();
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool TrimClipStart(MontageClipItem clip, TimeSpan newStart, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0) return false;

        TimeSpan maxStart = clip.End - MinimumClipDuration;
        if (maxStart < TimeSpan.Zero) maxStart = TimeSpan.Zero;

        TimeSpan prevEnd = idx > 0 ? track[idx - 1].TimelineEnd : TimeSpan.Zero;
        TimeSpan minStartByNeighbor = clip.Start - (clip.TimelineStart - prevEnd);
        TimeSpan minStart = minStartByNeighbor > TimeSpan.Zero ? minStartByNeighbor : TimeSpan.Zero;
        if (minStart > maxStart) minStart = maxStart;

        TimeSpan clamped = Clamp(newStart, minStart, maxStart);
        if (clamped == clip.Start) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan delta = clamped - clip.Start;
        clip.Start = clamped;
        TimeSpan nextTimelineStart = clip.TimelineStart + delta;
        clip.TimelineStart = nextTimelineStart < prevEnd ? prevEnd : nextTimelineStart;
        RecalculateTimeline();
        RefreshClipThumbnail(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool TrimClipEnd(MontageClipItem clip, TimeSpan newEnd, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0) return false;

        TimeSpan minEnd = clip.Start + MinimumClipDuration;
        TimeSpan sourceMax = clip.SourceDuration > minEnd ? clip.SourceDuration : clip.End;
        TimeSpan maxEnd = sourceMax;

        if (idx < track.Count - 1)
        {
            TimeSpan maxEndByNeighbor = clip.Start + (track[idx + 1].TimelineStart - clip.TimelineStart);
            if (maxEndByNeighbor < maxEnd)
                maxEnd = maxEndByNeighbor;
        }
        if (maxEnd < minEnd) maxEnd = minEnd;

        TimeSpan clamped = Clamp(newEnd, minEnd, maxEnd);
        if (clamped == clip.End) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        clip.End = clamped;
        RecalculateTimeline();
        RefreshClipThumbnail(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool SlipClip(MontageClipItem clip, TimeSpan newStart, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        if (!track.Contains(clip)) return false;
        TimeSpan dur = clip.Duration;
        TimeSpan maxStart = clip.SourceDuration > dur ? clip.SourceDuration - dur : TimeSpan.Zero;
        TimeSpan clampedStart = Clamp(newStart, TimeSpan.Zero, maxStart);
        if (clampedStart == clip.Start) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        clip.Start = clampedStart;
        clip.End = clampedStart + dur;
        RefreshClipThumbnail(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public (bool Success, MontageClipItem? Clip) TrimStartToPlayhead(TimeSpan montageTime, bool recordUndo = true)
    {
        var (clip, _, sourceTime) = MapMontageToSource(montageTime);
        if (clip == null) return (false, null);
        if (sourceTime <= clip.Start + TimeSpan.FromMilliseconds(5) ||
            clip.End - sourceTime < MinimumClipDuration)
            return (false, null);

        bool ok = TrimClipStart(clip, sourceTime, recordUndo);
        return (ok, ok ? clip : null);
    }

    public (bool Success, MontageClipItem? Clip) TrimEndToPlayhead(TimeSpan montageTime, bool recordUndo = true)
    {
        var (clip, _, sourceTime) = MapMontageToSource(montageTime);
        if (clip == null) return (false, null);
        if (sourceTime >= clip.End - TimeSpan.FromMilliseconds(5) ||
            sourceTime - clip.Start < MinimumClipDuration)
            return (false, null);

        bool ok = TrimClipEnd(clip, sourceTime, recordUndo);
        return (ok, ok ? clip : null);
    }

    public bool RestoreClipBounds(MontageClipItem clip, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0 || clip.SourceDuration <= MinimumClipDuration) return false;
        if (clip.Start == TimeSpan.Zero && clip.End == clip.SourceDuration) return false;

        TimeSpan prevEnd = idx > 0 ? track[idx - 1].TimelineEnd : TimeSpan.Zero;
        TimeSpan nextStart = idx < track.Count - 1 ? track[idx + 1].TimelineStart : TimeSpan.MaxValue;

        TimeSpan desiredStart = TimeSpan.Zero;
        TimeSpan desiredTimelineStart = clip.TimelineStart - clip.Start;
        if (desiredTimelineStart < prevEnd)
        {
            TimeSpan shift = prevEnd - desiredTimelineStart;
            desiredStart += shift;
            desiredTimelineStart = prevEnd;
        }
        TimeSpan desiredEnd = clip.SourceDuration;
        if (nextStart < TimeSpan.MaxValue && desiredTimelineStart + (desiredEnd - desiredStart) > nextStart)
            desiredEnd = desiredStart + (nextStart - desiredTimelineStart);

        if (desiredEnd - desiredStart < MinimumClipDuration) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        clip.Start = desiredStart;
        clip.End = desiredEnd;
        clip.TimelineStart = desiredTimelineStart;
        RecalculateTimeline();
        RefreshClipThumbnail(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool SetClipAudioVolume(MontageClipItem clip, double volume, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        if (!track.Contains(clip)) return false;
        double clamped = Math.Clamp(volume, 0.0, 2.0);
        if (Math.Abs(clip.AudioVolume - clamped) < 0.002) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        clip.AudioVolume = clamped;
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public (MontageClipItem? Clip, int ClipIndex, TimeSpan SourceTime) MapMontageToSource(TimeSpan montageTime)
    {
        if (Clips.Count == 0)
            return (null, -1, TimeSpan.Zero);

        TimeSpan clamped = Clamp(montageTime, TimeSpan.Zero, TotalDuration);

        for (int i = 0; i < Clips.Count; i++)
        {
            MontageClipItem c = Clips[i];
            if (clamped >= c.TimelineStart && clamped < c.TimelineEnd)
            {
                TimeSpan offset = Clamp(clamped - c.TimelineStart, TimeSpan.Zero, c.Duration);
                return (c, i, c.Start + offset);
            }
            if (i == Clips.Count - 1 && clamped == c.TimelineEnd && c.Duration > TimeSpan.Zero)
            {
                TimeSpan endOffset = c.Duration > TimeSpan.FromMilliseconds(20)
                    ? c.Duration - TimeSpan.FromMilliseconds(5)
                    : c.Duration;
                return (c, i, c.Start + endOffset);
            }
        }

        return (null, -1, TimeSpan.Zero);
    }

    public (MontageClipItem? Clip, int ClipIndex, TimeSpan SourceTime) GetActiveAudioOverlayClip(TimeSpan montageTime)
    {
        if (AudioOverlayClips.Count == 0)
            return (null, -1, TimeSpan.Zero);

        TimeSpan clamped = Clamp(montageTime, TimeSpan.Zero, TotalDuration);
        for (int i = 0; i < AudioOverlayClips.Count; i++)
        {
            MontageClipItem c = AudioOverlayClips[i];
            if (clamped >= c.TimelineStart && clamped < c.TimelineEnd)
            {
                TimeSpan offset = Clamp(clamped - c.TimelineStart, TimeSpan.Zero, c.Duration);
                return (c, i, c.Start + offset);
            }
        }
        return (null, -1, TimeSpan.Zero);
    }

    public (MontageClipItem? Clip, int ClipIndex, TimeSpan SourceTime) GetActiveVideoOverlayClip(TimeSpan montageTime)
    {
        if (VideoOverlayClips.Count == 0)
            return (null, -1, TimeSpan.Zero);

        TimeSpan clamped = Clamp(montageTime, TimeSpan.Zero, TotalDuration);
        for (int i = 0; i < VideoOverlayClips.Count; i++)
        {
            MontageClipItem c = VideoOverlayClips[i];
            if (clamped >= c.TimelineStart && clamped < c.TimelineEnd)
            {
                TimeSpan offset = Clamp(clamped - c.TimelineStart, TimeSpan.Zero, c.Duration);
                return (c, i, c.Start + offset);
            }
        }
        return (null, -1, TimeSpan.Zero);
    }

    public MontageClipItem? GetNextClipAfter(TimeSpan montageTime, bool audioOverlayTrack = false)
    {
        var track = audioOverlayTrack ? AudioOverlayClips : Clips;
        for (int i = 0; i < track.Count; i++)
        {
            if (track[i].TimelineStart > montageTime)
                return track[i];
        }
        return null;
    }

    public TimeSpan MapSourceToMontage(MontageClipItem clip, TimeSpan sourceTime)
    {
        TimeSpan offset = Clamp(sourceTime - clip.Start, TimeSpan.Zero, clip.Duration);
        return clip.TimelineStart + offset;
    }

    public void RefreshAssetMetadata(string sourcePath, VideoMediaInfo info)
    {
        if (_assets.TryGetValue(sourcePath, out SourceMediaAsset? asset))
            asset.Info = info;

        bool changed = false;
        foreach (MontageClipItem clip in Clips.Concat(VideoOverlayClips).Concat(AudioOverlayClips))
        {
            if (!string.Equals(clip.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                continue;

            bool wasFullDuration = clip.SourceDuration <= TimeSpan.FromSeconds(1) && clip.End == clip.SourceDuration;
            clip.SourceDuration = info.Duration > TimeSpan.Zero ? info.Duration : clip.SourceDuration;
            if (wasFullDuration && info.Duration > TimeSpan.Zero)
            {
                clip.Start = TimeSpan.Zero;
                clip.End = info.Duration;
            }
            else if (info.Duration > TimeSpan.Zero && clip.End > info.Duration)
            {
                clip.End = info.Duration;
                if (clip.Start >= clip.End)
                    clip.Start = TimeSpan.Zero;
            }

            if (info.DisplayWidth > 0 && info.DisplayHeight > 0)
            {
                clip.SourceWidth = info.DisplayWidth;
                clip.SourceHeight = info.DisplayHeight;
                clip.AspectRatioLabel = info.AspectRatioLabel;
            }
            if (info.Fps > 0)
                clip.SourceFps = info.Fps;
            clip.HasAudio = _assets.TryGetValue(sourcePath, out SourceMediaAsset? srcAsset)
                ? srcAsset.HasAudio
                : info.HasAudio;
            changed = true;
        }

        if (changed)
            RecalculateTimeline();
    }

    public void RefreshAllThumbnails(string sourcePath)
    {
        if (!_assets.TryGetValue(sourcePath, out SourceMediaAsset? asset))
            return;

        if (asset.Info.HasVideo)
        {
            double mid = asset.Info.Duration > TimeSpan.Zero ? asset.Info.Duration.TotalSeconds * 0.5 : 0.0;
            asset.BinThumbnail = asset.FindNearestFrame(mid) ?? asset.Frames.FirstOrDefault()?.Image;
        }

        foreach (MontageClipItem clip in Clips.Concat(VideoOverlayClips))
        {
            if (string.Equals(clip.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                RefreshClipThumbnail(clip, asset);
        }
    }

    public void BeginUndoStep() => _pendingUndoCheckpoint = CaptureSnapshot();

    public void CommitUndoStep()
    {
        ProjectSnapshot? checkpoint = _pendingUndoCheckpoint;
        _pendingUndoCheckpoint = null;
        if (checkpoint == null) return;

        ProjectSnapshot current = CaptureSnapshot();
        if (!SnapshotsEqual(checkpoint, current))
            PushUndoSnapshot(checkpoint);
    }

    public void CancelUndoStep() => _pendingUndoCheckpoint = null;

    public bool Undo()
    {
        if (_undoHistory.Count == 0) return false;
        ProjectSnapshot current = CaptureSnapshot();
        ProjectSnapshot previous = _undoHistory[^1];
        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        _redoHistory.Add(current);
        RestoreSnapshot(previous);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redoHistory.Count == 0) return false;
        ProjectSnapshot current = CaptureSnapshot();
        ProjectSnapshot next = _redoHistory[^1];
        _redoHistory.RemoveAt(_redoHistory.Count - 1);
        _undoHistory.Add(current);
        RestoreSnapshot(next);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ClearHistory()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        _pendingUndoCheckpoint = null;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        Clips.Clear();
        VideoOverlayClips.Clear();
        AudioOverlayClips.Clear();
        MediaBin.Clear();
        _assets.Clear();
        SelectedClip = null;
        TotalDuration = TimeSpan.Zero;
        ClearHistory();
    }

    private ProjectSnapshot CaptureSnapshot()
    {
        static ClipSnapshot MakeSnap(MontageClipItem c, bool isAudioOv, bool isVideoOv) =>
            new(c.SourcePath, c.Start, c.End, c.AudioVolume, c.TimelineStart, isAudioOv, isVideoOv,
                c.VideoScale, c.VideoOffsetX, c.VideoOffsetY, c.VideoOpacity);

        var v1 = Clips.Select(c => MakeSnap(c, false, false)).ToList();
        var v2 = VideoOverlayClips.Select(c => MakeSnap(c, false, true)).ToList();
        var a2 = AudioOverlayClips.Select(c => MakeSnap(c, true, false)).ToList();

        int trackKind = 0;
        int selectedIdx = -1;
        if (SelectedClip != null)
        {
            if (AudioOverlayClips.Contains(SelectedClip)) { trackKind = 2; selectedIdx = AudioOverlayClips.IndexOf(SelectedClip); }
            else if (VideoOverlayClips.Contains(SelectedClip)) { trackKind = 1; selectedIdx = VideoOverlayClips.IndexOf(SelectedClip); }
            else { trackKind = 0; selectedIdx = Clips.IndexOf(SelectedClip); }
        }
        return new ProjectSnapshot(v1, a2, v2, trackKind, selectedIdx);
    }

    private void PushUndoSnapshot(ProjectSnapshot snapshot)
    {
        _undoHistory.Add(snapshot);
        if (_undoHistory.Count > MaxUndoSteps)
            _undoHistory.RemoveAt(0);
        _redoHistory.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreSnapshot(ProjectSnapshot snapshot)
    {
        Clips.Clear();
        foreach (ClipSnapshot snap in snapshot.Clips)
        {
            if (_assets.TryGetValue(snap.SourcePath, out SourceMediaAsset? asset))
                Clips.Add(CreateClipItem(asset, snap.Start, snap.End, snap.AudioVolume, snap.TimelineStart,
                    false, false, snap.VideoScale, snap.VideoOffsetX, snap.VideoOffsetY, snap.VideoOpacity));
        }

        VideoOverlayClips.Clear();
        foreach (ClipSnapshot snap in snapshot.VideoOverlayClips)
        {
            if (_assets.TryGetValue(snap.SourcePath, out SourceMediaAsset? asset))
                VideoOverlayClips.Add(CreateClipItem(asset, snap.Start, snap.End, snap.AudioVolume, snap.TimelineStart,
                    false, true, snap.VideoScale, snap.VideoOffsetX, snap.VideoOffsetY, snap.VideoOpacity));
        }

        AudioOverlayClips.Clear();
        foreach (ClipSnapshot snap in snapshot.AudioOverlayClips)
        {
            if (_assets.TryGetValue(snap.SourcePath, out SourceMediaAsset? asset))
                AudioOverlayClips.Add(CreateClipItem(asset, snap.Start, snap.End, snap.AudioVolume, snap.TimelineStart,
                    true, false, snap.VideoScale, snap.VideoOffsetX, snap.VideoOffsetY, snap.VideoOpacity));
        }

        RecalculateTimeline();
        var selTrack = snapshot.SelectedTrackKind switch
        {
            1 => VideoOverlayClips,
            2 => AudioOverlayClips,
            _ => Clips
        };
        if (selTrack.Count == 0)
            selTrack = Clips.Count > 0 ? Clips : (VideoOverlayClips.Count > 0 ? VideoOverlayClips : AudioOverlayClips);

        if (selTrack.Count == 0)
        {
            SelectClip(null);
        }
        else
        {
            int idx = snapshot.SelectedIndex >= 0 ? Math.Clamp(snapshot.SelectedIndex, 0, selTrack.Count - 1) : 0;
            SelectClip(selTrack[idx]);
        }
    }

    private static bool SnapshotsEqual(ProjectSnapshot a, ProjectSnapshot b)
    {
        return ClipListsEqual(a.Clips, b.Clips) &&
               ClipListsEqual(a.VideoOverlayClips, b.VideoOverlayClips) &&
               ClipListsEqual(a.AudioOverlayClips, b.AudioOverlayClips);

        static bool ClipListsEqual(IReadOnlyList<ClipSnapshot> left, IReadOnlyList<ClipSnapshot> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i].SourcePath, right[i].SourcePath, StringComparison.OrdinalIgnoreCase) ||
                    left[i].Start != right[i].Start ||
                    left[i].End != right[i].End ||
                    left[i].TimelineStart != right[i].TimelineStart ||
                    left[i].IsAudioOverlay != right[i].IsAudioOverlay ||
                    left[i].IsVideoOverlay != right[i].IsVideoOverlay ||
                    Math.Abs(left[i].AudioVolume - right[i].AudioVolume) > 0.002 ||
                    Math.Abs(left[i].VideoScale - right[i].VideoScale) > 0.002 ||
                    Math.Abs(left[i].VideoOffsetX - right[i].VideoOffsetX) > 0.002 ||
                    Math.Abs(left[i].VideoOffsetY - right[i].VideoOffsetY) > 0.002 ||
                    Math.Abs(left[i].VideoOpacity - right[i].VideoOpacity) > 0.002)
                    return false;
            }
            return true;
        }
    }

    private void RefreshClipThumbnail(MontageClipItem clip, SourceMediaAsset? asset = null)
    {
        asset ??= _assets.GetValueOrDefault(clip.SourcePath);
        if (asset == null) return;
        double midSec = clip.Start.TotalSeconds + clip.Duration.TotalSeconds * 0.5;
        ImageSource? frame = asset.FindNearestFrame(midSec);
        if (frame != null)
            clip.Thumbnail = frame;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (max >= min && value > max) return max;
        return value;
    }

    public static string FormatShort(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        if (value.TotalHours >= 1)
            return value.ToString(@"h\:mm\:ss\.f");
        return value.ToString(@"mm\:ss\.f");
    }

    public static string FormatTimecode(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        if (value.TotalHours >= 1)
            return value.ToString(@"h\:mm\:ss\.ff");
        return value.ToString(@"mm\:ss\.ff");
    }

    public static TimeSpan SnapToFrame(TimeSpan value, double fps)
    {
        if (value <= TimeSpan.Zero) return TimeSpan.Zero;
        double safeFps = fps >= 1 && fps <= 240 ? fps : 30.0;
        long frame = (long)Math.Round(value.TotalSeconds * safeFps);
        return TimeSpan.FromSeconds(frame / safeFps);
    }

    public static long TimeToFrameIndex(TimeSpan value, double fps)
    {
        if (value <= TimeSpan.Zero) return 0;
        double safeFps = fps >= 1 && fps <= 240 ? fps : 30.0;
        return (long)Math.Floor(value.TotalSeconds * safeFps + 1e-4);
    }

    public static string FormatSmpteTimecode(TimeSpan value, double fps = 30.0)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        double safeFps = fps >= 1 && fps <= 240 ? fps : 30.0;
        int timebase = Math.Max(1, (int)Math.Round(safeFps));
        long totalFrames = (long)Math.Floor(value.TotalSeconds * safeFps + 1e-4);
        int ff = (int)(totalFrames % timebase);
        long totalSec = totalFrames / timebase;
        int ss = (int)(totalSec % 60);
        int mm = (int)((totalSec / 60) % 60);
        long hh = totalSec / 3600;
        return hh > 0
            ? $"{hh:D2}:{mm:D2}:{ss:D2}:{ff:D2}"
            : $"{mm:D2}:{ss:D2}:{ff:D2}";
    }

    public static string FormatVolumeDb(double volume)
    {
        if (volume <= 0.005)
            return "-∞ dB";
        double db = 20.0 * Math.Log10(volume);
        if (Math.Abs(db) < 0.05)
            return "0.0 dB";
        string sign = db > 0 ? "+" : "";
        return $"{sign}{db.ToString("0.0", CultureInfo.InvariantCulture)} dB";
    }
}
