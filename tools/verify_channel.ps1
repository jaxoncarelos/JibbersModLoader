param([Parameter(Mandatory)][string]$ChannelDir, [Parameter(Mandatory)][string]$BootstrapZip, [Parameter(Mandatory)][string]$Publisher)
$ErrorActionPreference = 'Stop'
& $Publisher verify $ChannelDir
if ($LASTEXITCODE -ne 0) { throw 'Signed catalog verification failed.' }
$catalog = Get-Content -LiteralPath (Join-Path $ChannelDir 'catalog.json') -Raw | ConvertFrom-Json
$publicIds = @('loader','jibbers_dotnet','jibbers_atmosphere','jibbers_reshred','jibbers_fpv','jibbers_slowmo','jibbers_rumble','jibbers_slvsh')
if (@($catalog.packages).Count -ne $publicIds.Count -or @($catalog.packages | Where-Object { $_.id -notin $publicIds }).Count -gt 0) { throw 'Only the loader, FPV, Atmosphere, Rewind, Slow Motion, Rumble and SLVSH may enter the public channel.' }
$weather = @($catalog.packages | Where-Object id -eq 'jibbers_atmosphere')
if ($weather.Count -ne 1 -or $weather[0].name -ne 'Alpine Vision' -or 'JibbersMods/packages/jibbers_atmosphere/jibbers_alpinevision.bundle' -notin $weather[0].files.path) { throw 'Alpine Vision must replace the single existing Atmosphere slot.' }
$slowMotion = @($catalog.packages | Where-Object id -eq 'jibbers_slowmo')
if ($slowMotion.Count -ne 1 -or @($slowMotion[0].files.path) -join ',' -ne 'JibbersMods/cs/jibbers_slowmo.dll') { throw 'Slow Motion must install only into the C# mods folder.' }
$rumble = @($catalog.packages | Where-Object id -eq 'jibbers_rumble')
if ($rumble.Count -ne 1 -or @($rumble[0].files.path) -join ',' -ne 'JibbersMods/cs/jibbers_rumble.dll') { throw 'Rumble must install only into the C# mods folder.' }
$slvsh = @($catalog.packages | Where-Object id -eq 'jibbers_slvsh')
if ($slvsh.Count -ne 1 -or @($slvsh[0].files.path) -join ',' -ne 'JibbersMods/cs/jibbers_slvsh.dll') { throw 'SLVSH must install only into the C# mods folder.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $BootstrapZip).Path)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    $requiredFiles = @('version.dll','JibbersMods/system/jibbers_loader.dll','JibbersMods/licenses/LICENSE','JibbersMods/licenses/THIRD-PARTY-NOTICES.txt','JibbersMods/README.txt')
    foreach ($required in $requiredFiles) {
        if (-not ($names -contains $required)) { throw "Bootstrap package is missing $required" }
    }
    foreach ($entry in $archive.Entries) {
        if ($entry.Name -ne '' -and $entry.FullName.Replace('\','/') -notin $requiredFiles) { throw "Unexpected file in minimal bootstrap: $($entry.FullName)" }
        if ($entry.FullName -match '(^|[\\/])\.\.([\\/]|$)|signing|\.key$|GameAssembly|UnityPlayer') { throw 'Unexpected private/game file in player package.' }
    }
} finally { $archive.Dispose() }
Write-Host 'Signed catalog and bootstrap package verified.'
