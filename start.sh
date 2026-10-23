#!/usr/bin/env bash
# Start R.E.P.O. through Steam with the RepoBot mod (BepInEx plugin + hot-reloaded brain).
#
#   ./start.sh            start the game (or say that it is already running)
#   ./start.sh --build    rebuild plugin + brain from source and install them first
#   ./start.sh --on       also switch the bot on once the mod has loaded (same as F9 in game)
#   ./start.sh --check    only report what is set up, start nothing
#
# The game folder is found through Steam's library list; set REPO_DIR=<folder with REPO.exe> to override.
set -euo pipefail

ROOT="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
APPID=3241660
LAUNCH_OPTION='WINEDLLOVERRIDES=winhttp=n,b %command%'
WAIT=180   # seconds to wait for the mod to load when --on is given

build=0 on=0 check=0
for a in "$@"; do
  case "$a" in
    --build) build=1 ;;
    --on) on=1 ;;
    --check) check=1 ;;
    -h|--help) sed -n '2,9s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown option: $a (try --help)" >&2; exit 2 ;;
  esac
done

die() { echo "start.sh: $*" >&2; exit 1; }
say() { printf '  %-5s %s\n' "$1" "$2"; }

# ---- find Steam and the game -------------------------------------------------------------------
STEAM_DIR=
for d in "$HOME/.local/share/Steam" "$HOME/.steam/steam"; do
  if [ -f "$d/steamapps/libraryfolders.vdf" ]; then STEAM_DIR="$d"; break; fi
done

find_game() {
  if [ -n "${REPO_DIR:-}" ]; then echo "$REPO_DIR"; return 0; fi
  [ -n "$STEAM_DIR" ] || return 1
  local lib
  while IFS= read -r lib; do
    if [ -f "$lib/steamapps/common/REPO/REPO.exe" ]; then echo "$lib/steamapps/common/REPO"; return 0; fi
  done < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)".*/\1/p' "$STEAM_DIR/steamapps/libraryfolders.vdf")
  return 1
}

GAME="$(find_game)" || die "R.E.P.O. not found in your Steam libraries - set REPO_DIR=<folder with REPO.exe>"
[ -f "$GAME/REPO.exe" ] || die "no REPO.exe in $GAME"
LOG="$GAME/BepInEx/LogOutput.log"
PLUGIN_DST="$GAME/BepInEx/plugins/RepoBot.dll"
BRAIN_DST="$ROOT/run/Brain.dll"

running() { pgrep -f 'REPO\.exe' >/dev/null; }

# ---- checks ------------------------------------------------------------------------------------
echo "game:   $GAME"
echo "bot:    $ROOT"
hard=0

if [ -f "$GAME/winhttp.dll" ] && [ -f "$GAME/BepInEx/core/BepInEx.dll" ]; then
  say ok "BepInEx is installed"
else
  say FAIL "BepInEx 5 (win x64) is not installed in the game folder - unzip it there: https://github.com/BepInEx/BepInEx/releases"
  hard=1
fi

if [ -f "$PLUGIN_DST" ]; then say ok "plugin installed (BepInEx/plugins/RepoBot.dll)"; else say todo "plugin not installed yet - will build"; build=1; fi
if [ -f "$BRAIN_DST" ]; then say ok "brain built (run/Brain.dll)"; else say todo "brain not built yet - will build"; build=1; fi

if [ -n "$STEAM_DIR" ]; then
  if grep -qs 'WINEDLLOVERRIDES=winhttp=n,b' "$STEAM_DIR"/userdata/*/config/localconfig.vdf; then
    say ok "Steam launch option is set"
  else
    say WARN "Steam launch option missing - the mod will NOT load. In Steam: R.E.P.O. > Properties > Launch options: $LAUNCH_OPTION"
  fi
fi

# the plugin and the brain look for run/ through a hardcoded Wine path (Z: is the Linux root)
wine_run="Z:${ROOT//\//\\}\\run"
for f in plugin/Plugin.cs brain/Brain.cs; do
  if ! grep -qF -- "$wine_run" "$ROOT/$f"; then
    say WARN "$f does not point at $wine_run - edit its path constant, or hot reload will not find Brain.dll"
  fi
done

if running; then say info "R.E.P.O. is running"; fi

if [ "$check" = 1 ]; then exit "$hard"; fi
[ "$hard" = 0 ] || die "fix the FAIL above first"

# ---- build and install -------------------------------------------------------------------------
# Replace by renaming, never by overwriting in place: a running game keeps the old file.
install_if_changed() {
  if [ -f "$2" ] && cmp -s "$1" "$2"; then return 1; fi
  cp "$1" "$2.tmp" && mv -f "$2.tmp" "$2"
}

if [ "$build" = 1 ]; then
  command -v dotnet >/dev/null || die "dotnet SDK not found in PATH"
  mkdir -p "$ROOT/refs" "$ROOT/run"
  # compile against the game's own assemblies and BepInEx's libraries (none of them are kept in the repo)
  ln -sfn "$GAME/REPO_Data/Managed" "$ROOT/refs/Managed"
  for lib in BepInEx 0Harmony Mono.Cecil; do cp -u "$GAME/BepInEx/core/$lib.dll" "$ROOT/refs/"; done
  echo "building plugin..."
  dotnet build "$ROOT/plugin" -c Release -nologo -v q
  echo "building brain..."
  dotnet build "$ROOT/brain" -c Release -nologo -v q
  if install_if_changed "$ROOT/plugin/bin/RepoBot.dll" "$PLUGIN_DST"; then
    if running; then say WARN "plugin replaced - restart the game to load it"; else say ok "plugin installed"; fi
  fi
  if install_if_changed "$ROOT/brain/bin/Brain.dll" "$BRAIN_DST"; then
    if running; then say ok "brain replaced - the running game reloads it within a second"; else say ok "brain installed"; fi
  fi
fi

# ---- launch ------------------------------------------------------------------------------------
stamp=
cleanup() { if [ -n "$stamp" ]; then rm -f "$stamp"; fi; }
trap cleanup EXIT

if running; then
  echo "R.E.P.O. is already running."
else
  command -v steam >/dev/null || die "steam not found in PATH"
  stamp="$(mktemp)"
  echo "starting R.E.P.O. through Steam..."
  setsid -f steam "steam://rungameid/$APPID" >/dev/null 2>&1
fi

if [ "$on" = 1 ]; then
  if [ -n "$stamp" ]; then
    echo "waiting for the mod to load (up to ${WAIT}s)..."
    loaded=0
    for ((i = 0; i < WAIT; i++)); do
      if [ "$LOG" -nt "$stamp" ] && grep -qs 'brain #[0-9]* loaded' "$LOG"; then loaded=1; break; fi
      sleep 1
    done
    [ "$loaded" = 1 ] || die "the mod did not load within ${WAIT}s - see $LOG"
  fi
  mkdir -p "$ROOT/run"
  echo on > "$ROOT/run/cmd.txt"
  echo "bot switched on"
fi
