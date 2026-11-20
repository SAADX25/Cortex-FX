using System.Diagnostics;
using System.IO;
using System.Windows;
using CortexFX.Core.Services.Infrastructure;

namespace CortexFX.Dialogs;

public partial class ModernSuccessDialog : Window
{
    private readonly string _filePath;

    public ModernSuccessDialog(string title, string message, string filePath)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? Loc.T("Loc_Dlg_ExportComplete", "Export complete") : title;
        TitleText.FlowDirection = Loc.TextFlowDirection;
        MessageText.Text = string.IsNullOrWhiteSpace(message) ? Loc.T("Loc_Dlg_TrimmedReady", "Your trimmed audio is ready.") : message;
        MessageText.FlowDirection = Loc.TextFlowDirection;
        BtnClose.Content = Loc.T("Loc_Dlg_Close", "Close");
        BtnOpenPath.Content = Loc.T("Loc_Dlg_OpenLocation", "Open file location");
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
