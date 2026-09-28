#!/usr/bin/env bash
# Launch one offline 1920x1080 player with a command bridge at out/players/solo.
#   tools/solo.sh start | tools/solo.sh <bridge args...> | tools/solo.sh stop
set -euo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
D="$REPO/out/players/solo"
export BRIDGE_DIR="$D"
case "${1:-}" in
  start)
    taskkill.exe /IM Tailed.exe /F >/dev/null 2>&1 || true; sleep 2
    rm -rf "$D"; mkdir -p "$D"
    W="$(wslpath -w "$D")"; E="$(wslpath -w "$REPO/out/build/win64/Tailed.exe")"
    powershell.exe -NoProfile -Command "Start-Process -FilePath '$E' -ArgumentList '-screen-fullscreen','0','-screen-width','1920','-screen-height','1080','-tailed-offline','-tailed-name','solo','-tailed-bridge','$W','-logFile','$W\\player.log'" </dev/null >/dev/null 2>&1
    for i in $(seq 1 60); do sleep 1; "$REPO/tools/bridge.sh" ping "" 5 >/dev/null 2>&1 && { echo "up after ${i}s"; exit 0; }; done
    echo "player did not come up" >&2; exit 1 ;;
  stop) "$REPO/tools/bridge.sh" quit "" 10 >/dev/null 2>&1 || true; echo stopped ;;
  *) exec "$REPO/tools/bridge.sh" "$@" ;;
esac
