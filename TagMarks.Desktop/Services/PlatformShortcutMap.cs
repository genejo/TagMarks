using System.Runtime.InteropServices;

namespace TagMarks.Desktop.Services;

public sealed record PlatformShortcutMap(
    Avalonia.Input.KeyModifiers ActionModifier,
    bool IsMac)
{
    public static PlatformShortcutMap Current { get; } =
        For(RuntimeInformation.IsOSPlatform(OSPlatform.OSX));

    public static PlatformShortcutMap For(bool isMac) =>
        isMac
            ? new(
                Avalonia.Input.KeyModifiers.Meta,
                IsMac: true)
            : new(
                Avalonia.Input.KeyModifiers.Control,
                IsMac: false);
}
