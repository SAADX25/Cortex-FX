using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CortexFX.Core.Services.Infrastructure;

namespace CortexFX.Dialogs;

public partial class ModernConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    public ModernConfirmDialog(string title, string message)
        : this(title, message, "Yes", "No", DialogTone.Question)
    {
    }

    public ModernConfirmDialog(
        string title,
        string message,
        string confirmText,
        string cancelText,
        DialogTone tone = DialogTone.Question)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(title) ? Loc.T("Loc_Dlg_Confirm", "Confirm") : title;
        TitleText.Text = Title;
        TitleText.FlowDirection = Loc.TextFlowDirection;
        MessageText.Text = message ?? string.Empty;
        MessageText.FlowDirection = Loc.TextFlowDirection;
        BtnYes.Content = Loc.TranslateDialogButton(string.IsNullOrWhiteSpace(confirmText) ? "Confirm" : confirmText);
        BtnNo.Content = Loc.TranslateDialogButton(string.IsNullOrWhiteSpace(cancelText) ? "Cancel" : cancelText);
        ApplyTone(tone);
    }

    private void ApplyTone(DialogTone tone)
    {
        switch (tone)
        {
            case DialogTone.Warning:
                SetPalette("#2C2519", "#FBBF24", "#92400E", "#B45309", "\uE7BA");
                break;

            case DialogTone.Danger:
                SetPalette("#2D1D20", "#F87171", "#991B1B", "#B91C1C", "\uE74D");
                break;

            default:
                SetPalette("#242833", "#93C5FD", "#2F3440", "#454B5C", "\uE9CE");
                break;
        }
    }

    private void SetPalette(string badgeBg, string iconColor, string btnBg, string btnBorder, string glyph)
    {
        IconBadge.Background = BrushFrom(badgeBg);
        IconGlyph.Foreground = BrushFrom(iconColor);
        IconGlyph.Text = glyph;

        BtnYes.Background = BrushFrom(btnBg);
        BtnYes.BorderBrush = BrushFrom(btnBorder);
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnYes_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void BtnNo_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Confirmed = false;
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }
}
