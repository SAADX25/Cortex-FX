using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Video;
using Unosquare.FFME.Common;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    private Unosquare.FFME.MediaElement? GetVideoOverlayMediaElement(int layer) => layer switch
    {
        1 => OverlayVideoMedia,
        2 => OverlayVideo3Media,
        _ => null
    };

    private Grid? GetVideoTransformHost(int layer) => layer switch
    {
        0 => V1TransformHost,
        1 => V2TransformHost,
        2 => V3TransformHost,
        _ => null
    };

    private Unosquare.FFME.MediaElement? GetAudioMediaElement(int trackIdx) => trackIdx switch
    {
        0 => Audio1Media,
        1 => Audio2Media,
        2 => Audio3Media,
        3 => AudioOverlayMedia,
        _ => null
    };

    private static int ParseElementLayerTag(object? sender, int fallback) =>
        sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int idx) ? idx : fallback;

    private (bool UseVideoEmbedded, MontageClipItem? AudioClip) ResolveAudioPlaybackModeForLayer(int layer, TimeSpan montageTime)
    {
        var (vClip, _, _) = _project.GetActiveVideoClipOnLayer(layer, montageTime, allowEndInclusive: false);
        var (aClip, _, _) = _project.GetActiveAudioClipOnTrack(layer, montageTime);

        bool useEmbedded = vClip != null &&
                           !_isVideoTrackMuted[layer] &&
                           vClip.HasAudio &&
                           aClip != null &&
                           MontageProject.AreAudioVideoAligned(vClip, aClip);

        return (useEmbedded, aClip);
    }

    // V1 Primary FFME Events

    private void PreviewMedia_MediaOpening(object? sender, MediaOpeningEventArgs e)
    {
        e.Options.UseParallelDecoding = true;
        e.Options.UseParallelRendering = true;
    }

    private async void PreviewMedia_MediaOpened(object? sender, MediaOpenedEventArgs e)
    {
        _mediaReady = true;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;

        string? openedPath = _activePlayerSourcePath;
        if (!string.IsNullOrEmpty(openedPath) && _project.Assets.TryGetValue(openedPath, out SourceMediaAsset? asset))
        {
            TimeSpan naturalDur = PreviewMedia.NaturalDuration ?? asset.Info.Duration;
            int w = PreviewMedia.NaturalVideoWidth > 0 ? PreviewMedia.NaturalVideoWidth : asset.Info.DisplayWidth;
            int h = PreviewMedia.NaturalVideoHeight > 0 ? PreviewMedia.NaturalVideoHeight : asset.Info.DisplayHeight;
            double fps = PreviewMedia.VideoFrameRate > 0 ? PreviewMedia.VideoFrameRate : asset.Info.Fps;

            bool durationChanged = asset.Info.Duration <= TimeSpan.FromSeconds(1) && naturalDur > TimeSpan.FromSeconds(1);
            bool needsRefresh = durationChanged || (asset.Info.DisplayWidth == 0 && w > 0);

            if (needsRefresh)
            {
                VideoMediaInfo updated = asset.Info with
                {
                    Duration = naturalDur > TimeSpan.Zero ? naturalDur : asset.Info.Duration,
                    DisplayWidth = w,
                    DisplayHeight = h,
                    Fps = fps,
                    HasAudio = PreviewMedia.HasAudio
                };
                _project.RefreshAssetMetadata(openedPath, updated);
                if (durationChanged)
                {
                    CancelAssetExtraction(openedPath);
                    asset.Frames.Clear();
                    asset.AudioPeaks = null;
                }
                StartAssetVisualExtraction(asset);
            }
        }

        ApplyPlayerAudioAndSpeed();

        if (_hasPendingSeekOnOpen)
        {
            _hasPendingSeekOnOpen = false;
            TimeSpan seekTarget = _pendingSeekOnOpen;
            bool shouldPlay = _pendingPlayOnOpen;
            _pendingPlayOnOpen = false;

            _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(250);
            await SafeSeekMediaAsync(PreviewMedia, seekTarget, skipIfNearCurrent: true);

            if (shouldPlay && _isPlaying)
            {
                await PreviewMedia.Play();
                _isPlaying = true;
                _uiTimer.Start();
                UpdatePlayPauseUi(true);
                SyncAllAuxiliaryChannels(_montagePosition, autoPlay: true, forceSeek: false);
            }
            else
            {
                await PreviewMedia.Pause();
                _isPlaying = false;
                UpdatePlayPauseUi(false);
            }
        }
        else
        {
            await PreviewMedia.Pause();
            UpdatePlayPauseUi(false);
        }

        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        RenderTimeline();
        UpdateAspectOverlay();
    }

    private void PreviewMedia_MediaEnded(object? sender, EventArgs e)
    {
        if (!_isPlaying) return;
        if (_playingClip != null)
            HandleActivePlaybackPosition(_playingClip.End);
    }

    private void PreviewMedia_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        _mediaReady = false;
        _activePlayerSourcePath = null;
        _isPlaying = false;
        _uiTimer.Stop();
        UpdatePlayPauseUi(false);
        PreviewPlaceholder.Text = "Live preview unavailable for this codec — timeline cutting & export still work.";
        PreviewPlaceholder.Visibility = Visibility.Visible;
    }

    // Unified Overlay Video (V2, V3) & Audio Track (A1..A4) FFME Events

    private void OverlayVideoMedia_MediaOpening(object? sender, MediaOpeningEventArgs e)
    {
        e.Options.IsSubtitleDisabled = true;
        e.Options.UseParallelDecoding = true;
        e.Options.UseParallelRendering = true;
    }

    private async void OverlayVideoMedia_MediaOpened(object? sender, MediaOpenedEventArgs e)
    {
        int layer = ParseElementLayerTag(sender, 1);
        OverlayChannelState channel = _videoOverlayChannels[layer - 1];
        Unosquare.FFME.MediaElement? media = GetVideoOverlayMediaElement(layer);
        if (media == null) return;

        RefreshChannelAssetDuration(channel, media);
        await HandleOverlayChannelMediaOpenedAsync(
            channel,
            media,
            resolveSourceTime: () =>
            {
                var (v, _, src) = _project.GetActiveVideoClipOnLayer(layer, _montagePosition);
                return (v != null && !_isVideoTrackMuted[layer], src);
            },
            afterOpened: () =>
            {
                Grid? host = GetVideoTransformHost(layer);
                if (host != null)
                    host.Visibility = channel.PlayingClip != null && !_isVideoTrackMuted[layer]
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                ApplyActiveFrameTransforms();
            });
    }

    private void OverlayVideoMedia_MediaEnded(object? sender, EventArgs e)
    {
        int layer = ParseElementLayerTag(sender, 1);
        OverlayChannelState channel = _videoOverlayChannels[layer - 1];
        Unosquare.FFME.MediaElement? media = GetVideoOverlayMediaElement(layer);
        try
        {
            if (channel.MediaReady && media != null)
                _ = media.Pause();
        }
        catch { }
    }

    private void OverlayVideoMedia_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        int layer = ParseElementLayerTag(sender, 1);
        _videoOverlayChannels[layer - 1].Reset();
    }

    private void AudioChannelMedia_MediaOpening(object? sender, MediaOpeningEventArgs e)
    {
        e.Options.IsSubtitleDisabled = true;
        e.Options.IsVideoDisabled = true;
        e.Options.UseParallelDecoding = true;
    }

    private async void AudioChannelMedia_MediaOpened(object? sender, MediaOpenedEventArgs e)
    {
        int trackIdx = ParseElementLayerTag(sender, 3);
        OverlayChannelState channel = _audioChannels[trackIdx];
        Unosquare.FFME.MediaElement? media = GetAudioMediaElement(trackIdx);
        if (media == null) return;

        RefreshChannelAssetDuration(channel, media);
        await HandleOverlayChannelMediaOpenedAsync(
            channel,
            media,
            resolveSourceTime: () =>
            {
                var (a, _, src) = _project.GetActiveAudioClipOnTrack(trackIdx, _montagePosition);
                if (a == null || _isAudioTrackMuted[trackIdx])
                    return (false, TimeSpan.Zero);
                if (trackIdx < MontageProject.MaxVideoLayers)
                {
                    var (useEmbedded, _) = ResolveAudioPlaybackModeForLayer(trackIdx, _montagePosition);
                    if (useEmbedded)
                        return (false, TimeSpan.Zero);
                }
                return (true, src);
            },
            afterOpened: null);
    }

    private void AudioChannelMedia_MediaEnded(object? sender, EventArgs e)
    {
        int trackIdx = ParseElementLayerTag(sender, 3);
        OverlayChannelState channel = _audioChannels[trackIdx];
        Unosquare.FFME.MediaElement? media = GetAudioMediaElement(trackIdx);
        try
        {
            if (channel.MediaReady && media != null)
                _ = media.Pause();
        }
        catch { }
    }

    private void AudioChannelMedia_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        int trackIdx = ParseElementLayerTag(sender, 3);
        _audioChannels[trackIdx].Reset();
    }

    private void RefreshChannelAssetDuration(OverlayChannelState channel, Unosquare.FFME.MediaElement media)
    {
        string? openedPath = channel.ActiveSourcePath;
        if (string.IsNullOrEmpty(openedPath) || !_project.Assets.TryGetValue(openedPath, out SourceMediaAsset? asset))
            return;

        TimeSpan naturalDur = media.NaturalDuration ?? asset.Info.Duration;
        if (asset.Info.Duration <= TimeSpan.FromSeconds(1) && naturalDur > TimeSpan.FromSeconds(1))
        {
            VideoMediaInfo updated = asset.Info with
            {
                Duration = naturalDur,
                HasAudio = media.HasAudio || asset.Info.HasAudio
            };
            _project.RefreshAssetMetadata(openedPath, updated);
            CancelAssetExtraction(openedPath);
            asset.AudioPeaks = null;
            StartAssetVisualExtraction(asset);
            UpdateProjectHeaderAndHud();
            UpdateTimeLabels();
            UpdateExportSummaryHint();
            RenderTimeline();
        }
    }

    // Multi-Layer Video & Independent Audio Sync

    private Task SyncPlayerToMontagePositionAsync(
        TimeSpan montageTime,
        bool autoPlay,
        bool forceExactSeek)
    {
        if (!HasAnyTimelineClips())
            return Task.CompletedTask;

        montageTime = ClampMontageTime(montageTime);
        _montagePosition = montageTime;

        if (autoPlay)
        {
            _isPlaying = true;
            UpdatePlayPauseUi(true);
        }

        var (clip, clipIndex, _) = _project.MapMontageToSource(montageTime);
        if (clip != null)
        {
            _playingClip = clip;
            _playingClipIndex = clipIndex;
            if (PreviewMedia.Visibility != Visibility.Visible)
                PreviewMedia.Visibility = Visibility.Visible;
        }
        else
        {
            _playingClip = null;
            _playingClipIndex = -1;
            if (PreviewMedia.Visibility != Visibility.Hidden)
                PreviewMedia.Visibility = Visibility.Hidden;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            try
            {
                if (_mediaReady && (PreviewMedia.MediaState == MediaPlaybackState.Play || PreviewMedia.IsPlaying))
                    _ = PreviewMedia.Pause();
            }
            catch { }

            if (autoPlay)
            {
                _lastGapPlaybackTickUtc = DateTime.UtcNow;
                _uiTimer.Start();
            }
        }

        SyncAllAuxiliaryChannels(montageTime, autoPlay, forceSeek: true);

        ApplyActiveFrameTransforms();
        UpdateProjectHeaderAndHud();
        UpdateTimeLabels();
        UpdatePlayheadVisual();

        if (clip == null)
            return Task.CompletedTask;

        _queuedSeekTarget = montageTime;
        _queuedAutoPlay = autoPlay;
        _hasQueuedSeek = true;

        if (_seekLoopRunning && _activeSeekLoopTask != null)
            return _activeSeekLoopTask;

        _seekLoopRunning = true;
        _activeSeekLoopTask = RunCoalescedSeekLoopAsync();
        return _activeSeekLoopTask;
    }

    private void SyncAllAuxiliaryChannels(TimeSpan montageTime, bool autoPlay, bool forceSeek)
    {
        for (int layer = 1; layer <= 2; layer++)
            _ = SyncVideoOverlayLayerAsync(layer, montageTime, autoPlay, forceSeek);

        for (int aTrack = 0; aTrack < MontageProject.MaxAudioTracks; aTrack++)
            _ = SyncAudioChannelAsync(aTrack, montageTime, autoPlay, forceSeek);
    }

    private Task SyncVideoOverlayLayerAsync(int layer, TimeSpan montageTime, bool autoPlay, bool forceSeek)
    {
        Unosquare.FFME.MediaElement? media = GetVideoOverlayMediaElement(layer);
        Grid? host = GetVideoTransformHost(layer);
        if (media == null || host == null) return Task.CompletedTask;

        return QueueOverlayChannelSyncAsync(
            _videoOverlayChannels[layer - 1],
            media,
            montageTime,
            autoPlay,
            forceSeek,
            resolveClip: t =>
            {
                var (vClip, _, sourceTime) = _project.GetActiveVideoClipOnLayer(layer, t);
                return (vClip, sourceTime, _isVideoTrackMuted[layer]);
            },
            onActiveChanged: active =>
            {
                host.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                if (active) PreviewPlaceholder.Visibility = Visibility.Collapsed;
                ApplyActiveFrameTransforms();
            });
    }

    private Task SyncAudioChannelAsync(int trackIdx, TimeSpan montageTime, bool autoPlay, bool forceSeek)
    {
        Unosquare.FFME.MediaElement? media = GetAudioMediaElement(trackIdx);
        if (media == null) return Task.CompletedTask;

        return QueueOverlayChannelSyncAsync(
            _audioChannels[trackIdx],
            media,
            montageTime,
            autoPlay,
            forceSeek,
            resolveClip: t =>
            {
                var (aClip, _, sourceTime) = _project.GetActiveAudioClipOnTrack(trackIdx, t);
                if (aClip == null || !aClip.HasAudio || _isAudioTrackMuted[trackIdx])
                    return (aClip, sourceTime, true);

                if (trackIdx < MontageProject.MaxVideoLayers)
                {
                    var (useEmbedded, _) = ResolveAudioPlaybackModeForLayer(trackIdx, t);
                    if (useEmbedded)
                        return (aClip, sourceTime, true);
                }
                return (aClip, sourceTime, false);
            },
            onActiveChanged: null);
    }

    private static TimeSpan ClampMediaSeekTarget(Unosquare.FFME.MediaElement media, TimeSpan target)
    {
        TimeSpan minPos = media.PlaybackStartTime ?? TimeSpan.Zero;
        if (target < minPos)
            target = minPos;

        TimeSpan? endLimit = media.PlaybackEndTime ?? media.NaturalDuration;
        if (endLimit.HasValue && endLimit.Value > minPos + TimeSpan.FromMilliseconds(40))
        {
            TimeSpan maxPos = endLimit.Value - TimeSpan.FromMilliseconds(20);
            if (target > maxPos)
                target = maxPos;
        }
        return target;
    }

    private static async Task SafeSeekMediaAsync(
        Unosquare.FFME.MediaElement media,
        TimeSpan target,
        bool skipIfNearCurrent = false)
    {
        TimeSpan safeTarget = ClampMediaSeekTarget(media, target);
        bool isStoppedOrEnded = media.MediaState == MediaPlaybackState.Stop || media.HasMediaEnded;
        if (skipIfNearCurrent && !isStoppedOrEnded &&
            Math.Abs((media.Position - safeTarget).TotalMilliseconds) < 30)
        {
            return;
        }

        try { await media.Seek(safeTarget); }
        catch { try { media.Position = safeTarget; } catch { } }
    }

    private async Task HandleOverlayChannelMediaOpenedAsync(
        OverlayChannelState channel,
        Unosquare.FFME.MediaElement media,
        Func<(bool IsActive, TimeSpan SourceTime)> resolveSourceTime,
        Action? afterOpened)
    {
        channel.MediaReady = true;
        ApplyPlayerAudioAndSpeed();

        if (channel.HasPendingSeekOnOpen)
        {
            channel.HasPendingSeekOnOpen = false;
            TimeSpan seekTarget = channel.PendingSeekOnOpen;
            bool shouldPlay = channel.PendingPlayOnOpen;
            channel.PendingPlayOnOpen = false;

            if (_isPlaying)
            {
                var (active, currentSourceTime) = resolveSourceTime();
                if (active)
                {
                    seekTarget = currentSourceTime;
                    shouldPlay = true;
                }
            }

            channel.LastSeekUtc = DateTime.UtcNow;
            await SafeSeekMediaAsync(media, seekTarget, skipIfNearCurrent: false);

            try
            {
                if (shouldPlay && _isPlaying)
                {
                    await media.Play();
                    channel.LastSeekUtc = DateTime.UtcNow;
                    ApplyPlayerAudioAndSpeed();
                }
                else
                {
                    await media.Pause();
                }
            }
            catch { }
        }
        else if (_isPlaying)
        {
            var (active, currentSourceTime) = resolveSourceTime();
            if (active)
            {
                channel.LastSeekUtc = DateTime.UtcNow;
                await SafeSeekMediaAsync(media, currentSourceTime, skipIfNearCurrent: false);
                try
                {
                    await media.Play();
                    channel.LastSeekUtc = DateTime.UtcNow;
                    ApplyPlayerAudioAndSpeed();
                }
                catch { }
            }
        }

        afterOpened?.Invoke();
    }

    private Task QueueOverlayChannelSyncAsync(
        OverlayChannelState channel,
        Unosquare.FFME.MediaElement media,
        TimeSpan montageTime,
        bool autoPlay,
        bool forceSeek,
        Func<TimeSpan, (MontageClipItem? Clip, TimeSpan SourceTime, bool Suppressed)> resolveClip,
        Action<bool>? onActiveChanged)
    {
        channel.QueuedMontageTime = montageTime;
        channel.QueuedAutoPlay = autoPlay;
        channel.QueuedForceSeek |= forceSeek;
        channel.HasQueuedSync = true;

        if (channel.LoopRunning && channel.ActiveSyncTask != null)
            return channel.ActiveSyncTask;

        channel.LoopRunning = true;
        channel.ActiveSyncTask = RunCoalescedOverlayChannelLoopAsync(channel, media, resolveClip, onActiveChanged);
        return channel.ActiveSyncTask;
    }

    private async Task RunCoalescedOverlayChannelLoopAsync(
        OverlayChannelState channel,
        Unosquare.FFME.MediaElement media,
        Func<TimeSpan, (MontageClipItem? Clip, TimeSpan SourceTime, bool Suppressed)> resolveClip,
        Action<bool>? onActiveChanged)
    {
        try
        {
            while (channel.HasQueuedSync)
            {
                channel.HasQueuedSync = false;
                TimeSpan targetMontage = channel.QueuedMontageTime;
                bool autoPlay = channel.QueuedAutoPlay;
                bool forceSeek = channel.QueuedForceSeek;
                channel.QueuedForceSeek = false;

                var (clip, sourceTime, suppressed) = resolveClip(targetMontage);
                if (clip == null || suppressed)
                {
                    channel.PlayingClip = null;
                    onActiveChanged?.Invoke(false);
                    try
                    {
                        if (channel.MediaReady && (media.MediaState == MediaPlaybackState.Play || media.IsPlaying))
                            await media.Pause();
                    }
                    catch { }
                    continue;
                }

                bool switchedClip = !ReferenceEquals(channel.PlayingClip, clip);
                channel.PlayingClip = clip;
                onActiveChanged?.Invoke(true);
                ApplyPlayerAudioAndSpeed();

                bool needOpen = !media.IsOpen ||
                    !string.Equals(channel.ActiveSourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase);
                if (needOpen)
                {
                    channel.ActiveSourcePath = clip.SourcePath;
                    channel.MediaReady = false;
                    channel.PendingSeekOnOpen = sourceTime;
                    channel.HasPendingSeekOnOpen = true;
                    channel.PendingPlayOnOpen = autoPlay;
                    channel.LastSeekUtc = DateTime.UtcNow;
                    try
                    {
                        bool opened = await media.Open(new Uri(clip.SourcePath));
                        if (!opened)
                            channel.ActiveSourcePath = null;
                    }
                    catch
                    {
                        channel.MediaReady = false;
                        channel.ActiveSourcePath = null;
                    }
                    continue;
                }

                if (!channel.MediaReady)
                {
                    channel.PendingSeekOnOpen = sourceTime;
                    channel.HasPendingSeekOnOpen = true;
                    channel.PendingPlayOnOpen = autoPlay;
                    continue;
                }

                bool mustResetFromStop = media.MediaState == MediaPlaybackState.Stop || media.HasMediaEnded;
                if (forceSeek || switchedClip || mustResetFromStop)
                {
                    channel.LastSeekUtc = DateTime.UtcNow;
                    await SafeSeekMediaAsync(media, sourceTime, skipIfNearCurrent: !mustResetFromStop);
                }

                bool effectivePlay = autoPlay && _isPlaying;
                try
                {
                    if (effectivePlay && media.MediaState != MediaPlaybackState.Play)
                    {
                        await media.Play();
                        channel.LastSeekUtc = DateTime.UtcNow;
                        ApplyPlayerAudioAndSpeed();
                    }
                    else if (!effectivePlay && (media.MediaState == MediaPlaybackState.Play || media.IsPlaying))
                    {
                        await media.Pause();
                    }
                }
                catch { }
            }
        }
        finally
        {
            channel.LoopRunning = false;
        }
    }

    private async Task RunCoalescedSeekLoopAsync()
    {
        try
        {
            while (_hasQueuedSeek && _project.Clips.Count > 0)
            {
                _hasQueuedSeek = false;
                TimeSpan targetMontage = _queuedSeekTarget;
                bool autoPlay = _queuedAutoPlay;

                var (clip, clipIndex, sourceTime) = _project.MapMontageToSource(targetMontage);
                if (clip == null) break;

                _playingClip = clip;
                _playingClipIndex = clipIndex;
                ApplyPlayerAudioAndSpeed();

                bool needOpen = !PreviewMedia.IsOpen ||
                    !string.Equals(_activePlayerSourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase);

                if (needOpen)
                {
                    _activePlayerSourcePath = clip.SourcePath;
                    _mediaReady = false;
                    _pendingSeekOnOpen = sourceTime;
                    _hasPendingSeekOnOpen = true;
                    _pendingPlayOnOpen = autoPlay;
                    _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(400);

                    PreviewPlaceholder.Text = "Loading clip...";
                    PreviewPlaceholder.Visibility = Visibility.Visible;

                    try
                    {
                        await PreviewMedia.Open(new Uri(clip.SourcePath));
                    }
                    catch
                    {
                        _activePlayerSourcePath = null;
                        PreviewPlaceholder.Text = "Live preview unavailable — export still works.";
                        PreviewPlaceholder.Visibility = Visibility.Visible;
                    }
                }
                else if (!_mediaReady)
                {
                    _pendingSeekOnOpen = sourceTime;
                    _hasPendingSeekOnOpen = true;
                    _pendingPlayOnOpen = autoPlay;
                }
                else
                {
                    _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(180);
                    await SafeSeekMediaAsync(PreviewMedia, sourceTime, skipIfNearCurrent: false);

                    if (autoPlay && _isPlaying && PreviewMedia.MediaState != MediaPlaybackState.Play)
                    {
                        await PreviewMedia.Play();
                        _isPlaying = true;
                        _uiTimer.Start();
                        UpdatePlayPauseUi(true);
                    }
                }
            }
        }
        finally
        {
            _seekLoopRunning = false;
        }
    }

    private void PreviewMedia_PositionChanged(object? sender, PositionChangedEventArgs e)
    {
        if (!_isPlaying || (_dragMode != TimelineDragMode.None && _dragMode != TimelineDragMode.DragAudioVolume)) return;
        if (DateTime.UtcNow < _transitionGuardUntil) return;
        if (_playingClip == null) return;
        HandleActivePlaybackPosition(e.Position);
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isPlaying || (_dragMode != TimelineDragMode.None && _dragMode != TimelineDragMode.DragAudioVolume)) return;
        if (DateTime.UtcNow < _transitionGuardUntil) return;

        if (_playingClip == null)
        {
            HandleGapOrOverlayPlaybackTick();
            return;
        }

        if (!_mediaReady) return;
        HandleActivePlaybackPosition(PreviewMedia.Position);
    }

    private void HandleGapOrOverlayPlaybackTick()
    {
        DateTime now = DateTime.UtcNow;
        double dt = _lastGapPlaybackTickUtc == DateTime.MinValue
            ? 0.033
            : Math.Clamp((now - _lastGapPlaybackTickUtc).TotalSeconds, 0.005, 0.15);
        _lastGapPlaybackTickUtc = now;

        _montagePosition += TimeSpan.FromSeconds(dt * _speedRatio);
        if (_montagePosition >= _project.TotalDuration - TimeSpan.FromMilliseconds(35))
        {
            if (LoopToggle.IsChecked == true && _project.TotalDuration > TimeSpan.Zero)
            {
                _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(220);
                _ = SyncPlayerToMontagePositionAsync(TimeSpan.Zero, autoPlay: true, forceExactSeek: true);
            }
            else
            {
                _montagePosition = _project.TotalDuration;
                _ = PausePlaybackAsync();
                UpdateTimeLabels();
                UpdatePlayheadVisual();
            }
            return;
        }

        var (enteredV1, _, _) = _project.MapMontageToSource(_montagePosition);
        if (enteredV1 != null)
        {
            _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(220);
            _ = SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: true, forceExactSeek: true);
            return;
        }

        EnsureOverlayPlayersSyncedDuringPlayback();
        UpdateTimeLabels();
        UpdatePlayheadVisual();
    }

    private void EnsureOverlayPlayersSyncedDuringPlayback()
    {
        DateTime now = DateTime.UtcNow;
        ApplyPlayerAudioAndSpeed();

        for (int layer = 1; layer <= 2; layer++)
        {
            OverlayChannelState ch = _videoOverlayChannels[layer - 1];
            Unosquare.FFME.MediaElement? media = GetVideoOverlayMediaElement(layer);
            var (activeClip, _, srcTime) = _project.GetActiveVideoClipOnLayer(layer, _montagePosition);
            MontageClipItem? expectedActive = _isVideoTrackMuted[layer] ? null : activeClip;

            if (ShouldResyncChannel(ch, media, expectedActive, srcTime, now, driftThresholdMs: 500, out bool force))
                _ = SyncVideoOverlayLayerAsync(layer, _montagePosition, autoPlay: true, forceSeek: force);
        }

        for (int aTrack = 0; aTrack < MontageProject.MaxAudioTracks; aTrack++)
        {
            OverlayChannelState ch = _audioChannels[aTrack];
            Unosquare.FFME.MediaElement? media = GetAudioMediaElement(aTrack);
            var (activeAudio, _, srcTime) = _project.GetActiveAudioClipOnTrack(aTrack, _montagePosition);

            bool suppressed = activeAudio == null || !activeAudio.HasAudio || _isAudioTrackMuted[aTrack];
            if (!suppressed && aTrack < MontageProject.MaxVideoLayers)
            {
                var (useEmbedded, _) = ResolveAudioPlaybackModeForLayer(aTrack, _montagePosition);
                if (useEmbedded) suppressed = true;
            }

            MontageClipItem? expectedActive = suppressed ? null : activeAudio;
            if (ShouldResyncChannel(ch, media, expectedActive, srcTime, now, driftThresholdMs: 650, out bool force))
                _ = SyncAudioChannelAsync(aTrack, _montagePosition, autoPlay: true, forceSeek: force);
        }
    }

    private static bool ShouldResyncChannel(
        OverlayChannelState channel,
        Unosquare.FFME.MediaElement? media,
        MontageClipItem? expectedClip,
        TimeSpan expectedSourceTime,
        DateTime now,
        double driftThresholdMs,
        out bool forceSeek)
    {
        if (!ReferenceEquals(expectedClip, channel.PlayingClip))
        {
            forceSeek = true;
            return true;
        }

        forceSeek = false;
        if (expectedClip == null || !channel.MediaReady || channel.LoopRunning || media == null)
            return false;

        double sinceSeekMs = (now - channel.LastSeekUtc).TotalMilliseconds;
        if (sinceSeekMs < 650)
            return false;

        TimeSpan expected = ClampMediaSeekTarget(media, expectedSourceTime);
        double driftMs = Math.Abs((media.Position - expected).TotalMilliseconds);
        if (media.MediaState != MediaPlaybackState.Play)
        {
            forceSeek = media.HasMediaEnded || media.MediaState == MediaPlaybackState.Stop || driftMs > 380;
            return true;
        }

        if (driftMs > driftThresholdMs && sinceSeekMs >= 1200)
        {
            forceSeek = true;
            return true;
        }

        return false;
    }

    private void HandleActivePlaybackPosition(TimeSpan sourcePos)
    {
        MontageClipItem? clip = _playingClip;
        if (clip == null || !string.Equals(clip.SourcePath, _activePlayerSourcePath, StringComparison.OrdinalIgnoreCase))
            return;

        if (sourcePos < clip.Start)
            sourcePos = clip.Start;

        _montagePosition = _project.MapSourceToMontage(clip, sourcePos);
        UpdateTimeLabels();
        UpdatePlayheadVisual();
        EnsureOverlayPlayersSyncedDuringPlayback();

        if (sourcePos >= clip.End - TimeSpan.FromMilliseconds(35))
        {
            int nextIndex = _playingClipIndex + 1;
            if (nextIndex >= 0 && nextIndex < _project.Clips.Count)
            {
                MontageClipItem nextClip = _project.Clips[nextIndex];
                if (nextClip.TimelineStart - clip.TimelineEnd > TimeSpan.FromMilliseconds(45))
                {
                    _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(180);
                    _montagePosition = clip.TimelineEnd;
                    _playingClip = null;
                    _playingClipIndex = -1;
                    _lastGapPlaybackTickUtc = DateTime.UtcNow;
                    if (PreviewMedia.Visibility != Visibility.Hidden)
                        PreviewMedia.Visibility = Visibility.Hidden;
                    try { if (_mediaReady && PreviewMedia.IsPlaying) _ = PreviewMedia.Pause(); } catch { }
                    MonitorMediaHudText.Text = $"GAP · {MontageProject.FormatShort(nextClip.TimelineStart - clip.TimelineEnd)}";
                    _uiTimer.Start();
                }
                else
                {
                    _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(260);
                    _ = SyncPlayerToMontagePositionAsync(nextClip.TimelineStart, autoPlay: true, forceExactSeek: true);
                }
            }
            else if (_project.TotalDuration - clip.TimelineEnd > TimeSpan.FromMilliseconds(45))
            {
                _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(180);
                _montagePosition = clip.TimelineEnd;
                _playingClip = null;
                _playingClipIndex = -1;
                _lastGapPlaybackTickUtc = DateTime.UtcNow;
                if (PreviewMedia.Visibility != Visibility.Hidden)
                    PreviewMedia.Visibility = Visibility.Hidden;
                try { if (_mediaReady && PreviewMedia.IsPlaying) _ = PreviewMedia.Pause(); } catch { }
                MonitorMediaHudText.Text = "Overlay / Audio · Playing";
                _uiTimer.Start();
            }
            else if (LoopToggle.IsChecked == true && _project.TotalDuration > TimeSpan.Zero)
            {
                _transitionGuardUntil = DateTime.UtcNow.AddMilliseconds(260);
                _ = SyncPlayerToMontagePositionAsync(TimeSpan.Zero, autoPlay: true, forceExactSeek: true);
            }
            else
            {
                _montagePosition = _project.TotalDuration;
                _ = PausePlaybackAsync();
                UpdateTimeLabels();
                UpdatePlayheadVisual();
            }
        }
    }

    // Transport, Jog Wheel, Frame Stepping & Track Audio Mixing

    private void MonitorSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeTransformHandle != null)
        {
            CommitProgramTransformDrag();
            e.Handled = true;
            return;
        }

        if (_didTransformDrag)
        {
            _didTransformDrag = false;
            return;
        }

        if (_isTransformMode && e.OriginalSource is DependencyObject dep &&
            FindAncestorTag<string>(dep) != null)
            return;

        if (EditorPanel.Visibility == Visibility.Visible && HasAnyTimelineClips())
            _ = TogglePlayPauseAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await TogglePlayPauseAsync();

    private async Task TogglePlayPauseAsync()
    {
        if (!HasAnyTimelineClips()) return;

        if (_isPlaying)
        {
            await PausePlaybackAsync();
            return;
        }

        if (_montagePosition >= _project.TotalDuration - TimeSpan.FromMilliseconds(60))
            _montagePosition = TimeSpan.Zero;

        _isPlaying = true;
        UpdatePlayPauseUi(true);
        await SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: true, forceExactSeek: true);
    }

    private async Task PausePlaybackAsync()
    {
        _isPlaying = false;
        _pendingPlayOnOpen = false;
        _queuedAutoPlay = false;
        foreach (OverlayChannelState ch in _videoOverlayChannels.Concat(_audioChannels))
        {
            ch.PendingPlayOnOpen = false;
            ch.QueuedAutoPlay = false;
        }
        _uiTimer.Stop();

        static async Task SafePauseAsync(Unosquare.FFME.MediaElement? media, bool ready)
        {
            if (media == null || !ready) return;
            try
            {
                if (media.MediaState == MediaPlaybackState.Play || media.IsPlaying)
                    await media.Pause();
            }
            catch { }
        }

        await SafePauseAsync(PreviewMedia, _mediaReady);
        for (int i = 0; i < _videoOverlayChannels.Length; i++)
            await SafePauseAsync(GetVideoOverlayMediaElement(i + 1), _videoOverlayChannels[i].MediaReady);
        for (int i = 0; i < _audioChannels.Length; i++)
            await SafePauseAsync(GetAudioMediaElement(i), _audioChannels[i].MediaReady);

        UpdatePlayPauseUi(false);
    }

    private void UpdatePlayPauseUi(bool playing) =>
        PlayPauseIcon.Text = playing ? "\uE769" : "\uE768";

    private async void StepBackFrame_Click(object sender, RoutedEventArgs e) =>
        await StepFrameAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -5 : -1);

    private async void StepForwardFrame_Click(object sender, RoutedEventArgs e) =>
        await StepFrameAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1);

    private async Task StepFrameAsync(int frameDelta)
    {
        if (!HasAnyTimelineClips() || frameDelta == 0) return;
        await PausePlaybackAsync();

        double fps = _playingClip?.SourceFps > 1 ? _playingClip.SourceFps : _project.SequenceFps;
        fps = Math.Clamp(fps, 12.0, 120.0);
        double frameDur = 1.0 / fps;

        TimeSpan snappedBase = MontageProject.SnapToFrame(_montagePosition, fps);
        TimeSpan target = ClampMontageTime(snappedBase + TimeSpan.FromSeconds(frameDelta * frameDur));

        if (Math.Abs((target - _montagePosition).TotalSeconds) < frameDur * 0.25)
            target = ClampMontageTime(_montagePosition + TimeSpan.FromSeconds(frameDelta * frameDur));

        _jogVisualOffset += frameDelta * 7.0;
        RenderJogTicks(_jogVisualOffset);

        await SyncPlayerToMontagePositionAsync(target, autoPlay: false, forceExactSeek: true);
        EnsurePlayheadVisible();
    }

    private void JogWheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasAnyTimelineClips()) return;
        _isDraggingJog = true;
        _jogLastX = e.GetPosition(JogWheelBorder).X;
        _jogAccumPx = 0;
        _ = PausePlaybackAsync();
        JogWheelBorder.CaptureMouse();
        JogWheelBorder.BorderBrush = BrushFrom("#2D8CFF");
        e.Handled = true;
    }

    private void JogWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingJog) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndJogDrag();
            return;
        }

        double x = e.GetPosition(JogWheelBorder).X;
        double dx = x - _jogLastX;
        _jogLastX = x;
        _jogVisualOffset += dx;
        RenderJogTicks(_jogVisualOffset);

        _jogAccumPx += dx;
        const double pxPerFrameStep = 7.0;
        if (Math.Abs(_jogAccumPx) >= pxPerFrameStep)
        {
            int steps = (int)(_jogAccumPx / pxPerFrameStep);
            _jogAccumPx -= steps * pxPerFrameStep;
            _ = StepFrameAsync(steps);
        }
    }

    private void JogWheel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingJog)
            EndJogDrag();
        e.Handled = true;
    }

    private void JogWheel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasAnyTimelineClips()) return;
        int steps = e.Delta > 0 ? 1 : -1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            steps *= 5;
        _ = StepFrameAsync(steps);
        e.Handled = true;
    }

    private void EndJogDrag()
    {
        _isDraggingJog = false;
        JogWheelBorder.BorderBrush = BrushFrom("#353945");
        if (JogWheelBorder.IsMouseCaptured)
            JogWheelBorder.ReleaseMouseCapture();
    }

    private async void NudgeBack_Click(object sender, RoutedEventArgs e) => await SeekMontageRelativeAsync(-1);
    private async void NudgeForward_Click(object sender, RoutedEventArgs e) => await SeekMontageRelativeAsync(1);

    private async Task SeekMontageRelativeAsync(double seconds)
    {
        if (_project.TotalDuration <= TimeSpan.Zero) return;
        TimeSpan target = ClampMontageTime(_montagePosition + TimeSpan.FromSeconds(seconds));
        await SyncPlayerToMontagePositionAsync(target, autoPlay: _isPlaying, forceExactSeek: true);
        EnsurePlayheadVisible();
    }

    private void CycleSpeed_Click(object sender, RoutedEventArgs e)
    {
        int idx = Array.FindIndex(SpeedSteps, s => Math.Abs(s - _speedRatio) < 0.05);
        _speedRatio = SpeedSteps[(idx + 1) % SpeedSteps.Length];
        SpeedButton.Content = $"{_speedRatio:0.##}×";
        ApplyPlayerAudioAndSpeed();
    }

    private void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        SyncMuteUi();
        ApplyPlayerAudioAndSpeed();
        RenderTimeline();
    }

    private void TrackVideoMute_Click(object sender, RoutedEventArgs e)
    {
        int layer = ParseElementLayerTag(sender, 1);
        bool isChecked = (sender as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true;
        _isVideoTrackMuted[layer] = isChecked;
        _ = SyncVideoOverlayLayerAsync(layer, _montagePosition, autoPlay: _isPlaying, forceSeek: true);
        if (layer < MontageProject.MaxVideoLayers)
            _ = SyncAudioChannelAsync(layer, _montagePosition, autoPlay: _isPlaying, forceSeek: true);
        ApplyPlayerAudioAndSpeed();
        RenderTimeline();
    }

    private void TrackAudioMute_Click(object sender, RoutedEventArgs e)
    {
        int trackIdx = ParseElementLayerTag(sender, 0);
        bool isChecked = (sender as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true;
        _isAudioTrackMuted[trackIdx] = isChecked;
        SyncMuteUi();
        _ = SyncAudioChannelAsync(trackIdx, _montagePosition, autoPlay: _isPlaying, forceSeek: true);
        ApplyPlayerAudioAndSpeed();
        RenderTimeline();
    }

    private void SyncMuteUi()
    {
        if (MuteIcon != null)
            MuteIcon.Text = _isMuted ? "\uE74F" : "\uE767";
        if (TrackMuteButton != null)
            TrackMuteButton.IsChecked = _isAudioTrackMuted[0];
        if (TrackA2MuteButton != null)
            TrackA2MuteButton.IsChecked = _isAudioTrackMuted[1];
        if (TrackA3MuteButton != null)
            TrackA3MuteButton.IsChecked = _isAudioTrackMuted[2];
        if (TrackA4MuteButton != null)
            TrackA4MuteButton.IsChecked = _isAudioTrackMuted[3];
        if (VolumePercentText != null)
            VolumePercentText.Text = _isMuted ? "Mute" : $"{(int)Math.Round(_volume * 100)}%";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _volume = e.NewValue;
        if (_isMuted && _volume > 0.01)
        {
            _isMuted = false;
            RenderTimeline();
        }
        SyncMuteUi();
        ApplyPlayerAudioAndSpeed();
    }

    private void ProgramScrubSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_project.TotalDuration <= TimeSpan.Zero) return;
        _isScrubbingMonitor = true;
        _wasPlayingBeforeScrub = _isPlaying;
        _ = PausePlaybackAsync();
    }

    private void ProgramScrubSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingScrubSlider || !IsInitialized || _project.TotalDuration <= TimeSpan.Zero) return;
        if (!_isScrubbingMonitor && !ProgramScrubSlider.IsMouseOver) return;
        TimeSpan target = TimeSpan.FromSeconds(_project.TotalDuration.TotalSeconds * (e.NewValue / 1000.0));
        _ = SyncPlayerToMontagePositionAsync(target, autoPlay: false, forceExactSeek: false);
    }

    private async void ProgramScrubSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        bool wasPlaying = _wasPlayingBeforeScrub;
        _isScrubbingMonitor = false;
        _wasPlayingBeforeScrub = false;
        if (_project.TotalDuration <= TimeSpan.Zero || ProgramScrubSlider == null) return;
        TimeSpan target = TimeSpan.FromSeconds(_project.TotalDuration.TotalSeconds * (ProgramScrubSlider.Value / 1000.0));
        await SyncPlayerToMontagePositionAsync(target, autoPlay: wasPlaying, forceExactSeek: true);
    }

    private void ApplyChannelAudioAndSpeed(Unosquare.FFME.MediaElement? media, double clipGain, bool trackOrMasterMuted)
    {
        if (media == null) return;
        try
        {
            bool targetMuted = trackOrMasterMuted || clipGain <= 0.005;
            double targetVol = Math.Clamp(_volume * clipGain, 0.0, 1.0);

            if (media.IsMuted != targetMuted)
                media.IsMuted = targetMuted;
            if (Math.Abs(media.Volume - targetVol) > 0.003)
                media.Volume = targetVol;
            if (Math.Abs(media.SpeedRatio - _speedRatio) > 0.003)
                media.SpeedRatio = _speedRatio;
        }
        catch { }
    }

    private void ApplyPlayerAudioAndSpeed()
    {
        // Video channels: play embedded audio ONLY when aligned with its separated A1..A3 clip
        var (v1UseEmbedded, a1Clip) = ResolveAudioPlaybackModeForLayer(0, _montagePosition);
        ApplyChannelAudioAndSpeed(
            PreviewMedia,
            a1Clip?.AudioVolume ?? 1.0,
            !v1UseEmbedded || _isMuted || _isAudioTrackMuted[0]);

        for (int layer = 1; layer <= 2; layer++)
        {
            Unosquare.FFME.MediaElement? vMedia = GetVideoOverlayMediaElement(layer);
            if (_videoOverlayChannels[layer - 1].MediaReady && vMedia != null)
            {
                var (useEmbedded, aClip) = ResolveAudioPlaybackModeForLayer(layer, _montagePosition);
                ApplyChannelAudioAndSpeed(
                    vMedia,
                    aClip?.AudioVolume ?? 1.0,
                    !useEmbedded || _isMuted || _isAudioTrackMuted[layer]);
            }
        }

        // Dedicated A1..A4 channels (active when unlinked, shifted, or standalone audio)
        for (int aTrack = 0; aTrack < MontageProject.MaxAudioTracks; aTrack++)
        {
            if (!_audioChannels[aTrack].MediaReady) continue;
            Unosquare.FFME.MediaElement? aMedia = GetAudioMediaElement(aTrack);
            bool embeddedTakesOver = aTrack < MontageProject.MaxVideoLayers &&
                                     ResolveAudioPlaybackModeForLayer(aTrack, _montagePosition).UseVideoEmbedded;
            ApplyChannelAudioAndSpeed(
                aMedia,
                _audioChannels[aTrack].PlayingClip?.AudioVolume ?? 1.0,
                embeddedTakesOver || _isMuted || _isAudioTrackMuted[aTrack]);
        }
    }
}
