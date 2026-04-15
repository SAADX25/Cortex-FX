using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services;

/// <summary>
/// Unified process execution and lifecycle manager.
/// Absorbs:
///   - MainWindow.RunExternalProcess()           (sync execution)
///   - WatermarkProcessor.RunFFmpegAsync()        (async execution)
///   - CortexEngine._managedPids                  (PID tracking)
///   - CortexEngine.GlobalCleanup()               (shutdown kill)
///   - CortexEngine.PreLaunchCleanup()            (zombie kill)
///   - CortexEngine.KillGhostPowerPoint()         (Office zombie kill)
/// </summary>
public sealed class ProcessManager : IProcessManager
{
    private readonly ConcurrentBag<int> _trackedPids = [];
    private bool _disposed;

    /// <inheritdoc />
    public ProcessResult RunSync(string exePath, string arguments, CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Executable not found: {exePath}");

        var psi = CreateStartInfo(exePath, arguments);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {exePath}");

        TrackProcess(process.Id);

        // Read stderr before WaitForExit to prevent deadlocks on large output
        string stderr = process.StandardError.ReadToEnd();
        string stdout = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        if (ct.IsCancellationRequested)
        {
            KillSafe(process);
            ct.ThrowIfCancellationRequested();
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Process exited with code {process.ExitCode}:\n{stderr}");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(string exePath, string arguments, CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Executable not found: {exePath}");

        var psi = CreateStartInfo(exePath, arguments);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<ProcessResult>();

        var stderrBuilder = new System.Text.StringBuilder();
        var stdoutBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        process.Exited += (_, _) =>
        {
            // Small delay to let buffered output flush
            try
            {
                var result = new ProcessResult(process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };

        process.Start();
        TrackProcess(process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wire cancellation to kill the process
        await using var ctRegistration = ct.Register(() =>
        {
            KillSafe(process);
            tcs.TrySetCanceled(ct);
        });

        var result = await tcs.Task;

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
