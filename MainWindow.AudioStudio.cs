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
    private AudioSeparationFormat SelectedSeparationFormat => AudioEditorGrid.OutputWav.IsChecked == true ? AudioSeparationFormat.Wav : AudioSeparationFormat.Mp3;
    private AudioSeparationProcessor SelectedSeparationProcessor => AudioEditorGrid.ProcessingGpu.IsChecked == true ? AudioSeparationProcessor.Gpu : AudioSeparationProcessor.Cpu;
    private int _audioSelectionEdge; // -1: in, 1: out, 0: new range
    private AudioStemSeparationService CreateSeparationService() => new(_processManager, _config.FFmpegPath,
        Path.Combine(_config.ResourcesDirectory, "AudioSeparation"));

    private void InitializeAudioStudio()
    {
        CancelAudioSeparation();
        AudioEditorGrid.LoopSelectionToggle.IsChecked = false;
        AudioEditorGrid.SeparateAudioBtn.IsEnabled = CreateSeparationService().IsAvailable;
        AudioEditorGrid.SeparationStatusText.Text = CreateSeparationService().IsAvailable
            ? "Ready · CPU · MP3"
            : "Local AI files are missing. Use the Audio Studio build with its Resources folder.";
        AudioEditorGrid.SeparationProgressPanel.Visibility = Visibility.Collapsed;
        AudioEditorGrid.SeparationResultsPanel.Visibility = _lastStemResult == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateAudioStudioDetails();
        SetSeparationBusy(false);
        AudioOptions_Changed(this, new RoutedEventArgs());
        _ = DetectAudioGpuAsync();
    }

    private void UpdateAudioStudioDetails()
    {
        if (_audioReader == null) return;
        var labels = new[] { AudioEditorGrid.Timeline0, AudioEditorGrid.Timeline1, AudioEditorGrid.Timeline2,
            AudioEditorGrid.Timeline3, AudioEditorGrid.Timeline4 };
        for (int i = 0; i < labels.Length; i++)
        {
            var time = TimeSpan.FromTicks(_audioReader.TotalTime.Ticks * i / 4);
            labels[i].Text = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss\.f");
        }
        var duration = AudioEditorGrid.SeparateWholeFile.IsChecked == true ? _audioReader.TotalTime : _selectionEnd - _selectionStart;
        string format = SelectedSeparationFormat == AudioSeparationFormat.Mp3 ? "MP3 192 kbps" : "WAV uncompressed";
        string estimated = FormatStemSize(AudioStemSeparationService.EstimateStemBytes(duration, SelectedSeparationFormat));
        AudioEditorGrid.SeparationScopeText.Text = (AudioEditorGrid.SeparateWholeFile.IsChecked == true ? "Full track" : "Selected range") + " · " + FormatAudioTime(duration) + "\n" + format + " · ~" + estimated + " per stem";
    }

    private static string FormatStemSize(long bytes) => bytes >= 1048576
        ? (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : Math.Ceiling(bytes / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";

    private async Task DetectAudioGpuAsync()
    {
        _audioGpuDetectionTask ??= Task.Run(AudioGpuDeviceDetector.Detect);
        _audioGpuDevice = await _audioGpuDetectionTask;
        AudioEditorGrid.ProcessingGpu.ToolTip = !CreateSeparationService().IsGpuEngineAvailable
            ? "GPU engine files are missing. Use the GPU build or choose CPU."
            : _audioGpuDevice.Available ? _audioGpuDevice.Name + " · NVIDIA CUDA" : _audioGpuDevice.Reason;
        SetSeparationBusy(_audioSeparationCts != null);
    }

    internal void AudioOptions_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAudioStudioDetails();
        if (_audioSeparationCts == null && _audioReader != null && CreateSeparationService().IsAvailable)
            AudioEditorGrid.SeparationStatusText.Text = "Ready · " + SelectedSeparationProcessor.ToString().ToUpperInvariant() + " · " + SelectedSeparationFormat.ToString().ToUpperInvariant();
    }

    private void UpdateStemResultDetails()
    {
        if (_lastStemResult == null) return;
        string sizes = File.Exists(_lastStemResult.VocalsPath) && File.Exists(_lastStemResult.MusicPath)
            ? FormatStemSize(new FileInfo(_lastStemResult.VocalsPath).Length) + " + " + FormatStemSize(new FileInfo(_lastStemResult.MusicPath).Length) : "files moved";
        AudioEditorGrid.SeparationResultSummary.Text = _lastStemResult.OutputFormat.ToString().ToUpperInvariant() + " · " + sizes;
        AudioEditorGrid.SeparationResultSummary.ToolTip = _lastStemResult.Folder;
    }

    internal void AudioScope_Changed(object sender, RoutedEventArgs e) => UpdateAudioStudioDetails();

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
        if (_audioSeparationCts != null || _currentAudioFile == null || _audioReader == null) return;
        NormalizeSelection();
        bool fullTrack = AudioEditorGrid.SeparateWholeFile.IsChecked == true;
        TimeSpan start = fullTrack ? TimeSpan.Zero : _selectionStart;
        TimeSpan duration = fullTrack ? _audioReader.TotalTime : _selectionEnd - _selectionStart;
        if (duration <= TimeSpan.Zero)
        {
            AudioEditorGrid.SeparationStatusText.Text = "Select an audio range first.";
            return;
        }
        var picker = new OpenFolderDialog
        {
            Title = "Choose where to save vocals and instrumental",
            InitialDirectory = Path.GetDirectoryName(_currentAudioFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (picker.ShowDialog(this) != true) return;
        StopAudioPlayback(resetToSelectionStart: false);
        var request = new AudioSeparationRequest(_currentAudioFile, picker.FolderName, start, duration)
        { Processor = SelectedSeparationProcessor, OutputFormat = SelectedSeparationFormat };
        await RunAudioSeparationAsync(request);
    }

    private async Task RunAudioSeparationAsync(AudioSeparationRequest request)
    {
        var cts = new CancellationTokenSource();
        _audioSeparationCts = cts;
        SetSeparationBusy(true);
        var progress = new Progress<AudioSeparationProgress>(p =>
        {
            if (!ReferenceEquals(_audioSeparationCts, cts)) return;
            AudioEditorGrid.SeparationProgress.Value = p.Percent;
            AudioEditorGrid.SeparationStatusText.Text = p.Message;
        });
        try
        {
            var result = await CreateSeparationService().SeparateAsync(request, progress, cts.Token);
            if (!ReferenceEquals(_audioSeparationCts, cts)) return;
            _lastStemResult = result;
            _lastOutputFolder = result.Folder;
            AudioEditorGrid.SeparationResultsPanel.Visibility = Visibility.Visible;
            AudioEditorGrid.SeparationStatusText.Text = "Saved · " + result.ProcessorUsed.ToString().ToUpperInvariant() + " · " + result.OutputFormat.ToString().ToUpperInvariant();
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_audioSeparationCts, cts))
                AudioEditorGrid.SeparationStatusText.Text = "Cancelled. No incomplete stems were saved.";
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("AudioSeparation", ex.ToString());
            if (ReferenceEquals(_audioSeparationCts, cts))
                AudioEditorGrid.SeparationStatusText.Text = ex is AudioGpuUnavailableException ? ex.Message : ex is UnauthorizedAccessException
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
        bool showResults = !busy && _lastStemResult != null;
        AudioEditorGrid.SeparationIntroText.Visibility = busy || showResults ? Visibility.Collapsed : Visibility.Visible;
        AudioEditorGrid.SeparationPrivacyText.Visibility = showResults ? Visibility.Collapsed : Visibility.Visible;
        AudioEditorGrid.SeparateAudioBtn.IsEnabled = !busy && CreateSeparationService().IsAvailable;
        AudioEditorGrid.SeparateAudioBtn.Content = busy ? "Separating…" : "Separate audio";
        AudioEditorGrid.SeparateWholeFile.IsEnabled = !busy;
        AudioEditorGrid.SeparateSelection.IsEnabled = !busy;
        AudioEditorGrid.ProcessingCpu.IsEnabled = !busy;
        AudioEditorGrid.ProcessingGpu.IsEnabled = !busy && _audioGpuDevice?.Available == true && CreateSeparationService().IsGpuEngineAvailable;
        AudioEditorGrid.OutputMp3.IsEnabled = !busy;
        AudioEditorGrid.OutputWav.IsEnabled = !busy;
        UpdateStemResultDetails();
        AudioEditorGrid.OpenVocalsBtn.IsEnabled = !busy;
        AudioEditorGrid.OpenMusicBtn.IsEnabled = !busy;
        AudioEditorGrid.SeparationProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            AudioEditorGrid.SeparationProgress.Value = 0;
            AudioEditorGrid.SeparationStatusText.Text = "Preparing audio on your device…";
            AudioEditorGrid.SeparationResultsPanel.Visibility = Visibility.Collapsed;
        }
        else if (_lastStemResult != null) AudioEditorGrid.SeparationResultsPanel.Visibility = Visibility.Visible;
    }

    internal void CancelSeparation_Click(object sender, RoutedEventArgs e)
    {
        _audioSeparationCts?.Cancel();
        AudioEditorGrid.SeparationStatusText.Text = "Cancelling…";
    }

    private void CancelAudioSeparation()
    {
        var cts = _audioSeparationCts;
        _audioSeparationCts = null;
        cts?.Cancel();
        SetSeparationBusy(false);
    }

    internal void OpenVocals_Click(object sender, RoutedEventArgs e) => OpenStemInEditor(_lastStemResult?.VocalsPath);
    internal void OpenMusic_Click(object sender, RoutedEventArgs e) => OpenStemInEditor(_lastStemResult?.MusicPath);
    private void OpenStemInEditor(string? path)
    {
        if (path != null && File.Exists(path)) LoadAudioEditor(path);
        else AudioEditorGrid.SeparationStatusText.Text = "This output was moved or deleted.";
    }
    internal void OpenStemsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastStemResult != null && Directory.Exists(_lastStemResult.Folder))
        {
            try { Process.Start(new ProcessStartInfo(_lastStemResult.Folder) { UseShellExecute = true }); }
            catch (Exception ex) { ConsoleLogger.Error("AudioSeparation", ex.ToString()); }
        }
        else AudioEditorGrid.SeparationStatusText.Text = "The output folder was moved or deleted.";
    }
}