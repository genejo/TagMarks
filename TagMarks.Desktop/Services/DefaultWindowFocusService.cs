using Avalonia.Controls;

namespace TagMarks.Desktop.Services;

/// <summary>
/// Cross-platform fallback: uses Avalonia's built-in Show/Activate. The window stays above
/// other windows through <c>Topmost="True"</c> in <c>MainWindowView.axaml</c>.
/// </summary>
public class DefaultWindowFocusService : IWindowFocusService {
    public void BringToForeground(Window window) {
        ActivateWindow(window);
    }

    /// <summary>
    /// Shared activation logic used by both the default and Win32 implementations.
    /// </summary>
    internal static void ActivateWindow(Window window) {
        window.Show();
        window.Activate();
    }
}
