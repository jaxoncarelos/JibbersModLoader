# Jibbers Reshred — plan

Hold **X** (keyboard X or controller X) for ~0.6 s: a radial indicator fills like the game's
"Respawning…" prompt, then the rider is put back to where they were ~1.5 s earlier with the exact
same pose, velocity and speed, and the replay drops the rewound stretch so recorded clips play
straight through. Same idea as Shredders' reshred.

## Shape of the mod

- **Loading — shared loader + plugins.** `version.dll` (crates/jibbers-hotload) stays the only
  proxy DLL. It loads, besides the developer core `jibbers_hotload_core.dll` (if present), every
  `*.dll` in `<game>\JibbersMods\` that exports the existing core ABI (`jh_core_init`,
  `jh_core_handle`, `jh_core_free`, abi.rs). The atmosphere mod and Reshred each ship as one
  plugin DLL; players can install either or both with the same `version.dll`.
  `hooks.rs` pre-hook slots grow from 4 to 12 (several plugins hook methods).
- **Reshred plugin** = new crate `crates/jibbers-reshred` (cdylib `jibbers_reshred.dll`), reusing
  `il2cpp.rs`, `unity.rs`, `log.rs`, `abi.rs` via `#[path]` like the loader does.

## How rewinding works

1. **Snapshots (MuJoCo state).** Physics is MuJoCo (`Mujoco.Runtime`, not obfuscated). Every
   physics step (pre-hook on `MjScene.FixedUpdate` or the step callback), copy the rider's
   simulation state from `mjData`: `qpos`, `qvel`, `act`, `qacc_warmstart`, `ctrl`,
   `mocap_pos/quat`, `time` into a ring buffer covering ~3 s (sizes from `mjModel`: nq, nv, na,
   nu, nmocap). Pointers come from `MjScene.Instance.Data` / `.Model` (Mujoco.Runtime).
2. **Restore.** On trigger: pick the snapshot ~1.5 s back (configurable), copy it back into
   `mjData`, run `mj_forward` (exported by the MuJoCo native lib the game loads — find its
   module), then let the game's own sync update the Unity transforms.
3. **Game state.** A crashed rider must come back "riding". Use the game's respawn path as the
   template: find the method behind "Hold B to reset / Respawning…" (RagdollController /
   PlayerSpawner / Manager), learn which flags/state it resets (crash/fallen state, grab state,
   trick/score state, Grabber, camera), and reset those the same way after restoring physics —
   but NOT the position (that comes from the snapshot).
4. **Replay cut.** The `Recorder` component (Systems/Recorder, with helper `of`) keeps the frames
   of the current session. Find its frame list and timestamps; on reshred, drop the frames newer
   than the restored snapshot time and shift later timestamps so playback is continuous.
5. **Input + indicator.** Per frame (EnvironmentManager.Update or MjScene.LateUpdate pre-hook):
   keyboard via `GetAsyncKeyState('X')` while the game window is focused, controller via
   XInput (`XInputGetState`, button X). Check X isn't already bound in Jibbers; make the key
   configurable in `jibbers_reshred.json`. Hold time ~0.6 s; show a radial ring + "RESHRED" in a
   screen-space canvas from a small Unity asset bundle (`jibbers_reshred.bundle`, built like
   mods/JibbersAtmosphere) — or, if simpler, drive the game's own `MainCanvas/Respawn` text.

## Phases (each verified in game before the next)

- **P0 Loader plugins** (no game needed): JibbersMods folder loading + 12 hook slots; developer
  core still loads as before; atmosphere-only build packaged as plugin. Builds clean.
- **P1 Investigation** (developer build in game, offline, Playtest map): report — MjScene/Data
  access and sizes; MuJoCo native module + `mj_forward`/`mj_resetData` exports; Recorder frame
  storage; respawn method + the state it resets; input system/bindings for X; physics rate.
  No gameplay changes yet.
