using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using SharpHook.Data;

namespace TagMarks.Desktop.Services;

/// <summary>
/// A key the global hotkey refers to by its friendly name (spec §3.1). A name without a side
/// (<c>Alt</c>) matches either side, so it has two <see cref="Codes"/>. <see cref="Mask"/> holds the
/// modifier flags of the key (none for other keys); <see cref="Label"/> is the platform's display text.
/// </summary>
public sealed record HotkeyKey(IReadOnlyList<KeyCode> Codes, EventMask Mask, string Label) {
    public bool IsModifier => Mask != EventMask.None;
}

/// <summary>
/// The configured global hotkey (spec §3.1, config <c>GlobalHotkey</c>): a double-tap of one key or a
/// gesture of modifiers plus one key. <see cref="Label"/> is shown in the help panel and toasts.
/// </summary>
public abstract record GlobalHotkey(string Label) {
    private const string DoubleTapToken = "DoubleTap";

    /// <summary>Double-tap of the platform action key: Left Cmd on macOS, Left Ctrl elsewhere.</summary>
    public static GlobalHotkey Default(bool isMac) =>
        new DoubleTapHotkey(HotkeyKeys.Find(isMac ? "LeftCmd" : "LeftCtrl", isMac)!);

    /// <summary>The documented key names, for the README and the warning on a bad value.</summary>
    public static IReadOnlyList<string> KeyNames => HotkeyKeys.Names;

    /// <summary>
    /// Parses <c>DoubleTap</c>, <c>DoubleTap:&lt;Key&gt;</c> or a gesture such as <c>Mod+Shift+Space</c>,
    /// ignoring case and whitespace. Any key or gesture is accepted; on failure <paramref name="error"/>
    /// names the part that doesn't parse.
    /// </summary>
    public static bool TryParse(string? value, bool isMac,
        [NotNullWhen(true)] out GlobalHotkey? hotkey, [NotNullWhen(false)] out string? error) {
        hotkey = null;
        error = null;
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0) {
            error = "the value is empty";
            return false;
        }

        var colon = text.IndexOf(':');
        var head = (colon < 0 ? text : text[..colon]).Trim();
        if (head.Equals(DoubleTapToken, StringComparison.OrdinalIgnoreCase)) {
            if (colon < 0) {
                hotkey = Default(isMac);
                return true;
            }
            var keyName = text[(colon + 1)..].Trim();
            var key = HotkeyKeys.Find(keyName, isMac);
            if (key is null) {
                error = keyName.Length == 0
                    ? $"'{text}' names no key"
                    : $"unknown key '{keyName}'";
                return false;
            }
            hotkey = new DoubleTapHotkey(key);
            return true;
        }
        if (colon >= 0) {
            error = $"'{head}' is not {DoubleTapToken}";
            return false;
        }

        var parts = text.Split('+').Select(p => p.Trim()).ToList();
        if (parts.Any(p => p.Length == 0)) {
            error = $"'{text}' has an empty key name";
            return false;
        }
        var keys = new List<HotkeyKey>();
        foreach (var part in parts) {
            var key = HotkeyKeys.Find(part, isMac);
            if (key is null) {
                error = parts.Count == 1 ? $"unknown key or value '{part}'" : $"unknown key '{part}'";
                return false;
            }
            keys.Add(key);
        }
        var notModifier = keys.SkipLast(1).Select((k, i) => (k, i)).FirstOrDefault(x => !x.k.IsModifier);
        if (notModifier.k is not null) {
            error = $"'{parts[notModifier.i]}' is not a modifier (only the last key of a gesture can be another key)";
            return false;
        }
        hotkey = new GestureHotkey(keys.SkipLast(1).OrderBy(HotkeyKeys.ModifierOrder).ToList(), keys[^1], isMac);
        return true;
    }
}

/// <summary>Two taps of <see cref="Key"/> within <c>DoubleTapMs</c>, with no other key in between.</summary>
public sealed record DoubleTapHotkey(HotkeyKey Key) : GlobalHotkey($"{Key.Label} {Key.Label}");

/// <summary>
/// <see cref="Key"/> pressed while exactly <see cref="Modifiers"/> are held (one side of each, or the named
/// side for a sided modifier). Labels read <c>⌃⌥T</c> on macOS and <c>Ctrl+Alt+T</c> elsewhere.
/// </summary>
public sealed record GestureHotkey : GlobalHotkey {
    public GestureHotkey(IReadOnlyList<HotkeyKey> modifiers, HotkeyKey key, bool isMac)
        : base(string.Join(isMac ? "" : "+", modifiers.Append(key).Select(k => k.Label))) {
        Modifiers = modifiers;
        Key = key;
    }

    public IReadOnlyList<HotkeyKey> Modifiers { get; }
    public HotkeyKey Key { get; }

    /// <summary>
    /// True when the held modifiers in <paramref name="mask"/> are exactly the required ones. The flag of
    /// <paramref name="pressed"/> itself is ignored, for gestures whose key is a modifier (<c>Ctrl+Shift</c>).
    /// </summary>
    public bool ModifiersMatch(EventMask mask, KeyCode pressed) {
        var held = mask & HotkeyKeys.AllModifiers & ~HotkeyKeys.MaskOf(pressed);
        var allowed = Modifiers.Aggregate(EventMask.None, (all, m) => all | m.Mask);
        return Modifiers.All(m => (held & m.Mask) != EventMask.None) && (held & ~allowed) == EventMask.None;
    }
}

