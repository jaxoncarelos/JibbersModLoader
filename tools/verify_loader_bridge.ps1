param([Parameter(Mandatory)][string]$UnsignedDir, [Parameter(Mandatory)][string]$SignedDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$unsigned = (Resolve-Path -LiteralPath $UnsignedDir).Path
$signed = (Resolve-Path -LiteralPath $SignedDir).Path
& "$unsigned/bin/sdk-publish.exe" verify --dir $signed
if ($LASTEXITCODE -ne 0) { throw 'Signed bridge did not verify.' }
$stage = Join-Path $root ('out/legacy-bridge-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$stage/checker/src", "$stage/game" -Force | Out-Null
foreach ($file in @('manifest.rs','trust.rs','update.rs')) {
    Copy-Item -LiteralPath "$unsigned/bundle/crates/jibbers-sdk-runtime/src/$file" -Destination "$stage/checker/src/$file"
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'legacy_update_smoke.rs') -Destination "$stage/checker/src/main.rs"
$manifest = @'
[package]
name = "legacy-sdk-bridge-smoke"
version = "0.0.0"
edition = "2021"
[workspace]
[dependencies]
serde = { version = "1", features = ["derive"] }
serde_json = "1"
sha2 = "0.10"
ed25519-dalek = { version = "2", default-features = false, features = ["std"] }
'@
[IO.File]::WriteAllText("$stage/checker/Cargo.toml", $manifest, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText("$stage/game/version.dll", 'MZ previous loader fixture', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText("$stage/game/player-settings.json", 'settings must survive', [Text.UTF8Encoding]::new($false))
& cargo run --quiet --manifest-path "$stage/checker/Cargo.toml" --target-dir (Join-Path $root 'target/legacy-sdk-bridge-smoke') -- $signed "$stage/game"
if ($LASTEXITCODE -ne 0) { throw 'Original SDK updater could not install the bridge.' }
# A harmless child stands in for the game; the real helper must wait for it to exit.
$sleeper = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 4') -WindowStyle Hidden -PassThru
$helper = "$stage/game/JibbersMods/sdk/loader/jibbers-migrate.exe"
$migration = Start-Process -FilePath $helper -ArgumentList @('--game',('"' + "$stage/game" + '"'),'--wait-pid',$sleeper.Id) -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 500
if ([IO.File]::ReadAllText("$stage/game/version.dll") -ne 'MZ previous loader fixture') { throw 'Helper changed version.dll before the launching process exited.' }
if (-not $migration.WaitForExit(30000) -or $migration.ExitCode -ne 0) { throw 'Migration helper failed.' }
foreach ($pair in @(
    @('version.dll', 'version.dll'),
    @('jibbers_loader.dll', 'JibbersMods/system/jibbers_loader.dll')
)) {
    if ((Get-FileHash -LiteralPath "$unsigned/loader/$($pair[0])").Hash -ne (Get-FileHash -LiteralPath "$stage/game/$($pair[1])").Hash) { throw 'Migrated file differs from signed payload.' }
}
if ([IO.File]::ReadAllText("$stage/game/player-settings.json") -ne 'settings must survive') { throw 'Migration changed user settings.' }
if ([IO.File]::ReadAllText("$stage/game/JibbersMods/system/version.dll.before-sdk-migration") -ne 'MZ previous loader fixture') { throw 'Previous loader was not backed up.' }
Write-Host 'Passed: original updater -> verified SDK payload -> wait for exit -> new version.dll and phone UI implementation. Settings preserved.'
Write-Host "Fixture retained: $stage"
