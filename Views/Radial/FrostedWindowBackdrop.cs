using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Media;

namespace CortexFX.Views.Radial;

/// <summary>Native desktop acrylic for tool dialogs, with an opaque fallback on older Windows versions.</summary>
internal static class FrostedWindowBackdrop
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int SystemBackdropType = 38;
    private const int DesktopAcrylic = 3;
    private const int RoundedCorners = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    internal static bool SupportsAcrylic(Version windowsVersion, bool highContrast) =>
        windowsVersion >= new Version(10, 0, 22621) && !highContrast;

    public static bool Apply(Window window, Border shell)
    {
        // Layered windows such as the radial wheel must retain per-pixel transparency.
        if (window.AllowsTransparency) return false;
        bool enabled = TryApply(window);
        ApplyAppearance(window, shell, enabled);
        return enabled;
    }

    internal static void ApplyAppearance(Window window, Border shell, bool acrylic)
    {
        shell.Background = new SolidColorBrush(acrylic
            ? Color.FromArgb(72, 16, 31, 48) : Color.FromRgb(25, 36, 50));
        if (!acrylic)
        {
            window.Background = new SolidColorBrush(Color.FromRgb(25, 36, 50));
            if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.GlassFrameThickness = new Thickness(0);
        }
    }

    private static bool TryApply(Window window)
    {
        if (!SupportsAcrylic(Environment.OSVersion.Version, SystemParameters.HighContrast)) return false;
        try
        {
            if (DwmIsCompositionEnabled(out bool enabled) < 0 || !enabled) return false;
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle == 0 || HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target) return false;
            int dark = 1, corner = RoundedCorners, backdrop = DesktopAcrylic;
            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));
            DwmSetWindowAttribute(handle, WindowCornerPreference, ref corner, sizeof(int));
            if (DwmSetWindowAttribute(handle, SystemBackdropType, ref backdrop, sizeof(int)) < 0) return false;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(handle, ref margins) < 0)
            {
                backdrop = 1; // Return to a normal opaque window if extending the frame failed.
                DwmSetWindowAttribute(handle, SystemBackdropType, ref backdrop, sizeof(int));
                return false;
            }
            target.BackgroundColor = Colors.Transparent;
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}