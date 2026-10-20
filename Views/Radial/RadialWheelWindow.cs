using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CortexFX.Core.Radial;

namespace CortexFX.Views.Radial;

public sealed class RadialWheelWindow : Window, IDisposable
{
    private readonly RadialActionCatalog _catalog;
    private readonly RadialSettings _settings;
    private readonly RadialSurface _surface = new();
    private readonly DispatcherTimer _ticker;
    private readonly DispatcherTimer _dismiss;
    private bool _disposed;
    private int _generation;
    public bool Busy { get; private set; }
    public RadialMode Mode { get; private set; }
    public RadialSelection? Selection { get; private set; }
    public event Action<RadialAction, RadialSelection>? ActionInvoked;
    public event Action? CancelRequested;

    public RadialWheelWindow(RadialActionCatalog catalog, RadialSettings settings)
    {
        _catalog = catalog; _settings = settings;
        Title = "Cortex FX · Radial wheel";
        Width = Height = 540;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FocusVisualStyle = null;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowDrop = true;
        Content = _surface;
        _surface.ActionRequested += Invoke;
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _ticker.Tick += (_, _) => _surface.Tick();
        _dismiss = new DispatcherTimer();
        _dismiss.Tick += (_, _) => Dismiss();
        DragEnter += ReceiveDrag;
        DragOver += ReceiveDragOver;
        DragLeave += (_, _) => _surface.Hover = -1;
        Drop += ReceiveDrop;
        _surface.MouseMove += (_, e) => { if (!Busy) _surface.Hover = Hit(e.GetPosition(_surface)); };
        _surface.MouseLeftButtonUp += (_, e) => { if (!Busy && _surface.State is "idle" or "unavailable") Invoke(Hit(e.GetPosition(_surface))); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { if (Busy) CancelRequested?.Invoke(); else Dismiss(); e.Handled = true; }
            else if (!Busy && _surface.Actions.Count > 0 && e.Key is Key.Left or Key.Right or Key.Tab)
            {
                _surface.Hover = (_surface.Hover + (e.Key == Key.Left ? -1 : 1) + _surface.Actions.Count) % _surface.Actions.Count;
                e.Handled = true;
            }
            else if (!Busy && e.Key == Key.Enter) { Invoke(_surface.Hover); e.Handled = true; }
        };
        Closing += (_, e) => { if (!_disposed) { e.Cancel = true; if (Busy) CancelRequested?.Invoke(); else Dismiss(); } };
    }

