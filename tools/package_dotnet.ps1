# Builds the C# modding packages:
#   dist/JibbersCSharp.zip      for players: drop into the game folder
#       version.dll                          stable bootstrap (preserves automatic updates)
#       JibbersMods/system/jibbers_loader.dll replaceable release loader
#       JibbersMods/jibbers_dotnet.dll       starts .NET inside the game
#       JibbersMods/dotnet/Jibbers.Sdk.*     the C# SDK (+ runtimeconfig, docs xml)
#       JibbersMods/dotnet/runtime/          the .NET 8 runtime (with -BundleRuntime): no install needed
#       JibbersMods/cs/                      C# mods go here
#   dist/JibbersCSharpSDK.zip   for modders: the SDK, template, examples, generator, guides, docs site,
#                               and the C# ports of the mods (mods/cs) as sources
#   dist/JibbersCSharpMods.zip  the ported mods, built: JibbersMods/cs/<Name>/<Name>.dll (+ asset files)
#
# -BundleRuntime   copy the installed .NET 8 runtime (host/fxr + Microsoft.NETCore.App) into the player package
# -SkipTests       skip the Rust and C# tests
# Does not read or start the game.
[CmdletBinding()]
param([switch]$BundleRuntime, [switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Run([string]$exe, [string[]]$a, [string]$dir = $root) {
        Push-Location $dir
        try {
            & $exe @a
            if ($LASTEXITCODE -ne 0) { throw "$exe $a failed ($LASTEXITCODE)" }
        } finally { Pop-Location }
    }
    $sdk = Join-Path $root 'sdk'
    $dotnet = Join-Path $sdk 'dotnet'

    if (-not $SkipTests) {
        Run cargo @('test', '-p', 'jibbers-hotload', '--lib', '--features', 'release')
        Run cargo @('test', '-p', 'jibbers-dotnet-bridge', '-p', 'jibbers-mod-sdk') $sdk
    }
    Run cargo @('build', '--release', '-p', 'jibbers-hotload', '-p', 'jibbers-bootstrap', '--features', 'release', '--target-dir', 'target/mod-release/loader')
    Run cargo @('build', '--release', '-p', 'jibbers-dotnet-bridge') $sdk
    Run dotnet @('build', 'Jibbers.Sdk', '-c', 'Release', '-nologo', '-v', 'q') $dotnet
    if (-not $SkipTests) { Run dotnet @('run', '--project', 'Jibbers.Sdk.Tests', '-c', 'Release', '-p:JibbersNoInstall=true') $dotnet }
    # The template and the examples must build against the SDK (without installing into a game).
    foreach ($p in @('template/MyMod') + (Get-ChildItem (Join-Path $dotnet 'examples') -Directory | ForEach-Object { 'examples/' + $_.Name })) {
        Run dotnet @('build', $p, '-c', 'Release', '-nologo', '-v', 'q', '-p:JibbersNoInstall=true', "-p:JibbersSdk=$dotnet/Jibbers.Sdk/bin/Release/net8.0/Jibbers.Sdk.dll") $dotnet
    }
    # The C# ports of the mods.
    $publicNames = @('Atmosphere', 'Reshred', 'FPV')
    $ports = @($publicNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $root "mods/cs/$_") })
    foreach ($p in $ports) {
        Run dotnet @('build', $p.FullName, '-c', 'Release', '-nologo', '-v', 'q', '-p:JibbersNoInstall=true') $root
    }

    $loader = Join-Path $root 'target/mod-release/loader/release/jibbers_bootstrap.dll'
    $implementation = Join-Path $root 'target/mod-release/loader/release/version.dll'
    $bridge = Join-Path $sdk 'target/release/jibbers_dotnet.dll'
    $sdkBin = Join-Path $dotnet 'Jibbers.Sdk/bin/Release/net8.0'
    foreach ($f in @($loader, $implementation, $bridge, (Join-Path $sdkBin 'Jibbers.Sdk.dll'), (Join-Path $sdkBin 'Jibbers.Sdk.runtimeconfig.json'))) {
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { throw "Missing build output $f" }
    }

    $stage = Join-Path $root ('out/dotnet-' + [guid]::NewGuid().ToString('N'))
    $game = Join-Path $stage 'JibbersCSharp'
    $mods = Join-Path $game 'JibbersMods'
    New-Item -ItemType Directory -Path (Join-Path $mods 'dotnet'), (Join-Path $mods 'cs'), (Join-Path $mods 'system') -Force | Out-Null
    Copy-Item -LiteralPath $loader -Destination (Join-Path $game 'version.dll')
    Copy-Item -LiteralPath $implementation -Destination (Join-Path $mods 'system/jibbers_loader.dll')
    Copy-Item -LiteralPath $bridge -Destination (Join-Path $mods 'jibbers_dotnet.dll')
    foreach ($n in @('Jibbers.Sdk.dll', 'Jibbers.Sdk.xml', 'Jibbers.Sdk.runtimeconfig.json', 'Jibbers.Sdk.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $sdkBin $n) -Destination (Join-Path $mods 'dotnet')
    }
    Set-Content -LiteralPath (Join-Path $mods 'cs/README.txt') -Value 'Put C# mods (their .dll, or a folder <Name>\<Name>.dll with dependencies) here, then restart the game.' -Encoding utf8

    if ($BundleRuntime) {
        $dn = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Join-Path $env:ProgramFiles 'dotnet' }
        $fxr = Get-ChildItem (Join-Path $dn 'host/fxr') -Directory | Where-Object { $_.Name -like '8.*' } | Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
        $rt = Get-ChildItem (Join-Path $dn 'shared/Microsoft.NETCore.App') -Directory | Where-Object { $_.Name -like '8.*' } | Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
        if (-not $fxr -or -not $rt) { throw ".NET 8 runtime not found under $dn" }
        $dst = Join-Path $mods 'dotnet/runtime'
        & robocopy $fxr.FullName (Join-Path $dst "host/fxr/$($fxr.Name)") /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "hostfxr copy failed ($LASTEXITCODE)" }
        & robocopy $rt.FullName (Join-Path $dst "shared/Microsoft.NETCore.App/$($rt.Name)") /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
        $global:LASTEXITCODE = 0
        Copy-Item -LiteralPath (Join-Path $dn 'LICENSE.txt') -Destination (Join-Path $dst 'LICENSE.txt') -ErrorAction SilentlyContinue
        Copy-Item -LiteralPath (Join-Path $dn 'ThirdPartyNotices.txt') -Destination (Join-Path $dst 'ThirdPartyNotices.txt') -ErrorAction SilentlyContinue
        Write-Host "Bundled .NET runtime $($rt.Name) (hostfxr $($fxr.Name))"
    }

    # Modder kit: SDK sources and binaries, template, examples, generator, guides, docs site.
    $kit = Join-Path $stage 'JibbersCSharpSDK'
    & robocopy $dotnet $kit /E /XD bin obj /XF ModPortTests.cs SkiSmoothTests.cs FastLoadTests.cs CollisionStreamTests.cs /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "SDK copy failed ($LASTEXITCODE)" }
    & robocopy (Join-Path $root 'docs') (Join-Path $kit 'site') /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
    New-Item -ItemType Directory -Path (Join-Path $kit 'lib') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sdkBin 'Jibbers.Sdk.dll'), (Join-Path $sdkBin 'Jibbers.Sdk.xml') -Destination (Join-Path $kit 'lib')
    Copy-Item -LiteralPath (Join-Path $sdk 'crates/jibbers-mod-sdk/symbols.json') -Destination (Join-Path $kit 'symbols.json')
    New-Item -ItemType Directory -Path (Join-Path $kit 'mods') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'mods/cs/Directory.Build.props') -Destination (Join-Path $kit 'mods')
    foreach ($p in $ports) {
        & robocopy $p.FullName (Join-Path $kit "mods/$($p.Name)") /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    }
    $global:LASTEXITCODE = 0
    # The kit's mods build against the kit's lib/ instead of the repo's SDK build.
    $props = Join-Path $kit 'mods/Directory.Build.props'
    (Get-Content -LiteralPath $props -Raw).Replace('..\..\sdk\dotnet\Jibbers.Sdk\bin\Release\net8.0\Jibbers.Sdk.dll', '..\lib\Jibbers.Sdk.dll') | Set-Content -LiteralPath $props -Encoding utf8

    $tests = Join-Path $kit 'Jibbers.Sdk.Tests/Jibbers.Sdk.Tests.csproj'
    (Get-Content -LiteralPath $tests -Raw).Replace('..\..\..\mods\cs\', '..\mods\') | Set-Content -LiteralPath $tests -Encoding utf8

    # Built mods. Atmosphere also needs its asset bundle beside the game executable.
    $built = Join-Path $stage 'JibbersCSharpMods'
    foreach ($p in $ports) {
        $dll = Join-Path $p.FullName "bin/Release/net8.0/$($p.Name).dll"
        if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Missing build output $dll" }
        $dst = Join-Path $built "JibbersMods/cs/$($p.Name)"
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item -LiteralPath $dll -Destination $dst
    }
    Copy-Item -LiteralPath (Join-Path $root 'mods/JibbersAtmosphere/build/jibbers_atmosphere.bundle') -Destination $built
    Copy-Item -LiteralPath (Join-Path $root 'mods/JibbersFPV/build/jibbers_fpv.bundle') -Destination (Join-Path $built 'JibbersMods')
    foreach ($folder in @($game, $kit, $built)) {
        Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $folder
        Copy-Item -LiteralPath (Join-Path $root 'mods/JibbersAtmosphere/unity/Assets/Atmosphere/Snow/LICENSE.txt') -Destination (Join-Path $folder 'SNOW-TEXTURES-LICENSE.txt')
    }
    Run python @('tools/third_party_notices.py', '--out', (Join-Path $game 'THIRD-PARTY-NOTICES.txt'), 'Cargo.toml', 'sdk/crates/jibbers-dotnet-bridge/Cargo.toml')
    foreach ($folder in @($kit, $built)) { Copy-Item -LiteralPath (Join-Path $game 'THIRD-PARTY-NOTICES.txt') -Destination $folder }
    Copy-Item -LiteralPath (Join-Path $root 'docs/CSHARP-REWRITE.md') -Destination (Join-Path $kit 'README.md')
    Copy-Item -LiteralPath (Join-Path $root 'tools/install_csharp_mods.ps1') -Destination (Join-Path $built 'install.ps1')
    Copy-Item -LiteralPath (Join-Path $root 'docs/CSHARP-MODS-INSTALL.txt') -Destination (Join-Path $built 'README.txt')
    Copy-Item -LiteralPath (Join-Path $root 'docs/CSHARP-RUNTIME-INSTALL.txt') -Destination (Join-Path $game 'README.txt')

    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    foreach ($pair in @(@($game, 'JibbersCSharp.zip'), @($kit, 'JibbersCSharpSDK.zip'), @($built, 'JibbersCSharpMods.zip'))) {
        $zip = Join-Path $dist $pair[1]
        Compress-Archive -Path (Join-Path $pair[0] '*') -DestinationPath $zip -Force
        Write-Host ("{0}  {1:N1} MB  sha256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant())
    }
    $hashes = foreach ($name in @('JibbersCSharp.zip','JibbersCSharpMods.zip','JibbersCSharpSDK.zip')) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $dist $name)).Hash.ToLowerInvariant(), $name
    }
    [IO.File]::WriteAllLines((Join-Path $dist 'JibbersCSharp-SHA256SUMS.txt'), $hashes)
    if ($BundleRuntime) { Run python @('tools/verify_csharp_packages.py', $dist) }
    if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'verify_csharp_install.ps1') -ModsZip (Join-Path $dist 'JibbersCSharpMods.zip') }
} finally {
    Pop-Location
}
