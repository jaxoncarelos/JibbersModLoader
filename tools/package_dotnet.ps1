# Builds the C# modding packages:
#   dist/JibbersCSharp.zip      for players: drop into the game folder
#       version.dll                          release loader (with jh_register_plugin)
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
    Run cargo @('build', '--release', '-p', 'jibbers-hotload', '--features', 'release', '--target-dir', 'target/mod-release/loader')
    Run cargo @('build', '--release', '-p', 'jibbers-dotnet-bridge') $sdk
    Run dotnet @('build', 'Jibbers.Sdk', '-c', 'Release', '-nologo', '-v', 'q') $dotnet
    if (-not $SkipTests) { Run dotnet @('run', '--project', 'Jibbers.Sdk.Tests', '-c', 'Release') $dotnet }
    # The template and the examples must build against the SDK (without installing into a game).
    foreach ($p in @('template/MyMod') + (Get-ChildItem (Join-Path $dotnet 'examples') -Directory | ForEach-Object { 'examples/' + $_.Name })) {
        Run dotnet @('build', $p, '-c', 'Release', '-nologo', '-v', 'q', '-p:JibbersNoInstall=true') $dotnet
    }
    # The C# ports of the mods.
    $ports = Get-ChildItem (Join-Path $root 'mods/cs') -Directory | Where-Object { Test-Path (Join-Path $_.FullName "$($_.Name).csproj") }
    foreach ($p in $ports) {
        Run dotnet @('build', $p.FullName, '-c', 'Release', '-nologo', '-v', 'q', '-p:JibbersNoInstall=true') $root
    }

    $loader = Join-Path $root 'target/mod-release/loader/release/version.dll'
    $bridge = Join-Path $sdk 'target/release/jibbers_dotnet.dll'
    $sdkBin = Join-Path $dotnet 'Jibbers.Sdk/bin/Release/net8.0'
    foreach ($f in @($loader, $bridge, (Join-Path $sdkBin 'Jibbers.Sdk.dll'), (Join-Path $sdkBin 'Jibbers.Sdk.runtimeconfig.json'))) {
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { throw "Missing build output $f" }
    }

    $stage = Join-Path $root ('out/dotnet-' + [guid]::NewGuid().ToString('N'))
    $game = Join-Path $stage 'JibbersCSharp'
    $mods = Join-Path $game 'JibbersMods'
    New-Item -ItemType Directory -Path (Join-Path $mods 'dotnet'), (Join-Path $mods 'cs') -Force | Out-Null
    Copy-Item -LiteralPath $loader -Destination (Join-Path $game 'version.dll')
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
        & robocopy $rt.FullName (Join-Path $dst "shared/Microsoft.NETCore.App/$($rt.Name)") /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
        $global:LASTEXITCODE = 0
        Copy-Item -LiteralPath (Join-Path $dn 'LICENSE.txt') -Destination (Join-Path $dst 'LICENSE.txt') -ErrorAction SilentlyContinue
        Copy-Item -LiteralPath (Join-Path $dn 'ThirdPartyNotices.txt') -Destination (Join-Path $dst 'ThirdPartyNotices.txt') -ErrorAction SilentlyContinue
        Write-Host "Bundled .NET runtime $($rt.Name) (hostfxr $($fxr.Name))"
    }

    # Modder kit: SDK sources and binaries, template, examples, generator, guides, docs site.
    $kit = Join-Path $stage 'JibbersCSharpSDK'
    & robocopy $dotnet $kit /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    & robocopy (Join-Path $root 'docs') (Join-Path $kit 'site') /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
    New-Item -ItemType Directory -Path (Join-Path $kit 'lib') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sdkBin 'Jibbers.Sdk.dll'), (Join-Path $sdkBin 'Jibbers.Sdk.xml') -Destination (Join-Path $kit 'lib')
    Copy-Item -LiteralPath (Join-Path $sdk 'crates/jibbers-mod-sdk/symbols.json') -Destination (Join-Path $kit 'symbols.json')
    & robocopy (Join-Path $root 'mods/cs') (Join-Path $kit 'mods') /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
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

    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    foreach ($pair in @(@($game, 'JibbersCSharp.zip'), @($kit, 'JibbersCSharpSDK.zip'), @($built, 'JibbersCSharpMods.zip'))) {
        $zip = Join-Path $dist $pair[1]
        Compress-Archive -Path (Join-Path $pair[0] '*') -DestinationPath $zip -Force
        Write-Host ("{0}  {1:N1} MB  sha256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant())
    }
} finally {
    Pop-Location
}
