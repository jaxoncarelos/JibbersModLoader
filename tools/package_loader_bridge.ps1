# Prepare the legacy-compatible SDK updater payload, without signing or publishing it.
[CmdletBinding()]
param([string]$PreviousReleaseDir = 'dist/sdk-release/0.1.1', [string]$OutDir = 'dist/loader-bridge-unsigned', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Invoke-BridgeCargo([string[]]$Arguments) {
        & cargo @Arguments
        if ($LASTEXITCODE -ne 0) { throw "cargo failed ($LASTEXITCODE)" }
    }
    if (-not $SkipTests) {
        Invoke-BridgeCargo -Arguments @('test','--manifest-path','sdk/Cargo.toml','-p','jibbers-sdk-runtime','-p','jibbers-sdk-publish')
    }
    $env:JIBBERS_SDK_UPDATE_URL = 'https://github.com/jaxoncarelos/JibbersModLoader/releases/latest/download'
    Invoke-BridgeCargo -Arguments @('build','--release','--manifest-path','sdk/Cargo.toml','-p','jibbers-sdk-runtime','-p','jibbers-sdk-publish')
    Invoke-BridgeCargo -Arguments @('build','--release','-p','jibbers-bootstrap','-p','jibbers-hotload','--features','release')
    $out = [IO.Path]::GetFullPath((Join-Path $root $OutDir))
    if (Test-Path -LiteralPath $out) { throw 'Use a fresh output directory for the loader bridge.' }
    New-Item -ItemType Directory -Path "$out/bundle", "$out/bin", "$out/loader" -Force | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $PreviousReleaseDir 'manifest.json'))) {
        $PreviousReleaseDir = "$out/previous"
        & gh release download v0.1.1 --repo jaxoncarelos/JibbersModLoader --pattern 'sdk__*' --pattern manifest.json --pattern manifest.json.sig --pattern jibbers_sdk.dll --dir $PreviousReleaseDir
        if ($LASTEXITCODE -ne 0) { throw 'Could not obtain the previous signed SDK release.' }
    }
    & sdk/target/release/sdk-publish.exe verify --dir $PreviousReleaseDir
    if ($LASTEXITCODE -ne 0) { throw 'The previous SDK release is not trusted.' }
    # Preserve every old SDK source/doc file. A small bridge release must not make the legacy
    # updater delete sources just because the new codebase has grown beyond its file limits.
    $previous = Get-Content -LiteralPath (Join-Path $PreviousReleaseDir 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $previous.files) {
        if ($file.path.StartsWith('sdk/') -and $file.path -ne 'sdk/compat.json') {
            $dest = Join-Path "$out/bundle" $file.path.Substring(4)
            New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $PreviousReleaseDir $file.asset) -Destination $dest
        }
    }
    Copy-Item -LiteralPath 'sdk/target/release/jibbers_sdk.dll' -Destination "$out/runtime.dll"
    Copy-Item -LiteralPath 'sdk/target/release/sdk-publish.exe' -Destination "$out/bin/sdk-publish.exe"
    Copy-Item -LiteralPath 'target/release/jibbers_bootstrap.dll' -Destination "$out/loader/version.dll"
    Copy-Item -LiteralPath 'target/release/version.dll' -Destination "$out/loader/jibbers_loader.dll"
    Copy-Item -LiteralPath 'target/release/jibbers-migrate.exe' -Destination "$out/loader/jibbers-migrate.exe"
    Copy-Item -LiteralPath 'docs/LOADER-MIGRATION.md' -Destination "$out/bundle/LOADER-MIGRATION.md"
    Copy-Item -LiteralPath 'sdk/compat/compat.json' -Destination "$out/compat.json"
    Write-Host "Unsigned legacy-compatible loader bridge prepared in $out"
} finally { Pop-Location }
