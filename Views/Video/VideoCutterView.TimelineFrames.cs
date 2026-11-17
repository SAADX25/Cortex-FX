using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CortexFX.Core.Video;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    private static readonly SolidColorBrush TileBgBrush = CreateFrozenBrush("#1A2029");
    private static readonly SolidColorBrush TileSeparatorBrush = CreateFrozenBrush("#10141A");
    private static readonly SolidColorBrush TrimGripIdleBrush = CreateFrozenBrush("#66FFFFFF");
    private static readonly SolidColorBrush TrimGripHoverBrush = CreateFrozenBrush("#FBBF24");

    private sealed record ClipEdgeHandle(MontageClipItem Clip, bool IsStart);
    private sealed record ClipMoveGrip(MontageClipItem Clip);
    private sealed record ClipAudioStripTrigger(MontageClipItem Clip);
    private sealed class ClipAudioVolumeHandle
    {
        public ClipAudioVolumeHandle(
            MontageClipItem clip,
            double waveHeight,
            Canvas waveHost,
            float[]? peaks,
            System.Windows.Shapes.Path? wavePath,
            Line volumeLine,
            Border volumeHitStrip,
            Border? volumeBadge,
            TextBlock? volumeBadgeText,
            TextBlock? headerDbText,
            double visStartNorm,
            double visEndNorm,
            double visWidth)
        {
            Clip = clip;
            WaveHeight = waveHeight;
            WaveHost = waveHost;
            Peaks = peaks;
            WavePath = wavePath;
            VolumeLine = volumeLine;
            VolumeHitStrip = volumeHitStrip;
            VolumeBadge = volumeBadge;
            VolumeBadgeText = volumeBadgeText;
            HeaderDbText = headerDbText;
            VisStartNorm = visStartNorm;
            VisEndNorm = visEndNorm;
            VisWidth = visWidth;
        }

        public MontageClipItem Clip { get; }
        public double WaveHeight { get; }
        public Canvas WaveHost { get; }
        public float[]? Peaks { get; }
        public System.Windows.Shapes.Path? WavePath { get; }
        public Line VolumeLine { get; }
        public Border VolumeHitStrip { get; }
        public Border? VolumeBadge { get; }
        public TextBlock? VolumeBadgeText { get; }
        public TextBlock? HeaderDbText { get; }
        public double VisStartNorm { get; }
        public double VisEndNorm { get; }
        public double VisWidth { get; }
        public bool IsDraggingOrHover { get; set; }
    }

    private sealed record MoveClipDragSnapshot(
        MontageClipItem Clip,
        Border? VideoBlock,
        Border? AudioBlock,
        double OrigLeft,
        double Width);

    private sealed class FilmstripTileSlot
    {
        public FilmstripTileSlot(double sampleSeconds, double spanSeconds, double timelineLeftX, Border border)
        {
            SampleSeconds = sampleSeconds;
            SpanSeconds = spanSeconds;
            TimelineLeftX = timelineLeftX;
            Border = border;
        }

        public double SampleSeconds { get; }
        public double SpanSeconds { get; }
        public double TimelineLeftX { get; }
        public Border Border { get; }
        public ImageSource? CurrentFrame { get; set; }
        public Image? ImageControl { get; set; }
    }

    private sealed record RenderedTimelineClip(
        MontageClipItem Clip,
        Border? VideoBlock,
        Border? AudioBlock,
        IReadOnlyList<FilmstripTileSlot> TileSlots,
        ClipAudioVolumeHandle? AudioHandle);

    private sealed class AssetExtractionJob
    {
        public AssetExtractionJob(CancellationTokenSource cts, double activeSnapDistanceSeconds)
        {
            Cts = cts;
            ActiveSnapDistanceSeconds = activeSnapDistanceSeconds;
        }

        public CancellationTokenSource Cts { get; }
        public double ActiveSnapDistanceSeconds { get; }
        public bool FramesCompleted { get; set; }
    }

    private readonly Dictionary<string, AssetExtractionJob> _assetExtractionJobs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RenderedTimelineClip> _renderedTimelineClips = new();
    private System.Windows.Threading.DispatcherTimer? _timelineSettleTimer;
    private System.Windows.Threading.DispatcherTimer? _viewportRefineTimer;
    private CancellationTokenSource? _viewportRefineCts;

    private double _renderedMinX;
    private double _renderedMaxX;
    private double _renderedTotalWidth;

    private List<MoveClipDragSnapshot>? _moveDragSnapshots;
    private TimeSpan _moveResolvedTargetTime;
    private bool _moveHoverIsV2;
    private double _movePxPerSec = 10.0;
    private double _currentTimelinePxPerSec = 10.0;
    private double _currentTimelineSurfaceWidth = 100.0;
    private const double TimelineTrailingRunwayPx = 110.0;
    private const double TimelineRulerHeight = 28.0;
    private const double TimelineV2RowHeight = 70.0;
    private const double TimelineV1RowHeight = 90.0;
    private const double TimelineA2RowHeight = 48.0;
    private double _moveMouseOffsetX;
    private int _moveSettleGeneration;

    private bool HasAnyTimelineClips() =>
        _project.Clips.Count > 0 || _project.VideoOverlayClips.Count > 0 || _project.AudioOverlayClips.Count > 0;

    private int TotalTimelineClipCount() =>
        _project.Clips.Count + _project.VideoOverlayClips.Count + _project.AudioOverlayClips.Count;

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private bool IsFramesExtracting(string sourcePath) =>
        _assetExtractionJobs.TryGetValue(sourcePath, out AssetExtractionJob? job) && !job.FramesCompleted;

    private double GetActiveSnapDistance(string sourcePath) =>
        _assetExtractionJobs.TryGetValue(sourcePath, out AssetExtractionJob? job) && !job.FramesCompleted
            ? job.ActiveSnapDistanceSeconds
            : double.MaxValue;

    private void CancelAllAssetExtractions()
    {
        foreach (AssetExtractionJob job in _assetExtractionJobs.Values)
        {
            try { job.Cts.Cancel(); } catch { }
        }
        _assetExtractionJobs.Clear();
        CancelViewportRefinement();
    }

    private void CancelAssetExtraction(string filePath)
    {
        if (_assetExtractionJobs.Remove(filePath, out AssetExtractionJob? job))
        {
            try { job.Cts.Cancel(); } catch { }
        }
    }

    private void CancelViewportRefinement()
    {
        _viewportRefineTimer?.Stop();
        if (_viewportRefineCts != null)
        {
            try { _viewportRefineCts.Cancel(); } catch { }
            _viewportRefineCts.Dispose();
            _viewportRefineCts = null;
        }
    }

    private void StartAssetVisualExtraction(SourceMediaAsset asset)
    {
        if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath))
            return;

        if (asset.Info.Duration <= TimeSpan.Zero)
            return;

        if (_assetExtractionJobs.ContainsKey(asset.FilePath))
            return;

        double durationSec = Math.Max(0.2, asset.Info.Duration.TotalSeconds);
        var (wave1, wave2, wave1Spacing) = SourceMediaAsset.BuildProgressiveSampleWaves(durationSec);
        double existingTolerance = Math.Min(0.25, durationSec / Math.Max(1, (wave1.Length + wave2.Length) * 2.5));

        double[] missingWave1 = asset.Info.HasVideo
            ? wave1.Where(p => !asset.HasFrameNear(p, existingTolerance)).ToArray()
            : Array.Empty<double>();
        double[] missingWave2 = asset.Info.HasVideo
            ? wave2.Where(p => !asset.HasFrameNear(p, existingTolerance)).ToArray()
            : Array.Empty<double>();

        bool needFrames = missingWave1.Length > 0 || missingWave2.Length > 0;
        bool needAudio = asset.Info.HasAudio && asset.AudioPeaks == null;

        if (!needFrames && !needAudio)
        {
            _project.RefreshAllThumbnails(asset.FilePath);
            UpdateClipThumbnailsAndTilesInPlace(asset, allowFallbackFill: true);
            ScheduleViewportFrameRefinement();
            return;
        }

        var cts = new CancellationTokenSource();
        var job = new AssetExtractionJob(cts, wave1Spacing * 0.68)
        {
            FramesCompleted = !needFrames
        };
        _assetExtractionJobs[asset.FilePath] = job;

        if (needFrames && asset.Frames.Count == 0)
        {
            TimelineFramesHint.Text = "Building filmstrip...";
            TimelineFramesHint.Visibility = Visibility.Visible;
        }

        _ = ExtractAssetVisualsAsync(asset, missingWave1, missingWave2, needFrames, needAudio, job);
    }

    private async Task ExtractAssetVisualsAsync(
        SourceMediaAsset asset,
        double[] wave1Positions,
        double[] wave2Positions,
        bool needFrames,
        bool needAudio,
        AssetExtractionJob job)
    {
        CancellationToken token = job.Cts.Token;
        Task audioTask = needAudio
            ? ExtractAudioPeaksAsync(asset, token)
            : Task.CompletedTask;
        Task framesTask = needFrames
            ? ExtractProgressiveFramesAsync(asset, wave1Positions, wave2Positions, job, token)
            : Task.CompletedTask;

        try
        {
            await Task.WhenAll(framesTask, audioTask);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            bool wasCancelled = token.IsCancellationRequested;
            if (!Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_assetExtractionJobs.TryGetValue(asset.FilePath, out AssetExtractionJob? active) &&
                        ReferenceEquals(active, job))
                    {
                        _assetExtractionJobs.Remove(asset.FilePath);
                    }

                    if (!wasCancelled && needFrames)
                    {
                        TimelineFramesHint.Visibility = Visibility.Collapsed;
                        UpdateClipThumbnailsAndTilesInPlace(asset, allowFallbackFill: true);
                        _project.RefreshAllThumbnails(asset.FilePath);
                        ScheduleViewportFrameRefinement();
                    }
                });
            }
            job.Cts.Dispose();
        }
    }

    private async Task ExtractProgressiveFramesAsync(
        SourceMediaAsset asset,
        double[] wave1Positions,
        double[] wave2Positions,
        AssetExtractionJob job,
        CancellationToken token)
    {
        await ExtractProgressiveWaveAsync(asset, wave1Positions, maxConcurrency: 3, job, token);
        await ExtractProgressiveWaveAsync(asset, wave2Positions, maxConcurrency: 2, job, token);

        if (!Dispatcher.HasShutdownStarted)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                job.FramesCompleted = true;
                TimelineFramesHint.Visibility = Visibility.Collapsed;
                UpdateClipThumbnailsAndTilesInPlace(asset, allowFallbackFill: true);
                _project.RefreshAllThumbnails(asset.FilePath);
            });
        }
    }

    private async Task ExtractProgressiveWaveAsync(
        SourceMediaAsset asset,
        double[] positions,
        int maxConcurrency,
        AssetExtractionJob job,
        CancellationToken token)
    {
        if (positions.Length == 0) return;

        await Parallel.ForEachAsync(
            positions,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = maxConcurrency },
            async (posSeconds, ct) =>
            {
                FilmstripFrame? frame = await CaptureSingleThumbnailAsync(asset.FilePath, posSeconds, ct).ConfigureAwait(false);
                if (frame == null || ct.IsCancellationRequested || Dispatcher.HasShutdownStarted)
                    return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    if (!_assetExtractionJobs.TryGetValue(asset.FilePath, out AssetExtractionJob? active) ||
                        !ReferenceEquals(active, job))
                    {
                        return;
                    }

                    asset.AddOrUpdateFrame(frame);
                    if (TimelineFramesHint.Visibility == Visibility.Visible)
                        TimelineFramesHint.Visibility = Visibility.Collapsed;

                    UpdateClipThumbnailsAndTilesInPlace(asset, allowFallbackFill: false);
                });
            });
    }

    private void ScheduleViewportFrameRefinement()
    {
        if (EditorPanel.Visibility != Visibility.Visible || (_project.Clips.Count == 0 && _project.VideoOverlayClips.Count == 0))
            return;
        if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath))
            return;

        if (_viewportRefineTimer == null)
        {
            _viewportRefineTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(140)
            };
            _viewportRefineTimer.Tick += (_, _) =>
            {
                _viewportRefineTimer.Stop();
                if (_dragMode == TimelineDragMode.None && EditorPanel.Visibility == Visibility.Visible)
                    StartViewportFrameRefinement();
            };
        }

        _viewportRefineTimer.Stop();
        _viewportRefineTimer.Start();
    }

    private void StartViewportFrameRefinement()
    {
        if (_renderedTimelineClips.Count == 0 || _project.TotalDuration <= TimeSpan.Zero)
            return;

        double viewLeft = TimelineScrollViewer.HorizontalOffset;
        double viewW = GetTimelineViewportWidth();
        double viewRight = viewLeft + viewW;

        var missingByAsset = new Dictionary<SourceMediaAsset, List<double>>();
        double focusCenterSec = 0;
        int totalQueued = 0;

        foreach (RenderedTimelineClip rendered in _renderedTimelineClips)
        {
            if (!_project.Assets.TryGetValue(rendered.Clip.SourcePath, out SourceMediaAsset? asset) || !asset.Info.HasVideo)
                continue;
            if (IsFramesExtracting(asset.FilePath))
                continue;

            foreach (FilmstripTileSlot slot in rendered.TileSlots)
            {
                if (slot.TimelineLeftX + slot.Border.Width < viewLeft - 60 || slot.TimelineLeftX > viewRight + 60)
                    continue;

                double tolerance = Math.Max(0.025, slot.SpanSeconds * 0.45);
                if (asset.HasFrameNear(slot.SampleSeconds, tolerance))
                    continue;

                if (!missingByAsset.TryGetValue(asset, out List<double>? list))
                {
                    list = new List<double>();
                    missingByAsset[asset] = list;
                }
                list.Add(slot.SampleSeconds);
                focusCenterSec = slot.SampleSeconds;
                if (++totalQueued >= 34)
                    break;
            }
            if (totalQueued >= 34)
                break;
        }

        if (missingByAsset.Count == 0)
            return;

        CancelViewportRefinement();
        _viewportRefineCts = new CancellationTokenSource();
        CancellationToken token = _viewportRefineCts.Token;
        _ = RunViewportRefinementAsync(missingByAsset, focusCenterSec, token);
    }

    private async Task RunViewportRefinementAsync(
        Dictionary<SourceMediaAsset, List<double>> missingByAsset,
        double focusCenterSec,
        CancellationToken token)
    {
        try
        {
            foreach (var (asset, rawTimes) in missingByAsset)
            {
                if (token.IsCancellationRequested) break;

                List<double> sorted = rawTimes.Distinct().OrderBy(t => t).ToList();
                if (sorted.Count == 0) continue;

                // Split into contiguous dense bursts vs isolated timestamps.
                var groups = new List<List<double>>();
                var current = new List<double> { sorted[0] };
                for (int i = 1; i < sorted.Count; i++)
                {
                    double gap = sorted[i] - sorted[i - 1];
                    if (gap <= 0.75 && sorted[i] - current[0] <= 3.5 && current.Count < 16)
                    {
                        current.Add(sorted[i]);
                    }
                    else
                    {
                        groups.Add(current);
                        current = new List<double> { sorted[i] };
                    }
                }
                groups.Add(current);

                foreach (List<double> group in groups)
                {
                    if (token.IsCancellationRequested) break;

                    if (group.Count >= 4)
                    {
                        List<FilmstripFrame> burst = await CaptureBurstThumbnailsAsync(asset.FilePath, group, token).ConfigureAwait(false);
                        if (burst.Count > 0 && !token.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (token.IsCancellationRequested) return;
                                foreach (FilmstripFrame f in burst)
                                    asset.AddOrUpdateFrame(f, replaceToleranceSeconds: 0.005);
                                asset.TrimFrameCache(420, focusCenterSec);
                                UpdateClipThumbnailsAndTilesInPlace(asset, allowFallbackFill: false);
                            });
                        }
                    }
                    else
                    {
                        await Parallel.ForEachAsync(
                            group,
                            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 3 },
                            async (pos, ct) =>
                            {
                                FilmstripFrame? frame = await CaptureSingleThumbnailAsync(asset.FilePath, pos, ct).ConfigureAwait(false);
                                if (frame == null || ct.IsCancellationRequested || Dispatcher.HasShutdownStarted)
                                    return;

                                await Dispatcher.InvokeAsync(() =>
                                {
                                    if (ct.IsCancellationRequested) return;
                                    asset.AddOrUpdateFrame(frame, replaceToleranceSeconds: 0.005);
                                    asset.TrimFrameCache(420, focusCenterSec);
                                    UpdateClipThumbnailsAndTilesInPlace(asset, allowFallbackFill: false);
                                });
                            }).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task<List<FilmstripFrame>> CaptureBurstThumbnailsAsync(
        string filePath, IReadOnlyList<double> positions, CancellationToken token)
    {
        var result = new List<FilmstripFrame>();
        if (positions.Count < 2) return result;

        try
        {
            double start = Math.Max(0, positions[0]);
            double end = positions[^1];
            double span = Math.Max(0.08, end - start);
            double fps = Math.Clamp((positions.Count - 1) / span, 1.0, 60.0);

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-threads", "2",
                "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                "-t", (span + 0.25).ToString("0.###", CultureInfo.InvariantCulture),
                "-i", filePath,
                "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", $"fps={fps.ToString("0.###", CultureInfo.InvariantCulture)},scale=-2:112:flags=bilinear,format=yuvj420p",
                "-frames:v", positions.Count.ToString(CultureInfo.InvariantCulture),
                "-q:v", "5",
                "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1"
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return result;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(14));
            using var reg = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            using var ms = new MemoryStream();
            Task<string> errTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await errTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || ms.Length == 0)
                return result;

            byte[] buffer = ms.ToArray();
            List<byte[]> jpegs = SplitMjpegFrames(buffer);
            int count = Math.Min(jpegs.Count, positions.Count);
            for (int i = 0; i < count; i++)
            {
                BitmapImage? bmp = DecodeFrozenBitmap(jpegs[i]);
                if (bmp != null)
                    result.Add(new FilmstripFrame(positions[i], bmp));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        return result;
    }

    private static List<byte[]> SplitMjpegFrames(byte[] data)
    {
        var frames = new List<byte[]>();
        int start = -1;
        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD8)
            {
                if (start < 0) start = i;
            }
            else if (data[i] == 0xFF && data[i + 1] == 0xD9 && start >= 0)
            {
                int len = (i + 2) - start;
                if (len > 128)
                {
                    var slice = new byte[len];
                    Buffer.BlockCopy(data, start, slice, 0, len);
                    frames.Add(slice);
                }
                start = -1;
                i++;
            }
        }
        return frames;
    }

    private static BitmapImage? DecodeFrozenBitmap(byte[] jpegBytes)
    {
        try
        {
            using var ms = new MemoryStream(jpegBytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private async Task<FilmstripFrame?> CaptureSingleThumbnailAsync(
        string filePath, double positionSeconds, CancellationToken token)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-threads", "2",
                "-ss", positionSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", filePath,
                "-map", "0:v:0", "-an", "-sn", "-dn",
                "-frames:v", "1",
                "-vf", "scale=-2:128:flags=bilinear,format=yuvj420p",
                "-q:v", "4",
                "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1"
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var reg = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            using var ms = new MemoryStream();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || ms.Length == 0)
                return null;

            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return new FilmstripFrame(positionSeconds, bmp);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateClipThumbnailsAndTilesInPlace(SourceMediaAsset asset, bool allowFallbackFill)
    {
        foreach (MontageClipItem clip in _project.Clips.Concat(_project.VideoOverlayClips))
        {
            if (!string.Equals(clip.SourcePath, asset.FilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            double midSec = clip.Start.TotalSeconds + clip.Duration.TotalSeconds * 0.5;
            ImageSource? thumb = asset.FindNearestFrame(midSec);
            if (thumb != null)
                clip.Thumbnail = thumb;
        }

        double maxDist = allowFallbackFill ? double.MaxValue : GetActiveSnapDistance(asset.FilePath);
        foreach (RenderedTimelineClip item in _renderedTimelineClips)
        {
            if (!string.Equals(item.Clip.SourcePath, asset.FilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            for (int t = 0; t < item.TileSlots.Count; t++)
            {
                FilmstripTileSlot slot = item.TileSlots[t];
                double slotLimit = allowFallbackFill ? maxDist : Math.Max(0.04, Math.Min(maxDist, slot.SpanSeconds * 0.75));
                ImageSource? frame = asset.FindNearestFrame(slot.SampleSeconds, slotLimit);
                if (frame == null && allowFallbackFill)
                    frame = item.Clip.Thumbnail;
                if (frame == null || ReferenceEquals(frame, slot.CurrentFrame))
                    continue;

                if (slot.ImageControl == null)
                {
                    var img = CreateTileImage(frame);
                    slot.Border.Child = img;
                    slot.ImageControl = img;
                }
                else
                {
                    slot.ImageControl.Source = frame;
                }
                slot.CurrentFrame = frame;
            }
        }
    }

    private static Image CreateTileImage(ImageSource frame)
    {
        var img = new Image
        {
            Source = frame,
            Stretch = Stretch.UniformToFill,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    private async Task ExtractAudioPeaksAsync(SourceMediaAsset asset, CancellationToken token)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", asset.FilePath,
                "-vn", "-sn", "-dn",
                "-ac", "1", "-ar", "4000", "-f", "s16le", "pipe:1"
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            using var reg = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            using var ms = new MemoryStream();
            Task<string> errTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await errTask;

            byte[] raw = ms.ToArray();
            int sampleCount = raw.Length / 2;
            if (sampleCount < 4) return;

            const int bucketCount = 1600;
            var peaks = new float[bucketCount];
            var activeValues = new List<float>(bucketCount);

            for (int b = 0; b < bucketCount; b++)
            {
                int start = (int)((long)b * sampleCount / bucketCount);
                int end = Math.Max(start + 1, (int)((long)(b + 1) * sampleCount / bucketCount));
                float localMax = 0;
                double sumSq = 0;
                int count = 0;
                for (int s = start; s < end && s < sampleCount; s++)
                {
                    short sample = BitConverter.ToInt16(raw, s * 2);
                    float abs = Math.Abs(sample / 32768f);
                    if (abs > localMax) localMax = abs;
                    sumSq += abs * abs;
                    count++;
                }
                float rms = count > 0 ? MathF.Sqrt((float)(sumSq / count)) : 0f;
                float combined = localMax * 0.65f + rms * 0.35f;
                peaks[b] = combined;
                if (combined > 0.002f)
                    activeValues.Add(combined);
            }

            float refLevel = 0.05f;
            if (activeValues.Count > 0)
            {
                activeValues.Sort();
                int p95Idx = Math.Clamp((int)(activeValues.Count * 0.95), 0, activeValues.Count - 1);
                refLevel = Math.Max(0.01f, activeValues[p95Idx]);
            }

            for (int b = 0; b < bucketCount; b++)
            {
                float norm = Math.Min(1.25f, peaks[b] / refLevel);
                peaks[b] = norm <= 0.008f ? 0.03f : Math.Clamp(MathF.Pow(norm, 0.45f), 0.10f, 1f);
            }

            if (!Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        asset.AudioPeaks = peaks;
                        if (EditorPanel.Visibility == Visibility.Visible)
                            RenderTimeline();
                    }
                });
            }
        }
        catch
        {
            // Non-fatal: waveform strip stays quiet if audio extraction fails.
        }
    }

    // Zoom & Viewport Geometry

    private double GetTimelineViewportWidth()
    {
        double w = TimelineScrollViewer.ViewportWidth;
        if (w <= 0) w = TimelineScrollViewer.ActualWidth;
        if (w <= 0) w = 640;
        return w;
    }

    private double ComputeMaxZoomFactor(double viewportW)
    {
        double totalSec = Math.Max(1.0, _project.TotalDuration.TotalSeconds);
        double fps = Math.Clamp(_project.SequenceFps, 12.0, 120.0);
        double maxPxPerSec = fps * 36.0;
        double baseW = Math.Max(220.0, viewportW - TimelineTrailingRunwayPx);
        double raw = (totalSec * maxPxPerSec) / baseW;
        return Math.Clamp(raw, 12.0, 240.0);
    }

    private double SliderToZoomFactor(double sliderValue, double viewportW)
    {
        if (sliderValue <= 0.05) return 1.0;
        double maxZoom = ComputeMaxZoomFactor(viewportW);
        return Math.Pow(maxZoom, Math.Clamp(sliderValue, 0, 100) / 100.0);
    }

    private double ZoomFactorToSlider(double zoomFactor, double viewportW)
    {
        if (zoomFactor <= 1.001) return 0.0;
        double maxZoom = ComputeMaxZoomFactor(viewportW);
        return Math.Clamp(100.0 * Math.Log(Math.Clamp(zoomFactor, 1.0, maxZoom)) / Math.Log(maxZoom), 0, 100);
    }

    private (double MinX, double MaxX, double ViewLeft, double ViewRight) GetVisiblePixelWindow(double totalWidth)
    {
        double viewLeft = TimelineScrollViewer.HorizontalOffset;
        double viewW = GetTimelineViewportWidth();
        double viewRight = viewLeft + viewW;
        double buffer = Math.Max(260, viewW * 0.55);
        double minX = Math.Max(0, viewLeft - buffer);
        double maxX = Math.Min(totalWidth, viewRight + buffer);
        return (minX, maxX, viewLeft, viewRight);
    }

    private (double PixelsPerSecond, double SequenceWidth, double SurfaceWidth) GetTimelineMetrics()
    {
        double viewportW = GetTimelineViewportWidth();
        double maxZoom = ComputeMaxZoomFactor(viewportW);
        if (_zoomFactor > maxZoom)
            _zoomFactor = maxZoom;

        double totalSec = Math.Max(0.1, _project.TotalDuration.TotalSeconds);
        double baseW = Math.Max(120.0, viewportW - TimelineTrailingRunwayPx);
        double seqW = Math.Max(120.0, baseW * _zoomFactor);
        double surfaceW = Math.Max(viewportW, seqW + TimelineTrailingRunwayPx);
        double pxPerSec = _project.TotalDuration > TimeSpan.Zero ? seqW / totalSec : 12.0;
        return (pxPerSec, seqW, surfaceW);
    }

    private double ComputeTimelineSurfaceWidth()
    {
        bool zoomed = _zoomFactor > 1.01;
        ScrollBarVisibility desiredHScroll = zoomed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (TimelineScrollViewer.HorizontalScrollBarVisibility != desiredHScroll)
            TimelineScrollViewer.HorizontalScrollBarVisibility = desiredHScroll;

        var (pxPerSec, _, surfaceW) = GetTimelineMetrics();
        _currentTimelinePxPerSec = pxPerSec;
        _currentTimelineSurfaceWidth = surfaceW;

        TimelineSurface.Width = surfaceW;
        TimelineRulerCanvas.Width = surfaceW;
        TimelineClipsCanvas.Width = surfaceW;
        TimelineAudioCanvas.Width = surfaceW;
        if (TimelineAudio2Canvas != null)
            TimelineAudio2Canvas.Width = surfaceW;
        TimelineOverlayCanvas.Width = surfaceW;

        UpdateZoomReadout(pxPerSec);
        return surfaceW;
    }

    private void UpdateZoomReadout(double pxPerSec)
    {
        if (ZoomReadoutText == null) return;
        double fps = _project.SequenceFps;
        double pxPerFrame = pxPerSec / Math.Max(1.0, fps);

        if (_zoomFactor <= 1.02)
            ZoomReadoutText.Text = "1.0×";
        else if (pxPerFrame >= 28.0)
            ZoomReadoutText.Text = "Frames";
        else if (_zoomFactor >= 10)
            ZoomReadoutText.Text = $"{_zoomFactor:F0}×";
        else
            ZoomReadoutText.Text = $"{_zoomFactor:F1}×";
    }

    private void UpdateTimelineLayoutInPlace()
    {
        double totalWidth = ComputeTimelineSurfaceWidth();

        if (_project.TotalDuration <= TimeSpan.Zero || !HasAnyTimelineClips())
        {
            UpdatePlayheadVisual();
            RenderProgramCutMarkers();
            return;
        }

        if (_renderedTimelineClips.Count != TotalTimelineClipCount())
        {
            RenderTimeline();
            return;
        }

        double pxPerSec = _currentTimelinePxPerSec;
        var (minX, maxX, _, _) = GetVisiblePixelWindow(totalWidth);
        TimelineRulerCanvas.Children.Clear();
        RenderTimeRuler(totalWidth, pxPerSec, minX, maxX);

        foreach (RenderedTimelineClip item in _renderedTimelineClips)
        {
            double left = item.Clip.TimelineStart.TotalSeconds * pxPerSec;
            double width = Math.Max(4, item.Clip.Duration.TotalSeconds * pxPerSec);
            if (item.VideoBlock != null)
            {
                Canvas.SetLeft(item.VideoBlock, left);
                item.VideoBlock.Width = width;
            }
            if (item.AudioBlock != null)
            {
                Canvas.SetLeft(item.AudioBlock, left);
                item.AudioBlock.Width = width;
            }
        }

        UpdatePlayheadVisual();
        RenderProgramCutMarkers();
        ScheduleTimelineSettleRefresh();
    }

    private void UpdateTrimVisualInPlace(MontageClipItem trimmedClip, bool isStart, double pxPerSec)
    {
        double left = trimmedClip.TimelineStart.TotalSeconds * pxPerSec;
        double width = Math.Max(4, trimmedClip.Duration.TotalSeconds * pxPerSec);

        foreach (RenderedTimelineClip item in _renderedTimelineClips)
        {
            if (!ReferenceEquals(item.Clip, trimmedClip))
                continue;

            if (item.VideoBlock != null)
            {
                Canvas.SetLeft(item.VideoBlock, left);
                item.VideoBlock.Width = width;
            }
            if (item.AudioBlock != null)
            {
                Canvas.SetLeft(item.AudioBlock, left);
                item.AudioBlock.Width = width;
            }
        }
    }

    private void ScheduleTimelineSettleRefresh()
    {
        if (_timelineSettleTimer == null)
        {
            _timelineSettleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(75)
            };
            _timelineSettleTimer.Tick += (_, _) =>
            {
                _timelineSettleTimer.Stop();
                if (_dragMode == TimelineDragMode.None && EditorPanel.Visibility == Visibility.Visible)
                    RenderTimeline();
            };
        }

        _timelineSettleTimer.Stop();
        _timelineSettleTimer.Start();
    }

    private void RenderTimeline()
    {
        _moveSettleGeneration++;
        _timelineSettleTimer?.Stop();
        double totalWidth = ComputeTimelineSurfaceWidth();

        TimelineRulerCanvas.Children.Clear();
        TimelineV2Canvas?.Children.Clear();
        TimelineClipsCanvas.Children.Clear();
        TimelineAudioCanvas.Children.Clear();
        TimelineAudio2Canvas?.Children.Clear();
        _renderedTimelineClips.Clear();

        TimeSpan totalDur = _project.TotalDuration;
        if (totalDur <= TimeSpan.Zero || !HasAnyTimelineClips())
        {
            _renderedMinX = 0;
            _renderedMaxX = totalWidth;
            _renderedTotalWidth = totalWidth;
            RenderTimeRuler(totalWidth, _currentTimelinePxPerSec, 0, totalWidth);
            UpdatePlayheadVisual();
            RenderProgramCutMarkers();
            return;
        }

        double pxPerSec = _currentTimelinePxPerSec;
        var (renderMinX, renderMaxX, _, _) = GetVisiblePixelWindow(totalWidth);
        _renderedMinX = renderMinX;
        _renderedMaxX = renderMaxX;
        _renderedTotalWidth = totalWidth;

        RenderTimeRuler(totalWidth, pxPerSec, renderMinX, renderMaxX);

        foreach (var (gapStart, gapEnd) in _project.GetGaps(audioOverlayTrack: false))
        {
            double gapLeft = gapStart.TotalSeconds * pxPerSec;
            double gapWidth = (gapEnd - gapStart).TotalSeconds * pxPerSec;
            if (gapWidth < 4) continue;
            Border gapBlock = BuildTimelineGapBlock(gapStart, gapEnd, gapWidth);
            Canvas.SetLeft(gapBlock, gapLeft);
            Canvas.SetTop(gapBlock, 5);
            TimelineClipsCanvas.Children.Add(gapBlock);
        }

        bool multiSource = _project.Clips
            .Concat(_project.VideoOverlayClips)
            .Concat(_project.AudioOverlayClips)
            .Select(c => c.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;

        if (TimelineV2Canvas != null)
        {
            for (int i = 0; i < _project.VideoOverlayClips.Count; i++)
            {
                MontageClipItem v2Clip = _project.VideoOverlayClips[i];
                double left = v2Clip.TimelineStart.TotalSeconds * pxPerSec;
                double width = Math.Max(4, v2Clip.Duration.TotalSeconds * pxPerSec);

                double visClipLeft = Math.Max(0, renderMinX - left);
                double visClipRight = Math.Min(width, renderMaxX - left);

                var (v2Block, v2Slots) = BuildPremiereVideoBlock(
                    v2Clip, left, width, pxPerSec, visClipLeft, visClipRight, multiSource, isVideoOverlay: true);
                Canvas.SetLeft(v2Block, left);
                Canvas.SetTop(v2Block, 3);
                TimelineV2Canvas.Children.Add(v2Block);

                _renderedTimelineClips.Add(new RenderedTimelineClip(v2Clip, v2Block, null, v2Slots, null));
            }
        }

        for (int i = 0; i < _project.Clips.Count; i++)
        {
            MontageClipItem clip = _project.Clips[i];
            double left = clip.TimelineStart.TotalSeconds * pxPerSec;
            double width = Math.Max(4, clip.Duration.TotalSeconds * pxPerSec);

            double visClipLeft = Math.Max(0, renderMinX - left);
            double visClipRight = Math.Min(width, renderMaxX - left);

            var (videoBlock, tileSlots) = BuildPremiereVideoBlock(
                clip, left, width, pxPerSec, visClipLeft, visClipRight, multiSource, isVideoOverlay: false);
            Canvas.SetLeft(videoBlock, left);
            Canvas.SetTop(videoBlock, 3);
            TimelineClipsCanvas.Children.Add(videoBlock);

            var (audioBlock, audioHandle) = BuildPremiereAudioBlock(
                clip, width, visClipLeft, visClipRight, multiSource, isOverlayTrack: false);
            Canvas.SetLeft(audioBlock, left);
            Canvas.SetTop(audioBlock, 3);
            TimelineAudioCanvas.Children.Add(audioBlock);

            _renderedTimelineClips.Add(new RenderedTimelineClip(clip, videoBlock, audioBlock, tileSlots, audioHandle));
        }

        if (TimelineAudio2Canvas != null)
        {
            for (int i = 0; i < _project.AudioOverlayClips.Count; i++)
            {
                MontageClipItem ovClip = _project.AudioOverlayClips[i];
                double left = ovClip.TimelineStart.TotalSeconds * pxPerSec;
                double width = Math.Max(4, ovClip.Duration.TotalSeconds * pxPerSec);

                double visClipLeft = Math.Max(0, renderMinX - left);
                double visClipRight = Math.Min(width, renderMaxX - left);

                var (ovBlock, ovHandle) = BuildPremiereAudioBlock(
                    ovClip, width, visClipLeft, visClipRight, multiSource, isOverlayTrack: true);
                Canvas.SetLeft(ovBlock, left);
                Canvas.SetTop(ovBlock, 3);
                TimelineAudio2Canvas.Children.Add(ovBlock);

                _renderedTimelineClips.Add(new RenderedTimelineClip(ovClip, null, ovBlock, Array.Empty<FilmstripTileSlot>(), ovHandle));
            }
        }

        UpdatePlayheadVisual();
        RenderProgramCutMarkers();
        if (_dragMode == TimelineDragMode.None)
            ScheduleViewportFrameRefinement();
    }

    private Border BuildTimelineGapBlock(TimeSpan gapStart, TimeSpan gapEnd, double width)
    {
        TimeSpan gapDur = gapEnd - gapStart;
        var gapMenu = new ContextMenu { Style = (Style)FindResource("StudioContextMenu") };
        var rippleItem = new MenuItem
        {
            Header = $"Ripple Delete Gap ({MontageProject.FormatShort(gapDur)})",
            Style = (Style)FindResource("StudioMenuItem")
        };
        rippleItem.Click += (_, _) =>
        {
            if (_project.RippleDeleteGap(gapStart, gapEnd, audioOverlayTrack: false))
            {
                UpdateProjectHeaderAndHud();
                UpdateTimeLabels();
                UpdateExportSummaryHint();
                RenderTimeline();
                ShowTimelineToast("Gap closed");
            }
        };
        gapMenu.Items.Add(rippleItem);

        var gapBox = new Border
        {
            Width = width,
            Height = 80,
            CornerRadius = new CornerRadius(2),
            Background = BrushFrom("#0F131A"),
            BorderBrush = BrushFrom("#2A3648"),
            BorderThickness = new Thickness(1),
            ContextMenu = gapMenu,
            ToolTip = $"Empty black gap ({MontageProject.FormatShort(gapDur)}) — Right-click for Ripple Delete"
        };
        if (width >= 58)
        {
            gapBox.Child = new TextBlock
            {
                Text = width >= 92 ? $"GAP · {MontageProject.FormatShort(gapDur)}" : "GAP",
                Foreground = BrushFrom("#64748B"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        return gapBox;
    }

    private void RenderTimeRuler(double totalWidth, double pxPerSec, double minX, double maxX)
    {
        double fps = Math.Clamp(_project.SequenceFps, 12.0, 120.0);
        double frameDur = 1.0 / fps;
        double pxPerFrame = pxPerSec * frameDur;
        int roundedFps = Math.Max(1, (int)Math.Round(fps));

        var tickBrush = BrushFrom("#383838");
        var majorBrush = BrushFrom("#6E7480");
        var frameTickBrush = BrushFrom("#3B5575");
        var textBrush = BrushFrom("#A4AAB5");

        double startSec = Math.Max(0, minX / Math.Max(1.0, pxPerSec));
        double endSec = Math.Max(startSec, maxX / Math.Max(1.0, pxPerSec));

        if (pxPerFrame >= 12.0)
        {
            // Frame-accurate Premiere ruler mode!
            int[] frameCandidates = { 1, 2, 5, 10, 15, roundedFps, roundedFps * 2 };
            int majorFrames = frameCandidates[^1];
            foreach (int fc in frameCandidates)
            {
                if (fc * pxPerFrame >= 76)
                {
                    majorFrames = fc;
                    break;
                }
            }

            long firstFrame = Math.Max(0, (long)Math.Floor(startSec * fps));
            long lastFrame = (long)Math.Ceiling(endSec * fps);

            for (long f = firstFrame; f <= lastFrame; f++)
            {
                double t = f / fps;
                double x = t * pxPerSec;
                if (x > totalWidth - 1) break;

                bool isMajor = (f % majorFrames) == 0;
                var tick = new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = isMajor ? 16 : 22,
                    Y2 = 28,
                    Stroke = isMajor ? majorBrush : frameTickBrush,
                    StrokeThickness = 1
                };
                TimelineRulerCanvas.Children.Add(tick);

                if (isMajor && x < totalWidth - 46)
                {
                    var label = new TextBlock
                    {
                        Text = MontageProject.FormatSmpteTimecode(TimeSpan.FromSeconds(t), fps),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 9,
                        Foreground = textBrush
                    };
                    Canvas.SetLeft(label, x + 4);
                    Canvas.SetTop(label, 2);
                    TimelineRulerCanvas.Children.Add(label);
                }
            }
            return;
        }

        double[] candidateSteps = { 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double majorStep = candidateSteps[^1];
        foreach (double step in candidateSteps)
        {
            if (step * pxPerSec >= 76)
            {
                majorStep = step;
                break;
            }
        }

        double minorStep = majorStep / 5.0;
        long firstStep = Math.Max(0, (long)Math.Floor(startSec / minorStep));
        long lastStep = (long)Math.Ceiling(endSec / minorStep);

        for (long idx = firstStep; idx <= lastStep; idx++)
        {
            double t = idx * minorStep;
            double x = t * pxPerSec;
            if (x > totalWidth - 1) break;

            bool isMajor = (idx % 5) == 0;
            var tick = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = isMajor ? 17 : 22,
                Y2 = 28,
                Stroke = isMajor ? majorBrush : tickBrush,
                StrokeThickness = 1
            };
            TimelineRulerCanvas.Children.Add(tick);

            if (isMajor && x < totalWidth - 42)
            {
                string labelText = majorStep < 5.0
                    ? MontageProject.FormatSmpteTimecode(TimeSpan.FromSeconds(t), fps)
                    : MontageProject.FormatShort(TimeSpan.FromSeconds(t));
                var label = new TextBlock
                {
                    Text = labelText,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 9,
                    Foreground = textBrush
                };
                Canvas.SetLeft(label, x + 4);
                Canvas.SetTop(label, 2);
                TimelineRulerCanvas.Children.Add(label);
            }
        }
    }

    private (Border Block, IReadOnlyList<FilmstripTileSlot> TileSlots) BuildPremiereVideoBlock(
        MontageClipItem clip,
        double clipTimelineLeft,
        double clipWidth,
        double pxPerSec,
        double visClipLeft,
        double visClipRight,
        bool multiSource,
        bool isVideoOverlay = false)
    {
        double blockHeight = isVideoOverlay ? 64.0 : 84.0;
        const double headerHeight = 16.0;
        double filmHeight = blockHeight - headerHeight;

        bool v2Muted = isVideoOverlay && TrackV2MuteButton?.IsChecked == true;
        var borderBrush = clip.IsSelected
            ? BrushFrom("#E6E6E6")
            : BrushFrom(isVideoOverlay ? "#483370" : "#203044");
        var headerBrush = clip.IsSelected
            ? BrushFrom(isVideoOverlay ? "#8664CC" : "#5B88BD")
            : BrushFrom(isVideoOverlay ? "#6547A1" : "#436793");

        var outer = new Border
        {
            Width = clipWidth,
            Height = blockHeight,
            CornerRadius = new CornerRadius(2),
            Background = BrushFrom(isVideoOverlay ? "#211B2D" : "#1F2630"),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(clip.IsSelected ? 1.5 : 1),
            Opacity = v2Muted ? 0.45 : 1.0,
            ClipToBounds = true,
            Tag = clip,
            ContextMenu = BuildClipContextMenu(clip)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(filmHeight) });

        // Premiere Video Clip Header Bar (Drag to Reorder or Switch V1 <-> V2 Track!)
        var header = new Border
        {
            Background = headerBrush,
            Padding = new Thickness(6, 0, 6, 0),
            Cursor = Cursors.SizeAll,
            Tag = new ClipMoveGrip(clip),
            ToolTip = isVideoOverlay
                ? "V2 Overlay clip · Drag horizontally to place, drag down to move to V1"
                : "V1 Video clip · Drag horizontally to place, drag up to move to V2 Overlay"
        };
        if (clipWidth >= 32)
        {
            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (clipWidth >= 56)
            {
                var grip = new TextBlock
                {
                    Text = "⋮⋮",
                    FontSize = 9,
                    Foreground = BrushFrom("#B5FFFFFF"),
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(grip, 0);
                headerRow.Children.Add(grip);
            }

            string trackPrefix = isVideoOverlay ? "[V2] " : (multiSource ? "" : $"#{clip.OrderIndex} ");
            string label = $"{trackPrefix}{clip.SourceFileName} · {clip.DisplayDuration}";
            var titleTb = new TextBlock
            {
                Text = label,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleTb, 1);
            headerRow.Children.Add(titleTb);

            if ((clip.HasCustomTransform || clip.IsTrimmed) && clipWidth >= 112)
            {
                string pillText = clip.HasCustomTransform ? clip.ScalePercentLabel : "TRIM";
                var pill = new Border
                {
                    Background = BrushFrom("#35000000"),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(4, 1, 0, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = pillText,
                        FontSize = 7.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushFrom(clip.HasCustomTransform ? "#C4B5FD" : "#FDE68A")
                    }
                };
                Grid.SetColumn(pill, 2);
                headerRow.Children.Add(pill);
            }

            header.Child = headerRow;
        }
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Viewport-Virtualized Filmstrip Row
        var stripCanvas = new Canvas
        {
            Width = clipWidth,
            Height = filmHeight,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        var tileSlots = new List<FilmstripTileSlot>();

        if (visClipRight > visClipLeft)
        {
            _project.Assets.TryGetValue(clip.SourcePath, out SourceMediaAsset? asset);
            double fps = clip.SourceFps > 1 ? clip.SourceFps : 30.0;
            double pxPerFrame = pxPerSec / fps;
            double clipStartSec = clip.Start.TotalSeconds;
            double clipDurSec = Math.Max(0.05, clip.Duration.TotalSeconds);
            bool extracting = IsFramesExtracting(clip.SourcePath);
            double activeMaxDist = extracting ? GetActiveSnapDistance(clip.SourcePath) : double.MaxValue;

            if (pxPerFrame >= 30.0)
            {
                // Individual Frame Mode: 1 tile = 1 exact video frame!
                double tileW = pxPerFrame;
                int totalFrameTiles = Math.Max(1, (int)Math.Ceiling(clipDurSec * fps));
                int firstTile = Math.Max(0, (int)Math.Floor(visClipLeft / tileW));
                int lastTile = Math.Min(totalFrameTiles - 1, (int)Math.Ceiling(visClipRight / tileW));
                long baseFrameIdx = MontageProject.TimeToFrameIndex(clip.Start, fps);

                for (int t = firstTile; t <= lastTile; t++)
                {
                    double x = t * tileW;
                    double w = Math.Min(tileW, clipWidth - x);
                    if (w <= 1) continue;

                    double sampleSec = Math.Clamp(clipStartSec + (t + 0.5) / fps, clipStartSec, clip.End.TotalSeconds);
                    double tileMaxDist = extracting ? activeMaxDist : Math.Max(0.03, (1.0 / fps) * 0.65);
                    ImageSource? frame = asset?.FindNearestFrame(sampleSec, tileMaxDist) ??
                                         asset?.FindNearestFrame(sampleSec, double.MaxValue) ??
                                         clip.Thumbnail;

                    var tileContainer = new Grid();
                    var tileBorder = new Border
                    {
                        Width = w,
                        Height = filmHeight,
                        Background = TileBgBrush,
                        BorderBrush = TileSeparatorBrush,
                        BorderThickness = new Thickness(0, 0, 1, 0),
                        ClipToBounds = true
                    };
                    var imageSlotBorder = new Border { ClipToBounds = true };
                    var slot = new FilmstripTileSlot(sampleSec, 1.0 / fps, clipTimelineLeft + x, imageSlotBorder);
                    if (frame != null)
                    {
                        var img = CreateTileImage(frame);
                        imageSlotBorder.Child = img;
                        slot.ImageControl = img;
                        slot.CurrentFrame = frame;
                    }
                    tileContainer.Children.Add(imageSlotBorder);

                    if (w >= 30)
                    {
                        var frameTag = new Border
                        {
                            Background = BrushFrom("#B00D1016"),
                            CornerRadius = new CornerRadius(2),
                            Padding = new Thickness(3, 0, 3, 0),
                            Margin = new Thickness(2),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Child = new TextBlock
                            {
                                Text = $"f{baseFrameIdx + t}",
                                FontFamily = new FontFamily("Consolas"),
                                FontSize = 7.5,
                                Foreground = BrushFrom("#B7D4F5")
                            }
                        };
                        tileContainer.Children.Add(frameTag);
                    }

                    tileBorder.Child = tileContainer;
                    Canvas.SetLeft(tileBorder, x);
                    Canvas.SetTop(tileBorder, 0);
                    stripCanvas.Children.Add(tileBorder);
                    tileSlots.Add(slot);
                }
            }
            else
            {
                double aspect = clip.SourceWidth > 0 && clip.SourceHeight > 0
                    ? Math.Clamp(clip.SourceWidth / (double)clip.SourceHeight, 0.52, 1.85)
                    : 16.0 / 9.0;
                double idealTileW = Math.Clamp(filmHeight * aspect * 0.78, 54, 108);
                int totalTiles = Math.Max(1, (int)Math.Ceiling(clipWidth / idealTileW));
                double tileW = clipWidth / totalTiles;
                double slotSpanSec = clipDurSec / totalTiles;

                int firstTile = Math.Max(0, (int)Math.Floor(visClipLeft / tileW));
                int lastTile = Math.Min(totalTiles - 1, (int)Math.Ceiling(visClipRight / tileW));

                for (int t = firstTile; t <= lastTile; t++)
                {
                    double x = t * tileW;
                    double sampleSec = clipStartSec + clipDurSec * ((t + 0.5) / totalTiles);
                    double tileMaxDist = extracting ? activeMaxDist : double.MaxValue;
                    ImageSource? frame = asset?.FindNearestFrame(sampleSec, tileMaxDist);
                    if (frame == null && !extracting)
                        frame = clip.Thumbnail;

                    var tileBorder = new Border
                    {
                        Width = tileW + 0.5,
                        Height = filmHeight,
                        Background = TileBgBrush,
                        BorderBrush = TileSeparatorBrush,
                        BorderThickness = new Thickness(0, 0, t < totalTiles - 1 ? 1 : 0, 0),
                        ClipToBounds = true
                    };
                    var slot = new FilmstripTileSlot(sampleSec, slotSpanSec, clipTimelineLeft + x, tileBorder);
                    if (frame != null)
                    {
                        var img = CreateTileImage(frame);
                        tileBorder.Child = img;
                        slot.ImageControl = img;
                        slot.CurrentFrame = frame;
                    }

                    Canvas.SetLeft(tileBorder, x);
                    Canvas.SetTop(tileBorder, 0);
                    stripCanvas.Children.Add(tileBorder);
                    tileSlots.Add(slot);
                }
            }
        }
        Grid.SetRow(stripCanvas, 1);
        root.Children.Add(stripCanvas);

        // Left & Right Non-Destructive Trim Handles
        if (clipWidth >= 14)
        {
            root.Children.Add(CreateEdgeTrimHandle(clip, isStart: true, spanRows: 2));
            root.Children.Add(CreateEdgeTrimHandle(clip, isStart: false, spanRows: 2));
        }

        outer.Child = root;
        return (outer, tileSlots);
    }

    private bool _isAudioExpanded;
    internal bool IsAudioTrackExpanded => _isAudioExpanded;

    private double GetAudioTrackRowHeight() => _isAudioExpanded ? 92.0 : 60.0;
    private double GetAudioBlockHeight() => _isAudioExpanded ? 86.0 : 54.0;

    internal void SetAudioTrackExpanded(bool expanded)
    {
        _isAudioExpanded = expanded;
        double a1RowHeight = GetAudioTrackRowHeight();
        double totalTrackHeight = TimelineRulerHeight + TimelineV2RowHeight + TimelineV1RowHeight + a1RowHeight + TimelineA2RowHeight;

        if (TrackHeaderA1Row != null) TrackHeaderA1Row.Height = new GridLength(a1RowHeight);
        if (TimelineA1Row != null) TimelineA1Row.Height = new GridLength(a1RowHeight);
        if (TimelineSurface != null) TimelineSurface.Height = totalTrackHeight;
        if (TrackA1ScaleHint != null) TrackA1ScaleHint.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (AudioStripToggle != null) AudioStripToggle.IsChecked = expanded;
        if (AudioGainStrip != null) AudioGainStrip.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        double overlayTrackSpan = TimelineV2RowHeight + TimelineV1RowHeight + a1RowHeight + TimelineA2RowHeight;
        if (MoveSlotPreviewRect != null) MoveSlotPreviewRect.Height = Math.Max(110, TimelineV1RowHeight + a1RowHeight - 6.0);
        if (RazorGuideLine != null) RazorGuideLine.Y2 = totalTrackHeight;
        if (DropInsertionLine != null) DropInsertionLine.Height = overlayTrackSpan;
        if (PlayheadLine != null) PlayheadLine.Height = overlayTrackSpan + 2.0;

        if (expanded)
            SyncAudioGainStripUi();

        if (EditorPanel?.Visibility == Visibility.Visible)
            RenderTimeline();
    }

    private const double AudioUnityBottomRatio = 0.65;

    private static double VolumeToLineBottomRatio(double volume)
    {
        double clamped = Math.Clamp(volume, 0.0, 2.0);
        return clamped <= 1.0
            ? clamped * AudioUnityBottomRatio
            : AudioUnityBottomRatio + (clamped - 1.0) * 0.31;
    }

    private static double LineBottomRatioToVolume(double bottomRatio)
    {
        double clamped = Math.Clamp(bottomRatio, 0.0, 0.96);
        return clamped <= AudioUnityBottomRatio
            ? Math.Clamp(clamped / AudioUnityBottomRatio, 0.0, 1.0)
            : Math.Clamp(1.0 + (clamped - AudioUnityBottomRatio) / 0.31, 1.0, 2.0);
    }

    private static double ComputeVolumeLineY(double volume, double waveHeight)
    {
        double ratio = VolumeToLineBottomRatio(volume);
        double usable = Math.Max(10.0, waveHeight - 6.0);
        return Math.Clamp((waveHeight - 3.0) - ratio * usable, 3.0, Math.Max(3.0, waveHeight - 3.0));
    }

    private static double LineYToVolume(double localY, double waveHeight)
    {
        double usable = Math.Max(10.0, waveHeight - 6.0);
        double bottomRatio = ((waveHeight - 3.0) - localY) / usable;
        return LineBottomRatioToVolume(bottomRatio);
    }

    private (Border Block, ClipAudioVolumeHandle? AudioHandle) BuildPremiereAudioBlock(
        MontageClipItem clip,
        double clipWidth,
        double visClipLeft,
        double visClipRight,
        bool multiSource,
        bool isOverlayTrack = false)
    {
        double blockHeight = isOverlayTrack ? 42.0 : GetAudioBlockHeight();
        const double headerHeight = 14;
        double waveHeight = blockHeight - headerHeight;

        bool trackMuted = isOverlayTrack
            ? TrackA2MuteButton?.IsChecked == true
            : TrackMuteButton?.IsChecked == true;
        var borderBrush = clip.IsSelected
            ? BrushFrom("#E6E6E6")
            : BrushFrom(isOverlayTrack ? "#1B433F" : "#1B363F");
        var headerBrush = clip.IsSelected
            ? BrushFrom(isOverlayTrack ? "#2E9E93" : "#458C9E")
            : BrushFrom(isOverlayTrack ? "#216E68" : "#326C7B");

        var outer = new Border
        {
            Width = clipWidth,
            Height = blockHeight,
            CornerRadius = new CornerRadius(2),
            Background = BrushFrom(isOverlayTrack ? "#162C2B" : "#1B2D35"),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(clip.IsSelected ? 1.5 : 1),
            Opacity = trackMuted ? 0.45 : 1.0,
            ClipToBounds = true,
            Tag = clip,
            ContextMenu = BuildClipContextMenu(clip)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(waveHeight) });

        var header = new Border
        {
            Background = headerBrush,
            Padding = new Thickness(6, 0, 5, 0),
            Cursor = Cursors.SizeAll,
            Tag = new ClipMoveGrip(clip)
        };
        TextBlock? headerDbText = null;
        if (clipWidth >= 42)
        {
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleBlock = new TextBlock
            {
                Text = isOverlayTrack
                    ? $"{clip.SourceFileName} [A2]"
                    : (multiSource ? clip.SourceFileName : $"{clip.SourceFileName} [A1]"),
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFrom("#EBF7FA"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleBlock, 0);
            headerGrid.Children.Add(titleBlock);

            if (clip.HasAudio && clipWidth >= 88)
            {
                headerDbText = new TextBlock
                {
                    Text = clip.AudioVolumeDbLabel,
                    FontSize = 8,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushFrom(clip.AudioVolume <= 0.005
                        ? "#FCA5A5"
                        : clip.IsAudioAdjusted ? "#FDE68A" : "#CBEBF2")
                };
                var dbTriggerPill = new Border
                {
                    Background = BrushFrom("#400A141A"),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(4, 1, 0, 1),
                    Cursor = Cursors.Hand,
                    Tag = new ClipAudioStripTrigger(clip),
                    ToolTip = "Click to expand A1 audio control strip",
                    Child = headerDbText
                };
                Grid.SetColumn(dbTriggerPill, 1);
                headerGrid.Children.Add(dbTriggerPill);
            }

            header.Child = headerGrid;
        }
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var waveContainer = new Border
        {
            Background = BrushFrom("#182931"),
            ClipToBounds = true
        };

        ClipAudioVolumeHandle? audioHandle = null;
        _project.Assets.TryGetValue(clip.SourcePath, out SourceMediaAsset? asset);
        if (clip.HasAudio)
        {
            var waveHost = new Canvas
            {
                Height = waveHeight,
                Width = clipWidth,
                Background = Brushes.Transparent,
                ClipToBounds = true
            };

            float[]? peaks = asset?.AudioPeaks;
            System.Windows.Shapes.Path? wavePath = null;
            double visW = 0, visStartNorm = 0, visEndNorm = 1;
            if (peaks is { Length: > 0 } && visClipRight > visClipLeft)
            {
                visW = Math.Max(4, visClipRight - visClipLeft);
                visStartNorm = Math.Clamp(visClipLeft / clipWidth, 0, 1);
                visEndNorm = Math.Clamp(visClipRight / clipWidth, visStartNorm, 1);

                wavePath = new System.Windows.Shapes.Path
                {
                    Fill = BrushFrom(clip.AudioVolume <= 0.005 ? "#496975" : "#7CE2F7"),
                    Data = BuildClipWaveformGeometry(peaks, clip, visW, waveHeight - 2, visStartNorm, visEndNorm),
                    Width = visW,
                    Height = waveHeight - 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(wavePath, visClipLeft);
                Canvas.SetTop(wavePath, 2);
                waveHost.Children.Add(wavePath);
            }

            // Faint 0 dB unity reference guide line (Adobe Premiere standard)
            double unityY = ComputeVolumeLineY(1.0, waveHeight);
            var unityGuide = new Line
            {
                X1 = 0,
                X2 = clipWidth,
                Y1 = unityY,
                Y2 = unityY,
                Stroke = BrushFrom("#3896B5"),
                StrokeThickness = 0.8,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                IsHitTestVisible = false
            };
            waveHost.Children.Add(unityGuide);

            // Draggable horizontal Audio Volume Rubberband Line ("سطر تحكم في درجة الصوت")
            double lineY = ComputeVolumeLineY(clip.AudioVolume, waveHeight);
            var volumeLine = new Line
            {
                X1 = 0,
                X2 = clipWidth,
                Y1 = lineY,
                Y2 = lineY,
                Stroke = BrushFrom(GetVolumeLineColorHex(clip)),
                StrokeThickness = 1.7,
                IsHitTestVisible = false
            };
            waveHost.Children.Add(volumeLine);

            TextBlock? volumeBadgeText = null;
            Border? volumeBadge = null;
            if (clipWidth >= 56)
            {
                volumeBadgeText = new TextBlock
                {
                    Text = clip.AudioVolumeDbLabel,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushFrom(clip.AudioVolume <= 0.005 ? "#FCA5A5" : "#EBF7FA")
                };
                volumeBadge = new Border
                {
                    Background = BrushFrom("#CC111C22"),
                    BorderBrush = BrushFrom(clip.IsAudioAdjusted ? "#D97706" : "#356C7E"),
                    BorderThickness = new Thickness(0.8),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(4, 0, 4, 0),
                    IsHitTestVisible = false,
                    Visibility = _isAudioExpanded || clip.IsAudioAdjusted ? Visibility.Visible : Visibility.Collapsed,
                    Child = volumeBadgeText
                };
                Canvas.SetLeft(volumeBadge, 6);
                Canvas.SetTop(volumeBadge, Math.Max(2, lineY > waveHeight * 0.45 ? lineY - 15 : lineY + 3));
                waveHost.Children.Add(volumeBadge);
            }

            var hitStrip = new Border
            {
                Width = clipWidth,
                Height = 16,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeNS,
                ToolTip = $"Clip Audio Level: {clip.AudioVolumeDbLabel} ({Math.Round(clip.AudioVolume * 100)}%) — Drag up/down or double-click to reset 0 dB"
            };
            Canvas.SetLeft(hitStrip, 0);
            Canvas.SetTop(hitStrip, Math.Clamp(lineY - 8, 0, Math.Max(0, waveHeight - 16)));
            waveHost.Children.Add(hitStrip);

            audioHandle = new ClipAudioVolumeHandle(
                clip,
                waveHeight,
                waveHost,
                peaks,
                wavePath,
                volumeLine,
                hitStrip,
                volumeBadge,
                volumeBadgeText,
                headerDbText,
                visStartNorm,
                visEndNorm,
                visW);
            hitStrip.Tag = audioHandle;
            hitStrip.MouseEnter += (_, _) =>
            {
                audioHandle.IsDraggingOrHover = true;
                volumeLine.Stroke = BrushFrom("#FFFFFF");
                volumeLine.StrokeThickness = 2.2;
                if (volumeBadge != null) volumeBadge.Visibility = Visibility.Visible;
            };
            hitStrip.MouseLeave += (_, _) =>
            {
                if (_dragMode == TimelineDragMode.DragAudioVolume && ReferenceEquals(_dragVolumeHandle, audioHandle))
                    return;
                audioHandle.IsDraggingOrHover = false;
                volumeLine.Stroke = BrushFrom(GetVolumeLineColorHex(clip));
                volumeLine.StrokeThickness = 1.7;
                if (volumeBadge != null && !_isAudioExpanded && !clip.IsAudioAdjusted)
                    volumeBadge.Visibility = Visibility.Collapsed;
            };

            waveContainer.Child = waveHost;
        }
        else if (clipWidth > 44)
        {
            waveContainer.IsHitTestVisible = false;
            waveContainer.Child = new TextBlock
            {
                Text = "Silent",
                FontSize = 8.5,
                Foreground = BrushFrom("#64828E"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
        }
        Grid.SetRow(waveContainer, 1);
        root.Children.Add(waveContainer);

        if (clipWidth >= 14)
        {
            root.Children.Add(CreateEdgeTrimHandle(clip, isStart: true, spanRows: 2));
            root.Children.Add(CreateEdgeTrimHandle(clip, isStart: false, spanRows: 2));
        }

        outer.Child = root;
        return (outer, audioHandle);
    }

    private static string GetVolumeLineColorHex(MontageClipItem clip)
    {
        if (clip.AudioVolume <= 0.005) return "#F87171";
        if (clip.IsAudioAdjusted) return "#FBBF24";
        return "#EAF6FA";
    }

    private DateTime _lastWaveformRebuildUtc = DateTime.MinValue;

    private void RefreshClipAudioBlockVisual(MontageClipItem clip, bool highlightActive = false)
    {
        RenderedTimelineClip? item = _renderedTimelineClips.FirstOrDefault(r => ReferenceEquals(r.Clip, clip));
        ClipAudioVolumeHandle? handle = item?.AudioHandle;
        if (handle == null) return;

        double lineY = ComputeVolumeLineY(clip.AudioVolume, handle.WaveHeight);
        handle.VolumeLine.Y1 = lineY;
        handle.VolumeLine.Y2 = lineY;
        handle.VolumeLine.Stroke = BrushFrom(highlightActive ? "#FFFFFF" : GetVolumeLineColorHex(clip));
        handle.VolumeLine.StrokeThickness = highlightActive ? 2.2 : 1.7;
        Canvas.SetTop(handle.VolumeHitStrip, Math.Clamp(lineY - 8, 0, Math.Max(0, handle.WaveHeight - 16)));
        handle.VolumeHitStrip.ToolTip = $"Clip Audio Level: {clip.AudioVolumeDbLabel} ({Math.Round(clip.AudioVolume * 100)}%) — Drag up/down or double-click to reset 0 dB";

        if (handle.HeaderDbText != null)
        {
            handle.HeaderDbText.Text = clip.AudioVolumeDbLabel;
            handle.HeaderDbText.Foreground = BrushFrom(clip.AudioVolume <= 0.005
                ? "#FCA5A5"
                : clip.IsAudioAdjusted ? "#FDE68A" : "#CBEBF2");
        }

        if (handle.VolumeBadge != null && handle.VolumeBadgeText != null)
        {
            handle.VolumeBadgeText.Text = clip.AudioVolumeDbLabel;
            handle.VolumeBadgeText.Foreground = BrushFrom(clip.AudioVolume <= 0.005 ? "#FCA5A5" : "#EBF7FA");
            handle.VolumeBadge.BorderBrush = BrushFrom(clip.IsAudioAdjusted ? "#D97706" : "#356C7E");
            handle.VolumeBadge.Visibility = highlightActive || _isAudioExpanded || clip.IsAudioAdjusted
                ? Visibility.Visible
                : Visibility.Collapsed;
            Canvas.SetTop(handle.VolumeBadge, Math.Max(2, lineY > handle.WaveHeight * 0.45 ? lineY - 15 : lineY + 3));
        }

        if (handle.WavePath != null && handle.Peaks is { Length: > 0 } && handle.VisWidth > 0)
        {
            handle.WavePath.Fill = BrushFrom(clip.AudioVolume <= 0.005 ? "#496975" : "#7CE2F7");
            DateTime now = DateTime.UtcNow;
            if (!highlightActive || (now - _lastWaveformRebuildUtc).TotalMilliseconds >= 40)
            {
                _lastWaveformRebuildUtc = now;
                handle.WavePath.Data = BuildClipWaveformGeometry(
                    handle.Peaks,
                    clip,
                    handle.VisWidth,
                    handle.WaveHeight - 2,
                    handle.VisStartNorm,
                    handle.VisEndNorm);
            }
        }
    }

    private Border CreateEdgeTrimHandle(MontageClipItem clip, bool isStart, int spanRows)
    {
        var gripBar = new Border
        {
            Width = 2,
            Margin = new Thickness(isStart ? 1 : 0, 6, isStart ? 0 : 1, 6),
            CornerRadius = new CornerRadius(1),
            Background = TrimGripIdleBrush,
            HorizontalAlignment = isStart ? HorizontalAlignment.Left : HorizontalAlignment.Right
        };

        var handle = new Border
        {
            Width = 8,
            Background = Brushes.Transparent,
            HorizontalAlignment = isStart ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Cursor = Cursors.SizeWE,
            Tag = new ClipEdgeHandle(clip, isStart),
            ToolTip = isStart
                ? "Drag left edge to trim Clip Start (In)"
                : "Drag right edge to trim Clip End (Out)",
            Child = gripBar
        };
        handle.MouseEnter += (_, _) => gripBar.Background = TrimGripHoverBrush;
        handle.MouseLeave += (_, _) => gripBar.Background = TrimGripIdleBrush;
        Grid.SetRowSpan(handle, spanRows);
        return handle;
    }

    private static Geometry BuildClipWaveformGeometry(
        float[] peaks,
        MontageClipItem clip,
        double width,
        double height,
        double clipVisStartNorm,
        double clipVisEndNorm)
    {
        var geom = new StreamGeometry();
        double srcSec = Math.Max(0.1, clip.SourceDuration.TotalSeconds);
        double clipStartRatio = Math.Clamp(clip.Start.TotalSeconds / srcSec, 0, 1);
        double clipEndRatio = Math.Clamp(clip.End.TotalSeconds / srcSec, clipStartRatio, 1);

        double rangeStart = clipStartRatio + (clipEndRatio - clipStartRatio) * clipVisStartNorm;
        double rangeEnd = clipStartRatio + (clipEndRatio - clipStartRatio) * clipVisEndNorm;

        int barCount = Math.Clamp((int)(width / 2.4), 8, 420);
        double barStep = width / barCount;
        double barW = Math.Max(1.1, barStep * 0.72);
        double volScale = Math.Clamp(clip.AudioVolume, 0.0, 2.0);
        bool isMuted = clip.AudioVolume <= 0.005;

        using (StreamGeometryContext ctx = geom.Open())
        {
            for (int b = 0; b < barCount; b++)
            {
                double r = rangeStart + (rangeEnd - rangeStart) * ((b + 0.5) / barCount);
                int idx = Math.Clamp((int)(r * (peaks.Length - 1)), 0, peaks.Length - 1);
                double amp = peaks[idx] * volScale;
                double barH = isMuted ? 1.5 : Math.Clamp(amp * height, 2.0, height);
                double x = b * barStep;
                double yTop = height - barH;

                ctx.BeginFigure(new Point(x, height), true, true);
                ctx.LineTo(new Point(x, yTop), false, false);
                ctx.LineTo(new Point(x + barW, yTop), false, false);
                ctx.LineTo(new Point(x + barW, height), false, false);
            }
        }
        geom.Freeze();
        return geom;
    }

    private ContextMenu BuildClipContextMenu(MontageClipItem clip)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("StudioContextMenu") };

        MenuItem MakeItem(string header, string shortcut, Action action, bool enabled = true)
        {
            var mi = new MenuItem
            {
                Header = header,
                InputGestureText = shortcut,
                Style = (Style)FindResource("StudioMenuItem"),
                IsEnabled = enabled
            };
            mi.Click += (_, _) => action();
            return mi;
        }

        menu.Items.Add(MakeItem(
            "Split at Playhead",
            "S / Ctrl+B",
            () => _ = SplitAtPlayheadAsync(clip.IsAudioOverlay, clip.IsVideoOverlay)));

        if (!clip.IsAudioOverlay)
        {
            menu.Items.Add(MakeItem(
                "Frame / Motion Controls...",
                "M",
                () =>
                {
                    _project.SelectClip(clip);
                    SetTransformMode(true);
                    RenderTimeline();
                }));
            menu.Items.Add(MakeItem(
                clip.IsVideoOverlay ? "Move to V1 Main Track" : "Move to V2 Overlay Track",
                "",
                () =>
                {
                    if (_project.MoveVideoClipTrack(clip, !clip.IsVideoOverlay, clip.TimelineStart, recordUndo: true))
                    {
                        _ = SyncPlayerToMontagePositionAsync(_montagePosition, autoPlay: false, forceExactSeek: true);
                        UpdateProjectHeaderAndHud();
                        UpdateExportSummaryHint();
                        RenderTimeline();
                        ShowTimelineToast(clip.IsVideoOverlay ? "Moved to V2 Overlay" : "Moved to V1 Track");
                    }
                }));
        }

        menu.Items.Add(MakeItem("Audio Level Strip", "dB", () => OpenAudioControlForClip(clip), clip.HasAudio));
        menu.Items.Add(MakeItem("Reset Audio Level (0 dB)", "", () => ResetClipAudioVolume(clip), clip.HasAudio && clip.IsAudioAdjusted));
        menu.Items.Add(new Separator { Style = (Style)FindResource("StudioMenuSeparator") });
        menu.Items.Add(MakeItem("Restore Full Clip Edges", "", () => _ = RestoreClipBoundsAsync(clip), clip.IsTrimmed));
        menu.Items.Add(MakeItem("Duplicate Clip", "Ctrl+D", () => DuplicateClipItem(clip)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("StudioMenuSeparator") });
        menu.Items.Add(MakeItem("Delete Clip (Keep Gap)", "Del", () => _ = RemoveClipWithUiRefreshAsync(clip, ripple: false)));
        menu.Items.Add(MakeItem("Ripple Delete Clip", "Shift+Del", () => _ = RemoveClipWithUiRefreshAsync(clip, ripple: true)));

        return menu;
    }

    // Free Non-Ripple NLE Timeline Drag-Move (Gap Preserving, Track Switching V1 <-> V2, Snap Aware)

    private void BeginMoveClipAnimation(MontageClipItem draggedClip, double mouseX, double mouseY, double totalW, double totalSec)
    {
        _moveSettleGeneration++;
        _movePxPerSec = _currentTimelinePxPerSec > 0 ? _currentTimelinePxPerSec : (totalSec > 0 ? totalW / totalSec : 10.0);

        RenderedTimelineClip? rendered = _renderedTimelineClips.FirstOrDefault(r => ReferenceEquals(r.Clip, draggedClip));
        double origLeft = draggedClip.TimelineStart.TotalSeconds * _movePxPerSec;
        double width = Math.Max(4, draggedClip.Duration.TotalSeconds * _movePxPerSec);

        _moveDragSnapshots = new List<MoveClipDragSnapshot>
        {
            new(draggedClip, rendered?.VideoBlock, rendered?.AudioBlock, origLeft, width)
        };
        _moveResolvedTargetTime = draggedClip.TimelineStart;
        _moveHoverIsV2 = !draggedClip.IsAudioOverlay && (draggedClip.IsVideoOverlay || mouseY < TimelineRulerHeight + TimelineV2RowHeight);
        _moveMouseOffsetX = Math.Clamp(mouseX - origLeft, 0, width);

        ApplyLiftAnimation(rendered?.VideoBlock, liftY: -4.0);
        ApplyLiftAnimation(rendered?.AudioBlock, liftY: -2.0);

        MoveSlotPreviewRect.Width = width;
        ApplyMoveSlotVerticalBounds(draggedClip);

        MoveSlotPreviewRect.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(MoveSlotPreviewRect, origLeft);
        MoveSlotPreviewRect.Opacity = 0;
        MoveSlotPreviewRect.Visibility = Visibility.Visible;
        MoveSlotPreviewRect.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
    }

    private void ApplyMoveSlotVerticalBounds(MontageClipItem draggedClip)
    {
        if (draggedClip.IsAudioOverlay)
        {
            double a2Top = TimelineRulerHeight + TimelineV2RowHeight + TimelineV1RowHeight + GetAudioTrackRowHeight() + 3.0;
            Canvas.SetTop(MoveSlotPreviewRect, a2Top);
            MoveSlotPreviewRect.Height = 42.0;
        }
        else if (_moveHoverIsV2)
        {
            Canvas.SetTop(MoveSlotPreviewRect, TimelineRulerHeight + 3.0);
            MoveSlotPreviewRect.Height = 64.0;
        }
        else
        {
            Canvas.SetTop(MoveSlotPreviewRect, TimelineRulerHeight + TimelineV2RowHeight + 3.0);
            MoveSlotPreviewRect.Height = Math.Max(110, TimelineV1RowHeight + GetAudioTrackRowHeight() - 6.0);
        }
    }

    private void UpdateMoveClipAnimation(double mouseX, double mouseY, double totalW)
    {
        if (_moveDragSnapshots == null || _moveDragSnapshots.Count == 0)
            return;

        MoveClipDragSnapshot dragged = _moveDragSnapshots[0];
        MontageClipItem clip = dragged.Clip;
        double pxPerSec = Math.Max(1.0, _movePxPerSec);
        double maxDragRight = Math.Max(totalW - dragged.Width + 80, dragged.OrigLeft + 120);
        double floatingLeft = Math.Clamp(mouseX - _moveMouseOffsetX, 0, Math.Max(0, maxDragRight));

        if (!clip.IsAudioOverlay)
        {
            bool newHoverV2 = mouseY < TimelineRulerHeight + TimelineV2RowHeight;
            if (newHoverV2 != _moveHoverIsV2)
            {
                _moveHoverIsV2 = newHoverV2;
                ApplyMoveSlotVerticalBounds(clip);
            }
        }

        if (dragged.VideoBlock != null)
        {
            dragged.VideoBlock.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(dragged.VideoBlock, floatingLeft);
        }
        if (dragged.AudioBlock != null)
        {
            dragged.AudioBlock.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(dragged.AudioBlock, floatingLeft);
        }

        TimeSpan rawDesired = TimeSpan.FromSeconds(Math.Max(0.0, floatingLeft / pxPerSec));
        TimeSpan snappedDesired = SnapMovedClipStart(clip, rawDesired, pxPerSec);
        TimeSpan resolvedStart = _project.ResolveMoveTargetTime(clip, snappedDesired, targetVideoOverlay: !clip.IsAudioOverlay ? _moveHoverIsV2 : (bool?)null);
        _moveResolvedTargetTime = resolvedStart;

        double previewLeft = resolvedStart.TotalSeconds * pxPerSec;
        MoveSlotPreviewRect.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(MoveSlotPreviewRect, previewLeft);
        Canvas.SetLeft(DropInsertionLine, Math.Clamp(previewLeft - 1.0, 0, Math.Max(0, totalW - 2.5)));
        DropInsertionLine.Visibility = Visibility.Visible;

        string trackLabel = clip.IsAudioOverlay ? "A2" : (_moveHoverIsV2 ? "V2" : "V1");
        string timeText = MontageProject.FormatSmpteTimecode(resolvedStart, _project.SequenceFps);
        ShowDragBadge($"MOVE [{trackLabel}] → {timeText}", floatingLeft, totalW);
    }

    private TimeSpan SnapMovedClipStart(MontageClipItem clip, TimeSpan desiredStart, double pxPerSec)
    {
        if (!_isSnapEnabled || pxPerSec <= 0)
            return desiredStart;

        double thresholdSec = 9.0 / pxPerSec;
        TimeSpan best = desiredStart;
        double bestDist = thresholdSec;
        TimeSpan dur = clip.Duration;

        void TrySnapEdge(TimeSpan targetEdgeTime)
        {
            double dStart = Math.Abs((desiredStart - targetEdgeTime).TotalSeconds);
            if (dStart < bestDist)
            {
                bestDist = dStart;
                best = targetEdgeTime;
            }
            if (targetEdgeTime >= dur)
            {
                TimeSpan startForEndSnap = targetEdgeTime - dur;
                double dEnd = Math.Abs((desiredStart - startForEndSnap).TotalSeconds);
                if (dEnd < bestDist)
                {
                    bestDist = dEnd;
                    best = startForEndSnap;
                }
            }
        }

        TrySnapEdge(TimeSpan.Zero);
        TrySnapEdge(_montagePosition);

        foreach (MontageClipItem other in _project.Clips.Concat(_project.VideoOverlayClips).Concat(_project.AudioOverlayClips))
        {
            if (ReferenceEquals(other, clip)) continue;
            TrySnapEdge(other.TimelineStart);
            TrySnapEdge(other.TimelineEnd);
        }

        return best < TimeSpan.Zero ? TimeSpan.Zero : best;
    }

    private bool FinishMoveClipAnimation(MontageClipItem draggedClip)
    {
        List<MoveClipDragSnapshot>? snapshots = _moveDragSnapshots;
        TimeSpan resolvedTime = _moveResolvedTargetTime;
        bool targetV2 = !draggedClip.IsAudioOverlay && _moveHoverIsV2;
        double pxPerSec = Math.Max(1.0, _movePxPerSec);

        _moveDragSnapshots = null;

        MoveSlotPreviewRect.BeginAnimation(OpacityProperty, null);
        MoveSlotPreviewRect.Visibility = Visibility.Collapsed;
        DropInsertionLine.Visibility = Visibility.Collapsed;

        if (snapshots == null || snapshots.Count == 0)
            return false;

        MoveClipDragSnapshot dragged = snapshots[0];
        bool switchedTrack = !draggedClip.IsAudioOverlay && targetV2 != draggedClip.IsVideoOverlay;
        bool moved = switchedTrack
            ? _project.MoveVideoClipTrack(draggedClip, targetV2, resolvedTime, recordUndo: true)
            : _project.MoveClipToTime(draggedClip, resolvedTime, recordUndo: true);

        if (switchedTrack)
        {
            RenderTimeline();
            return moved;
        }

        double targetLeft = draggedClip.TimelineStart.TotalSeconds * pxPerSec;

        int settleGen = ++_moveSettleGeneration;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(120);

        if (dragged.VideoBlock != null)
        {
            AnimateSettleClip(dragged.VideoBlock, targetLeft, dur, ease, onCompleted: () =>
            {
                if (settleGen == _moveSettleGeneration && _dragMode == TimelineDragMode.None)
                    RenderTimeline();
            });
            AnimateSettleClip(dragged.AudioBlock, targetLeft, dur, ease, onCompleted: null);
        }
        else if (dragged.AudioBlock != null)
        {
            AnimateSettleClip(dragged.AudioBlock, targetLeft, dur, ease, onCompleted: () =>
            {
                if (settleGen == _moveSettleGeneration && _dragMode == TimelineDragMode.None)
                    RenderTimeline();
            });
        }

        return moved;
    }

    private static void ApplyLiftAnimation(Border? block, double liftY)
    {
        if (block == null) return;
        Panel.SetZIndex(block, 100);
        block.RenderTransformOrigin = new Point(0.5, 0.5);

        var scale = new ScaleTransform(1.0, 1.0);
        var translate = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        block.RenderTransform = group;

        block.BorderBrush = BrushFrom("#4EA1FF");
        block.BorderThickness = new Thickness(1.8);
        block.Effect = new DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 3,
            Opacity = 0.65,
            Color = Colors.Black
        };

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(115);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.03, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.03, dur) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, liftY, dur) { EasingFunction = ease });
        block.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.94, dur) { EasingFunction = ease });
    }

    private static void AnimateSettleClip(
        Border? block, double targetLeft, TimeSpan dur, IEasingFunction ease, Action? onCompleted)
    {
        if (block == null)
        {
            onCompleted?.Invoke();
            return;
        }

        double fromLeft = Canvas.GetLeft(block);
        if (double.IsNaN(fromLeft)) fromLeft = targetLeft;
        var leftAnim = new DoubleAnimation(fromLeft, targetLeft, dur) { EasingFunction = ease };
        if (onCompleted != null)
            leftAnim.Completed += (_, _) => onCompleted();
        block.BeginAnimation(Canvas.LeftProperty, leftAnim);
        block.BeginAnimation(OpacityProperty, new DoubleAnimation(block.Opacity, 1.0, dur) { EasingFunction = ease });

        if (block.RenderTransform is TransformGroup group && group.Children.Count >= 2)
        {
            if (group.Children[0] is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 1.0, dur) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 1.0, dur) { EasingFunction = ease });
            }
            if (group.Children[1] is TranslateTransform translate)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(translate.Y, 0, dur) { EasingFunction = ease });
            }
        }
    }

    private static void AnimateElementCanvasLeft(UIElement? el, double targetLeft, double durationMs)
    {
        if (el == null) return;
        double currentLeft = Canvas.GetLeft(el);
        if (double.IsNaN(currentLeft)) currentLeft = targetLeft;
        if (Math.Abs(currentLeft - targetLeft) < 0.5)
        {
            el.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(el, targetLeft);
            return;
        }

        var anim = new DoubleAnimation(currentLeft, targetLeft, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        el.BeginAnimation(Canvas.LeftProperty, anim);
    }

    private void RenderJogTicks(double offsetPx)
    {
        if (JogTicksCanvas == null || JogWheelBorder == null) return;
        JogTicksCanvas.Children.Clear();

        double w = JogWheelBorder.ActualWidth > 0 ? JogWheelBorder.ActualWidth : JogWheelBorder.Width;
        double h = JogWheelBorder.ActualHeight > 0 ? JogWheelBorder.ActualHeight : JogWheelBorder.Height;
        if (w <= 0) w = 168;
        if (h <= 0) h = 20;

        const double spacing = 8.0;
        double phase = ((offsetPx % spacing) + spacing) % spacing;
        var minorTick = BrushFrom("#343947");
        var majorTick = BrushFrom("#4C5468");

        int index = 0;
        for (double x = phase; x < w; x += spacing)
        {
            bool isMajor = (index % 3) == 0;
            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = isMajor ? 2 : 5,
                Y2 = h - (isMajor ? 2 : 5),
                Stroke = isMajor ? majorTick : minorTick,
                StrokeThickness = 1
            };
            JogTicksCanvas.Children.Add(line);
            index++;
        }
    }

    private void RenderProgramCutMarkers()
    {
        if (ProgramClipMarkersCanvas == null) return;
        ProgramClipMarkersCanvas.Children.Clear();

        double w = ProgramClipMarkersCanvas.ActualWidth;
        if (w <= 20) w = ProgramScrubSlider?.ActualWidth ?? 0;
        double totalSec = _project.TotalDuration.TotalSeconds;
        if (w <= 20 || totalSec <= 0 || (_project.Clips.Count == 0 && _project.VideoOverlayClips.Count == 0)) return;

        var markerBrush = BrushFrom("#C8E2FF");
        foreach (MontageClipItem c in _project.Clips.Concat(_project.VideoOverlayClips))
        {
            if (c.TimelineStart > TimeSpan.FromMilliseconds(20))
                AddMarker(c.TimelineStart.TotalSeconds);
            if (c.TimelineEnd < _project.TotalDuration - TimeSpan.FromMilliseconds(20))
                AddMarker(c.TimelineEnd.TotalSeconds);
        }

        void AddMarker(double sec)
        {
            double x = Math.Clamp((sec / totalSec) * w, 2, w - 2);
            var tick = new Rectangle
            {
                Width = 1.5,
                Height = 8,
                Fill = markerBrush,
                Opacity = 0.72
            };
            Canvas.SetLeft(tick, x - 0.75);
            Canvas.SetTop(tick, 1);
            ProgramClipMarkersCanvas.Children.Add(tick);
        }
    }
}
