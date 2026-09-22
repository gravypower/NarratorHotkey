# NarratorHotkey on Linux

The Windows build is a tray application that owns its own global hotkeys. Linux has
no portable equivalent, so the same binary runs as a background daemon and the
hotkeys are bound in your desktop environment to short-lived CLI invocations that
talk to it over `http://127.0.0.1:49191`.

## Install

```bash
./linux/install.sh
```

That publishes the `net10.0` target, installs it to `~/.local/share/narratorhotkey`,
links `~/.local/bin/narratorhotkey`, enables a systemd user service for the daemon,
and — on GNOME — binds <kbd>Ctrl</kbd>+<kbd>2</kbd> to read and
<kbd>Ctrl</kbd>+<kbd>3</kbd> to pause/resume. On any other desktop it prints the two
commands to bind yourself.

Re-running is safe: it reuses the hotkey slots it created rather than adding more.

```
--uninstall           Remove service, hotkeys, symlink and files.
--no-service          Skip the systemd user service.
--no-hotkeys          Skip hotkey registration.
--self-contained      Bundle the .NET runtime (no dotnet needed at run time).
--from DIR            Install an already-published directory instead of building.
--install-dir DIR     Install location (default ~/.local/share/narratorhotkey).
--runtime RID         Override the detected RID (linux-x64, linux-arm64, …).

READ_BINDING=…        Hotkey for reading (default '<Control>2').
PAUSE_BINDING=…       Hotkey for pause/resume (default '<Control>3').
```

## Requirements

- **.NET 10 SDK** to build. Not needed at run time if you pass `--self-contained`.
- **A clipboard tool** — `wl-clipboard` on Wayland, or `xclip`/`xsel` on X11.
  Without one there is no way to read the selection.
- **An audio player** — `paplay` (pulseaudio-utils), `pw-play` (pipewire) or
  `aplay` (alsa-utils).

```bash
# Debian / Ubuntu
sudo apt install wl-clipboard xclip pulseaudio-utils

# Fedora
sudo dnf install wl-clipboard xclip pulseaudio-utils

# Arch
sudo pacman -S wl-clipboard xclip libpulse
```

## How the pieces fit

| Piece | Windows | Linux |
| --- | --- | --- |
| Hotkey | `RegisterHotKey` in `HotkeyManager` | desktop keybinding → `narratorhotkey --read` |
| Selection | UI Automation, then clipboard | `wl-paste --primary`, `xclip`, `xsel` |
| Playback | MCI (`winmm`) | `paplay`/`pw-play`/`aplay`, paused with SIGSTOP |
| Voices | System.Speech, Kokoro, Piper | Kokoro, Piper |
| Lives in | tray icon | `narratorhotkey.service` (systemd user unit) |
| Settings | tray menu and web page | web page (`--settings`) |

The daemon exists because model load is slow: `--read` on a cold process would pay
the Kokoro startup cost on every press. If the daemon is not running, `--read`
starts one and retries for about two seconds before falling back to speaking
in-process.

## Commands

```bash
narratorhotkey --read           # speak the current selection
narratorhotkey --toggle-pause   # hold / carry on
narratorhotkey --stop           # stop speaking
narratorhotkey --settings       # open the settings page in a browser
narratorhotkey --status         # show the current configuration
narratorhotkey --list-voices    # voices for the selected provider
narratorhotkey --daemon         # run the daemon in the foreground
```

## Voices

`Kokoro ONNX` is the default on Linux; a saved `Windows` provider is remapped to it
automatically. The first spoken text downloads the model (~80MB) to
`~/.config/NarratorHotkey/Kokoro`, so the first press is slow and needs a network
connection.

Piper works too, and uses a system-wide `piper` binary if one is on `PATH` rather
than downloading its own.

## Troubleshooting

```bash
systemctl --user status narratorhotkey     # is the daemon up?
journalctl --user -u narratorhotkey -f     # what is it saying?
narratorhotkey --daemon                    # run in the foreground to watch it
```

**Nothing is spoken, no error.** Check an audio player is installed and that the
daemon can reach your sound server — `systemctl --user show-environment | grep
XDG_RUNTIME_DIR` should be set.

**"No text selected."** The PRIMARY selection is what gets read, and some
applications (notably Electron and GTK4 ones) do not export it. Copy the text
first; the clipboard is the fallback.

**Hotkeys do nothing.** Confirm the binding runs the command at all by running it
from a terminal. On GNOME, check Settings → Keyboard → View and Customize Shortcuts
→ Custom Shortcuts for a conflict with an existing binding.

**The service will not start.** Session managers that never reach
`graphical-session.target` are why the unit is wanted by `default.target` instead;
if your setup has no systemd user manager at all, run `narratorhotkey --daemon`
from your session autostart.
