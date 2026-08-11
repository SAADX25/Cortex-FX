using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace CortexFX.Core.Services.Infrastructure;

/// <summary>
/// Close (X) sends the app to the tray by the clock.
/// Minimize stays a normal taskbar minimize. Exit only from the tray menu.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private TaskbarIcon? _tray;
    private bool _disposed;
    private bool _forceExit;

    public TrayIconService(Window window)
    {
        _window = window;
    }

    public void Attach()
    {
        if (_tray != null)
            return;

        var openItem = new MenuItem { Header = "Open Cortex FX" };
        openItem.Click += (_, _) => RestoreFromTray();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApp();

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Cortex FX",
            IconSource = LoadIconImage(),
            ContextMenu = menu,
            Visibility = Visibility.Collapsed
        };
        _tray.TrayMouseDoubleClick += (_, _) => RestoreFromTray();
        _tray.TrayLeftMouseUp += (_, _) => RestoreFromTray();

        _window.Closing += Window_Closing;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_disposed)
            return;

        // X / Alt+F4 → tray. Real exit only via tray "Exit".
        if (!_forceExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        Dispose();
    }

    public void HideToTray()
    {
        if (_tray == null || _disposed)
            return;

        // If it was minimized to the taskbar, restore state before hiding
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Hide();
        _window.ShowInTaskbar = false;
        _tray.Visibility = Visibility.Visible;
        _tray.ShowBalloonTip(
            "Cortex FX",
            "Still running next to the clock. Click the icon to open, or right-click → Exit to quit.",
            BalloonIcon.Info);
    }

    public void RestoreFromTray()
    {
        if (_disposed)
            return;

        if (_tray != null)
            _tray.Visibility = Visibility.Collapsed;

        _window.ShowInTaskbar = true;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void ExitApp()
    {
        _forceExit = true;
        if (_tray != null)
            _tray.Visibility = Visibility.Collapsed;

        _window.ShowInTaskbar = true;
        _window.Close();
        Application.Current.Shutdown();
    }

    private static System.Windows.Media.ImageSource? LoadIconImage()
    {
        try
        {
            return new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/logo.ico"));
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _window.Closing -= Window_Closing;

        if (_tray != null)
        {
            _tray.Dispose();
            _tray = null;
        }
    }
}
