using System.Runtime.InteropServices;

namespace Cauldron.Models;

/// <summary>
/// The desktop OS the app runs on, for settings that differ per platform (<see cref="PerPlatformValue"/>).
/// </summary>
public enum HostPlatform {
    Mac,
    Windows,
    Linux
}

public static class HostPlatforms {
    public static HostPlatform Current { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? HostPlatform.Mac
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? HostPlatform.Windows
        : HostPlatform.Linux;
}
