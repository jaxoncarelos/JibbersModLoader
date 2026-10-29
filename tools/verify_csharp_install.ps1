param([string]$ModsZip = 'dist/JibbersCSharpMods.zip')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $repo ('out/csharp-install-smoke-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $ModsZip -DestinationPath "$stage/package"
function Fixture([string]$Name) {
    $fixtureRoot = Join-Path $stage $Name
    New-Item -ItemType Directory -Path "$fixtureRoot/JibbersMods/system/distribution", "$fixtureRoot/JibbersMods/cs" -Force | Out-Null
    foreach ($file in @('Jibbers.exe','version.dll','JibbersMods/jibbers_dotnet.dll','JibbersMods/jibbers_atmosphere.dll',
        'JibbersMods/jibbers_reshred.dll','JibbersMods/jibbers_fpv.dll','JibbersMods/cs/Atmosphere.dll','JibbersMods/cs/Filmer.dll',
        'JibbersMods/cs/RayTracing.dll','jibbers_atmosphere.json','jibbers_atmosphere.bundle','JibbersMods/reshred.settings.json')) {
        [IO.File]::WriteAllText((Join-Path $fixtureRoot $file), "original $file")
    }
    $config = '{"autoUpdate":false,"wanted":["jibbers_atmosphere","jibbers_reshred","jibbers_fpv","local_mod"],"catalogUrl":"https://example.com/catalog.json"}'
    [IO.File]::WriteAllText("$fixtureRoot/JibbersMods/system/distribution/preferences.json", $config)
    $installed = '{"sequence":100,"packages":{"loader":{"version":"0.2.1","files":[]},"jibbers_atmosphere":{"version":"0.1.2","files":[]},"jibbers_fpv":{"version":"0.1.0","files":[]},"local_mod":{"version":"1.0.0","files":[]}}}'
    [IO.File]::WriteAllText("$fixtureRoot/JibbersMods/system/distribution/installed.json", $installed)
    return $fixtureRoot
}
$success = Fixture 'success'
& "$stage/package/install.ps1" -GameDir $success
foreach ($file in @('jibbers_atmosphere','jibbers_reshred','jibbers_fpv')) {
    if (Test-Path -LiteralPath "$success/JibbersMods/$file.dll") { throw 'Native duplicate survived installation' }
}
foreach ($relative in @('JibbersMods/cs/Atmosphere.dll','JibbersMods/cs/RayTracing.dll')) {
    if (Test-Path -LiteralPath (Join-Path $success $relative)) { throw 'Conflicting manual mod survived migration' }
}
foreach ($relative in @('AlpineVision/AlpineVision.dll','Reshred/Reshred.dll','FPV/FPV.dll','jibbers_slowmo.dll','jibbers_rumble.dll','jibbers_slvsh.dll')) {
    if (-not (Test-Path -LiteralPath "$success/JibbersMods/cs/$relative")) { throw "Managed mod missing: $relative" }
}
foreach ($file in @('version.dll','JibbersMods/cs/Filmer.dll','jibbers_atmosphere.json','JibbersMods/reshred.settings.json')) {
    if ([IO.File]::ReadAllText((Join-Path $success $file)) -ne "original $file") { throw 'Settings/local mods/loader changed' }
}
$config = Get-Content "$success/JibbersMods/system/distribution/preferences.json" -Raw | ConvertFrom-Json
if ($config.autoUpdate -or @($config.wanted).Count -ne 7 -or 'local_mod' -notin $config.wanted -or 'jibbers_fpv' -notin $config.wanted -or 'jibbers_slowmo' -notin $config.wanted -or 'jibbers_rumble' -notin $config.wanted -or 'jibbers_slvsh' -notin $config.wanted -or $config.catalogUrl -ne 'https://example.com/catalog.json') { throw 'Update selections or unrelated preferences changed' }
$record = Get-Content "$success/JibbersMods/system/distribution/installed.json" -Raw | ConvertFrom-Json
if ($record.sequence -ne 100 -or $record.packages.loader.version -ne '0.2.1' -or $record.packages.local_mod.version -ne '1.0.0' -or $record.packages.jibbers_atmosphere.version -ne '0.0.0' -or @($record.packages.jibbers_atmosphere.files).Count -ne 1) { throw 'C# bundle adoption or unrelated package records were not preserved correctly' }
if (@(Get-ChildItem "$success/JibbersMods/system" -Directory -Filter 'before-csharp-*').Count -ne 1) { throw 'Backup missing' }

$rollback = Fixture 'rollback'
$before = @(Get-ChildItem -LiteralPath $rollback -Recurse -File | ForEach-Object { [pscustomobject]@{path=$_.FullName; hash=(Get-FileHash -LiteralPath $_.FullName).Hash} })
$locked = [IO.File]::Open("$rollback/JibbersMods/jibbers_reshred.dll", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$failed = $false
try {
    try { & "$stage/package/install.ps1" -GameDir $rollback } catch { $failed = $true }
} finally { $locked.Dispose() }
if (-not $failed) { throw 'Expected locked-file installation failure' }
foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.path).Hash -ne $file.hash) { throw "Rollback changed $($file.path)" } }
if (Test-Path "$rollback/JibbersMods/cs/FPV/FPV.dll") { throw 'Rollback left a newly installed mod active' }
Write-Host 'Passed C# install and locked-DLL rollback; settings, local mods, loader and unrelated manager state preserved.'
Write-Host "Fixtures retained in $stage"