- **P2 Rewind core:** ring buffer + restore on a debug command (`{"cmd":"reshred"}`) — rider
  snaps back with matching speed; works while riding, in the air, and after a crash.
- **P3 Replay cut:** saved clip plays straight through the cut.
- **P4 Hold-X + indicator.**
- **P5 Package:** `dist/JibbersReshred.zip` (version.dll + JibbersMods/jibbers_reshred.dll +
  bundle + README), and re-package the atmosphere mod as a plugin.

## Rules for every phase

Never close or kill the game or other processes; never send chat/online actions; test offline
only; never leave the game in a broken state (restore files/settings after experiments); no
commits; match the repo's code style.

## Hold-X crash fix (2026-10-18)

- The saved crash dumps end in GameAssembly RVA `0x5a8700`, inside the implementation reached
  by `il2cpp_gchandle_get_target` (export RVA `0x5b79a0`). The handle in RCX is truncated to
  32 bits. This happens when the progress indicator resolves its cloned label, before rewind.
- Reshred now stores the label handle as `isize` and uses managed `GCHandle.GetTargetHandle`,
  `GetTarget`, and `FreeHandle` through shared Unity helpers, matching Atmosphere's working
  approach. The unused legacy u32 bindings were removed to prevent reintroducing the bug.
- Verification: 12 tests pass for both the developer and `reshred-only` builds;
  `atmosphere-only` checks and the Reshred release build succeed. The corrected plugin was
  installed in the Playtest game's JibbersMods folder, with the old DLL backed up under
  `out/reshred-before-gchandle-fix/`.
- Runtime verification is still pending: the running game retains the previous plugin until
  restart. After restarting, test repeated hold-X/release cycles offline with the indicator
  enabled, then grounded/airborne rewind, crash recovery, replay cuts, and scene changes.

## Grounding, indicator and reserved input (2026-10-18)

- Replaced the failing Unity Physics raycast from the ragdoll root with MuJoCo static-geometry
  rays from the actual SegmentSki transforms. Invisible static heightfields are checked directly;
  dynamic rider geometry cannot count as ground. Native calls require version 336 and use the
  official double-precision 3.3.6 ABI; mjModel offsets come from validated IL2CPP mirror metadata.
- Rewind targets require a grounded run-up, including at least 0.5 seconds before takeoff.
  Insufficient history fails visibly rather than selecting an airborne or too-recent frame.
  Scene, rider, replay-clip and state-size changes invalidate stale history.
- The cloned Respawn label now has independent bottom-center anchors, size, scale, opaque text
  and alignment. It shows progress immediately and retains success/failure feedback until release.
  Waiting for active physics/history is visible too; initialization failures go to the mod log.
- Rewired element mappings for bare X and recognized controller west buttons are reversibly
  masked while enabled. Original enabled flags are saved before changes and restored on disable,
  key/controller changes, obsolete-map removal and normal teardown. Keyboard detection is raw
  Win32; controller detection uses raw Rewired buttons with XInput fallback.
- Offline raw serialized Rewired config in level0 object 482 confirms categories Ragdoll, UI,
  Replay, Default, Default - Debug and SpawnSelector. The gameplay allowlist includes Ragdoll;
  the others remain untouched. Controller API failures cannot prevent keyboard masking.
- Offline regression coverage includes native ABI-independent ray fixtures, static/dynamic
  filtering, invalid indices, key/category/controller policies, scalar validation, scan-error
  cooldown, run-up selection, finite settings and the earlier GCHandle/inspector guards.
- This work deliberately performs no live probes, reload requests, input injection or process
  control. In-game acceptance remains a user-run offline check after restarting; no claim of
  verified crash recovery or replay continuity is made from offline tests alone.
- Verification: 31 tests pass in both default and reshred-only builds; atmosphere-only checks
  and the Reshred release build succeed. Installed the 664064-byte plugin, SHA-256
  `8AAB17AC148E28483E3ADF3316E717B32E47813FDEFA1027B5F07597A4C5C13E`, without reloading it.
  Previous plugin/settings backup: `out/reshred-before-ground-input-fix-20261018-121329/`.
  Package: `dist/JibbersReshred.zip`, containing the plugin, the already-installed shared loader
  and `mods/JibbersReshred/README.txt`. The running game still needs a user-initiated restart.

