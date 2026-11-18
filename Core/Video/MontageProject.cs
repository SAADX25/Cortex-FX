using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Video;

/// <summary>
/// Pure NLE montage sequence state: multi-source asset registry, 3 video tracks (V1..V3),
/// 4 audio tracks (A1..A4), non-destructive editing, and montage-to-source time mapping.
/// </summary>
public sealed partial class MontageProject
{
    public const int MaxVideoLayers = 3;
    public const int MaxAudioTracks = 4;
    public static readonly TimeSpan MinimumClipDuration = TimeSpan.FromMilliseconds(60);

    private static readonly string[] AssetPalette =
    {
        "#38BDF8", "#34D399", "#A78BFA", "#FBBF24", "#F472B6", "#60A5FA"
    };

    private readonly Dictionary<string, SourceMediaAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProjectSnapshot> _undoHistory = new();
    private readonly List<ProjectSnapshot> _redoHistory = new();
    private ProjectSnapshot? _pendingUndoCheckpoint;
    private const int MaxUndoSteps = 50;

    public ObservableCollection<SourceMediaAsset> MediaBin { get; } = new();

    // Video tracks: V1 (Clips), V2 (VideoOverlayClips), V3 (VideoOverlay3Clips)
    public ObservableCollection<MontageClipItem> Clips { get; } = new();
    public ObservableCollection<MontageClipItem> VideoOverlayClips { get; } = new();
    public ObservableCollection<MontageClipItem> VideoOverlay3Clips { get; } = new();

    // Audio tracks: A1 (V1 audio), A2 (V2 audio), A3 (V3 audio), A4 (Music / external audio)
    public ObservableCollection<MontageClipItem> AudioClips { get; } = new();
    public ObservableCollection<MontageClipItem> Audio2Clips { get; } = new();
    public ObservableCollection<MontageClipItem> Audio3Clips { get; } = new();
    public ObservableCollection<MontageClipItem> AudioOverlayClips { get; } = new();

    public IReadOnlyDictionary<string, SourceMediaAsset> Assets => _assets;
    public MontageClipItem? SelectedClip { get; private set; }
    public TimeSpan TotalDuration { get; private set; }

    public double SequenceFps
    {
        get
        {
            if (SelectedClip is { SourceFps: > 1 }) return SelectedClip.SourceFps;
            MontageClipItem? first = AllVideoClips.FirstOrDefault(c => c.SourceFps > 1);
            return first?.SourceFps ?? 30.0;
        }
    }

    public bool CanUndo => _undoHistory.Count > 0;
    public bool CanRedo => _redoHistory.Count > 0;

    public event EventHandler? HistoryChanged;

    public ObservableCollection<MontageClipItem> GetVideoTrack(int layer) => layer switch
    {
        1 => VideoOverlayClips,
        2 => VideoOverlay3Clips,
        _ => Clips
    };

    public ObservableCollection<MontageClipItem> GetAudioTrack(int trackIndex) => trackIndex switch
    {
        0 => AudioClips,
        1 => Audio2Clips,
        2 => Audio3Clips,
        _ => AudioOverlayClips
    };

    public IEnumerable<ObservableCollection<MontageClipItem>> AllVideoTracks
    {
        get
        {
            yield return Clips;
            yield return VideoOverlayClips;
            yield return VideoOverlay3Clips;
        }
    }

    public IEnumerable<ObservableCollection<MontageClipItem>> AllAudioTracks
    {
        get
        {
            yield return AudioClips;
            yield return Audio2Clips;
            yield return Audio3Clips;
            yield return AudioOverlayClips;
        }
    }

    public IEnumerable<MontageClipItem> AllVideoClips => Clips.Concat(VideoOverlayClips).Concat(VideoOverlay3Clips);
    public IEnumerable<MontageClipItem> AllAudioClips => AudioClips.Concat(Audio2Clips).Concat(Audio3Clips).Concat(AudioOverlayClips);
    public IEnumerable<MontageClipItem> AllClips => AllVideoClips.Concat(AllAudioClips);

