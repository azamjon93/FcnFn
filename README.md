# FcnFn

**Forced control of Fn keys** — a tiny Windows system-tray app that remaps the Fn row of a Bluetooth keyboard back to real **F1–F12**.

Some Bluetooth keyboards send their top row as media keys (volume, play/pause) and Windows-modifier chords (`Win+F21`, `Win+Tab`, …) instead of function keys, with no firmware toggle to change it. FcnFn intercepts those events with a low-level keyboard hook and re-emits the function key you actually pressed.

## Features

- Remaps OS-injected **media keys** → F1–F6 and **Win-modifier chords** → F7/F8/F10/F12.
- Suppresses the Start menu when a `Win+…` chord fires, so you get a clean F-key.
- Runs hidden in the **system tray**; the icon shows whether remapping is on (green) or off (grey).
- **Per-user auto-start** at login (optional).
- Native-AOT single executable, no runtime install, **no admin required** to run.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build

## Build & run

```powershell
dotnet build
dotnet run --project FcnFn
```

A green **Fn** icon appears in the tray. Right-click it for the menu:

| Menu item | What it does |
|---|---|
| **Enabled** | Turn remapping on/off. Also toggled by **Scroll Lock**, or a left/double-click on the icon. |
| **Diagnostic logging** | Open a console that logs every key event (vk, scancode, flags). Use this to capture the raw key codes on a new keyboard before editing the maps. |
| **Run at login** | Register/unregister a per-user logon task so FcnFn starts automatically. |
| **Quit** | Unhook and remove the tray icon. |

### Auto-start from the command line

```powershell
FcnFn --install      # register the logon auto-start task
FcnFn --uninstall    # remove it
```

> **Note:** enabling auto-start creates a Windows Scheduled Task via `schtasks`, which requires elevation — so `--install` / `--uninstall` and the **Run at login** menu item trigger a one-time **UAC prompt**. Declining it is handled gracefully. The remapper and tray themselves need no admin; only enabling auto-start does.

### Publishing a standalone executable

```powershell
dotnet publish FcnFn/FcnFn.csproj -c Release -r win-x64
```

Native-AOT publish invokes the MSVC linker, so run it from a **Visual Studio Developer Command Prompt/PowerShell** (or with the VS C++ build tools on `PATH`). The plain `dotnet build` and `dotnet test` work from any shell.

## How it works

The hook decision logic lives in a pure, unit-tested `RemapEngine`; everything touching Windows is a thin P/Invoke wrapper (no WinForms — it stays AOT-clean).

- **Marker guard** — every key FcnFn injects carries a marker in `dwExtraInfo`, so the hook re-processes OS-injected media keys but never its own injections.
- **Two maps** — `MediaMap` (media VK → F-key) and `ChordMap` (`Chord(Vk, Win, Shift, Ctrl)` → F-key), both at the top of `RemapEngine.cs`.
- **Start-menu suppression** — on a `Win+…` chord, FcnFn dirties the Win press and logically releases the held modifier so the app sees a bare F-key, then swallows the trailing physical modifier-up.

The key codes are keyboard-specific. Capture yours with **Diagnostic logging** before adjusting the maps.

## Development

```powershell
dotnet test    # RemapEngine + AutoStart unit tests
```

| Area | Files |
|---|---|
| Entry point / wiring / message loop | `FcnFn/Program.cs` |
| Win32 interop | `FcnFn/Native.cs` |
| Remap decision logic (tested) | `FcnFn/RemapEngine.cs` |
| Keyboard hook lifetime | `FcnFn/KeyboardHook.cs` |
| Diagnostic console | `FcnFn/DiagConsole.cs` |
| Logon auto-start | `FcnFn/AutoStart.cs` |
| Tray icon + menu | `FcnFn/TrayIcon.cs`, `FcnFn/IconFactory.cs` |
| Tests | `FcnFn.Tests/` |

The remap engine is isolated from all Win32 calls so its state machine (media remap, chord handling, Start-menu suppression, modifier tracking) can be exercised in unit tests. Everything touching the hook, tray, or scheduled task is verified by building and running on a real Windows session.

Design notes live in [`docs/superpowers/`](docs/superpowers/).
