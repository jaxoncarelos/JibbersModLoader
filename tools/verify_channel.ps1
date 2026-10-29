param([Parameter(Mandatory)][string]$ChannelDir, [Parameter(Mandatory)][string]$BootstrapZip, [Parameter(Mandatory)][string]$Publisher)
$ErrorActionPreference = 'Stop'
& $Publisher verify $ChannelDir
if ($LASTEXITCODE -ne 0) { throw 'Signed catalog verification failed.' }
$catalog = Get-Content -LiteralPath (Join-Path $ChannelDir 'catalog.json') -Raw | ConvertFrom-Json
# Only the loader, the runtime and the mods in tools/public-mods.json, each with exactly its own files.
$registry = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'public-mods.json') -Raw | ConvertFrom-Json
$publicIds = @('loader','jibbers_dotnet') + @($registry.mods | ForEach-Object { $_.id })
if (@($catalog.packages).Count -ne $publicIds.Count -or @($catalog.packages | Where-Object { $_.id -notin $publicIds }).Count -gt 0) { throw 'Only the loader, the C# runtime and the mods in tools/public-mods.json may enter the public channel.' }
foreach ($m in $registry.mods) {
    $package = @($catalog.packages | Where-Object id -eq $m.id)
    $dll = if ($m.legacy) { "JibbersMods/$($m.id).dll" } else { "JibbersMods/cs/$($m.id).dll" }
    $expected = @($dll) + @($m.assets | ForEach-Object { $_.path })
    if ($package.Count -ne 1 -or $package[0].name -ne $m.name -or (@($package[0].files.path | Sort-Object) -join ',') -ne (@($expected | Sort-Object) -join ',')) { throw "$($m.name) must install exactly $($expected -join ', ')" }
}
$weather = @($catalog.packages | Where-Object id -eq 'jibbers_atmosphere')
if ($weather.Count -ne 1 -or $weather[0].name -ne 'Alpine Vision') { throw 'Alpine Vision must replace the single existing Atmosphere slot.' }
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
