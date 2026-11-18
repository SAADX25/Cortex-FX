using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CortexFX.Core.Video;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
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
        bool needAudio = asset.HasAudio && asset.AudioPeaks == null;

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
        Task audioTask = needAudio ? ExtractAudioPeaksAsync(asset, token) : Task.CompletedTask;
        Task framesTask = needFrames ? ExtractProgressiveFramesAsync(asset, wave1Positions, wave2Positions, job, token) : Task.CompletedTask;

        try { await Task.WhenAll(framesTask, audioTask); }
        catch { }
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
        if (EditorPanel.Visibility != Visibility.Visible || !_project.AllVideoClips.Any())
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
        catch { }
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

            List<byte[]> jpegs = SplitMjpegFrames(ms.ToArray());
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
        catch { }

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
        foreach (MontageClipItem clip in _project.AllVideoClips)
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
        catch { }
    }
}