    public void Open(RadialMode mode, ScreenPoint point, IEnumerable<string>? paths = null)
    {
        if (Busy || _disposed) return;
        bool previouslyVisible = IsVisible;
        _dismiss.Stop();
        _generation++;
        Mode = mode;
        _surface.State = "idle";
        _surface.ActiveActionLabel = "";
        _surface.ToolTip = null;
        _surface.Caption = mode switch { RadialMode.Convert => "FORMAT CONVERSION", RadialMode.Tools => "ADVANCED TOOLS", _ => "QUICK ACTIONS" };
        if (paths is not null) { if (!LoadPaths(paths)) return; }
        else if (previouslyVisible && Selection is not null) { if (!Route()) return; }
        else { Selection = null; _surface.Selection = null; _surface.SetActions([]); _surface.Icon = null; _surface.Message = "Hold a file over the wheel"; }
        _surface.Hover = -1;
        Show();
        NativeWheel.CenterWindow(this, point);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (IsVisible) NativeWheel.CenterWindow(this, point); });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        _surface.BeginAnimation(RadialSurface.RevealProperty, new DoubleAnimation(.88, 1, TimeSpan.FromMilliseconds(170))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _ticker.Start();
        if (paths is not null) { Activate(); _surface.Focus(); }
    }

    private bool LoadPaths(IEnumerable<string> paths)
    {
        if (Busy || _disposed) return false;
        try
        {
            Selection = RadialSelection.FromPaths(paths);
            _surface.Selection = Selection;
            // Validate the paths and available actions before asking the Windows shell for an icon.
            if (!Route()) return false;
            _dismiss.Stop();
            _generation++;
            _surface.State = "idle";
            _surface.ActiveActionLabel = "";
            _surface.ToolTip = null;
            _surface.Icon = LoadIcon(Selection!.Paths[0]);
            return true;
        }
        catch (Exception error) when (IsInvalidInput(error)) { return RejectInput(); }
    }
    private bool Route()
    {
        var actions = _catalog.GetActions(Selection!, Mode, _settings).Where(action => action.Enabled).ToArray();
        if (actions.Length == 0) return RejectInput();
        _surface.SetActions(actions);
        _surface.Message = "Move to a segment · release to run";
        return true;
    }
    private bool RejectInput()
    {
        _generation++;
        Selection = null;
        _surface.Selection = null;
        _surface.Icon = null;
        _surface.SetActions([]);
        _surface.State = "idle";
        _surface.ActiveActionLabel = "";
        _surface.ToolTip = null;
        _surface.Message = "Hold a file over the wheel";
        Dismiss();
        return false;
    }
    private static bool IsInvalidInput(Exception error) => error is IOException or ArgumentException or
        UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or
        System.Security.SecurityException;
    private bool LoadDragData(IDataObject data, bool reuseSelection = false)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, false) || data.GetData(DataFormats.FileDrop, false) is not string[] paths)
                return RejectInput();
            // DragOver is frequent. Reuse an accepted selection, but revalidate it when dropping.
            if (reuseSelection && _surface.State == "idle" && Selection is not null &&
                paths.SequenceEqual(Selection.Paths, StringComparer.OrdinalIgnoreCase)) return true;
            return LoadPaths(paths);
        }
        catch (Exception error) when (IsInvalidInput(error)) { return RejectInput(); }
    }
    private int Hit(Point point) => RadialGeometry.Hit(point.X, point.Y, _surface.Actions.Count);
    private void ReceiveDrag(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (Busy || !LoadDragData(e.Data)) return;
        e.Effects = DragDropEffects.Copy;
    }
    private void ReceiveDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (Busy || !LoadDragData(e.Data, reuseSelection: true)) return;
        _surface.Hover = Hit(e.GetPosition(_surface));
        if (_surface.Hover >= 0 && _surface.Actions[_surface.Hover].Enabled) e.Effects = DragDropEffects.Copy;
    }
    private void ReceiveDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (Busy || !LoadDragData(e.Data)) return;
        int index = Hit(e.GetPosition(_surface));
        if (index < 0) { Dismiss(); return; }
        if (!_surface.Actions[index].Enabled) { ShowUnavailable(index); return; }
        e.Effects = DragDropEffects.Copy;
        Invoke(index);
    }
    private void Invoke(int index)
    {
        if (Busy || Selection is null || index < 0 || index >= _surface.Actions.Count) return;
        var action = _surface.Actions[index];
        if (!action.Enabled) { ShowUnavailable(index); return; }
        _dismiss.Stop();
        Busy = true; _surface.State = "working"; _surface.Progress = -1;
        _surface.ActiveActionLabel = action.Label; _surface.ToolTip = null;
        _surface.Hover = -1; _surface.Message = "Starting · Esc to cancel";
        Activate(); _surface.Focus();
        ActionInvoked?.Invoke(action, Selection);
    }
    public void SetProgress(RadialProgress progress) { _surface.Progress = progress.Percent; _surface.Message = progress.Message; }
    public void SuspendForOptions() { _ticker.Stop(); Hide(); }
    public void ResumeAfterOptions() { if (_disposed) return; Show(); _ticker.Start(); Activate(); _surface.Focus(); }
    public void CancelOptions() { if (_disposed) return; Busy = false; _surface.State = "idle"; Dismiss(); }
    public void Complete(string message) { Busy = false; _surface.State = "success"; _surface.SuccessAge = 0; _surface.Message = message; ScheduleDismiss(1400); }
    public void Fail(string message) { Busy = false; _surface.State = "error"; _surface.Message = message; _surface.ToolTip = message; ScheduleDismiss(7000); }
    private void ShowUnavailable(int index)
    {
        var action = _surface.Actions[index];
        _surface.State = "unavailable"; _surface.Hover = index;
        _surface.Message = action.UnavailableReason ?? "This format is unavailable.";
        _surface.ToolTip = _surface.Message;
        ScheduleDismiss(7000);
    }
    private void ScheduleDismiss(int milliseconds) { _dismiss.Stop(); _dismiss.Interval = TimeSpan.FromMilliseconds(milliseconds); _dismiss.Start(); _surface.InvalidateVisual(); }
    public void OnDragReleased()
    {
        int generation = _generation;
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        delay.Tick += (_, _) => { delay.Stop(); if (generation == _generation && !Busy && _surface.State == "idle") Dismiss(); };
        delay.Start();
    }
    public void Dismiss() { if (Busy) return; _dismiss.Stop(); _ticker.Stop(); Hide(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _dismiss.Stop(); _ticker.Stop(); Close(); }

    private static ImageSource? LoadIcon(string path)
    {
        try
        {
            if (RadialSelection.GetCategory(path) == "Image")
            {
                using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 48; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
            image.Freeze(); return image;
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException) { return null; }
    }
}

public sealed class RadialSurface : FrameworkElement
{
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double), typeof(RadialSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }
    public IReadOnlyList<RadialAction> Actions { get; private set; } = [];
    public RadialSelection? Selection { get; set; }
    public ImageSource? Icon { get; set; }
    private int _hoverIndex = -1;
    public int Hover
    {
        get => _hoverIndex;
        set
        {
            if (_hoverIndex == value) return;
            _hoverIndex = value;
            ToolTip = value >= 0 && value < Actions.Count ? Actions[value].Label + (Actions[value].Enabled ? "" : " · " + Actions[value].UnavailableReason) : null;
            InvalidateVisual();
        }
    }
    public event Action<int>? ActionRequested;
    internal void RequestAction(int index) => ActionRequested?.Invoke(index);
    public string State { get; set; } = "idle";
    public string ActiveActionLabel { get; set; } = "";
    public string Caption { get; set; } = "FORMAT CONVERSION";
    public string Message { get; set; } = "Hold a file over the wheel";
    public double Progress { get; set; } = -1;
    public double SuccessAge { get; set; }
    private double[] _hover = [];
    private double _phase;
    private static readonly Point Center = new(270, 270);
    public RadialSurface()
    {
        Focusable = true;
        // Keyboard focus is shown by the highlighted segment. A theme adorner would
        // outline this transparent 540px canvas with an unwanted rectangular frame.
        FocusVisualStyle = null;
        Width = Height = 540;
    }
    public void SetActions(IReadOnlyList<RadialAction> actions) { Actions = actions; _hover = new double[actions.Count]; Hover = -1; InvalidateVisual(); UIElementAutomationPeer.FromElement(this)?.InvalidatePeer(); }
    public void Tick()
    {
        _phase = (_phase + 3.5) % 360;
        SuccessAge += 16;
        for (int i = 0; i < _hover.Length; i++) _hover[i] += ((i == Hover ? 1 : 0) - _hover[i]) * .23;
        InvalidateVisual();
    }
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private static Point Polar(double angle, double radius) => new(270 + Math.Cos(angle * Math.PI / 180) * radius, 270 + Math.Sin(angle * Math.PI / 180) * radius);
    private static StreamGeometry Segment(double start, double end, double outer, double inner)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(Polar(start, outer), true, true);
        context.ArcTo(Polar(end, outer), new(outer, outer), 0, end - start > 180, SweepDirection.Clockwise, true, false);
        context.LineTo(Polar(end, inner), true, false);
        context.ArcTo(Polar(start, inner), new(inner, inner), 0, end - start > 180, SweepDirection.Counterclockwise, true, false);
        return geometry;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 540, 540));
        dc.PushTransform(new ScaleTransform(Reveal, Reveal, 270, 270));
        var glow = new RadialGradientBrush(); glow.GradientStops.Add(new(Color.FromArgb(180, 0, 0, 0), 0));
        glow.GradientStops.Add(new(Color.FromArgb(70, 95, 0, 28), .82)); glow.GradientStops.Add(new(Color.FromArgb(0, 95, 0, 28), 1));
        dc.DrawEllipse(glow, null, Center, 260, 260);
        for (int i = 0; i < Actions.Count; i++)
        {
            double step = 360d / Actions.Count, angle = -90 + i * step, amount = _hover[i];
            var geometry = Segment(angle - step / 2 + 1.5, angle + step / 2 - 1.5, 224 + 10 * amount, 112);
            if (amount > .01) dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(35 * amount), 220, 20, 60)), 9), geometry);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(246, (byte)(20 + 38 * amount), (byte)(20 - 8 * amount), 24)), new Pen(Brush(amount > .1 ? "#DC143C" : "#353034"), 1 + amount), geometry);
            var action = Actions[i];
            Point contentCenter = Polar(angle, 167 + amount * 4);
            bool showFormat = action.Operation == RadialOperation.Convert && !string.IsNullOrWhiteSpace(action.Target);
            RadialActionIcons.Draw(dc, action, new(contentCenter.X, contentCenter.Y - (showFormat ? 10 : 0)),
                showFormat ? (Actions.Count > 7 ? 28 : 32) : (Actions.Count > 8 ? 28 : 34));
            if (showFormat)
            {
                string format = action.Target.TrimStart('.').ToUpperInvariant();
                double width = Math.Clamp(format.Length * 7 + 18, 44, 80);
                var label = new Rect(contentCenter.X - width / 2, contentCenter.Y + 18, width, 20);
                dc.DrawRoundedRectangle(Brush(amount > .1 ? "#391C2A" : "#1E1B23"),
                    new Pen(Brush(amount > .1 ? "#784052" : "#42313D"), 1), label, 6, 6);
                Text(dc, format, new(label.X + 4, label.Y + 2), width - 8, 11,
                    !action.Enabled ? "#877E88" : amount > .1 ? "#FFE6ED" : "#D5CED9", true);
            }
        }
        dc.DrawEllipse(Brush("#0A0A0A"), new Pen(Brush("#493039"), 1), Center, 98, 98);
        dc.DrawEllipse(null, new Pen(Brush("#351720"), 3), Center, 104, 104);
        if (State == "working")
        {
            double start = Progress < 0 ? _phase : -90, sweep = Progress < 0 ? 95 : Math.Max(1, Math.Clamp(Progress, 0, 100) * 3.599);
            var arc = new StreamGeometry();
            using (var context = arc.Open())
            {
                context.BeginFigure(Polar(start, 104), false, false);
                context.ArcTo(Polar(start + sweep, 104), new(104, 104), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, new Pen(Brush("#DC143C"), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
            Text(dc, Progress < 0 ? "•••" : $"{(int)Progress}%", new(190, 218), 160, 33, "#FFFFFF");
        }
        else if (State == "success")
        {
            double pulse = 1 + .07 * Math.Sin(Math.Min(1, SuccessAge / 700) * Math.PI);
            dc.PushTransform(new ScaleTransform(pulse, pulse, 270, 245));
            var pen = new Pen(Brush("#FF5278"), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new(252, 246), new(266, 260)); dc.DrawLine(pen, new(266, 260), new(294, 229)); dc.Pop();
        }
        else if (State == "error") Text(dc, "!", new(230, 208), 80, 43, "#FF6584");
        else if (State == "unavailable") Text(dc, "i", new(230, 208), 80, 39, "#A8B4C8");
        else if (Hover >= 0 && Hover < Actions.Count) RadialActionIcons.Draw(dc, Actions[Hover], new(270, 236), 32);
        else if (Icon is not null) dc.DrawImage(Icon, new Rect(246, 208, 48, 48));
        else Text(dc, "C", new(220, 211), 100, 31, "#DC143C");
        bool describing = State is "idle" or "unavailable" && Hover >= 0 && Hover < Actions.Count;
        string title = State == "error" ? (ActiveActionLabel.Length > 0 ? ActiveActionLabel + " failed" : "Conversion failed") :
            describing ? Actions[Hover].Label + (Actions[Hover].Enabled ? "" : " unavailable") : Selection?.Title ?? "CORTEX FX";
        Text(dc, title, new(185, 279), 170, describing ? 14 : 12, "#FFFFFF", true);
        Text(dc, describing || State == "error" ? Selection?.Title ?? "" : Selection?.SizeLabel ?? "DROP TO CONVERT", new(185, 303), 170, 10, "#A49AA0");
        Text(dc, Caption, new(105, 15), 330, 10, "#D8758B", true);
        string hint = Hover >= 0 && Hover < Actions.Count && !Actions[Hover].Enabled ? Actions[Hover].UnavailableReason! : Message;
        Text(dc, hint.ReplaceLineEndings(" "), new(70, 504), 400, 11, "#E6D4DA", false, 2);
        dc.Pop();
    }
    private void Text(DrawingContext dc, string value, Point origin, double width, double size, string color, bool bold = false, int lines = 1)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = width, MaxLineCount = lines, TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, origin);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new RadialSurfaceAutomationPeer(this);
}

internal sealed class RadialSurfaceAutomationPeer(RadialSurface surface) : FrameworkElementAutomationPeer(surface)
{
    protected override string GetNameCore() => "Cortex FX action wheel";
    protected override List<AutomationPeer> GetChildrenCore() => surface.Actions.Select((action, index) => (AutomationPeer)new RadialActionAutomationPeer(surface, action, index)).ToList();
}

internal sealed class RadialActionAutomationPeer(RadialSurface surface, RadialAction action, int index)
    : FrameworkElementAutomationPeer(surface), System.Windows.Automation.Provider.IInvokeProvider
{
    protected override string GetNameCore() => action.Label;
    protected override string GetHelpTextCore() => action.UnavailableReason ?? "Select to run this action.";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
    protected override bool IsEnabledCore() => action.Enabled && surface.State is "idle" or "unavailable";
    protected override List<AutomationPeer>? GetChildrenCore() => null;
    public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
    public void Invoke()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        surface.Dispatcher.BeginInvoke(() => surface.RequestAction(index));
    }
    protected override void SetFocusCore() { surface.Focus(); surface.Hover = index; }
}
