using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Cauldron.Services;

public class ProcessUriLauncher(ILogger<ProcessUriLauncher> logger) : IUriLauncher {
    // `open` and `xdg-open` exit right after handing the URI over. If one is still running after
    // this long it is waiting on the opened app, so the URI was accepted.
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(2);

    public async Task<bool> LaunchAsync(string uri) {
        try {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                // Throws when no handler is registered for the scheme.
                Process.Start(new ProcessStartInfo {
                    FileName = uri,
                    UseShellExecute = true // required to open URL with default browser
                });
                return true;
            }
            string opener = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open";
            using var process = Process.Start(opener, [uri]);
            // A non-zero exit code means no handler for the scheme (or a bad file path).
            using var timeout = new CancellationTokenSource(ExitTimeout);
            try {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) {
                return true;
            }
            if (process.ExitCode == 0) return true;
            logger.LogWarning("{Opener} exited with code {ExitCode} for URI {Uri}", opener, process.ExitCode, uri);
            return false;
        }
        catch (Exception ex) {
            logger.LogError(ex, "Error starting URI process");
            return false;
        }
    }
}
