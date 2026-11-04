# TagMarks

TagMarks is a keyboard-first desktop launcher for named links and clipboard snippets. Summon it with a global double-tap hotkey, search by tag or substring, then open, copy, rename, relink, or retarget entries without leaving the keyboard.

## Features

- **Global summon** — double-tap the platform action key to bring the window to the foreground (`Left Ctrl` on Windows/Linux, `Left Cmd` on macOS), or [configure any other hotkey](#global-hotkey)
- **Two payload types** — store either a URI or clipboard text under a human-readable name
- **Fast filtering** — case-insensitive, whitespace-delimited substring matching with inline highlight feedback
- **Recent-first list** — tagmarks are sorted by last-opened time
- **Keyboard-first management** — add, rename, delete, undo delete, copy payloads, and hide the UI without touching the mouse
- **Clipboard-aware workflows** — create new tagmarks from the current clipboard and change an existing payload directly from the clipboard
- **Linked URIs / expanded mode** — attach secondary URIs to a tagmark, expand it to browse them, open them directly, or jump to another tagmark when a linked URI matches its primary URI
- **Type switching** — convert a tagmark between URI and clip when the payload is valid for the target type
- **Single-instance + tray** — reopening the app activates the existing instance, and the app stays available from the system tray
- **File-backed storage** — tagmarks are stored on disk, external changes are watched and reflected in the UI, and interrupted deletions are recovered on startup

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `↑` / `↓` | Move the selection |
| `Enter` | Open the selected tagmark or confirm the active panel |
| `Tab` | Toggle add mode |
| `F2` | Rename the selected tagmark |
| `Esc` | Cancel the current mode or hide the window |
| `Ctrl/Cmd + C` | Copy the selected payload |
| `Ctrl/Cmd + D` | Delete the selected tagmark |
| `Ctrl/Cmd + Z` | Undo delete while the undo toast is visible |
| `Ctrl/Cmd + K` | Add a linked URI from the clipboard |
| `Ctrl/Cmd + Shift + K` | Change the selected payload from the clipboard |
| `Ctrl/Cmd + T` | Switch the selected tagmark between URI and clip |
| `Right Arrow` | Expand linked entries |
| `Left Arrow` | Collapse expanded entries |
| `Ctrl/Cmd + Enter` | In expanded mode, jump to the tagmark whose primary URI matches the highlighted linked entry |
| `Delete` | In expanded mode, remove the highlighted linked URI |

## Storage and config

- Default tagmarks folder: `~/TagMarks`
- Default config file: `~/TagMarksConfig.yaml`
- A config file that can't be read is left as it is: the defaults are used for that run and a toast says so the first time the window opens. If the tagmarks folder can't be created, an error dialog says why and TagMarks quits
- Configuration includes automatic tag prefix suggestions, the [global hotkey](#global-hotkey) and double-tap timing (`DoubleTapMs`)
- Optional daily backups: set `BackupFolderPath` to get `yyyy-MM-dd_HHmm_tagmarks.zip` there once a day when the tagmarks changed (the 10 newest are kept)

## Global hotkey

Set `GlobalHotkey` in `~/TagMarksConfig.yaml` to change the hotkey that shows the window. It takes effect on restart.

| Value | Hotkey |
| --- | --- |
| `DoubleTap` (default) | Double-tap `Left Cmd` on macOS, `Left Ctrl` on Windows/Linux |
| `DoubleTap:<Key>` | Double-tap any key, e.g. `DoubleTap:LeftAlt`, `DoubleTap:RightCmd`, `DoubleTap:F13` |
| `<Modifier>+…+<Key>` | A gesture, e.g. `Mod+Shift+Space`, `Ctrl+Alt+T`, `F13` |

Both taps of a double-tap must fall within `DoubleTapMs` (default 400 ms), with no other key in between. A gesture fires when its key goes down while exactly those modifiers are held. `Mod` is `Cmd` on macOS and `Ctrl` on Windows/Linux.

Any key or gesture is accepted, including ones other apps or the OS already use (double `Shift` in JetBrains IDEs, `Cmd+Space`, `Alt` on Windows); the focused app still receives the keys. An invalid value (including an unknown platform name such as `Win:` in the per-platform form) logs a warning, falls back to `DoubleTap` and shows a toast the next time the window opens; the rest of the config is still loaded.

```yaml
GlobalHotkey: DoubleTap:LeftAlt     # the same on every platform
```

```yaml
GlobalHotkey:                       # one value per platform
  Mac: DoubleTap:LeftCmd
  Windows: DoubleTap:LeftAlt
  Linux: Ctrl+Alt+Space             # a platform left out uses DoubleTap
```

Key names (case does not matter):

| Key | Left | Right | Either side |
| --- | --- | --- | --- |
| Ctrl | `LeftCtrl` | `RightCtrl` | `Ctrl` (alias `Control`) |
| Alt / Option | `LeftAlt`, `LeftOption` | `RightAlt`, `RightOption` | `Alt`, `Option` |
| Shift | `LeftShift` | `RightShift` | `Shift` |
| Cmd / Win / Super | `LeftCmd`, `LeftWin`, `LeftSuper` | `RightCmd`, `RightWin`, `RightSuper` | `Cmd`, `Win`, `Super` |
| Platform action key | | | `Mod` (`Cmd` on macOS, `Ctrl` elsewhere) |

| Other keys | Names |
| --- | --- |
| Letters and digits | `A`–`Z`, `0`–`9` |
| Function keys | `F1`–`F24` |
| Whitespace and editing | `Space`, `Tab`, `Enter` (`Return`), `Escape` (`Esc`), `Backspace`, `Delete`, `Insert` |
| Navigation | `Up`, `Down`, `Left`, `Right`, `Home`, `End`, `PageUp`, `PageDown` |
| Lock and system | `CapsLock`, `ScrollLock`, `PrintScreen`, `Pause` |
| Punctuation | `Backquote`, `Minus`, `Equals`, `OpenBracket`, `CloseBracket`, `Backslash`, `Semicolon`, `Quote`, `Comma`, `Period`, `Slash` |

## Tech

**Built with:** .NET 10 · Avalonia UI 12 · ReactiveUI 26 · DynamicData

Building from source needs the .NET 10 SDK:

```bash
dotnet build TagMarks.Desktop
```

## License

[MIT](LICENSE)
