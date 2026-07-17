# FcnFn Tray App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the single-file FcnFn keyboard remapper into focused units, optimize its per-keystroke hot path, and turn it into a per-user auto-start system-tray application with a mode-switching menu.

**Architecture:** One native-AOT executable, changed from a console app to a windowed (`WinExe`) app. The remap decision logic is extracted into a pure, allocation-free `RemapEngine` that is unit-tested; all Win32 interaction (hook, tray, GDI icons, console, scheduled task) stays in thin P/Invoke wrappers around it. No WinForms (incompatible with Native AOT); the tray is raw `Shell_NotifyIcon` on a message-only window.

**Tech Stack:** C# / .NET 10, Native AOT (`PublishAot=true`), Win32 P/Invoke (user32, shell32, gdi32, kernel32), `schtasks.exe` for auto-start, xUnit for the logic tests.

## Global Constraints

- Target framework `net10.0` (NOT `net10.0-windows`) — all Windows access is via manual `DllImport`, so no Windows TFM or extra NuGet packages are added.
- `PublishAot=true` must be preserved — no dependency or API that breaks Native AOT (rules out WinForms/WPF and reflection-based Registry helpers; use `schtasks.exe` and raw P/Invoke).
- Remap behavior must be **functionally identical** to the current `FcnFn/Program.cs` — same `MediaMap`, same `ChordMap`, same Start-menu suppression, same modifier tracking. This is a refactor, not a redesign of the mapping.
- No administrative privileges required at runtime or for install (per-user only).
- Injected key events must carry `dwExtraInfo == Marker` (`0xF17F17`); the hook skips only events carrying this marker.
- Windows-only; the hook and `SendInput` require a real interactive session and cannot be exercised headlessly. Interop tasks are verified by build + a manual checklist run by the user on the target machine.

---

### Task 1: Test harness + project flags

**Files:**
- Modify: `FcnFn/FcnFn.csproj`
- Create: `FcnFn/Properties/AssemblyInfo.cs`
- Create: `FcnFn.Tests/FcnFn.Tests.csproj`
- Create: `FcnFn.Tests/SmokeTest.cs`
- Modify: `FcnFn.slnx`

**Interfaces:**
- Produces: a buildable test project referencing `FcnFn` with `internal` visibility, so later tasks can unit-test internal classes.

- [ ] **Step 1: Enable unsafe blocks and keep console output type for now**

Edit `FcnFn/FcnFn.csproj` `<PropertyGroup>` to add `AllowUnsafeBlocks` (needed for the Task 3 pointer read). Leave `OutputType` as `Exe` for now — it flips to `WinExe` in Task 7 once the tray exists, so the app stays runnable during the refactor.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: Expose internals to the test project**

Create `FcnFn/Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FcnFn.Tests")]
```

- [ ] **Step 3: Create the test project**

Create `FcnFn.Tests/FcnFn.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FcnFn\FcnFn.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Add a smoke test**

Create `FcnFn.Tests/SmokeTest.cs`:

```csharp
using Xunit;

