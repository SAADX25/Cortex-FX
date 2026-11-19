using System.Collections.ObjectModel;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Video;

public sealed partial class MontageProject
{
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
            ProjectSnapshot? checkpoint = HasAnyClips ? CaptureSnapshot() : null;
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

            foreach (ObservableCollection<MontageClipItem> track in AllVideoTracks.Concat(AllAudioTracks))
                RemoveFrom(track);

            if (removedAny)
            {
                RecalculateTimeline();
                if (SelectedClip != null && !AllClips.Contains(SelectedClip))
                    SelectClip(AllClips.FirstOrDefault());
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

    public MontageClipItem AddClipAtTime(
        SourceMediaAsset asset,
        TimeSpan desiredTimelineStart,
        TimeSpan? start = null,
        TimeSpan? end = null,
        bool select = true,
        bool recordUndo = false,
        int targetVideoLayer = 0,
        int? targetAudioLayer = null,
        bool autoSeparateAudio = true)
    {
        ProjectSnapshot? checkpoint = recordUndo && HasAnyClips ? CaptureSnapshot() : null;

        TimeSpan dur = asset.Info.Duration > TimeSpan.Zero ? asset.Info.Duration : TimeSpan.FromSeconds(1);
        TimeSpan s = Clamp(start ?? TimeSpan.Zero, TimeSpan.Zero, dur);
        TimeSpan e = Clamp(end ?? dur, TimeSpan.Zero, dur);
        if (e <= s)
        {
            s = TimeSpan.Zero;
            e = dur;
        }
        TimeSpan clipDur = e - s;

        bool isAudioOnly = asset.IsAudioOnly || targetAudioLayer.HasValue;
        if (isAudioOnly)
        {
            int audioIdx = Math.Clamp(targetAudioLayer ?? 3, 0, MaxAudioTracks - 1);
            ObservableCollection<MontageClipItem> audioTrack = GetAudioTrack(audioIdx);
            TimeSpan resolvedAudioStart = ResolveFreeInsertStart(audioTrack, desiredTimelineStart, clipDur);
            MontageClipItem audioClip = CreateClipItem(
                asset, s, e, 1.0, resolvedAudioStart,
                TimelineTrackKind.Audio, audioIdx);
            audioTrack.Add(audioClip);
            RecalculateTimeline();
            if (select || SelectedClip == null)
                SelectClip(audioClip);
            if (checkpoint != null)
                PushUndoSnapshot(checkpoint);
            return audioClip;
        }

        int vLayer = Math.Clamp(targetVideoLayer, 0, MaxVideoLayers - 1);
        ObservableCollection<MontageClipItem> vTrack = GetVideoTrack(vLayer);
        TimeSpan resolvedStart = ResolveFreeInsertStart(vTrack, desiredTimelineStart, clipDur);

        MontageClipItem videoClip = CreateClipItem(
            asset, s, e, 1.0, resolvedStart,
            TimelineTrackKind.Video, vLayer);
        vTrack.Add(videoClip);

        if (autoSeparateAudio && asset.HasAudio)
            CreatePairedAudioClip(videoClip, asset, preferredLayer: vLayer, resolvedStart);

        RecalculateTimeline();
        if (select || SelectedClip == null)
            SelectClip(videoClip);
        if (checkpoint != null)
            PushUndoSnapshot(checkpoint);

        return videoClip;
    }

    public MontageClipItem? EnsureSeparatedAudioForVideoClip(MontageClipItem videoClip, bool recordUndo = true)
    {
        if (videoClip.TrackKind != TimelineTrackKind.Video || !videoClip.HasAudio)
            return null;

        MontageClipItem? existing = FindLinkedAudioClip(videoClip);
        if (existing != null)
            return existing;

        if (!_assets.TryGetValue(videoClip.SourcePath, out SourceMediaAsset? asset))
            return null;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        MontageClipItem audioClip = CreatePairedAudioClip(videoClip, asset, videoClip.TrackLayer, videoClip.TimelineStart);
        RecalculateTimeline();
        if (checkpoint != null)
            PushUndoSnapshot(checkpoint);
        return audioClip;
    }

    private MontageClipItem CreatePairedAudioClip(
        MontageClipItem videoClip,
        SourceMediaAsset asset,
        int preferredLayer,
        TimeSpan timelineStart)
    {
        TimeSpan dur = videoClip.Duration;
        int chosenTrack = Math.Clamp(preferredLayer, 0, MaxAudioTracks - 1);
        TimeSpan chosenStart = timelineStart;

        if (!IsRangeFree(GetAudioTrack(chosenTrack), timelineStart, dur))
        {
            bool foundOtherFree = false;
            for (int candidate = 0; candidate < MaxAudioTracks; candidate++)
            {
                if (candidate == chosenTrack) continue;
                if (IsRangeFree(GetAudioTrack(candidate), timelineStart, dur))
                {
                    chosenTrack = candidate;
                    foundOtherFree = true;
                    break;
                }
            }
            if (!foundOtherFree)
                chosenStart = ResolveFreeInsertStart(GetAudioTrack(chosenTrack), timelineStart, dur);
        }

        MontageClipItem audioClip = CreateClipItem(
            asset,
            videoClip.Start,
            videoClip.End,
            videoClip.AudioVolume,
            chosenStart,
            TimelineTrackKind.Audio,
            chosenTrack,
            linkedClipId: videoClip.Id);

        videoClip.LinkedClipId = audioClip.Id;
        GetAudioTrack(chosenTrack).Add(audioClip);
        return audioClip;
    }

    private static bool IsRangeFree(IEnumerable<MontageClipItem> track, TimeSpan start, TimeSpan duration, MontageClipItem? ignore = null)
    {
        TimeSpan end = start + duration;
        TimeSpan tol = TimeSpan.FromMilliseconds(5);
        foreach (MontageClipItem other in track)
        {
            if (ignore != null && ReferenceEquals(other, ignore)) continue;
            if (other.TimelineEnd > start + tol && other.TimelineStart < end - tol)
                return false;
        }
        return true;
    }

    private static MontageClipItem CreateClipItem(
        SourceMediaAsset asset,
        TimeSpan start,
        TimeSpan end,
        double audioVolume = 1.0,
        TimeSpan? timelineStart = null,
        TimelineTrackKind trackKind = TimelineTrackKind.Video,
        int trackLayer = 0,
        double videoScale = 1.0,
        double videoOffsetX = 0.0,
        double videoOffsetY = 0.0,
        double videoOpacity = 1.0,
        Guid? id = null,
        Guid? linkedClipId = null)
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
            colorHex: asset.ColorHex,
            id: id)
        {
            TrackKind = trackKind,
            TrackLayer = trackLayer,
            LinkedClipId = linkedClipId,
            AudioVolume = audioVolume,
            TimelineStart = ts,
            TimelineEnd = ts + (end > start ? end - start : TimeSpan.Zero),
            VideoScale = videoScale,
            VideoOffsetX = videoOffsetX,
            VideoOffsetY = videoOffsetY,
            VideoOpacity = videoOpacity,
            Thumbnail = trackKind == TimelineTrackKind.Video
                ? asset.FindNearestFrame(start.TotalSeconds + (end - start).TotalSeconds * 0.5)
                : null
        };
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

