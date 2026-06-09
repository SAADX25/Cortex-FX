namespace CortexFX.Core.Interfaces;

/// <summary>
/// Result of an external process execution.
/// </summary>
public record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Exception raised when an external process exits unsuccessfully.
/// Full stdout/stderr are kept on the exception for logging, while Message remains UI-safe.
/// </summary>
public sealed class ProcessExecutionException : InvalidOperationException
{
    public ProcessExecutionException(string executableName, int exitCode, string stdOut, string stdErr)
        : base($"{executableName} failed with exit code {exitCode}.")
    {
        ExecutableName = executableName;
        ExitCode = exitCode;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    public string ExecutableName { get; }

    public int ExitCode { get; }

    public string StdOut { get; }

    public string StdErr { get; }

    public string Details => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;

    public override string ToString()
    {
        string details = Details;
        return string.IsNullOrWhiteSpace(details)
            ? base.ToString()
            : $"{base.ToString()}{Environment.NewLine}{details}";
    }
}

/// <summary>
/// Unified abstraction for all external process execution and lifecycle management.
/// Replaces legacy external-process execution and Office process cleanup helpers.
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
