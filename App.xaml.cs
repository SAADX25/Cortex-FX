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
        ConsoleLogger.Initialize();

        Version? version = typeof(App).Assembly.GetName().Version;
        string versionText = version != null
            ? $"v{version.Major}.{version.Minor}.{version.Build}"
            : "v0.6.0";

        ConsoleLogger.Info("App", $"Cortex FX {versionText} starting...");

        // 1. Build the DI Container
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();
        ConsoleLogger.Success("App", "Services loaded.");

        // 2. Pre-launch cleanup via the new ProcessManager
        var processManager = Services.GetRequiredService<IProcessManager>();
        processManager.KillZombieProcesses("WINWORD", "POWERPNT", "EXCEL");
        ConsoleLogger.Info("Office", "Startup cleanup completed.");

        // 3. Resolve and show MainWindow
        string? startupFile = e.Args.Length > 0 ? e.Args[0] : null;
        object[] startupParameters = startupFile == null
            ? Array.Empty<object>()
            : [startupFile];

        // MainWindow still accepts a startup file for context menu integration
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
        services.AddSingleton<IOptionalConversionService, OptionalConversionService>();
        services.AddSingleton<IResourceValidationService, ResourceValidationService>();

        // --- Routing (Singleton — the brain of the conversion pipeline) ---
        services.AddSingleton<IConversionRouter, ConversionRouter>();

        // --- ViewModels (Transient — fresh instances per resolution) ---
        services.AddTransient<MainViewModel>();
        services.AddTransient<ConversionViewModel>();
        services.AddTransient<AudioEditorViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MainWindow>();
    }
}
