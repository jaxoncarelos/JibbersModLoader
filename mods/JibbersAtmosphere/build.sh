#!/usr/bin/env bash
# Builds jibbers_atmosphere.bundle with Unity 6000.3 (headless) and, with --install, copies it and
# the hotload core into the game folder and swaps them into the running game.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.8f1/Editor/Unity.exe}"
game="${JIBBERS_GAME_DIR:-/c/Program Files (x86)/Steam/steamapps/common/Jibbers Playtest}"
log="$(mktemp)"
"$unity" -batchmode -quit -nographics -projectPath "$(cygpath -w "$here/unity")" \
  -executeMethod BuildAtmosphere.Build -logFile "$(cygpath -w "$log")" || true
if grep -qiE "shader error|error CS" "$log"; then grep -iE "shader error|error CS" "$log"; exit 1; fi
grep -q "bundle built" "$log" || { tail -40 "$log"; exit 1; }
echo "bundle built"
if [[ "${1:-}" == "--install" ]]; then
  (cd "$root" && cargo build --release -p jibbers-hotload-core)
  cp "$here/build/jibbers_atmosphere.bundle" "$game/"
  cp "$root/target/release/jibbers_hotload_core.dll" "$game/"
  if python "$root/tools/hotload.py" reload-core >/dev/null 2>&1; then
    python "$root/tools/hotload.py" raw '{"cmd":"atmo","respawn":true}' >/dev/null && echo "swapped into the running game"
  else
    echo "installed (game not running)"
  fi
fi
