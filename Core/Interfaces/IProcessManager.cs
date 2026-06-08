namespace CortexFX.Core.Interfaces;

/// <summary>
/// Result of an external process execution.
/// </summary>
public record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Unified abstraction for all external process execution and lifecycle management.
/// Replaces: MainWindow.RunExternalProcess(), CortexEngine._managedPids,
/// CortexEngine.KillGhostPowerPoint(),
/// CortexEngine.GlobalCleanup(), CortexEngine.PreLaunchCleanup().
/// </summary>
public interface IProcessManager : IDisposable
{
    /// <summary>
    /// Run an external process synchronously. Throws on non-zero exit code.
    /// </summary>
    ProcessResult RunSync(string exePath, string arguments, CancellationToken ct = default);

    /// <summary>
    /// Run an external process asynchronously with lifecycle tracking.
    /// </summary>
    Task<ProcessResult> RunAsync(string exePath, string arguments, CancellationToken ct = default);

    /// <summary>
    /// Track a process ID for cleanup on application exit.
    /// </summary>
    void TrackProcess(int pid);

    /// <summary>
    /// Kill all tracked processes. Called on application shutdown.
    /// </summary>
    void KillAllTracked();

    /// <summary>
    /// Kill zombie Office processes that have no visible window (backgrounded COM instances).
    /// </summary>
    void KillZombieProcesses(params string[] processNames);
}
