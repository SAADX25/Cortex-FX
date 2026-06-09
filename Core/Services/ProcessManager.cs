using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services;

/// <summary>
/// Unified process execution and lifecycle manager.
/// Absorbs legacy external-process execution and Office process cleanup.
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
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillSafe(process);
            ConsoleLogger.Warning("Process", $"Cancelled {Path.GetFileName(exePath)}.");
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
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

        _trackedProcesses[pid] = startTime;
    }

    /// <inheritdoc />
    public void KillAllTracked()
    {
        foreach (var tracked in _trackedProcesses.ToArray())
        {
            int pid = tracked.Key;
            _trackedProcesses.TryRemove(pid, out _);

            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    bool sameProcess = tracked.Value == null || proc.StartTime == tracked.Value.Value;
                    if (sameProcess)
                    {
                        proc.Kill();
                    }
                }
                proc.Dispose();
            }
            catch
            {
                // Process already exited or access denied — safe to ignore
            }
        }
    }

    /// <inheritdoc />
    public void KillZombieProcesses(params string[] processNames)
    {
        foreach (var name in processNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    // A process with no visible window title is likely a zombie
                    // from a crashed COM automation session
                    if (string.IsNullOrEmpty(p.MainWindowTitle))
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch
                        {
                            // Access denied or already gone
                        }
                    }
                    p.Dispose();
                }
            }
            catch
            {
                // GetProcessesByName can throw on access issues
            }
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
