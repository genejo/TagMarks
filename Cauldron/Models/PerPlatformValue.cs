namespace Cauldron.Models;

/// <summary>
/// A config value that is either one string for every platform or a <c>Mac</c>/<c>Windows</c>/<c>Linux</c>
/// mapping, so one config file works on every machine. A platform left out of the mapping is null.
/// Read and written by <see cref="Services.PerPlatformValueYamlConverter"/>, which sets <see cref="Error"/>
/// instead of failing when the YAML has the wrong shape, so a typo never stops the rest of the config loading.
/// </summary>
public sealed record PerPlatformValue {
    public string? Mac { get; set; }
    public string? Windows { get; set; }
    public string? Linux { get; set; }

    /// <summary>What was wrong with the YAML (unknown platform, a list instead of a value, …), or null. Not written back.</summary>
    public string? Error { get; set; }

    public static PerPlatformValue ForAll(string? value) => new() { Mac = value, Windows = value, Linux = value };

    public string? For(HostPlatform platform) => platform switch {
        HostPlatform.Mac => Mac,
        HostPlatform.Windows => Windows,
        _ => Linux
    };
}
