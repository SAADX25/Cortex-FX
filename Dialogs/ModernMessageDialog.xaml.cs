using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CortexFX.Dialogs;

public partial class ModernMessageDialog : Window
{
    private readonly string _rawMessage;

    public ModernMessageDialog(
        string title,
        string message,
        DialogTone tone = DialogTone.Info,
        string buttonText = "OK",
        bool showCopyButton = false)
    {
        InitializeComponent();

        _rawMessage = message ?? string.Empty;
        Title = string.IsNullOrWhiteSpace(title) ? "Cortex FX" : title;
        TitleText.Text = Title;
        MessageText.Text = _rawMessage;
        BtnOk.Content = string.IsNullOrWhiteSpace(buttonText) ? "OK" : buttonText;
        BtnCopy.Visibility = showCopyButton || tone == DialogTone.Danger || _rawMessage.Contains('\n')
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyTone(tone);
    }

    private void ApplyTone(DialogTone tone)
    {
        switch (tone)
        {
            case DialogTone.Success:
                SetPalette("#1E2C24", "#4ADE80", "#22C55E", "#2EB86A", "\uE73E");
                break;

            case DialogTone.Warning:
                SetPalette("#2C2519", "#FBBF24", "#363945", "#4D5263", "\uE7BA");
                break;

            case DialogTone.Danger:
                SetPalette("#2D1D20", "#F87171", "#991B1B", "#B91C1C", "\uEA39");
                break;

            case DialogTone.Question:
                SetPalette("#232834", "#93C5FD", "#2F3440", "#454B5C", "\uE9CE");
                break;

            default:
                SetPalette("#242833", "#93C5FD", "#2F3440", "#454B5C", "\uE946");
                break;
        }
    }

    private void SetPalette(string badgeBg, string iconColor, string btnBg, string btnBorder, string glyph)
    {
        IconBadge.Background = BrushFrom(badgeBg);
        IconGlyph.Foreground = BrushFrom(iconColor);
        IconGlyph.Text = glyph;

        BtnOk.Background = BrushFrom(btnBg);
        BtnOk.BorderBrush = BrushFrom(btnBorder);
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

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{TitleText.Text}\n\n{_rawMessage}");
            BtnCopy.Content = "Copied";
        }
        catch { }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
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
