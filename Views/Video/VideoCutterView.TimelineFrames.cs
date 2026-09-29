using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    private const int TimelineFrameCount = 12;
    private CancellationTokenSource? _timelineFramesCts;

    private void CancelTimelineFrames()
    {
        CancellationTokenSource? current = _timelineFramesCts;
        _timelineFramesCts = null;
        current?.Cancel();
        TimelineFrames.Children.Clear();
        TimelineFramesHint.Text = "Preparing video frames...";
        TimelineFramesHint.Visibility = Visibility.Visible;
    }

    private void StartTimelineFrames(string path)
    {
        CancelTimelineFrames();
        if (!IsVisible)
            return;
        if (_duration <= TimeSpan.Zero || !File.Exists(_ffmpegPath))
        {
            TimelineFramesHint.Text = "Video frames unavailable";
            return;
        }

        TimelineFrames.Columns = TimelineFrameCount;
        var slots = new Image[TimelineFrameCount];
        for (int index = 0; index < slots.Length; index++)
        {
            slots[index] = new Image
            {
                Stretch = Stretch.UniformToFill,
                ClipToBounds = true,
                SnapsToDevicePixels = true
            };
            TimelineFrames.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(18, 34, 44)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(9, 18, 26)),
                BorderThickness = new Thickness(0, 0, 1, 0),
                ClipToBounds = true,
                Child = slots[index]
            });
        }

        var cts = new CancellationTokenSource();
        _timelineFramesCts = cts;
        _ = PopulateTimelineFramesAsync(path, _duration, slots, cts);
    }

    private async Task PopulateTimelineFramesAsync(
        string path, TimeSpan duration, Image[] slots, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        int successfulFrames = 0;
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, slots.Length),
                new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 3 },
                async (index, frameToken) =>
                {
                    double position = Math.Min(
                        Math.Max(0, duration.TotalSeconds - 0.05),
                        duration.TotalSeconds * (index + 0.5) / slots.Length);
                    byte[]? jpeg;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(frameToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(8));
                        jpeg = await CaptureTimelineFrameAsync(path, position, timeout.Token);
                    }
                    catch (Exception) when (!frameToken.IsCancellationRequested)
                    {
                        return;
                    }

                    if (jpeg is not { Length: > 0 })
                        return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (frameToken.IsCancellationRequested ||
                            !ReferenceEquals(_timelineFramesCts, cts) ||
                            !string.Equals(_inputFilePath, path, StringComparison.OrdinalIgnoreCase))
                            return;

                        using var stream = new MemoryStream(jpeg);
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        slots[index].Source = bitmap;
                        successfulFrames++;
                        TimelineFramesHint.Visibility = Visibility.Collapsed;
                    });
                });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A failed thumbnail must not prevent cutting or export.
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_timelineFramesCts, cts))
                        return;

                    _timelineFramesCts = null;
                    if (successfulFrames == 0)
                        TimelineFramesHint.Text = "Video frames unavailable";
                });
            }
            cts.Dispose();
        }
    }

    private async Task<byte[]?> CaptureTimelineFrameAsync(
        string path, double positionSeconds, CancellationToken token)
    {
        var start = new ProcessStartInfo
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
            "-ss", positionSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", path, "-frames:v", "1",
            "-vf", "scale=160:90:force_original_aspect_ratio=increase,crop=160:90",
            "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1"
        })
            start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            return null;

        using var cancellation = token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        });
        using var output = new MemoryStream();
        Task<string> errors = process.StandardError.ReadToEndAsync(token);
        await process.StandardOutput.BaseStream.CopyToAsync(output, token);
        await process.WaitForExitAsync(token);
        _ = await errors;
        return process.ExitCode == 0 && output.Length > 0 ? output.ToArray() : null;
    }
}
