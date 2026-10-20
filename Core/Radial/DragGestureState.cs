namespace CortexFX.Core.Radial;

public readonly record struct ScreenPoint(int X, int Y);
public readonly record struct GestureUpdate(RadialMode? Triggered, bool Released);

public sealed class DragGestureState(int threshold = 8)
{
    private bool _down;
    private ScreenPoint _origin;
    private RadialMode? _mode;

    public GestureUpdate Update(bool down, ScreenPoint point, RadialMode? mode, bool enabled)
    {
        if (!down)
        {
            bool released = _down && _mode.HasValue;
            _down = false;
            _mode = null;
            return new(null, released);
        }
        if (!_down) { _origin = point; _down = true; }
        if (!enabled || !mode.HasValue || Math.Abs(point.X - _origin.X) + Math.Abs(point.Y - _origin.Y) < threshold)
            return new(null, false);
        if (_mode == mode) return new(null, false);
        _mode = mode;
        return new(mode, false);
    }
}
