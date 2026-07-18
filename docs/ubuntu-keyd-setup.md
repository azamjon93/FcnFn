# FcnFn on Ubuntu — the Linux equivalent (keyd)

FcnFn itself is a Windows app: ~90% of it is P/Invoke into `user32`/`gdi32`/`shell32`/`kernel32`
(a low-level keyboard hook, `SendInput`, a `Shell_NotifyIcon` tray, a Win32 message loop). None of
that exists on Linux, and Ubuntu 24.04 defaults to **Wayland**, whose security model forbids an app
from globally intercepting or injecting keystrokes across other apps. So the Windows approach cannot
be ported as-is.

The Linux way to remap keys lives **below the display server**, at the kernel input layer
(`/dev/input` + `/dev/uinput`), which works on both X11 and Wayland. The purpose-built tool for this
is **[keyd](https://github.com/rvaiya/keyd)** — a small systemd daemon driven by a config file. It
solves the same problem (Fn row / media keys sending the wrong thing) with far less effort than
porting the app, and it needs no tray icon or per-user autostart.

> **Note on key codes:** the keyboard emits *different* codes on Linux than on Windows — Linux uses
> evdev key names (`mute`, `volumeup`, `f21`…), not Windows virtual-key codes (`0xAD`, `0x5B`…). The
> kernel's HID driver may also decode this keyboard's Fn row more cleanly than Windows did (the
> `Win+F21`-style chords were a Windows HID-mapping artifact). So the mapping must be **captured on
> Ubuntu**, not translated from the Windows tables.

## 1. Install keyd

keyd isn't in the default Ubuntu repos; build from source (takes ~1 minute):

```bash
sudo apt update
sudo apt install -y git build-essential
git clone https://github.com/rvaiya/keyd
cd keyd
make && sudo make install
sudo systemctl enable --now keyd
```

## 2. Capture what the Fn row actually emits

This is the key step — it tells us the real names to map.

```bash
sudo keyd monitor
```

Press **each key in the top row, left to right**, one at a time. Each press prints a line like:

```
keyboard /dev/input/event5: key down volumeup
```

Record the **name** keyd reports for each physical key (`mute`, `volumeup`, `previoussong`,
`brightnessdown`, `search`, `f9`, …). Press `Ctrl-C` when done. Also note the device line
(`keyboard /dev/input/eventN: …`) so the config can target the Bluetooth keyboard specifically if
needed (`keyd -m` or `cat /proc/bus/input/devices` lists devices).

**If a key prints nothing** in `keyd monitor` (like the F9/F11 keys that were invisible to the
Windows hook), run `sudo libinput debug-events` and press it again to see whether it reaches evdev at
all, or whether that key needs a different approach.

## 3. Write the config

The config lives at `/etc/keyd/default.conf`. Below is a **starting hypothesis** based on the Windows
mapping — replace each right-hand value with the names from step 2.

```ini
[ids]
*

[main]
# left half was media keys on Windows → F1–F6
mute         = f1
volumedown   = f2
volumeup     = f3
previoussong = f4
playpause    = f5
nextsong     = f6

# right half (brightness / search / task view / cast / settings) → F7–F12
# names depend entirely on the step-2 capture, e.g.:
brightnessdown = f7
brightnessup   = f8
# search       = f9
# <taskview>   = f10
# <cast>       = f11
# <settings>   = f12
```

- `[ids] *` applies to every keyboard but only rewrites the keys you list — everything else passes
  through untouched, so it's safe. Pin it to the Bluetooth keyboard's ID (from step 2) if you prefer.
- Names for keyd are lowercase evdev names (`f1`…`f12`, `mute`, `volumeup`, `volumedown`,
  `previoussong`, `nextsong`, `playpause`, `brightnessup`, `brightnessdown`, `search`, …).

## 4. Apply and test

```bash
sudo keyd reload      # re-read /etc/keyd/default.conf
```

Press the keys — they should now send F1–F12; unmapped keys keep working normally. keyd auto-starts at
boot (the `systemctl enable --now` above), so there is **no tray app and no per-user login task** to
manage: that entire Windows layer is unnecessary on Linux.

## Why not port the .NET app?

Only `RemapEngine.cs` (the pure decision logic) is portable; the capture, injection, tray, icon,
console, and autostart layers are all Windows API. A real .NET port would be a *new platform backend*
— an evdev reader (`EVIOCGRAB`) + `uinput` virtual keyboard running as a root/systemd daemon, plus an
optional StatusNotifierItem tray over DBus — not a recompile. And Native-AOT can't cross-compile
Windows→Linux, so the Linux binary would have to be built on Ubuntu. keyd delivers the same outcome
for a fraction of the effort, which is why it's the recommended path here.
```