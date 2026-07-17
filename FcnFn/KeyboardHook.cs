using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FcnFn;

internal sealed class KeyboardHook : IDisposable
{
    private IntPtr _hook;
    private readonly LowLevelKeyboardProc _proc; // pinned against GC for the hook's lifetime

    public KeyboardHook(LowLevelKeyboardProc callback)
    {
        _proc = callback;
        _hook = Native.SetWindowsHookEx(
            Native.WH_KEYBOARD_LL, _proc,
            Process.GetCurrentProcess().MainModule!.BaseAddress, 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed");
    }

    public IntPtr Handle => _hook;
    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
