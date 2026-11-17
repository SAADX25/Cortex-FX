using System.Windows;

namespace CortexFX.Dialogs;

/// <summary>
/// Centralized Cortex FX themed modal alerts and confirmations (replaces native Windows MessageBox).
/// </summary>
public static class AppDialog
{
    public static void ShowInfo(DependencyObject? owner, string title, string message, string buttonText = "OK") =>
        ShowAlert(owner, title, message, DialogTone.Info, buttonText);

    public static void ShowWarning(DependencyObject? owner, string title, string message, string buttonText = "OK") =>
        ShowAlert(owner, title, message, DialogTone.Warning, buttonText);

    public static void ShowError(DependencyObject? owner, string title, string message, string buttonText = "OK") =>
        ShowAlert(owner, title, message, DialogTone.Danger, buttonText, showCopyButton: true);

    public static void ShowSuccess(DependencyObject? owner, string title, string message, string buttonText = "Done") =>
        ShowAlert(owner, title, message, DialogTone.Success, buttonText);

    public static bool Confirm(
        DependencyObject? owner,
        string title,
        string message,
        string confirmText = "Yes",
        string cancelText = "No",
        DialogTone tone = DialogTone.Question)
    {
        Window? ownerWindow = ResolveOwnerWindow(owner);
        var dialog = new ModernConfirmDialog(title, message, confirmText, cancelText, tone);
        ConfigureOwnership(dialog, ownerWindow);
        return dialog.ShowDialog() == true && dialog.Confirmed;
    }

    private static void ShowAlert(
        DependencyObject? owner,
        string title,
        string message,
        DialogTone tone,
        string buttonText,
        bool showCopyButton = false)
    {
        Window? ownerWindow = ResolveOwnerWindow(owner);
        var dialog = new ModernMessageDialog(title, message, tone, buttonText, showCopyButton);
        ConfigureOwnership(dialog, ownerWindow);
        dialog.ShowDialog();
    }

    private static Window? ResolveOwnerWindow(DependencyObject? owner)
    {
        Window? resolved = owner switch
        {
            Window w => w,
            DependencyObject d => Window.GetWindow(d),
            _ => null
        };

        if (resolved == null && Application.Current != null)
        {
            foreach (Window win in Application.Current.Windows)
            {
                if (win.IsActive && win.IsVisible)
                {
                    resolved = win;
                    break;
                }
            }
            resolved ??= Application.Current.MainWindow;
        }

        return resolved is { IsLoaded: true, IsVisible: true } ? resolved : null;
    }

    private static void ConfigureOwnership(Window dialog, Window? ownerWindow)
    {
        if (ownerWindow != null)
        {
            dialog.Owner = ownerWindow;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ShowInTaskbar = false;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }
    }
}
