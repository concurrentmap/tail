#!/usr/bin/env bash
# Drive Unity from WSL. Uses the editor bridge when the editor has the project open,
# otherwise runs Unity in batch mode.
#   tools/unity.sh compile            # compile all assemblies, report errors
#   tools/unity.sh test [EditMode|PlayMode]
#   tools/unity.sh build [--dev]      # Windows player -> out/build/win64/Tailed.exe
#   tools/unity.sh setup              # regenerate Town scene + layers/always-included shaders
#   tools/unity.sh open               # launch the editor GUI (detached)
#   tools/unity.sh core-test          # dotnet test for the pure-C# core (no Unity)
set -euo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY_VERSION="$(sed -n 's/^m_EditorVersion: //p' "$REPO/unity/ProjectSettings/ProjectVersion.txt")"
UNITY="/mnt/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe"
PROJECT_WIN="$(wslpath -w "$REPO/unity")"
LOGS="$REPO/out/logs"
mkdir -p "$LOGS"

editor_open() { "$REPO/tools/bridge.sh" ping "" 5 >/dev/null 2>&1; }

# Run Unity in batch mode; on failure print compiler errors and the log tail.
batch() {
  local name="$1"; shift
  local log="$LOGS/$name.log"
  if [[ -f "$REPO/unity/Temp/UnityLockfile" ]] && ! editor_open; then
    echo "note: stale Temp/UnityLockfile found; if the editor is open without the bridge, close it" >&2
  fi
  set +e
  "$UNITY" -batchmode -nographics -projectPath "$PROJECT_WIN" -logFile "$(wslpath -w "$log")" "$@"
  local rc=$?
  set -e
  if (( rc != 0 )); then
    echo "unity exited $rc — log: $log" >&2
    grep -E "error CS|Exception|Aborting batchmode|Scripts have compiler errors" "$log" | sort -u | head -40 >&2 || true
  fi
  return $rc
}

summarize_results() {
  python3 - "$1" <<'EOF'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
a = root.attrib
print(f"result={a.get('result')} total={a.get('total')} passed={a.get('passed')} failed={a.get('failed')} skipped={a.get('skipped')}")
for tc in root.iter('test-case'):
    if tc.attrib.get('result') == 'Failed':
        msg = tc.find('failure/message')
        print(f"FAIL {tc.attrib.get('fullname')}: {(msg.text or '').strip() if msg is not None else ''}")
sys.exit(0 if a.get('failed', '0') == '0' else 1)
EOF
}

case "${1:-}" in
  compile)
    if editor_open; then exec "$REPO/tools/bridge.sh" refresh; fi
    batch compile -executeMethod Tailed.EditorTools.CI.CompileCheck -quit
    echo "compile OK"
    ;;
  test)
    mode="${2:-EditMode}"
    if editor_open; then exec "$REPO/tools/bridge.sh" run-tests "$mode" 900; fi
    results="$REPO/out/test-$mode.xml"
    rm -f "$results"
    batch "test-$mode" -runTests -testPlatform "$mode" -testResults "$(wslpath -w "$results")" || true
    [[ -f "$results" ]] || { echo "no test results produced" >&2; exit 1; }
    summarize_results "$results"
    ;;
  build)
    if editor_open; then echo "close the editor before building (batch mode needs the project)" >&2; exit 1; fi
    extra=()
    [[ "${2:-}" == "--dev" ]] && extra+=(-tailedDev)
    batch build -executeMethod Tailed.EditorTools.CI.BuildWindows -tailedOut "$(wslpath -w "$REPO/out/build/win64")" "${extra[@]}" -quit
    echo "built: $REPO/out/build/win64/Tailed.exe"
    ;;
  setup)
    # Regenerate the Town scene + project settings (layers, always-included shaders).
    if editor_open; then exec "$REPO/tools/bridge.sh" execute Tailed.EditorTools.TownSceneSetup.Create; fi
    batch setup -executeMethod Tailed.EditorTools.TownSceneSetup.Create -quit
    echo "scene regenerated"
    ;;
  open)
    nohup "$UNITY" -projectPath "$PROJECT_WIN" >/dev/null 2>&1 &
    echo "editor launching; wait for: tools/bridge.sh ping"
    ;;
  core-test)
    export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    dotnet test "$REPO/dotnet/Tailed.sln" --nologo -v q
    ;;
  *)
    sed -n '2,9p' "$0"; exit 1 ;;
esac
