# Scene rebuild optimization validation

Respawning after placing an object rebuilds the physics scene. The loader now avoids rebuilding
MuJoCo's model-wide tree lists after each parsed element and exposes inline collider meshes as
in-memory files so MuJoCo can reuse its processed mesh assets. Geometry order is preserved.

The native paths support MuJoCo 3.3.6. Before patching, the tree-list code checks the expected nine
call-site pairs and probes the spec/model layout. Partial patch failures restore the original calls.
The mesh rewrite preserves inline data when VFS insertion fails, the retained-file budget is full,
or meshes contain additional face/normal/texture data. Retained VFS mesh payloads are capped at
512 MiB; MuJoCo's processed asset cache is configured separately.

## Reproduction

`crates/jibbers-hotload/examples/perf_scene_probe.rs` loads the game's pcore.dll into its own process.
It does not launch or attach to Jibbers. Pass the DLL, an initial scene capture, a changed scene capture
and a new output directory:

    cargo run -p jibbers-hotload --release --example perf_scene_probe -- <pcore.dll> <initial.xml> <respawn.xml> <new-output-directory>
    python tools/verify_scene_models.py <new-output-directory>/baseline.mjb <new-output-directory>/optimized.mjb

The comparison tool checks 17 model arrays and body positions after 600 steps using the 3.3.6 native
layout. It does not establish equivalence of every possible model field or an in-game test.

## Captured 97k-collider scene, 2026-11-08

With the previously installed fast parsing optimizations already enabled:

| Path | Parse | Compile | Total |
| --- | ---: | ---: | ---: |
| Existing path, changed scene | 21.461 s | 15.025 s | 36.486 s |
| New path, initial load | 4.614 s | 16.741 s | 21.355 s |
| New path, changed scene with warm mesh assets | 5.146 s | 4.054 s | 9.200 s |

All 17 checked arrays were identical. Body positions after 600 simulation steps were identical.
Loader tests: 70 passed. Standalone shared scene-code tests: 5 passed. The standalone candidate
0.1.1-preview.1 was installed locally with a verified file hash and a backup of the previous DLL.
The integrated loader path has not been exercised in game. The new full-collider map has more
geometry than this captured scene; these timings do not predict its respawn time.

## Further incremental rebuilding

Not enabled. Appending/removing spec elements can change geometry/contact ordering. A future
implementation must preserve the full-build order and pass model and motion comparisons for
insertions, deletions, renames and reset/scene transitions before it can replace the complete parse.