/// <summary>
/// The friendly key names of the <c>GlobalHotkey</c> setting (README key-name table), independent of
/// SharpHook's <c>Vc…</c> names. Lookup ignores case.
/// </summary>
internal static class HotkeyKeys {
    public const EventMask AllModifiers =
        EventMask.Shift | EventMask.Ctrl | EventMask.Alt | EventMask.Meta;

    // Modifier groups in label order (macOS convention ⌃⌥⇧⌘): name, aliases, left/right codes and flags, labels.
    private static readonly (string Name, string[] Aliases, KeyCode Left, KeyCode Right,
        EventMask LeftMask, EventMask RightMask, string Label, string MacLabel)[] Modifiers = [
        ("Ctrl", ["Control"], KeyCode.VcLeftControl, KeyCode.VcRightControl, EventMask.LeftCtrl, EventMask.RightCtrl, "Ctrl", "⌃"),
        ("Alt", ["Option"], KeyCode.VcLeftAlt, KeyCode.VcRightAlt, EventMask.LeftAlt, EventMask.RightAlt, "Alt", "⌥"),
        ("Shift", [], KeyCode.VcLeftShift, KeyCode.VcRightShift, EventMask.LeftShift, EventMask.RightShift, "Shift", "⇧"),
        ("Cmd", ["Win", "Super"], KeyCode.VcLeftMeta, KeyCode.VcRightMeta, EventMask.LeftMeta, EventMask.RightMeta, "Win", "⌘")
    ];

    private static readonly (string Name, KeyCode Code)[] OtherKeys = [
        .. Enumerable.Range('A', 26).Select(c => (((char)c).ToString(), Enum.Parse<KeyCode>($"Vc{(char)c}"))),
        .. Enumerable.Range(0, 10).Select(d => (d.ToString(), Enum.Parse<KeyCode>($"Vc{d}"))),
        .. Enumerable.Range(1, 24).Select(f => ($"F{f}", Enum.Parse<KeyCode>($"VcF{f}"))),
        ("Space", KeyCode.VcSpace), ("Tab", KeyCode.VcTab), ("Escape", KeyCode.VcEscape), ("Esc", KeyCode.VcEscape),
        ("Enter", KeyCode.VcEnter), ("Return", KeyCode.VcEnter), ("Backspace", KeyCode.VcBackspace),
        ("Delete", KeyCode.VcDelete), ("Insert", KeyCode.VcInsert), ("Home", KeyCode.VcHome), ("End", KeyCode.VcEnd),
        ("PageUp", KeyCode.VcPageUp), ("PageDown", KeyCode.VcPageDown),
        ("Up", KeyCode.VcUp), ("Down", KeyCode.VcDown), ("Left", KeyCode.VcLeft), ("Right", KeyCode.VcRight),
        ("CapsLock", KeyCode.VcCapsLock), ("PrintScreen", KeyCode.VcPrintScreen), ("ScrollLock", KeyCode.VcScrollLock),
        ("Pause", KeyCode.VcPause), ("Backquote", KeyCode.VcBackQuote), ("Minus", KeyCode.VcMinus),
        ("Equals", KeyCode.VcEquals), ("OpenBracket", KeyCode.VcOpenBracket), ("CloseBracket", KeyCode.VcCloseBracket),
        ("Backslash", KeyCode.VcBackslash), ("Semicolon", KeyCode.VcSemicolon), ("Quote", KeyCode.VcQuote),
        ("Comma", KeyCode.VcComma), ("Period", KeyCode.VcPeriod), ("Slash", KeyCode.VcSlash)
    ];

    public static IReadOnlyList<string> Names { get; } = [
        .. Modifiers.SelectMany(m => new[] { m.Name }.Concat(m.Aliases)
            .SelectMany(n => new[] { $"Left{n}", $"Right{n}", n })),
        "Mod",
        .. OtherKeys.Select(k => k.Name)
    ];

    /// <summary>The key named <paramref name="name"/> (case-insensitive), or null if there is none.</summary>
    public static HotkeyKey? Find(string name, bool isMac) {
        if (name.Equals("Mod", StringComparison.OrdinalIgnoreCase))
            name = isMac ? "Cmd" : "Ctrl";

        foreach (var m in Modifiers) {
            string label = isMac ? m.MacLabel : m.Label;
            foreach (var n in new[] { m.Name }.Concat(m.Aliases)) {
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    return new([m.Left, m.Right], m.LeftMask | m.RightMask, label);
                if (name.Equals($"Left{n}", StringComparison.OrdinalIgnoreCase))
                    return new([m.Left], m.LeftMask, label);
                if (name.Equals($"Right{n}", StringComparison.OrdinalIgnoreCase))
                    return new([m.Right], m.RightMask, $"Right {label}");
            }
        }

        foreach (var k in OtherKeys) {
            if (name.Equals(k.Name, StringComparison.OrdinalIgnoreCase))
                return new([k.Code], EventMask.None, OtherKeys.First(o => o.Code == k.Code).Name);
        }
        return null;
    }

    /// <summary>The modifier flag of <paramref name="code"/>, or none for a key that is no modifier.</summary>
    public static EventMask MaskOf(KeyCode code) {
        foreach (var m in Modifiers) {
            if (code == m.Left) return m.LeftMask;
            if (code == m.Right) return m.RightMask;
        }
        return EventMask.None;
    }

    /// <summary>Sort key that puts modifiers in label order: Ctrl, Alt, Shift, Cmd.</summary>
    public static int ModifierOrder(HotkeyKey key) =>
        Array.FindIndex(Modifiers, m => (key.Mask & (m.LeftMask | m.RightMask)) != EventMask.None);
}
