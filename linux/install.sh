#!/usr/bin/env bash
#
# Installs NarratorHotkey on Linux: builds the portable target, drops it under
# ~/.local, registers a systemd user service for the speech daemon, and binds the
# read and pause hotkeys where the desktop environment allows it to be scripted.
#
# Run with --uninstall to undo all of it.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/narratorhotkey"
BIN_DIR="$HOME/.local/bin"
BIN_LINK="$BIN_DIR/narratorhotkey"
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
UNIT_NAME="narratorhotkey.service"

# Matches the HotkeyModifier/HotkeyKey defaults in src/AppSettings.cs, so the two
# platforms answer to the same keys.
READ_BINDING="${READ_BINDING:-<Control>2}"
PAUSE_BINDING="${PAUSE_BINDING:-<Control>3}"

READ_NAME="NarratorHotkey: read selection"
PAUSE_NAME="NarratorHotkey: pause/resume"

PREBUILT_DIR=""
RUNTIME_ID=""
SELF_CONTAINED=0
WITH_SERVICE=1
WITH_HOTKEYS=1
DO_UNINSTALL=0

GNOME_SCHEMA="org.gnome.settings-daemon.plugins.media-keys"
GNOME_PATH_PREFIX="/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings"

say()  { printf '\033[1m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33m warn:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
    cat <<'EOF'
Usage: install.sh [options]

  --uninstall           Remove the service, hotkeys, symlink and installed files.
  --no-service          Do not install the systemd user service.
  --no-hotkeys          Do not register desktop hotkeys.
  --self-contained      Bundle the .NET runtime (larger, needs no dotnet on this box).
  --from DIR            Install an already-published directory instead of building.
  --install-dir DIR     Where to install (default: ~/.local/share/narratorhotkey).
  --runtime RID         .NET runtime identifier (default: detected from uname).
  -h, --help            Show this message.

Environment:
  READ_BINDING          Hotkey for reading the selection (default: <Control>2).
  PAUSE_BINDING         Hotkey for pause/resume (default: <Control>3).
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --uninstall)      DO_UNINSTALL=1 ;;
        --no-service)     WITH_SERVICE=0 ;;
        --no-hotkeys)     WITH_HOTKEYS=0 ;;
        --self-contained) SELF_CONTAINED=1 ;;
        --from)           PREBUILT_DIR="${2:?--from needs a directory}"; shift ;;
        --install-dir)    INSTALL_DIR="${2:?--install-dir needs a directory}"; shift ;;
        --runtime)        RUNTIME_ID="${2:?--runtime needs a RID}"; shift ;;
        -h|--help)        usage; exit 0 ;;
        *)                die "Unknown option: $1 (try --help)" ;;
    esac
    shift
done

have() { command -v "$1" >/dev/null 2>&1; }

# systemctl exists on images without a running user manager (containers, WSL
# without systemd), where every --user call fails. Check the manager, not the tool.
user_systemd_available() {
    have systemctl && systemctl --user show-environment >/dev/null 2>&1
}

detect_runtime_id() {
    local arch libc=glibc
    arch="$(uname -m)"
    if [[ -f /etc/alpine-release ]] || ldd --version 2>&1 | grep -qi musl; then
        libc=musl
    fi

    case "$arch" in
        x86_64|amd64)
            [[ $libc == musl ]] && echo "linux-musl-x64" || echo "linux-x64" ;;
        aarch64|arm64)
            [[ $libc == musl ]] && echo "linux-musl-arm64" || echo "linux-arm64" ;;
        armv7l|armv7)
            echo "linux-arm" ;;
        *) die "Unsupported architecture '$arch'. Pass --runtime <rid> explicitly." ;;
    esac
}

# ---------------------------------------------------------------- GNOME hotkeys

gnome_available() {
    have gsettings && gsettings list-schemas 2>/dev/null | grep -qx "$GNOME_SCHEMA"
}

