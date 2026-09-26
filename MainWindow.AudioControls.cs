using CortexFX.Views.Audio;

namespace CortexFX;

public partial class MainWindow
{
    private System.Windows.Controls.TextBlock AudioEditorFileNameText => AudioEditorGrid.AudioEditorFileNameText;
    private System.Windows.Controls.TextBlock CurrentTimeDisplay => AudioEditorGrid.CurrentTimeDisplay;
    private System.Windows.Controls.Grid WaveformContainer => AudioEditorGrid.WaveformContainer;
    private System.Windows.Shapes.Rectangle SelectionBand => AudioEditorGrid.SelectionBand;
    private System.Windows.Controls.Canvas WaveformCanvas => AudioEditorGrid.WaveformCanvas;
    private System.Windows.Shapes.Rectangle DimLeft => AudioEditorGrid.DimLeft;
    private System.Windows.Shapes.Rectangle DimRight => AudioEditorGrid.DimRight;
    private System.Windows.Shapes.Rectangle StartMarker => AudioEditorGrid.StartMarker;
    private System.Windows.Shapes.Rectangle EndMarker => AudioEditorGrid.EndMarker;
    private System.Windows.Shapes.Line PlaybackCursor => AudioEditorGrid.PlaybackCursor;
    private System.Windows.Controls.TextBlock SpectrumStatusText => AudioEditorGrid.SpectrumStatusText;
    private System.Windows.Controls.Grid SpectrumHost => AudioEditorGrid.SpectrumHost;
    private System.Windows.Controls.Canvas SpectrumCanvas => AudioEditorGrid.SpectrumCanvas;
    private System.Windows.Controls.TextBlock StartTimeDisplay => AudioEditorGrid.StartTimeDisplay;
    private System.Windows.Controls.TextBlock EndTimeDisplay => AudioEditorGrid.EndTimeDisplay;
    private System.Windows.Controls.TextBlock DurationTimeDisplay => AudioEditorGrid.DurationTimeDisplay;
    private System.Windows.Controls.Slider VolumeSlider => AudioEditorGrid.VolumeSlider;
    private System.Windows.Controls.TextBlock VolumePercentText => AudioEditorGrid.VolumePercentText;
    private System.Windows.Controls.Button SetStartBtn => AudioEditorGrid.SetStartBtn;
    private System.Windows.Controls.Button SetEndBtn => AudioEditorGrid.SetEndBtn;
    private System.Windows.Controls.Button PlaySelectionBtn => AudioEditorGrid.PlaySelectionBtn;
    private System.Windows.Controls.Button StopSelectionBtn => AudioEditorGrid.StopSelectionBtn;
    private System.Windows.Controls.Button SaveSelectionBtn => AudioEditorGrid.SaveSelectionBtn;
}
