using Cauldron.Models;
using Microsoft.Extensions.Logging;

namespace TagMarks.Desktop.Services;

/// <summary>
/// The global hotkey in use, and whether the <c>GlobalHotkey</c> setting was invalid so the default is used
/// instead (the app then shows a hint toast on the next show, spec §3.1).
/// </summary>
public sealed record ResolvedGlobalHotkey(GlobalHotkey Hotkey, bool IsSettingInvalid);

/// <summary>
/// Picks the <c>GlobalHotkey</c> value for the current platform and parses it (spec §3.1, §10.4).
/// </summary>
public static class GlobalHotkeySetting {
    /// <summary>
    /// A missing setting or platform uses the default double-tap. A value that doesn't parse, or a setting
    /// whose YAML had the wrong shape (<see cref="PerPlatformValue.Error"/>, which makes it invalid on every
    /// platform), logs a warning naming the bad part and listing the valid key names, and also uses the default.
    /// </summary>
    public static ResolvedGlobalHotkey Resolve(PerPlatformValue? setting, HostPlatform platform, ILogger logger) {
        bool isMac = platform == HostPlatform.Mac;
        var fallback = GlobalHotkey.Default(isMac);
        if (setting?.Error is { } shapeError)
            return Invalid(string.Empty, shapeError);

        var value = setting?.For(platform);
        if (value is null)
            return new(fallback, IsSettingInvalid: false);

        if (GlobalHotkey.TryParse(value, isMac, out var hotkey, out var error))
            return new(hotkey, IsSettingInvalid: false);

        return Invalid($" \"{value}\" for {platform}", error);

        ResolvedGlobalHotkey Invalid(string what, string why) {
            logger.LogWarning(
                "Invalid GlobalHotkey setting{Value}: {Error}. Using {Default} instead. Valid key names: {KeyNames}",
                what, why, fallback.Label, string.Join(", ", GlobalHotkey.KeyNames));
            return new(fallback, IsSettingInvalid: true);
        }
    }
}
