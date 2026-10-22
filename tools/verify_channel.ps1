param([Parameter(Mandatory)][string]$ChannelDir, [Parameter(Mandatory)][string]$BootstrapZip, [Parameter(Mandatory)][string]$Publisher)
$ErrorActionPreference = 'Stop'
& $Publisher verify $ChannelDir
if ($LASTEXITCODE -ne 0) { throw 'Signed catalog verification failed.' }
$catalog = Get-Content -LiteralPath (Join-Path $ChannelDir 'catalog.json') -Raw | ConvertFrom-Json
$publicIds = @('loader','jibbers_dotnet','jibbers_atmosphere','jibbers_reshred','jibbers_fpv')
if (@($catalog.packages).Count -ne $publicIds.Count -or @($catalog.packages | Where-Object { $_.id -notin $publicIds }).Count -gt 0) { throw 'Only the loader, FPV, Atmosphere and Rewind may enter the public channel.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $BootstrapZip).Path)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    foreach ($required in @('version.dll','JibbersMods/system/jibbers_loader.dll','catalog.json','catalog.json.sig','install.ps1','LICENSE')) {
        if (-not ($names -contains "JibbersModManager/$required")) { throw "Bootstrap package is missing $required" }
    }
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -match '(^|[\\/])\.\.([\\/]|$)|signing|\.key$|GameAssembly|UnityPlayer') { throw 'Unexpected private/game file in player package.' }
    }
} finally { $archive.Dispose() }
Write-Host 'Signed catalog and bootstrap package verified.'
