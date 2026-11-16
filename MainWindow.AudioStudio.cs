using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CortexFX.Core.Audio;
using CortexFX.Core.Services.Infrastructure;
using Microsoft.Win32;

namespace CortexFX;

public partial class MainWindow
{
    private CancellationTokenSource? _audioSeparationCts;
    private AudioSeparationResult? _lastStemResult;
    private Task<AudioGpuDevice>? _audioGpuDetectionTask;
    private AudioGpuDevice? _audioGpuDevice;
    private bool _audioSeparationMode;
    private NAudio.Wave.WaveOutEvent? _stemPreviewPlayer;
    private NAudio.Wave.AudioFileReader? _stemPreviewReader;
    private string? _playingStemPath;
    private CancellationTokenSource? _audioAiDownloadCts;
    private string DownloadedAudioAiDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CortexFX", "AudioSeparation");
    private string AudioAiDirectory => Directory.Exists(DownloadedAudioAiDirectory)
        ? DownloadedAudioAiDirectory : Path.Combine(_config.ResourcesDirectory, "AudioSeparation");
    private AudioSeparationFormat SelectedSeparationFormat => AudioSeparatorEditor.OutputWav.IsChecked == true ? AudioSeparationFormat.Wav : AudioSeparationFormat.Mp3;
    private AudioSeparationProcessor SelectedSeparationProcessor => AudioSeparatorEditor.ProcessingGpu.IsChecked == true ? AudioSeparationProcessor.Gpu : AudioSeparationProcessor.Cpu;
    private int _audioSelectionEdge; // -1: in, 1: out, 0: new range
    private AudioStemSeparationService CreateSeparationService() => new(_processManager, _config.FFmpegPath,
        AudioAiDirectory);

    private void AudioSeparatorCard_Click(object sender, MouseButtonEventArgs e)
    {
        _audioSeparationMode = true;
        SwitchToMode(AppMode.AudioTrimmer);
        ShowMainContent(AudioSeparatorEditor);
        CurrentToolTitle.Text = "AI Audio Separator";
        BackBtn.Visibility = Visibility.Visible;
        if (TopNavPanel != null) TopNavPanel.Visibility = Visibility.Collapsed;
        UpdateAudioStudioDetails();
        SetSeparationBusy(false);
        _ = DetectAudioGpuAsync();
        e.Handled = true;
    }

