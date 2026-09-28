#!/usr/bin/env bash
# Launch a local multiplayer test: one host + N clients (built player), all bots by default.
#   tools/mp-test.sh start [clients=2] [extra host args...]   (MP_HOST_RES / MP_CLIENT_RES=1920x1080 for big windows)
#   tools/mp-test.sh join NAME ADDR    # one more bot client (late joiner, or a bad address)
#   tools/mp-test.sh quit NAME         # close one instance (leave / host quits)
#   tools/mp-test.sh status            # one line per instance
#   tools/mp-test.sh shot NAME         # screenshot an instance (host, c1, c2, ...)
#   tools/mp-test.sh stop
set -euo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXE="$REPO/out/build/win64/Tailed.exe"
PLAYERS="$REPO/out/players"
WIN_EXE="$(wslpath -w "$EXE")"

launch() { # name args...
  local name="$1"; shift
  mkdir -p "$PLAYERS/$name"
  rm -rf "$PLAYERS/$name/inbox" "$PLAYERS/$name/outbox" "$PLAYERS/$name/heartbeat"
  local dir; dir="$(wslpath -w "$PLAYERS/$name")"
  local res="${RES:-800x450}"
  local args=(-screen-fullscreen 0 -screen-width "${res%x*}" -screen-height "${res#*x}" -logFile "$(wslpath -w "$PLAYERS/$name/player.log")"
              -tailed-name "$name" -tailed-bridge "$dir" "$@")
  local list; list=$(printf "'%s'," "${args[@]}"); list="${list%,}"
  powershell.exe -NoProfile -Command "Start-Process -FilePath '$WIN_EXE' -ArgumentList $list" </dev/null >/dev/null 2>&1
}

case "${1:-}" in
  start)
    n="${2:-2}"; shift 2 || shift $#
    RES="${MP_HOST_RES:-800x450}" launch host -tailed-host 7777 -tailed-bot -tailed-autostart $((n + 1)) "$@"
    sleep 4
    # Clients get the host's -tailed-test-voice too, so tests never open a real microphone.
    client_extra=(); for a in "$@"; do [[ "$a" == "-tailed-test-voice" ]] && client_extra+=("$a"); done
    for i in $(seq 1 "$n"); do RES="${MP_CLIENT_RES:-800x450}" launch "c$i" -tailed-join 127.0.0.1:7777 -tailed-bot "${client_extra[@]}"; sleep 1; done
    echo "launched host + $n clients"
    ;;
  join)
    launch "$2" -tailed-join "$3" -tailed-bot -tailed-test-voice
    echo "launched $2 -> $3"
    ;;
  quit)
    BRIDGE_DIR="$PLAYERS/$2" "$REPO/tools/bridge.sh" quit "" 5 >/dev/null 2>&1 || true
    echo "quit $2"
    ;;
  status)
    for d in "$PLAYERS"/*/; do
      name="$(basename "$d")"
      printf '%-5s ' "$name"
      BRIDGE_DIR="$d" "$REPO/tools/bridge.sh" status "" 10 2>&1 | head -1 || true
    done
    ;;
  shot)
    BRIDGE_DIR="$PLAYERS/$2" "$REPO/tools/bridge.sh" screenshot "" 30
    ;;
  exec)
    BRIDGE_DIR="$PLAYERS/$2" "$REPO/tools/bridge.sh" execute "$3" 30
    ;;
  stop)
    for d in "$PLAYERS"/*/; do BRIDGE_DIR="$d" "$REPO/tools/bridge.sh" quit "" 5 >/dev/null 2>&1 || true; done
    sleep 2
    taskkill.exe /IM Tailed.exe /F >/dev/null 2>&1 || true
    echo stopped
    ;;
  *) sed -n '2,7p' "$0"; exit 1 ;;
esac
