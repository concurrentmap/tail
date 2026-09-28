#!/usr/bin/env bash
# Send one command to the running Unity editor (see unity/Assets/Tailed/Editor/Bridge.cs).
#   tools/bridge.sh <cmd> [arg] [timeout_s]
#   cmds: ping status refresh play stop run-tests[EditMode|PlayMode] screenshot console[n] execute[Type.Method] quit
# Exit 0 if the editor reports ok, 1 if not ok, 2 if the editor isn't running, 3 on timeout.
set -euo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BRIDGE="${BRIDGE_DIR:-$REPO/unity/Temp/Bridge}"

cmd="${1:?usage: bridge.sh <cmd> [arg] [timeout_s]}"
arg="${2:-}"
timeout_s="${3:-300}"

editor_alive() {
  [[ -f "$BRIDGE/heartbeat" ]] || return 1
  local age=$(( $(date +%s) - $(stat -c %Y "$BRIDGE/heartbeat") ))
  (( age < 10 ))
}

if ! editor_alive; then
  echo "bridge: editor not running (no fresh heartbeat in $BRIDGE)" >&2
  exit 2
fi

id="$(date +%s%N)-$$"
json=$(python3 -c 'import json,sys; print(json.dumps({"id":sys.argv[1],"cmd":sys.argv[2],"arg":sys.argv[3]}))' "$id" "$cmd" "$arg")
printf '%s' "$json" > "$BRIDGE/inbox/$id.tmp"
mv "$BRIDGE/inbox/$id.tmp" "$BRIDGE/inbox/$id.json"

out="$BRIDGE/outbox/$id.json"
deadline=$(( $(date +%s) + timeout_s ))
until [[ -f "$out" ]]; do
  if (( $(date +%s) > deadline )); then echo "bridge: timed out waiting for '$cmd'" >&2; exit 3; fi
  sleep 0.25
done

status=0
python3 - "$out" <<'EOF' || status=$?
import json, sys
r = json.load(open(sys.argv[1], encoding="utf-8"))
print(r["message"])
sys.exit(0 if r["ok"] else 1)
EOF
rm -f "$out"
exit $status
