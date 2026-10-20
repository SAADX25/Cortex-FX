using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CortexFX.Core.Radial;

public sealed class NativeDragMonitor : IDisposable
{
    private readonly RadialSettings _settings;
    private readonly DragGestureState _state = new();
    private readonly DispatcherTimer _timer;
    public event Action<RadialMode, ScreenPoint>? Triggered;
    public event Action? Released;

    public NativeDragMonitor(RadialSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += Poll;
    }

    public void Start() => _timer.Start();
    private void Poll(object? sender, EventArgs args)
    {
        NativeWheel.GetCursorPos(out var point);
        string[] held = new[] { ("Shift", 0x10), ("Alt", 0x12), ("Ctrl", 0x11) }
            .Where(key => (NativeWheel.GetAsyncKeyState(key.Item2) & 0x8000) != 0).Select(key => key.Item1).ToArray();
        RadialMode? mode = held.Length != 1 ? null : held[0] == _settings.ConvertKey ? RadialMode.Convert
            : held[0] == _settings.ToolsKey ? RadialMode.Tools : RadialMode.Quick;
        var update = _state.Update((NativeWheel.GetAsyncKeyState(1) & 0x8000) != 0, new(point.X, point.Y), mode, _settings.Enabled);
        if (update.Triggered.HasValue) Triggered?.Invoke(update.Triggered.Value, new(point.X, point.Y));
        if (update.Released) Released?.Invoke();
    }
    public void Dispose() { _timer.Stop(); _timer.Tick -= Poll; }
}

internal static class NativeWheel
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    public static void CenterWindow(Window window, ScreenPoint point)
    {
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(new Point { X = point.X, Y = point.Y }, 2), ref info) || !GetWindowRect(handle, out var rect)) return;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        int x = Math.Clamp(point.X - width / 2, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        int y = Math.Clamp(point.Y - height / 2, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        SetWindowPos(handle, new nint(-1), x, y, 0, 0, 0x0010 | 0x0001);
    }
}