    public TimeSpan ResolveMoveTargetTime(
        MontageClipItem clip,
        TimeSpan desiredStart,
        int? targetTrackLayer = null)
    {
        ObservableCollection<MontageClipItem> track = targetTrackLayer.HasValue
            ? (clip.TrackKind == TimelineTrackKind.Audio
                ? GetAudioTrack(targetTrackLayer.Value)
                : GetVideoTrack(targetTrackLayer.Value))
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

        MontageClipItem? partner = FindLinkedPartner(clip);
        bool movePartnerWithClip = partner != null && (clip.IsVideoClip
            ? AreAudioVideoAligned(clip, partner)
            : AreAudioVideoAligned(partner, clip));

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        clip.TimelineStart = targetStart;
        clip.TimelineEnd = targetStart + clip.Duration;

        if (movePartnerWithClip && partner != null)
        {
            ObservableCollection<MontageClipItem> partnerTrack = GetTrackCollection(partner);
            if (IsRangeFree(partnerTrack, targetStart, partner.Duration, ignore: partner))
            {
                partner.TimelineStart = targetStart;
                partner.TimelineEnd = targetStart + partner.Duration;
            }
            else if (partner.IsAudioClip)
            {
                // Try another free audio track (A1..A3) to preserve exact A/V sync
                for (int candidate = 0; candidate < MaxVideoLayers; candidate++)
                {
                    if (candidate == partner.TrackLayer) continue;
                    ObservableCollection<MontageClipItem> altTrack = GetAudioTrack(candidate);
                    if (IsRangeFree(altTrack, targetStart, partner.Duration, ignore: partner))
                    {
                        partnerTrack.Remove(partner);
                        partner.TrackLayer = candidate;
                        partner.TimelineStart = targetStart;
                        partner.TimelineEnd = targetStart + partner.Duration;
                        altTrack.Add(partner);
                        break;
                    }
                }
            }
        }

        RecalculateTimeline();
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool MoveVideoClipTrack(MontageClipItem clip, int targetVideoLayer, TimeSpan? desiredTimelineStart = null, bool recordUndo = true)
    {
        if (clip.TrackKind != TimelineTrackKind.Video) return false;
        int clampedLayer = Math.Clamp(targetVideoLayer, 0, MaxVideoLayers - 1);
        if (clip.TrackLayer == clampedLayer)
            return desiredTimelineStart.HasValue && MoveClipToTime(clip, desiredTimelineStart.Value, recordUndo);

        ObservableCollection<MontageClipItem> srcTrack = GetVideoTrack(clip.TrackLayer);
        ObservableCollection<MontageClipItem> dstTrack = GetVideoTrack(clampedLayer);
        if (!srcTrack.Contains(clip)) return false;

        MontageClipItem? linkedAudio = FindLinkedAudioClip(clip);
        bool moveAudioWithVideo = linkedAudio != null && AreAudioVideoAligned(clip, linkedAudio);

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan desiredStart = desiredTimelineStart ?? clip.TimelineStart;
        TimeSpan resolvedStart = ResolveFreeInsertStart(dstTrack, desiredStart, clip.Duration);

        srcTrack.Remove(clip);
        clip.TrackLayer = clampedLayer;
        clip.TimelineStart = resolvedStart;
        dstTrack.Add(clip);

        // Move paired audio seamlessly to matching A1..A3 track when free
        if (moveAudioWithVideo && linkedAudio != null)
        {
            ObservableCollection<MontageClipItem> srcAudioTrack = GetTrackCollection(linkedAudio);
            ObservableCollection<MontageClipItem> dstAudioTrack = GetAudioTrack(clampedLayer);
            if (IsRangeFree(dstAudioTrack, resolvedStart, linkedAudio.Duration, ignore: linkedAudio))
            {
                if (!ReferenceEquals(srcAudioTrack, dstAudioTrack))
                {
                    srcAudioTrack.Remove(linkedAudio);
                    linkedAudio.TrackLayer = clampedLayer;
                    dstAudioTrack.Add(linkedAudio);
                }
                linkedAudio.TimelineStart = resolvedStart;
            }
            else if (IsRangeFree(srcAudioTrack, resolvedStart, linkedAudio.Duration, ignore: linkedAudio))
            {
                linkedAudio.TimelineStart = resolvedStart;
            }
        }

        RecalculateTimeline();
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool MoveAudioClipTrack(MontageClipItem clip, int targetAudioLayer, TimeSpan? desiredTimelineStart = null, bool recordUndo = true)
    {
        if (clip.TrackKind != TimelineTrackKind.Audio) return false;
        int clampedLayer = Math.Clamp(targetAudioLayer, 0, MaxAudioTracks - 1);
        if (clip.TrackLayer == clampedLayer)
            return desiredTimelineStart.HasValue && MoveClipToTime(clip, desiredTimelineStart.Value, recordUndo);

        ObservableCollection<MontageClipItem> srcTrack = GetAudioTrack(clip.TrackLayer);
        ObservableCollection<MontageClipItem> dstTrack = GetAudioTrack(clampedLayer);
        if (!srcTrack.Contains(clip)) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan desiredStart = desiredTimelineStart ?? clip.TimelineStart;
        TimeSpan resolvedStart = ResolveFreeInsertStart(dstTrack, desiredStart, clip.Duration);

        srcTrack.Remove(clip);
        clip.TrackLayer = clampedLayer;
        clip.TimelineStart = resolvedStart;
        dstTrack.Add(clip);

        RecalculateTimeline();
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool UnlinkClip(MontageClipItem clip, bool recordUndo = true)
    {
        MontageClipItem? partner = clip.IsVideoClip ? FindLinkedAudioClip(clip) : FindLinkedVideoClip(clip);
        if (clip.LinkedClipId == null && partner == null)
            return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        clip.LinkedClipId = null;
        if (partner != null && partner.LinkedClipId == clip.Id)
            partner.LinkedClipId = null;
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

    public bool RippleDeleteGap(TimeSpan gapStart, TimeSpan gapEnd, int videoLayer = 0, bool recordUndo = true)
    {
        TimeSpan delta = gapEnd - gapStart;
        if (delta < TimeSpan.FromMilliseconds(15)) return false;
        ObservableCollection<MontageClipItem> track = GetVideoTrack(videoLayer);
        if (track.Count == 0) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        bool moved = false;
        var ordered = track.OrderBy(c => c.TimelineStart).ToList();
        foreach (MontageClipItem c in ordered)
        {
            if (c.TimelineStart >= gapEnd - TimeSpan.FromMilliseconds(15))
            {
                MontageClipItem? linkedAudio = FindLinkedAudioClip(c);
                bool shiftLinked = linkedAudio != null && AreAudioVideoAligned(c, linkedAudio);

                TimeSpan nextStart = c.TimelineStart > delta ? c.TimelineStart - delta : TimeSpan.Zero;
                c.TimelineStart = nextStart;
                c.TimelineEnd = nextStart + c.Duration;
                if (shiftLinked && linkedAudio != null)
                {
                    ObservableCollection<MontageClipItem> aTrack = GetTrackCollection(linkedAudio);
                    if (IsRangeFree(aTrack, nextStart, linkedAudio.Duration, ignore: linkedAudio))
                    {
                        linkedAudio.TimelineStart = nextStart;
                        linkedAudio.TimelineEnd = nextStart + linkedAudio.Duration;
                    }
                }
                moved = true;
            }
        }
        if (!moved) return false;

        RecalculateTimeline();
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    /// <summary>Close all leading and intermediate gaps on the target video track, snapping clips flush together with linked audio synced.</summary>
    public (bool Changed, int GapsClosed) CloseAllGaps(int videoLayer = 0, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetVideoTrack(videoLayer);
        if (track.Count == 0) return (false, 0);

        var ordered = track.OrderBy(c => c.TimelineStart).ToList();
        int gapsFound = 0;
        TimeSpan probeCursor = TimeSpan.Zero;
        foreach (MontageClipItem c in ordered)
        {
            if (c.TimelineStart - probeCursor >= TimeSpan.FromMilliseconds(15))
                gapsFound++;
            probeCursor = c.TimelineEnd > probeCursor ? c.TimelineEnd : probeCursor + c.Duration;
        }
        if (gapsFound == 0)
            return (false, 0);

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan cursor = TimeSpan.Zero;
        foreach (MontageClipItem c in ordered)
        {
            MontageClipItem? linkedAudio = FindLinkedAudioClip(c);
            bool shiftLinked = linkedAudio != null && AreAudioVideoAligned(c, linkedAudio);

            c.TimelineStart = cursor;
            c.TimelineEnd = cursor + c.Duration;

            if (shiftLinked && linkedAudio != null)
            {
                ObservableCollection<MontageClipItem> aTrack = GetTrackCollection(linkedAudio);
                if (IsRangeFree(aTrack, cursor, linkedAudio.Duration, ignore: linkedAudio))
                {
                    linkedAudio.TimelineStart = cursor;
                    linkedAudio.TimelineEnd = cursor + linkedAudio.Duration;
                }
            }

            cursor = c.TimelineEnd;
        }

        RecalculateTimeline();
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return (true, gapsFound);
    }

    public bool CanHealSplitWithNext(MontageClipItem clip)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0 || idx >= track.Count - 1) return false;
        MontageClipItem next = track[idx + 1];
        return AreContiguousSameSource(clip, next);
    }

    private static bool AreContiguousSameSource(MontageClipItem left, MontageClipItem right) =>
        string.Equals(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs((left.TimelineEnd - right.TimelineStart).TotalMilliseconds) < 35 &&
        Math.Abs((left.End - right.Start).TotalMilliseconds) < 35 &&
        Math.Abs(left.AudioVolume - right.AudioVolume) < 0.01 &&
        Math.Abs(left.VideoScale - right.VideoScale) < 0.01 &&
        Math.Abs(left.VideoOffsetX - right.VideoOffsetX) < 0.005 &&
        Math.Abs(left.VideoOffsetY - right.VideoOffsetY) < 0.005 &&
        Math.Abs(left.VideoOpacity - right.VideoOpacity) < 0.01;

    /// <summary>Rejoin two contiguous split clips from the same source into a single continuous clip (plus linked partner).</summary>
    public bool HealSplitClip(MontageClipItem clip, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0 || idx >= track.Count - 1) return false;

        MontageClipItem next = track[idx + 1];
        if (!AreContiguousSameSource(clip, next)) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;

        MontageClipItem? leftPartner = FindLinkedPartner(clip);
        MontageClipItem? rightPartner = FindLinkedPartner(next);

        clip.End = next.End;
        clip.TimelineEnd = clip.TimelineStart + clip.Duration;
        track.RemoveAt(idx + 1);

        if (leftPartner != null && rightPartner != null &&
            ReferenceEquals(GetTrackCollection(leftPartner), GetTrackCollection(rightPartner)) &&
            AreContiguousSameSource(leftPartner, rightPartner))
        {
            ObservableCollection<MontageClipItem> pTrack = GetTrackCollection(leftPartner);
            leftPartner.End = rightPartner.End;
            leftPartner.TimelineEnd = leftPartner.TimelineStart + leftPartner.Duration;
            pTrack.Remove(rightPartner);
            clip.LinkedClipId = leftPartner.Id;
            leftPartner.LinkedClipId = clip.Id;
        }

        RecalculateTimeline();
        RefreshClipThumbnail(clip);
        SelectClip(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    /// <summary>Premiere-style Q / W Ripple Trim to Playhead: trims Start or End of the active clip to playhead and closes the gap.</summary>
    public (bool Success, MontageClipItem? Clip, TimeSpan NewPlayhead) RippleTrimToPlayhead(
        TimeSpan montageTime,
        bool trimStart,
        TimelineTrackKind? preferredKind = null,
        int? preferredLayer = null,
        bool recordUndo = true)
    {
        TimeSpan clamped = Clamp(montageTime, TimeSpan.Zero, TotalDuration);
        MontageClipItem? target = FindClipCoveringTime(clamped, preferredKind, preferredLayer);
        if (target == null)
            return (false, null, montageTime);

        TimeSpan sourceTime = target.Start + (clamped - target.TimelineStart);
        if (trimStart)
        {
            if (sourceTime - target.Start < TimeSpan.FromMilliseconds(15) ||
                target.End - sourceTime < MinimumClipDuration)
                return (false, null, montageTime);
        }
        else
        {
            if (target.End - sourceTime < TimeSpan.FromMilliseconds(15) ||
                sourceTime - target.Start < MinimumClipDuration)
                return (false, null, montageTime);
        }

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan gapStart;
        TimeSpan gapEnd;
        TimeSpan newPlayhead;

        if (trimStart)
        {
            gapStart = target.TimelineStart;
            gapEnd = clamped;
            if (!TrimClipStart(target, sourceTime, recordUndo: false))
                return (false, null, montageTime);
            newPlayhead = gapStart;
        }
        else
        {
            gapStart = clamped;
            gapEnd = target.TimelineEnd;
            if (!TrimClipEnd(target, sourceTime, recordUndo: false))
                return (false, null, montageTime);
            newPlayhead = clamped;
        }

        TimeSpan delta = gapEnd - gapStart;
        if (delta >= TimeSpan.FromMilliseconds(15))
        {
            if (target.IsVideoClip)
            {
                RippleDeleteGap(gapStart, gapEnd, target.TrackLayer, recordUndo: false);
            }
            else
            {
                ObservableCollection<MontageClipItem> aTrack = GetTrackCollection(target);
                foreach (MontageClipItem c in aTrack.OrderBy(x => x.TimelineStart).ToList())
                {
                    if (c.TimelineStart >= gapEnd - TimeSpan.FromMilliseconds(15))
                    {
                        TimeSpan nextStart = c.TimelineStart > delta ? c.TimelineStart - delta : TimeSpan.Zero;
                        c.TimelineStart = nextStart;
                        c.TimelineEnd = nextStart + c.Duration;
                    }
                }
                RecalculateTimeline();
            }
        }

        SelectClip(target);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return (true, target, Clamp(newPlayhead, TimeSpan.Zero, TotalDuration));
    }

    public MontageClipItem? FindClipCoveringTime(
        TimeSpan montageTime,
        TimelineTrackKind? preferredKind = null,
        int? preferredLayer = null)
    {
        TimeSpan clamped = Clamp(montageTime, TimeSpan.Zero, TotalDuration);

        MontageClipItem? SearchTrack(ObservableCollection<MontageClipItem> tr) =>
            tr.FirstOrDefault(c => clamped > c.TimelineStart && clamped < c.TimelineEnd);

        if (preferredKind.HasValue && preferredLayer.HasValue)
        {
            ObservableCollection<MontageClipItem> pref = preferredKind.Value == TimelineTrackKind.Audio
                ? GetAudioTrack(preferredLayer.Value)
                : GetVideoTrack(preferredLayer.Value);
            if (SearchTrack(pref) is { } direct)
                return direct;
        }

        for (int v = 0; v < MaxVideoLayers; v++)
        {
            if (SearchTrack(GetVideoTrack(v)) is { } hit)
                return hit;
        }
        for (int a = 0; a < MaxAudioTracks; a++)
        {
            if (SearchTrack(GetAudioTrack(a)) is { } hit)
                return hit;
        }
        return null;
    }

    public (bool Success, MontageClipItem? SecondClip) SplitAtMontageTime(
        TimeSpan montageTime,
        TimelineTrackKind trackKind,
        int trackLayer,
        bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = trackKind == TimelineTrackKind.Audio
            ? GetAudioTrack(trackLayer)
            : GetVideoTrack(trackLayer);

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

        MontageClipItem? partner = FindLinkedPartner(clip);
        bool splitPartner = partner != null &&
                            clamped > partner.TimelineStart + MinimumClipDuration &&
                            clamped < partner.TimelineEnd - MinimumClipDuration;

        MontageClipItem second = SplitSingleClipInTrack(track, index, sourceTime);

        if (splitPartner && partner != null)
        {
            ObservableCollection<MontageClipItem> pTrack = GetTrackCollection(partner);
            int pIdx = pTrack.IndexOf(partner);
            if (pIdx >= 0)
            {
                TimeSpan pSourceTime = partner.Start + (clamped - partner.TimelineStart);
                if (pSourceTime - partner.Start >= MinimumClipDuration &&
                    partner.End - pSourceTime >= MinimumClipDuration)
                {
                    MontageClipItem secondPartner = SplitSingleClipInTrack(pTrack, pIdx, pSourceTime);
                    second.LinkedClipId = secondPartner.Id;
                    secondPartner.LinkedClipId = second.Id;
                }
            }
        }

        RecalculateTimeline();
        SelectClip(second);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return (true, second);
    }

    private MontageClipItem SplitSingleClipInTrack(ObservableCollection<MontageClipItem> track, int index, TimeSpan sourceTime)
    {
        MontageClipItem clip = track[index];
        TimeSpan originalEnd = clip.End;
        TimeSpan splitTimelinePos = clip.TimelineStart + (sourceTime - clip.Start);
        clip.End = sourceTime;
        clip.TimelineEnd = splitTimelinePos;

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
            TrackKind = clip.TrackKind,
            TrackLayer = clip.TrackLayer,
            AudioVolume = clip.AudioVolume,
            TimelineStart = splitTimelinePos,
            TimelineEnd = splitTimelinePos + (originalEnd - sourceTime),
            VideoScale = clip.VideoScale,
            VideoOffsetX = clip.VideoOffsetX,
            VideoOffsetY = clip.VideoOffsetY,
            VideoOpacity = clip.VideoOpacity
        };

        if (clip.IsVideoClip && _assets.TryGetValue(clip.SourcePath, out SourceMediaAsset? asset))
        {
            clip.Thumbnail = asset.FindNearestFrame(clip.Start.TotalSeconds + clip.Duration.TotalSeconds * 0.5);
            second.Thumbnail = asset.FindNearestFrame(second.Start.TotalSeconds + second.Duration.TotalSeconds * 0.5);
        }

        track.Insert(index + 1, second);
        return second;
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
            TrackKind = clip.TrackKind,
            TrackLayer = clip.TrackLayer,
            AudioVolume = clip.AudioVolume,
            TimelineStart = insertAt,
            TimelineEnd = insertAt + clip.Duration,
            VideoScale = clip.VideoScale,
            VideoOffsetX = clip.VideoOffsetX,
            VideoOffsetY = clip.VideoOffsetY,
            VideoOpacity = clip.VideoOpacity,
            Thumbnail = clip.Thumbnail
        };
        track.Add(copy);

        if (clip.IsVideoClip)
        {
            MontageClipItem? linkedAudio = FindLinkedAudioClip(clip);
            if (linkedAudio != null && AreAudioVideoAligned(clip, linkedAudio) &&
                _assets.TryGetValue(linkedAudio.SourcePath, out SourceMediaAsset? asset))
            {
                MontageClipItem audioCopy = CreatePairedAudioClip(copy, asset, linkedAudio.TrackLayer, insertAt);
                audioCopy.AudioVolume = linkedAudio.AudioVolume;
                audioCopy.Start = linkedAudio.Start;
                audioCopy.End = linkedAudio.End;
            }
        }

        RecalculateTimeline();
        SelectClip(copy);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return copy;
    }

    public bool RemoveClip(MontageClipItem clip, bool recordUndo = true, bool ripple = false, bool removeLinkedAudio = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan removedEnd = clip.TimelineEnd;
        TimeSpan removedDur = clip.Duration;

        MontageClipItem? linkedPartner = FindLinkedPartner(clip);

        bool shouldAlsoRemoveLinkedAudio =
            removeLinkedAudio &&
            clip.IsVideoClip &&
            linkedPartner != null &&
            AreAudioVideoAligned(clip, linkedPartner);

        track.RemoveAt(idx);

        if (linkedPartner != null && !shouldAlsoRemoveLinkedAudio && linkedPartner.LinkedClipId == clip.Id)
            linkedPartner.LinkedClipId = null;

        if (shouldAlsoRemoveLinkedAudio && linkedPartner != null)
        {
            ObservableCollection<MontageClipItem> aTrack = GetTrackCollection(linkedPartner);
            aTrack.Remove(linkedPartner);
        }

        if (ripple && removedDur > TimeSpan.Zero)
        {
            var shiftedAudio = new HashSet<Guid>();
            foreach (MontageClipItem c in track.OrderBy(x => x.TimelineStart).ToList())
            {
                if (c.TimelineStart >= removedEnd - TimeSpan.FromMilliseconds(15))
                {
                    MontageClipItem? cPartner = c.IsVideoClip ? FindLinkedAudioClip(c) : null;
                    bool shiftPartner = cPartner != null && AreAudioVideoAligned(c, cPartner);

                    TimeSpan nextStart = c.TimelineStart > removedDur ? c.TimelineStart - removedDur : TimeSpan.Zero;
                    c.TimelineStart = nextStart;
                    c.TimelineEnd = nextStart + c.Duration;

                    if (shiftPartner && cPartner != null)
                    {
                        ObservableCollection<MontageClipItem> pTrack = GetTrackCollection(cPartner);
                        if (IsRangeFree(pTrack, nextStart, cPartner.Duration, ignore: cPartner))
                        {
                            cPartner.TimelineStart = nextStart;
                            cPartner.TimelineEnd = nextStart + cPartner.Duration;
                            shiftedAudio.Add(cPartner.Id);
                        }
                    }
                }
            }

            if (shouldAlsoRemoveLinkedAudio && linkedPartner != null)
            {
                ObservableCollection<MontageClipItem> aTrack = GetTrackCollection(linkedPartner);
                foreach (MontageClipItem ac in aTrack.OrderBy(x => x.TimelineStart).ToList())
                {
                    if (shiftedAudio.Contains(ac.Id)) continue;
                    if (ac.TimelineStart >= removedEnd - TimeSpan.FromMilliseconds(15) &&
                        FindLinkedVideoClip(ac) == null)
                    {
                        TimeSpan nextStart = ac.TimelineStart > removedDur ? ac.TimelineStart - removedDur : TimeSpan.Zero;
                        ac.TimelineStart = nextStart;
                        ac.TimelineEnd = nextStart + ac.Duration;
                    }
                }
            }
        }

        RecalculateTimeline();

        if (track.Count == 0)
        {
            SelectClip(AllClips.FirstOrDefault());
        }
        else
        {
            int nextIdx = Math.Clamp(idx, 0, track.Count - 1);
            SelectClip(track[nextIdx]);
        }
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

        TimeSpan origStart = clip.Start;
        TimeSpan origTimelineStart = clip.TimelineStart;
        MontageClipItem? partner = FindLinkedPartner(clip);
        bool syncPartnerStart = partner != null &&
                                string.Equals(partner.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                                Math.Abs((partner.Start - origStart).TotalMilliseconds) < 35 &&
                                Math.Abs((partner.TimelineStart - origTimelineStart).TotalMilliseconds) < 35;

        if (syncPartnerStart && partner != null)
        {
            ObservableCollection<MontageClipItem> pTrack = GetTrackCollection(partner);
            int pIdx = pTrack.IndexOf(partner);
            if (pIdx >= 0)
            {
                TimeSpan pPrevEnd = pIdx > 0 ? pTrack[pIdx - 1].TimelineEnd : TimeSpan.Zero;
                TimeSpan pMinByNeighbor = partner.Start - (partner.TimelineStart - pPrevEnd);
                if (pMinByNeighbor > minStart) minStart = pMinByNeighbor;
                TimeSpan pMaxStart = partner.End - MinimumClipDuration;
                if (pMaxStart < maxStart) maxStart = pMaxStart;
            }
            else
            {
                syncPartnerStart = false;
            }
        }

        if (minStart > maxStart) minStart = maxStart;

        TimeSpan clamped = Clamp(newStart, minStart, maxStart);
        if (clamped == clip.Start) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        TimeSpan delta = clamped - clip.Start;
        clip.Start = clamped;
        TimeSpan nextTimelineStart = clip.TimelineStart + delta;
        clip.TimelineStart = nextTimelineStart < prevEnd ? prevEnd : nextTimelineStart;
        clip.TimelineEnd = clip.TimelineStart + clip.Duration;

        if (syncPartnerStart && partner != null)
        {
            partner.Start = clamped;
            partner.TimelineStart = clip.TimelineStart;
            partner.TimelineEnd = partner.TimelineStart + partner.Duration;
            RefreshClipThumbnail(partner);
        }

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

        TimeSpan origEnd = clip.End;
        MontageClipItem? partner = FindLinkedPartner(clip);
        bool syncPartnerEnd = partner != null &&
                              (clip.IsVideoClip ? AreAudioVideoAligned(clip, partner) : AreAudioVideoAligned(partner, clip)) &&
                              Math.Abs((partner.End - origEnd).TotalMilliseconds) < 35;

        if (syncPartnerEnd && partner != null)
        {
            ObservableCollection<MontageClipItem> pTrack = GetTrackCollection(partner);
            int pIdx = pTrack.IndexOf(partner);
            if (pIdx >= 0)
            {
                if (pIdx < pTrack.Count - 1)
                {
                    TimeSpan pMaxByNeighbor = partner.Start + (pTrack[pIdx + 1].TimelineStart - partner.TimelineStart);
                    if (pMaxByNeighbor < maxEnd) maxEnd = pMaxByNeighbor;
                }
                TimeSpan pMinEnd = partner.Start + MinimumClipDuration;
                if (pMinEnd > minEnd) minEnd = pMinEnd;
            }
            else
            {
                syncPartnerEnd = false;
            }
        }

        if (maxEnd < minEnd) maxEnd = minEnd;

        TimeSpan clamped = Clamp(newEnd, minEnd, maxEnd);
        if (clamped == clip.End) return false;

        ProjectSnapshot? checkpoint = recordUndo ? CaptureSnapshot() : null;
        clip.End = clamped;
        clip.TimelineEnd = clip.TimelineStart + clip.Duration;

        if (syncPartnerEnd && partner != null)
        {
            partner.End = clamped;
            partner.TimelineEnd = partner.TimelineStart + partner.Duration;
            RefreshClipThumbnail(partner);
        }

        RecalculateTimeline();
        RefreshClipThumbnail(clip);
        if (checkpoint != null) PushUndoSnapshot(checkpoint);
        return true;
    }

    public bool RestoreClipBounds(MontageClipItem clip, bool recordUndo = true)
    {
        ObservableCollection<MontageClipItem> track = GetTrackCollection(clip);
        int idx = track.IndexOf(clip);
        if (idx < 0 || clip.SourceDuration <= MinimumClipDuration) return false;
        if (clip.Start == TimeSpan.Zero && clip.End == clip.SourceDuration) return false;

        TimeSpan prevEnd = idx > 0 ? track[idx - 1].TimelineEnd : TimeSpan.Zero;
        TimeSpan nextStart = idx < track.Count - 1 ? track[idx + 1].TimelineStart : TimeSpan.MaxValue;

        MontageClipItem? partner = FindLinkedPartner(clip);
        bool syncPartner = partner != null &&
                           (clip.IsVideoClip ? AreAudioVideoAligned(clip, partner) : AreAudioVideoAligned(partner, clip));
        if (syncPartner && partner != null)
        {
            ObservableCollection<MontageClipItem> pTrack = GetTrackCollection(partner);
            int pIdx = pTrack.IndexOf(partner);
            if (pIdx >= 0)
            {
                TimeSpan pPrevEnd = pIdx > 0 ? pTrack[pIdx - 1].TimelineEnd : TimeSpan.Zero;
                TimeSpan pNextStart = pIdx < pTrack.Count - 1 ? pTrack[pIdx + 1].TimelineStart : TimeSpan.MaxValue;
                if (pPrevEnd > prevEnd) prevEnd = pPrevEnd;
                if (pNextStart < nextStart) nextStart = pNextStart;
            }
            else
            {
                syncPartner = false;
            }
        }

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
        clip.TimelineEnd = desiredTimelineStart + clip.Duration;

        if (syncPartner && partner != null)
        {
            partner.Start = desiredStart;
            partner.End = desiredEnd;
            partner.TimelineStart = desiredTimelineStart;
            partner.TimelineEnd = desiredTimelineStart + partner.Duration;
            RefreshClipThumbnail(partner);
        }

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
}
