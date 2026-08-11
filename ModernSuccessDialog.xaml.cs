using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CortexFX;

public partial class ModernSuccessDialog : Window
{
    private readonly string _filePath;

    public ModernSuccessDialog(string title, string message, string filePath)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Export complete" : title;
        MessageText.Text = string.IsNullOrWhiteSpace(message) ? "Your trimmed audio is ready." : message;
        _filePath = filePath ?? string.Empty;
        FileNameText.Text = string.IsNullOrWhiteSpace(_filePath)
            ? "Unknown file"
            : Path.GetFileName(_filePath);
        PathText.Text = _filePath;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnOpenPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_filePath))
            {
                Process.Start("explorer.exe", $"/select,\"{_filePath}\"");
            }
            else
            {
                string? folder = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{folder}\"",
                        UseShellExecute = true
                    });
                }
            }
        }
        catch
        {
            // Ignore explorer failures; user can still close.
        }

        DialogResult = true;
        Close();
    }
}
