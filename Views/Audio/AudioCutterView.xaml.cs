using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CortexFX.Views.Audio;

public partial class AudioCutterView : UserControl
{
    internal MainWindow? Owner { get; set; }
    public AudioCutterView() => InitializeComponent();
    private void CloseAudioEditor_Click(object sender, RoutedEventArgs e) => Owner?.CloseAudioEditor_Click(sender, e);
    private void PlaySelection_Click(object sender, RoutedEventArgs e) => Owner?.PlaySelection_Click(sender, e);
    private void SaveSelection_Click(object sender, RoutedEventArgs e) => Owner?.SaveSelection_Click(sender, e);
    private void SetEnd_Click(object sender, RoutedEventArgs e) => Owner?.SetEnd_Click(sender, e);
    private void SetStart_Click(object sender, RoutedEventArgs e) => Owner?.SetStart_Click(sender, e);
    private void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Owner?.SpectrumCanvas_SizeChanged(sender, e);
    private void StopSelection_Click(object sender, RoutedEventArgs e) => Owner?.StopSelection_Click(sender, e);
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.VolumeSlider_ValueChanged(sender, e);
    private void WaveformContainer_SizeChanged(object sender, SizeChangedEventArgs e) => Owner?.WaveformContainer_SizeChanged(sender, e);
    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Owner?.Waveform_MouseLeftButtonDown(sender, e);
    private void Waveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Owner?.Waveform_MouseLeftButtonUp(sender, e);
    private void Waveform_MouseMove(object sender, MouseEventArgs e) => Owner?.Waveform_MouseMove(sender, e);
}
