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
    private readonly ConcurrentBag<int> _trackedPids = [];
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
            throw new FileNotFoundException($"Executable not found: {exePath}");

        var psi = CreateStartInfo(exePath, arguments);

        using var process = new Process { StartInfo = psi };

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
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        var result = new ProcessResult(process.ExitCode, stdout, stderr);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Process exited with code {result.ExitCode}:\n{result.StdErr}");
        }

        return result;
    }

    /// <inheritdoc />
    public void TrackProcess(int pid)
    {
        _trackedPids.Add(pid);
    }

    /// <inheritdoc />
    public void KillAllTracked()
    {
        while (_trackedPids.TryTake(out int pid))
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
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
