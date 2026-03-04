using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace CortexFX.Views;

/// <summary>
/// Data model for a single thumbnail frame in the timeline strip.
/// </summary>
public class ThumbnailItem
{
    public ImageSource? Image { get; set; }
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// Self-contained Watermark Remover editor view.
/// Call <see cref="Initialize"/> once after construction to supply shared paths,
/// then subscribe to <see cref="CloseRequested"/> to be notified when the user
/// wishes to return to the dashboard.
/// </summary>
public partial class WatermarkRemoverView : UserControl
{
    // ------------------------------------------------------------------
    // Public surface
    // ------------------------------------------------------------------

    /// <summary>Raised when the user clicks Cancel or closes the project,
    /// and navigation back to the dashboard should occur.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Supplies the paths that are resolved by MainWindow at startup.
    /// Must be called before the view is made visible for the first time.
    /// </summary>
    public void Initialize(string ffmpegPath, string thumbnailsDirectory)
    {
        _ffmpegPath = ffmpegPath;
        _thumbnailsDirectory = thumbnailsDirectory;
    }

    /// <summary>
    /// Called by MainWindow when the user presses Delete while this view is visible.
    /// Removes the currently selected region, if any.
    /// </summary>
    public void DeleteSelectedRegion()
    {
        if (_selectedRegion == null) return;
        _selectedRegions.Remove(_selectedRegion);
        _selectedRegion = null;
        _selectedRectangleVisual = null;
        _regionColorIndex = _selectedRegions.Count % _regionStrokeBrushes.Length;
        ApplySelectionBrushes();
        UpdateTimelineVisuals();
    }

    // ------------------------------------------------------------------
    // Private paths (supplied via Initialize)
    // ------------------------------------------------------------------

    private string _ffmpegPath = string.Empty;
    private string _thumbnailsDirectory = string.Empty;

    // ------------------------------------------------------------------
    // Thumbnail / timeline state
    // ------------------------------------------------------------------

    private readonly ObservableCollection<ThumbnailItem> _thumbnails = new();
    private CancellationTokenSource? _thumbnailCts;
    private FileSystemWatcher? _thumbnailWatcher;
    private Process? _thumbnailProcess;
    private readonly HashSet<string> _thumbnailPaths = new(StringComparer.OrdinalIgnoreCase);

    private double _videoFps = 30;
    private bool _isScrubbing;
    private bool _wasPlayingBeforeScrub;
    private double _lastScrubRatio = -1;
    private DateTime _lastScrubUpdate = DateTime.MinValue;

    // ------------------------------------------------------------------
    // Watermark editor state
    // ------------------------------------------------------------------

    private string? currentInputPath;
    private double _naturalVideoWidth;
    private double _naturalVideoHeight;
    private readonly ObservableCollection<RegionModel> _selectedRegions = new();

    private readonly Brush[] _regionStrokeBrushes =
    {
        new SolidColorBrush(Color.FromRgb(0, 229, 255)),
        new SolidColorBrush(Color.FromRgb(255, 45, 85)),
        new SolidColorBrush(Color.FromRgb(255, 214, 79)),
        new SolidColorBrush(Color.FromRgb(76, 175, 80))
    };

    private readonly Brush[] _regionFillBrushes =
    {
        new SolidColorBrush(Color.FromArgb(60, 0, 229, 255)),
        new SolidColorBrush(Color.FromArgb(60, 255, 45, 85)),
        new SolidColorBrush(Color.FromArgb(60, 255, 214, 79)),
        new SolidColorBrush(Color.FromArgb(60, 76, 175, 80))
    };

    private int _regionColorIndex;
    private bool _isSelectionMode;
    private RegionModel? _selectedRegion;
    private Rectangle? _selectedRectangleVisual;

    private double _lastSelectionVideoX;
    private double _lastSelectionVideoY;
    private double _lastSelectionVideoWidth;
    private double _lastSelectionVideoHeight;

    // Temporal In/Out points for region time-bounding
    private TimeSpan _currentInPoint = TimeSpan.Zero;
    private TimeSpan _currentOutPoint = TimeSpan.MaxValue;

    // Drawing state
    private bool _isDrawingMode;
    private bool _isDrawing;
    private Point _startPoint;

