# Builds independent player plugins and one shared, server-free loader.
# Does not read the game installation, install files, or contact a running game.
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Invoke-Cargo([string[]]$CargoArgs) {
        & cargo @CargoArgs
        if ($LASTEXITCODE -ne 0) { throw "cargo $CargoArgs failed ($LASTEXITCODE)" }
    }
    if (-not $SkipTests) {
        Invoke-Cargo -CargoArgs @('test', '-p', 'jibbers-hotload-core', '--lib')
        Invoke-Cargo -CargoArgs @('test', '-p', 'jibbers-hotload-core', '--features', 'reshred-only', '--lib')
        Invoke-Cargo -CargoArgs @('test', '-p', 'jibbers-hotload-core', '--features', 'atmosphere-only', '--lib')
        Invoke-Cargo -CargoArgs @('test', '-p', 'jibbers-hotload', '--features', 'release', '--lib')
        Invoke-Cargo -CargoArgs @('test', '-p', 'jibbers-hotload', '--lib')
        Invoke-Cargo -CargoArgs @('check', '-p', 'jibbers-hotload')
    }
    $base = 'target/mod-release'
    Invoke-Cargo -CargoArgs @('build', '--release', '-p', 'jibbers-hotload', '--features', 'release', '--target-dir', "$base/loader")
    Invoke-Cargo -CargoArgs @('build', '--release', '-p', 'jibbers-hotload-core', '--features', 'atmosphere-only', '--target-dir', "$base/atmosphere")
    Invoke-Cargo -CargoArgs @('build', '--release', '-p', 'jibbers-hotload-core', '--features', 'reshred-only', '--target-dir', "$base/reshred")

    $dist = Join-Path $root 'dist'
    # Which game builds this release is validated for, from sdk/compat/compat.json (the same list the loader embeds).
    $buildsBlock = (& python (Join-Path $PSScriptRoot 'verify_mod_release.py') '--builds-block') -join "`r`n"
    if ($LASTEXITCODE -ne 0 -or -not $buildsBlock) { throw 'sdk/compat/compat.json is missing or invalid' }
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    # Fresh GUID staging directories avoid accidentally shipping stale DLLs/settings.
    $stage = Join-Path $root ('out/mod-release-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $payload = @{
        'version.dll' = "$base/loader/release/version.dll"
        'JibbersMods/jibbers_atmosphere.dll' = "$base/atmosphere/release/jibbers_hotload_core.dll"
        'JibbersMods/jibbers_reshred.dll' = "$base/reshred/release/jibbers_hotload_core.dll"
        'jibbers_atmosphere.bundle' = 'mods/JibbersAtmosphere/build/jibbers_atmosphere.bundle'
    }
    $archives = [ordered]@{
        'JibbersModLoader' = @('version.dll')
        'JibbersAtmosphere' = @('JibbersMods/jibbers_atmosphere.dll', 'jibbers_atmosphere.bundle')
        'JibbersReshred' = @('JibbersMods/jibbers_reshred.dll')
        'JibbersMods-Complete' = @('version.dll', 'JibbersMods/jibbers_atmosphere.dll', 'JibbersMods/jibbers_reshred.dll', 'jibbers_atmosphere.bundle')
        'JibbersAtmosphere-Standalone' = @('version.dll', 'JibbersMods/jibbers_atmosphere.dll', 'jibbers_atmosphere.bundle')
        'JibbersReshred-Standalone' = @('version.dll', 'JibbersMods/jibbers_reshred.dll')
    }
    $manifest = [ordered]@{
        version = '0.1.1'
        channel = 'release-candidate'
        builtUtc = [DateTime]::UtcNow.ToString('o')
        abi = 4
        platform = 'windows-x86_64'
        runtimeAcceptance = 'Pending offline in-game checks; builds and automated tests only'
        loader = 'release feature; no TCP server; shared Mods tab'
        payloads = @()
        archives = @()
    }
    foreach ($path in ($payload.Keys | Sort-Object)) {
        $source = Join-Path $root $payload[$path]
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing payload $source" }
        $manifest.payloads += [ordered]@{path=$path; bytes=(Get-Item -LiteralPath $source).Length; sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    foreach ($name in $archives.Keys) {
        $dir = Join-Path $stage $name
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        foreach ($path in $archives[$name]) {
            $dest = Join-Path $dir $path
            New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $root $payload[$path]) -Destination $dest
        }
        [IO.File]::WriteAllText((Join-Path $dir 'README.txt'), ([IO.File]::ReadAllText((Join-Path $root 'mods/RELEASE-README.txt')).Replace('{{VALIDATED_BUILDS}}', $buildsBlock)), (New-Object Text.UTF8Encoding $false))
        if ($archives[$name] -contains 'JibbersMods/jibbers_atmosphere.dll') {
            Copy-Item -LiteralPath 'mods/JibbersAtmosphere/README.txt' -Destination (Join-Path $dir 'ATMOSPHERE.txt')
            Copy-Item -LiteralPath 'mods/JibbersAtmosphere/unity/Assets/Atmosphere/Snow/LICENSE.txt' -Destination (Join-Path $dir 'SNOW-TEXTURES-LICENSE.txt')
        }
        if ($archives[$name] -contains 'JibbersMods/jibbers_reshred.dll') {
            Copy-Item -LiteralPath 'mods/JibbersReshred/README.txt' -Destination (Join-Path $dir 'RESHRED.txt')
        }
        $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File | ForEach-Object {
            [ordered]@{ path=$_.FullName.Substring($dir.Length + 1).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
        $archive = Join-Path $dist ($name + '.zip')
        Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $archive -Force
        $manifest.archives += [ordered]@{path=($name + '.zip'); sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); files=$files}
    }
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $dist 'mod-release-manifest.json') -Encoding UTF8
    $manifest.archives | ForEach-Object { "$($_.sha256)  $($_.path)" } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
    & python (Join-Path $PSScriptRoot 'verify_mod_release.py') $dist
    if ($LASTEXITCODE -ne 0) { throw 'Release archive verification failed' }
    Write-Host "Release candidates created and verified in $dist"
    Write-Host "Staging retained for inspection: $stage"
} finally {
    Pop-Location
}