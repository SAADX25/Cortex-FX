namespace CortexFX.Core.Radial;

public static class RadialGeometry
{
    public static int Hit(double x, double y, int count)
    {
        double dx = x - 270, dy = y - 270, radius = Math.Sqrt(dx * dx + dy * dy);
        if (count < 1 || radius < 112 || radius > 237) return -1;
        double step = 360d / count;
        double angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + step / 2 + 360) % 360;
        double local = angle % step;
        return local < 1.5 || local > step - 1.5 ? -1 : (int)(angle / step) % count;
    }
}
