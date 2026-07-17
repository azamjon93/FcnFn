// FcnFn v2 — Fn-row remapper for BT keyboards that send media keys + Win-chords
//
// Build (.NET 8+ console project) or: csc /target:exe FcnFn.cs
//
// Usage:
//   FcnFn diag      -> log every key event (vk, scancode, flags, injected)
//   FcnFn remap     -> remap per the tables below. Scroll Lock toggles.
//
// v2 changes based on real diag data:
//  - Media keys arrive OS-INJECTED (hidserv translates HID consumer usages
//    via SendInput). We now only skip events carrying OUR marker, so these
//    get remapped. LL hooks can swallow injected events.
//  - Chord keys (Shift+Win+F21, Win+Tab, Ctrl+Win+F21, Win+F21) are handled
//    with Start-menu suppression: inject neutral vk 0xFF to dirty the Win
//    press, release chord modifiers logically, emit the F-key, then swallow
//    the physical modifier-up events when they arrive.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FcnFn;

internal static class FcnFn
{
    // ---- Media-key map (OS-injected VKs from HID consumer usages) --------
    // CONFIRM physical layout: assumed you pressed F1..F10 left-to-right.
    private static readonly Dictionary<uint, ushort> MediaMap = new()
    {
        { 0xAD /* mute       */, 0x70 /* F1 */ },
        { 0xAE /* vol down   */, 0x71 /* F2 */ },
        { 0xAF /* vol up     */, 0x72 /* F3 */ },
        { 0xB1 /* prev track */, 0x73 /* F4 */ },
        { 0xB3 /* play/pause */, 0x74 /* F5 */ },
        { 0xB0 /* next track */, 0x75 /* F6 */ },
    };

    // ---- Chord map: terminal key + exact modifier pattern -> F-key -------
    private readonly record struct Chord(uint Vk, bool Win, bool Shift, bool Ctrl);
    private static readonly Dictionary<Chord, ushort> ChordMap = new()
    {
        { new Chord(0x84 /*F21*/, Win: true, Shift: true,  Ctrl: false), 0x76 /* F7  (brightness down key) - verify */ },
        { new Chord(0x84 /*F21*/, Win: true, Shift: false, Ctrl: true ), 0x77 /* F8  (brightness up key)   - verify */ },
        { new Chord(0x09 /*Tab*/, Win: true, Shift: false, Ctrl: false), 0x79 /* F10 (task view key)       */ },
        { new Chord(0x84 /*F21*/, Win: true, Shift: false, Ctrl: false), 0x7B /* F12 (settings key) - CONFIRMED */ },
        // F9 (search) and possibly F11 (cast) emit no hook-visible events:
        // they are raw HID consumer usages -> need the Raw Input listener path.
    };
    // ----------------------------------------------------------------------

    private static bool _diag;
    private static bool _remapEnabled = true;
    private static LowLevelKeyboardProc _proc = HookCallback; // pin against GC
    private static IntPtr _hook = IntPtr.Zero;

    // Physical modifier state (tracked from non-marker events only)
    private static bool _win, _shift, _ctrl, _alt;
    // Modifier VKs whose next physical UP must be swallowed (we already
    // injected their logical UP when a chord fired)
    private static readonly HashSet<uint> _suppressedModUps = new();
    // terminal vk -> F-key currently held (to pair the UP event)
    private static readonly Dictionary<uint, ushort> _activeChords = new();

