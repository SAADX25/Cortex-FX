using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services;
using CortexFX.ViewModels;

namespace CortexFX;

/// <summary>
/// Application entry point with DI container configuration.
/// All service lifetimes are managed by the container; cleanup
/// is handled via IProcessManager.Dispose() on exit.
/// </summary>
public partial class App : Application
{
    /// <summary>Global service provider — enables service location where DI injection isn't possible.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Build the DI Container
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        // 2. Pre-launch cleanup via the new ProcessManager
        var processManager = Services.GetRequiredService<IProcessManager>();
        processManager.KillZombieProcesses("WINWORD", "POWERPNT", "EXCEL");

        // 3. Resolve and show MainWindow
        string? startupFile = e.Args.Length > 0 ? e.Args[0] : null;

        // MainWindow still accepts a startup file for context menu integration
        var mainWindow = new MainWindow(startupFile);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose ProcessManager → kills all tracked processes
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>
    /// Register all services and ViewModels in the DI container.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // --- Core Infrastructure (Singletons — one instance for entire app lifetime) ---
        services.AddSingleton<IAppConfiguration, AppConfiguration>();
        services.AddSingleton<IProcessManager, ProcessManager>();

        // --- Engine Services (Singletons — stateless, thread-safe) ---
        services.AddSingleton<IFFmpegService, FFmpegService>();
        services.AddSingleton<IMagickService, MagickService>();
        services.AddSingleton<IOfficeInteropService, OfficeInteropService>();
        services.AddSingleton<IPdfRenderService, PdfRenderService>();

        // --- Routing (Singleton — the brain of the conversion pipeline) ---
        services.AddSingleton<IConversionRouter, ConversionRouter>();

        // --- ViewModels (Transient — fresh instances per resolution) ---
        services.AddTransient<MainViewModel>();
        services.AddTransient<ConversionViewModel>();
        services.AddTransient<AudioEditorViewModel>();
        services.AddTransient<SettingsViewModel>();
    }
}
