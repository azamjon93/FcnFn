# FcnFn — Per-User Auto-Start Tray App

**Date:** 2026-07-17
**Status:** Approved design, pending implementation plan

## Summary

FcnFn is a keyboard remapper for Bluetooth keyboards whose Fn row emits media keys
and Win-modifier chords instead of real F1–F12. The current version is a working
single-file console app (`FcnFn/Program.cs`) driven by a `WH_KEYBOARD_LL` hook.

This work refactors that single file into focused units, applies two hot-path
optimizations, and converts the program from a console app into a **per-user,
auto-start system-tray application** that lets the user switch modes from the tray.

## Why not a Windows Service

The original request was "convert it to a Windows Service that runs for each user at
login with a tray icon." A literal Windows Service cannot satisfy this, for two
independent reasons:

1. **Session 0 isolation** — services run in Session 0, isolated from every
   interactive user desktop since Windows Vista. A service cannot display a system
   tray icon; there is no interactive desktop in Session 0 to display it on.
2. **Hook scope** — a low-level keyboard hook (`WH_KEYBOARD_LL`) installed from
   Session 0 does not receive the keystrokes of a logged-in interactive user, so a
   service could not perform the remapping at all.

The app requires **no administrative privileges** — a low-level keyboard hook works
from an ordinary user process. A service therefore adds nothing and breaks both the
tray icon and the hook. The correct pattern for "runs for each user at login, with a
tray icon" is a **per-user auto-start tray application** (the model used by PowerToys,
AutoHotkey trays, etc.). This design adopts that pattern.

## Goals

- Preserve the existing remap behavior exactly (same media/chord → F-key mappings,
  same Start-menu suppression, same modifier handling).
- Refactor the one large file into small, independently understandable units.
- Optimize the per-keystroke hot path (no allocation per key event).
- Run hidden with a system-tray icon; no console window flash at login.
- Provide a tray menu to switch modes: Enabled/Disabled, Diagnostic logging,
  Run at login, and Quit.
- Auto-start in each user's session at login, self-service (no admin).

## Non-goals

- Machine-wide "all users at once" installation (would need admin; per-user only).
- Changing which physical keys map to which F-keys.
- A configuration UI or editable key maps at runtime (maps stay compile-time).
- Cross-platform support (Windows-only by nature of the Win32 hook).

## Architecture

Remains a **single native-AOT executable** (`PublishAot=true` retained), but changes
from `OutputType=Exe` (console) to `OutputType=WinExe` (windowed) so no console flashes
at login.

The tray UI is built with **raw Win32 (`Shell_NotifyIcon`) via P/Invoke, not WinForms**,
because WinForms does not support Native AOT. Staying with P/Invoke keeps the small
self-contained executable and matches the existing code style.

### File breakdown

| File | Responsibility |
|---|---|
| `Program.cs` | Entry point; CLI arg parsing (`--install` / `--uninstall`); single-instance mutex; wiring; Win32 message loop |
| `Native.cs` | All P/Invoke declarations and interop structs (currently inline in `Program.cs`) |
| `Remapper.cs` | Hook callback + remap state machine: `MediaMap`, `ChordMap`, physical-modifier tracking, Start-menu suppression, active-chord pairing. Behavior unchanged. |
| `KeyboardHook.cs` | `SetWindowsHookEx` install/uninstall and hook lifetime |
| `TrayIcon.cs` | `Shell_NotifyIcon`; hidden message-only window + `WndProc`; right-click popup menu; icon state |
| `AutoStart.cs` | Register/unregister the per-user logon Scheduled Task |
| `DiagConsole.cs` | On-demand `AllocConsole` / `FreeConsole` for diagnostic logging |

Each unit has a single purpose and communicates through a small explicit interface
(e.g. `Remapper` exposes the callback + an `Enabled` toggle + a `Diag` toggle;
`TrayIcon` raises menu events that `Program` wires to those toggles).

## Runtime behavior

