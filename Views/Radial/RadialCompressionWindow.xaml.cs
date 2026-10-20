using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CortexFX.Core.Radial;

namespace CortexFX.Views.Radial;

public partial class RadialCompressionWindow : Window
{
    private readonly RadialSelection _selection;
    public RadialCompressionOptions? Options { get; private set; }
    internal bool IsAcrylicEnabled { get; private set; }
    public RadialCompressionWindow(RadialSelection selection)
    {
        _selection = selection;
        InitializeComponent();
        FileTitle.Text = selection.Title; FileTitle.ToolTip = selection.Title;
        OriginalSize.Text = RadialTargetCompression.Size(selection.Bytes);
        OriginalCaption.Text = selection.Paths.Count > 1 ? "Combined size" : "Original size";
        SelectionHint.Text = selection.Paths.Count > 1 ? "Each file is compressed separately" :
            System.IO.Path.GetExtension(selection.Paths[0]).TrimStart('.').ToUpperInvariant() + " file";
        PdfOptions.Visibility = selection.Paths.All(path => System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
        if (PdfOptions.Visibility == Visibility.Visible) Height = 700;
        double availableHeight = Math.Max(1, SystemParameters.WorkArea.Height - 24);
        double availableWidth = Math.Max(1, SystemParameters.WorkArea.Width - 24);
        MinHeight = Math.Min(MinHeight, availableHeight); Height = Math.Min(Height, availableHeight);
        MinWidth = Math.Min(MinWidth, availableWidth); Width = Math.Min(Width, availableWidth);
        Refresh();
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IsAcrylicEnabled = FrostedWindowBackdrop.Apply(this, GlassShell);
    }
    private RadialCompressionOptions ReadOptions()
    {
        long maximum = 512 * 1024;
        if (MaximumMode.IsChecked == true)
        {
            if ((!double.TryParse(SizeValue.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
                 !double.TryParse(SizeValue.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) || !double.IsFinite(value) || value <= 0)
                throw new ArgumentException("Enter a positive size.");
            double bytes = value * (SizeUnit.SelectedIndex == 1 ? 1048576d : 1024d);
            if (bytes < 1024 || bytes > long.MaxValue / 2d) throw new ArgumentException("Choose a maximum size of at least 1 KB.");
            maximum = (long)bytes;
        }
        return new(MaximumMode.IsChecked == true ? CompressionTargetMode.MaximumSize : CompressionTargetMode.Percentage,
            ReductionSlider.Value, maximum, RasterizePdf.IsChecked == true);
    }
    private void Refresh()
    {
        if (!IsInitialized || _selection is null) return;
        bool percentage = PercentageMode.IsChecked == true;
        PercentagePanel.Visibility = percentage ? Visibility.Visible : Visibility.Collapsed;
        SizePanel.Visibility = percentage ? Visibility.Collapsed : Visibility.Visible;
        PercentText.Text = $"{ReductionSlider.Value:0}%";
        PdfHint.Text = RasterizePdf.IsChecked == true
            ? "Pages become flat images. Selectable text, links, forms and layers are removed."
            : "Preserves text and interactive content. Smaller targets may require rasterizing scanned pages.";
        try
        {
            var options = ReadOptions(); long limit = options.TargetBytes(_selection.Bytes);
            TargetText.Text = $"At most {RadialTargetCompression.Size(limit)}" + (_selection.Paths.Count > 1 && !percentage ? " per file" : "");
            TargetHint.Text = _selection.Paths.Count > 1 && percentage ? "Each file is reduced by this percentage. The limit above is the combined total."
                : "A new copy is saved only if it fits this limit. Increase the limit if needed.";
            ValidationText.Text = ""; ValidationText.Visibility = Visibility.Collapsed; ApplyButton.IsEnabled = true;
        }
        catch (ArgumentException error)
        {
            TargetText.Text = "Check your settings";
            TargetHint.Text = "Enter a valid compression target to continue.";
            ValidationText.Text = error.Message; ValidationText.Visibility = Visibility.Visible; ApplyButton.IsEnabled = false;
        }
    }
    private void Mode_Checked(object sender, RoutedEventArgs e) => Refresh();
    private void Options_Changed(object sender, SelectionChangedEventArgs e) => Refresh();
    private void Reduction_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Refresh();
    private void Size_Changed(object sender, TextChangedEventArgs e) => Refresh();
    private void Rasterize_Changed(object sender, RoutedEventArgs e) => Refresh();
    private void Apply_Click(object sender, RoutedEventArgs e) { Options = ReadOptions(); DialogResult = true; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
}
