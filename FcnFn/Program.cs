// FcnFn v2 — Fn-row remapper for BT keyboards that send media keys + Win-chords
//
// Build (.NET 8+ console project) or: csc /target:exe FcnFn.cs
//
// Usage:
//   FcnFn          -> remap per the tables below. Scroll Lock toggles.
//                     Diagnostics (vk, scancode, flags, injected) can be
//                     toggled on demand via DiagConsole (tray menu).
//
// v2 changes based on real diag data:
//  - Media keys arrive OS-INJECTED (hidserv translates HID consumer usages
//    via SendInput). We now only skip events carrying OUR marker, so these
//    get remapped. LL hooks can swallow injected events.
//  - Chord keys (Shift+Win+F21, Win+Tab, Ctrl+Win+F21, Win+F21) are handled
//    with Start-menu suppression: inject neutral vk 0xFF to dirty the Win
//    press, release chord modifiers logically, emit the F-key, then swallow
//    the physical modifier-up events when they arrive.
//
// The remap decision logic (media map, chord map, modifier bookkeeping,
// Start-menu suppression, active-chord pairing) lives in the pure,
// unit-tested RemapEngine (see RemapEngine.cs). This file is just the Win32
// hook plumbing: read the event, ask the engine, apply the injects.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FcnFn;

internal static unsafe class FcnFn
{
    private static readonly RemapEngine _engine = new();
    private static LowLevelKeyboardProc _proc = HookCallback; // pin against GC
    private static KeyboardHook? _keyboardHook;

    private static void Main(string[] args)
    {
        try
        {
            _keyboardHook = new KeyboardHook(_proc);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        Console.WriteLine("REMAP mode - Scroll Lock toggles on/off. Ctrl+C to quit.");

        while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        _keyboardHook?.Dispose();
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Native.CallNextHookEx(_keyboardHook!.Handle, nCode, wParam, lParam);

        ref var kb = ref *(Native.KBDLLHOOKSTRUCT*)lParam;   // no allocation
        int msg = (int)wParam;
        bool isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        bool isMarker = kb.dwExtraInfo == Native.Marker;

        DiagConsole.Log(in kb, isDown, isMarker);

        HookOutcome outcome = _engine.Process(kb.vkCode, isDown, isMarker);

        var ops = _engine.Injects;
        for (int i = 0; i < ops.Length; i++)
            Inject(ops[i].Vk, ops[i].Up);

        return outcome.Swallow ? (IntPtr)1 : Native.CallNextHookEx(_keyboardHook!.Handle, nCode, wParam, lParam);
    }

    private static readonly Native.INPUT[] _injectBuf = new Native.INPUT[1];

    private static void Inject(ushort vk, bool up)
    {
        _injectBuf[0] = new Native.INPUT
        {
            type = 1, // INPUT_KEYBOARD
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)Native.MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */),
                    dwFlags = up ? Native.KEYEVENTF_KEYUP : 0,
                    dwExtraInfo = Native.Marker
                }
            }
        };
        uint sent = Native.SendInput(1, _injectBuf, Marshal.SizeOf<Native.INPUT>());
        if (sent != 1)
            DiagConsole.Error(
                $"SendInput failed for vk 0x{vk:X2} (err={Marshal.GetLastWin32Error()}, cbSize={Marshal.SizeOf<Native.INPUT>()})");
    }
}
