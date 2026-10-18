# Jibbers Mod Loader release candidates

One Windows x64 loader supports independent Atmosphere and Reshred DLLs. It automatically
attaches, provides the shared **Options → Mods** tab, and does not start a TCP server in its
player build. No developer core is required.

## Downloads and installation

- **JibbersModLoader.zip** — install once beside the game executable.
- **JibbersAtmosphere.zip** — plugin and asset bundle only; requires the loader.
- **JibbersReshred.zip** — plugin only; requires the loader.
- **JibbersMods-Complete.zip** — one loader and both independent plugins.
- **JibbersAtmosphere-Standalone.zip** / **JibbersReshred-Standalone.zip** — one plugin with the same loader.

Exit the game before extracting. Merge the plugin directory; do not replace it wholesale.
Do not overwrite an unrelated proxy loader. Restart after changing installed plugins.
Do not leave the developer core beside the executable: it duplicates Atmosphere
and supplies a separate menu. Preserve backups outside the game folder.

See the [common installation and compatibility notes](../RELEASE-README.txt).

## Build and verify

Run [the PowerShell packaging script](../../tools/package_mods.ps1) from a Windows development
environment with Rust/Cargo and Python available. It tests core variants and the player loader,
builds each plugin separately (never enabling both plugin features at once), and creates all
six archives from a fresh staging directory. It never reads from or modifies a game installation.
The Atmosphere Unity bundle is the prebuilt workspace artifact; native code is rebuilt locally.

The script invokes the [offline verifier](../../tools/verify_mod_release.py) to check every ZIP
entry and SHA-256 digest, identical shared payloads across archives, x64 PE headers, plugin ABI
exports, loader forwarding exports, lack of socket imports in the release loader, and Unity
bundle presence. It emits a machine-readable manifest and archive checksums in the distribution
directory. No personal settings, developer core, PDBs, logs, saves or game binaries are packaged.

## Required acceptance checks before publication

These builds are **release candidates**, not a claim of completed in-game QA:

1. Fresh offline install: loader + Reshred only; Mods tab appears and settings persist.
2. Loader + Atmosphere only: menu, F9/F10, bundle loading and visual controls work.
3. Combined install: one Mods tab, separate plugin sections, no duplicated effects/hooks.
4. Reshred: controller/keyboard hold, visible indicator, disable restores mappings; menus/replay
   retain their controls.
5. Snow → jump/grind: targets are on snow, about 2.5 seconds before leaving it. Long airtime
   retains a checkpoint; an airborne spawn without prior snow contact has none.
6. Jump → snow landing → next jump: rewind never crosses back before the latest landing,
   even when the snow interval is shorter than the requested run-up. Rail frames are excluded,
   including simultaneous ground/rail contact.
7. Repeated rewind, crash recovery, replay continuity, map changes and removing either plugin.
8. Review distribution rights/project licensing and dependency obligations before publication.

No processes are closed, restarted, probed or controlled by the packaging workflow.