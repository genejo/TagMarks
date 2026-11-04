using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI.Reactive;
using SharpHook.Reactive;

namespace TagMarks.Desktop.Services;

/// <summary>
/// Detects the configured <see cref="GlobalHotkey"/> via SharpHook (a double-tap of one key, by default the
/// platform action key: Left Ctrl on Windows/Linux, Left Cmd on macOS, or a gesture) and emits on
/// <see cref="Activated"/>. The hook only observes keys; the focused app still receives them.
/// Owns the <see cref="IReactiveGlobalHook"/> lifecycle.
/// </summary>
public class GlobalHotkeyService : IGlobalHotkeyService {
    private readonly IReactiveGlobalHook _hook;
    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly AsyncSubject<Exception> _failed = new();

    public IObservable<Unit> Activated { get; }

    public IObservable<Exception> Failed { get; }

    public GlobalHotkeyService(ILogger<GlobalHotkeyService> logger, GlobalHotkey? hotkey = null, uint doubleTapMs = 400)
        : this(new ReactiveGlobalHook(), logger, hotkey, doubleTapMs) { }

    /// <summary>
    /// Accepts an injected hook for testing. A null <paramref name="hotkey"/> is the platform default double-tap.
    /// </summary>
    public GlobalHotkeyService(IReactiveGlobalHook hook, ILogger<GlobalHotkeyService> logger,
        GlobalHotkey? hotkey = null, uint doubleTapMs = 400) {
        _hook = hook;
        _logger = logger;
        Failed = _failed.ObserveOn(RxSchedulers.MainThreadScheduler);

        hotkey ??= GlobalHotkey.Default(PlatformShortcutMap.Current.IsMac);
        var matches = hotkey switch {
            DoubleTapHotkey doubleTap => DoubleTaps(doubleTap.Key, doubleTapMs),
            GestureHotkey gesture => Gestures(gesture),
            _ => throw new ArgumentOutOfRangeException(nameof(hotkey), hotkey, "Unknown hotkey kind")
        };

        Activated = matches
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Publish()
            .RefCount();

        ObserveRun(StartHook());
    }

    /// <summary>
    /// Emits on each double-tap of <paramref name="key"/> (either of its codes).
    /// </summary>
    private IObservable<Unit> DoubleTaps(HotkeyKey key, uint doubleTapMs) {
        double tapWindow = Math.Clamp(doubleTapMs, 100, 1000);
        double cooldown = tapWindow + 100;

        // Merge all key events into a timestamped stream:
        //   activation press = 1, activation release = -1, any other key = 0 (poison).
        // A poison event in the sliding window breaks the double-tap pattern,
        // preventing activation when other KEYS are pressed between taps.
        var keyEvents = _hook.KeyPressed
            .Select(e => key.Codes.Contains(e.Data.KeyCode) ? 1 : 0)
            .Merge(
                _hook.KeyReleased
                    .Select(e => key.Codes.Contains(e.Data.KeyCode) ? -1 : 0)
            )
            .Timestamp(RxSchedulers.TaskpoolScheduler);

        // Double-tap detection: sliding window of 4 events (down-up-down-up) within tapWindow ms.
        // A Scan-based cooldown suppresses repeated matches for cooldown ms after each activation,
        // preventing a triple-tap or overlapping sliding windows from firing twice.
        // Scan runs sequentially on the hook thread — no cross-thread race.
        return keyEvents
            .Buffer(4, 1)
            .Where(buffer => buffer.Count == 4 &&
                             buffer[0].Value == 1 &&    // first press
                             buffer[1].Value == -1 &&   // first release
                             buffer[2].Value == 1 &&    // second press
                             buffer[3].Value == -1 &&   // second release
                             (buffer[2].Timestamp - buffer[0].Timestamp).TotalMilliseconds <= tapWindow)
            .Select(buffer => buffer[3].Timestamp)
            .Scan((last: (DateTimeOffset?)null, emit: false),
                  (state, ts) => state.last is null || (ts - state.last.Value).TotalMilliseconds > cooldown
                      ? (last: ts, emit: true)
                      : (state.last, emit: false))
            .Where(x => x.emit)
            .Select(_ => Unit.Default);
    }

    /// <summary>
    /// Emits when the gesture key goes down with exactly the gesture's modifiers held. Auto-repeat sends
    /// more presses without a release; only the first press after a release counts, so holding the
    /// key doesn't fire again. The modifiers come from the event's mask, which the OS keeps current.
    /// </summary>
    private IObservable<Unit> Gestures(GestureHotkey gesture) =>
        _hook.KeyPressed.Select(e => (pressed: true, e))
            .Merge(_hook.KeyReleased.Select(e => (pressed: false, e)))
            .Where(x => gesture.Key.Codes.Contains(x.e.Data.KeyCode))
            .Scan((held: false, fire: false), (state, x) => x.pressed
                ? (held: true, fire: !state.held && gesture.ModifiersMatch(x.e.RawEvent.Mask, x.e.Data.KeyCode))
                : (held: false, fire: false))
            .Where(x => x.fire)
            .Select(_ => Unit.Default);

    /// <summary>
    /// Starts the hook. A synchronous throw is turned into a faulted task, so both
    /// failure paths are handled by <see cref="ObserveRun"/>.
    /// </summary>
    private Task StartHook() {
        try {
            return _hook.RunAsync();
        } catch (Exception ex) {
            return Task.FromException(ex);
        }
    }

    /// <summary>
    /// Logs a hook failure and emits it on <see cref="Failed"/>. A normal stop (on dispose) emits nothing.
    /// </summary>
    private void ObserveRun(Task run) {
        run.ContinueWith(t => {
            if (t.IsFaulted) {
                var ex = t.Exception!.GetBaseException();
                _logger.LogError(ex, "Global hotkey hook failed; the hotkey won't work until the app restarts");
                _failed.OnNext(ex);
            }
            _failed.OnCompleted();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose() {
        _hook.Dispose();
    }
}