# The custom-keybindings list, one dconf path per line.
gnome_read_paths() {
    local raw
    raw="$(gsettings get "$GNOME_SCHEMA" custom-keybindings 2>/dev/null || echo '@as []')"
    [[ "$raw" == "@as []" || "$raw" == "[]" ]] && return 0
    raw="${raw#[}"
    raw="${raw%]}"
    printf '%s' "$raw" | tr ',' '\n' | sed "s/^[[:space:]]*'//; s/'[[:space:]]*$//" | grep -v '^$' || true
}

gnome_write_paths() {
    local out="[" first=1 p
    while IFS= read -r p; do
        [[ -z "$p" ]] && continue
        [[ $first -eq 1 ]] || out+=", "
        out+="'$p'"
        first=0
    done
    out+="]"
    gsettings set "$GNOME_SCHEMA" custom-keybindings "$out"
}

gnome_slot_name() {
    local value
    value="$(gsettings get "${GNOME_SCHEMA}.custom-keybinding:$1" name 2>/dev/null || true)"
    value="${value#\'}"
    printf '%s' "${value%\'}"
}

# Reuse the slot we created last time so re-running does not pile up duplicates.
gnome_find_slot() {
    local want="$1" p
    while IFS= read -r p; do
        [[ -z "$p" ]] && continue
        if [[ "$(gnome_slot_name "$p")" == "$want" ]]; then
            printf '%s' "$p"
            return 0
        fi
    done < <(gnome_read_paths)
    return 1
}

gnome_free_slot() {
    local existing i=0 candidate
    existing="$(gnome_read_paths)"
    while :; do
        candidate="${GNOME_PATH_PREFIX}/custom${i}/"
        if ! grep -qxF "$candidate" <<<"$existing"; then
            printf '%s' "$candidate"
            return
        fi
        i=$((i + 1))
    done
}

gnome_bind() {
    local want_name="$1" command="$2" binding="$3" path schema
    if ! path="$(gnome_find_slot "$want_name")"; then
        path="$(gnome_free_slot)"
        { gnome_read_paths; printf '%s\n' "$path"; } | gnome_write_paths
    fi

    schema="${GNOME_SCHEMA}.custom-keybinding:${path}"
    gsettings set "$schema" name "$want_name"
    gsettings set "$schema" command "$command"
    gsettings set "$schema" binding "$binding"
    say "Bound $binding to '$want_name'"
}

gnome_unbind() {
    local want_name="$1" path
    if ! path="$(gnome_find_slot "$want_name")"; then
        return 0
    fi
    # grep exits 1 when the removed slot was the only one; that is not a failure.
    gnome_read_paths | { grep -vxF "$path" || true; } | gnome_write_paths
    # Leaves the (now unreferenced) key values behind; dconf has no scripted reset
    # for a relocatable schema path that is portable across versions.
    say "Removed hotkey '$want_name'"
}

manual_hotkey_help() {
    cat <<EOF

Bind these two commands to keys in your desktop environment's settings:

  read selection   $BIN_LINK --read
  pause / resume   $BIN_LINK --toggle-pause

  KDE Plasma  System Settings -> Keyboard -> Shortcuts -> Add Command
  XFCE        Settings -> Keyboard -> Application Shortcuts
  Cinnamon    Settings -> Keyboard -> Shortcuts -> Custom Shortcuts
  sway/i3     bindsym \$mod+2 exec $BIN_LINK --read
  Hyprland    bind = \$mainMod, 2, exec, $BIN_LINK --read

EOF
}

# ------------------------------------------------------------------- uninstall

if [[ $DO_UNINSTALL -eq 1 ]]; then
    say "Uninstalling NarratorHotkey"

    if user_systemd_available; then
        systemctl --user stop "$UNIT_NAME" 2>/dev/null || true
        systemctl --user disable "$UNIT_NAME" 2>/dev/null || true
    fi
    rm -f "$UNIT_DIR/$UNIT_NAME"
    if user_systemd_available; then
        systemctl --user daemon-reload || true
    fi

    if gnome_available; then
        gnome_unbind "$READ_NAME"
        gnome_unbind "$PAUSE_NAME"
    fi

    if [[ -L "$BIN_LINK" ]]; then
        rm -f "$BIN_LINK"
    fi
    rm -rf "$INSTALL_DIR"

    say "Removed. Settings and downloaded voices are left in ~/.config/NarratorHotkey."
    exit 0