public class SmokeTest
{
    [Fact]
    public void Harness_runs()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 5: Add the test project to the solution**

Edit `FcnFn.slnx` to include both projects:

```xml
<Solution>
  <Project Path="FcnFn/FcnFn.csproj" />
  <Project Path="FcnFn.Tests/FcnFn.Tests.csproj" />
</Solution>
```

- [ ] **Step 6: Verify build and test**

Run: `dotnet test`
Expected: PASS — 1 test passed (`Harness_runs`). The `FcnFn` project still builds and runs as before.

- [ ] **Step 7: Commit**

```bash
git add FcnFn/FcnFn.csproj FcnFn/Properties/AssemblyInfo.cs FcnFn.Tests/ FcnFn.slnx
git commit -m "test: add xUnit test project and enable unsafe blocks"
```

---

### Task 2: Extract Native interop into Native.cs

**Files:**
- Create: `FcnFn/Native.cs`
- Modify: `FcnFn/Program.cs` (remove the moved declarations, keep behavior)

**Interfaces:**
- Produces: `internal static class Native` holding every struct, constant, delegate, and `DllImport` currently inline in `Program.cs`, plus the shared `Marker` value.

- [ ] **Step 1: Create Native.cs with all interop moved out of Program.cs**

Create `FcnFn/Native.cs`. Move the structs (`KBDLLHOOKSTRUCT`, `KEYBDINPUT`, `MOUSEINPUT`, `InputUnion`, `INPUT`, `MSG`), the delegate `LowLevelKeyboardProc`, all `DllImport`s, and the constants verbatim from `Program.cs`. Change their access to `internal` and make the structs `internal`.

```csharp
using System.Runtime.InteropServices;

namespace FcnFn;

internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

internal static class Native
{
    // Hook / message constants
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint LLKHF_INJECTED = 0x10;

    // Marker stamped on every key event we inject (recursion guard).
    internal static readonly IntPtr Marker = new(0xF17F17);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi; // sizes the union correctly (32 bytes on x64)
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
```

- [ ] **Step 2: Update Program.cs to use Native and remove the duplicates**

In `FcnFn/Program.cs`, delete all the moved struct/const/DllImport/delegate declarations. Add `namespace FcnFn;` (file-scoped) at the top and qualify the interop uses with `Native.` (e.g. `Native.SetWindowsHookEx`, `Native.WH_KEYBOARD_LL`, `Native.Marker`, `Native.KBDLLHOOKSTRUCT`). Keep `MediaMap`, `ChordMap`, `Chord`, all remap logic, and `Main` exactly as they are for now.

- [ ] **Step 3: Verify build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Verify tests still pass**

Run: `dotnet test`
Expected: PASS — 1 test.

- [ ] **Step 5: Commit**

```bash
git add FcnFn/Native.cs FcnFn/Program.cs
git commit -m "refactor: move Win32 interop into Native.cs"
```

---

### Task 3: Extract the pure RemapEngine (TDD)

> This is the core refactor + optimization, and it is fully test-driven.

**Files:**
- Create: `FcnFn/RemapEngine.cs`
- Create: `FcnFn.Tests/RemapEngineTests.cs`
- Modify: `FcnFn/Program.cs` (hook callback delegates decisions to the engine; add pointer read + reused inject buffer)

**Interfaces:**
- Produces:
  - `internal readonly record struct InjectOp(ushort Vk, bool Up)`
  - `internal readonly record struct HookOutcome(bool Swallow)`
  - `internal sealed class RemapEngine` with:
    - `bool Enabled { get; set; }` (default `true`)
    - `event Action? EnabledChanged`
    - `HookOutcome Process(uint vk, bool isDown, bool isMarker)` — pure decision; fills an internal inject buffer, mutates internal state, allocates nothing on the media/passthrough paths
    - `ReadOnlySpan<InjectOp> Injects` — the ops produced by the most recent `Process` call, in order
- Consumes: nothing from other tasks.

- [ ] **Step 1: Write the failing tests**

Create `FcnFn.Tests/RemapEngineTests.cs`. These encode the exact behavior of today's `HookCallback`.

```csharp
using FcnFn;
using Xunit;

public class RemapEngineTests
{
    private static InjectOp[] Ops(RemapEngine e) => e.Injects.ToArray();

    [Fact]
    public void Marker_events_pass_through_untouched()
    {
        var e = new RemapEngine();
        var r = e.Process(0xAD /*mute*/, isDown: true, isMarker: true);
        Assert.False(r.Swallow);
        Assert.Empty(Ops(e));
    }

    [Fact]
    public void Media_key_down_remaps_to_fkey_down_and_swallows()
    {
        var e = new RemapEngine();
        var r = e.Process(0xAD /*mute*/, isDown: true, isMarker: false);
        Assert.True(r.Swallow);
        Assert.Equal(new[] { new InjectOp(0x70 /*F1*/, false) }, Ops(e));
    }

    [Fact]
    public void Media_key_up_remaps_to_fkey_up()
    {
        var e = new RemapEngine();
        var r = e.Process(0xAF /*vol up*/, isDown: false, isMarker: false);
        Assert.True(r.Swallow);
        Assert.Equal(new[] { new InjectOp(0x72 /*F3*/, true) }, Ops(e));
    }

    [Fact]
    public void Disabled_engine_passes_media_through()
    {
        var e = new RemapEngine { Enabled = false };
        var r = e.Process(0xAD, isDown: true, isMarker: false);
        Assert.False(r.Swallow);
        Assert.Empty(Ops(e));
    }

    [Fact]
    public void ScrollLock_down_toggles_enabled_and_swallows()
    {
        var e = new RemapEngine();
        bool fired = false;
        e.EnabledChanged += () => fired = true;

        var r = e.Process(0x91 /*VK_SCROLL*/, isDown: true, isMarker: false);

        Assert.True(r.Swallow);
        Assert.False(e.Enabled);
        Assert.True(fired);
        Assert.Empty(Ops(e));
    }

    [Fact]
    public void ScrollLock_up_is_swallowed_without_toggle()
    {
        var e = new RemapEngine();
        e.Process(0x91, isDown: true, isMarker: false);  // now disabled
        var r = e.Process(0x91, isDown: false, isMarker: false);
        Assert.True(r.Swallow);
        Assert.False(e.Enabled); // unchanged by the up event
    }

    [Fact]
    public void Win_chord_first_fire_dirties_win_releases_modifiers_then_emits_fkey()
    {
        var e = new RemapEngine();
        // Physically: Win down, then the terminal F21 (0x84) down.
        e.Process(0x5B /*LWin*/, isDown: true, isMarker: false);
        var r = e.Process(0x84 /*F21*/, isDown: true, isMarker: false);

        Assert.True(r.Swallow);
        Assert.Equal(new[]
        {
            new InjectOp(0xFF, false),  // dirty Win press
            new InjectOp(0xFF, true),
            new InjectOp(0x5B, true),   // logical Win up
            new InjectOp(0x7B /*F12*/, false),
        }, Ops(e));
    }

    [Fact]
    public void Win_chord_release_emits_fkey_up()
    {
        var e = new RemapEngine();
        e.Process(0x5B, isDown: true, isMarker: false);
        e.Process(0x84, isDown: true, isMarker: false);   // fires F12 down
        var r = e.Process(0x84, isDown: false, isMarker: false);

        Assert.True(r.Swallow);
        Assert.Equal(new[] { new InjectOp(0x7B /*F12*/, true) }, Ops(e));
    }

    [Fact]
    public void Physical_win_up_after_chord_is_suppressed_once()
    {
        var e = new RemapEngine();
        e.Process(0x5B, isDown: true, isMarker: false);
        e.Process(0x84, isDown: true, isMarker: false);   // ReleaseMod(0x5B) marks LWin up for suppression

        var up = e.Process(0x5B, isDown: false, isMarker: false);
        Assert.True(up.Swallow);   // first physical LWin up swallowed

        // A later, unrelated LWin up passes through normally.
        e.Process(0x5B, isDown: true, isMarker: false);
        var up2 = e.Process(0x5B, isDown: false, isMarker: false);
        Assert.False(up2.Swallow);
    }

    [Fact]
    public void Shift_win_chord_emits_f7()
    {
        var e = new RemapEngine();
        e.Process(0x5B, isDown: true, isMarker: false);
        e.Process(0xA0 /*LShift*/, isDown: true, isMarker: false);
        var r = e.Process(0x84 /*F21*/, isDown: true, isMarker: false);

        Assert.True(r.Swallow);
        // 0xFF x2, release Win (0x5B), release Shift (0xA0), then F7 (0x76).
        Assert.Equal(new[]
        {
            new InjectOp(0xFF, false),
            new InjectOp(0xFF, true),
            new InjectOp(0x5B, true),
            new InjectOp(0xA0, true),
            new InjectOp(0x76 /*F7*/, false),
        }, Ops(e));
    }

    [Fact]
    public void Unmapped_key_passes_through()
    {
        var e = new RemapEngine();
        var r = e.Process(0x41 /*A*/, isDown: true, isMarker: false);
        Assert.False(r.Swallow);
        Assert.Empty(Ops(e));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter RemapEngineTests`
Expected: FAIL to compile — `RemapEngine`, `InjectOp`, `HookOutcome` not defined.

- [ ] **Step 3: Implement RemapEngine**

Create `FcnFn/RemapEngine.cs`. This is a faithful port of the current `HookCallback` decision logic with the actual `SendInput`/`CallNextHookEx` calls removed — it decides, the caller acts.

```csharp
namespace FcnFn;

internal readonly record struct InjectOp(ushort Vk, bool Up);

internal readonly record struct HookOutcome(bool Swallow);

/// <summary>
/// Pure remap decision logic. No Win32 calls: Process() records the key events
/// that should be injected (see <see cref="Injects"/>) and whether the incoming
/// event should be swallowed. This makes the tricky chord / Start-menu
/// suppression state machine unit-testable.
/// </summary>
internal sealed class RemapEngine
{
    // ---- Media-key map (OS-injected VKs from HID consumer usages) ----
    private static readonly Dictionary<uint, ushort> MediaMap = new()
    {
        { 0xAD /* mute       */, 0x70 /* F1 */ },
        { 0xAE /* vol down   */, 0x71 /* F2 */ },
        { 0xAF /* vol up     */, 0x72 /* F3 */ },
        { 0xB1 /* prev track */, 0x73 /* F4 */ },
        { 0xB3 /* play/pause */, 0x74 /* F5 */ },
        { 0xB0 /* next track */, 0x75 /* F6 */ },
    };

    private readonly record struct Chord(uint Vk, bool Win, bool Shift, bool Ctrl);
    private static readonly Dictionary<Chord, ushort> ChordMap = new()
    {
        { new Chord(0x84 /*F21*/, Win: true, Shift: true,  Ctrl: false), 0x76 /* F7  - verify */ },
        { new Chord(0x84 /*F21*/, Win: true, Shift: false, Ctrl: true ), 0x77 /* F8  - verify */ },
        { new Chord(0x09 /*Tab*/, Win: true, Shift: false, Ctrl: false), 0x79 /* F10 */ },
        { new Chord(0x84 /*F21*/, Win: true, Shift: false, Ctrl: false), 0x7B /* F12 */ },
    };

    private const uint VK_SCROLL = 0x91;

    // Injected-op scratch buffer, reused every Process() call (no per-key alloc).
    // Worst case is a first-fire triple-modifier chord: 0xFF down/up (2) +
    // release Win/Shift/Ctrl (3) + F-key down (1) = 6.
    private readonly InjectOp[] _buffer = new InjectOp[8];
    private int _count;

    private bool _win, _shift, _ctrl, _alt;
    private readonly HashSet<uint> _suppressedModUps = new();
    private readonly Dictionary<uint, ushort> _activeChords = new();

    public bool Enabled { get; set; } = true;
    public event Action? EnabledChanged;

    public ReadOnlySpan<InjectOp> Injects => _buffer.AsSpan(0, _count);

    private HookOutcome Emit(ushort vk, bool up)
    {
        _buffer[_count++] = new InjectOp(vk, up);
        return default; // convenience; callers set Swallow explicitly
    }

    private void ReleaseMod(ushort vk)
    {
        _buffer[_count++] = new InjectOp(vk, true);
        _suppressedModUps.Add(vk);            // left variant
        _suppressedModUps.Add((uint)vk + 1);  // right variant (0x5C, 0xA1, 0xA3)
    }

    public HookOutcome Process(uint vk, bool isDown, bool isMarker)
    {
        _count = 0;

        // Skip ONLY our own injections. OS-injected media keys must be processed.
        if (isMarker)
            return new HookOutcome(false);

        // ---- modifier bookkeeping (physical state) ----
        switch (vk)
        {
            case 0x5B:
            case 0x5C: // L/R Win
                _win = isDown;
                if (!isDown && _suppressedModUps.Remove(vk)) return new HookOutcome(true);
                return new HookOutcome(false);
            case 0xA0:
            case 0xA1: // L/R Shift
                _shift = isDown;
                if (!isDown && _suppressedModUps.Remove(vk)) return new HookOutcome(true);
                return new HookOutcome(false);
            case 0xA2:
            case 0xA3: // L/R Ctrl
                _ctrl = isDown;
                if (!isDown && _suppressedModUps.Remove(vk)) return new HookOutcome(true);
                return new HookOutcome(false);
            case 0xA4:
            case 0xA5: // L/R Alt
                _alt = isDown;
                return new HookOutcome(false);
        }

        // ---- toggle: Scroll Lock (swallow both down and up) ----
        if (vk == VK_SCROLL)
        {
            if (isDown)
            {
                Enabled = !Enabled;
                EnabledChanged?.Invoke();
            }
            return new HookOutcome(true);
        }

        if (!Enabled)
            return new HookOutcome(false);

        // ---- media keys (OS-injected consumer usages) ----
        if (MediaMap.TryGetValue(vk, out ushort mediaFk))
        {
            Emit(mediaFk, up: !isDown);
            return new HookOutcome(true);
        }

        // ---- chord terminal keys ----
        if (isDown)
        {
            if (ChordMap.TryGetValue(new Chord(vk, _win, _shift, _ctrl), out ushort fk))
            {
                bool firstFire = !_activeChords.ContainsKey(vk);
                _activeChords[vk] = fk;

                if (firstFire)
                {
                    Emit(0xFF, up: false); // dirty the Win press
                    Emit(0xFF, up: true);
                    if (_win) ReleaseMod(0x5B);
                    if (_shift) ReleaseMod(0xA0);
                    if (_ctrl) ReleaseMod(0xA2);
                }
                Emit(fk, up: false);
                return new HookOutcome(true);
            }
        }
        else if (_activeChords.Remove(vk, out ushort heldFk))
        {
            Emit(heldFk, up: true);
            return new HookOutcome(true);
        }

        return new HookOutcome(false);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter RemapEngineTests`
Expected: PASS — all 12 tests green.

- [ ] **Step 5: Rewire Program.cs's hook callback onto the engine (pointer read + reused buffer)**

In `FcnFn/Program.cs`: delete the inline `MediaMap`, `ChordMap`, `Chord`, `_win/_shift/_ctrl/_alt`, `_suppressedModUps`, `_activeChords`, `ReleaseMod`, and `_remapEnabled`. Add a `static readonly RemapEngine _engine = new();`. Mark the class `unsafe`. Replace the callback body so it reads the struct via pointer (no `Marshal.PtrToStructure` boxing), delegates the decision to `_engine`, and injects using one reused `INPUT[1]` array.

```csharp
private static readonly Native.INPUT[] _injectBuf = new Native.INPUT[1];

private static unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
{
    if (nCode < 0)
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

    ref var kb = ref *(Native.KBDLLHOOKSTRUCT*)lParam;   // no allocation
    int msg = (int)wParam;
    bool isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
    bool isMarker = kb.dwExtraInfo == Native.Marker;

    if (_diag)
        DiagConsole.Log(in kb, isDown, isMarker);   // Task 5 adds DiagConsole

    HookOutcome outcome = _engine.Process(kb.vkCode, isDown, isMarker);

    var ops = _engine.Injects;
    for (int i = 0; i < ops.Length; i++)
        Inject(ops[i].Vk, ops[i].Up);

    return outcome.Swallow ? (IntPtr)1 : Native.CallNextHookEx(_hook, nCode, wParam, lParam);
}

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
                wScan = (ushort)Native.MapVirtualKey(vk, 0),
                dwFlags = up ? Native.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = Native.Marker
            }
        }
    };
    if (Native.SendInput(1, _injectBuf, Marshal.SizeOf<Native.INPUT>()) != 1)
        DiagConsole.Error($"SendInput failed vk=0x{vk:X2} err={Marshal.GetLastWin32Error()}");
}
```

> Note: `DiagConsole.Log` / `DiagConsole.Error` are introduced in Task 5. Until then, temporarily keep the existing `Console.WriteLine`-based diag block and `Console.Error.WriteLine` in `Inject`, and swap them to `DiagConsole` in Task 5. Do NOT leave a call to an undefined type at the end of this task.

- [ ] **Step 6: Verify build and full test run**

Run: `dotnet test`
Expected: PASS — smoke + 12 engine tests. `dotnet build` succeeds.

- [ ] **Step 7: Commit**

```bash
git add FcnFn/RemapEngine.cs FcnFn.Tests/RemapEngineTests.cs FcnFn/Program.cs
git commit -m "refactor: extract pure RemapEngine; pointer read + reused inject buffer"
```

---

### Task 4: Extract KeyboardHook.cs

**Files:**
- Create: `FcnFn/KeyboardHook.cs`
- Modify: `FcnFn/Program.cs`

**Interfaces:**
- Produces: `internal sealed class KeyboardHook : IDisposable` with:
  - `KeyboardHook(LowLevelKeyboardProc callback)` — installs the hook; throws `Win32Exception` on failure
  - `bool IsInstalled { get; }`
  - `IntPtr Handle { get; }`
  - `void Dispose()` — unhooks
- Consumes: `Native` (Task 2).

- [ ] **Step 1: Implement KeyboardHook**

Create `FcnFn/KeyboardHook.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;

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
```

> `Program.cs` still owns the callback (it references `_hook` inside `CallNextHookEx`). Give the callback access to the hook handle via the `KeyboardHook` instance: store `static KeyboardHook? _keyboardHook;` in Program and use `_keyboardHook!.Handle` in place of `_hook` inside `HookCallback`. Full wiring of construction/teardown happens in Task 8; this task only introduces the class and switches the handle source.

- [ ] **Step 2: Verify build and tests**

Run: `dotnet test`
Expected: PASS. `dotnet build` succeeds.

- [ ] **Step 3: Commit**

```bash
git add FcnFn/KeyboardHook.cs FcnFn/Program.cs
git commit -m "refactor: extract KeyboardHook lifetime wrapper"
```

---

### Task 5: DiagConsole.cs (on-demand console)

**Files:**
- Create: `FcnFn/DiagConsole.cs`
- Modify: `FcnFn/Native.cs` (add kernel32 console imports)
- Modify: `FcnFn/Program.cs` (use `DiagConsole` in the callback and `Inject`)

**Interfaces:**
- Produces: `internal static class DiagConsole` with:
  - `bool Enabled { get; }`
  - `void Toggle()` — allocates a console on first enable, frees it on disable (idempotent, guarded)
  - `void Log(in Native.KBDLLHOOKSTRUCT kb, bool isDown, bool isMarker)`
  - `void Error(string message)`
- Consumes: `Native`.

- [ ] **Step 1: Add console P/Invokes to Native.cs**

Append to `Native` in `FcnFn/Native.cs`:

```csharp
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FreeConsole();
```

- [ ] **Step 2: Implement DiagConsole**

Create `FcnFn/DiagConsole.cs`:

```csharp
namespace FcnFn;

internal static class DiagConsole
{
    private static bool _enabled;

    public static bool Enabled => _enabled;

    public static void Toggle()
    {
        if (_enabled)
        {
            Native.FreeConsole();
            _enabled = false;
        }
        else if (Native.AllocConsole())
        {
            // Re-point Console's cached streams at the new console buffer.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stdout);
            _enabled = true;
            Console.WriteLine("DIAG on - vk / scan / flags / ext / inj / msg. Toggle off from the tray.");
        }
    }

    public static void Log(in Native.KBDLLHOOKSTRUCT kb, bool isDown, bool isMarker)
    {
        if (!_enabled) return;
        bool inj = (kb.flags & Native.LLKHF_INJECTED) != 0;
        Console.WriteLine($" 0x{kb.vkCode:X2}    0x{kb.scanCode:X3}   0x{kb.flags:X2}    " +
                          $"{((kb.flags & 1) != 0 ? "E" : " ")}   {(inj ? "I" : " ")}   " +
                          $"{(isDown ? "DOWN" : "UP  ")}{(isMarker ? "  <ours>" : "")}");
    }

    public static void Error(string message)
    {
        if (_enabled) Console.Error.WriteLine(message);
    }
}
```

- [ ] **Step 3: Point Program.cs's callback and Inject at DiagConsole**

Remove the old `_diag` bool and the inline diag `Console.WriteLine` block. The callback now calls `DiagConsole.Log(in kb, isDown, isMarker)` unconditionally (it self-gates on `Enabled`), and `Inject` uses `DiagConsole.Error(...)` (as already shown in Task 3 Step 5). Remove the `diag` command-line branch from `Main` — diagnostics are now a tray toggle.

- [ ] **Step 4: Verify build and tests**

Run: `dotnet test`
Expected: PASS. `dotnet build` succeeds.

- [ ] **Step 5: Commit**

```bash
git add FcnFn/Native.cs FcnFn/DiagConsole.cs FcnFn/Program.cs
git commit -m "feat: on-demand diagnostic console, replacing the diag CLI mode"
```

---

### Task 6: AutoStart.cs (per-user logon task) + TDD on the command builder

**Files:**
- Create: `FcnFn/AutoStart.cs`
- Create: `FcnFn.Tests/AutoStartTests.cs`

**Interfaces:**
- Produces: `internal static class AutoStart` with:
  - `const string TaskName = "FcnFn"`
  - `static string[] CreateArgs(string exePath)` — pure; the `schtasks` argument list to register the logon task
  - `static string[] DeleteArgs()` — pure
  - `static string[] QueryArgs()` — pure
  - `bool IsRegistered()` — runs `schtasks /Query`, returns exit code == 0
  - `void Register()` / `void Unregister()` — run `schtasks`, no elevation
- Consumes: nothing.

- [ ] **Step 1: Write the failing tests for the pure arg builders**

Create `FcnFn.Tests/AutoStartTests.cs`:

```csharp
using FcnFn;
using Xunit;

public class AutoStartTests
{
    [Fact]
    public void CreateArgs_registers_onlogon_limited_and_force()
    {
        var args = AutoStart.CreateArgs(@"C:\Tools\FcnFn.exe");

        Assert.Equal("/Create", args[0]);
        Assert.Contains("/SC", args);
        Assert.Contains("ONLOGON", args);
        Assert.Contains("/RL", args);
        Assert.Contains("LIMITED", args);   // no elevation
        Assert.Contains("/F", args);        // overwrite without prompt

        int tn = Array.IndexOf(args, "/TN");
        Assert.Equal("FcnFn", args[tn + 1]);

        // The run target is the raw exe path; ArgumentList quotes it if needed.
        int tr = Array.IndexOf(args, "/TR");
        Assert.Equal(@"C:\Tools\FcnFn.exe", args[tr + 1]);
    }

    [Fact]
    public void DeleteArgs_targets_the_task_with_force()
    {
        var args = AutoStart.DeleteArgs();
        Assert.Equal("/Delete", args[0]);
        int tn = Array.IndexOf(args, "/TN");
        Assert.Equal("FcnFn", args[tn + 1]);
        Assert.Contains("/F", args);
    }

    [Fact]
    public void QueryArgs_targets_the_task()
    {
        var args = AutoStart.QueryArgs();
        Assert.Equal("/Query", args[0]);
        int tn = Array.IndexOf(args, "/TN");
        Assert.Equal("FcnFn", args[tn + 1]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AutoStartTests`
Expected: FAIL to compile — `AutoStart` not defined.

- [ ] **Step 3: Implement AutoStart**

Create `FcnFn/AutoStart.cs`. Using `schtasks.exe` avoids the Registry API (keeps the plain `net10.0` TFM and AOT-clean) and a per-user `ONLOGON` + `/RL LIMITED` task needs no elevation.

```csharp
using System.Diagnostics;

namespace FcnFn;

internal static class AutoStart
{
    public const string TaskName = "FcnFn";

    // ArgumentList (in Run) quotes each arg as needed, so pass the raw path —
    // pre-quoting it here would double-encode and corrupt the /TR value.
    public static string[] CreateArgs(string exePath) => new[]
    {
        "/Create", "/TN", TaskName, "/TR", exePath,
        "/SC", "ONLOGON", "/RL", "LIMITED", "/F"
    };

    public static string[] DeleteArgs() => new[] { "/Delete", "/TN", TaskName, "/F" };

    public static string[] QueryArgs() => new[] { "/Query", "/TN", TaskName };

    private static int Run(string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    public static bool IsRegistered() => Run(QueryArgs()) == 0;

    public static void Register()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");
        Run(CreateArgs(exe));
    }

    public static void Unregister() => Run(DeleteArgs());
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter AutoStartTests`
Expected: PASS — 3 tests.

- [ ] **Step 5: Commit**

```bash
git add FcnFn/AutoStart.cs FcnFn.Tests/AutoStartTests.cs
git commit -m "feat: per-user logon-task auto-start via schtasks"
```

---

### Task 7: IconFactory.cs + TrayIcon.cs (flip to WinExe)

**Files:**
- Create: `FcnFn/IconFactory.cs`
- Create: `FcnFn/TrayIcon.cs`
- Modify: `FcnFn/Native.cs` (add shell32/gdi32/user32 imports, structs, constants)
- Modify: `FcnFn/FcnFn.csproj` (`OutputType` → `WinExe`)

**Interfaces:**
- Produces:
  - `internal static class IconFactory` with `static IntPtr CreateFnIcon(bool enabled)` — GDI-drawn 16×16 HICON (green "Fn" when enabled, grey when disabled); caller destroys via `Native.DestroyIcon`.
  - `internal sealed class TrayIcon : IDisposable` with:
    - `TrayIcon(TrayCallbacks callbacks)` — creates a message-only window, adds the notify icon
    - `void SetEnabled(bool enabled)` — swaps icon + tooltip
    - `void ShowBalloon(string title, string text)`
    - `void Dispose()` — removes the icon, destroys the window/icons
  - `internal sealed record TrayCallbacks(Func<bool> IsEnabled, Action ToggleEnabled, Func<bool> IsDiag, Action ToggleDiag, Func<bool> IsAutoStart, Action ToggleAutoStart, Action Quit)` — the tray reads current state via the `Func<bool>` getters when building the menu and invokes the `Action`s on click.
- Consumes: `Native`.

- [ ] **Step 1: Add the tray/GDI/menu interop to Native.cs**

Append to `Native` in `FcnFn/Native.cs`:

```csharp
    // ---- window class / message-only window ----
    internal const uint WM_APP = 0x8000;
    internal const uint WM_TRAY = WM_APP + 1;   // our NotifyIcon callback message
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_ENDSESSION = 0x0016;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    // ---- Shell_NotifyIcon ----
    internal const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    internal const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;

    // ---- menu ----
    internal const uint MF_STRING = 0x0000, MF_CHECKED = 0x0008, MF_SEPARATOR = 0x0800;
    internal const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);
    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(IntPtr hMenu, uint flags, UIntPtr idNewItem, string? newItem);
    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ---- GDI for drawing the icon ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] internal static extern uint SetBkColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] internal static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")]
    internal static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    internal const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20;
    internal const int TRANSPARENT = 1;
```

- [ ] **Step 2: Implement IconFactory (GDI-drawn HICON, no binary assets)**

Create `FcnFn/IconFactory.cs`:

```csharp
namespace FcnFn;

internal static class IconFactory
{
    private const int Size = 16;

    // Colors are 0x00BBGGRR (COLORREF).
    private const uint Green = 0x0033AA22;
    private const uint Grey  = 0x00888888;
    private const uint White = 0x00FFFFFF;

    public static IntPtr CreateFnIcon(bool enabled)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr memdc = Native.CreateCompatibleDC(screen);
        IntPtr color = Native.CreateCompatibleBitmap(screen, Size, Size);
        // Monochrome mask: all zero => whole icon opaque.
        IntPtr mask = Native.CreateBitmap(Size, Size, 1, 1, IntPtr.Zero);

        IntPtr oldBmp = Native.SelectObject(memdc, color);

        var rect = new Native.RECT { Left = 0, Top = 0, Right = Size, Bottom = Size };
        IntPtr brush = Native.CreateSolidBrush(enabled ? Green : Grey);
        Native.FillRect(memdc, ref rect, brush);
        Native.DeleteObject(brush);

        Native.SetBkMode(memdc, Native.TRANSPARENT);
        Native.SetTextColor(memdc, White);
        Native.DrawTextW(memdc, "Fn", 2, ref rect,
            Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE);

        Native.SelectObject(memdc, oldBmp);

        var ii = new Native.ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
        IntPtr icon = Native.CreateIconIndirect(ref ii);

        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        Native.DeleteDC(memdc);
        Native.ReleaseDC(IntPtr.Zero, screen);
        return icon;
    }
}
```

- [ ] **Step 3: Implement TrayIcon**

Create `FcnFn/TrayIcon.cs`. Menu command IDs are constants; the `WndProc` routes tray right-clicks to a rebuilt popup menu and menu clicks to the callbacks.

```csharp
namespace FcnFn;

internal sealed record TrayCallbacks(
    Func<bool> IsEnabled, Action ToggleEnabled,
    Func<bool> IsDiag, Action ToggleDiag,
    Func<bool> IsAutoStart, Action ToggleAutoStart,
    Action Quit);

internal sealed class TrayIcon : IDisposable
{
    private const uint ID_ENABLED = 1, ID_DIAG = 2, ID_AUTOSTART = 3, ID_QUIT = 4;
    private const string ClassName = "FcnFnTrayWnd";

    private readonly TrayCallbacks _cb;
    private readonly Native.WndProc _wndProc; // pinned
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private Native.NOTIFYICONDATA _nid;
    private bool _disposed;

    public TrayIcon(TrayCallbacks callbacks)
    {
        _cb = callbacks;
        _wndProc = WndProcImpl;

        IntPtr hInstance = Native.GetModuleHandleW(null);
        var wc = new Native.WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        Native.RegisterClassW(ref wc);

        _hwnd = Native.CreateWindowExW(0, ClassName, "FcnFn", 0, 0, 0, 0, 0,
            Native.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        _icon = IconFactory.CreateFnIcon(_cb.IsEnabled());
        _nid = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = Native.WM_TRAY,
            hIcon = _icon,
            szTip = Tip(_cb.IsEnabled()),
            szInfo = "",
            szInfoTitle = "",
        };
        Native.Shell_NotifyIconW(Native.NIM_ADD, ref _nid);
    }

    private static string Tip(bool enabled) => enabled ? "FcnFn — remap ON" : "FcnFn — remap OFF";

    public void SetEnabled(bool enabled)
    {
        IntPtr old = _icon;
        _icon = IconFactory.CreateFnIcon(enabled);
        _nid.hIcon = _icon;
        _nid.szTip = Tip(enabled);
        _nid.uFlags = Native.NIF_ICON | Native.NIF_TIP;
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref _nid);
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
    }

    public void ShowBalloon(string title, string text)
    {
        _nid.uFlags = Native.NIF_INFO;
        _nid.szInfoTitle = title;
        _nid.szInfo = text;
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref _nid);
    }

    private void ShowMenu()
    {
        IntPtr menu = Native.CreatePopupMenu();
        Native.AppendMenuW(menu, Native.MF_STRING | (_cb.IsEnabled() ? Native.MF_CHECKED : 0),
            ID_ENABLED, "Enabled");
        Native.AppendMenuW(menu, Native.MF_STRING | (_cb.IsDiag() ? Native.MF_CHECKED : 0),
            ID_DIAG, "Diagnostic logging");
        Native.AppendMenuW(menu, Native.MF_STRING | (_cb.IsAutoStart() ? Native.MF_CHECKED : 0),
            ID_AUTOSTART, "Run at login");
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
        Native.AppendMenuW(menu, Native.MF_STRING, ID_QUIT, "Quit");

        Native.GetCursorPos(out var pt);
        Native.SetForegroundWindow(_hwnd); // so the menu dismisses on focus loss
        int cmd = Native.TrackPopupMenu(menu,
            Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);

        switch ((uint)cmd)
        {
            case ID_ENABLED: _cb.ToggleEnabled(); break;
            case ID_DIAG: _cb.ToggleDiag(); break;
            case ID_AUTOSTART: _cb.ToggleAutoStart(); break;
            case ID_QUIT: _cb.Quit(); break;
        }
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_TRAY:
                uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev == Native.WM_RBUTTONUP) ShowMenu();
                else if (ev is Native.WM_LBUTTONUP or Native.WM_LBUTTONDBLCLK) _cb.ToggleEnabled();
                return IntPtr.Zero;

            case Native.WM_ENDSESSION:
                _cb.Quit();
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                _cb.Quit();
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref _nid);
        if (_icon != IntPtr.Zero) { Native.DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
    }
}
```

- [ ] **Step 4: Flip the app to a windowed executable**

Edit `FcnFn/FcnFn.csproj`: change `<OutputType>Exe</OutputType>` to `<OutputType>WinExe</OutputType>` so no console flashes at login.

- [ ] **Step 5: Verify build and tests**

Run: `dotnet test`
Expected: PASS (logic tests unaffected). `dotnet build` succeeds with `WinExe`.

- [ ] **Step 6: Commit**

```bash
git add FcnFn/Native.cs FcnFn/IconFactory.cs FcnFn/TrayIcon.cs FcnFn/FcnFn.csproj
git commit -m "feat: GDI-drawn tray icon and Shell_NotifyIcon menu; WinExe"
```

---

### Task 8: Wire it all together in Program.cs

**Files:**
- Modify: `FcnFn/Program.cs`

**Interfaces:**
- Consumes: `RemapEngine`, `KeyboardHook`, `DiagConsole`, `AutoStart`, `TrayIcon`, `TrayCallbacks`, `Native`.
- Produces: the finished `Main` — CLI install/uninstall branch, single-instance mutex, hook + tray startup, message loop, and full cleanup.

- [ ] **Step 1: Add the mutex P/Invoke to Native.cs**

Append to `Native`:

```csharp
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateMutexW(IntPtr attr, bool initialOwner, string name);
    internal const int ERROR_ALREADY_EXISTS = 183;
```

- [ ] **Step 2: Rewrite Program.cs's Main and lifecycle**

Replace `Main` and the module-level statics. The hook callback and `Inject` stay as finalized in Tasks 3–5. Full file shape:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FcnFn;

internal static unsafe class Program
{
    private static readonly RemapEngine _engine = new();
    private static KeyboardHook? _keyboardHook;
    private static TrayIcon? _tray;
    private static readonly Native.INPUT[] _injectBuf = new Native.INPUT[1];
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
            _tray.ShowBalloon("FcnFn", $"Keyboard hook failed: {ex.Message}. Remapping is off.");
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
        if (Native.SendInput(1, _injectBuf, Marshal.SizeOf<Native.INPUT>()) != 1)
            DiagConsole.Error($"SendInput failed vk=0x{vk:X2} err={Marshal.GetLastWin32Error()}");
    }
}
```

> Guard against a double-cleanup: `WM_ENDSESSION`/`WM_CLOSE` both call `Quit` → `PostQuitMessage`, which ends the loop and reaches `Cleanup()` once. `KeyboardHook.Dispose` and `TrayIcon.Dispose` are already idempotent.

- [ ] **Step 3: Verify build and tests**

Run: `dotnet test`
Expected: PASS. `dotnet build` succeeds.

- [ ] **Step 4: Commit**

```bash
git add FcnFn/Native.cs FcnFn/Program.cs
git commit -m "feat: wire tray, hook, engine, autostart, single-instance into Main"
```

---

### Task 9: AOT publish + manual verification on the target machine

**Files:** none (build/verify only)

- [ ] **Step 1: Publish the native-AOT single file**

Run: `dotnet publish FcnFn/FcnFn.csproj -c Release -r win-x64`
Expected: Build succeeds; a self-contained `FcnFn.exe` is produced under `FcnFn/bin/Release/net10.0/win-x64/publish/`. No AOT trim/analyzer warnings.

- [ ] **Step 2: Manual verification checklist (user runs on the real machine)**

The hook, tray, and auto-start cannot be verified headlessly — run through this list on the target Windows session:

1. Double-click `FcnFn.exe` — no console window appears; a green **Fn** icon appears in the tray.
2. Press the keyboard's Fn-row media keys → they act as F1–F6. Press the Win-chord keys → F7/F8/F10/F12 as mapped.
3. Right-click the tray icon → menu shows **Enabled** (checked), **Diagnostic logging**, **Run at login**, separator, **Quit**.
4. Click **Enabled** → check clears, icon turns grey, remapping stops (media keys behave as media again). Click again → back on.
5. Press **Scroll Lock** → icon toggles grey/green in sync; physical Scroll Lock does nothing else.
6. Click **Diagnostic logging** → a console window appears logging key events; toggle off → it closes.
7. Click **Run at login** → check appears. Open Task Scheduler → a `FcnFn` task with an "At log on" trigger exists, created **without** any UAC/admin prompt. Log out and back in → FcnFn is running (tray icon present).
8. Click **Run at login** again → check clears; the scheduled task is gone.
9. Click **Quit** → tray icon disappears, no `FcnFn.exe` remains in Task Manager, remapping stops.
10. Relaunch and confirm a second double-click does not start a second instance (only one tray icon; one process).

> If step 7 raises a UAC prompt on this machine's policy, the documented fallback is to switch `AutoStart` to the `HKCU\...\Run` key (guaranteed admin-free); note it and continue.

- [ ] **Step 3: Update CLAUDE.md**

Update the repo's `CLAUDE.md` to reflect the new reality: it's now a `WinExe` tray app (not a console app), `diag` is a tray toggle (not a CLI arg), `--install`/`--uninstall` manage the logon task, auto-start is a per-user Scheduled Task, and the code is split across `Native.cs` / `RemapEngine.cs` / `KeyboardHook.cs` / `DiagConsole.cs` / `AutoStart.cs` / `IconFactory.cs` / `TrayIcon.cs` / `Program.cs` with the remap logic unit-tested in `FcnFn.Tests`. Note the new build/run/test/publish commands.

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md for tray-app architecture"
```

---

## Notes for the implementer

- **Task order matters for build-ability.** Tasks 3–5 reference `DiagConsole` before Task 5 creates it; follow the in-task notes (keep temporary `Console.WriteLine`/`Console.Error.WriteLine` until Task 5 swaps them) so every task still compiles.
- **Only `RemapEngine` and `AutoStart` arg-building are unit-testable.** Everything touching the hook, tray, GDI, console, or scheduled task is verified by build + the Task 9 manual checklist — do not fabricate unit tests that can't actually exercise Win32 in the sandbox.
- **COLORREF is `0x00BBGGRR`**, not RGB — the icon color constants in `IconFactory` are already in that order.
- **Do not change the key maps.** `MediaMap` and `ChordMap` are copied verbatim from the original; the "verify" comments on F7/F8 are pre-existing and out of scope.
```