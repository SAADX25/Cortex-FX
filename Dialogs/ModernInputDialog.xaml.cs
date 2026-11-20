using System.Windows;
using System.Windows.Input;
using CortexFX.Core.Services.Infrastructure;

namespace CortexFX.Dialogs;

public partial class ModernInputDialog : Window
{
    public string InputText => InputBox.Text?.Trim() ?? string.Empty;

    public ModernInputDialog(
        string title,
        string message,
        string initialValue = "",
        string confirmText = "Save",
        string cancelText = "Cancel")
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(title) ? "Cortex FX" : title;
        TitleText.Text = Title;
        TitleText.FlowDirection = Loc.TextFlowDirection;
        MessageText.Text = message ?? string.Empty;
        MessageText.FlowDirection = Loc.TextFlowDirection;
        InputBox.Text = initialValue ?? string.Empty;
        BtnOk.Content = Loc.TranslateDialogButton(string.IsNullOrWhiteSpace(confirmText) ? "Save" : confirmText);
        BtnCancel.Content = Loc.TranslateDialogButton(string.IsNullOrWhiteSpace(cancelText) ? "Cancel" : cancelText);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            InputBox.Focus();
            return;
        }
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }
}