At login the app starts hidden (no visible window), installs the keyboard hook, and
shows the tray icon.

**Single-instance guard:** a named mutex ensures a second copy (e.g. a login-time race)
exits immediately rather than installing a second hook.

**Tray icon:** the icon reflects Enabled vs. Disabled state at a glance (normal vs.
greyed/badged). Left-click or double-click quick-toggles Enabled/Disabled.

**Right-click popup menu**, rebuilt each open so check marks reflect live state:

```
FcnFn
─────────────────
✓ Enabled            (toggle remap on/off; same state Scroll Lock flips)
  Diagnostic logging (toggle on-demand console + key-event logging)
  Run at login       (✓ when the logon task is registered; toggles it)
─────────────────
  Quit
```

**Scroll Lock quick-toggle retained:** in addition to the tray, pressing Scroll Lock
flips Enabled/Disabled (existing behavior). It continues to swallow the physical
Scroll Lock key so apps never see it.

**Diagnostic logging:** because a tray app has no console, toggling "Diagnostic logging"
on calls `AllocConsole` to open a console window for live event viewing, and `FreeConsole`
when toggled off. This preserves the existing `Console.WriteLine` diagnostic output with
minimal code change. (The old `diag` command-line mode is superseded by this toggle.)

## Auto-start mechanism

Auto-start uses a **per-user Scheduled Task with a logon trigger**, in preference to the
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key, because a logon-triggered task
runs more reliably and can be configured to restart on failure.

- `--install` CLI flag registers the logon task for the current user.
- `--uninstall` CLI flag removes it.
- The tray menu "Run at login" item calls the same register/unregister code and shows a
  check mark when the task is present.

Each user who runs `--install` (or toggles the menu item) gets their own logon task in
their own session, satisfying "runs for each user when they log in." Registering a single
machine-wide task for all users at once would require admin rights and is out of scope.

## Refactor & optimization details

The remap **logic is functionally identical** to the current working code. Changes:

- Split the single file into the units above; move inline structs and `DllImport`
  declarations into `Native.cs`.
- **Hot-path allocation fix:** `HookCallback` fires on every keystroke and currently calls
  `Marshal.PtrToStructure<KBDLLHOOKSTRUCT>`, which boxes a struct per event. Replace with an
  `unsafe` pointer read (`*(KBDLLHOOKSTRUCT*)lParam`) for zero per-event allocation.
- **Reuse a single `INPUT[1]` buffer** in `Inject` instead of allocating a new array per call.
- Introduce named VK constants where it improves clarity, keeping the annotated map comments.

## Error handling

- **Hook install failure:** show a tray balloon notification and put the tray icon/menu in a
  visible error/disabled state, instead of writing to stderr and exiting (invisible in a
  windowed app, as today).
- **Diag console:** guard against double `AllocConsole` / `FreeConsole`.
- **Cleanup:** unhook, delete the `Shell_NotifyIcon`, destroy the message window, free the
  console if open, and release the mutex — on Quit and on `WM_CLOSE` / logoff
  (`WM_ENDSESSION`).

## Testing / verification

The low-level hook and `SendInput` require a real interactive Windows session; they cannot
be exercised in a headless sandbox. Verification is therefore:

- Build succeeds under `dotnet build` with `PublishAot=true` / `WinExe`.
- Manual verification by the user on the target machine: tray icon appears at login; menu
  toggles work; remapping behaves as before; `--install` creates the logon task and the app
  starts on next login; Scroll Lock still toggles; Quit cleans up (no orphaned icon/hook).
- Diagnostic-console toggle reproduces the old `diag` output for capturing VKs.

## Open items / decisions made

- Per-user auto-start chosen over machine-wide (no admin). Revisit only if all-users install
  is explicitly required.
- Scheduled Task (logon trigger) chosen over `Run` registry key for reliability.
- Scroll Lock quick-toggle retained alongside the tray.
- Diagnostic output via on-demand console (not a log file).
