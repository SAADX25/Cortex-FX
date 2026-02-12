using System.Configuration;
using System.Data;
using System.Windows;
using System.Linq;

namespace CortexFX;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? startupFile = null;
        if (e.Args.Length > 0)
        {
            startupFile = e.Args[0];
        }

        MainWindow mainWindow = new MainWindow(startupFile);
        mainWindow.Show();
    }
}
