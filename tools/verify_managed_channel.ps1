param([Parameter(Mandatory)][string]$ChannelDir, [Parameter(Mandatory)][string]$Publisher, [Parameter(Mandatory)][string]$RuntimeProbe)
$ErrorActionPreference = 'Stop'
& $Publisher verify $ChannelDir
if ($LASTEXITCODE -ne 0) { throw 'Signed managed channel failed verification' }
$repo = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repo ('out/managed-channel-smoke-' + [guid]::NewGuid().ToString('N'))
$distribution = Join-Path $fixture 'JibbersMods/system/distribution'
New-Item -ItemType Directory -Path $distribution -Force | Out-Null
$publicIds = @('jibbers_atmosphere','jibbers_reshred','jibbers_fpv')
$oldPackages = [ordered]@{}
foreach ($id in @('loader') + $publicIds) {
    $relative = if ($id -eq 'loader') { 'JibbersMods/system/jibbers_loader.dll' } else { "JibbersMods/$id.dll" }
    $path = Join-Path $fixture $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, "old native $id")
    $oldPackages[$id] = [ordered]@{version='0.0.0'; files=@([ordered]@{path=$relative;url='https://example.com/old.bin';size=(Get-Item $path).Length;sha256=(Get-FileHash $path).Hash.ToLowerInvariant()})}
}
[IO.File]::WriteAllText((Join-Path $distribution 'installed.json'), (@{sequence=1;packages=$oldPackages} | ConvertTo-Json -Depth 10))
[IO.File]::WriteAllText((Join-Path $distribution 'preferences.json'), (@{autoUpdate=$true;wanted=$publicIds;catalogUrl='https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable/catalog.json'} | ConvertTo-Json -Depth 5))
[IO.File]::WriteAllText((Join-Path $fixture 'version.dll'), 'existing stable bootstrap')
[IO.File]::WriteAllText((Join-Path $fixture 'JibbersMods/local.dll'), 'local mod')
[IO.File]::WriteAllText((Join-Path $fixture 'JibbersMods/fpv.settings.json'), '{"enabled":false,"fov":95}')
& $RuntimeProbe $fixture (Resolve-Path -LiteralPath $ChannelDir).Path
if ($LASTEXITCODE -ne 0) { throw 'Native to C# startup migration failed' }
# Also exercise the original pre-manager installation, with no preferences/ownership record.
$legacyFixture = Join-Path $fixture 'legacy-install'
foreach ($id in @('loader') + $publicIds) {
    $relative = if ($id -eq 'loader') { 'JibbersMods/system/jibbers_loader.dll' } else { "JibbersMods/$id.dll" }
    $path = Join-Path $legacyFixture $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, "legacy native $id")
}
& $RuntimeProbe $legacyFixture (Resolve-Path -LiteralPath $ChannelDir).Path
if ($LASTEXITCODE -ne 0) { throw 'Pre-manager native mod enrollment failed' }
foreach ($expected in @(
    @('version.dll','existing stable bootstrap'),
    @('JibbersMods/local.dll','local mod'),
    @('JibbersMods/fpv.settings.json','{"enabled":false,"fov":95}')
)) { if ([IO.File]::ReadAllText((Join-Path $fixture $expected[0])) -ne $expected[1]) { throw "Migration modified $($expected[0])" } }
$package = Join-Path $fixture 'JibbersMods/packages/jibbers_dotnet'
$identity = [IO.File]::ReadAllText((Join-Path $package 'active.txt'))
$sdk = Join-Path $fixture ('JibbersMods/system/clr/' + $identity.Substring(0,32) + '/Jibbers.Sdk.dll')
$sdkHash = (Get-FileHash $sdk).Hash
[IO.File]::WriteAllText($sdk, 'damaged cache')
# A failed later catalog fetch must still allow authenticated offline recovery.
[IO.File]::WriteAllText((Join-Path $distribution 'catalog.json'), '{}')
& $RuntimeProbe $fixture
if ($LASTEXITCODE -ne 0 -or (Get-FileHash $sdk).Hash -ne $sdkHash) { throw 'Offline cache repair failed' }
& (Join-Path $PSScriptRoot 'verify_csharp_runtime.ps1') -RuntimeDir $fixture -ModsDir $fixture -Channel
if ($LASTEXITCODE -ne 0) { throw 'Migrated C# runtime failed loading' }
Write-Host "Passed managed startup migration, cache repair and bundled runtime loading: $fixture"
