#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd -P)"
mods="$root/JibbersMods"
if [ "$(uname -s)" != Darwin ]; then echo "Use this launcher on macOS."; exit 1; fi
app=""
case "${1:-}" in
    *.app) app="$1"; shift ;;
    *.app/Contents/MacOS/*) app="${1%%.app/*}.app"; shift ;;
esac
if [ -z "$app" ]; then
    for candidate in "$root"/*.app; do
        [ -d "$candidate" ] || continue
        case "$(basename "$candidate")" in *[Jj]ibbers*) app="$candidate"; break ;; esac
    done
fi
if [ -z "$app" ] || [ ! -f "$app/Contents/Info.plist" ]; then
    echo "Extract this ZIP beside the Jibbers .app, then run this launcher again."
    exit 1
fi
app="$(cd "$app" && pwd -P)"
name=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist")
exe="$app/Contents/MacOS/$name"
metadata="$app/Contents/Resources/Data/il2cpp_data/Metadata/global-metadata.dat"
mkdir -p "$mods"
report="$mods/mac-launch.log"
{
    date -u
    sw_vers
    echo "Game: $app"
    file "$exe"
    [ ! -f "$metadata" ] || shasum -a 256 "$metadata"
    codesign -dv "$app" 2>&1 || true
} >> "$report"
if [ ! -f "$metadata" ]; then
    echo "This preview needs the IL2CPP Mac game build. No mods have been loaded. See $report."
    exit 1
fi
arches=$(/usr/bin/lipo -archs "$exe")
if [ "$(/usr/sbin/sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = 1 ] && [[ " $arches " == *" arm64 "* ]]; then
    arch=arm64; rid=osx-arm64
elif [[ " $arches " == *" x86_64 "* ]]; then
    arch=x86_64; rid=osx-x64
else
    echo "Unsupported game architecture: $arches"; exit 1
fi
core="$mods/mac/runtime/$rid"
if [ ! -f "$core/libcoreclr.dylib" ]; then echo "Incomplete ZIP: $core/libcoreclr.dylib is missing."; exit 1; fi
# Remove download quarantine from this mod's own native payload only. Never modify the game app.
/usr/bin/xattr -dr com.apple.quarantine "$mods/mac" 2>/dev/null || true
export JIBBERS_GAME_ROOT="$root" JIBBERS_GAME_APP="$app"
export JIBBERS_IL2CPP_LIBRARY="$app/Contents/Frameworks/GameAssembly.dylib"
if [ ! -f "$JIBBERS_IL2CPP_LIBRARY" ]; then
    candidate=$(/usr/bin/find "$app/Contents" -name 'GameAssembly.dylib' -type f -print -quit)
    [ -z "$candidate" ] || export JIBBERS_IL2CPP_LIBRARY="$candidate"
fi
export DOORSTOP_ENABLED=1 DOORSTOP_IGNORE_DISABLED_ENV=1
export DOORSTOP_TARGET_ASSEMBLY="$mods/mac/managed/Jibbers.Sdk.dll"
export DOORSTOP_CLR_RUNTIME_CORECLR_PATH="$core/libcoreclr.dylib" DOORSTOP_CLR_CORLIB_DIR="$core"
export SteamAppId=4397910 SteamGameId=4397910
inherited="${DYLD_INSERT_LIBRARIES:-${STEAM_DYLD_INSERT_LIBRARIES:-}}"
libraries="$mods/mac/libdoorstop.dylib${inherited:+:$inherited}"
echo "Starting native Jibbers ($arch) with experimental mods. Logs: $report"
cd "$root"
# arch is SIP-protected; pass DYLD settings with -e instead of exporting them to arch.
exec /usr/bin/arch -"$arch" \
    -e "DYLD_LIBRARY_PATH=$mods/mac:$core:${DYLD_LIBRARY_PATH:-}" \
    -e "DYLD_INSERT_LIBRARIES=$libraries" \
    "$exe" "$@" >> "$report" 2>&1