    // ------------------------------------------------------------------
    // Constructor & initialisation
    // ------------------------------------------------------------------

    public WatermarkRemoverView()
    {
        InitializeComponent();
        Loaded += WatermarkRemoverView_Loaded;
    }

    private void WatermarkRemoverView_Loaded(object sender, RoutedEventArgs e)
    {
        RegionsOverlay.ItemsSource = _selectedRegions;
        ApplySelectionBrushes();
        ThumbnailsTimeline.ItemsSource = _thumbnails;
        CompositionTarget.Rendering += TimelineRendering;
    }

    // ------------------------------------------------------------------
    // Navigation helper
    // ------------------------------------------------------------------

    private void FireCloseRequested()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------
    // Thumbnail generation & timeline
    // ------------------------------------------------------------------

    private void ResetThumbnails()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;

        if (_thumbnailProcess != null)
        {
            if (!_thumbnailProcess.HasExited)
                _thumbnailProcess.Kill();
            _thumbnailProcess.Dispose();
            _thumbnailProcess = null;
        }

        if (_thumbnailWatcher != null)
        {
            _thumbnailWatcher.EnableRaisingEvents = false;
            _thumbnailWatcher.Dispose();
            _thumbnailWatcher = null;
        }

        _thumbnailPaths.Clear();

        Dispatcher.Invoke(() =>
        {
            ThumbnailsTimeline.ItemsSource = null;
            _thumbnails.Clear();
        });

        // Force GC to release file handles held by frozen BitmapImage objects
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Now delete the temp files — handles should be free
        try
        {
            Directory.CreateDirectory(_thumbnailsDirectory);
            foreach (var file in Directory.EnumerateFiles(_thumbnailsDirectory, "*.jpg"))
            {
                try { File.Delete(file); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Thumbnail cleanup skip: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Thumbnail folder cleanup: {ex.Message}");
        }

        Dispatcher.Invoke(() =>
        {
            ThumbnailsTimeline.ItemsSource = _thumbnails;
        });
    }

    private void StartThumbnailWatcher()
    {
        Directory.CreateDirectory(_thumbnailsDirectory);
        _thumbnailWatcher = new FileSystemWatcher(_thumbnailsDirectory, "*.jpg")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
        };

        _thumbnailWatcher.Created += async (s, e) =>
        {
            await LoadThumbnailAsync(e.FullPath);
        };

        _thumbnailWatcher.EnableRaisingEvents = true;
    }

