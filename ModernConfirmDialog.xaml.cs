using System.Windows;

namespace CortexFX;

public partial class ModernConfirmDialog : Window
{
    /// <summary>
    /// True if the user clicked "Yes, Close".
    /// </summary>
    public bool Confirmed { get; private set; }

    public ModernConfirmDialog(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
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
}
