# Hank Mod Pack

Hank's controller quick menu, free look, play cameras, HUD themes, replay editor effects,
replay cameras, Improved Spawn, trick banners and custom GIF/video/audio overlays. Install **Hank Mod Pack**
from Options > Mods > Install and update mods, then restart. Each of the nine features has
its own enabled toggle. The pack heading is informational.

Hold D-pad down to open the quick menu. Button assignments and camera preferences are in
Options > Mods. The game's normal stance action remains authoritative: holding D-pad down
does not rewind the rider's internal state. Cameras yield to the game's other camera modes,
including FPV Drone and photo mode.

**Improved Spawn is included in this pack**, using the existing `join_spawn` settings ID.
It contains Hank's newer spawn menu, spectator camera, camera styles and input blocking.
The quick menu opens it directly. The old standalone catalog listing and implementation
are removed. Existing subscriptions migrate to Hank Mod Pack, retaining their settings.
A hidden, code-free retirement record remains readable by older bootstraps so removing the
old listing cannot block all of their startup updates. Managed old DLLs are removed by the
installer transaction; manually installed duplicates are skipped before their hooks load.

Custom media belongs in `JibbersMods/Hank Mod Pack/Overlay Files` and
`JibbersMods/Hank Mod Pack/Fun Overlay`. No default third-party songs or videos are included.
Audio input is limited to 64 MiB, GIF input to 32 MiB. NLayer's MIT notice is distributed in
the package alongside the mod; see `Overlay/NLayer/LICENSE.txt`.

## Catalog review, 1.52.9

Source submission: [issue #10](https://github.com/jaxoncarelos/JibbersModLoader/issues/10),
Hank-Mod-Pack commit `5ced4070882011b5305fdf28a67cd8b03240b5ec`.

The catalog variant removes broad integer/boolean rider-state restoration; adds explicit
disable/unload cleanup for controls, cursor, paused time, hidden HUD and replay effects;
restores overlapping input overrides in either closing order; roots cached game objects;
and routes the pack through one hook per camera method. Replay cameras remain independent
of the replay effects toggle. Old replay-menu delegates are invalidated without reusing
their identifiers. Alpine setting changes and restoration are ordered, and audio shutdown
does not wait for its worker on a gameplay frame.

Tests cover those managed ownership/dispatch rules, duplicate mod IDs, command gating and
malformed audio. Build and tests use the SDK from the published catalog, with installation
disabled. These are regression and compatibility checks; they do not establish live in-game
visual quality or prove every native Unity interaction on every map.

Original mod author: Hank / hankhulerygit. Original mod code and catalog modifications:
GPL-3.0-only. The bundled NLayer decoder retains its MIT license.
