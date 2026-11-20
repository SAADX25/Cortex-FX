using System.Collections.ObjectModel;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Video;

public sealed partial class MontageProject
{
    public (List<SavedAssetRecord> Assets, List<SavedClipRecord> Clips, Guid? SelectedClipId) ExportStateRecords()
    {
        var assets = MediaBin
            .Select(a => new SavedAssetRecord
            {
                FilePath = a.FilePath,
                DurationTicks = a.Info.Duration.Ticks,
                CodedWidth = a.Info.CodedWidth,
                CodedHeight = a.Info.CodedHeight,
                DisplayWidth = a.Info.DisplayWidth,
                DisplayHeight = a.Info.DisplayHeight,
                RotationDegrees = a.Info.RotationDegrees,
                Fps = a.Info.Fps,
                VideoCodec = a.Info.VideoCodec,
                AudioCodec = a.Info.AudioCodec,
                HasVideo = a.Info.HasVideo,
                HasAudio = a.Info.HasAudio,
                ContainerFormat = a.Info.ContainerFormat,
                FileSizeBytes = a.Info.FileSizeBytes,
                ColorHex = a.ColorHex,
                BinOrderIndex = a.BinOrderIndex
            })
            .ToList();

        var clips = AllClips
            .Select(c => new SavedClipRecord
            {
                Id = c.Id,
                LinkedClipId = c.LinkedClipId,
                SourcePath = c.SourcePath,
                StartTicks = c.Start.Ticks,
                EndTicks = c.End.Ticks,
                TimelineStartTicks = c.TimelineStart.Ticks,
                TrackKind = c.TrackKind,
                TrackLayer = c.TrackLayer,
                AudioVolume = c.AudioVolume,
                VideoScale = c.VideoScale,
                VideoOffsetX = c.VideoOffsetX,
                VideoOffsetY = c.VideoOffsetY,
                VideoOpacity = c.VideoOpacity
            })
            .ToList();

        return (assets, clips, SelectedClip?.Id);
    }

    public void RestoreFromStateRecords(
        IReadOnlyList<SavedAssetRecord> assetRecords,
        IReadOnlyList<SavedClipRecord> clipRecords,
        Guid? selectedClipId)
    {
        Clear();

        foreach (SavedAssetRecord rec in assetRecords.OrderBy(a => a.BinOrderIndex))
        {
            if (string.IsNullOrWhiteSpace(rec.FilePath))
                continue;

            var info = new VideoMediaInfo(
                FilePath: rec.FilePath,
                Duration: TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, rec.DurationTicks)),
                CodedWidth: rec.CodedWidth,
                CodedHeight: rec.CodedHeight,
                DisplayWidth: rec.DisplayWidth,
                DisplayHeight: rec.DisplayHeight,
                RotationDegrees: rec.RotationDegrees,
                Fps: rec.Fps > 0 ? rec.Fps : 30.0,
                VideoCodec: rec.VideoCodec ?? string.Empty,
                AudioCodec: rec.AudioCodec ?? string.Empty,
                HasVideo: rec.HasVideo,
                HasAudio: rec.HasAudio,
                ContainerFormat: rec.ContainerFormat ?? string.Empty,
                FileSizeBytes: rec.FileSizeBytes);

            RegisterAsset(info, rec.ColorHex);
        }

        foreach (SavedClipRecord c in clipRecords)
        {
            if (!_assets.TryGetValue(c.SourcePath, out SourceMediaAsset? asset))
                continue;

            TimeSpan dur = asset.Info.Duration > TimeSpan.Zero ? asset.Info.Duration : TimeSpan.FromSeconds(1);
            TimeSpan start = Clamp(TimeSpan.FromTicks(c.StartTicks), TimeSpan.Zero, dur);
            TimeSpan end = Clamp(TimeSpan.FromTicks(c.EndTicks), TimeSpan.Zero, dur);
            if (end <= start)
            {
                start = TimeSpan.Zero;
                end = dur;
            }

            TimeSpan timelineStart = c.TimelineStartTicks > 0
                ? TimeSpan.FromTicks(c.TimelineStartTicks)
                : TimeSpan.Zero;

            int layer = c.TrackKind == TimelineTrackKind.Audio
                ? Math.Clamp(c.TrackLayer, 0, MaxAudioTracks - 1)
                : Math.Clamp(c.TrackLayer, 0, MaxVideoLayers - 1);

            MontageClipItem item = CreateClipItem(
                asset: asset,
                start: start,
                end: end,
                audioVolume: c.AudioVolume,
                timelineStart: timelineStart,
                trackKind: c.TrackKind,
                trackLayer: layer,
                videoScale: c.VideoScale,
                videoOffsetX: c.VideoOffsetX,
                videoOffsetY: c.VideoOffsetY,
                videoOpacity: c.VideoOpacity,
                id: c.Id == Guid.Empty ? null : c.Id,
                linkedClipId: c.LinkedClipId);

            ObservableCollection<MontageClipItem> track = c.TrackKind == TimelineTrackKind.Audio
                ? GetAudioTrack(layer)
                : GetVideoTrack(layer);
            track.Add(item);
        }

        RecalculateTimeline();

        // Clear any dangling links if a partner clip was dropped or skipped
        var validIds = new HashSet<Guid>(AllClips.Select(c => c.Id));
        foreach (MontageClipItem clip in AllClips)
        {
            if (clip.LinkedClipId.HasValue && !validIds.Contains(clip.LinkedClipId.Value))
                clip.LinkedClipId = null;
        }

        MontageClipItem? selected = FindClipById(selectedClipId) ?? AllClips.FirstOrDefault();
        SelectClip(selected);
        ClearHistory();
    }
}
