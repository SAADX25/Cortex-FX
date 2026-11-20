using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CortexFX.ViewModels;
using CortexFX.Core.Services.Infrastructure;

namespace CortexFX.Dialogs;

public partial class ResourceStatusDialog : Window
{
    private readonly Func<ResourceStatusReport> _loadReport;
    internal ResourceStatusReport Report => (ResourceStatusReport)DataContext;
    public ResourceStatusDialog(Func<ResourceStatusReport> loadReport)
    {
        _loadReport = loadReport;
        InitializeComponent();
        Height = Math.Min(640, Math.Max(440, SystemParameters.WorkArea.Height - 48));
        RefreshStatus();
    }

    internal void RefreshStatus()
    {
        DataContext = _loadReport();
        bool ready = Report.Core.IsReady;
        SummaryPanel.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ready ? "#1C3029" : "#382C24"));
        SummaryPanel.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ready ? "#355144" : "#6A4B35"));
        SummaryCount.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ready ? "#B2E5C7" : "#FFD2B2"));
        OpenToolsButton.IsEnabled = Report.Core.ResourcesDirectoryExists && Directory.Exists(Report.Core.ResourcesDirectory);
        CopyDetailsButton.Content = Loc.T("Loc_Dlg_CopyDetails", "Copy details");
    }
    private void Recheck_Click(object sender, RoutedEventArgs e) => RefreshStatus();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button) DragMove(); }
    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Report.Details); CopyDetailsButton.Content = Loc.T("Loc_Dlg_Copied", "Copied"); }
        catch { CopyDetailsButton.Content = Loc.T("Loc_Dlg_TryAgain", "Try again"); }
    }
    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(Report.Core.ResourcesDirectory)) { RefreshStatus(); return; }
        try { Process.Start(new ProcessStartInfo(Report.Core.ResourcesDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { ConsoleLogger.Warning("Resources", ex.Message); OpenToolsButton.Content = Loc.T("Loc_Res_OpenFailed", "Could not open folder"); }
    }
}
