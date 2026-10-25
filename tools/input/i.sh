#!/usr/bin/env bash
# Send input commands (stdin, one per line) to the virtual keyboard/mouse daemon,
# but only while the R.E.P.O. window has focus. Waits until they have all run.
# Usage: i.sh <<'EOF' ... EOF      (add --nofocus as $1 to skip the focus guard)
# The daemon (inp_daemon.py) keeps its FIFO and log next to itself, so default to this script's folder.
S="${INPUT_DIR:-$(cd "$(dirname "$(readlink -f "$0")")" && pwd)}"
# The game window (X11 class steam_app_3241660 = Steam's R.E.P.O.); its id changes on every launch. REPO_WIN=0x... overrides.
find_win() {
  local id
  for id in $(xprop -root _NET_CLIENT_LIST 2>/dev/null | grep -o '0x[0-9a-f]*'); do
    if xprop -id "$id" WM_CLASS 2>/dev/null | grep -q 'steam_app_3241660'; then echo "$id"; return 0; fi
  done
  return 1
}
if [ "$1" != "--nofocus" ]; then
  WIN="${REPO_WIN:-$(find_win)}"
  [ -n "$WIN" ] && xprop -id "$WIN" _NET_WM_STATE 2>/dev/null | grep -q FOCUSED || { echo "Game window NOT focused: input cancelled" >&2; exit 3; }
fi
[ -p "$S/inp.fifo" ] || { echo "input daemon not running (python3 inp_daemon.py)" >&2; exit 4; }
tok="t$RANDOM$RANDOM"
{ cat; echo "ack $tok"; } > "$S/inp.fifo"
for _ in $(seq 1 600); do
  grep -q "^ack $tok\$" "$S/inp.log" 2>/dev/null && { grep -E '^(error|unknown)' "$S/inp.log" | tail -3; exit 0; }
  sleep 0.1
done
echo "timed out waiting for the daemon" >&2; exit 5
