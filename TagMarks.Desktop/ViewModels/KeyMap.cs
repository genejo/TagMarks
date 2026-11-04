using Avalonia.Input;
using TagMarks.Desktop.Services;

namespace TagMarks.Desktop.ViewModels;

/// <summary>
/// What a key press does (spec §3.4). The view model runs the command behind each action; the view marks
/// the key handled unless it is <see cref="PassThrough"/>, and scrolls the help panel itself.
/// </summary>
public enum KeyAction {
    /// <summary>Not handled: the key reaches the focused control (typing, caret moves, text undo).</summary>
    PassThrough,
    /// <summary>Handled with no action ("No action" in spec §3.4), so it doesn't reach the focused control.</summary>
    Swallow,
    HideWindow,
    MoveSelectionUp,
    MoveSelectionDown,
    LaunchSelected,
    ExpandSelected,
    EnterAddMode,
    CancelAddMode,
    ConfirmAdd,
    EnterRenameMode,
    ConfirmRename,
    CancelRename,
    DeleteSelected,
    UndoDelete,
    CopyPayload,
    AddLink,
    ChangePayload,
    SwitchType,
    CollapseExpanded,
    MoveExpandedSelectionUp,
    MoveExpandedSelectionDown,
    LaunchExpandedEntry,
    NavigateExpandedLink,
    ConfirmPanelConfirm,
    ConfirmPanelCancel,
    ShowHelpPanel,
    CloseHelpPanel,
    ScrollHelpLineUp,
    ScrollHelpLineDown,
    ScrollHelpPageUp,
    ScrollHelpPageDown,
    ScrollHelpHome,
    ScrollHelpEnd
}

/// <summary>
/// A key press, with the filter textbox facts that routing depends on.
/// </summary>
/// <param name="Symbol">The character the key types, if any (<see cref="KeyEventArgs.KeySymbol"/>).</param>
/// <param name="FilterCaretAtEnd">The filter caret is at the end of its text, or the text is empty.</param>
/// <param name="FilterHasSelection">The filter textbox has selected text.</param>
public readonly record struct KeyPress(
    Key Key,
    KeyModifiers Modifiers = KeyModifiers.None,
    string? Symbol = null,
    bool FilterCaretAtEnd = true,
    bool FilterHasSelection = false) {
    /// <summary>
    /// True for a key that types a character (no Ctrl/Cmd/Alt), such as "?" or a letter.
    /// </summary>
    public bool IsCharacter =>
        (Modifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) == 0
        && Symbol is { Length: > 0 } symbol
        && !char.IsControl(symbol[0]);
}

/// <summary>
/// The key tables of spec §3.4 in one place: what each key does in each state. Ctrl+X in the spec is the
/// platform action modifier: Cmd on macOS, Ctrl elsewhere.
/// </summary>
public sealed class KeyMap {
    private readonly KeyModifiers _actionModifier;

    public static KeyMap For(bool isMac) => new(PlatformShortcutMap.For(isMac).ActionModifier);

    private KeyMap(KeyModifiers actionModifier) {
        _actionModifier = actionModifier;
    }

    /// <param name="hasPendingUndo">The undo toast is showing, so Ctrl+Z undoes the delete instead of text.</param>
    public KeyAction Route(UiState state, KeyPress key, bool hasPendingUndo) => state.Panel switch {
        UiPanel.Help => RouteHelpPanel(key),
        UiPanel.Confirm => RouteConfirmPanel(key),
        _ => state.Mode switch {
            UiMode.Search => RouteSearch(key, hasPendingUndo),
            UiMode.Expanded => RouteExpanded(key, hasPendingUndo),
            UiMode.Add => RouteAdd(key, hasPendingUndo),
            UiMode.Rename => RouteRename(key),
            _ => KeyAction.PassThrough
        }
    };

    /// <summary>
    /// Routes typed text (TextInput). "?" can't be part of a tagmark name, so it never clashes with filtering;
    /// in ADD and RENAME it goes on to the invalid-character tooltip.
    /// </summary>
    public static KeyAction RouteText(UiState state, string? text) => state switch {
        { Panel: UiPanel.Help } => text == "?" ? KeyAction.CloseHelpPanel : KeyAction.Swallow,
        { IsPrimary: true } when text == "?" => KeyAction.ShowHelpPanel,
        _ => KeyAction.PassThrough
    };

    private KeyAction RouteSearch(KeyPress key, bool hasPendingUndo) => key switch {
        { Key: Key.Escape } => KeyAction.HideWindow,
        { Key: Key.Up } => KeyAction.MoveSelectionUp,
        { Key: Key.Down } => KeyAction.MoveSelectionDown,
        { Key: Key.Enter } => KeyAction.LaunchSelected,
        // With the caret before the end, → moves it.
        { Key: Key.Right, Modifiers: KeyModifiers.None, FilterCaretAtEnd: true } => KeyAction.ExpandSelected,
        { Key: Key.Tab } => KeyAction.EnterAddMode,
        { Key: Key.F2 } => KeyAction.EnterRenameMode,
        _ => RoutePrimary(key, hasPendingUndo)
    };

