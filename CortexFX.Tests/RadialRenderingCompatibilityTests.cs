using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using CortexFX.Core.Radial;
using CortexFX.Views.Radial;
using Xunit;

namespace CortexFX.Tests;

public sealed class RadialRenderingCompatibilityTests
{
    [Theory]
    [InlineData(17763, false, false)]
    [InlineData(19045, false, false)]
    [InlineData(22000, false, false)]
    [InlineData(22620, false, false)]
    [InlineData(22621, false, true)]
    [InlineData(26100, false, true)]
    [InlineData(26300, false, true)]
    [InlineData(22621, true, false)]
    public void AcrylicRequiresACompatibleWindowsBuildAndContrastMode(int build, bool highContrast, bool expected) =>
        Assert.Equal(expected, FrostedWindowBackdrop.SupportsAcrylic(new Version(10, 0, build), highContrast));

    [Fact]
    public void FallbackUsesAnOpaqueClientSurfaceWithoutExtendedGlass() => RunSta(() =>
    {
        var shell = new Border { Background = Brushes.Transparent };
        var window = new Window { WindowStyle = WindowStyle.None, Background = Brushes.Transparent, Content = shell };
        try
        {
            WindowChrome.SetWindowChrome(window, new WindowChrome { GlassFrameThickness = new Thickness(-1) });
            FrostedWindowBackdrop.ApplyAppearance(window, shell, acrylic: false);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(shell.Background).Color.A);
            Assert.Equal(new Thickness(0), WindowChrome.GetWindowChrome(window).GlassFrameThickness);
            Assert.False(window.AllowsTransparency);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ToolBackdropCannotChangeTheLayeredWheelTransparency() => RunSta(() =>
    {
        using var window = new RadialWheelWindow(new(new FakeEngines().Router, new FakeConfig(".")), new());
        var shell = new Border { Background = Brushes.Transparent };
        Brush before = window.Background;
        Assert.False(FrostedWindowBackdrop.Apply(window, shell));
        Assert.Same(before, window.Background);
        Assert.Equal(0, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
        Assert.Equal(0, Assert.IsType<SolidColorBrush>(shell.Background).Color.A);
        Assert.True(window.AllowsTransparency);
    });

    [Fact]
    public void KeyboardFocusNeverDrawsASquareDuringConversionOrResults() => RunSta(() =>
    {
        string folder = Path.Combine(Path.GetTempPath(), "CortexFX_FocusFrame_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string input = Path.Combine(folder, "picture.png");
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 10, 40, 90, 255, 10, 40, 90, 255, 10, 40, 90, 255, 10, 40, 90, 255 }, 8)));
            using (var file = File.Create(input)) png.Save(file);
            using var window = new RadialWheelWindow(new(new FakeEngines().Router, new FakeConfig(folder)), new());
            int calls = 0; string? target = null;
            window.ActionInvoked += (action, _) => { calls++; target = action.Target; };
            window.Open(RadialMode.Convert, new(300, 300), [input]);
            var surface = (RadialSurface)window.Content;
            window.BeginAnimation(UIElement.OpacityProperty, null); window.Opacity = 1;
            surface.BeginAnimation(RadialSurface.RevealProperty, null); surface.Reveal = 1;
            Assert.True(surface.Focusable);
            CheckTransparentFrame(window, surface);
            SendKey(window, Key.Right);
            Assert.Equal(0, surface.Hover);
            SendKey(window, Key.Enter);
            Assert.True(window.Busy); Assert.Equal(1, calls); Assert.Equal("jpg", target);
            window.SetProgress(new(50, "Converting"));
            CheckTransparentFrame(window, surface);
            window.Complete("Saved");
            CheckTransparentFrame(window, surface);
            window.Fail("Test failure");
            CheckTransparentFrame(window, surface);
        }
        finally { Directory.Delete(folder, true); }
    });

    private static void CheckTransparentFrame(RadialWheelWindow window, RadialSurface surface)
    {
        var type = typeof(KeyboardNavigation);
        var always = type.GetProperty("AlwaysShowFocusVisual", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previous = always.GetValue(null);
        try
        {
            always.SetValue(null, true);
            object navigation = type.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var show = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(m => m.Name == "ShowFocusVisual" && m.GetParameters().Length == 1);
            foreach (var element in new FrameworkElement[] { window, surface })
            {
                element.Focus(); show.Invoke(navigation, [element]);
                Assert.Empty(AdornerLayer.GetAdornerLayer(element)?.GetAdorners(element) ?? []);
            }
        }
        finally { always.SetValue(null, previous); }
        surface.Tick(); window.UpdateLayout();
        Visual root = surface;
        while (VisualTreeHelper.GetParent(root) is Visual parent) root = parent;
        var bitmap = new RenderTargetBitmap(540, 540, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var pixels = new byte[540 * 540 * 4]; bitmap.CopyPixels(pixels, 540 * 4, 0);
        for (int y = 0; y < 540; y++) for (int x = 0; x < 540; x++)
            if (x < 4 || y < 4 || x >= 536 || y >= 536)
                Assert.Equal(0, pixels[(y * 540 + x) * 4 + 3]);
    }

    private static void SendKey(RadialWheelWindow window, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(args);
        Assert.True(args.Handled);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}