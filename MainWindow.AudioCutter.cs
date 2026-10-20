using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using CortexFX.Core.Audio;
using CortexFX.Core.Constants;
using CortexFX.Dialogs;
using CortexFX.Models;

namespace CortexFX;

public partial class MainWindow
{
    // Audio Editor State
    private string? _currentAudioFile;
    private string? _pendingAudioFile;
    private AudioFileReader? _audioReader;
    private WaveOutEvent? _waveOut;
    private LiveSpectrumAnalyzer? _spectrumAnalyzer;
    private readonly float[] _spectrumBands = new float[48];
    private readonly float[] _displaySpectrumBands = new float[48];
    private TimeSpan _selectionStart = TimeSpan.Zero;
    private TimeSpan _selectionEnd = TimeSpan.Zero;
    private System.Windows.Threading.DispatcherTimer? _playbackTimer;
    private bool _isDraggingAudioSelection;
    private double _audioSelectionDragStartX;
    private TimeSpan _audioSelectionDragStartTime = TimeSpan.Zero;
    private CancellationTokenSource? _waveformRenderCts;
    private string? _waveformCacheFile;
    private int _waveformCacheBuckets;
    private double[]? _waveformCachePeaks;
    private bool _isSavingAudioSelection;
    private bool _isPreviewMuted;
    private double _previewVolume = 1.0;

    #region Audio Cutter

    // --- Audio Editor Implementation ---