    public bool HasAnyClips =>
        Clips.Count > 0 || VideoOverlayClips.Count > 0 || VideoOverlay3Clips.Count > 0 ||
        AudioClips.Count > 0 || Audio2Clips.Count > 0 || Audio3Clips.Count > 0 || AudioOverlayClips.Count > 0;

    public int TotalClipCount => AllClips.Count();

    public void SelectClip(MontageClipItem? clip)
    {
        bool found = false;
        foreach (MontageClipItem item in AllClips)
        {
            bool match = ReferenceEquals(item, clip);
            item.IsSelected = match;
            if (match) found = true;
        }
        SelectedClip = found ? clip : null;
    }

    public MontageClipItem? FindClipById(Guid? id) =>
        id.HasValue ? AllClips.FirstOrDefault(c => c.Id == id.Value) : null;

    public MontageClipItem? FindLinkedAudioClip(MontageClipItem? videoClip)
    {
        if (videoClip == null || videoClip.TrackKind != TimelineTrackKind.Video)
            return null;

        if (videoClip.LinkedClipId.HasValue)
        {
            MontageClipItem? direct = AllAudioClips.FirstOrDefault(a => a.Id == videoClip.LinkedClipId.Value);
            if (direct != null)
                return direct;
        }

        // Fall back to aligned audio on matching layer
        ObservableCollection<MontageClipItem> track = GetAudioTrack(videoClip.TrackLayer);
        return track.FirstOrDefault(a =>
            a.LinkedClipId == videoClip.Id ||
            AreAudioVideoAligned(videoClip, a));
    }

    public MontageClipItem? FindLinkedVideoClip(MontageClipItem? audioClip)
    {
        if (audioClip == null || audioClip.TrackKind != TimelineTrackKind.Audio)
            return null;

        if (audioClip.LinkedClipId.HasValue)
        {
            MontageClipItem? direct = AllVideoClips.FirstOrDefault(v => v.Id == audioClip.LinkedClipId.Value);
            if (direct != null)
                return direct;
        }

        return AllVideoClips.FirstOrDefault(v => v.LinkedClipId == audioClip.Id);
    }

