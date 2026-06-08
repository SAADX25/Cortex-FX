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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static bool IsEnabled => _enabled;

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

            if (_enabled)
            {
                Console.WriteLine();
            }
        }
    }

    public static void Info(string area, string message) => Write(ConsoleLogLevel.Info, area, message);

    public static void Success(string area, string message) => Write(ConsoleLogLevel.Success, area, message);

    public static void Warning(string area, string message) => Write(ConsoleLogLevel.Warning, area, message);

    public static void Error(string area, string message) => Write(ConsoleLogLevel.Error, area, message);

    public static void Write(ConsoleLogLevel level, string area, string message)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_lock)
        {
            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                ConsoleLogLevel.Success => ConsoleColor.Green,
                ConsoleLogLevel.Warning => ConsoleColor.Yellow,
                ConsoleLogLevel.Error => ConsoleColor.Red,
                _ => ConsoleColor.Cyan
            };

            string prefix = level switch
            {
                ConsoleLogLevel.Success => "OK",
                ConsoleLogLevel.Warning => "WARN",
                ConsoleLogLevel.Error => "ERR",
                _ => "INFO"
            };

            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} > [{prefix}] [{area}] {message}");
            Console.ForegroundColor = oldColor;
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