fi

# --------------------------------------------------------------------- install

say "Installing NarratorHotkey to $INSTALL_DIR"

if ! have wl-paste && ! have xclip && ! have xsel; then
    warn "No clipboard tool found. Install wl-clipboard (Wayland) or xclip/xsel (X11),"
    warn "otherwise --read cannot see the selected text."
fi

if ! have paplay && ! have pw-play && ! have aplay; then
    warn "No audio player found. Install pulseaudio-utils, pipewire-bin or alsa-utils,"
    warn "otherwise there will be nothing to play the speech through."
fi

if [[ -n "$PREBUILT_DIR" ]]; then
    [[ -d "$PREBUILT_DIR" ]] || die "--from directory does not exist: $PREBUILT_DIR"
    [[ -f "$PREBUILT_DIR/NarratorHotkey.dll" ]] || die "$PREBUILT_DIR does not look like a publish output."
    SOURCE_DIR="$PREBUILT_DIR"
else
    have dotnet || die "The .NET SDK is required to build. Install it, or use --from <published-dir>."
    [[ -n "$RUNTIME_ID" ]] || RUNTIME_ID="$(detect_runtime_id)"

    SOURCE_DIR="$(mktemp -d)"
    trap 'rm -rf "$SOURCE_DIR"' EXIT

    say "Publishing for $RUNTIME_ID"
    dotnet publish "$REPO_ROOT/src/NarratorHotkey.csproj" \
        -f net10.0 \
        -c Release \
        -r "$RUNTIME_ID" \
        --self-contained "$([[ $SELF_CONTAINED -eq 1 ]] && echo true || echo false)" \
        -o "$SOURCE_DIR"
fi

mkdir -p "$INSTALL_DIR" "$BIN_DIR"
# Clear the old payload so a renamed or dropped dependency does not linger.
rm -rf "${INSTALL_DIR:?}"/*
cp -a "$SOURCE_DIR"/. "$INSTALL_DIR"/

[[ -f "$INSTALL_DIR/NarratorHotkey" ]] || die "No apphost at $INSTALL_DIR/NarratorHotkey."
# Publishing from Windows loses the executable bit.
chmod +x "$INSTALL_DIR/NarratorHotkey"

ln -sfn "$INSTALL_DIR/NarratorHotkey" "$BIN_LINK"
say "Linked $BIN_LINK"

case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) warn "$BIN_DIR is not on your PATH; add it to use 'narratorhotkey' by name." ;;
esac

if [[ $WITH_SERVICE -eq 1 ]]; then
    if user_systemd_available; then
        mkdir -p "$UNIT_DIR"
        sed "s|@INSTALL_DIR@|$INSTALL_DIR|g" "$SCRIPT_DIR/$UNIT_NAME" > "$UNIT_DIR/$UNIT_NAME"
        systemctl --user daemon-reload
        systemctl --user enable --now "$UNIT_NAME"
        say "Service $UNIT_NAME enabled and started"
    else
        warn "No systemd user manager here; skipping the service."
        warn "Start the daemon yourself with: $BIN_LINK --daemon"
    fi
fi

if [[ $WITH_HOTKEYS -eq 1 ]]; then
    if gnome_available; then
        gnome_bind "$READ_NAME"  "$BIN_LINK --read"         "$READ_BINDING"
        gnome_bind "$PAUSE_NAME" "$BIN_LINK --toggle-pause" "$PAUSE_BINDING"
    else
        warn "Hotkeys cannot be registered automatically on this desktop."
        manual_hotkey_help
    fi
fi

cat <<EOF

Done. Settings page:  $BIN_LINK --settings
Current config:       $BIN_LINK --status
Voices:               $BIN_LINK --list-voices

The first spoken text downloads the Kokoro model (~80MB), so expect a delay.

EOF
