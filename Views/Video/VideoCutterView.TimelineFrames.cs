using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CortexFX.Core.Video;
using Path = System.IO.Path;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    private const double CanonicalWaveformWidth = 720.0;

    private static readonly SolidColorBrush TileBgBrush = CreateFrozenBrush("#1A2029");
    private static readonly SolidColorBrush TileSeparatorBrush = CreateFrozenBrush("#11151B");

    private sealed class FilmstripTileSlot
    {
        public FilmstripTileSlot(double sampleSeconds, Border border)
        {
            SampleSeconds = sampleSeconds;
            Border = border;
        }

        public double SampleSeconds { get; }
        public Border Border { get; }
        public ImageSource? CurrentFrame { get; set; }
        public Image? ImageControl { get; set; }
    }

    private sealed record RenderedTimelineClip(
        MontageClipItem Clip,
        Border VideoBlock,
        Border AudioBlock,
        ScaleTransform? WaveScale,
        IReadOnlyList<FilmstripTileSlot> TileSlots);

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
    }

    private void CancelAssetExtraction(string filePath)
    {
        if (_assetExtractionJobs.Remove(filePath, out AssetExtractionJob? job))
        {
            try { job.Cts.Cancel(); } catch { }
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
                "-vf", "scale=-2:144:flags=bilinear,format=yuvj420p",
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
        foreach (MontageClipItem clip in _project.Clips)
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
                ImageSource? frame = asset.FindNearestFrame(slot.SampleSeconds, maxDist);
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

            const int bucketCount = 1200;
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
                            RefreshTimelineAudioInPlace(asset.FilePath);
                    }
                });
            }
        }
        catch
        {
            // Non-fatal: waveform strip gracefully stays flat if audio extraction fails.
        }
    }

    // Timeline visual rendering

    private double ComputeTimelineSurfaceWidth()
    {
        bool zoomed = _zoomFactor > 1.01;
        ScrollBarVisibility desiredHScroll = zoomed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (TimelineScrollViewer.HorizontalScrollBarVisibility != desiredHScroll)
            TimelineScrollViewer.HorizontalScrollBarVisibility = desiredHScroll;

        double viewportW = TimelineScrollViewer.ActualWidth;
        if (viewportW <= 0)
            viewportW = TimelineScrollViewer.ViewportWidth;
        if (viewportW <= 0)
            viewportW = 640;

        double totalWidth = Math.Max(100, viewportW * _zoomFactor);
        TimelineSurface.Width = totalWidth;
        TimelineRulerCanvas.Width = totalWidth;
        TimelineClipsCanvas.Width = totalWidth;
        TimelineAudioCanvas.Width = totalWidth;
        TimelineOverlayCanvas.Width = totalWidth;
        return totalWidth;
    }

    private void UpdateTimelineLayoutInPlace()
    {
        double totalWidth = ComputeTimelineSurfaceWidth();

        if (_project.TotalDuration <= TimeSpan.Zero || _project.Clips.Count == 0)
        {
            UpdatePlayheadVisual();
            return;
        }

        if (_renderedTimelineClips.Count != _project.Clips.Count)
        {
            RenderTimeline();
            return;
        }

        double totalSec = _project.TotalDuration.TotalSeconds;
        TimelineRulerCanvas.Children.Clear();
        RenderTimeRuler(totalSec, totalWidth, totalWidth / totalSec);

        foreach (RenderedTimelineClip item in _renderedTimelineClips)
        {
            double left = (item.Clip.TimelineStart.TotalSeconds / totalSec) * totalWidth;
            double width = Math.Max(4, (item.Clip.Duration.TotalSeconds / totalSec) * totalWidth);
            Canvas.SetLeft(item.VideoBlock, left);
            item.VideoBlock.Width = width;
            Canvas.SetLeft(item.AudioBlock, left);
            item.AudioBlock.Width = width;
            if (item.WaveScale != null)
                item.WaveScale.ScaleX = width / CanonicalWaveformWidth;
        }

        UpdatePlayheadVisual();
        ScheduleTimelineSettleRefresh();
    }

    private void ScheduleTimelineSettleRefresh()
    {
        if (_timelineSettleTimer == null)
        {
            _timelineSettleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(85)
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
        _timelineSettleTimer?.Stop();
        double totalWidth = ComputeTimelineSurfaceWidth();

        TimelineRulerCanvas.Children.Clear();
        TimelineClipsCanvas.Children.Clear();
        TimelineAudioCanvas.Children.Clear();
        _renderedTimelineClips.Clear();

        TimeSpan totalDur = _project.TotalDuration;
        if (totalDur <= TimeSpan.Zero || _project.Clips.Count == 0)
        {
            UpdatePlayheadVisual();
            return;
        }

        double totalSec = totalDur.TotalSeconds;
        double pxPerSec = totalWidth / totalSec;

        RenderTimeRuler(totalSec, totalWidth, pxPerSec);

        bool multiSource = _project.Clips
            .Select(c => c.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;

        for (int i = 0; i < _project.Clips.Count; i++)
        {
            MontageClipItem clip = _project.Clips[i];
            double left = (clip.TimelineStart.TotalSeconds / totalSec) * totalWidth;
            double width = Math.Max(4, (clip.Duration.TotalSeconds / totalSec) * totalWidth);

            var (videoBlock, tileSlots) = BuildPremiereVideoBlock(clip, width, multiSource);
            Canvas.SetLeft(videoBlock, left);
            Canvas.SetTop(videoBlock, 3);
            TimelineClipsCanvas.Children.Add(videoBlock);

            (Border audioBlock, ScaleTransform? waveScale) = BuildPremiereAudioBlock(clip, width, multiSource);
            Canvas.SetLeft(audioBlock, left);
            Canvas.SetTop(audioBlock, 3);
            TimelineAudioCanvas.Children.Add(audioBlock);

            _renderedTimelineClips.Add(new RenderedTimelineClip(clip, videoBlock, audioBlock, waveScale, tileSlots));
        }

        UpdatePlayheadVisual();
    }

    private void RefreshTimelineAudioInPlace(string sourcePath)
    {
        TimeSpan totalDur = _project.TotalDuration;
        if (totalDur <= TimeSpan.Zero || _project.Clips.Count == 0 ||
            _renderedTimelineClips.Count != _project.Clips.Count)
        {
            RenderTimeline();
            return;
        }

        double totalWidth = ComputeTimelineSurfaceWidth();
        double totalSec = totalDur.TotalSeconds;
        bool multiSource = _project.Clips
            .Select(c => c.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;

        for (int i = 0; i < _renderedTimelineClips.Count; i++)
        {
            RenderedTimelineClip item = _renderedTimelineClips[i];
            if (!string.Equals(item.Clip.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                continue;

            double left = (item.Clip.TimelineStart.TotalSeconds / totalSec) * totalWidth;
            double width = Math.Max(4, (item.Clip.Duration.TotalSeconds / totalSec) * totalWidth);
            (Border newAudioBlock, ScaleTransform? newWaveScale) = BuildPremiereAudioBlock(item.Clip, width, multiSource);
            Canvas.SetLeft(newAudioBlock, left);
            Canvas.SetTop(newAudioBlock, 3);

            int childIndex = TimelineAudioCanvas.Children.IndexOf(item.AudioBlock);
            if (childIndex >= 0)
            {
                TimelineAudioCanvas.Children.RemoveAt(childIndex);
                TimelineAudioCanvas.Children.Insert(childIndex, newAudioBlock);
            }
            else
            {
                TimelineAudioCanvas.Children.Add(newAudioBlock);
            }

            _renderedTimelineClips[i] = item with { AudioBlock = newAudioBlock, WaveScale = newWaveScale };
        }
    }

    private void RenderTimeRuler(double totalSeconds, double totalWidth, double pxPerSec)
    {
        double[] candidateSteps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double majorStep = candidateSteps[^1];
        foreach (double step in candidateSteps)
        {
            if (step * pxPerSec >= 68)
            {
                majorStep = step;
                break;
            }
        }

        double minorStep = majorStep / 5.0;
        var tickBrush = BrushFrom("#383838");
        var majorBrush = BrushFrom("#646464");
        var textBrush = BrushFrom("#9A9A9A");

        for (double t = 0; t <= totalSeconds + 0.0001; t += minorStep)
        {
            double x = (t / totalSeconds) * totalWidth;
            if (x > totalWidth - 1) break;

            bool isMajor = Math.Abs((t / majorStep) - Math.Round(t / majorStep)) < 0.01;
            var tick = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = isMajor ? 14 : 18,
                Y2 = 22,
                Stroke = isMajor ? majorBrush : tickBrush,
                StrokeThickness = 1
            };
            TimelineRulerCanvas.Children.Add(tick);

            if (isMajor && x < totalWidth - 36)
            {
                var label = new TextBlock
                {
                    Text = MontageProject.FormatShort(TimeSpan.FromSeconds(t)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 9,
                    Foreground = textBrush
                };
                Canvas.SetLeft(label, x + 5);
                Canvas.SetTop(label, 2);
                TimelineRulerCanvas.Children.Add(label);
            }
        }
    }

    private (Border Block, IReadOnlyList<FilmstripTileSlot> TileSlots) BuildPremiereVideoBlock(
        MontageClipItem clip, double clipWidth, bool multiSource)
    {
        const double blockHeight = 98;
        const double headerHeight = 14;
        const double filmHeight = blockHeight - headerHeight;

        // Classic Adobe Premiere Pro Video Track colors (steel blue header + crisp border on selection).
        var borderBrush = clip.IsSelected ? BrushFrom("#E6E6E6") : BrushFrom("#1F2C3D");
        var headerBrush = clip.IsSelected ? BrushFrom("#5B88BD") : BrushFrom("#466994");

        var outer = new Border
        {
            Width = clipWidth,
            Height = blockHeight,
            CornerRadius = new CornerRadius(2),
            Background = BrushFrom("#1F2630"),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(clip.IsSelected ? 1.5 : 1),
            ClipToBounds = true,
            Tag = clip
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(filmHeight) });

        // Premiere Video Clip Header Bar
        var header = new Border
        {
            Background = headerBrush,
            Padding = new Thickness(6, 0, 6, 0),
            IsHitTestVisible = false
        };
        if (clipWidth >= 34)
        {
            string label = multiSource
                ? $"{clip.SourceFileName} · {clip.DisplayDuration}"
                : $"#{clip.OrderIndex} {clip.SourceFileName} · {clip.DisplayDuration}";
            header.Child = new TextBlock
            {
                Text = label,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Filmstrip Row
        _project.Assets.TryGetValue(clip.SourcePath, out SourceMediaAsset? asset);
        double aspect = clip.SourceWidth > 0 && clip.SourceHeight > 0
            ? Math.Clamp(clip.SourceWidth / (double)clip.SourceHeight, 0.52, 1.85)
            : 16.0 / 9.0;
        double idealTileW = Math.Clamp(filmHeight * aspect * 0.78, 56, 120);
        int tileCount = Math.Clamp((int)Math.Ceiling(clipWidth / idealTileW), 1, 36);

        var stripGrid = new Grid { ClipToBounds = true, IsHitTestVisible = false };
        for (int t = 0; t < tileCount; t++)
            stripGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        double clipStartSec = clip.Start.TotalSeconds;
        double clipDurSec = Math.Max(0.05, clip.Duration.TotalSeconds);
        bool extracting = IsFramesExtracting(clip.SourcePath);
        double maxDist = extracting ? GetActiveSnapDistance(clip.SourcePath) : double.MaxValue;
        var tileSlots = new List<FilmstripTileSlot>(tileCount);

        for (int t = 0; t < tileCount; t++)
        {
            double sampleSec = clipStartSec + clipDurSec * ((t + 0.5) / tileCount);
            ImageSource? frame = asset?.FindNearestFrame(sampleSec, maxDist);
            if (frame == null && !extracting)
                frame = clip.Thumbnail;

            var tileBorder = new Border
            {
                Background = TileBgBrush,
                BorderBrush = TileSeparatorBrush,
                BorderThickness = new Thickness(0, 0, t < tileCount - 1 ? 1 : 0, 0),
                ClipToBounds = true
            };
            var slot = new FilmstripTileSlot(sampleSec, tileBorder);
            if (frame != null)
            {
                var img = CreateTileImage(frame);
                tileBorder.Child = img;
                slot.ImageControl = img;
                slot.CurrentFrame = frame;
            }
            tileSlots.Add(slot);
            Grid.SetColumn(tileBorder, t);
            stripGrid.Children.Add(tileBorder);
        }
        Grid.SetRow(stripGrid, 1);
        root.Children.Add(stripGrid);

        outer.Child = root;
        return (outer, tileSlots);
    }

    private (Border Block, ScaleTransform? WaveScale) BuildPremiereAudioBlock(MontageClipItem clip, double clipWidth, bool multiSource)
    {
        const double blockHeight = 58;
        const double headerHeight = 12;
        const double waveHeight = blockHeight - headerHeight;

        bool trackMuted = TrackMuteButton?.IsChecked == true;
        var borderBrush = clip.IsSelected ? BrushFrom("#E6E6E6") : BrushFrom("#1B363F");
        var headerBrush = clip.IsSelected ? BrushFrom("#458C9E") : BrushFrom("#326C7B");

        var outer = new Border
        {
            Width = clipWidth,
            Height = blockHeight,
            CornerRadius = new CornerRadius(2),
            Background = BrushFrom("#1B2D35"),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(clip.IsSelected ? 1.5 : 1),
            Opacity = trackMuted ? 0.45 : 1.0,
            ClipToBounds = true,
            Tag = clip
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(waveHeight) });

        var header = new Border
        {
            Background = headerBrush,
            Padding = new Thickness(6, 0, 6, 0),
            IsHitTestVisible = false
        };
        if (clipWidth >= 42)
        {
            header.Child = new TextBlock
            {
                Text = multiSource ? clip.SourceFileName : $"{clip.SourceFileName} [A1]",
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFrom("#EBF7FA"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var waveContainer = new Border
        {
            Background = BrushFrom("#182931"),
            IsHitTestVisible = false,
            ClipToBounds = true
        };

        ScaleTransform? waveScale = null;
        _project.Assets.TryGetValue(clip.SourcePath, out SourceMediaAsset? asset);
        if (clip.HasAudio && asset?.AudioPeaks is { Length: > 0 } peaks)
        {
            waveScale = new ScaleTransform(clipWidth / CanonicalWaveformWidth, 1.0);
            var wavePath = new System.Windows.Shapes.Path
            {
                Fill = BrushFrom("#7CE2F7"),
                Data = BuildClipWaveformGeometry(peaks, clip, CanonicalWaveformWidth, waveHeight - 2),
                Width = CanonicalWaveformWidth,
                Height = waveHeight - 2,
                RenderTransform = waveScale
            };
            // Place inside Canvas so WPF never applies pre-transform LayoutClip when clipWidth < CanonicalWaveformWidth.
            var waveHost = new Canvas { Height = waveHeight, ClipToBounds = false };
            Canvas.SetLeft(wavePath, 0);
            Canvas.SetTop(wavePath, 2);
            waveHost.Children.Add(wavePath);
            waveContainer.Child = waveHost;
        }
        else if (!clip.HasAudio && clipWidth > 44)
        {
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

        outer.Child = root;
        return (outer, waveScale);
    }

    private static Geometry BuildClipWaveformGeometry(
        float[] peaks, MontageClipItem clip, double width, double height)
    {
        var geom = new StreamGeometry();
        double srcSec = Math.Max(0.1, clip.SourceDuration.TotalSeconds);
        double startRatio = Math.Clamp(clip.Start.TotalSeconds / srcSec, 0, 1);
        double endRatio = Math.Clamp(clip.End.TotalSeconds / srcSec, startRatio, 1);

        int barCount = Math.Clamp((int)(width / 2.2), 16, 340);
        double barStep = width / barCount;
        double barW = Math.Max(1.1, barStep * 0.72);

        using (StreamGeometryContext ctx = geom.Open())
        {
            for (int b = 0; b < barCount; b++)
            {
                double r = startRatio + (endRatio - startRatio) * ((b + 0.5) / barCount);
                int idx = Math.Clamp((int)(r * (peaks.Length - 1)), 0, peaks.Length - 1);
                double amp = peaks[idx];
                double barH = Math.Max(2.0, amp * height);
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
}
