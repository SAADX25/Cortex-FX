using System.ComponentModel;
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
        "MP3" => "\uEC4F",
        "WAV" or "AIFF" => "\uE93E",
        "FLAC" or "ALAC" => "\uE8D6",
        "AAC" or "M4A" => "\uE90B",
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
