using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TagMarks.Desktop.Managers;

public static class SingleInstanceManager
{
    private static CancellationTokenSource? _cts;
    private static Mutex? _mutex;
    private static ILogger? _logger;
    private static Action? _onActivated;
    
    /// <summary>
    /// Sets the logger after the DI container is built. Calls before this are silently unlogged.
    /// </summary>
    public static void SetLogger(ILoggerFactory loggerFactory) {
        _logger = loggerFactory.CreateLogger(typeof(SingleInstanceManager));
    }

    /// <summary>
    /// Registers the callback invoked on the UI thread when a second instance requests activation.
    /// </summary>
    public static void SetActivationCallback(Action onActivated) {
        _onActivated = onActivated;
    }
    
    public static bool EnsureSingleInstance(string name)
    {
        _mutex = new Mutex(true, $"Global\\{name}", out var createdNew);
        if (!createdNew) {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _cts = new CancellationTokenSource();

        Task.Run(() => ListenForActivation(name, _cts.Token));
        return true;
    }

    public static void Cleanup()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_mutex is not null) {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static async Task ListenForActivation(string name, CancellationToken token)
    {
        while (!token.IsCancellationRequested) {
            try {
                await using var server = new NamedPipeServerStream(name, PipeDirection.In);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server);
                var message = await reader.ReadLineAsync(token);

                if (message == "activate") {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _onActivated?.Invoke());
                }
            }
            catch (OperationCanceledException) {
                break;
            }
            catch (Exception ex) {
                _logger?.LogError(ex, "Error in named pipe listener");
                continue;
            }
        }
    }
}
