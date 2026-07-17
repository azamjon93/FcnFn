// FcnFn v2 — Fn-row remapper for BT keyboards that send media keys + Win-chords
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
// hook plumbing plus the tray/lifecycle wiring: read the event, ask the
// engine, apply the injects.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FcnFn;

internal static unsafe class Program
{
    private static readonly RemapEngine _engine = new();
    private static KeyboardHook? _keyboardHook;
    private static TrayIcon? _tray;
    private static readonly Native.INPUT[] _injectBuf = new Native.INPUT[1];
    private static readonly int _inputSize = Marshal.SizeOf<Native.INPUT>();
    private static readonly LowLevelKeyboardProc _proc = HookCallback; // pin against GC

    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--install":   AutoStart.Register();   return 0;
                case "--uninstall": AutoStart.Unregister(); return 0;
            }
        }

        // Single instance: a second login-time copy must not install a 2nd hook.
        Native.CreateMutexW(IntPtr.Zero, true, "FcnFn.SingleInstance.Mutex");
        if (Marshal.GetLastWin32Error() == Native.ERROR_ALREADY_EXISTS)
            return 0;

        _tray = new TrayIcon(new TrayCallbacks(
            IsEnabled: () => _engine.Enabled,
            ToggleEnabled: () => { _engine.Enabled = !_engine.Enabled; OnEnabledChanged(); },
            IsDiag: () => DiagConsole.Enabled,
            ToggleDiag: DiagConsole.Toggle,
            IsAutoStart: AutoStart.IsRegistered,
            ToggleAutoStart: () =>
            {
                if (AutoStart.IsRegistered()) AutoStart.Unregister();
                else AutoStart.Register();
            },
            Quit: () => Native.PostQuitMessage(0)));

        // Keep the tray icon in sync when Scroll Lock flips Enabled.
        _engine.EnabledChanged += OnEnabledChanged;

        try
        {
            _keyboardHook = new KeyboardHook(_proc);
        }
        catch (Win32Exception ex)
        {
            _tray.ShowBalloon("FcnFn", $"Keyboard hook failed: {ex.Message} (err {ex.NativeErrorCode}). Remapping is off.");
        }

        // Standard Win32 message loop.
        while (Native.GetMessage(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        Cleanup();
        return 0;
    }

    private static void OnEnabledChanged() => _tray?.SetEnabled(_engine.Enabled);

    private static void Cleanup()
    {
        _keyboardHook?.Dispose();
        if (DiagConsole.Enabled) DiagConsole.Toggle();
        _tray?.Dispose();
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Native.CallNextHookEx(_keyboardHook!.Handle, nCode, wParam, lParam);

        ref var kb = ref *(Native.KBDLLHOOKSTRUCT*)lParam;
        int msg = (int)wParam;
        bool isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        bool isMarker = kb.dwExtraInfo == Native.Marker;

        DiagConsole.Log(in kb, isDown, isMarker);

        HookOutcome outcome = _engine.Process(kb.vkCode, isDown, isMarker);

        var ops = _engine.Injects;
        for (int i = 0; i < ops.Length; i++)
            Inject(ops[i].Vk, ops[i].Up);

        return outcome.Swallow
            ? (IntPtr)1
            : Native.CallNextHookEx(_keyboardHook!.Handle, nCode, wParam, lParam);
    }

    private static void Inject(ushort vk, bool up)
    {
        _injectBuf[0] = new Native.INPUT
        {
            type = 1,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)Native.MapVirtualKey(vk, 0),
                    dwFlags = up ? Native.KEYEVENTF_KEYUP : 0,
                    dwExtraInfo = Native.Marker
                }
            }
        };
        if (Native.SendInput(1, _injectBuf, _inputSize) != 1)
            DiagConsole.Error($"SendInput failed vk=0x{vk:X2} err={Marshal.GetLastWin32Error()}");
    }
}
