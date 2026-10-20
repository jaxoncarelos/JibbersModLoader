[CmdletBinding()]
param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Jibbers Playtest')
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'Jibbers.exe'))) { throw 'Select the folder containing Jibbers.exe with -GameDir.' }
if (Get-Process Jibbers -ErrorAction SilentlyContinue) { throw 'Close Jibbers before installing the bootstrap.' }
$systemDir = Join-Path $gameRoot 'JibbersMods/system'
$stateDir = Join-Path $systemDir 'distribution'
foreach ($candidate in @((Join-Path $gameRoot 'JibbersMods'),$systemDir,$stateDir,(Join-Path $gameRoot 'version.dll'),(Join-Path $systemDir 'jibbers_loader.dll'))) {
    if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -LiteralPath $candidate).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The mod-manager install directory must not contain junctions or symlinks.' }
}
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
$backupDir = Join-Path $systemDir ('before-bootstrap-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupDir | Out-Null
if (Test-Path -LiteralPath (Join-Path $gameRoot 'version.dll')) { Copy-Item -LiteralPath (Join-Path $gameRoot 'version.dll') -Destination (Join-Path $backupDir 'version.dll') }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'JibbersMods/system/jibbers_loader.dll') -Destination (Join-Path $systemDir 'jibbers_loader.dll') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'version.dll') -Destination (Join-Path $gameRoot 'version.dll') -Force
foreach ($file in @('catalog.json','catalog.json.sig')) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file)) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stateDir $file) -Force }
}
# First installation explicitly enrolls existing public mods so their older binaries can update.
# Only files listed in the shipped catalog are enrolled. Player settings are never included.
$catalogPath = Join-Path $PSScriptRoot 'catalog.json'
if ((Test-Path -LiteralPath $catalogPath) -and -not (Test-Path -LiteralPath (Join-Path $stateDir 'installed.json'))) {
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    $installed = [ordered]@{}
    $wanted = @()
    foreach ($package in $catalog.packages) {
        if ($package.id -ne 'loader' -and @($package.files | Where-Object { $_.path.EndsWith('.dll') -and (Test-Path -LiteralPath (Join-Path $gameRoot $_.path) -PathType Leaf) }).Count -eq 0) { continue }
        $files = @()
        foreach ($file in $package.files) {
            $targetPath = [IO.Path]::GetFullPath((Join-Path $gameRoot $file.path))
            if (-not $targetPath.StartsWith($gameRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe catalog install path.' }
            if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                $files += [ordered]@{path=$file.path; url=$file.url; size=(Get-Item -LiteralPath $targetPath).Length; sha256=(Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()}
            }
        }
        if ($files.Count -gt 0) {
            $installed[$package.id] = [ordered]@{version= $(if ($package.id -eq 'loader') {$package.version} else {'0.0.0'}); files=$files}
            if ($package.id -ne 'loader') { $wanted += $package.id }
        }
    }
    [IO.File]::WriteAllText((Join-Path $stateDir 'installed.json'), ([ordered]@{sequence=$catalog.sequence; packages=$installed} | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    if (-not (Test-Path -LiteralPath (Join-Path $stateDir 'preferences.json'))) {
        [IO.File]::WriteAllText((Join-Path $stateDir 'preferences.json'), ([ordered]@{autoUpdate=$true; wanted=@($wanted); catalogUrl='https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable/catalog.json'} | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    }
}
Write-Host 'Installed. Launch Jibbers, then open Options > Mods > Install and update mods.'
Write-Host "Previous loader saved in $backupDir"