    private async Task LoadThumbnailAsync(string filePath)
    {
        if (_thumbnailPaths.Contains(filePath)) return;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();

                Dispatcher.Invoke(() =>
                {
                    if (_thumbnailPaths.Count >= 15) return;
                    if (_thumbnailPaths.Add(filePath))
                        _thumbnails.Add(new ThumbnailItem { Image = bitmap, Path = filePath });
                });
                break;
            }
            catch
            {
                await Task.Delay(120);
            }
        }
    }

    private async Task StartThumbnailGenerationAsync(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return;
        if (!File.Exists(_ffmpegPath)) return;

        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();

        StartThumbnailWatcher();

        for (int i = 0; i < 10; i++)
        {
            if (WatermarkVideoPlayer.NaturalDuration.HasValue &&
                WatermarkVideoPlayer.NaturalDuration.Value.TotalSeconds > 0)
                break;
            await Task.Delay(100);
        }

        double durationSeconds = WatermarkVideoPlayer.NaturalDuration.HasValue
            ? WatermarkVideoPlayer.NaturalDuration.Value.TotalSeconds : 0;
        if (durationSeconds <= 0) return;

        double totalFrames = Math.Max(1, durationSeconds * Math.Max(1, _videoFps));
        int interval = Math.Max(1, (int)Math.Round(totalFrames / 15.0));

        string outputPattern = System.IO.Path.Combine(_thumbnailsDirectory, "thumb%03d.jpg");
        string filter = $"select='not(mod(n\\,{interval}))',scale=160:-1";
        string arguments = $"-i \"{inputPath}\" -vf \"{filter}\" -vsync vfr -q:v 2 \"{outputPattern}\"";

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        _thumbnailProcess = new Process { StartInfo = psi };
        _thumbnailProcess.Start();
        _thumbnailProcess.BeginOutputReadLine();
        _thumbnailProcess.BeginErrorReadLine();
        await _thumbnailProcess.WaitForExitAsync();
        _thumbnailProcess.Dispose();
        _thumbnailProcess = null;
    }

    private void ThumbnailsTimeline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue || _thumbnails.Count == 0) return;
        if (ThumbnailsTimeline.SelectedIndex < 0) return;

        double ratio = ThumbnailsTimeline.SelectedIndex / (double)_thumbnails.Count;
        SeekToRatio(ratio);
    }

    private void TimelineRendering(object? sender, EventArgs e)
    {
        if (_isScrubbing) return;
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue) return;
        var duration = WatermarkVideoPlayer.NaturalDuration.Value;
        if (duration.TotalMilliseconds <= 0) return;

        double ratio = WatermarkVideoPlayer.Position.TotalMilliseconds / duration.TotalMilliseconds;
        UpdatePlayheadPosition(ratio);
    }

    private async void TimelineOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue) return;

        // Remember playback state, then pause for smooth scrubbing
        _wasPlayingBeforeScrub = WatermarkVideoPlayer.IsPlaying;
        if (_wasPlayingBeforeScrub)
            await WatermarkVideoPlayer.Pause();

        _isScrubbing = true;
        TimelineOverlay.CaptureMouse();
        UpdateScrubFromPoint(e.GetPosition(TimelineOverlay), true);
    }

    private void TimelineOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isScrubbing) return;
        UpdateScrubFromPoint(e.GetPosition(TimelineOverlay), false);
    }

    private async void TimelineOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isScrubbing) return;
        UpdateScrubFromPoint(e.GetPosition(TimelineOverlay), true);
        _isScrubbing = false;
        TimelineOverlay.ReleaseMouseCapture();

        // Resume playback only if it was playing before the drag
        if (_wasPlayingBeforeScrub)
        {
            await WatermarkVideoPlayer.Play();
            UpdatePlayPauseButton();
        }
    }

    private void UpdateScrubFromPoint(Point point, bool force)
    {
        double ratio = GetTimelineRatio(point.X);
        double delta = Math.Abs(ratio - _lastScrubRatio);
        double elapsed = (DateTime.Now - _lastScrubUpdate).TotalMilliseconds;
        // Throttle: skip if change is tiny AND less than 60ms since last seek
        if (!force && delta < 0.008 && elapsed < 60) return;

        _lastScrubRatio = ratio;
        _lastScrubUpdate = DateTime.Now;
        SeekToRatio(ratio);
    }

    private double GetTimelineRatio(double x)
    {
        double width = TimelineOverlay.ActualWidth;
        if (width <= 0) return 0;
        double ratio = x / width;
        return Math.Max(0, Math.Min(1, ratio));
    }

    private async void SeekToRatio(double ratio)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue) return;
        var duration = WatermarkVideoPlayer.NaturalDuration.Value;
        if (duration.TotalMilliseconds <= 0) return;
        ratio = Math.Max(0, Math.Min(1, ratio));

        var targetTime = TimeSpan.FromMilliseconds(ratio * duration.TotalMilliseconds);
        await WatermarkVideoPlayer.Seek(targetTime);
        UpdatePlayheadPosition(ratio);
    }

    private void UpdatePlayheadPosition(double ratio)
    {
        if (TimelineOverlay == null || PlayheadLine == null) return;
        double width = TimelineOverlay.ActualWidth;
        if (width <= 0) return;
        Canvas.SetLeft(PlayheadLine, ratio * width);
    }

    /// <summary>Draws semi-transparent colored bars on the timeline for each region's time span.</summary>
    private void UpdateTimelineVisuals()
    {
        if (TimelineRegionCanvas == null) return;
        TimelineRegionCanvas.Children.Clear();

        if (_selectedRegions.Count == 0) return;
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue) return;

        double totalSeconds = WatermarkVideoPlayer.NaturalDuration.Value.TotalSeconds;
        if (totalSeconds <= 0) return;

        double timelineWidth = TimelineBorder.ActualWidth;
        if (timelineWidth <= 0) return;

        double pixelsPerSecond = timelineWidth / totalSeconds;

        foreach (var region in _selectedRegions)
        {
            double startSec = region.StartTime.TotalSeconds;
            double endSec = region.EndTime == TimeSpan.MaxValue
                ? totalSeconds
                : Math.Min(region.EndTime.TotalSeconds, totalSeconds);
            if (endSec <= startSec) continue;

            double xPos = startSec * pixelsPerSecond;
            double barWidth = (endSec - startSec) * pixelsPerSecond;

            var barBrush = region.Stroke.Clone();
            barBrush.Opacity = 0.4;
            barBrush.Freeze();

            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = barWidth,
                Height = TimelineRegionCanvas.ActualHeight > 0 ? TimelineRegionCanvas.ActualHeight : 60,
                Fill = barBrush,
                RadiusX = 3,
                RadiusY = 3,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(bar, xPos);
            Canvas.SetTop(bar, 0);
            TimelineRegionCanvas.Children.Add(bar);
        }
    }

    private void TimelineBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTimelineVisuals();
    }

    // ------------------------------------------------------------------
    // Resource cleanup (internal, no navigation side-effect)
    // ------------------------------------------------------------------

    /// <summary>
    /// Releases all media resources and resets internal editor state.
    /// Does NOT fire <see cref="CloseRequested"/>; navigation is the caller's responsibility.
    /// </summary>
    private async Task CleanupResourcesAsync()
    {
        try
        {
            if (WatermarkVideoPlayer.IsPlaying)
                await WatermarkVideoPlayer.Pause();
            if (WatermarkVideoPlayer.IsPlaying || WatermarkVideoPlayer.IsPaused)
                await WatermarkVideoPlayer.Stop();
            await WatermarkVideoPlayer.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CleanupResourcesAsync player: {ex.Message}");
        }

        WatermarkVideoPlayer.Visibility = Visibility.Visible;
        WatermarkVideoPlayer.IsHitTestVisible = true;

        _selectedRegions.Clear();
        _selectedRegion = null;
        _selectedRectangleVisual = null;
        _regionColorIndex = 0;
        ApplySelectionBrushes();
        ClearActiveSelection();

        RegionsOverlay?.UpdateLayout();

        if (DrawingCanvas != null)
        {
            var toRemove = DrawingCanvas.Children.Cast<UIElement>()
                .Where(c => c != SelectionRect).ToList();
            foreach (var child in toRemove)
                DrawingCanvas.Children.Remove(child);
        }

        _currentInPoint = TimeSpan.Zero;
        _currentOutPoint = TimeSpan.MaxValue;
        if (WmInPointLabel != null) WmInPointLabel.Text = "IN  00:00:00.0";
        if (WmOutPointLabel != null) WmOutPointLabel.Text = "OUT  END";

        SnapshotPreview.Visibility = Visibility.Collapsed;
        SnapshotPreview.Source = null;
        _isSelectionMode = false;
        DisableDrawingMode();

        currentInputPath = null;
        _naturalVideoWidth = 0;
        _naturalVideoHeight = 0;

        ResetThumbnails();
    }

    // ------------------------------------------------------------------
    // Play / Pause toggle
    // ------------------------------------------------------------------

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue) return;

        if (WatermarkVideoPlayer.IsPlaying)
            await WatermarkVideoPlayer.Pause();
        else
            await WatermarkVideoPlayer.Play();

        UpdatePlayPauseButton();
    }

    private void UpdatePlayPauseButton()
    {
        if (btnPlayPause == null) return;
        btnPlayPause.Content = WatermarkVideoPlayer.IsPlaying ? "⏸ Pause" : "▶ Play";
    }

    // ------------------------------------------------------------------
    // Video player actions
    // ------------------------------------------------------------------

    private async void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        // Release any currently loaded file first to prevent file-lock on the dialog
        await CleanupResourcesAsync();

        var dialog = new OpenFileDialog
        {
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov",
            Title = "Select Video"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                currentInputPath = dialog.FileName;
                ResetThumbnails();
                await WatermarkVideoPlayer.Open(new Uri(currentInputPath));
                await WatermarkVideoPlayer.Play();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load video: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                await CleanupResourcesAsync();
                FireCloseRequested();
            }
        }
        else
        {
            // User cancelled the file picker — go back to dashboard
            FireCloseRequested();
        }
    }

    private void MediaElement_MediaOpened(object sender, Unosquare.FFME.Common.MediaOpenedEventArgs e)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;

        // Mute audio — we only need video frames for watermark editing
        WatermarkVideoPlayer.Volume = 0;

        if (WatermarkVideoPlayer.NaturalVideoWidth > 0 && WatermarkVideoPlayer.NaturalVideoHeight > 0)
        {
            _naturalVideoWidth = WatermarkVideoPlayer.NaturalVideoWidth;
            _naturalVideoHeight = WatermarkVideoPlayer.NaturalVideoHeight;
        }

        var videoStream = e.Info?.Streams?.Values
            .FirstOrDefault(s => s.CodecType == FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_VIDEO);
        _videoFps = (videoStream != null && videoStream.FPS > 0) ? videoStream.FPS : 30;

        _selectedRegions.Clear();
        _regionColorIndex = 0;
        ApplySelectionBrushes();
        ClearActiveSelection();
        SnapshotPreview.Visibility = Visibility.Collapsed;
        SnapshotPreview.Source = null;
        WatermarkVideoPlayer.Visibility = Visibility.Visible;
        WatermarkVideoPlayer.IsHitTestVisible = true;
        _isSelectionMode = false;
        DisableDrawingMode();
        UpdatePlayPauseButton();

        if (!string.IsNullOrWhiteSpace(currentInputPath))
            _ = StartThumbnailGenerationAsync(currentInputPath);
    }

    private void MediaElement_MediaFailed(object sender, Unosquare.FFME.Common.MediaFailedEventArgs e)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        MessageBox.Show($"Failed to load video: {e.ErrorException?.Message}", "Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ------------------------------------------------------------------
    // Frame stepping
    // ------------------------------------------------------------------

    private async void StepForward_Click(object sender, RoutedEventArgs e)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue) return;
        if (!WatermarkVideoPlayer.IsPaused) await WatermarkVideoPlayer.Pause();
        await WatermarkVideoPlayer.StepForward();
    }

    private async void StepBackward_Click(object sender, RoutedEventArgs e)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue) return;
        if (!WatermarkVideoPlayer.IsPaused) await WatermarkVideoPlayer.Pause();
        await WatermarkVideoPlayer.StepBackward();
    }

    // ------------------------------------------------------------------
    // Drawing / selection mode
    // ------------------------------------------------------------------

    private void EnableDrawingMode()
    {
        _isDrawingMode = true;
        DrawingCanvas.IsHitTestVisible = true;
        DrawingCanvas.Background = Brushes.Transparent;
        DrawingCanvas.Cursor = Cursors.Cross;
        Panel.SetZIndex(DrawingCanvas, 1000);
        btnSelectRegion.Content = "✂ Cancel";
    }

    private void DisableDrawingMode()
    {
        _isDrawingMode = false;
        DrawingCanvas.IsHitTestVisible = false;
        DrawingCanvas.Background = Brushes.Transparent;
        DrawingCanvas.Cursor = Cursors.Arrow;
        Panel.SetZIndex(DrawingCanvas, 998);
        btnSelectRegion.Content = "✂ Select";
    }

    private void ApplySelectionBrushes()
    {
        if (SelectionRect == null) return;
        int index = _regionColorIndex % _regionStrokeBrushes.Length;
        SelectionRect.Stroke = _regionStrokeBrushes[index];
        SelectionRect.Fill = _regionFillBrushes[index];
    }

    private void ClearActiveSelection()
    {
        if (SelectionRect == null) return;
        SelectionRect.Visibility = Visibility.Collapsed;
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
    }

    private async void ExitSelectionMode(bool resumePlayback)
    {
        SnapshotPreview.Visibility = Visibility.Collapsed;
        SnapshotPreview.Source = null;
        WatermarkVideoPlayer.Visibility = Visibility.Visible;
        WatermarkVideoPlayer.IsHitTestVisible = true;
        _isSelectionMode = false;
        DisableDrawingMode();
        if (resumePlayback && WatermarkVideoPlayer.NaturalDuration.HasValue)
            await WatermarkVideoPlayer.Play();
    }

    // ------------------------------------------------------------------
    // Coordinate mapping
    // ------------------------------------------------------------------

    private bool TryGetSelectionInVideoPixels(
        out double videoX, out double videoY, out double videoWidth, out double videoHeight)
    {
        videoX = videoY = videoWidth = videoHeight = 0;
        if (_naturalVideoWidth <= 0 || _naturalVideoHeight <= 0) return false;

        double cW = DrawingCanvas.ActualWidth;
        double cH = DrawingCanvas.ActualHeight;
        if (cW <= 0 || cH <= 0) return false;

        double vAspect = _naturalVideoWidth / _naturalVideoHeight;
        double cAspect = cW / cH;
        double dW, dH, oX, oY;

        if (cAspect > vAspect)
        { dH = cH; dW = cH * vAspect; oX = (cW - dW) / 2; oY = 0; }
        else
        { dW = cW; dH = cW / vAspect; oX = 0; oY = (cH - dH) / 2; }

        double sX = _naturalVideoWidth / dW;
        double sY = _naturalVideoHeight / dH;

        double uiX = Canvas.GetLeft(SelectionRect);
        double uiY = Canvas.GetTop(SelectionRect);
        double uiW = SelectionRect.Width;
        double uiH = SelectionRect.Height;

        double mX = (uiX - oX) * sX;
        double mY = (uiY - oY) * sY;
        double mW = uiW * sX;
        double mH = uiH * sY;

        double cX = Math.Max(0, mX);
        double cY = Math.Max(0, mY);
        double cWw = Math.Min(_naturalVideoWidth - cX, mW);
        double cHh = Math.Min(_naturalVideoHeight - cY, mH);

        if (cWw <= 0 || cHh <= 0) return false;
        videoX = cX; videoY = cY; videoWidth = cWw; videoHeight = cHh;
        return true;
    }

    private bool TryConvertRegionToVideoPixels(
        RegionModel region, out int vx, out int vy, out int vw, out int vh)
    {
        vx = vy = vw = vh = 0;
        if (_naturalVideoWidth <= 0 || _naturalVideoHeight <= 0) return false;

        double cW = DrawingCanvas.ActualWidth;
        double cH = DrawingCanvas.ActualHeight;
        if (cW <= 0 || cH <= 0) return false;

        double vAspect = _naturalVideoWidth / _naturalVideoHeight;
        double cAspect = cW / cH;
        double dW, dH, oX, oY;

        if (cAspect > vAspect)
        { dH = cH; dW = cH * vAspect; oX = (cW - dW) / 2; oY = 0; }
        else
        { dW = cW; dH = cW / vAspect; oX = 0; oY = (cH - dH) / 2; }

        double sX = _naturalVideoWidth / dW;
        double sY = _naturalVideoHeight / dH;

        int cx = (int)Math.Max(0, Math.Round((region.X - oX) * sX));
        int cy = (int)Math.Max(0, Math.Round((region.Y - oY) * sY));
        int cw = (int)Math.Round(Math.Min(_naturalVideoWidth - cx, region.Width * sX));
        int ch = (int)Math.Round(Math.Min(_naturalVideoHeight - cy, region.Height * sY));

        if (cw <= 0 || ch <= 0) return false;
        vx = cx; vy = cy; vw = cw; vh = ch;
        return true;
    }

    // ------------------------------------------------------------------
    // Watermark removal
    // ------------------------------------------------------------------

    private static async Task RunFFmpegAsync(string ffmpegPath, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();

        process.OutputDataReceived += (s, e) => { };
        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start FFmpeg process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            string error = stderr.Length > 0 ? stderr.ToString().Trim()
                                             : $"FFmpeg exited with code {process.ExitCode}";
            throw new Exception(error);
        }
    }

    private async void RemoveWatermark_Click(object sender, RoutedEventArgs e)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue)
        {
            MessageBox.Show("Please open a video first.", "No Video",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_naturalVideoWidth == 0 || _naturalVideoHeight == 0)
        {
            MessageBox.Show("Video dimensions not available. Please play the video first.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_selectedRegions.Count == 0)
        {
            MessageBox.Show("Please add at least one region first.", "No Regions",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        double videoDurationSec = WatermarkVideoPlayer.NaturalDuration.Value.TotalSeconds;
        var filters = new List<string>();

        foreach (var region in _selectedRegions)
        {
            if (!TryConvertRegionToVideoPixels(region, out int vx, out int vy, out int vw, out int vh))
                continue;

            double startSec = region.StartTime.TotalSeconds;
            double endSec = region.EndTime == TimeSpan.MaxValue
                ? videoDurationSec : region.EndTime.TotalSeconds;

            filters.Add(
                $"delogo=x={vx}:y={vy}:w={vw}:h={vh}:show=0:" +
                $"enable='between(t,{startSec:F3},{endSec:F3})'");
        }

        if (filters.Count == 0)
        {
            MessageBox.Show("All regions are outside the video bounds.", "Selection Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!File.Exists(_ffmpegPath))
        {
            MessageBox.Show($"FFmpeg not found.\nExpected at:\n{_ffmpegPath}",
                "FFmpeg Missing", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string inputFile = currentInputPath!;
        string outputFile = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(inputFile) ?? AppDomain.CurrentDomain.BaseDirectory,
            $"{System.IO.Path.GetFileNameWithoutExtension(inputFile)}_cleaned.mp4");

        string filterArgs = string.Join(",", filters);
        string arguments =
            $"-i \"{inputFile}\" -vf \"{filterArgs}\" -c:v libx264 -preset slow -crf 17 -c:a copy -y \"{outputFile}\"";

        var btn = sender as Button;
        if (btn != null) { btn.Content = "Processing..."; btn.IsEnabled = false; }

        try
        {
            await RunFFmpegAsync(_ffmpegPath, arguments);
            MessageBox.Show($"Watermark removed successfully!\nSaved to: {outputFile}",
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (btn != null) { btn.Content = "✨ Remove"; btn.IsEnabled = true; }
        }
    }

    private async void CancelWatermark_Click(object sender, RoutedEventArgs e)
    {
        if (_isSelectionMode)
        {
            ExitSelectionMode(true);
            return;
        }

        bool confirmed = false;
        try
        {
            var dialog = new ModernConfirmDialog(
                "Close Video",
                "Do you want to close the current video? All unsaved regions will be lost.");
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();
            confirmed = dialog.Confirmed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Dialog error: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!confirmed) return;

        try { await CleanupResourcesAsync(); }
        catch (Exception ex) { Debug.WriteLine($"CancelWatermark cleanup: {ex.Message}"); }

        FireCloseRequested();
    }

    // ------------------------------------------------------------------
    // Region drawing (canvas mouse events)
    // ------------------------------------------------------------------

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawingMode || !WatermarkVideoPlayer.NaturalDuration.HasValue) return;

        _isDrawing = true;
        _startPoint = e.GetPosition(DrawingCanvas);

        int index = _regionColorIndex % _regionStrokeBrushes.Length;
        SelectionRect.Stroke = _regionStrokeBrushes[index];
        SelectionRect.Fill = _regionFillBrushes[index];
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _startPoint.X);
        Canvas.SetTop(SelectionRect, _startPoint.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        DrawingCanvas.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDrawing) return;

        var pos = e.GetPosition(DrawingCanvas);
        var x = Math.Min(pos.X, _startPoint.X);
        var y = Math.Min(pos.Y, _startPoint.Y);
        var w = Math.Abs(pos.X - _startPoint.X);
        var h = Math.Abs(pos.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawing) return;

        _isDrawing = false;
        DrawingCanvas.ReleaseMouseCapture();

        if (SelectionRect.Width <= 5 || SelectionRect.Height <= 5)
        {
            ClearActiveSelection();
        }

        if (SelectionRect.Width > 5 && SelectionRect.Height > 5 &&
            TryGetSelectionInVideoPixels(out double vx, out double vy, out double vw, out double vh))
        {
            _lastSelectionVideoX = vx;
            _lastSelectionVideoY = vy;
            _lastSelectionVideoWidth = vw;
            _lastSelectionVideoHeight = vh;
        }
    }

    // ------------------------------------------------------------------
    // Select / Add region buttons
    // ------------------------------------------------------------------

    private void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue)
        {
            MessageBox.Show("Please open a video first.", "No Video",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_isDrawingMode)
        {
            DisableDrawingMode();
            _isSelectionMode = false;
        }
        else
        {
            EnableDrawingMode();
            _isSelectionMode = true;
            ClearActiveSelection();
        }
    }

    private void AddRegion_Click(object sender, RoutedEventArgs e)
    {
        if (SelectionRect.Visibility != Visibility.Visible ||
            SelectionRect.Width <= 5 || SelectionRect.Height <= 5)
        {
            MessageBox.Show("Please draw a region first.", "No Selection",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TimeSpan resolvedEnd = _currentOutPoint;
        if (resolvedEnd == TimeSpan.MaxValue && WatermarkVideoPlayer.NaturalDuration.HasValue)
            resolvedEnd = WatermarkVideoPlayer.NaturalDuration.Value;

        int index = _regionColorIndex % _regionStrokeBrushes.Length;
        var region = new RegionModel
        {
            X = Canvas.GetLeft(SelectionRect),
            Y = Canvas.GetTop(SelectionRect),
            Width = SelectionRect.Width,
            Height = SelectionRect.Height,
            Stroke = _regionStrokeBrushes[index],
            Fill = _regionFillBrushes[index],
            StartTime = _currentInPoint,
            EndTime = resolvedEnd
        };
        _selectedRegions.Add(region);
        _regionColorIndex = (_regionColorIndex + 1) % _regionStrokeBrushes.Length;
        ApplySelectionBrushes();
        ClearActiveSelection();
        DeselectRegion();
        UpdateTimelineVisuals();

        // Reset in/out for the next region
        _currentInPoint = TimeSpan.Zero;
        _currentOutPoint = TimeSpan.MaxValue;
        if (WmInPointLabel != null) WmInPointLabel.Text = "IN  00:00:00.0";
        if (WmOutPointLabel != null) WmOutPointLabel.Text = "OUT  END";

        if (_isSelectionMode)
            ExitSelectionMode(true);
    }

    // ------------------------------------------------------------------
    // Temporal In / Out point handlers
    // ------------------------------------------------------------------

    private void WmSetIn_Click(object sender, RoutedEventArgs e)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue) return;
        _currentInPoint = WatermarkVideoPlayer.Position;
        if (_currentInPoint > _currentOutPoint && _currentOutPoint != TimeSpan.MaxValue)
            _currentOutPoint = TimeSpan.MaxValue;
        if (WmInPointLabel != null)
            WmInPointLabel.Text = $"IN  {_currentInPoint:hh\\:mm\\:ss\\.f}";
    }

    private void WmSetOut_Click(object sender, RoutedEventArgs e)
    {
        if (!WatermarkVideoPlayer.NaturalDuration.HasValue) return;
        _currentOutPoint = WatermarkVideoPlayer.Position;
        if (_currentOutPoint < _currentInPoint) _currentInPoint = TimeSpan.Zero;
        if (WmOutPointLabel != null)
            WmOutPointLabel.Text = $"OUT  {_currentOutPoint:hh\\:mm\\:ss\\.f}";
    }

    // ------------------------------------------------------------------
    // Region click-to-select / deselect
    // ------------------------------------------------------------------

    private void Region_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Rectangle clickedRect)
        {
            if (clickedRect.DataContext is not RegionModel region) return;

            if (_selectedRegion == region)
            {
                DeselectRegion();
                e.Handled = true;
                return;
            }

            DeselectRegion();
            _selectedRegion = region;
            _selectedRectangleVisual = clickedRect;
            region.IsSelected = true;
            clickedRect.Stroke = new SolidColorBrush(Color.FromRgb(255, 165, 0));
            clickedRect.StrokeThickness = 3;
            e.Handled = true;
        }
    }

    private void DeselectRegion()
    {
        if (_selectedRegion != null)
        {
            _selectedRegion.IsSelected = false;
            if (_selectedRectangleVisual != null)
            {
                _selectedRectangleVisual.Stroke = _selectedRegion.Stroke;
                _selectedRectangleVisual.StrokeThickness = 2;
            }
        }
        _selectedRegion = null;
        _selectedRectangleVisual = null;
    }

    /// <summary>Handler for the on-canvas X close button on each region overlay.</summary>
    private void RegionClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RegionModel region)
        {
            _selectedRegions.Remove(region);
            if (_selectedRegion == region)
            {
                _selectedRegion = null;
                _selectedRectangleVisual = null;
            }
            _regionColorIndex = _selectedRegions.Count % _regionStrokeBrushes.Length;
            ApplySelectionBrushes();
            UpdateTimelineVisuals();
        }
    }
}
