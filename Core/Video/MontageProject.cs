using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Video;

public sealed record FilmstripFrame(double PositionSeconds, ImageSource Image);

/// <summary>Cached metadata, filmstrip thumbnails, and audio waveform peaks for a source video file.</summary>
public sealed class SourceMediaAsset
{
    public SourceMediaAsset(VideoMediaInfo info, string colorHex)
    {
        Info = info;
        ColorHex = colorHex;
    }

    public VideoMediaInfo Info { get; set; }
    public string FilePath => Info.FilePath;
    public string FileName => Path.GetFileName(Info.FilePath);
    public string ColorHex { get; }
    public List<FilmstripFrame> Frames { get; } = new();
    public float[]? AudioPeaks { get; set; }

    public ImageSource? FindNearestFrame(double seconds) =>
        FindNearestFrame(seconds, double.MaxValue);

    public ImageSource? FindNearestFrame(double seconds, double maxDistanceSeconds)
    {
        if (Frames.Count == 0) return null;
        ImageSource? best = null;
        double bestDist = double.MaxValue;
        for (int i = 0; i < Frames.Count; i++)
        {
            double dist = Math.Abs(Frames[i].PositionSeconds - seconds);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = Frames[i].Image;
            }
        }
        return bestDist <= maxDistanceSeconds ? best : null;
    }

    public bool HasFrameNear(double seconds, double toleranceSeconds)
    {
        for (int i = 0; i < Frames.Count; i++)
        {
            if (Math.Abs(Frames[i].PositionSeconds - seconds) <= toleranceSeconds)
                return true;
        }
        return false;
    }

    public void AddOrUpdateFrame(FilmstripFrame frame, double replaceToleranceSeconds = 0.05)
    {
        for (int i = 0; i < Frames.Count; i++)
        {
            if (Math.Abs(Frames[i].PositionSeconds - frame.PositionSeconds) <= replaceToleranceSeconds)
            {
                Frames[i] = frame;
                return;
            }
        }

        int insertAt = Frames.Count;
        for (int i = 0; i < Frames.Count; i++)
        {
            if (Frames[i].PositionSeconds > frame.PositionSeconds)
            {
                insertAt = i;
                break;
            }
        }
        Frames.Insert(insertAt, frame);
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
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(DisplayRange));
            OnPropertyChanged(nameof(DisplayDuration));
            OnPropertyChanged(nameof(DisplayBadge));
        }
    }

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

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

    public string DisplayTitle => $"#{OrderIndex} · {SourceFileName}";
    public string DisplayBadge => $"#{OrderIndex} · {MontageProject.FormatShort(Duration)}";
    public string DisplayDuration => MontageProject.FormatShort(Duration);
    public string DisplayRange => $"{MontageProject.FormatShort(Start)} → {MontageProject.FormatShort(End)} · {DisplayResolution}";
    public string DisplayResolution => SourceWidth > 0 && SourceHeight > 0 ? $"{SourceWidth}×{SourceHeight}" : AspectRatioLabel;

    public VideoMontageClip ToExportClip() =>
        new(SourcePath, Start, End, HasAudio, SourceWidth, SourceHeight, SourceFps);

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
    public static readonly TimeSpan MinimumClipDuration = TimeSpan.FromMilliseconds(100);

    private static readonly string[] AssetPalette =
    {
        "#38BDF8", "#34D399", "#A78BFA", "#FBBF24", "#F472B6", "#60A5FA"
    };

    private sealed record ClipSnapshot(string SourcePath, TimeSpan Start, TimeSpan End);
    private sealed record ProjectSnapshot(IReadOnlyList<ClipSnapshot> Clips, int SelectedIndex);

    private readonly Dictionary<string, SourceMediaAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProjectSnapshot> _undoHistory = new();
    private readonly List<ProjectSnapshot> _redoHistory = new();
    private ProjectSnapshot? _pendingUndoCheckpoint;
    private const int MaxUndoSteps = 50;

    public ObservableCollection<MontageClipItem> Clips { get; } = new();
    public IReadOnlyDictionary<string, SourceMediaAsset> Assets => _assets;
    public MontageClipItem? SelectedClip { get; private set; }
    public TimeSpan TotalDuration { get; private set; }
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
        return asset;
    }

    public MontageClipItem AddClipFromAsset(
        SourceMediaAsset asset,
        TimeSpan? start = null,
        TimeSpan? end = null,
        bool select = true,
        bool recordUndo = false)
    {
        ProjectSnapshot? checkpoint = recordUndo && Clips.Count > 0 ? CaptureSnapshot() : null;

        TimeSpan dur = asset.Info.Duration > TimeSpan.Zero ? asset.Info.Duration : TimeSpan.FromSeconds(1);
        TimeSpan s = Clamp(start ?? TimeSpan.Zero, TimeSpan.Zero, dur);
        TimeSpan e = Clamp(end ?? dur, TimeSpan.Zero, dur);
        if (e <= s)
        {
            s = TimeSpan.Zero;
            e = dur;
        }

        MontageClipItem clip = CreateClipItem(asset, s, e);

        Clips.Add(clip);
        RecalculateTimeline();
        if (select || SelectedClip == null)
            SelectClip(clip);

        if (checkpoint != null)
            PushUndoSnapshot(checkpoint);

        return clip;
    }

    private static MontageClipItem CreateClipItem(SourceMediaAsset asset, TimeSpan start, TimeSpan end)
    {
        TimeSpan dur = asset.Info.Duration > TimeSpan.Zero ? asset.Info.Duration : TimeSpan.FromSeconds(1);
        return new MontageClipItem(
            sourcePath: asset.FilePath,
            sourceFileName: asset.FileName,
            sourceDuration: dur,
            start: start,
            end: end,
            sourceWidth: asset.Info.DisplayWidth,
            sourceHeight: asset.Info.DisplayHeight,
            sourceFps: asset.Info.Fps,
            hasAudio: asset.Info.HasAudio,
            aspectRatioLabel: asset.Info.AspectRatioLabel,
            colorHex: asset.ColorHex)
        {
            Thumbnail = asset.FindNearestFrame(start.TotalSeconds + (end - start).TotalSeconds * 0.5)
        };
    }

    public void SelectClip(MontageClipItem? clip)
    {
        foreach (MontageClipItem item in Clips)
            item.IsSelected = ReferenceEquals(item, clip);
        SelectedClip = clip != null && Clips.Contains(clip) ? clip : null;
    }

    public void RecalculateTimeline()
    {
        TimeSpan cursor = TimeSpan.Zero;
        for (int i = 0; i < Clips.Count; i++)
        {
            MontageClipItem c = Clips[i];
            c.OrderIndex = i + 1;
            c.TimelineStart = cursor;
            cursor += c.Duration;
            c.TimelineEnd = cursor;
        }
        TotalDuration = cursor;
    }

    public (bool Success, MontageClipItem? SecondClip) SplitAtMontageTime(TimeSpan montageTime, bool recordUndo = true)
    {
        var (clip, index, sourceTime) = MapMontageToSource(montageTime);
        if (clip == null || index < 0)
            return (false, null);

        if (sourceTime - clip.Start < MinimumClipDuration || clip.End - sourceTime < MinimumClipDuration)
            return (false, null);

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;

        TimeSpan originalEnd = clip.End;
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
            colorHex: clip.ColorHex);

        if (_assets.TryGetValue(clip.SourcePath, out SourceMediaAsset? asset))
        {
            clip.Thumbnail = asset.FindNearestFrame(clip.Start.TotalSeconds + clip.Duration.TotalSeconds * 0.5);
            second.Thumbnail = asset.FindNearestFrame(second.Start.TotalSeconds + second.Duration.TotalSeconds * 0.5);
        }

        Clips.Insert(index + 1, second);
        RecalculateTimeline();
        SelectClip(second);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return (true, second);
    }

    public MontageClipItem? DuplicateClip(MontageClipItem clip, bool recordUndo = true)
    {
        int idx = Clips.IndexOf(clip);
        if (idx < 0) return null;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
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
            Thumbnail = clip.Thumbnail
        };

        Clips.Insert(idx + 1, copy);
        RecalculateTimeline();
        SelectClip(copy);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return copy;
    }

    public bool RemoveClip(MontageClipItem clip, bool recordUndo = true)
    {
        int idx = Clips.IndexOf(clip);
        if (idx < 0) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        Clips.RemoveAt(idx);
        RecalculateTimeline();

        if (Clips.Count == 0)
        {
            SelectClip(null);
        }
        else
        {
            int nextIdx = Math.Clamp(idx, 0, Clips.Count - 1);
            SelectClip(Clips[nextIdx]);
        }
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool MoveClipEarlier(MontageClipItem clip, bool recordUndo = true)
    {
        int idx = Clips.IndexOf(clip);
        if (idx <= 0) return false;
        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        Clips.Move(idx, idx - 1);
        RecalculateTimeline();
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool MoveClipLater(MontageClipItem clip, bool recordUndo = true)
    {
        int idx = Clips.IndexOf(clip);
        if (idx < 0 || idx >= Clips.Count - 1) return false;
        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        Clips.Move(idx, idx + 1);
        RecalculateTimeline();
        SelectClip(clip);
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
            bool isLast = i == Clips.Count - 1;
            if (clamped < c.TimelineEnd || isLast)
            {
                TimeSpan offset = Clamp(clamped - c.TimelineStart, TimeSpan.Zero, c.Duration);
                return (c, i, c.Start + offset);
            }
        }

        MontageClipItem last = Clips[^1];
        return (last, Clips.Count - 1, last.End);
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
        foreach (MontageClipItem clip in Clips)
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
            clip.HasAudio = info.HasAudio;
            changed = true;
        }

        if (changed)
            RecalculateTimeline();
    }

    public void RefreshAllThumbnails(string sourcePath)
    {
        if (!_assets.TryGetValue(sourcePath, out SourceMediaAsset? asset))
            return;

        foreach (MontageClipItem clip in Clips)
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
        _assets.Clear();
        SelectedClip = null;
        TotalDuration = TimeSpan.Zero;
        ClearHistory();
    }

    private ProjectSnapshot CaptureSnapshot()
    {
        var items = Clips.Select(c => new ClipSnapshot(c.SourcePath, c.Start, c.End)).ToList();
        int selectedIdx = SelectedClip != null ? Clips.IndexOf(SelectedClip) : -1;
        return new ProjectSnapshot(items, selectedIdx);
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
                Clips.Add(CreateClipItem(asset, snap.Start, snap.End));
        }

        RecalculateTimeline();
        if (Clips.Count == 0)
        {
            SelectClip(null);
        }
        else
        {
            int idx = snapshot.SelectedIndex >= 0 ? Math.Clamp(snapshot.SelectedIndex, 0, Clips.Count - 1) : 0;
            SelectClip(Clips[idx]);
        }
    }

    private static bool SnapshotsEqual(ProjectSnapshot a, ProjectSnapshot b)
    {
        if (a.Clips.Count != b.Clips.Count) return false;
        for (int i = 0; i < a.Clips.Count; i++)
        {
            if (!string.Equals(a.Clips[i].SourcePath, b.Clips[i].SourcePath, StringComparison.OrdinalIgnoreCase) ||
                a.Clips[i].Start != b.Clips[i].Start ||
                a.Clips[i].End != b.Clips[i].End)
                return false;
        }
        return true;
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
}
