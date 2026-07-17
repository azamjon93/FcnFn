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

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi; // sizes the union correctly (32 bytes on x64)
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int ptX, ptY;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private static readonly IntPtr Marker = new IntPtr(0xF17F17);

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

        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            Process.GetCurrentProcess().MainModule!.BaseAddress, 0);
        if (_hook == IntPtr.Zero)
        {
            Console.Error.WriteLine($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        Console.WriteLine(_diag
            ? "DIAG mode - press keys; Ctrl+C to quit.\n vk      scan    flags   ext inj  msg"
            : "REMAP mode - Scroll Lock toggles on/off. Ctrl+C to quit.");

        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(_hook);
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int msg = (int)wParam;
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool ours = kb.dwExtraInfo == Marker;

        if (_diag)
        {
            bool inj = (kb.flags & LLKHF_INJECTED) != 0;
            Console.WriteLine($" 0x{kb.vkCode:X2}    0x{kb.scanCode:X3}   0x{kb.flags:X2}    " +
                              $"{((kb.flags & 1) != 0 ? "E" : " ")}   {(inj ? "I" : " ")}   " +
                              $"{(isDown ? "DOWN" : "UP  ")}{(ours ? "  <ours>" : "")}");
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // Skip ONLY our own injections. OS-injected media keys must be processed.
        if (ours)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        // --- modifier bookkeeping (physical state) ---
        switch (kb.vkCode)
        {
            case 0x5B:
            case 0x5C: // L/R Win
                _win = isDown;
                if (!isDown && _suppressedModUps.Remove(kb.vkCode)) return (IntPtr)1;
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            case 0xA0:
            case 0xA1: // L/R Shift
                _shift = isDown;
                if (!isDown && _suppressedModUps.Remove(kb.vkCode)) return (IntPtr)1;
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            case 0xA2:
            case 0xA3: // L/R Ctrl
                _ctrl = isDown;
                if (!isDown && _suppressedModUps.Remove(kb.vkCode)) return (IntPtr)1;
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            case 0xA4:
            case 0xA5: // L/R Alt
                _alt = isDown;
                return CallNextHookEx(_hook, nCode, wParam, lParam);
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
            return CallNextHookEx(_hook, nCode, wParam, lParam);

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

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void ReleaseMod(ushort vk)
    {
        Inject(vk, up: true);
        _suppressedModUps.Add(vk);           // left variant
        _suppressedModUps.Add((uint)vk + 1); // right variant (0x5C, 0xA1, 0xA3)
    }

    private static void Inject(ushort vk, bool up)
    {
        var input = new INPUT
        {
            type = 1, // INPUT_KEYBOARD
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */),
                    dwFlags = up ? KEYEVENTF_KEYUP : 0,
                    dwExtraInfo = Marker
                }
            }
        };
        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (sent != 1)
            Console.Error.WriteLine(
                $"SendInput failed for vk 0x{vk:X2} (err={Marshal.GetLastWin32Error()}, cbSize={Marshal.SizeOf<INPUT>()})");
    }
}