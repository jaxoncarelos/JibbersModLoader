# Installs the Jibbers mod loader and the SDK runtime into the game folder.
#   .\install.ps1                      finds the Steam install of Jibbers Playtest
#   .\install.ps1 -GameDir "D:\Games\Jibbers Playtest"
# Close the game first. An existing version.dll is kept as version.dll.bak.
[CmdletBinding()]
param([string]$GameDir)

$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot 'bin'

if (-not $GameDir) {
    $candidates = @(
        'C:\Program Files (x86)\Steam\steamapps\common\Jibbers Playtest',
        'C:\Program Files\Steam\steamapps\common\Jibbers Playtest'
    )
    $GameDir = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Jibbers.exe') } | Select-Object -First 1
}
if (-not $GameDir -or -not (Test-Path -LiteralPath (Join-Path $GameDir 'Jibbers.exe'))) {
    throw 'Jibbers.exe not found. Pass the folder that contains it: .\install.ps1 -GameDir "<folder>"'
}
if (Get-Process -Name 'Jibbers' -ErrorAction SilentlyContinue) { throw 'The game is running. Close it and run this again.' }

$loader = Join-Path $GameDir 'version.dll'
if ((Test-Path -LiteralPath $loader) -and -not (Test-Path -LiteralPath "$loader.bak")) {
    Copy-Item -LiteralPath $loader -Destination "$loader.bak"
    Write-Host "Existing version.dll saved as version.dll.bak"
}
Copy-Item -LiteralPath (Join-Path $bin 'version.dll') -Destination $loader -Force
$mods = Join-Path $GameDir 'JibbersMods'
New-Item -ItemType Directory -Path $mods -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $bin 'jibbers_sdk.dll') -Destination (Join-Path $mods 'jibbers_sdk.dll') -Force

Write-Host "Installed to $GameDir"
Write-Host "Put your mod DLLs in $mods and start the game. Log: $GameDir\jibbers_hotload.log"
Write-Host 'If the game was updated, the mods stay off until a release validates that build (see docs\GAME-BUILDS.md).'
