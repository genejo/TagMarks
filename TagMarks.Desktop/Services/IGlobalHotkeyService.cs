using System;
using System.Reactive;

namespace TagMarks.Desktop.Services;

/// <summary>
/// Listens for a global hotkey gesture and exposes an observable signal when activated.
/// </summary>
public interface IGlobalHotkeyService : IDisposable {
    /// <summary>
    /// Emits a Unit each time the activation gesture (e.g., double-tap Left Ctrl) is detected.
    /// Observed on the UI thread scheduler.
    /// </summary>
    IObservable<Unit> Activated { get; }

    /// <summary>
    /// Emits once if the hook fails to start or stops with an error (e.g. macOS without
    /// Accessibility permission); the hotkey then no longer works. Replays to late subscribers.
    /// Observed on the UI thread scheduler.
    /// </summary>
    IObservable<Exception> Failed { get; }
}
