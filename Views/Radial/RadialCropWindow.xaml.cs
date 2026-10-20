using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CortexFX.Core.Radial;

namespace CortexFX.Views.Radial;

public partial class RadialCropWindow : Window
{
    private readonly string _source;
    private bool _closed, _syncing;
    private Task? _loading;
    public RadialCropOptions? Options { get; private set; }
    internal bool IsAcrylicEnabled { get; private set; }
    public RadialCropWindow(string source)
    {
        _source = source;
        InitializeComponent();
        FileTitle.Text = Path.GetFileName(source); FileTitle.ToolTip = FileTitle.Text;
        CropSurface.SelectionChanged += SynchronizeDimensions;
        Closed += (_, _) => _closed = true;
        double availableHeight = Math.Max(1, SystemParameters.WorkArea.Height - 24);
        double availableWidth = Math.Max(1, SystemParameters.WorkArea.Width - 24);
        MinHeight = Math.Min(MinHeight, availableHeight); Height = Math.Min(Height, availableHeight);
        MinWidth = Math.Min(MinWidth, availableWidth); Width = Math.Min(Width, availableWidth);
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IsAcrylicEnabled = FrostedWindowBackdrop.Apply(this, GlassShell);
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadImageAsync();
    public Task LoadImageAsync() => _loading ??= LoadCoreAsync();
    private async Task LoadCoreAsync()
    {
        try
        {
            var preview = await Task.Run(() => RadialImageTools.LoadPreview(_source));
            if (_closed) return;
            using var stream = new MemoryStream(preview.Png);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            CropSurface.SetImage(bitmap, preview.Width, preview.Height);
            SourceDimensions.Text = $"{preview.Width} × {preview.Height} px";
            RatioPanel.IsEnabled = true; ResetButton.IsEnabled = true; ApplyButton.IsEnabled = true;
        }
        catch (Exception error) { if (!_closed) { SourceDimensions.Text = "Preview unavailable"; ValidationText.Text = error.Message; } }
    }
    private void SynchronizeDimensions()
    {
        if (CropSurface.PixelSelection is not { } crop) return;
        _syncing = true;
        WidthInput.Text = crop.Width.ToString(CultureInfo.CurrentCulture); HeightInput.Text = crop.Height.ToString(CultureInfo.CurrentCulture);
        _syncing = false; ValidationText.Text = "";
    }
    private void Ratio_Checked(object sender, RoutedEventArgs e)
    {
        if (CropSurface?.PixelSelection is null || sender is not RadioButton button) return;
        double ratio = double.Parse(button.Tag.ToString()!, CultureInfo.InvariantCulture);
        CropSurface.SetAspectRatio(ratio > 0 ? ratio : null);
    }
    private bool CommitDimensions(bool widthEdited)
    {
        if (_syncing || CropSurface.PixelSelection is not { } current) return false;
        if (!int.TryParse(WidthInput.Text, out int width) || !int.TryParse(HeightInput.Text, out int height) ||
            width < 1 || width > current.SourceWidth || height < 1 || height > current.SourceHeight)
        { ValidationText.Text = $"Enter width 1–{current.SourceWidth} and height 1–{current.SourceHeight}."; return false; }
        double w = width, h = height;
        if (CropSurface.AspectRatio is double ratio)
        {
            if (widthEdited) h = w / ratio; else w = h * ratio;
            double scale = Math.Min(1, Math.Min(current.SourceWidth / w, current.SourceHeight / h)); w *= scale; h *= scale;
        }
        var region = CropSurface.Region;
        CropSurface.SetRegion(new(region.X + (region.Width - w) / 2, region.Y + (region.Height - h) / 2, w, h));
        return true;
    }
    private void Dimension_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitDimensions(ReferenceEquals(sender, WidthInput));
    private void Dimension_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitDimensions(ReferenceEquals(sender, WidthInput)); e.Handled = true; } }
    private void Reset_Click(object sender, RoutedEventArgs e) { FreeRatio.IsChecked = true; CropSurface.Reset(); }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitDimensions(!ReferenceEquals(Keyboard.FocusedElement, HeightInput))) return;
        Options = CropSurface.PixelSelection; DialogResult = true;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
}
