using System.IO;
using System.IO.Pipes;
using System.Text;

namespace CortexFX.Core.Services.Infrastructure;

/// <summary>
/// Keeps only one Cortex FX process. A second launch wakes the running window
/// (including when it is hidden next to the clock) instead of opening a copy.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\CortexFX.SingleInstance";
    private const string PipeName = "CortexFX.SingleInstance.Pipe";
    private const int ConnectTimeoutMs = 3000;

    private readonly Mutex _mutex;
    private readonly bool _isPrimary;
    private CancellationTokenSource? _listenCts;
    private bool _disposed;

    private SingleInstanceService(Mutex mutex, bool isPrimary)
    {
        _mutex = mutex;
        _isPrimary = isPrimary;
    }

    public bool IsPrimary => _isPrimary;

    public static SingleInstanceService Create()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool isPrimary;
        try
        {
            isPrimary = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed without releasing the mutex.
            isPrimary = true;
        }

        return new SingleInstanceService(mutex, isPrimary);
    }

    public static bool TryNotifyRunningInstance(IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            client.Connect(ConnectTimeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
            writer.Write(args.Count > 0 ? args[0] : string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warning("App", $"Could not reach the running Cortex FX window: {ex.Message}");
            return false;
        }
    }

    public void StartListening(Action<string> onActivated)
    {
        if (!_isPrimary)
        {
            return;
        }

        _listenCts = new CancellationTokenSource();
        CancellationToken token = _listenCts.Token;
        _ = Task.Run(() => ListenLoopAsync(onActivated, token), token);
    }

    private static async Task ListenLoopAsync(Action<string> onActivated, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string payload = await reader.ReadToEndAsync(token);
                onActivated(payload.Trim());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                ConsoleLogger.Warning("App", $"Single-instance listener: {ex.Message}");
                try
                {
                    await Task.Delay(250, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenCts?.Cancel();
        _listenCts?.Dispose();
        _listenCts = null;

        if (_isPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Process is exiting and may no longer own the mutex.
            }
        }

        _mutex.Dispose();
    }
}
