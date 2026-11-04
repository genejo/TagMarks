using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace TagMarks.Desktop.Services;

/// <summary>
/// Windows-specific implementation that uses P/Invoke to reliably steal focus
/// from the current foreground window (works around SetForegroundWindow restrictions).
/// </summary>
public class Win32WindowFocusService : IWindowFocusService {
    
    // Win32 API declarations
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    private const int SwRestore = 9;
    private const int InputMouse = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public void BringToForeground(Window window) {
        IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        if (hwnd == IntPtr.Zero) {
            // Can't get native handle; fall back to cross-platform approach.
            DefaultWindowFocusService.ActivateWindow(window);
            return;
        }

        // Restore if minimized
        if (IsIconic(hwnd)) {
            ShowWindow(hwnd, SwRestore);
        }

        // Attach to the foreground thread so SetForegroundWindow is allowed
        IntPtr foregroundWindow = GetForegroundWindow();
        uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        uint currentThreadId = GetCurrentThreadId();

        bool attached = false;
        if (foregroundThreadId != currentThreadId) {
            attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
        }

        // PowerToys-style SendInput hack: send empty mouse input to bypass restrictions
        INPUT[] inputs = [new INPUT { type = InputMouse, mi = new MOUSEINPUT() }];
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));

        SetForegroundWindow(hwnd);

        if (attached) {
            AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }

        DefaultWindowFocusService.ActivateWindow(window);
    }
}
