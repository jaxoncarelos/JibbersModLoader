# Builds the Jibbers Mod SDK package for modders: dist/JibbersModSDK.zip
#   JibbersModSDK/  README.md, AGENTS.md, docs/, crates/, template/, compat/, Cargo.*  (the SDK source)
#                   bin/version.dll        release loader (no server), same bytes as the mod releases
#                   bin/jibbers_sdk.dll    SDK runtime plugin with the auto-updater
#                   install.ps1            copies both into the game folder
#
# -UpdateUrl   folder URL that serves manifest.json, manifest.json.sig and the assets (for GitHub:
#              https://github.com/<owner>/<repo>/releases/latest/download). Compiled into the runtime.
#              Without it the updater stays idle (placeholder URL).
# -Publish     also write a signed update release for this version into dist/sdk-release/<version>
#              (needs the signing key from `sdk-publish keygen`). Upload every file in that folder.
# Does not read or start the game.
[CmdletBinding()]
param([string]$UpdateUrl, [string]$Publish, [switch]$SkipTests, [string]$Key)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Invoke-Cargo([string[]]$CargoArgs, [string]$Dir = $root) {
        Push-Location $Dir
        try {
            & cargo @CargoArgs
            if ($LASTEXITCODE -ne 0) { throw "cargo $CargoArgs failed ($LASTEXITCODE)" }
        } finally { Pop-Location }
    }
    $sdk = Join-Path $root 'sdk'
    $version = (Select-String -LiteralPath (Join-Path $sdk 'Cargo.toml') -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value
    if ($UpdateUrl -and ($UpdateUrl -notmatch '^https://' -or $UpdateUrl -match 'OWNER/REPO')) { throw '-UpdateUrl must be an https:// folder URL' }
    if ($Publish -and $Publish -ne $version) { throw "-Publish $Publish does not match the workspace version $version in sdk/Cargo.toml; bump it first" }

    # The loader and the runtime must agree on the compatibility list; the loader embeds it at build time.
    & python (Join-Path $PSScriptRoot 'verify_mod_release.py') '--builds-block' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sdk/compat/compat.json is missing or invalid' }

    if (-not $SkipTests) { Invoke-Cargo -CargoArgs @('test', '--workspace') -Dir $sdk }
    if ($UpdateUrl) { $env:JIBBERS_SDK_UPDATE_URL = $UpdateUrl } else { Remove-Item Env:\JIBBERS_SDK_UPDATE_URL -ErrorAction SilentlyContinue }
    Invoke-Cargo -CargoArgs @('build', '--release', '-p', 'jibbers-sdk-runtime') -Dir $sdk
    Invoke-Cargo -CargoArgs @('build', '--release', '-p', 'jibbers-sdk-publish') -Dir $sdk
    # The template is its own package: make sure a fresh copy of it still builds against the SDK.
    Invoke-Cargo -CargoArgs @('build', '--release', '--target-dir', (Join-Path $root 'target/sdk-template')) -Dir (Join-Path $sdk 'template')
    Invoke-Cargo -CargoArgs @('build', '--release', '-p', 'jibbers-hotload', '--features', 'release', '--target-dir', 'target/mod-release/loader')

    $runtime = Join-Path $sdk 'target/release/jibbers_sdk.dll'
    $loader = Join-Path $root 'target/mod-release/loader/release/version.dll'
    foreach ($f in @($runtime, $loader)) { if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { throw "Missing build output $f" } }

    $dist = Join-Path $root 'dist'
    $stage = Join-Path $root ('out/sdk-' + [guid]::NewGuid().ToString('N'))
    $pkg = Join-Path $stage 'JibbersModSDK'
    New-Item -ItemType Directory -Path (Join-Path $pkg 'bin') -Force | Out-Null
    New-Item -ItemType Directory -Path $dist -Force | Out-Null

    # Source tree without build output or secrets.
    & robocopy $sdk $pkg /E /XD target .git /XF '*.key' 'signing*' /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
    Copy-Item -LiteralPath $loader -Destination (Join-Path $pkg 'bin/version.dll')
    Copy-Item -LiteralPath $runtime -Destination (Join-Path $pkg 'bin/jibbers_sdk.dll')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sdk-install.ps1') -Destination (Join-Path $pkg 'install.ps1')

    $zip = Join-Path $dist 'JibbersModSDK.zip'
    Compress-Archive -Path $pkg -DestinationPath $zip -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "SDK $version packaged: $zip"
    Write-Host "sha256 $hash"
    if (-not $UpdateUrl) { Write-Host 'Note: no -UpdateUrl given; auto-update is idle in this build.' }

    if ($Publish) {
        # The update bundle is the same tree the zip carries (docs, template, SDK source), minus
        # binaries, the list (shipped separately, stamped with the version) and the installer.
        $bundle = Join-Path $stage 'bundle'
        & robocopy $pkg $bundle /E /XD bin compat /XF install.ps1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
        $global:LASTEXITCODE = 0
        $out = Join-Path $dist "sdk-release/$Publish"
        $args2 = @('release', '--version', $Publish, '--runtime', $runtime, '--bundle', $bundle, '--compat', (Join-Path $sdk 'compat/compat.json'), '--out', $out)
        if ($Key) { $args2 += @('--key', $Key) }
        if ($UpdateUrl) { $args2 += @('--base-url', $UpdateUrl) }
        & (Join-Path $sdk 'target/release/sdk-publish.exe') @args2
        if ($LASTEXITCODE -ne 0) { throw 'sdk-publish release failed' }
        & (Join-Path $sdk 'target/release/sdk-publish.exe') verify --dir $out
        if ($LASTEXITCODE -ne 0) { throw 'sdk-publish verify failed' }
        Write-Host "Signed update release in $out : upload every file in it to the release host."
    }
    Write-Host "Staging retained for inspection: $stage"
} finally {
    Pop-Location
}
