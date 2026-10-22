[CmdletBinding()]
param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Jibbers Playtest')
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'Jibbers.exe'))) { throw 'Select the folder containing Jibbers.exe with -GameDir.' }
if (Get-Process Jibbers -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $gameRoot 'Jibbers.exe') }) { throw 'Close Jibbers before installing C# mods.' }
if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'JibbersMods/jibbers_dotnet.dll'))) { throw 'Install JibbersCSharp.zip before installing the C# mods.' }

function Game-Path([string]$Relative) {
    $candidate = [IO.Path]::GetFullPath((Join-Path $gameRoot $Relative))
    if (-not $candidate.StartsWith($gameRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Install path escaped the game folder.' }
    $walk = $gameRoot
    foreach ($part in $Relative.Replace('\','/').Split('/')) {
        $walk = Join-Path $walk $part
        if ((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Install path contains a junction/symlink: $Relative" }
    }
    return $candidate
}
$preferences = Game-Path 'JibbersMods/system/distribution/preferences.json'
$installed = Game-Path 'JibbersMods/system/distribution/installed.json'
if (Test-Path -LiteralPath (Game-Path 'JibbersMods/system/distribution/journal.json')) { throw 'Launch and close the game to finish its pending update before switching to C# mods.' }
$publicIds = @('jibbers_atmosphere','jibbers_reshred','jibbers_fpv')
$payloads = @('JibbersMods/cs/Atmosphere/Atmosphere.dll','JibbersMods/cs/Reshred/Reshred.dll','JibbersMods/cs/FPV/FPV.dll',
    'jibbers_atmosphere.bundle','JibbersMods/jibbers_fpv.bundle')
foreach ($relative in $payloads) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $relative) -PathType Leaf)) { throw "Package is incomplete: $relative" }
    $null = Game-Path $relative
}
# Parse state before changing files. Retire native ownership of the shared bundles, so the native
# updater cannot remove C# assets on the following launch. Keep loader and local packages/settings.
$config = if (Test-Path -LiteralPath $preferences) { Get-Content -LiteralPath $preferences -Raw | ConvertFrom-Json } else {
    [pscustomobject]@{autoUpdate=$true; wanted=@(); catalogUrl='https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable/catalog.json'}
}
$config.wanted = @($config.wanted | Where-Object { $_ -notin $publicIds })
$record = if (Test-Path -LiteralPath $installed) { Get-Content -LiteralPath $installed -Raw | ConvertFrom-Json } else { $null }
if ($record) { foreach ($id in $publicIds) { $record.packages.PSObject.Properties.Remove($id) } }

$backup = Game-Path ('JibbersMods/system/before-csharp-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$changes = [Collections.Generic.List[object]]::new()
function Save-Before([string]$Relative) {
    $target = Game-Path $Relative
    $hadFile = Test-Path -LiteralPath $target -PathType Leaf
    if ($hadFile) {
        $saved = Join-Path $backup $Relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $saved
    }
    $changes.Add([pscustomobject]@{relative=$Relative; existed=$hadFile})
    return $target
}
try {
    foreach ($relative in $payloads) {
        $target = Save-Before $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relative) -Destination $target -Force
    }
    foreach ($relative in @('JibbersMods/jibbers_atmosphere.dll','JibbersMods/jibbers_reshred.dll','JibbersMods/jibbers_fpv.dll',
        'JibbersMods/cs/Atmosphere.dll','JibbersMods/cs/Reshred.dll','JibbersMods/cs/FPV.dll')) {
        $target = Game-Path $relative
        if (Test-Path -LiteralPath $target -PathType Leaf) { $target = Save-Before $relative; Remove-Item -LiteralPath $target }
    }
    $target = Save-Before 'JibbersMods/system/distribution/preferences.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, ($config | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    if ($record) {
        $target = Save-Before 'JibbersMods/system/distribution/installed.json'
        [IO.File]::WriteAllText($target, ($record | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    }
    [IO.File]::WriteAllText((Join-Path $backup 'changes.json'), ($changes | ConvertTo-Json -Depth 5))
} catch {
    for ($i = $changes.Count - 1; $i -ge 0; $i--) {
        $change = $changes[$i]; $target = Game-Path $change.relative
        if ($change.existed) {
            $saved = Join-Path $backup $change.relative
            # A failed deletion of a locked DLL has not changed it; skip overwriting that same
            # locked file so restoration can continue for earlier successful replacements.
            if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $saved).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
                Copy-Item -LiteralPath $saved -Destination $target -Force
            }
        }
        elseif (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target }
    }
    throw
}
Write-Host 'Installed C# Atmosphere, Rewind and FPV. Previous native copies and manager selections were backed up.'
Write-Host 'Use the C# Mods sections. The stable public catalog still distributes native mod builds; selecting them again would reinstall a native copy.'
Write-Host "Backup: $backup"
