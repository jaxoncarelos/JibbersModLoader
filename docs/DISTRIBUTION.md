# Public mods and automatic updates

The project source and its mod source live in the public
[JibbersModLoader repository](https://github.com/jaxoncarelos/JibbersModLoader).
Original code is licensed under **GPL-3.0-only**, including modifications and derived work.
Third-party code and assets retain their licenses; see their notices. Game binaries are never shipped.

## Players

Install **JibbersModManager.zip** from Releases once. Close the game and extract `version.dll` and
`JibbersMods` directly beside `Jibbers.exe`. There is no installer script or extra wrapper folder.
On first launch, the loader downloads the signed catalog and required runtime (internet required)
and enrolls existing catalog mods without changing settings. License notices live inside `JibbersMods/licenses`.
Subsequent loader and selected-mod updates download automatically at game startup.
The channel contains the public mods listed in [`tools/public-mods.json`](../tools/public-mods.json). Existing native subscriptions migrate
in place; saved settings are retained. The shared C# SDK and bundled .NET runtime update as
required dependencies. No separate .NET installation is needed. The runtime cache is checked
against signed file hashes on every launch and can recover offline after a failed later update.


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

The public catalog contains the mods listed in **`tools/public-mods.json`**, the one list every build,
packaging, verification and publishing script reads. Each entry names the project folder under `mods/cs`, the mod's
in-game id, its catalog package id, asset files and the GitHub accounts allowed to submit updates. `legacy` mods
(FPV, Alpine Vision and Rewind) keep the historical `JibbersMods/<id>.dll` path that bootstraps migrate from native
plugins; the others install as `JibbersMods/cs/<id>.dll`. Other local mods are excluded from public builds,
catalogs and release assets. The source allowlist check rejects accidentally tracked local mod directories.

`.github/workflows/mod-channel.yml` tests and builds the loader and these public mods on Windows for
pull requests and pushes to master. It uploads a reviewable unsigned build artifact. The manual workflow
dispatch can additionally publish, exclusively from the default branch and through the `mod-channel-stable` environment.

**Publishing one mod.** `.github/workflows/publish-mod.yml` ("Publish one mod") ships a single public mod in a few
minutes: it downloads the published catalog and files, builds and tests the chosen mod against the SDK inside the
published runtime (`tools/publish_mod.py`), and signs a catalog in which every other package is byte-for-byte
unchanged; only the mod's new file is uploaded. Use it for changes confined to a mod. Merging a change to a public
mod runs it automatically (`publish-merged.yml`, one mod at a time); a merge that also touches the SDK or loader is
skipped there and needs the full workflow. Changes to the loader, SDK or
runtime, or a mod that needs a newer SDK, go through the full workflow. The public mods are listed once, in
`tools/public-mods.json`, for both workflows. Before signing, both run `tools/channel_versions.py`: packages whose
files match the published catalog keep their version, and changed ones get a version above it, so the publisher's
"changed without a version bump" and downgrade checks cannot fail on a forgotten version. Both publish jobs share
one concurrency group, so they never sign against the same catalog at once. The manual packages
(`JibbersCSharpMods.zip` and the others) are refreshed only by the full workflow.

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

Alpine Vision retains the Atmosphere package ID `jibbers_atmosphere` for automatic replacement. Do not add a second Atmosphere package. Runtime conflict checks skip legacy `atmosphere`/`raytracing` mods before OnLoad. Alpine Vision has no hardware ray-tracing feature or hotkey. F10 cycles weather.

The player catalog shows optional mods only. The loader and all of its required dependencies (including the C# runtime) are hidden from optional selections, installed automatically even with no mods selected, and cannot be deselected. Runtime payloads remain in the signed update manifest so the loader can authenticate and update them.

## Mod submissions

Anyone can submit a mod from the project site's **Submit a mod** page, which opens a prefilled
"Submit a mod" issue. Every public mod lives in this repository, so players install only code that was
reviewed here, and updates arrive as reviewable diffs.

1. **Check** (`mod-submission.yml`, job `stage`, read-only token, no secrets): `tools/submission.py` fetches the
   source at the pinned commit (or the attached zip), rejects binaries, build scripts, NuGet packages, oversized
   files and hidden folders, regenerates both project files from the repository's template so nothing runs at
   build time, derives the mod id, and checks names against the registry. Updates are accepted only from the
   mod's `authors` or the registry's `maintainers`. The staged files are uploaded before the mod is built and
   tested against the published SDK; that build result is a convenience, not a security signal.
2. **Pull request** (job `open-pr`): re-validates the staged files without running them, copies them to
   `mods/cs/<Folder>` and `mods/cs/<Folder>.Tests`, adds or updates the registry entry, and opens a pull request
   with the report (including spots that use native calls, processes, networking, file deletion or reflection).
3. **Review and merge.** Read the code. Merging publishes the mod through `publish-merged.yml`.

The site lives in `site/` and deploys with `pages.yml` (GitHub Pages, source: GitHub Actions).
