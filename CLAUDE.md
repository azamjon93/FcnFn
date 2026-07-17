# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-file Windows console app (`FcnFn/Program.cs`, internal class `FcnFn`) that remaps the Fn row of a Bluetooth keyboard back to real F1–F12. The keyboard emits its top row as OS-injected media keys (volume/play) and as Win-modifier chords (e.g. `Win+F21`, `Win+Tab`) rather than function keys; this app intercepts those via a low-level keyboard hook and re-emits the intended F-keys.

## Build & run

```powershell
dotnet build                       # from repo root; builds FcnFn.slnx
dotnet run --project FcnFn          # remap mode (default)
dotnet run --project FcnFn diag     # diagnostic mode
```

- `diag` — logs every key event (vk, scancode, flags, injected/ours, down/up). Use this first on any new keyboard to capture the actual VKs before editing the maps.
- (no arg / anything else) — remap mode. **Scroll Lock toggles remapping on/off** at runtime.
- Target framework is `net10.0` with `PublishAot=true`. There are no tests, no linter config, and no CI.
- Must run on Windows with a real interactive session — the low-level hook and `SendInput` do nothing meaningful in a headless/sandboxed context, so changes can't be verified by running here; verify by reasoning about the hook logic and, when possible, having the user run `diag`.

## How the remapping works (the parts that require reading multiple pieces together)

Everything lives in `HookCallback`, a `WH_KEYBOARD_LL` hook. Returning `(IntPtr)1` swallows an event; `CallNextHookEx` passes it through.

- **Marker-based recursion guard.** Every key this app injects carries `dwExtraInfo == Marker` (`0xF17F17`). The hook skips only events with our marker (`ours`) — it does **not** skip all injected events, because the media keys arrive OS-injected (hidserv translates HID consumer usages via `SendInput`) and still need remapping.

- **Two translation tables**, both editable at the top of the file:
  - `MediaMap`: OS-injected media VK → F-key. These are simple one-shot re-injects.
  - `ChordMap`: a `Chord(Vk, Win, Shift, Ctrl)` (terminal key + exact physical modifier pattern) → F-key.

- **Physical modifier tracking.** `_win/_shift/_ctrl/_alt` are updated only from non-marker events, so the app knows the real modifier state when a chord's terminal key arrives.

- **Start-menu suppression for Win chords.** Firing an F-key out of a `Win+…` chord would otherwise leave the Win key "clean" and open the Start menu on release. The fix (in the `firstFire` branch): inject a neutral `vk 0xFF` down/up to dirty the Win press, then logically release the held chord modifiers via `ReleaseMod`, which also records their VKs (both L/R variants) in `_suppressedModUps` so the later physical modifier-UP events get swallowed.

- **Chord up-pairing.** `_activeChords` maps a terminal VK to the F-key currently held so the key-up can emit the matching F-key up (and supports auto-repeat while held).

## Editing the key maps

- VKs are hardware/OS-specific. Always capture them with `diag` before adding or changing an entry — several `ChordMap` entries are still marked "verify" in comments and the intended physical layout (F1..F10 left-to-right) is an assumption.
- Some keys (noted in comments: F9/search, possibly F11/cast) arrive as raw HID consumer usages that produce **no hook-visible events**, so they can't be handled by the current LL-hook approach and would need a Raw Input listener path.
