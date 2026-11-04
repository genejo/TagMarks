using System.Collections.Generic;
using System.Linq;

namespace TagMarks.Desktop.Services;

/// <summary>
/// The interaction states a group of shortcuts applies to (spec §3.4 / §9).
/// </summary>
public enum ShortcutScope {
    Global,
    Search,
    Add,
    Expanded,
    Rename,
    ConfirmPanel
}

public sealed record Shortcut(string Gesture, string Description);

public sealed record ShortcutSection(ShortcutScope Scope, string Title, IReadOnlyList<Shortcut> Shortcuts);

/// <summary>
/// Single source of the shortcut labels shown in the app: the context-menu hints
/// (<see cref="ContextMenuShortcuts"/>) and the shortcut help panel (spec §9) both read from here.
/// Labels follow the platform: ⌘ on macOS, Ctrl elsewhere.
/// </summary>
public sealed class ShortcutCatalog {
    private const string Enter = "↩";
    private const string Escape = "Esc";
    private const string Typing = "Typing";

    /// <summary>
    /// The catalog for this platform with the default global hotkey, for the static context-menu hints.
    /// The view model gets the catalog with the configured hotkey from DI (<see cref="ServiceRegistration"/>).
    /// </summary>
    public static ShortcutCatalog Current { get; } = For(PlatformShortcutMap.Current.IsMac);

    /// <param name="summon">The configured global hotkey; null is the platform default double-tap.</param>
    public static ShortcutCatalog For(bool isMac, GlobalHotkey? summon = null) =>
        new(isMac, summon ?? GlobalHotkey.Default(isMac));

    private ShortcutCatalog(bool isMac, GlobalHotkey summon) {
        string mod = isMac ? "⌘" : "Ctrl+";
        string shiftMod = isMac ? "⇧⌘" : "Ctrl+Shift+";

        IsMac = isMac;
        Summon = summon.Label;
        Copy = $"{mod}C";
        ChangePayload = $"{shiftMod}K";
        SwitchType = $"{mod}T";
        AddLink = $"{mod}K";
        Delete = $"{mod}D";
        NavigateLink = $"{mod}{Enter}";
        Undo = $"{mod}Z";

        Sections = [
            new(ShortcutScope.Global, "Global", [
                new(Summon, summon is DoubleTapHotkey ? "Show window (double-tap)" : "Show window")
            ]),
            new(ShortcutScope.Search, "Search", [
                new(Typing, "Filter tagmarks"),
                new("↑ ↓", "Navigate list"),
                new(Activate, "Open URI / copy clip"),
                new("→", "Expand links"),
                new("Tab", "Create tagmark from clipboard"),
                new(Rename, "Rename"),
                new(Delete, "Delete"),
                new(Undo, "Undo delete (while the toast shows)"),
                new(AddLink, "Add link from clipboard"),
                new(ChangePayload, "Change payload from clipboard"),
                new(SwitchType, "Switch type (URI ↔ clip)"),
                new(Copy, "Copy payload"),
                new(Help, "Show shortcuts"),
                new(Escape, "Hide window")
            ]),
            new(ShortcutScope.Add, "Add", [
                new(Typing, "Edit new tagmark name"),
                new(Enter, "Create"),
                new($"{Escape} / Tab", "Cancel")
            ]),
            new(ShortcutScope.Expanded, "Expanded (after →)", [
                new("↑ ↓", "Navigate entries"),
                new(Enter, "Open highlighted URI"),
                new(NavigateLink, "Jump to linked tagmark"),
                new(DeleteEntry, "Remove highlighted link"),
                new(AddLink, "Add link from clipboard"),
                new("←", "Collapse"),
                new(Help, "Show shortcuts"),
                new(Escape, "Hide window")
            ]),
            new(ShortcutScope.Rename, "Rename (after F2)", [
                new(Typing, "Edit tagmark name"),
                new(Enter, "Confirm"),
                new(Escape, "Cancel")
            ]),
            new(ShortcutScope.ConfirmPanel, "Confirm panel (after Add link / Change payload)", [
                new(Enter, "Confirm"),
                new(Escape, "Cancel")
            ])
        ];
    }

    public bool IsMac { get; }
    public string Summon { get; }
    public string Activate => Enter;
    public string Copy { get; }
    public string Rename => "F2";
    public string ChangePayload { get; }
    public string SwitchType { get; }
    public string AddLink { get; }
    public string Delete { get; }
    public string NavigateLink { get; }
    public string DeleteEntry => Delete;
    public string Undo { get; }
    public string Help => "F1 / ?";

    /// <summary>All shortcut groups in spec §9 order.</summary>
    public IReadOnlyList<ShortcutSection> Sections { get; }

    /// <summary>
    /// All groups, with the group for <paramref name="scope"/> first and the rest in spec §9 order.
    /// </summary>
    public IReadOnlyList<ShortcutSection> SectionsStartingWith(ShortcutScope scope) =>
        Sections.Where(s => s.Scope == scope)
            .Concat(Sections.Where(s => s.Scope != scope))
            .ToList();
}
