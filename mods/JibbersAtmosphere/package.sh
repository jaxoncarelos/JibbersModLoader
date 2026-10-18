#!/usr/bin/env bash
# Keep every player archive on the same tested shared-loader build.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$root/tools/package_mods.ps1")" "$@"
