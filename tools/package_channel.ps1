# Build a reviewable, unsigned release. Signing/publishing is a separate step in Actions.
[CmdletBinding()]
param([UInt64]$Sequence = 1, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Invoke-ChannelCargo([string[]]$Arguments, [string]$Dir = $root) {
        Push-Location $Dir
        try { & cargo @Arguments; if ($LASTEXITCODE -ne 0) { throw "cargo failed ($LASTEXITCODE)" } }
        finally { Pop-Location }
    }
    if (-not $SkipTests) {
        Invoke-ChannelCargo -Arguments @('test', '--workspace', '--features', 'jibbers-hotload/release')
        foreach ($mod in @('JibbersFPV')) {
            Invoke-ChannelCargo -Arguments @('test', '--lib') -Dir (Join-Path $root "mods/$mod")
        }
    }
    Invoke-ChannelCargo -Arguments @('build','--release','-p','jibbers-hotload','--features','release','-p','jibbers-bootstrap','-p','jibbers-distribution')
    $stage = Join-Path $root ('out/channel-' + [guid]::NewGuid().ToString('N'))
    $payload = Join-Path $stage 'payload'
    $installer = Join-Path $stage 'JibbersModManager'
    New-Item -ItemType Directory -Path "$payload/JibbersMods/system", "$installer/JibbersMods/system", "$stage/bin" -Force | Out-Null
    Copy-Item -LiteralPath 'target/release/version.dll' -Destination "$payload/JibbersMods/system/jibbers_loader.dll"
    Copy-Item -LiteralPath 'target/release/version.dll' -Destination "$installer/JibbersMods/system/jibbers_loader.dll"
    Copy-Item -LiteralPath 'target/release/jibbers_bootstrap.dll' -Destination "$installer/version.dll"
    Copy-Item -LiteralPath 'target/release/jibbers-publish.exe' -Destination "$stage/bin/jibbers-publish.exe"
    Copy-Item -LiteralPath 'tools/install_manager.ps1' -Destination "$installer/install.ps1"
    Copy-Item -LiteralPath 'LICENSE' -Destination "$installer/LICENSE"
    Copy-Item -LiteralPath 'docs/DISTRIBUTION.md' -Destination "$installer/README.md"
    $packages = [Collections.Generic.List[object]]::new()
    $packages.Add([ordered]@{ id='loader'; name='Jibbers Mod Loader'; description='The shared Mods menu, game integration and mod manager.'; version='0.2.1'; files=@('JibbersMods/system/jibbers_loader.dll') })
    foreach ($entry in @(
        @{ feature='atmosphere-only'; id='jibbers_atmosphere'; name='Atmosphere'; description='Weather, snow, fog and colour grading.'; version='0.1.2' },
        @{ feature='reshred-only'; id='jibbers_reshred'; name='Rewind'; description='Record, scrub and resume your run.'; version='0.1.2' }
    )) {
        Invoke-ChannelCargo -Arguments @('build','--release','-p','jibbers-hotload-core','--features',$entry.feature)
        Copy-Item -LiteralPath 'target/release/jibbers_hotload_core.dll' -Destination "$payload/JibbersMods/$($entry.id).dll"
        $files = @("JibbersMods/$($entry.id).dll")
        if ($entry.id -eq 'jibbers_atmosphere') {
            Copy-Item -LiteralPath 'mods/JibbersAtmosphere/build/jibbers_atmosphere.bundle' -Destination "$payload/jibbers_atmosphere.bundle"
            $files += 'jibbers_atmosphere.bundle'
        }
        $packages.Add([ordered]@{id=$entry.id; name=$entry.name; description=$entry.description; version=$entry.version; files=$files})
    }
    foreach ($entry in @(
        @{folder='JibbersFPV'; id='jibbers_fpv'; name='FPV Drone'; description='Fly and film with a controllable FPV drone.'; version='0.1.0'}
    )) {
        Invoke-ChannelCargo -Arguments @('build','--release') -Dir (Join-Path $root "mods/$($entry.folder)")
        Copy-Item -LiteralPath "mods/$($entry.folder)/target/release/$($entry.id).dll" -Destination "$payload/JibbersMods/$($entry.id).dll"
        $files = @("JibbersMods/$($entry.id).dll")
        if ($entry.id -eq 'jibbers_fpv') {
            Copy-Item -LiteralPath 'mods/JibbersFPV/build/jibbers_fpv.bundle' -Destination "$payload/JibbersMods/jibbers_fpv.bundle"
            $files += 'JibbersMods/jibbers_fpv.bundle'
        }
        $packages.Add([ordered]@{id=$entry.id; name=$entry.name; description=$entry.description; version=$entry.version; files=$files})
    }
    $spec = [ordered]@{sequence=$Sequence; baseUrl='https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable'; packages=@($packages.ToArray())}
    [IO.File]::WriteAllText("$stage/spec.json", ($spec | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    # Preserve third-party asset licensing alongside source and release artifacts.
    Copy-Item -LiteralPath 'mods/JibbersAtmosphere/unity/Assets/Atmosphere/Snow/LICENSE.txt' -Destination "$installer/SNOW-TEXTURES-LICENSE.txt"
    & python tools/third_party_notices.py --out "$installer/THIRD-PARTY-NOTICES.txt" Cargo.toml mods/JibbersFPV/Cargo.toml
    if ($LASTEXITCODE -ne 0) { throw 'Dependency license collection failed.' }
    # Actions transports the exact built payload to a separately permissioned publishing job.
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    Compress-Archive -Path "$stage/*" -DestinationPath "$dist/JibbersChannel-Unsigned.zip" -Force
    Write-Host "Unsigned build ready: $dist/JibbersChannel-Unsigned.zip"
    Write-Host "Staging retained: $stage"
} finally { Pop-Location }
