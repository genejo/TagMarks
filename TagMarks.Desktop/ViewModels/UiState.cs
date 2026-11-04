using System;

namespace TagMarks.Desktop.ViewModels;

/// <summary>
/// The states of the interaction state machine (spec §3.4) that a panel can't open over in turn.
/// HIDDEN is the window's visibility, not a mode.
/// </summary>
public enum UiMode {
    Search,
    Expanded,
    Add,
    Rename
}

/// <summary>
/// CONFIRM_PANEL and HELP_PANEL: they open over SEARCH or EXPANDED, which stays as it was underneath.
/// </summary>
public enum UiPanel {
    None,
    Confirm,
    Help
}

/// <summary>
/// The interaction state (spec §3.4): a mode, plus the panel open over it. A panel opens only over SEARCH or
/// EXPANDED and returns there when it closes, so the invoking state is the mode itself. The default is SEARCH.
/// </summary>
public readonly record struct UiState {
    public static UiState Search { get; } = new(UiMode.Search, UiPanel.None);
    public static UiState Expanded { get; } = new(UiMode.Expanded, UiPanel.None);
    public static UiState Add { get; } = new(UiMode.Add, UiPanel.None);
    public static UiState Rename { get; } = new(UiMode.Rename, UiPanel.None);

    private UiState(UiMode mode, UiPanel panel) {
        Mode = mode;
        Panel = panel;
    }

    public UiMode Mode { get; }
    public UiPanel Panel { get; }

    /// <summary>
    /// SEARCH or EXPANDED with no panel open: the states that open panels and enter the other modes.
    /// </summary>
    public bool IsPrimary => Panel == UiPanel.None && Mode is UiMode.Search or UiMode.Expanded;

    /// <summary>
    /// Opens a panel over this state. Throws unless the state <see cref="IsPrimary"/>.
    /// </summary>
    public UiState WithPanel(UiPanel panel) =>
        IsPrimary ? new UiState(Mode, panel) : throw new InvalidOperationException($"Can't open {panel} over {this}");

    /// <summary>
    /// Closes the open panel: back to the invoking state.
    /// </summary>
    public UiState WithoutPanel() => new(Mode, UiPanel.None);

    public override string ToString() => Panel == UiPanel.None ? Mode.ToString() : $"{Panel} over {Mode}";
}
