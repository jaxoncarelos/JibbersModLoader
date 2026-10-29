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
# The public mods and their files, from the list packaged beside this script.
$registry = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mods.json') -Raw | ConvertFrom-Json
$publicIds = @($registry.mods | ForEach-Object { $_.id })
$payloads = @($registry.mods | ForEach-Object { "JibbersMods/cs/$($_.folder)/$($_.folder).dll" }) + @($registry.mods | ForEach-Object { $_.assets } | ForEach-Object { $_.zip })
# Mods that never had a native version install where the catalog puts them, so updates replace them.
$destinations = @{}
foreach ($m in @($registry.mods | Where-Object { -not $_.legacy })) { $destinations["JibbersMods/cs/$($m.folder)/$($m.folder).dll"] = "JibbersMods/cs/$($m.id).dll" }
function Destination([string]$Relative) { if ($destinations.ContainsKey($Relative)) { $destinations[$Relative] } else { $Relative } }
foreach ($relative in $payloads) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $relative) -PathType Leaf)) { throw "Package is incomplete: $relative" }
    $null = Game-Path (Destination $relative)
}
# Keep the stable package IDs selected so subsequent launches update these C# mods.
$config = if (Test-Path -LiteralPath $preferences) { Get-Content -LiteralPath $preferences -Raw | ConvertFrom-Json } else {
    [pscustomobject]@{autoUpdate=$true; wanted=@(); catalogUrl='https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable/catalog.json'}
}
$config.wanted = @(($config.wanted + $publicIds) | Select-Object -Unique)
$record = if (Test-Path -LiteralPath $installed) { Get-Content -LiteralPath $installed -Raw | ConvertFrom-Json } else { [pscustomobject]@{sequence=0;packages=[pscustomobject]@{}} }

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
        $target = Save-Before (Destination $relative)
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relative) -Destination $target -Force
    }
    # Native copies of the legacy mods, older manual layouts, and the replaced Atmosphere and Ray Tracing mods.
    $stale = @('JibbersMods/cs/Atmosphere.dll','JibbersMods/cs/Atmosphere/Atmosphere.dll','JibbersMods/cs/RayTracing.dll','JibbersMods/cs/RayTracing/RayTracing.dll')
    foreach ($m in $registry.mods) {
        if ($m.legacy) { $stale += "JibbersMods/$($m.id).dll" } else { $stale += "JibbersMods/cs/$($m.folder)/$($m.folder).dll" }
        $stale += "JibbersMods/cs/$($m.folder).dll"
    }
    foreach ($relative in $stale) {
        $target = Game-Path $relative
        if (Test-Path -LiteralPath $target -PathType Leaf) { $target = Save-Before $relative; Remove-Item -LiteralPath $target }
    }
    # Adopt only the bundles this installer explicitly copied. The next catalog update can
    # replace them safely without treating them as unrelated, unmanaged local files.
    foreach ($m in $registry.mods) {
        $id = $m.id
        $owned = @($m.assets | ForEach-Object { $_.zip } | ForEach-Object {
            $path = Game-Path $_
            [pscustomobject]@{path=$_;url=$config.catalogUrl;size=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}
        })
        $record.packages | Add-Member -MemberType NoteProperty -Name $id -Value ([pscustomobject]@{version='0.0.0';files=$owned}) -Force
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
Write-Host ('Installed ' + (($registry.mods | ForEach-Object { $_.name }) -join ', ') + '. Previous native copies and manager selections were backed up.')
Write-Host 'The C# mods remain enrolled in the public update channel; changes apply on the next launch.'
Write-Host "Backup: $backup"