## Grounded history correction after user test (2026-10-18)

The user reported continuous "No grounded run-up" errors. The previous ray replacement was
not adequate: SegmentSki's component transform is an equipment container, not its physical
pose. Offline native disassembly establishes that SegmentSki.cns() returns a separate pose
transform, and RagdollController.dan() already computes authoritative support flags in fb:
phs is the grounded decision; phz is current ski/rail contact. The new recorder reads these
validated instance Boolean fields directly through metadata, with no rays or live inspector.
It deliberately does not use pht, which includes a 50ms post-takeoff grace period.

The rolling buffer also used to evict every grounded frame during long airtime. The recorder
now keeps an independent owned run-up snapshot, preferring about 0.5 seconds before takeoff,
which survives ring eviction. A short grounded spawn can use its earliest supported frame;
it is no longer rejected for lacking a full half-second. Scene/rider/clip changes still reset
all checkpoints. A never-grounded airborne spawn remains ineligible. First support acquisition
is logged so saved logs can confirm actual contact recognition without developer probing.

Verification: 33 tests pass in default and reshred-only builds; atmosphere-only check and
release build pass. Installed 659968-byte plugin SHA-256
`424FA242B9803BC14346E3748C4412D35054F2955CFAACF21423220763EFE798` and refreshed the ZIP.
Prior ray-based plugin backup: `out/reshred-before-contact-history-fix-20261018-122944/`.
No game interaction or reload; user restart and offline acceptance remain necessary.

## Longer pre-jump approach (2026-10-18)

User confirmed rewind targets were too close to the ramp lip. Replaced the fixed 0.5-second
pre-takeoff margin with runupTime, default 2.5 seconds, exposed as "Pre-jump run-up" in the
Mods menu (0.5-5 seconds). Older settings inherit 2.5 seconds without overwriting existing
preferences. This duration is measured before takeoff rather than before pressing X.
Rolling history is now eight seconds; the retained approach uses the same setting and is
preferred if ring eviction would otherwise force a newer/lip-adjacent target. Short spawn
and never-grounded safeguards remain. Grounded rewind duration remains independently configurable.

Verification: 36 offline tests pass in both default and reshred-only variants; atmosphere-only
check and release build pass. Installed 672256-byte plugin SHA-256
`504DE04D418E5369375C3FE68F905000AB058C66D2C2DFC9EE38D46296E80E89`; ZIP refreshed.
Backup: `out/reshred-before-longer-runup-20261018-123448/`. No live interaction or reload.

## Snow-only, per-landing approach windows (2026-10-18)

Only phs AND NOT phz qualifies: grinding counts as airborne even with overlapping ground/rail
contacts. Rewind never selects a grind frame. Each transition from air/grind onto snow clears
the previous feature's history/checkpoint before saving the new landing. Target selectors are
also bounded to the latest uninterrupted snow segment. If the next jump happens before a
full run-up is available, the target is that segment's earliest snow frame, never a previous
jump's approach. The retained snapshot survives long air/grinds until the next snow landing.
Restoring a snapshot keeps it as the ring's final snow frame, preserving segment boundaries.
Regression tests cover two jumps, short landings, ring eviction, overlapping rail contacts,
and snow-to-grind-to-air chains followed by a new snow landing.

## Scrub rewind replaces checkpoint rewind (2026-10-18)

Holding the button now scrubs back through the last 6 s anywhere (snow, air, rail) and releasing resumes from that moment. While held, each MjScene.FixedUpdate pre-hook steps speed frames back through the history and restores that frame (physics state plus primitive game state) instead of recording. The ground-contact, run-up and per-landing checkpoint logic was removed. Offline tests only; not yet verified in game.
