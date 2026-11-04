using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;

namespace TagMarks.Desktop.ViewModels;

/// <summary>
/// The tooltip and the toast (spec §7) with the timers that hide them. Which message to show,
/// and when, is the owner's decision.
/// </summary>
public sealed partial class NotificationsViewModel : ViewModelBase, IDisposable {
    private readonly SerialDisposable _tooltipDismissTimer = new();
    private readonly SerialDisposable _toastCountdownTimer = new();

    [Reactive] public partial string TooltipMessage { get; private set; } = string.Empty;
    [Reactive] public partial bool IsTooltipVisible { get; private set; }

    [Reactive] public partial string ToastMessage { get; private set; } = string.Empty;
    [Reactive] public partial string ToastActionHint { get; private set; } = string.Empty;
    [Reactive] public partial int ToastCountdown { get; private set; }
    [Reactive] public partial bool IsToastVisible { get; private set; }

    /// <summary>Shows <paramref name="message"/> for 2s; another tooltip replaces it and restarts the 2s.</summary>
    public void ShowTooltip(string message) {
        TooltipMessage = message;
        IsTooltipVisible = true;
        _tooltipDismissTimer.Disposable = Observable
            .Timer(TimeSpan.FromSeconds(2), RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => IsTooltipVisible = false);
    }

    public void HideTooltip() => IsTooltipVisible = false;

    /// <summary>
    /// Shows a toast counting down from <paramref name="durationSeconds"/>. At zero it runs
    /// <paramref name="onExpired"/> if given, or else dismisses itself.
    /// </summary>
    public void ShowToast(string message, string? actionHint = null, int durationSeconds = 5, Action? onExpired = null) {
        ToastMessage = message;
        ToastActionHint = actionHint ?? string.Empty;
        ToastCountdown = durationSeconds;
        IsToastVisible = true;

        _toastCountdownTimer.Disposable = Observable
            .Interval(TimeSpan.FromSeconds(1), RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => {
                ToastCountdown--;
                if (ToastCountdown <= 0) {
                    if (onExpired is not null)
                        onExpired();
                    else
                        DismissToast();
                }
            });
    }

    public void DismissToast() {
        IsToastVisible = false;
        _toastCountdownTimer.Disposable = null;
    }

    /// <summary>True while the toast is on screen showing <paramref name="message"/>.</summary>
    public bool IsToastShowing(string? message) => IsToastVisible && ToastMessage == message;

    public void Dispose() {
        _tooltipDismissTimer.Dispose();
        _toastCountdownTimer.Dispose();
    }
}
