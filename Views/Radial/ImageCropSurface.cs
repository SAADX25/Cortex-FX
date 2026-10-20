using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CortexFX.Core.Radial;

namespace CortexFX.Views.Radial;

public sealed class ImageCropSurface : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush),
        typeof(ImageCropSurface), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    private BitmapSource? _image;
    private int _width, _height;
    private CropRectangle _crop, _start;
    private Point _anchor;
    private CropHandle _handle;
    private bool _dragging, _newSelection;
    public double? AspectRatio { get; private set; }
    public event Action? SelectionChanged;
    public CropRectangle Region => _crop;
    public RadialCropOptions? PixelSelection => _image is null ? null : CropGeometry.ToPixels(_crop, _width, _height);
    public Rect ImageBounds
    {
        get
        {
            if (_image is null) return Rect.Empty;
            double scale = Math.Max(.0001, Math.Min(Math.Max(1, ActualWidth - 32) / _width, Math.Max(1, ActualHeight - 32) / _height));
            return new((ActualWidth - _width * scale) / 2, (ActualHeight - _height * scale) / 2, _width * scale, _height * scale);
        }
    }
    public ImageCropSurface() { Focusable = true; ClipToBounds = true; MinHeight = 150; Cursor = Cursors.Cross; }
    public void SetImage(BitmapSource image, int width, int height)
    {
        _image = image; _width = width; _height = height; Reset();
    }
    public void Reset() { if (_image is null) return; AspectRatio = null; SetRegion(new(0, 0, _width, _height)); }
    public void SetAspectRatio(double? ratio) { if (_image is null) return; AspectRatio = ratio; SetRegion(CropGeometry.Fit(_width, _height, ratio, _crop)); }
    public void SetRegion(CropRectangle region)
    {
        _crop = CropGeometry.Clamp(region, _width, _height); InvalidateVisual();
        if (PixelSelection is { } pixels) AutomationProperties.SetName(this, $"Crop selection: {pixels.Width} by {pixels.Height} pixels. Arrow keys move the selection; Shift moves ten pixels.");
        SelectionChanged?.Invoke();
    }
    private Rect DisplayCrop()
    {
        Rect image = ImageBounds; double scale = image.Width / _width;
        return new(image.X + _crop.X * scale, image.Y + _crop.Y * scale, _crop.Width * scale, _crop.Height * scale);
    }
    private Point PixelPoint(Point point)
    {
        Rect image = ImageBounds; double scale = image.Width / _width;
        return new(Math.Clamp((point.X - image.X) / scale, 0, _width), Math.Clamp((point.Y - image.Y) / scale, 0, _height));
    }
    private static (Point Point, CropHandle Handle)[] Handles(Rect r) =>
    [ (r.TopLeft, CropHandle.TopLeft), (new(r.Left + r.Width / 2, r.Top), CropHandle.Top), (r.TopRight, CropHandle.TopRight),
      (new(r.Right, r.Top + r.Height / 2), CropHandle.Right), (r.BottomRight, CropHandle.BottomRight),
      (new(r.Left + r.Width / 2, r.Bottom), CropHandle.Bottom), (r.BottomLeft, CropHandle.BottomLeft), (new(r.Left, r.Top + r.Height / 2), CropHandle.Left) ];
    private CropHandle? Hit(Point point)
    {
        var rect = DisplayCrop();
        foreach (var handle in Handles(rect)) if (Math.Abs(point.X - handle.Point.X) <= 9 && Math.Abs(point.Y - handle.Point.Y) <= 9) return handle.Handle;
        return rect.Contains(point) ? CropHandle.Move : null;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_image is null || (!ImageBounds.Contains(e.GetPosition(this)) && Hit(e.GetPosition(this)) is null)) return;
        Focus(); _anchor = PixelPoint(e.GetPosition(this)); _start = _crop;
        var hit = Hit(e.GetPosition(this)); _newSelection = hit is null; _handle = hit ?? CropHandle.BottomRight;
        _dragging = CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (_image is null) return;
        if (!_dragging)
        {
            Cursor = Hit(e.GetPosition(this)) switch
            {
                CropHandle.Move => Cursors.SizeAll, CropHandle.Top or CropHandle.Bottom => Cursors.SizeNS,
                CropHandle.Left or CropHandle.Right => Cursors.SizeWE, CropHandle.TopLeft or CropHandle.BottomRight => Cursors.SizeNWSE,
                CropHandle.TopRight or CropHandle.BottomLeft => Cursors.SizeNESW, _ => Cursors.Cross
            }; return;
        }
        Point point = PixelPoint(e.GetPosition(this));
        if (_newSelection)
        {
            double width = Math.Max(1, Math.Abs(point.X - _anchor.X)), height = Math.Max(1, Math.Abs(point.Y - _anchor.Y));
            if (AspectRatio is double ratio) { width = Math.Min(width, height * ratio); height = width / ratio; }
            SetRegion(new(point.X < _anchor.X ? _anchor.X - width : _anchor.X, point.Y < _anchor.Y ? _anchor.Y - height : _anchor.Y, width, height));
        }
        else SetRegion(CropGeometry.Resize(_start, _handle, point.X - _anchor.X, point.Y - _anchor.Y, _width, _height, AspectRatio));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (_dragging) { _dragging = false; ReleaseMouseCapture(); e.Handled = true; } base.OnMouseLeftButtonUp(e); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { _dragging = false; base.OnLostMouseCapture(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (_image is null) return;
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        double x = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, y = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        if (x == 0 && y == 0) return;
        SetRegion(CropGeometry.Resize(_crop, CropHandle.Move, x, y, _width, _height)); e.Handled = true;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (_image is null) return;
        Rect bounds = ImageBounds, crop = DisplayCrop();
        dc.PushClip(new RectangleGeometry(bounds));
        for (double y = bounds.Top; y < bounds.Bottom; y += 14)
            for (double x = bounds.Left; x < bounds.Right; x += 14)
                dc.DrawRectangle(new SolidColorBrush(((int)((x - bounds.Left) / 14) + (int)((y - bounds.Top) / 14)) % 2 == 0 ? Color.FromRgb(40, 45, 54) : Color.FromRgb(29, 34, 43)), null, new Rect(x, y, 14, 14));
        dc.DrawImage(_image, bounds);
        var shade = new SolidColorBrush(Color.FromArgb(165, 4, 8, 15));
        dc.DrawRectangle(shade, null, new Rect(bounds.Left, bounds.Top, bounds.Width, Math.Max(0, crop.Top - bounds.Top)));
        dc.DrawRectangle(shade, null, new Rect(bounds.Left, crop.Bottom, bounds.Width, Math.Max(0, bounds.Bottom - crop.Bottom)));
        dc.DrawRectangle(shade, null, new Rect(bounds.Left, crop.Top, Math.Max(0, crop.Left - bounds.Left), crop.Height));
        dc.DrawRectangle(shade, null, new Rect(crop.Right, crop.Top, Math.Max(0, bounds.Right - crop.Right), crop.Height));
        var thirds = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
        for (int i = 1; i <= 2; i++)
        {
            dc.DrawLine(thirds, new(crop.Left + crop.Width * i / 3, crop.Top), new(crop.Left + crop.Width * i / 3, crop.Bottom));
            dc.DrawLine(thirds, new(crop.Left, crop.Top + crop.Height * i / 3), new(crop.Right, crop.Top + crop.Height * i / 3));
        }
        dc.Pop();
        var accent = new SolidColorBrush(Color.FromRgb(247, 78, 111));
        dc.DrawRectangle(null, new Pen(accent, 2), crop);
        foreach (var handle in Handles(crop)) dc.DrawRoundedRectangle(Brushes.White, new Pen(accent, 2), new Rect(handle.Point.X - 4, handle.Point.Y - 4, 8, 8), 2, 2);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
