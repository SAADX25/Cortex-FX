using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CortexFX.Views.Audio;

public partial class AudioCutterView : UserControl
{
    internal MainWindow? Owner { get; set; }
    public AudioCutterView() => InitializeComponent();
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e) => Owner?.AudioEditor_PreviewKeyDown(sender, e);
    private void AudioOptions_Changed(object sender, RoutedEventArgs e) => Owner?.AudioOptions_Changed(sender, e);
    private void AudioScope_Changed(object sender, RoutedEventArgs e) => Owner?.AudioScope_Changed(sender, e);
    private void SelectAll_Click(object sender, RoutedEventArgs e) => Owner?.SelectAllAudio_Click(sender, e);
    private void SeparateAudio_Click(object sender, RoutedEventArgs e) => Owner?.SeparateAudio_Click(sender, e);
    private void CancelSeparation_Click(object sender, RoutedEventArgs e) => Owner?.CancelSeparation_Click(sender, e);
    private void OpenVocals_Click(object sender, RoutedEventArgs e) => Owner?.OpenVocals_Click(sender, e);
    private void OpenMusic_Click(object sender, RoutedEventArgs e) => Owner?.OpenMusic_Click(sender, e);
    private void OpenStemsFolder_Click(object sender, RoutedEventArgs e) => Owner?.OpenStemsFolder_Click(sender, e);
    private void PlaySelection_Click(object sender, RoutedEventArgs e) => Owner?.PlaySelection_Click(sender, e);
    private void PreviewSound_Click(object sender, RoutedEventArgs e) => Owner?.PreviewSound_Click(sender, e);
    private void AudioGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.AudioGainSlider_ValueChanged(sender, e);
    private void SaveSelection_Click(object sender, RoutedEventArgs e) => Owner?.SaveSelection_Click(sender, e);
    private void SetEnd_Click(object sender, RoutedEventArgs e) => Owner?.SetEnd_Click(sender, e);
    private void SetStart_Click(object sender, RoutedEventArgs e) => Owner?.SetStart_Click(sender, e);
    private void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Owner?.SpectrumCanvas_SizeChanged(sender, e);
    private void WaveformContainer_SizeChanged(object sender, SizeChangedEventArgs e) => Owner?.WaveformContainer_SizeChanged(sender, e);
    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Owner?.Waveform_MouseLeftButtonDown(sender, e);
    private void Waveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Owner?.Waveform_MouseLeftButtonUp(sender, e);
    private void Waveform_MouseMove(object sender, MouseEventArgs e) => Owner?.Waveform_MouseMove(sender, e);
}
