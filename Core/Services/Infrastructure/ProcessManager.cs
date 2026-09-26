using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services.Infrastructure;

/// <summary>
/// Starts external processes and tracks them so we can kill leftovers on exit.
/// </summary>
public sealed class ProcessManager : IProcessManager
{
    private readonly ConcurrentDictionary<int, DateTime?> _trackedProcesses = [];
    private bool _disposed;

    /// <inheritdoc />
    public ProcessResult RunSync(string exePath, string arguments, CancellationToken ct = default)
    {
        return RunAsync(exePath, arguments, ct).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(string exePath, string arguments, CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
        {
            ConsoleLogger.Error("Process", $"Executable not found: {exePath}");
            throw new FileNotFoundException($"Executable not found: {exePath}");
        }

        var psi = CreateStartInfo(exePath, arguments);

        using var process = new Process { StartInfo = psi };

        ConsoleLogger.Info("Process", $"Starting {Path.GetFileName(exePath)}.");
        process.Start();
        TrackProcess(process.Id);

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillSafe(process);
            UntrackProcess(process.Id);
            ConsoleLogger.Warning("Process", $"Cancelled {Path.GetFileName(exePath)}.");
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        UntrackProcess(process.Id);
        var result = new ProcessResult(process.ExitCode, stdout, stderr);

        if (result.ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            string executableName = Path.GetFileName(exePath);
            ConsoleLogger.Error("Process", $"{executableName} exited with code {result.ExitCode}: {details}");
            throw new ProcessExecutionException(executableName, result.ExitCode, result.StdOut, result.StdErr);
        }

        ConsoleLogger.Success("Process", $"{Path.GetFileName(exePath)} completed.");
        return result;
    }

    /// <inheritdoc />
    public void TrackProcess(int pid)
    {
        DateTime? startTime = null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            startTime = proc.StartTime;
        }
        catch
        {
            // Process may have exited before it could be inspected.
        }

        if (startTime.HasValue) _trackedProcesses[pid] = startTime;
    }

    public void UntrackProcess(int pid) => _trackedProcesses.TryRemove(pid, out _);

    public bool IsTrackedProcess(int pid)
    {
        if (!_trackedProcesses.TryGetValue(pid, out var startTime) || !startTime.HasValue) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited && proc.StartTime == startTime.Value;
        }
        catch { return false; }
    }

    public void KillTrackedProcess(int pid)
    {
        if (!_trackedProcesses.TryRemove(pid, out var startTime) || !startTime.HasValue) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.HasExited && proc.StartTime == startTime.Value) proc.Kill();
        }
        catch { /* Already exited or access denied. */ }
    }

    /// <inheritdoc />
    public void KillAllTracked()
    {
        foreach (var tracked in _trackedProcesses.ToArray())
        {
            KillTrackedProcess(tracked.Key);
        }
    }

    /// <inheritdoc />
    public void KillZombieProcesses(params string[] processNames)
    {
        foreach (var tracked in _trackedProcesses.ToArray())
        {
            try
            {
                using var proc = Process.GetProcessById(tracked.Key);
                if (processNames.Any(n => string.Equals(n, proc.ProcessName, StringComparison.OrdinalIgnoreCase)))
                    KillTrackedProcess(tracked.Key);
            }
            catch { UntrackProcess(tracked.Key); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KillAllTracked();
    }

    // --- Private helpers ---

    private static ProcessStartInfo CreateStartInfo(string exePath, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    private static void KillSafe(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch
        {
            // Already exited
        }
    }
}