    public static bool AreAudioVideoAligned(MontageClipItem video, MontageClipItem audio) =>
        string.Equals(video.SourcePath, audio.SourcePath, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs((video.TimelineStart - audio.TimelineStart).TotalMilliseconds) < 35 &&
        Math.Abs((video.Start - audio.Start).TotalMilliseconds) < 35;

    private ObservableCollection<MontageClipItem> GetTrackCollection(MontageClipItem clip)
    {
        if (clip.TrackKind == TimelineTrackKind.Audio)
        {
            for (int i = 0; i < MaxAudioTracks; i++)
            {
                ObservableCollection<MontageClipItem> tr = GetAudioTrack(i);
                if (tr.Contains(clip))
                {
                    clip.TrackLayer = i;
                    return tr;
                }
            }
            return GetAudioTrack(clip.TrackLayer);
        }

        for (int i = 0; i < MaxVideoLayers; i++)
        {
            ObservableCollection<MontageClipItem> tr = GetVideoTrack(i);
            if (tr.Contains(clip))
            {
                clip.TrackLayer = i;
                return tr;
            }
        }
        return GetVideoTrack(clip.TrackLayer);
    }

    public void RecalculateTimeline()
    {
        TimeSpan max = TimeSpan.Zero;

        for (int v = 0; v < MaxVideoLayers; v++)
        {
            ObservableCollection<MontageClipItem> track = GetVideoTrack(v);
            TimeSpan end = NormalizeTrackGapsPreserved(track, TimelineTrackKind.Video, v);
            if (end > max) max = end;
        }

        for (int a = 0; a < MaxAudioTracks; a++)
        {
            ObservableCollection<MontageClipItem> track = GetAudioTrack(a);
            TimeSpan end = NormalizeTrackGapsPreserved(track, TimelineTrackKind.Audio, a);
            if (end > max) max = end;
        }

        TotalDuration = max;
    }

    private static TimeSpan NormalizeTrackGapsPreserved(
        ObservableCollection<MontageClipItem> track,
        TimelineTrackKind kind,
        int trackLayer)
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
            c.TrackKind = kind;
            c.TrackLayer = trackLayer;
            c.OrderIndex = i + 1;
            TimeSpan start = c.TimelineStart < cursor ? cursor : c.TimelineStart;
            c.TimelineStart = start;
            c.TimelineEnd = start + c.Duration;
            cursor = c.TimelineEnd;
        }
        return cursor;
    }

    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> GetGaps(int videoLayer = 0)
    {
        ObservableCollection<MontageClipItem> track = GetVideoTrack(videoLayer);
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

    public (MontageClipItem? Clip, int ClipIndex, TimeSpan SourceTime) MapMontageToSource(TimeSpan montageTime) =>
        GetActiveVideoClipOnLayer(0, montageTime, allowEndInclusive: true);

    public (MontageClipItem? Clip, int ClipIndex, TimeSpan SourceTime) GetActiveVideoOverlayClip(TimeSpan montageTime) =>
        GetActiveVideoClipOnLayer(1, montageTime, allowEndInclusive: false);

    public (MontageClipItem? Clip, int ClipIndex, TimeSpan SourceTime) GetActiveVideoClipOnLayer(
        int layer,
        TimeSpan montageTime,
        bool allowEndInclusive = false)
    {
        ObservableCollection<MontageClipItem> track = GetVideoTrack(layer);
        if (track.Count == 0)
            return (null, -1, TimeSpan.Zero);

        TimeSpan clamped = Clamp(montageTime, TimeSpan.Zero, TotalDuration);
        for (int i = 0; i < track.Count; i++)
        {
            MontageClipItem c = track[i];
            if (clamped >= c.TimelineStart && clamped < c.TimelineEnd)
            {
                TimeSpan offset = Clamp(clamped - c.TimelineStart, TimeSpan.Zero, c.Duration);
                return (c, i, c.Start + offset);
            }
            if (allowEndInclusive && i == track.Count - 1 && clamped == c.TimelineEnd && c.Duration > TimeSpan.Zero)
            {
                TimeSpan endOffset = c.Duration > TimeSpan.FromMilliseconds(20)
                    ? c.Duration - TimeSpan.FromMilliseconds(5)
                    : c.Duration;
                return (c, i, c.Start + endOffset);
            }
        }
        return (null, -1, TimeSpan.Zero);
    }

    public (MontageClipItem? Clip, int ClipIndex, TimeSpan SourceTime) GetActiveAudioClipOnTrack(
        int trackIndex,
        TimeSpan montageTime)
    {
        ObservableCollection<MontageClipItem> track = GetAudioTrack(trackIndex);
        if (track.Count == 0)
            return (null, -1, TimeSpan.Zero);

        TimeSpan clamped = Clamp(montageTime, TimeSpan.Zero, TotalDuration);
        for (int i = 0; i < track.Count; i++)
        {
            MontageClipItem c = track[i];
            if (clamped >= c.TimelineStart && clamped < c.TimelineEnd)
            {
                TimeSpan offset = Clamp(clamped - c.TimelineStart, TimeSpan.Zero, c.Duration);
                return (c, i, c.Start + offset);
            }
        }
        return (null, -1, TimeSpan.Zero);
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
        foreach (MontageClipItem clip in AllClips)
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

        foreach (MontageClipItem clip in AllVideoClips)
        {
            if (string.Equals(clip.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                RefreshClipThumbnail(clip, asset);
        }
    }

    private void RefreshClipThumbnail(MontageClipItem clip, SourceMediaAsset? asset = null)
    {
        if (clip.TrackKind != TimelineTrackKind.Video) return;
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
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss\.f")
            : value.ToString(@"mm\:ss\.f");
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
