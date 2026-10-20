# Public mods and automatic updates

The project source and its mod source live in the public
[JibbersModLoader repository](https://github.com/jaxoncarelos/JibbersModLoader).
Original code is licensed under **GPL-3.0-only**, including modifications and derived work.
Third-party code and assets retain their licenses; see their notices. Game binaries are never shipped.

## Players

Install **JibbersModManager.zip** from Releases once. Close the game, extract the archive, and run
`install.ps1` from the extracted JibbersModManager folder. Use `-GameDir 'your game folder'` if necessary.
The installer saves the previous loader and enrolls existing catalog mods without changing settings.
Subsequent loader and selected-mod updates download automatically at game startup.

Open **Options > Mods > Install and update mods**, in either the main menu or the phone.
Enable a mod to install it, or disable it to uninstall it on the next launch. Dependencies install with it.
The game keeps its current plugins for the whole session; menu selections apply at the next launch.
Use **Check for updates** to refresh the catalog. Close the phone/options window after refreshing to
populate any newly published mod rows. Automatic updates can be disabled independently of selecting mods.

Offline startup keeps working with installed mods. A cached, signed catalog remains usable when the CDN
is unavailable. Missing or corrupt downloads leave installed files in place. Modified local binaries are
not overwritten by updates; settings and manually installed unrelated mods are never removed.

## How the loader updates itself

`version.dll` is now a small stable bootstrap. Before loading mods it checks the signed catalog, downloads
and verifies updates, and installs them. It then loads a private working copy of
`JibbersMods/system/jibbers_loader.dll`. UI, hooks and the mod manager live in that replaceable implementation.
Windows therefore does not lock the installed implementation and prevent next-launch updates.

Older versions shipped the whole loader inside version.dll. Existing SDK updater clients receive the
[signed SDK loader bridge](LOADER-MIGRATION.md), which installs the bootstrap after a normal game exit.
Installs without an updater use the bootstrap installer once. The stable bootstrap itself
is deliberately outside the catalog's install permissions; an exceptional bootstrap/security change may
still need a new bootstrap installer. Routine features live in the updateable implementation.

The SDK runtime's existing update channel remains independent. This public channel manages the loader
and catalog mods; it does not change SDK trust keys or overwrite C# SDK files.

## Maintainers: GitHub Actions and Releases

The public catalog contains **FPV Drone, Atmosphere and Rewind** (Rewind keeps its existing internal
`jibbers_reshred` id and DLL name for compatibility). Other local mods are excluded from public builds,
catalogs and release assets. The source allowlist check rejects accidentally tracked local mod directories.

`.github/workflows/mod-channel.yml` tests and builds the loader and these three public mods on Windows for
pull requests and pushes to master. It uploads a reviewable unsigned build artifact. The manual workflow
dispatch can additionally publish, exclusively from the default branch and through the `mod-channel-stable` environment.

1. Commit the intended source, including mod sources and prebuilt asset bundles. GitHub release source
   archives always point back to that commit; no game install or Unity installation is needed in CI.
2. Configure the protected GitHub environment **mod-channel-stable**. Add **JIBBERS_SIGNING_KEY** as a
   secret containing the existing 64-hex-character Ed25519 private key. It must match the public key
   already trusted by bootstraps. Keep it out of the repository, artifacts and logs.
3. Bump each changed package version in its Cargo.toml and `tools/package_channel.ps1`. A package's
   version must identify immutable file hashes; an update changing bytes without bumping its version
   is refused. Increase the catalog sequence monotonically (Actions uses its run number).
4. Run **Build and publish public mods** on the default branch with `publish` enabled. The publishing job signs and
   verifies the exact tested build before uploading it. It never runs on pull requests.
5. Review the versioned release and stable channel. Players receive updates on their next launch.

GitHub Releases serves as the CDN. No separate hosting account is required. The stable catalog is at
`https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable/catalog.json`.
The matching `.sig` signs the exact catalog bytes. Payload filenames contain their SHA-256 and remain
available across catalog updates. Publish payloads first, then the catalog/signature, and retain old assets.
Never reuse a sequence or mutate a published package version. The SDK's `/releases/latest/download`
channel is separate so publishing an SDK release cannot redirect public mod checks.

To build and inspect locally:

```powershell
tools/package_channel.ps1 -Sequence 1
# Extract dist/JibbersChannel-Unsigned.zip into a fresh directory, then:
out/channel/bin/jibbers-publish.exe --spec out/channel/spec.json --root out/channel/payload --out dist/channel --key "$env:USERPROFILE/.jibbers-sdk/signing.key"
out/channel/bin/jibbers-publish.exe verify dist/channel
```

New public native mods are added to `tools/package_channel.ps1`: source directory, package id, friendly
name, description, version and explicit file list. Each package owns its own DLL/bundles and optional
`JibbersMods/packages/<id>/` asset directory. This is a curated catalog: third-party submissions arrive
as source pull requests, go through tests/review, and are signed by the project maintainer.
The existing C# sources and SDK remain public; automatically distributing the .NET bridge/runtime is
outside this initial native catalog and remains available through the C# packaging script.

## Verification and recovery

The bootstrap trusts the existing project Ed25519 public key. An HTTPS response is not enough: catalog
signatures, monotonic sequence numbers, package versions, ownership paths, file sizes and SHA-256 hashes
are checked. Dependencies must exist and cannot cycle. Windows reserved paths and reparse points are
rejected. Public mods cannot write game files, version.dll, updater state, another mod's native DLL,
or player settings.

All selected downloads verify before installed files change. A journal and backups recover interrupted
transactions before any plugin loads; failed recovery stops mod loading instead of loading mixed versions.
Errors appear in `jibbers_hotload.log` and the mod-manager status. Catalog, preferences and installed-file
records live under `JibbersMods/system/distribution/`. Deleting player settings is never part of uninstall.
