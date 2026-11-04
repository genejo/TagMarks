using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace TagMarks.Desktop.Services;

/// <summary>
/// macOS-specific implementation: lets the (topmost) window join every Space, including the
/// Space of a fullscreen app, so a summon shows it over that app instead of switching Spaces.
/// </summary>
public class MacWindowFocusService : IWindowFocusService {

    // Objective-C runtime declarations
    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_nuint(IntPtr receiver, IntPtr selector, nuint arg);

    // NSWindowCollectionBehavior flags
    private const nuint CanJoinAllSpaces = 1 << 0;
    private const nuint FullScreenAuxiliary = 1 << 8;

    private bool _collectionBehaviorSet;

    public void BringToForeground(Window window) {
        if (!_collectionBehaviorSet
            && window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle { NSWindow: var nsWindow }
            && nsWindow != IntPtr.Zero) {
            objc_msgSend_nuint(nsWindow, sel_registerName("setCollectionBehavior:"),
                CanJoinAllSpaces | FullScreenAuxiliary);
            _collectionBehaviorSet = true;
        }

        DefaultWindowFocusService.ActivateWindow(window);
    }
}