    internal void ChooseSeparationAudio(string? path = null)
    {
        if (_audioSeparationCts != null || _audioAiDownloadCts != null) return;
        if (path == null)
        {
            var picker = new OpenFileDialog { Title = "Choose a song to separate",
                Filter = "Audio files|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.wma;*.opus;*.aiff;*.aif" };
            if (picker.ShowDialog(this) != true) return;
            path = picker.FileName;
        }
        if (!File.Exists(path) || !new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".opus", ".aiff", ".aif" }
            .Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            AudioSeparatorEditor.SeparationStatusText.Text = "Choose a supported audio file.";
            return;
        }
        _lastStemResult = null;
        StopStemPreview();
        AudioSeparatorEditor.SetWaveform([]);
        _audioSeparationMode = true;
        LoadAudioEditor(path);
    }

    internal void PreviewSeparationAudio()
    {
        if (_audioReader == null) return;
        StopStemPreview();
        _selectionStart = TimeSpan.Zero;
        _selectionEnd = _audioReader.TotalTime;
        PlaySelection_Click(this, new RoutedEventArgs());
    }

    private void SeekSeparationAudio(TimeSpan position)
    {
        if (_audioReader == null || _audioSeparationCts != null || _audioAiDownloadCts != null) return;
        StopStemPreview();
        _selectionStart = TimeSpan.Zero;
        _selectionEnd = _audioReader.TotalTime;
        try
        {
            bool wasPlaying = _waveOut?.PlaybackState == NAudio.Wave.PlaybackState.Playing;
            _waveOut?.Stop(); // Flush queued samples from the previous position before seeking.
            _audioReader.CurrentTime = ClampAudioTime(position);
            _spectrumAnalyzer?.ResetLevels();
            if (wasPlaying) _waveOut?.Play();
            UpdatePlaybackCursor();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("AudioSeek", ex.ToString());
            AudioSeparatorEditor.SeparationStatusText.Text = "Could not seek in this audio file. Try opening it again.";
        }
    }

    private void SetSeparationPreviewVolume(double percent)
    {
        _isPreviewMuted = false;
        AudioGainSlider.Value = Math.Clamp(percent, 0, 100);
        UpdatePreviewSoundButton();
    }

    internal void SeparatorPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (NeedsSeparationEngine) DownloadAudioAi(SelectedSeparationProcessor == AudioSeparationProcessor.Gpu);
        else SeparateAudio_Click(sender, e);
    }

    private bool NeedsSeparationEngine => !CreateSeparationService().IsAvailable ||
        (SelectedSeparationProcessor == AudioSeparationProcessor.Gpu && !CreateSeparationService().IsGpuEngineAvailable);

    private async Task RenderSeparationWaveformAsync(string path)
    {
        CancelWaveformRender();
        var cts = new CancellationTokenSource();
        _waveformRenderCts = cts;
        try
        {
            var peaks = await Task.Run(() => BuildWaveformPeaks(path, 100, cts.Token), cts.Token);
            if (_currentAudioFile == path && !cts.IsCancellationRequested) AudioSeparatorEditor.SetWaveform(peaks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ConsoleLogger.Error("SeparationWaveform", ex.ToString()); }
        finally
        {
            if (ReferenceEquals(_waveformRenderCts, cts)) _waveformRenderCts = null;
            cts.Dispose();
        }
    }

    internal void CancelAudioAiDownload() => _audioAiDownloadCts?.Cancel();

    internal async void DownloadAudioAi(bool gpu)
    {
        if (_audioAiDownloadCts != null || _audioSeparationCts != null) return;
        using var cts = new CancellationTokenSource();
        _audioAiDownloadCts = cts;
        SetSeparationBusy(false);
        AudioSeparatorEditor.CancelAiDownloadBtn.Visibility = Visibility.Visible;
        AudioSeparatorEditor.AiDownloadProgressPanel.Visibility = Visibility.Visible;
        AudioSeparatorEditor.UpdateDownloadProgress(new("Preparing AI download…"));
        AudioSeparatorEditor.SeparationStatusText.Text = "";
        var progress = new Progress<AudioAiDownloadProgress>(update =>
        {
            if (ReferenceEquals(_audioAiDownloadCts, cts)) AudioSeparatorEditor.UpdateDownloadProgress(update);
        });
        try
        {
            await new AudioAiInstaller(_processManager, _config.ResourcesDirectory).InstallAsync(
                DownloadedAudioAiDirectory, gpu, progress, cts.Token);
            AudioSeparatorEditor.SeparationStatusText.Text = "AI installed. Ready to separate audio locally.";
        }
        catch (OperationCanceledException) { AudioSeparatorEditor.SeparationStatusText.Text = "Download cancelled. You can retry anytime."; }
        catch (Exception ex)
        {
            ConsoleLogger.Error("AudioAiDownload", ex.ToString());
            AudioSeparatorEditor.SeparationStatusText.Text = "AI download failed. Check your connection and free disk space, then retry.";
        }
        finally
        {
            _audioAiDownloadCts = null;
            AudioSeparatorEditor.CancelAiDownloadBtn.Visibility = Visibility.Collapsed;
            AudioSeparatorEditor.AiDownloadProgressPanel.Visibility = Visibility.Collapsed;
            SetSeparationBusy(false);
            _ = DetectAudioGpuAsync();
        }
    }

    private void InitializeAudioStudio()
    {
        CancelAudioSeparation();
        AudioEditorGrid.LoopSelectionToggle.IsChecked = false;
        AudioSeparatorEditor.SeparateAudioBtn.IsEnabled = CreateSeparationService().IsAvailable;
        AudioSeparatorEditor.SeparationStatusText.Text = CreateSeparationService().IsAvailable
            ? "Ready · CPU · MP3"
            : "";
        AudioSeparatorEditor.SeparationProgressPanel.Visibility = Visibility.Collapsed;
        AudioSeparatorEditor.SeparationResultsPanel.Visibility = _lastStemResult == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateAudioStudioDetails();
        SetSeparationBusy(false);
        AudioOptions_Changed(this, new RoutedEventArgs());
        _ = DetectAudioGpuAsync();
    }

    private void UpdateAudioStudioDetails()
    {
        if (_audioReader == null)
        {
            AudioSeparatorEditor.SourceFileName.Text = "Choose your song";
            AudioSeparatorEditor.SourceDetails.Text = "Drop an audio file here";
            AudioSeparatorEditor.SourcePosition.Text = "00:00.000";
            AudioSeparatorEditor.SetWaveform([]);
            return;
        }
        var labels = new[] { AudioEditorGrid.Timeline0, AudioEditorGrid.Timeline1, AudioEditorGrid.Timeline2,
            AudioEditorGrid.Timeline3, AudioEditorGrid.Timeline4 };
        for (int i = 0; i < labels.Length; i++)
        {
            var time = TimeSpan.FromTicks(_audioReader.TotalTime.Ticks * i / 4);
            labels[i].Text = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss\.f");
        }
        AudioSeparatorEditor.SourceFileName.Text = Path.GetFileName(_currentAudioFile);
        AudioSeparatorEditor.SourceDetails.Text = FormatAudioTime(_audioReader.TotalTime) + " · " + Path.GetExtension(_currentAudioFile)?.TrimStart('.').ToUpperInvariant() + " · Full song";
        var duration = _audioReader.TotalTime;
        string format = SelectedSeparationFormat == AudioSeparationFormat.Mp3 ? "MP3 192 kbps" : "WAV uncompressed";
        string estimated = FormatStemSize(AudioStemSeparationService.EstimateStemBytes(duration, SelectedSeparationFormat));
        AudioSeparatorEditor.SeparationScopeText.Text = format + " · ~" + estimated + " per stem";
    }

    private static string FormatStemSize(long bytes) => bytes >= 1048576
        ? (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : Math.Ceiling(bytes / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";

    private async Task DetectAudioGpuAsync()
    {
        _audioGpuDetectionTask ??= Task.Run(AudioGpuDeviceDetector.Detect);
        _audioGpuDevice = await _audioGpuDetectionTask;
        AudioSeparatorEditor.ProcessingGpu.ToolTip = !_audioGpuDevice.Available ? _audioGpuDevice.Reason : !CreateSeparationService().IsGpuEngineAvailable
            ? "Optional GPU acceleration needs additional files. The setup button will install them."
            : _audioGpuDevice.Available ? _audioGpuDevice.Name + " · NVIDIA CUDA" : _audioGpuDevice.Reason;
        SetSeparationBusy(_audioSeparationCts != null);
    }

    internal void AudioOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (AudioSeparatorEditor == null) return;
        UpdateAudioStudioDetails();
        SetSeparationBusy(_audioSeparationCts != null);
        if (_audioSeparationCts == null && _audioAiDownloadCts == null && _audioReader != null && CreateSeparationService().IsAvailable)
            AudioSeparatorEditor.SeparationStatusText.Text = "Ready · " + SelectedSeparationProcessor.ToString().ToUpperInvariant() + " · " + SelectedSeparationFormat.ToString().ToUpperInvariant();
    }

    private void UpdateStemResultDetails()
    {
        if (_lastStemResult == null) return;
        string sizes = File.Exists(_lastStemResult.VocalsPath) && File.Exists(_lastStemResult.MusicPath)
            ? FormatStemSize(new FileInfo(_lastStemResult.VocalsPath).Length) + " + " + FormatStemSize(new FileInfo(_lastStemResult.MusicPath).Length) : "files moved";
        AudioSeparatorEditor.SeparationResultSummary.Text = _lastStemResult.OutputFormat.ToString().ToUpperInvariant() + " · " + sizes;
        AudioSeparatorEditor.SeparationResultSummary.ToolTip = _lastStemResult.Folder;
    }

    internal void SelectAllAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_audioReader == null) return;
        _selectionStart = TimeSpan.Zero;
        _selectionEnd = _audioReader.TotalTime;
        UpdateSelectionVisuals();
        UpdateTimeDisplay();
    }

    internal void AudioEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_audioReader == null || e.OriginalSource is System.Windows.Controls.TextBox ||
            e.OriginalSource is System.Windows.Controls.Slider) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            SelectAllAudio_Click(sender, new RoutedEventArgs());
        else if (Keyboard.Modifiers != ModifierKeys.None) return;
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not ButtonBase)
            PlaySelection_Click(sender, new RoutedEventArgs());
        else if (e.Key == Key.I) SetStart_Click(sender, new RoutedEventArgs());
        else if (e.Key == Key.O) SetEnd_Click(sender, new RoutedEventArgs());
        else return;
        e.Handled = true;
    }

    internal async void SeparateAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_audioSeparationCts != null || _audioAiDownloadCts != null || _currentAudioFile == null || _audioReader == null) return;
        NormalizeSelection();
        TimeSpan start = TimeSpan.Zero;
        TimeSpan duration = _audioReader.TotalTime;
        if (duration <= TimeSpan.Zero)
        {
            AudioSeparatorEditor.SeparationStatusText.Text = "Select an audio range first.";
            return;
        }
        var picker = new OpenFolderDialog
        {
            Title = "Choose where to save vocals and instrumental",
            InitialDirectory = Path.GetDirectoryName(_currentAudioFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (picker.ShowDialog(this) != true) return;
        StopAudioPlayback(resetToSelectionStart: false);
        StopStemPreview();
        var request = new AudioSeparationRequest(_currentAudioFile, picker.FolderName, start, duration)
        { Processor = SelectedSeparationProcessor, OutputFormat = SelectedSeparationFormat };
        await RunAudioSeparationAsync(request);
    }

    private async Task RunAudioSeparationAsync(AudioSeparationRequest request)
    {
        var cts = new CancellationTokenSource();
        _audioSeparationCts = cts;
        SetSeparationBusy(true);
        AudioSeparatorEditor.SeparationProgress.Value = 0;
        AudioSeparatorEditor.SeparationStatusText.Text = "Preparing audio on your device…";
        var progress = new Progress<AudioSeparationProgress>(p =>
        {
            if (!ReferenceEquals(_audioSeparationCts, cts)) return;
            AudioSeparatorEditor.SeparationProgress.Value = p.Percent;
            AudioSeparatorEditor.SeparationStatusText.Text = p.Message;
        });
        try
        {
            var result = await CreateSeparationService().SeparateAsync(request, progress, cts.Token);
            if (!ReferenceEquals(_audioSeparationCts, cts)) return;
            _lastStemResult = result;
            _lastOutputFolder = result.Folder;
            AudioSeparatorEditor.SeparationResultsPanel.Visibility = Visibility.Visible;
            AudioSeparatorEditor.SeparationStatusText.Text = "Saved · " + result.ProcessorUsed.ToString().ToUpperInvariant() + " · " + result.OutputFormat.ToString().ToUpperInvariant();
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_audioSeparationCts, cts))
                AudioSeparatorEditor.SeparationStatusText.Text = "Cancelled. No incomplete stems were saved.";
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("AudioSeparation", ex.ToString());
            if (ReferenceEquals(_audioSeparationCts, cts))
                AudioSeparatorEditor.SeparationStatusText.Text = ex is AudioGpuUnavailableException ? ex.Message : ex is UnauthorizedAccessException
                    ? "Cannot write to that folder. Choose another location."
                    : "Separation could not finish. Check free disk space and the local AI files, then try again.";
        }
        finally
        {
            if (ReferenceEquals(_audioSeparationCts, cts))
            {
                _audioSeparationCts = null;
                SetSeparationBusy(false);
            }
            cts.Dispose();
        }
    }

    private void SetSeparationBusy(bool busy)
    {
        bool downloading = _audioAiDownloadCts != null;
        bool available = CreateSeparationService().IsAvailable;
        bool needsEngine = NeedsSeparationEngine;
        AudioSeparatorEditor.EngineSetupPanel.Visibility = needsEngine ? Visibility.Visible : Visibility.Collapsed;
        AudioSeparatorEditor.EngineSetupDescription.Text = SelectedSeparationProcessor == AudioSeparationProcessor.Gpu
            ? "GPU download · ~2.15 GB installed"
            : "One-time download · ~82 MB installed";
        AudioSeparatorEditor.ChooseAudioBtn.IsEnabled = !busy && !downloading;
        AudioSeparatorEditor.PreviewSourceBtn.IsEnabled = !busy && !downloading && _audioReader != null;
        AudioSeparatorEditor.SourceSeekSlider.IsEnabled = !busy && !downloading && _audioReader != null;
        bool showResults = !busy && !downloading && _lastStemResult != null;
        AudioSeparatorEditor.SeparateAudioBtn.IsEnabled = !busy && !downloading && (needsEngine || _audioReader != null);
        AudioSeparatorEditor.SeparateAudioBtn.Content = downloading ? "Downloading AI engine…" : busy ? "Separating your song…" : needsEngine
            ? available ? "Enable GPU acceleration" : "Download AI engine" : "Separate vocals & music";
        AudioSeparatorEditor.ProcessingCpu.IsEnabled = !busy && !downloading;
        AudioSeparatorEditor.ProcessingGpu.IsEnabled = !busy && !downloading && _audioGpuDevice?.Available == true;
        AudioSeparatorEditor.OutputMp3.IsEnabled = !busy && !downloading;
        AudioSeparatorEditor.OutputWav.IsEnabled = !busy && !downloading;
        UpdateStemResultDetails();
        AudioSeparatorEditor.OpenVocalsBtn.IsEnabled = showResults;
        AudioSeparatorEditor.OpenMusicBtn.IsEnabled = showResults;
        AudioSeparatorEditor.SeparationProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            AudioSeparatorEditor.SeparationResultsPanel.Visibility = Visibility.Collapsed;
        }
        else if (_lastStemResult != null) AudioSeparatorEditor.SeparationResultsPanel.Visibility = Visibility.Visible;
    }

    internal void CancelSeparation_Click(object sender, RoutedEventArgs e)
    {
        _audioSeparationCts?.Cancel();
        AudioSeparatorEditor.SeparationStatusText.Text = "Cancelling…";
    }

    private void CancelAudioSeparation()
    {
        CancelAudioAiDownload();
        var cts = _audioSeparationCts;
        _audioSeparationCts = null;
        cts?.Cancel();
        SetSeparationBusy(false);
    }

    internal void OpenVocals_Click(object sender, RoutedEventArgs e) => OpenStemInEditor(_lastStemResult?.VocalsPath);
    internal void OpenMusic_Click(object sender, RoutedEventArgs e) => OpenStemInEditor(_lastStemResult?.MusicPath);
    private void OpenStemInEditor(string? path)
    {
        if (path != null && File.Exists(path))
        {
            bool stop = _playingStemPath == path && _stemPreviewPlayer?.PlaybackState == NAudio.Wave.PlaybackState.Playing;
            StopStemPreview();
            StopAudioPlayback(false);
            if (stop) return;
            try
            {
                _stemPreviewReader = new NAudio.Wave.AudioFileReader(path)
                { Volume = _isPreviewMuted ? 0f : (float)_previewVolume };
                _stemPreviewPlayer = new NAudio.Wave.WaveOutEvent();
                _stemPreviewPlayer.Init(_stemPreviewReader);
                _playingStemPath = path;
                AudioSeparatorEditor.VocalsLabel.Text = path == _lastStemResult?.VocalsPath ? "■  Stop vocals" : "▶  Vocals";
                AudioSeparatorEditor.MusicLabel.Text = path == _lastStemResult?.MusicPath ? "■  Stop music" : "▶  Instrumental";
                _stemPreviewPlayer.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_playingStemPath == path && _stemPreviewPlayer?.PlaybackState == NAudio.Wave.PlaybackState.Stopped) StopStemPreview();
                }));
                _stemPreviewPlayer.Play();
            }
            catch (Exception ex)
            {
                StopStemPreview();
                ConsoleLogger.Error("StemPreview", ex.ToString());
                AudioSeparatorEditor.SeparationStatusText.Text = "Cannot preview this stem. You can open the saved files from the output folder.";
            }
        }
        else AudioSeparatorEditor.SeparationStatusText.Text = "This output was moved or deleted.";
    }

    private void StopStemPreview()
    {
        _playingStemPath = null;
        _stemPreviewPlayer?.Stop();
        _stemPreviewPlayer?.Dispose();
        _stemPreviewPlayer = null;
        _stemPreviewReader?.Dispose();
        _stemPreviewReader = null;
        AudioSeparatorEditor.VocalsLabel.Text = "▶  Vocals";
        AudioSeparatorEditor.MusicLabel.Text = "▶  Instrumental";
    }
    internal void OpenStemsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastStemResult != null && Directory.Exists(_lastStemResult.Folder))
        {
            try { Process.Start(new ProcessStartInfo(_lastStemResult.Folder) { UseShellExecute = true }); }
            catch (Exception ex) { ConsoleLogger.Error("AudioSeparation", ex.ToString()); }
        }
        else AudioSeparatorEditor.SeparationStatusText.Text = "The output folder was moved or deleted.";
    }
}
