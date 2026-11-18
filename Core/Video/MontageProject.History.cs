using System.Collections.ObjectModel;

namespace CortexFX.Core.Video;

public sealed partial class MontageProject
{
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
        foreach (ObservableCollection<MontageClipItem> t in AllVideoTracks) t.Clear();
        foreach (ObservableCollection<MontageClipItem> t in AllAudioTracks) t.Clear();
        MediaBin.Clear();
        _assets.Clear();
        SelectedClip = null;
        TotalDuration = TimeSpan.Zero;
        ClearHistory();
    }

    private ProjectSnapshot CaptureSnapshot()
    {
        static ClipSnapshot MakeSnap(MontageClipItem c) =>
            new(c.Id, c.LinkedClipId, c.SourcePath, c.Start, c.End, c.AudioVolume, c.TimelineStart,
                c.TrackKind, c.TrackLayer, c.VideoScale, c.VideoOffsetX, c.VideoOffsetY, c.VideoOpacity);

        var videoTracks = new IReadOnlyList<ClipSnapshot>[MaxVideoLayers];
        for (int i = 0; i < MaxVideoLayers; i++)
            videoTracks[i] = GetVideoTrack(i).Select(MakeSnap).ToList();

        var audioTracks = new IReadOnlyList<ClipSnapshot>[MaxAudioTracks];
        for (int i = 0; i < MaxAudioTracks; i++)
            audioTracks[i] = GetAudioTrack(i).Select(MakeSnap).ToList();

        TimelineTrackKind selKind = TimelineTrackKind.Video;
        int selLayer = 0;
        int selIdx = -1;
        if (SelectedClip != null)
        {
            selKind = SelectedClip.TrackKind;
            selLayer = SelectedClip.TrackLayer;
            selIdx = GetTrackCollection(SelectedClip).IndexOf(SelectedClip);
        }

        return new ProjectSnapshot(videoTracks, audioTracks, selKind, selLayer, selIdx);
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
        void RestoreTrack(ObservableCollection<MontageClipItem> target, IReadOnlyList<ClipSnapshot> items, TimelineTrackKind kind, int layer)
        {
            target.Clear();
            foreach (ClipSnapshot snap in items)
            {
                if (_assets.TryGetValue(snap.SourcePath, out SourceMediaAsset? asset))
                {
                    target.Add(CreateClipItem(
                        asset, snap.Start, snap.End, snap.AudioVolume, snap.TimelineStart,
                        kind, layer, snap.VideoScale, snap.VideoOffsetX, snap.VideoOffsetY, snap.VideoOpacity,
                        snap.ClipId, snap.LinkedClipId));
                }
            }
        }

        for (int i = 0; i < MaxVideoLayers; i++)
            RestoreTrack(GetVideoTrack(i), snapshot.VideoTracks[i], TimelineTrackKind.Video, i);

        for (int i = 0; i < MaxAudioTracks; i++)
            RestoreTrack(GetAudioTrack(i), snapshot.AudioTracks[i], TimelineTrackKind.Audio, i);

        RecalculateTimeline();

        ObservableCollection<MontageClipItem> selTrack = snapshot.SelectedTrackKind == TimelineTrackKind.Audio
            ? GetAudioTrack(snapshot.SelectedTrackLayer)
            : GetVideoTrack(snapshot.SelectedTrackLayer);

        if (selTrack.Count == 0)
        {
            SelectClip(AllClips.FirstOrDefault());
        }
        else
        {
            int idx = snapshot.SelectedIndex >= 0 ? Math.Clamp(snapshot.SelectedIndex, 0, selTrack.Count - 1) : 0;
            SelectClip(selTrack[idx]);
        }
    }

    private static bool SnapshotsEqual(ProjectSnapshot a, ProjectSnapshot b)
    {
        for (int i = 0; i < MaxVideoLayers; i++)
        {
            if (!ClipListsEqual(a.VideoTracks[i], b.VideoTracks[i]))
                return false;
        }
        for (int i = 0; i < MaxAudioTracks; i++)
        {
            if (!ClipListsEqual(a.AudioTracks[i], b.AudioTracks[i]))
                return false;
        }
        return true;

        static bool ClipListsEqual(IReadOnlyList<ClipSnapshot> left, IReadOnlyList<ClipSnapshot> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i].SourcePath, right[i].SourcePath, StringComparison.OrdinalIgnoreCase) ||
                    left[i].Start != right[i].Start ||
                    left[i].End != right[i].End ||
                    left[i].TimelineStart != right[i].TimelineStart ||
                    left[i].TrackKind != right[i].TrackKind ||
                    left[i].TrackLayer != right[i].TrackLayer ||
                    left[i].LinkedClipId != right[i].LinkedClipId ||
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
}
