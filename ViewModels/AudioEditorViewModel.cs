using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;
using NAudio.Wave;

namespace CortexFX.ViewModels;

/// <summary>
/// ViewModel for the audio trim/cut editor.
/// Absorbs ~300 lines of audio logic from MainWindow.xaml.cs:
///   - Audio file loading (NAudio)
///   - Playback control (play/pause/seek)
///   - Selection (start/end markers)
///   - Save trimmed selection via FFmpeg
/// </summary>
public partial class AudioEditorViewModel : ObservableObject
{
    private readonly IFFmpegService _ffmpeg;
    private readonly IAppConfiguration _config;
    private AudioFileReader? _audioReader;
    private WaveOutEvent? _waveOut;

    public AudioEditorViewModel(IFFmpegService ffmpeg, IAppConfiguration config)
    {
        _ffmpeg = ffmpeg;
        _config = config;
    }

    // ------------------------------------------------------------------
    // Observable State
    // ------------------------------------------------------------------

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _audioFileName = string.Empty;

    [ObservableProperty]
    private string? _currentFilePath;

    [ObservableProperty]
    private TimeSpan _selectionStart;

    [ObservableProperty]
    private TimeSpan _selectionEnd;

    [ObservableProperty]
    private TimeSpan _totalDuration;

    [ObservableProperty]
    private TimeSpan _currentPosition;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private string _timeDisplayText = "Start: 00:00.000  |  End: 00:00.000  |  Duration: 00:00.000";

    [ObservableProperty]
    private bool _isPlaying;

    /// <summary>
    /// Pending audio file path (set when the AudioChoiceOverlay is shown).
    /// If the user chooses "Trim", this gets loaded into the editor.
    /// </summary>
    [ObservableProperty]
    private string? _pendingAudioFile;

    [ObservableProperty]
    private bool _showAudioChoiceOverlay;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>Load an audio file into the editor.</summary>
    [RelayCommand]
    private void LoadAudio(string filePath)
    {
        try
        {
            Close();

            CurrentFilePath = filePath;
            AudioFileName = Path.GetFileName(filePath);

            _audioReader = new AudioFileReader(filePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioReader);

            TotalDuration = _audioReader.TotalTime;
            SelectionStart = TimeSpan.Zero;
            SelectionEnd = _audioReader.TotalTime;
            Volume = 1.0;
            IsVisible = true;

            UpdateTimeDisplay();
        }
        catch (Exception ex)
        {
            Close();
            throw new InvalidOperationException($"Error loading audio: {ex.Message}", ex);
        }
    }

    /// <summary>Close the editor and release all resources.</summary>
    [RelayCommand]
    public void Close()
    {
        IsPlaying = false;

        if (_waveOut != null)
        {
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }

        if (_audioReader != null)
        {
            _audioReader.Dispose();
            _audioReader = null;
        }

        IsVisible = false;
        CurrentFilePath = null;
        AudioFileName = string.Empty;
    }

    // ------------------------------------------------------------------
    // Playback Controls
    // ------------------------------------------------------------------

    [RelayCommand]
    private void PlaySelection()
    {
        if (_waveOut == null || _audioReader == null) return;

        _audioReader.CurrentTime = SelectionStart;
        _waveOut.Play();
        IsPlaying = true;
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (_waveOut == null) return;

        if (IsPlaying)
        {
            _waveOut.Pause();
            IsPlaying = false;
        }
        else
        {
            _waveOut.Play();
            IsPlaying = true;
        }
    }

    /// <summary>Called by the UI timer to update the playback cursor position.</summary>
    public void UpdatePlaybackTick()
    {
        if (_audioReader == null) return;

        CurrentPosition = _audioReader.CurrentTime;

        // Auto-stop at selection end
        if (IsPlaying && SelectionEnd > TimeSpan.Zero && _audioReader.CurrentTime >= SelectionEnd)
        {
            _waveOut?.Pause();
            _audioReader.CurrentTime = SelectionEnd;
            IsPlaying = false;
        }
    }

    /// <summary>Seek to a specific position (from waveform click).</summary>
    public void SeekTo(double progressRatio)
    {
        if (_audioReader == null) return;

        progressRatio = Math.Clamp(progressRatio, 0, 1);
        _audioReader.CurrentTime = TimeSpan.FromSeconds(progressRatio * TotalDuration.TotalSeconds);
        CurrentPosition = _audioReader.CurrentTime;
    }

    // ------------------------------------------------------------------
    // Selection
    // ------------------------------------------------------------------

    [RelayCommand]
    private void SetStartMarker()
    {
        if (_audioReader == null) return;
        SelectionStart = _audioReader.CurrentTime;
        if (SelectionStart > SelectionEnd) SelectionEnd = TotalDuration;
        UpdateTimeDisplay();
    }

    [RelayCommand]
    private void SetEndMarker()
    {
        if (_audioReader == null) return;
        SelectionEnd = _audioReader.CurrentTime;
        if (SelectionEnd < SelectionStart) SelectionStart = TimeSpan.Zero;
        UpdateTimeDisplay();
    }

    // ------------------------------------------------------------------
    // Save
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task SaveSelectionAsync(string outputPath)
    {
        if (CurrentFilePath == null) return;

        // Pause during save
        if (IsPlaying)
        {
            _waveOut?.Pause();
            IsPlaying = false;
        }

        await _ffmpeg.TrimAudioAsync(CurrentFilePath, outputPath, SelectionStart, SelectionEnd);
    }

    // ------------------------------------------------------------------
    // Audio Choice Overlay (Trim vs Convert)
    // ------------------------------------------------------------------

    /// <summary>Show the choice overlay for an audio file.</summary>
    public void ShowChoiceFor(string filePath)
    {
        PendingAudioFile = filePath;
        ShowAudioChoiceOverlay = true;
    }

    [RelayCommand]
    private void ChooseTrim()
    {
        ShowAudioChoiceOverlay = false;
        if (!string.IsNullOrEmpty(PendingAudioFile))
        {
            LoadAudio(PendingAudioFile);
            PendingAudioFile = null;
        }
    }

    [RelayCommand]
    private void ChooseConvert()
    {
        ShowAudioChoiceOverlay = false;
        // The parent will handle adding to conversion queue via _pendingAudioFile
    }

    [RelayCommand]
    private void CancelChoice()
    {
        ShowAudioChoiceOverlay = false;
        PendingAudioFile = null;
    }

    // ------------------------------------------------------------------
    // Volume
    // ------------------------------------------------------------------

    partial void OnVolumeChanged(double value)
    {
        if (_audioReader != null)
        {
            _audioReader.Volume = (float)value;
        }
    }

    // ------------------------------------------------------------------
    // Private
    // ------------------------------------------------------------------

    private void UpdateTimeDisplay()
    {
        var duration = SelectionEnd - SelectionStart;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        TimeDisplayText = $"Start: {SelectionStart:mm\\:ss\\.fff}  |  End: {SelectionEnd:mm\\:ss\\.fff}  |  Duration: {duration:mm\\:ss\\.fff}";
    }
}