    private static void Main(string[] args)
    {
        _diag = args.Length > 0 && args[0].Equals("diag", StringComparison.OrdinalIgnoreCase);

        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc,
            Process.GetCurrentProcess().MainModule!.BaseAddress, 0);
        if (_hook == IntPtr.Zero)
        {
            Console.Error.WriteLine($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        Console.WriteLine(_diag
            ? "DIAG mode - press keys; Ctrl+C to quit.\n vk      scan    flags   ext inj  msg"
            : "REMAP mode - Scroll Lock toggles on/off. Ctrl+C to quit.");

        while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        Native.UnhookWindowsHookEx(_hook);
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var kb = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        int msg = (int)wParam;
        bool isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        bool ours = kb.dwExtraInfo == Native.Marker;

        if (_diag)
        {
            bool inj = (kb.flags & Native.LLKHF_INJECTED) != 0;
            Console.WriteLine($" 0x{kb.vkCode:X2}    0x{kb.scanCode:X3}   0x{kb.flags:X2}    " +
                              $"{((kb.flags & 1) != 0 ? "E" : " ")}   {(inj ? "I" : " ")}   " +
                              $"{(isDown ? "DOWN" : "UP  ")}{(ours ? "  <ours>" : "")}");
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // Skip ONLY our own injections. OS-injected media keys must be processed.
        if (ours)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        // --- modifier bookkeeping (physical state) ---
        switch (kb.vkCode)
        {
            case 0x5B:
            case 0x5C: // L/R Win
                _win = isDown;
                if (!isDown && _suppressedModUps.Remove(kb.vkCode)) return (IntPtr)1;
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            case 0xA0:
            case 0xA1: // L/R Shift
                _shift = isDown;
                if (!isDown && _suppressedModUps.Remove(kb.vkCode)) return (IntPtr)1;
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            case 0xA2:
            case 0xA3: // L/R Ctrl
                _ctrl = isDown;
                if (!isDown && _suppressedModUps.Remove(kb.vkCode)) return (IntPtr)1;
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            case 0xA4:
            case 0xA5: // L/R Alt
                _alt = isDown;
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // --- toggle: Scroll Lock (single key, no chord). Both down and up
        //     are swallowed so the OS scroll-lock state (and apps like Excel)
        //     never see it. ---
        if (kb.vkCode == 0x91 /* VK_SCROLL */)
        {
            if (isDown)
            {
                _remapEnabled = !_remapEnabled;
                Console.WriteLine($"Remap {(_remapEnabled ? "ON" : "OFF")}");
            }
            return (IntPtr)1;
        }

        if (!_remapEnabled)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        // --- media keys (OS-injected consumer usages) ---
        if (MediaMap.TryGetValue(kb.vkCode, out ushort mediaFk))
        {
            Inject(mediaFk, up: !isDown);
            return (IntPtr)1;
        }

        // --- chord terminal keys ---
        if (isDown)
        {
            if (ChordMap.TryGetValue(new Chord(kb.vkCode, _win, _shift, _ctrl), out ushort fk))
            {
                bool firstFire = !_activeChords.ContainsKey(kb.vkCode);
                _activeChords[kb.vkCode] = fk;

                if (firstFire)
                {
                    // 1) Dirty the Win press so releasing Win won't open Start.
                    Inject(0xFF, up: false);
                    Inject(0xFF, up: true);
                    // 2) Logically release chord modifiers so apps see a bare F-key,
                    //    and mark their physical UPs for suppression.
                    if (_win) ReleaseMod(0x5B);
                    if (_shift) ReleaseMod(0xA0);
                    if (_ctrl) ReleaseMod(0xA2);
                }
                // 3) The actual F-key (repeats naturally on auto-repeat).
                Inject(fk, up: false);
                return (IntPtr)1;
            }
        }
        else if (_activeChords.Remove(kb.vkCode, out ushort heldFk))
        {
            Inject(heldFk, up: true);
            return (IntPtr)1;
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void ReleaseMod(ushort vk)
    {
        Inject(vk, up: true);
        _suppressedModUps.Add(vk);           // left variant
        _suppressedModUps.Add((uint)vk + 1); // right variant (0x5C, 0xA1, 0xA3)
    }

    private static void Inject(ushort vk, bool up)
    {
        var input = new Native.INPUT
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
        uint sent = Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
        if (sent != 1)
            Console.Error.WriteLine(
                $"SendInput failed for vk 0x{vk:X2} (err={Marshal.GetLastWin32Error()}, cbSize={Marshal.SizeOf<Native.INPUT>()})");
    }
}