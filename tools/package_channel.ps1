# Build an unsigned managed update, using paths understood by installed bootstraps.
[CmdletBinding()]
param([UInt64]$Sequence = 1, [string]$Version = '0.3.0', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$previousLoaderVersion = $env:JIBBERS_LOADER_VERSION
$env:JIBBERS_LOADER_VERSION = $Version
Push-Location $root
try {
    if (-not $SkipTests) {
        & cargo test -p jibbers-distribution
        if ($LASTEXITCODE -ne 0) { throw 'Distribution migration tests failed' }
    }
    $buildArguments = @{BundleRuntime=$true}
    if ($SkipTests) { $buildArguments.SkipTests = $true }
    & ./tools/package_dotnet.ps1 @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Managed package build failed' }
    & cargo build --release -p jibbers-distribution
    if ($LASTEXITCODE -ne 0) { throw 'Channel publisher build failed' }
    & cargo build --release -p jibbers-hotload --example verify_managed_runtime
    if ($LASTEXITCODE -ne 0) { throw 'Runtime migration probe build failed' }
    $stage = Join-Path $root ('out/channel-' + [guid]::NewGuid().ToString('N'))
    $payload = Join-Path $stage 'payload'
    $installer = Join-Path $stage 'JibbersModManager'
    New-Item -ItemType Directory -Path "$payload/JibbersMods/system", "$payload/JibbersMods/packages/jibbers_dotnet", "$installer/JibbersMods/system", "$installer/JibbersMods/licenses", "$stage/bin" -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    function Extract-Entry($Archive, [string]$Name, [string]$Destination) {
        $entry = $Archive.Entries | Where-Object { $_.FullName.Replace('\','/') -eq $Name } | Select-Object -First 1
        if (-not $entry) { throw "Missing package entry $Name" }
        New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $Destination, $true)
    }
    $player = [IO.Compression.ZipFile]::OpenRead((Join-Path $root 'dist/JibbersCSharp.zip'))
    try {
        Extract-Entry $player 'version.dll' "$installer/version.dll"
        Extract-Entry $player 'JibbersMods/system/jibbers_loader.dll' "$payload/JibbersMods/system/jibbers_loader.dll"
        Extract-Entry $player 'JibbersMods/jibbers_dotnet.dll' "$payload/JibbersMods/packages/jibbers_dotnet/jibbers_dotnet.dll"
    } finally { $player.Dispose() }
    Copy-Item "$payload/JibbersMods/system/jibbers_loader.dll" "$installer/JibbersMods/system/jibbers_loader.dll"
    & python tools/package_managed_runtime.py --player dist/JibbersCSharp.zip --out "$payload/JibbersMods/packages/jibbers_dotnet/runtime.zip"
    if ($LASTEXITCODE -ne 0) { throw 'Managed runtime archive failed' }
    Copy-Item 'target/release/jibbers-publish.exe' "$stage/bin/jibbers-publish.exe"
    Copy-Item 'target/release/examples/verify_managed_runtime.exe' "$stage/bin/verify_managed_runtime.exe"
    Copy-Item 'LICENSE' "$installer/JibbersMods/licenses/LICENSE"
    $sourceCommit = (& git rev-parse HEAD).Trim()
    [IO.File]::WriteAllText("$installer/JibbersMods/README.txt", "Close Jibbers. Extract version.dll and JibbersMods beside Jibbers.exe. Launch and open Options > Mods.`nRequired runtime installs automatically on first launch (internet required).`nJibbers Mod Loader $Version. GPL-3.0-only. Source: https://github.com/jaxoncarelos/JibbersModLoader/tree/$sourceCommit`n", [Text.UTF8Encoding]::new($false))
    & python tools/third_party_notices.py --out "$installer/JibbersMods/licenses/THIRD-PARTY-NOTICES.txt" Cargo.toml sdk/crates/jibbers-dotnet-bridge/Cargo.toml
    if ($LASTEXITCODE -ne 0) { throw 'License collection failed' }
    $packages = [Collections.Generic.List[object]]::new()
    $packages.Add([ordered]@{id='loader'; name='Jibbers Mod Loader'; description='Phone menu, startup updates and C# mod loading.'; version=$Version; dependencies=@('jibbers_dotnet'); files=@('JibbersMods/system/jibbers_loader.dll')})
    $packages.Add([ordered]@{id='jibbers_dotnet'; name='C# Runtime'; description='Required shared C# SDK and bundled .NET runtime.'; version=$Version; files=@('JibbersMods/packages/jibbers_dotnet/jibbers_dotnet.dll','JibbersMods/packages/jibbers_dotnet/runtime.zip')})
    $mods = [IO.Compression.ZipFile]::OpenRead((Join-Path $root 'dist/JibbersCSharpMods.zip'))
    try {
        foreach ($entry in @(
            @{folder='AlpineVision'; id='jibbers_atmosphere'; name='Alpine Vision'; description='Atmosphere extended: weather, alpine sky, lighting and a URP cinematic finish. Retains game shadows. Replaces Atmosphere.'; asset='jibbers_alpinevision.bundle'},
            @{folder='Reshred'; id='jibbers_reshred'; name='Rewind'; description='C# physics history, hold to rewind and resume.'; asset=$null},
            @{folder='FPV'; id='jibbers_fpv'; name='FPV Drone'; description='C# drone flight, camera, controls, HUD and audio.'; asset='JibbersMods/jibbers_fpv.bundle'},
            @{folder='SlowMo'; id='jibbers_slowmo'; name='Slow Motion'; description='Click the left stick or press V to slow the game down, with smooth easing.'; asset=$null; managed=$true}
        )) {
            # Historical paths let the installed bootstrap replace native DLLs atomically.
            # The new loader routes these CLR assemblies to the C# host. Mods that never had a
            # native version install straight into the C# host's folder instead.
            $dll = if ($entry.managed) { "JibbersMods/cs/$($entry.id).dll" } else { "JibbersMods/$($entry.id).dll" }
            Extract-Entry $mods "JibbersMods/cs/$($entry.folder)/$($entry.folder).dll" "$payload/$dll"
            $files = @($dll)
            if ($entry.asset) {
                $assetPath = if ($entry.folder -eq 'AlpineVision') { 'JibbersMods/packages/jibbers_atmosphere/jibbers_alpinevision.bundle' } else { $entry.asset }
                Extract-Entry $mods $entry.asset "$payload/$assetPath"; $files += $assetPath
            }
            $packageVersion = if ($entry.folder -eq 'AlpineVision') { '1.2.8' } else { $Version }
            $packages.Add([ordered]@{id=$entry.id; name=$entry.name; description=$entry.description; version=$packageVersion; dependencies=@('jibbers_dotnet'); files=$files})
        }
    } finally { $mods.Dispose() }
    $spec = [ordered]@{sequence=$Sequence; baseUrl='https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable'; packages=@($packages.ToArray())}
    [IO.File]::WriteAllText("$stage/spec.json", ($spec | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Compress-Archive -Path "$stage/*" -DestinationPath 'dist/JibbersChannel-Unsigned.zip' -Force
    Write-Host "Unsigned C# channel ready: $stage"
} finally {
    $env:JIBBERS_LOADER_VERSION = $previousLoaderVersion
    Pop-Location
}