    private void LoadAudioEditor(string filePath)
    {
        try
        {
            _currentAudioFile = filePath;
            CancelWaveformRender();
            _waveformCacheFile = null;
            _waveformCacheBuckets = 0;
            _waveformCachePeaks = null;

            // Switch UI
            if (_currentMode != AppMode.AudioTrimmer)
            {
                _audioReturnMode = _currentMode;
                _audioReturnFilterMode = _universalFilterMode;
            }

            _currentMode = AppMode.AudioTrimmer;
            if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;
            BackBtn.Visibility = Visibility.Visible;
            ShowMainContent(AudioEditorGrid);
            CurrentToolTitle.Text = "Audio Studio";
            AudioEditorFileNameText.Text = System.IO.Path.GetFileName(filePath);

            // Initialize Audio + live spectrum analyzer in the playback chain
            _playbackTimer?.Stop();
            if (_audioReader != null) { _audioReader.Dispose(); }
            if (_waveOut != null) { _waveOut.Dispose(); }
            _spectrumAnalyzer = null;

            _audioReader = new AudioFileReader(filePath);
            _spectrumAnalyzer = new LiveSpectrumAnalyzer(_audioReader, bandCount: _spectrumBands.Length, fftLength: 1024);
            _waveOut = new WaveOutEvent { DesiredLatency = 80 };
            _waveOut.Init(_spectrumAnalyzer);
            _isPreviewMuted = false;
            _previewVolume = 1.0;
            AudioGainSlider.Value = 100;
            AudioGainText.Text = "100%";
            _audioReader.Volume = 1f;
            UpdatePreviewSoundButton();
            UpdatePlaybackButton(false);
            _spectrumAnalyzer.ResetLevels();
            Array.Clear(_displaySpectrumBands);
            if (SpectrumStatusText != null)
            {
                SpectrumStatusText.Visibility = Visibility.Visible;
                SpectrumStatusText.Text = "Play selection to see real-time spectrum";
            }

            // Reset Selection
            _selectionStart = TimeSpan.Zero;
            _selectionEnd = _audioReader.TotalTime;
            _isDraggingAudioSelection = false;
            _audioSelectionDragStartTime = TimeSpan.Zero;
            _audioSelectionDragStartX = 0;

            InitializeAudioStudio();

            // Wait for layout update to draw waveform correctly
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                DrawWaveform();
                UpdateSelectionVisuals();
                UpdateTimeDisplay();
                UpdatePlaybackCursor();
                ClearSpectrumCanvas();
            }));

            // Setup / refresh playback timer (waveform cursor + live spectrum)
            if (_playbackTimer == null)
            {
                _playbackTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
            }

            _playbackTimer.Tick -= AudioPlaybackTimer_Tick;
            _playbackTimer.Tick += AudioPlaybackTimer_Tick;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading audio: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            CloseAudioEditor(returnToPrevious: true);
        }
    }

    private void AudioPlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_waveOut == null || _waveOut.PlaybackState != PlaybackState.Playing)
        {
            if (_waveOut != null && _audioReader != null && AudioEditorGrid.LoopSelectionToggle.IsChecked == true && _selectionEnd > _selectionStart)
            {
                _audioReader.CurrentTime = _selectionStart;
                _waveOut.Play();
                return;
            }
            StopAudioPlayback(resetToSelectionStart: false);
            return;
        }
        UpdatePlaybackCursor();
        UpdateLiveSpectrum();
    }

    internal void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
        {
            UpdateLiveSpectrum();
        }
    }

    private void DrawWaveform()
    {
        if (_currentAudioFile == null || _audioReader == null || WaveformContainer.ActualWidth <= 0)
        {
            return;
        }

        double width = WaveformContainer.ActualWidth;
        double height = WaveformContainer.ActualHeight > 0 ? WaveformContainer.ActualHeight : 240;
        int buckets = GetWaveformBucketCount(width);

        if (_waveformCachePeaks != null &&
            _waveformCacheBuckets == buckets &&
            string.Equals(_waveformCacheFile, _currentAudioFile, StringComparison.OrdinalIgnoreCase))
        {
            ApplyWaveformPeaks(_waveformCachePeaks, width, height);
            return;
        }

        CancelWaveformRender();
        ClearWaveformCanvas();

        var cts = new CancellationTokenSource();
        _waveformRenderCts = cts;
        string filePath = _currentAudioFile;
        _ = RenderWaveformAsync(filePath, buckets, width, height, cts);
    }

    private async Task RenderWaveformAsync(string filePath, int buckets, double width, double height, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;

        try
        {
            double[] peaks = await Task.Run(() => BuildWaveformPeaks(filePath, buckets, token), token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested ||
                    !string.Equals(_currentAudioFile, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _waveformCacheFile = filePath;
                _waveformCacheBuckets = buckets;
                _waveformCachePeaks = peaks;
                ApplyWaveformPeaks(peaks, width, height);
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (string.Equals(_currentAudioFile, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    ClearWaveformCanvas();
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        finally
        {
            if (ReferenceEquals(_waveformRenderCts, cts))
            {
                _waveformRenderCts = null;
            }

            cts.Dispose();
        }
    }

    private void ApplyWaveformPeaks(double[] peaks, double width, double height)
    {
        DrawWaveformBars(peaks, width, height);
        WaveformCanvas.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            });
        UpdateSelectionVisuals();
        UpdatePlaybackCursor();
    }

    private static int GetWaveformBucketCount(double width)
    {
        return Math.Max(96, Math.Min(480, (int)(width / 2.5)));
    }

    private static double[] BuildWaveformPeaks(string filePath, int buckets, CancellationToken token)
    {
        using var waveformReader = new AudioFileReader(filePath);

        double totalSeconds = waveformReader.TotalTime.TotalSeconds;
        if (totalSeconds <= 0 || buckets <= 0)
        {
            return Array.Empty<double>();
        }

        int channels = Math.Max(1, waveformReader.WaveFormat.Channels);
        int sampleRate = Math.Max(1, waveformReader.WaveFormat.SampleRate);
        long totalFrames = Math.Max(1, (long)Math.Ceiling(totalSeconds * sampleRate));
        int bufferFrames = Math.Max(2048, sampleRate / 12);
        int bufferSize = bufferFrames * channels;
        float[] buffer = new float[bufferSize];
        double[] peaks = new double[buckets];
        double[] energy = new double[buckets];
        int[] counts = new int[buckets];
        long frameIndex = 0;
        int samplesRead;

        while ((samplesRead = waveformReader.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();

            int framesRead = samplesRead / channels;
            for (int frame = 0; frame < framesRead; frame++)
            {
                double framePeak = 0;
                int sampleOffset = frame * channels;

                for (int channel = 0; channel < channels; channel++)
                {
                    double sample = Math.Abs(buffer[sampleOffset + channel]);
                    if (sample > framePeak) framePeak = sample;
                }

                int bucket = (int)Math.Min(buckets - 1, (frameIndex * buckets) / totalFrames);
                if (framePeak > peaks[bucket])
                {
                    peaks[bucket] = framePeak;
                }

                energy[bucket] += framePeak * framePeak;
                counts[bucket]++;
                frameIndex++;
            }
        }

        double loudest = 0;
        for (int i = 0; i < peaks.Length; i++)
        {
            double rms = counts[i] > 0 ? Math.Sqrt(energy[i] / counts[i]) : 0;
            peaks[i] = Math.Min(1, (peaks[i] * 0.72) + (rms * 0.46));
            if (peaks[i] > loudest) loudest = peaks[i];
        }

        if (loudest > 0)
        {
            for (int i = 0; i < peaks.Length; i++)
            {
                double normalized = peaks[i] / loudest;
                peaks[i] = Math.Min(1, Math.Pow(normalized, 0.72));
            }
        }

        return peaks;
    }

    private void DrawWaveformBars(IReadOnlyList<double> peaks, double width, double height)
    {
        ClearWaveformCanvas();
        if (peaks.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        double mid = height / 2;
        double step = width / peaks.Count;
        double strokeThickness = Math.Max(1, Math.Min(2.4, step * 0.62));

        for (int i = 0; i < peaks.Count; i++)
        {
            double x = (i * step) + (step / 2);
            double amplitude = Math.Max(2, peaks[i] * mid * 0.9);
            var bar = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = mid - amplitude,
                Y2 = mid + amplitude,
                Stroke = CreateWaveformBrush(peaks[i]),
                StrokeThickness = strokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.96,
                SnapsToDevicePixels = true
            };

            WaveformCanvas.Children.Add(bar);
        }
    }

    private void ClearWaveformCanvas()
    {
        WaveformCanvas?.Children.Clear();
    }

    private void ClearSpectrumCanvas()
    {
        SpectrumCanvas?.Children.Clear();
    }

    private void UpdateLiveSpectrum()
    {
        if (SpectrumCanvas == null || _spectrumAnalyzer == null)
        {
            return;
        }

        bool playing = _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing;
        if (!playing)
        {
            return;
        }

        _spectrumAnalyzer.CopyBandLevels(_spectrumBands);
        for (int i = 0; i < _spectrumBands.Length; i++)
        {
            float target = _spectrumBands[i];
            float ease = target > _displaySpectrumBands[i] ? 0.42f : 0.15f;
            _displaySpectrumBands[i] += (target - _displaySpectrumBands[i]) * ease;
        }
        DrawSpectrumBars(_displaySpectrumBands);
    }

    private void DrawSpectrumBars(IReadOnlyList<float> bands)
    {
        if (SpectrumCanvas == null)
        {
            return;
        }

        double width = SpectrumHost?.ActualWidth > 1 ? SpectrumHost.ActualWidth : SpectrumCanvas.ActualWidth;
        double height = SpectrumHost?.ActualHeight > 1 ? SpectrumHost.ActualHeight : SpectrumCanvas.ActualHeight;
        if (width <= 1 || height <= 1 || bands.Count == 0)
        {
            return;
        }

        SpectrumCanvas.Width = width;
        SpectrumCanvas.Height = height;
        SpectrumCanvas.Children.Clear();
        if (SpectrumStatusText != null)
        {
            SpectrumStatusText.Visibility = Visibility.Collapsed;
        }

        double gap = 2;
        double barWidth = Math.Max(2, (width - gap * (bands.Count - 1)) / bands.Count);

        for (int i = 0; i < bands.Count; i++)
        {
            double level = Math.Clamp(bands[i], 0, 1);
            // Ease the visual curve a bit so quiet content still moves.
            level = Math.Pow(level, 0.85);
            double barHeight = Math.Max(2, level * height);
            double x = i * (barWidth + gap);
            double y = height - barHeight;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                RadiusX = 2,
                RadiusY = 2,
                Fill = CreateSpectrumBrush(level, i, bands.Count)
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            SpectrumCanvas.Children.Add(rect);
        }
    }

    private static Brush CreateSpectrumBrush(double level, int index, int total)
    {
        double t = total <= 1 ? 0 : (double)index / (total - 1);
        byte r = (byte)(56 + (225 - 56) * t);      // cyan -> coral
        byte g = (byte)(189 - (189 - 70) * t);
        byte b = (byte)(248 - (248 - 90) * t);
        byte a = (byte)(140 + 115 * Math.Clamp(level, 0, 1));
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateWaveformBrush(double peak)
    {
        var brush = new SolidColorBrush(GetWaveformColor(peak));
        brush.Freeze();
        return brush;
    }

    private static Color GetWaveformColor(double peak)
    {
        peak = Math.Max(0, Math.Min(1, peak));

        if (peak < 0.32)
        {
            return LerpColor(Color.FromRgb(0x1E, 0x3A, 0x8A), Color.FromRgb(0x25, 0x63, 0xEB), peak / 0.32);
        }

        if (peak < 0.7)
        {
            return LerpColor(Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x38, 0xBD, 0xF8), (peak - 0.32) / 0.38);
        }

        if (peak < 0.9)
        {
            return LerpColor(Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x7D, 0xD3, 0xFC), (peak - 0.7) / 0.2);
        }

        return LerpColor(Color.FromRgb(0x7D, 0xD3, 0xFC), Color.FromRgb(0xBA, 0xE6, 0xFD), (peak - 0.9) / 0.1);
    }

    private static Color LerpColor(Color from, Color to, double amount)
    {
        amount = Math.Max(0, Math.Min(1, amount));
        byte r = (byte)(from.R + ((to.R - from.R) * amount));
        byte g = (byte)(from.G + ((to.G - from.G) * amount));
        byte b = (byte)(from.B + ((to.B - from.B) * amount));
        return Color.FromRgb(r, g, b);
    }

    private void CancelWaveformRender()
    {
        CancellationTokenSource? cts = _waveformRenderCts;
        _waveformRenderCts = null;
        cts?.Cancel();
    }

    private void UpdatePlaybackCursor()
    {
        if (_audioReader == null || WaveformContainer.ActualWidth <= 0) return;

        double progress = 0;
        if (_audioReader.TotalTime.TotalSeconds > 0)
            progress = _audioReader.CurrentTime.TotalSeconds / _audioReader.TotalTime.TotalSeconds;

        double x = progress * WaveformContainer.ActualWidth;

        if (x < 0) x = 0;
        if (x > WaveformContainer.ActualWidth) x = WaveformContainer.ActualWidth;

        PlaybackCursor.X1 = x;
        PlaybackCursor.X2 = x;
        PlaybackCursor.Y2 = Math.Max(1, WaveformContainer.ActualHeight);
        UpdateCurrentTimeDisplay();

        if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
        {
            if (_selectionEnd > TimeSpan.Zero && _audioReader.CurrentTime >= _selectionEnd)
            {
                if (AudioEditorGrid.LoopSelectionToggle.IsChecked == true && _selectionEnd > _selectionStart)
                {
                    _audioReader.CurrentTime = _selectionStart;
                    return;
                }
                _waveOut.Pause();
                _audioReader.CurrentTime = _selectionEnd;
                _playbackTimer?.Stop();
                UpdatePlaybackButton(false);
                UpdateCurrentTimeDisplay();
            }
        }
    }

    internal void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_audioReader == null) return;

        WaveformContainer.Focus();
        Point p = e.GetPosition(WaveformContainer);
        double total = _audioReader.TotalTime.TotalSeconds;
        double width = WaveformContainer.ActualWidth;
        double inX = total > 0 ? _selectionStart.TotalSeconds / total * width : 0;
        double outX = total > 0 ? _selectionEnd.TotalSeconds / total * width : width;
        _audioSelectionEdge = Math.Abs(p.X - inX) <= 8 ? -1 : Math.Abs(p.X - outX) <= 8 ? 1 : 0;
        _audioSelectionDragStartX = p.X;
        _audioSelectionDragStartTime = GetAudioTimeFromWaveformPoint(p);
        _isDraggingAudioSelection = false;

        _audioReader.CurrentTime = _audioSelectionDragStartTime;
        UpdatePlaybackCursor();
        WaveformContainer.CaptureMouse();
        e.Handled = true;
    }

    internal void Waveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (_audioReader == null || !WaveformContainer.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point p = e.GetPosition(WaveformContainer);
        TimeSpan currentTime = GetAudioTimeFromWaveformPoint(p);

        if (!_isDraggingAudioSelection && Math.Abs(p.X - _audioSelectionDragStartX) > 4)
        {
            _isDraggingAudioSelection = true;
        }

        if (_isDraggingAudioSelection)
        {
            if (_audioSelectionEdge < 0) _selectionStart = MinTime(currentTime, _selectionEnd);
            else if (_audioSelectionEdge > 0) _selectionEnd = MaxTime(currentTime, _selectionStart);
            else
            {
                _selectionStart = MinTime(_audioSelectionDragStartTime, currentTime);
                _selectionEnd = MaxTime(_audioSelectionDragStartTime, currentTime);
            }
            NormalizeSelection();
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
        }

        UpdateCurrentTimeDisplay(currentTime);
        e.Handled = true;
    }

    internal void Waveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_audioReader == null) return;

        if (WaveformContainer.IsMouseCaptured)
        {
            WaveformContainer.ReleaseMouseCapture();
        }

        Point p = e.GetPosition(WaveformContainer);
        TimeSpan currentTime = GetAudioTimeFromWaveformPoint(p);

        if (_isDraggingAudioSelection)
        {
            if (_audioSelectionEdge < 0) _selectionStart = MinTime(currentTime, _selectionEnd);
            else if (_audioSelectionEdge > 0) _selectionEnd = MaxTime(currentTime, _selectionStart);
            else
            {
                _selectionStart = MinTime(_audioSelectionDragStartTime, currentTime);
                _selectionEnd = MaxTime(_audioSelectionDragStartTime, currentTime);
            }
            NormalizeSelection();
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
            _audioReader.CurrentTime = _selectionStart;
            UpdatePlaybackCursor();
        }
        else
        {
            _audioReader.CurrentTime = currentTime;
            UpdatePlaybackCursor();
        }

        _isDraggingAudioSelection = false;
        e.Handled = true;
    }

    internal void WaveformContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawWaveform();
        UpdateSelectionVisuals();
        UpdatePlaybackCursor();
    }

    private TimeSpan GetAudioTimeFromWaveformPoint(Point point)
    {
        if (_audioReader == null || WaveformContainer.ActualWidth <= 0)
        {
            return TimeSpan.Zero;
        }

        double progress = point.X / WaveformContainer.ActualWidth;
        progress = Math.Max(0, Math.Min(1, progress));
        return TimeSpan.FromSeconds(_audioReader.TotalTime.TotalSeconds * progress);
    }

    private TimeSpan ClampAudioTime(TimeSpan value)
    {
        if (_audioReader == null) return TimeSpan.Zero;

        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        if (value > _audioReader.TotalTime) return _audioReader.TotalTime;
        return value;
    }

    private static TimeSpan MinTime(TimeSpan first, TimeSpan second) => first <= second ? first : second;

    private static TimeSpan MaxTime(TimeSpan first, TimeSpan second) => first >= second ? first : second;

    private static string FormatAudioTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"hh\:mm\:ss\.fff")
            : time.ToString(@"mm\:ss\.fff");
    }

    private void NormalizeSelection()
    {
        if (_audioReader == null) return;

        _selectionStart = ClampAudioTime(_selectionStart);
        _selectionEnd = ClampAudioTime(_selectionEnd);

        if (_selectionEnd < _selectionStart)
        {
            (_selectionStart, _selectionEnd) = (_selectionEnd, _selectionStart);
        }
    }

    private void UpdateCurrentTimeDisplay(TimeSpan? previewTime = null)
    {
        if (CurrentTimeDisplay == null) return;

        CurrentTimeDisplay.Text = _audioReader == null && previewTime == null
            ? "00:00.000"
            : FormatAudioTime(previewTime.HasValue ? ClampAudioTime(previewTime.Value) : ClampAudioTime(_audioReader!.CurrentTime));
    }

    private void UpdateTimeDisplay()
    {
        NormalizeSelection();

        TimeSpan duration = _selectionEnd - _selectionStart;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        StartTimeDisplay.Text = FormatAudioTime(_selectionStart);
        EndTimeDisplay.Text = FormatAudioTime(_selectionEnd);
        DurationTimeDisplay.Text = FormatAudioTime(duration);
        DurationTimeDisplay.Foreground = duration.TotalMilliseconds > 0
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7DD3FC"))
            : Brushes.Gray;
        UpdateCurrentTimeDisplay();
        UpdateAudioStudioDetails();
    }

    internal void SetStart_Click(object sender, RoutedEventArgs e)
    {
        if (_audioReader != null)
        {
            _selectionStart = ClampAudioTime(_audioReader.CurrentTime);
            if (_selectionStart > _selectionEnd) _selectionEnd = _audioReader.TotalTime;
            NormalizeSelection();
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
        }
    }

    internal void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_audioReader != null)
        {
            _selectionEnd = ClampAudioTime(_audioReader.CurrentTime);
            if (_selectionEnd < _selectionStart) _selectionStart = TimeSpan.Zero;
            NormalizeSelection();
            UpdateSelectionVisuals();
            UpdateTimeDisplay();
        }
    }

    private void UpdateSelectionVisuals()
    {
        if (_audioReader == null || WaveformContainer.ActualWidth <= 0) return;

        NormalizeSelection();

        double totalSeconds = _audioReader.TotalTime.TotalSeconds;
        if (totalSeconds <= 0) return;

        double totalWidth = WaveformContainer.ActualWidth;
        double startX = (_selectionStart.TotalSeconds / totalSeconds) * totalWidth;
        double endX = (_selectionEnd.TotalSeconds / totalSeconds) * totalWidth;

        startX = Math.Max(0, Math.Min(totalWidth, startX));
        endX = Math.Max(0, Math.Min(totalWidth, endX));

        DimLeft.Width = startX;

        double rightWidth = totalWidth - endX;
        if (rightWidth < 0) rightWidth = 0;
        DimRight.Width = rightWidth;
        DimRight.Margin = new Thickness(endX, 0, 0, 0);

        SelectionBand.Margin = new Thickness(startX, 0, 0, 0);
        SelectionBand.Width = Math.Max(0, endX - startX);

        StartMarker.Margin = new Thickness(startX, 0, 0, 0);
        EndMarker.Margin = new Thickness(Math.Max(0, endX - EndMarker.Width), 0, 0, 0);
    }

    private static bool FormatSupportsEmbeddedCover(string extension) =>
        extension is ".mp3" or ".m4a" or ".aac" or ".flac" or ".ogg" or ".opus" or ".wma";

    internal static string BuildAudioExportArgs(string inputFile, string outputFile, string start, string duration)
    {
        string outExt = System.IO.Path.GetExtension(outputFile).ToLowerInvariant();
        string inExt = System.IO.Path.GetExtension(inputFile).ToLowerInvariant();
        bool sameContainer = string.Equals(outExt, inExt, StringComparison.OrdinalIgnoreCase);
        bool keepCover = FormatSupportsEmbeddedCover(outExt);
        string id3 = outExt == ".mp3" ? "-id3v2_version 3 " : string.Empty;

        // Fast path: trim only — copy audio + embedded album art (do NOT use -vn).
        if (sameContainer)
        {
            if (keepCover)
            {
                return $"-ss {start} -i \"{inputFile}\" -t {duration} " +
                       $"-map_metadata 0 -map 0 -c copy {id3}-y \"{outputFile}\"";
            }

            return $"-ss {start} -i \"{inputFile}\" -t {duration} -map 0:a:0 -c copy -y \"{outputFile}\"";
        }

        string codecArgs = outExt switch
        {
            ".mp3" => "-c:a libmp3lame -q:a 2",
            ".wav" => "-c:a pcm_s16le",
            ".flac" => "-c:a flac",
            ".ogg" => "-c:a libvorbis -q:a 5",
            ".m4a" or ".aac" => "-c:a aac -b:a 192k",
            ".wma" => "-c:a wmav2 -b:a 192k",
            ".opus" => "-c:a libopus -b:a 128k",
            _ => "-c:a libmp3lame -q:a 2"
        };

        // Keep album art thumbnail from the source when the output format supports it.
        string coverArgs = keepCover
            ? "-map_metadata 0 -map 0:a:0 -map 0:v? -c:v copy -disposition:v:0 attached_pic "
            : "-map_metadata 0 -map 0:a:0 ";

        return $"-ss {start} -i \"{inputFile}\" -t {duration} {coverArgs}{codecArgs} {id3}-y \"{outputFile}\"";
    }

    internal void PlaySelection_Click(object sender, RoutedEventArgs e)
    {
        if (_waveOut != null && _audioReader != null)
        {
            try
            {
                if (_waveOut.PlaybackState == PlaybackState.Playing)
                {
                    StopAudioPlayback(resetToSelectionStart: true);
                    return;
                }

                NormalizeSelection();
                if ((_selectionEnd - _selectionStart).TotalMilliseconds <= 0)
                {
                    MessageBox.Show("Select a valid audio range first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _audioReader.CurrentTime = _selectionStart;
                UpdatePlaybackCursor();
                _spectrumAnalyzer?.ResetLevels();
                Array.Clear(_displaySpectrumBands);
                if (SpectrumStatusText != null)
                {
                    SpectrumStatusText.Visibility = Visibility.Collapsed;
                }

                _waveOut.Play();
                _playbackTimer?.Start();
                UpdatePlaybackButton(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not play audio:\n{ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal void PreviewSound_Click(object sender, RoutedEventArgs e)
    {
        if (_previewVolume <= 0)
        {
            AudioGainSlider.Value = 100;
            _isPreviewMuted = false;
        }
        else
        {
            _isPreviewMuted = !_isPreviewMuted;
        }
        if (_audioReader != null)
        {
            _audioReader.Volume = _isPreviewMuted ? 0f : (float)_previewVolume;
        }
        UpdatePreviewSoundButton();
    }

    internal void AudioGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _previewVolume = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        AudioGainText.Text = $"{(int)Math.Round(_previewVolume * 100)}%";
        UpdatePreviewSoundButton();
        if (_audioReader != null && !_isPreviewMuted)
        {
            _audioReader.Volume = (float)_previewVolume;
        }
    }

    private void UpdatePreviewSoundButton()
    {
        bool silent = _isPreviewMuted || _previewVolume <= 0;
        PreviewSoundIcon.Text = silent ? "\uE74F" : "\uE767";
        PreviewSoundLabel.Text = silent ? "Muted" : "Sound";
        PreviewSoundBtn.ToolTip = _previewVolume <= 0
            ? "Restore sound to 100%"
            : _isPreviewMuted ? "Unmute preview sound" : "Mute preview sound";
    }

    private void UpdatePlaybackButton(bool playing)
    {
        PlaybackButtonIcon.Text = playing ? "\uE71A" : "\uE768";
        PlaybackButtonLabel.Text = playing ? "Stop" : "Play";
        PlaySelectionBtn.ToolTip = playing ? "Stop preview" : "Play selected range";
    }

    private void StopAudioPlayback(bool resetToSelectionStart)
    {
        if (_waveOut != null && _audioReader != null)
        {
            try
            {
                if (_waveOut.PlaybackState == PlaybackState.Playing)
                {
                    _waveOut.Pause();
                }

                _playbackTimer?.Stop();
                UpdatePlaybackButton(false);
                _spectrumAnalyzer?.ResetLevels();
                Array.Clear(_displaySpectrumBands);
                ClearSpectrumCanvas();
                if (SpectrumStatusText != null)
                {
                    SpectrumStatusText.Visibility = Visibility.Visible;
                    SpectrumStatusText.Text = "Play selection to see real-time spectrum";
                }

                if (resetToSelectionStart)
                {
                    _audioReader.CurrentTime = ClampAudioTime(_selectionStart);
                }

                UpdatePlaybackCursor();
                UpdateCurrentTimeDisplay();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not stop audio:\n{ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal async void SaveSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_isSavingAudioSelection)
        {
            return;
        }

        if (_currentAudioFile == null || _audioReader == null) return;

        NormalizeSelection();
        TimeSpan selectedDuration = _selectionEnd - _selectionStart;
        if (selectedDuration.TotalMilliseconds <= 0)
        {
            MessageBox.Show("Select a valid audio range before saving.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Pause first so FFmpeg isn't fighting the player
        if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
        {
            StopAudioPlayback(resetToSelectionStart: false);
        }

        string dir = System.IO.Path.GetDirectoryName(_currentAudioFile) ?? "";
        string name = System.IO.Path.GetFileNameWithoutExtension(_currentAudioFile);
        string ext = System.IO.Path.GetExtension(_currentAudioFile);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".wav";
        }

        // Let the user pick format + path
        string preferredExt = string.IsNullOrWhiteSpace(ext) ? ".mp3" : ext.ToLowerInvariant();
        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Trimmed Audio",
            FileName = $"{name}_Trimmed{preferredExt}",
            DefaultExt = preferredExt,
            Filter =
                "MP3 (*.mp3)|*.mp3|" +
                "WAV (*.wav)|*.wav|" +
                "M4A / AAC (*.m4a)|*.m4a|" +
                "FLAC (*.flac)|*.flac|" +
                "OGG (*.ogg)|*.ogg|" +
                "OPUS (*.opus)|*.opus|" +
                "WMA (*.wma)|*.wma|" +
                "All Files (*.*)|*.*",
            InitialDirectory = dir
        };

        // Match filter to the source extension when we can
        saveDialog.FilterIndex = preferredExt switch
        {
            ".mp3" => 1,
            ".wav" => 2,
            ".m4a" or ".aac" => 3,
            ".flac" => 4,
            ".ogg" => 5,
            ".opus" => 6,
            ".wma" => 7,
            _ => 1
        };

        if (saveDialog.ShowDialog() == true)
        {
            string outputFile = saveDialog.FileName;

            if (string.Equals(outputFile, _currentAudioFile, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Cannot overwrite the source file while it is open. Please choose a different name.", "File Locked", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(_config.FFmpegPath))
            {
                MessageBox.Show($"FFmpeg not found.\nExpected location:\n{_config.FFmpegPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string start = _selectionStart.ToString(@"hh\:mm\:ss\.fff");
            string duration = selectedDuration.ToString(@"hh\:mm\:ss\.fff");
            string args = BuildAudioExportArgs(_currentAudioFile, outputFile, start, duration);

            try
            {
                _isSavingAudioSelection = true;
                SaveSelectionBtn.IsEnabled = false;
                SaveSelectionBtn.Content = "Saving...";

                await _processManager.RunAsync(_config.FFmpegPath, args);

                _lastOutputFolder = System.IO.Path.GetDirectoryName(outputFile);
                SaveSelectionBtn.Content = "Export cut";

                var success = new ModernSuccessDialog(
                    "Export complete",
                    "Your trimmed audio is ready at its original volume.",
                    outputFile)
                {
                    Owner = this
                };
                success.ShowDialog();
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Audio save was cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isSavingAudioSelection = false;
                SaveSelectionBtn.IsEnabled = true;
                SaveSelectionBtn.Content = "Export cut";
            }
        }
    }

    private void CloseAudioEditor(bool returnToPrevious)
    {
        CancelAudioSeparation();
        CancelWaveformRender();

        if (_waveOut != null)
        {
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }
        _spectrumAnalyzer = null;
        if (_audioReader != null)
        {
            _audioReader.Dispose();
            _audioReader = null;
        }
        if (_playbackTimer != null)
        {
            _playbackTimer.Stop();
        }
        if (WaveformContainer != null && WaveformContainer.IsMouseCaptured)
        {
            WaveformContainer.ReleaseMouseCapture();
        }

        AudioEditorGrid.Visibility = Visibility.Collapsed;
        DropZone.Visibility = Visibility.Visible;
        FilesList.Visibility = Visibility.Visible;

        _currentAudioFile = null;
        _isDraggingAudioSelection = false;
        _waveformCacheFile = null;
        _waveformCacheBuckets = 0;
        _waveformCachePeaks = null;
        ClearWaveformCanvas();
        ClearSpectrumCanvas();
        SelectionBand.Width = 0;

        if (returnToPrevious)
        {
            RestoreAudioReturnView();
        }
    }

    private void RestoreAudioReturnView()
    {
        AppMode returnMode = _audioReturnMode is AppMode.AudioTrimmer or AppMode.Unknown
            ? AppMode.Universal
            : _audioReturnMode;

        if (returnMode == AppMode.Universal)
        {
            _universalFilterMode = _audioReturnFilterMode;
            SwitchToMode(AppMode.Universal);

            if (!string.IsNullOrWhiteSpace(_universalFilterMode))
            {
                SetCategoryRadioChecked(_universalFilterMode);
                CurrentToolTitle.Text = $"{_universalFilterMode} Converter Mode";
                PopulateFormats(_universalFilterMode);

                if (FormatComboBox.Items.Count > 0)
                {
                    FormatComboBox.SelectedIndex = 0;
                    FormatComboBox.IsEnabled = true;
                }
            }

            return;
        }

        SwitchToMode(returnMode);
    }

    #endregion
}