    private KeyAction RouteExpanded(KeyPress key, bool hasPendingUndo) => key switch {
        { Key: Key.Escape } => KeyAction.HideWindow,
        { Key: Key.Left, Modifiers: KeyModifiers.None } => KeyAction.CollapseExpanded,
        { Key: Key.Up } => KeyAction.MoveExpandedSelectionUp,
        { Key: Key.Down } => KeyAction.MoveExpandedSelectionDown,
        { Key: Key.Enter, Modifiers: KeyModifiers.None } => KeyAction.LaunchExpandedEntry,
        _ when IsAction(key, Key.Enter) => KeyAction.NavigateExpandedLink,
        { Key: Key.Tab or Key.F2 } => KeyAction.Swallow,
        _ => RoutePrimary(key, hasPendingUndo)
    };

    // The keys SEARCH and EXPANDED share: they act on the selected tagmark, or on the highlighted link.
    private KeyAction RoutePrimary(KeyPress key, bool hasPendingUndo) => key switch {
        { Key: Key.F1, Modifiers: KeyModifiers.None } => KeyAction.ShowHelpPanel,
        _ when IsAction(key, Key.D) => KeyAction.DeleteSelected,
        _ when IsAction(key, Key.Z) && hasPendingUndo => KeyAction.UndoDelete,
        // With text selected in the filter, Ctrl+C copies that text.
        _ when IsAction(key, Key.C) && !key.FilterHasSelection => KeyAction.CopyPayload,
        _ when IsAction(key, Key.K) => KeyAction.AddLink,
        _ when IsShiftAction(key, Key.K) => KeyAction.ChangePayload,
        _ when IsAction(key, Key.T) => KeyAction.SwitchType,
        _ => KeyAction.PassThrough
    };

    // The filter textbox holds the new name; ← → and text editing keys reach it.
    private KeyAction RouteAdd(KeyPress key, bool hasPendingUndo) => key switch {
        { Key: Key.Escape or Key.Tab } => KeyAction.CancelAddMode,
        { Key: Key.Enter } => KeyAction.ConfirmAdd,
        _ when IsAction(key, Key.Z) && hasPendingUndo => KeyAction.UndoDelete,
        { Key: Key.Up or Key.Down or Key.F1 or Key.F2 } => KeyAction.Swallow,
        _ when IsAction(key, Key.D) || IsAction(key, Key.C) && !key.FilterHasSelection => KeyAction.Swallow,
        _ => KeyAction.PassThrough
    };

    // The inline rename textbox has focus; text editing keys, including Ctrl+Z, reach it.
    private KeyAction RouteRename(KeyPress key) => key switch {
        { Key: Key.Enter } => KeyAction.ConfirmRename,
        { Key: Key.Escape } => KeyAction.CancelRename,
        { Key: Key.Tab or Key.F1 } => KeyAction.Swallow,
        _ when IsAction(key, Key.D) => KeyAction.Swallow,
        _ => KeyAction.PassThrough
    };

    private static KeyAction RouteConfirmPanel(KeyPress key) => key switch {
        { Key: Key.Enter } => KeyAction.ConfirmPanelConfirm,
        { Key: Key.Escape } => KeyAction.ConfirmPanelCancel,
        _ => KeyAction.Swallow
    };

    private static KeyAction RouteHelpPanel(KeyPress key) => key switch {
        { Key: Key.Escape or Key.F1 } => KeyAction.CloseHelpPanel,
        { Key: Key.Up } => KeyAction.ScrollHelpLineUp,
        { Key: Key.Down } => KeyAction.ScrollHelpLineDown,
        { Key: Key.PageUp } => KeyAction.ScrollHelpPageUp,
        { Key: Key.PageDown } => KeyAction.ScrollHelpPageDown,
        { Key: Key.Home } => KeyAction.ScrollHelpHome,
        { Key: Key.End } => KeyAction.ScrollHelpEnd,
        // Character keys go on to TextInput (RouteText), which swallows them and closes the panel on "?":
        // a handled KeyDown can suppress the TextInput that carries "?".
        { IsCharacter: true } => KeyAction.PassThrough,
        _ => KeyAction.Swallow
    };

    private bool IsAction(KeyPress key, Key expected) =>
        key.Key == expected && key.Modifiers == _actionModifier;

    private bool IsShiftAction(KeyPress key, Key expected) =>
        key.Key == expected && key.Modifiers == (_actionModifier | KeyModifiers.Shift);
}
