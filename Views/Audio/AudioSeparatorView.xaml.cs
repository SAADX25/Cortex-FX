using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using CortexFX.Core.Audio;

namespace CortexFX.Views.Audio;

public partial class AudioSeparatorView : UserControl
{
    internal MainWindow? Owner { get; set; }
    private double[] _peaks = [];
    private bool _updatingPlaybackPosition;
    private bool _draggingPlayhead;
    private TimeSpan _sourceDuration;
    internal event Action<TimeSpan>? SeekRequested;
    internal event Action<double>? VolumeRequested;
    internal event Action? ToggleMuteRequested;
    private bool _updatingPreviewVolume;
    public AudioSeparatorView() => InitializeComponent();
    internal void SetPreviewVolume(double percent, bool muted)
    {
        _updatingPreviewVolume = true;
        try
        {
            percent = Math.Clamp(percent, 0, 100);
            PreviewVolume.Value = percent;
            PreviewVolumeText.Text = muted ? "Muted" : $"{percent:0}%";
            PreviewVolumeIcon.Text = muted || percent <= 0 ? "\uE74F" : "\uE767";
            PreviewMuteBtn.ToolTip = percent <= 0 ? "Restore preview sound" : muted ? "Unmute preview sound" : "Mute preview sound";
        }
        finally { _updatingPreviewVolume = false; }
    }
    private void PreviewVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingPreviewVolume) VolumeRequested?.Invoke(Math.Clamp(e.NewValue, 0, 100));
    }
    private void PreviewMute_Click(object sender, RoutedEventArgs e) => ToggleMuteRequested?.Invoke();
    internal void UpdateDownloadProgress(AudioAiDownloadProgress update)
    {
        bool known = update.TotalBytes is > 0;
        AiDownloadProgress.IsIndeterminate = !known;
        AiDownloadProgress.Value = known ? Math.Clamp(update.ReceivedBytes * 100.0 / update.TotalBytes!.Value, 0, 100) : 0;
        AiDownloadProgressText.Text = update.Message;
        if (known)
            AiDownloadProgressText.Text = $"Current file · {AiDownloadProgress.Value:0}% · {update.ReceivedBytes / 1048576.0:0.0} / {update.TotalBytes!.Value / 1048576.0:0.0} MB";
        else if (update.ReceivedBytes > 0)
            AiDownloadProgressText.Text += $" · {update.ReceivedBytes / 1048576.0:0.0} MB";
    }
    private void ChooseAudio_Click(object sender, RoutedEventArgs e) => Owner?.ChooseSeparationAudio();
    private void Preview_Click(object sender, RoutedEventArgs e) => Owner?.PreviewSeparationAudio();
    private void Primary_Click(object sender, RoutedEventArgs e) => Owner?.SeparatorPrimary_Click(sender, e);
    private void Options_Changed(object sender, RoutedEventArgs e) => Owner?.AudioOptions_Changed(sender, e);
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => Owner?.CancelAudioAiDownload();
    private void CancelSeparation_Click(object sender, RoutedEventArgs e) => Owner?.CancelSeparation_Click(sender, e);
    private void OpenVocals_Click(object sender, RoutedEventArgs e) => Owner?.OpenVocals_Click(sender, e);
    private void OpenMusic_Click(object sender, RoutedEventArgs e) => Owner?.OpenMusic_Click(sender, e);
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Owner?.OpenStemsFolder_Click(sender, e);
    private void Audio_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            Owner?.ChooseSeparationAudio(files[0]);
        e.Handled = true;
    }
    internal void SetPlaybackPosition(TimeSpan position, TimeSpan duration)
    {
        _sourceDuration = duration;
        if (_draggingPlayhead) return;
        _updatingPlaybackPosition = true;
        try
        {
            SourceSeekSlider.Maximum = Math.Max(0.001, duration.TotalSeconds);
            SourceSeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, Math.Max(0, duration.TotalSeconds));
            SourceSeekSlider.Visibility = duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
            SetSourceTimeLabel(TimeSpan.FromSeconds(SourceSeekSlider.Value));
            UpdatePlayedBand();
        }
        finally { _updatingPlaybackPosition = false; }
    }
    private void UpdatePlayedBand() => SourcePlayedBand.Width = Math.Max(0, SourceWaveform.ActualWidth *
        SourceSeekSlider.Value / SourceSeekSlider.Maximum);
    private void SourceSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SourcePlayedBand == null) return;
        UpdatePlayedBand();
        if (_updatingPlaybackPosition || _sourceDuration <= TimeSpan.Zero) return;
        var position = TimeSpan.FromSeconds(Math.Clamp(e.NewValue, 0, _sourceDuration.TotalSeconds));
        SetSourceTimeLabel(position);
        if (!_draggingPlayhead) SeekRequested?.Invoke(position);
    }
    private void SetSourceTimeLabel(TimeSpan position)
    {
        if (SourcePosition != null)
            SourcePosition.Text = position.TotalHours >= 1 ? position.ToString(@"h\:mm\:ss\.fff") : position.ToString(@"mm\:ss\.fff");
    }
    private void SourceSeek_DragStarted(object sender, DragStartedEventArgs e) => _draggingPlayhead = true;
    private void SourceSeek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _draggingPlayhead = false;
        if (!e.Canceled && _sourceDuration > TimeSpan.Zero)
            SeekRequested?.Invoke(TimeSpan.FromSeconds(Math.Clamp(SourceSeekSlider.Value, 0, _sourceDuration.TotalSeconds)));
    }
    internal void SetWaveform(double[] peaks)
    {
        _peaks = peaks;
        if (peaks.Length == 0) SetPlaybackPosition(TimeSpan.Zero, TimeSpan.Zero);
        DrawWaveform();
    }
    private void Waveform_SizeChanged(object sender, SizeChangedEventArgs e) => DrawWaveform();
    private void DrawWaveform()
    {
        if (SourceWaveform == null) return;
        SourceWaveform.Children.Clear();
        UpdatePlayedBand();
        WaveformPlaceholder.Visibility = _peaks.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        int count = Math.Min(100, _peaks.Length);
        double step = SourceWaveform.ActualWidth / Math.Max(1, count);
        for (int i = 0; i < count; i++)
        {
            double availableHeight = Math.Max(3, SourceWaveform.ActualHeight);
            double height = Math.Max(3, _peaks[i * _peaks.Length / count] * availableHeight * 0.9);
            var bar = new Rectangle { Width = Math.Max(1, step - 2), Height = height, RadiusX = 1.5, RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromRgb(180, 125, 244)) };
            Canvas.SetLeft(bar, i * step);
            Canvas.SetTop(bar, (availableHeight - height) / 2);
            SourceWaveform.Children.Add(bar);
        }
    }
}
