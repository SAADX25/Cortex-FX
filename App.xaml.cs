using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Documents;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Core.Services.Media;
using CortexFX.ViewModels;

namespace CortexFX;

/// <summary>
/// Application startup: wire DI, then open the main window.
/// </summary>
public partial class App : Application
{
    /// <summary>Shared DI container (used when a control cannot take constructor injection).</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ConsoleLogger.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            ConsoleLogger.Error("Unhandled", args.Exception.ToString());
            MessageBox.Show(
                "Cortex FX hit an unexpected problem. Your files were not deleted or uploaded.\n\n" +
                $"A diagnostic log was saved here:\n{ConsoleLogger.LogFilePath}",
                "Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                ConsoleLogger.Error("Unhandled", ex.ToString());
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ConsoleLogger.Error("Unhandled", args.Exception.ToString());
            args.SetObserved();
        };

        Version? version = typeof(App).Assembly.GetName().Version;
        string versionText = version != null
            ? $"v{version.Major}.{version.Minor}.{version.Build}"
            : "v1.5.0";

        ConsoleLogger.Info("App", $"Cortex FX {versionText} starting...");

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();
        ConsoleLogger.Success("App", "Services loaded.");

        // Kill leftover Office COM processes from a previous crash (don't block first paint).
        var processManager = Services.GetRequiredService<IProcessManager>();
        _ = Task.Run(() =>
        {
            processManager.KillZombieProcesses("WINWORD", "POWERPNT", "EXCEL");
            ConsoleLogger.Info("Office", "Startup cleanup completed.");
        });

        string? startupFile = e.Args.Length > 0 ? e.Args[0] : null;
        object[] startupParameters = startupFile == null
            ? Array.Empty<object>()
            : [startupFile];

        var mainWindow = ActivatorUtilities.CreateInstance<MainWindow>(Services, startupParameters);
        mainWindow.Show();
        ConsoleLogger.Success("App", "Main window ready.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ConsoleLogger.Info("App", "Shutting down...");
        // Dispose ProcessManager → kills all tracked processes
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>
    /// Register services and ViewModels used by the app.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Shared for the whole app lifetime
        services.AddSingleton<IAppConfiguration, AppConfiguration>();
        services.AddSingleton<IProcessManager, ProcessManager>();

        // Conversion engines
        services.AddSingleton<IFFmpegService, FFmpegService>();
        services.AddSingleton<IMagickService, MagickService>();
        services.AddSingleton<IOfficeInteropService, OfficeInteropService>();
        services.AddSingleton<IPdfRenderService, PdfRenderService>();
        services.AddSingleton<IOptionalConversionService, OptionalConversionService>();
        services.AddSingleton<IResourceValidationService, ResourceValidationService>();

        services.AddSingleton<IConversionRouter, ConversionRouter>();

        // Fresh instance each time they are resolved
        services.AddTransient<MainViewModel>();
        services.AddTransient<ConversionViewModel>();
        services.AddTransient<AudioEditorViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MainWindow>();
    }
}
