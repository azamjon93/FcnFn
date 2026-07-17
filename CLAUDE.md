# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows **system-tray application** (`FcnFn`, native-AOT, no console) that remaps the Fn row of a Bluetooth keyboard back to real F1–F12. The keyboard emits its top row as OS-injected media keys (volume/play) and as Win-modifier chords (e.g. `Win+F21`, `Win+Tab`) rather than function keys; the app intercepts those via a low-level keyboard hook and re-emits the intended F-keys. It runs per-user, auto-starting at login, with a tray icon to switch modes.

## Build, test & run

```powershell
dotnet build                 # from repo root; builds FcnFn.slnx (app + tests)
dotnet test                  # runs the FcnFn.Tests xUnit suite
dotnet run --project FcnFn   # launches the tray app (managed build)
```

- **Tests:** only the pure logic is unit-tested — `RemapEngine` (the remap decision state machine) and `AutoStart`'s argument builders, in `FcnFn.Tests`. Everything touching the Win32 hook, tray, GDI, console, or scheduled task cannot be exercised headlessly and is verified by build + manual runtime checks on a real interactive Windows session.
- **CLI flags:** `FcnFn --install` registers the per-user logon auto-start task; `FcnFn --uninstall` removes it. Both return immediately without showing the tray. Any other launch shows the tray and installs the hook.
- **Auto-start needs elevation:** creating/removing the logon Scheduled Task via `schtasks` requires admin, so `--install`/`--uninstall` and the tray's "Run at login" toggle launch `schtasks` elevated via a UAC prompt (`ShellExecute` `runas`). Declining the prompt is handled gracefully (no-op, `ERROR_CANCELLED` swallowed). The remap hook and tray themselves need **no** admin — only enabling auto-start does. (`schtasks /Query` for the checkmark state runs un-elevated.)
- **Target framework** is `net10.0` (NOT `net10.0-windows` — all Windows access is manual P/Invoke) with `PublishAot=true` and `OutputType=WinExe`.

### AOT publish

```powershell
dotnet publish FcnFn/FcnFn.csproj -c Release -r win-x64
```

Native-AOT publish invokes the **MSVC linker**, so it must run from an environment where `vswhere.exe` / `link.exe` are discoverable (a *Visual Studio Developer Command Prompt/PowerShell*, or with the VS C++ build tools on `PATH`). From a plain shell the publish fails at the native link step (`vswhere.exe not recognized`, exit 123) even though the managed `dotnet build` succeeds. Runtime/manual verification can use the ordinary `dotnet build` output exe — AOT is only needed for the final self-contained artifact.

## Architecture

The remap **decision logic** is isolated from all Win32 calls so it can be unit-tested; everything else is a thin P/Invoke wrapper. No WinForms/WPF (incompatible with Native AOT) — the tray UI is raw `Shell_NotifyIcon` on a message-only window.

| File | Responsibility |
|---|---|
| `FcnFn/Program.cs` | Entry point (`internal static unsafe class Program`): CLI `--install`/`--uninstall`, single-instance mutex, wiring, Win32 message loop, `HookCallback`, `Inject`, `Cleanup`. |
| `FcnFn/Native.cs` | All P/Invoke declarations, interop structs, constants, and the injection `Marker`. |
| `FcnFn/RemapEngine.cs` | **Pure** remap decision state machine (`MediaMap`, `ChordMap`, modifier tracking, Start-menu suppression, active-chord pairing). No Win32 calls — returns `HookOutcome` + a buffer of `InjectOp`s. This is the unit-tested core. |
| `FcnFn/KeyboardHook.cs` | `SetWindowsHookEx` install/uninstall lifetime (`IDisposable`). |
| `FcnFn/DiagConsole.cs` | On-demand diagnostic console (`AllocConsole`/`FreeConsole`) + key-event logging. |
| `FcnFn/AutoStart.cs` | Per-user logon Scheduled Task register/unregister/query via `schtasks.exe`. |
| `FcnFn/IconFactory.cs` | GDI-drawn tray icon (green "Fn" enabled, grey disabled) → HICON. |
| `FcnFn/TrayIcon.cs` | `Shell_NotifyIcon`, message-only window + `WndProc`, right-click popup menu, icon-state swap, balloons. |
| `FcnFn.Tests/` | xUnit tests for `RemapEngine` and `AutoStart`. |

### Runtime behavior & modes

Starts hidden, installs the hook, shows the tray icon (which reflects enabled/disabled state). The right-click menu switches modes, all reflecting live state:

- **Enabled** — toggle remapping on/off. Also toggled by **Scroll Lock** (a quick-toggle hotkey; the physical Scroll Lock key is always swallowed so apps never see it) and by left/double-click on the icon.
- **Diagnostic logging** — opens an on-demand console window logging every key event (vk/scancode/flags/injected/ours/down-up). Use this first on a new keyboard to capture the actual VKs before editing the maps. (This replaced the old `diag` command-line mode.)
- **Run at login** — register/unregister this user's logon auto-start task (checked when present). Toggling it triggers a UAC elevation prompt (see "Auto-start needs elevation" above).
- **Quit** — unhook, remove the tray icon, exit.

A named mutex enforces a single instance (a second launch, e.g. a login race, exits immediately). Auto-start is **per-user** (no admin); a machine-wide "all users" install would need elevation and is out of scope.

## How the remapping works (the parts that require reading multiple pieces together)

The hook decision logic lives in `RemapEngine.Process`; `Program.HookCallback` reads the event (via an unsafe pointer, no per-keystroke allocation), asks the engine, applies the injects, and returns `(IntPtr)1` to swallow or falls through to `CallNextHookEx`.

- **Marker-based recursion guard.** Every key the app injects carries `dwExtraInfo == Native.Marker` (`0xF17F17`). The engine skips only events with the marker — it does **not** skip all OS-injected events, because the media keys arrive OS-injected (hidserv translates HID consumer usages via `SendInput`) and still need remapping.
- **Two translation tables** in `RemapEngine`: `MediaMap` (OS-injected media VK → F-key, one-shot re-inject) and `ChordMap` (a `Chord(Vk, Win, Shift, Ctrl)` — terminal key + exact physical modifier pattern → F-key).
- **Physical modifier tracking.** `_win/_shift/_ctrl/_alt` update only from non-marker events, so the engine knows the real modifier state when a chord's terminal key arrives.
- **Start-menu suppression for Win chords.** Firing an F-key out of a `Win+…` chord would otherwise leave the Win key "clean" and open the Start menu on release. In the `firstFire` branch the engine emits a neutral `vk 0xFF` down/up to dirty the Win press, then logically releases the held chord modifiers via `ReleaseMod`, recording both L/R variant VKs in `_suppressedModUps` so the later physical modifier-UP events get swallowed.
- **Chord up-pairing.** `_activeChords` maps a terminal VK to the F-key currently held, so the key-up emits the matching F-key up (and auto-repeat works while held).

## Editing the key maps

- The maps live at the top of `RemapEngine.cs`. VKs are hardware/OS-specific — capture them with the tray's **Diagnostic logging** before adding or changing an entry. Several `ChordMap` entries are still marked "verify" in comments and the intended physical layout (F1..F10 left-to-right) is an assumption.
- Changing map behavior? Add/adjust a `RemapEngine` test — the suite asserts exact `InjectOp` sequences per key event (all four chords and the right-variant modifier suppression are covered), which is the safety net for this tricky state machine.
- Some keys (noted in comments: F9/search, possibly F11/cast) arrive as raw HID consumer usages that produce **no hook-visible events**, so they can't be handled by the current LL-hook approach and would need a Raw Input listener path.
