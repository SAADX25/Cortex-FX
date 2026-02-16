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

        // Pre-Launch Cleanup: Ensure environment is clean before starting
        CortexFX.Core.Engines.CortexEngine.PreLaunchCleanup();

        string? startupFile = null;
        if (e.Args.Length > 0)
        {
            startupFile = e.Args[0];
        }

        MainWindow mainWindow = new MainWindow(startupFile);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Global Process Cleanup
        CortexFX.Core.Engines.CortexEngine.GlobalCleanup();
        base.OnExit(e);
    }
}
