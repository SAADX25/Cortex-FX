using System.IO;
using System.Runtime.InteropServices;

namespace CortexFX.Core.Services;

public enum ConsoleLogLevel
{
    Info,
    Success,
    Warning,
    Error
}

public static class ConsoleLogger
{
    private const int AttachParentProcess = -1;
    private static bool _initialized;
    private static bool _enabled;
    private static readonly object _lock = new();
    private static string _logFilePath = string.Empty;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static bool IsEnabled => _enabled;

    public static string LogDirectory { get; private set; } = string.Empty;

    public static string LogFilePath => _logFilePath;

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _enabled = AttachConsole(AttachParentProcess);
            LogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cortex FX",
                "Logs");
            Directory.CreateDirectory(LogDirectory);
            _logFilePath = Path.Combine(LogDirectory, $"cortexfx-{DateTime.Now:yyyyMMdd}.log");

            if (_enabled)
            {
                Console.WriteLine();
            }

            Write(ConsoleLogLevel.Info, "Log", $"Logging to {_logFilePath}");
        }
    }

    public static void Info(string area, string message) => Write(ConsoleLogLevel.Info, area, message);

    public static void Success(string area, string message) => Write(ConsoleLogLevel.Success, area, message);

    public static void Warning(string area, string message) => Write(ConsoleLogLevel.Warning, area, message);

    public static void Error(string area, string message) => Write(ConsoleLogLevel.Error, area, message);

    public static void Write(ConsoleLogLevel level, string area, string message)
    {
        lock (_lock)
        {
            string prefix = level switch
            {
                ConsoleLogLevel.Success => "OK",
                ConsoleLogLevel.Warning => "WARN",
                ConsoleLogLevel.Error => "ERR",
                _ => "INFO"
            };

            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} > [{prefix}] [{area}] {message}";

            if (_enabled)
            {
                var oldColor = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    ConsoleLogLevel.Success => ConsoleColor.Green,
                    ConsoleLogLevel.Warning => ConsoleColor.Yellow,
                    ConsoleLogLevel.Error => ConsoleColor.Red,
                    _ => ConsoleColor.Cyan
                };

                Console.WriteLine(line);
                Console.ForegroundColor = oldColor;
            }

            if (!string.IsNullOrWhiteSpace(_logFilePath))
            {
                try
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
                catch
                {
                    // Logging must never crash the app.
                }
            }
        }
    }

    public static string ShortPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            string fileName = Path.GetFileName(path);
            string? parent = Directory.GetParent(path)?.Name;
            return string.IsNullOrWhiteSpace(parent) ? fileName : Path.Combine(parent, fileName);
        }
        catch
        {
            return path;
        }
    }
}
