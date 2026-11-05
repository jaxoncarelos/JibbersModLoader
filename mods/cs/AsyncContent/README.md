# Async Content

Prepares downloaded session PNG textures, OBJ meshes and JSON text on a background worker. Unity objects are staged one at a time across frames. Jibbers' original importer then validates the original archive and receives the staged objects for caching and registration.

This is a functional import-path replacement for the supported decoding stages, not a telemetry-only mod. It does not replace mod.io downloading or the game's networking. Live logs confirm the accelerated path is active; remaining engine work can still cause a hitch.

## Installation

Copy `AsyncContent.dll` into `<game>/JibbersMods/cs/AsyncContent/` and launch Jibbers. Options â†’ Mods â†’ Async Content has an enable switch and a content work budget of 1â€“5 ms (default 2 ms). A new DLL loads on the next game launch.

Before enabling its hooks, the mod runs small, local comparisons against the original Unity loaders. PNG pixels/alpha/mipmap count and OBJ vertices, normals, UVs, indices and tangents must match. A missing API, changed coroutine state layout or parity failure prevents acceleration from loading. Check `jibbers_hotload.log` for the result.

During a content import, the log reports streaming preparation, fallback filenames/reasons, staged uploads, slow texture allocation/pixel copy/mipmap apply, mesh upload/recalculation and registration. The summary separates staged-upload and original-coroutine stalls. `async_content.status` returns counters. Nonzero accelerated texture/mesh counts are required before attributing any performance change to this mod.

Install Async Content from the public mod catalogue in Options > Mods. Selected installations receive signed updates on launch. The source is GPL-3.0-only. Remove the mod through the manager to uninstall it.

## How it works

`GameContentAdapter` holds the original archive coroutine at its initial boundary while `PreparationQueue` processes an owned byte snapshot. Waiting yields to the game; it never waits synchronously on a worker. There are at most two retained preparations, one decoding worker and one queued decoded entry. The worker waits when that slot is full. The main thread stages one entry per coroutine tick, freeing its decoded pixel/geometry arrays instead of retaining an entire texture set.

`ArchiveDecoder` owns a separate managed `ZipArchive`. The managed PNG decoder supports grayscale, RGB, indexed color, alpha, packed samples, 8/16-bit samples and Adam7 interlacing. Like Unity's PNG-to-ARGB32 path, 16-bit samples are converted to 8-bit channels; there is no resizing or gamma adjustment. CRCs, dimensions and output lengths are validated. `ObjDecoder` matches the native parser's X reflection, reversed triangle fans and position/UV/normal corner keys, including extended coordinate records and mixed normal coverage. JSON is extracted/decoded on the worker; the game still parses its schema. Workers touch no Unity objects or IL2CPP data.

Staged Unity objects remain private and rooted until requested. The original coroutine resumes with its original bytes; its filename, type, size and image-header validation still runs before it receives an object. Entry ownership is checked against that exact archive. The original caches, material assignment, registration, completion notifications and cleanup retain their behavior. Objects rejected or unused by the importer are destroyed by the mod; handed-off objects remain game-owned. Native strings remain rooted until the original import step finishes.

Unknown encodings/syntax, preparation errors, capacity limits or upload failures use the original decoder. No texture is resized, no shader is substituted and no null placeholder is returned. Disabling the mod, disposing the iterator, resetting content or unloading cancels preparation and releases roots. A 30-second preparation deadline resumes the original importer if preparation has not completed.

## Limits

- Up to 64 MiB compressed input per prepared import, 2,048 entries and 64 MiB per selected entry, matching the native PNG-file limit. A cumulative 768 MiB staged payload limit allows normal multi-texture 4K sets to stream without the former 128 MiB fallback. Larger content retains native decoding.
- Images are limited to 16 million pixels and 8,192 pixels per dimension; the game's stricter validation still applies. Unsupported PNG formats retain native decoding.
- Meshes are limited to 500,000 output vertices and 3 million triangle indices. Incomplete or invalid geometry retains the original parser. Metadata directives and extra coordinate components follow the original parser's behavior.
- These are admission/output bounds, not a process working-set promise: decoding and array conversion require temporary managed/native memory as well.
- The frame budget prevents starting additional import steps after the budget has been spent. It cannot interrupt a single Unity upload, normal/tangent recalculation or final registration/physics rebuild. Those can still cause a hitch. This mod does not promise zero join stalls.
- No persistent mod-owned asset cache is added. The original game's final asset caches remain authoritative.

## Build and verification

```powershell
dotnet build mods/cs/AsyncContent -c Release -p:JibbersNoInstall=true
dotnet run --project mods/cs/AsyncContent.Tests -c Release -p:JibbersNoInstall=true -- --dump out/dumps/2026-10-18-build.json
```

Offline tests cover 19 PNG format fixtures, all filters and Adam7 passes, hidden RGB under transparency, OBJ attributes/winding/corner sharing, archive fallback/duplicate paths, JSON text, cancellation, bounded-channel backpressure, final-entry delivery and production symbol resolution. A 192 MiB three-texture 4K test verifies that the old 128 MiB limit no longer triggers fallback. All 49 PNG/OBJ files in the installed local asset folders also decode without fallback. Native comparisons for the extended PNG and OBJ formats run inside the game before hooks are enabled.

### Texture upload path in 0.1.4

The 0.1.2 live logs identified large normal PNGs rejected by the old 32 MiB cap (about 0.6â€“0.7 seconds of native decoding), plus expensive default texture initialization, Color32 conversion and mip generation. The entry limit now matches the game's 64 MiB validation; raw mip copying replaces per-pixel Color32 conversion where available. An uninitialized texture constructor skips redundant clear/initial-upload work, with the fully initialized texture kept private until upload completes.

Opaque power-of-two textures may also generate their complete mip chain on the decoder worker. This is enabled only if a candidate filter matches every byte of every mip from multiple native PNG fixtures, and the actual raw-chain upload also matches. Transparent/non-power-of-two images and unmatched filtering keep native mip generation. The log records the chosen filter and available upload paths. Base size, color values, CPU readability and mip count are preserved; mip optimization is rejected when exact native results cannot be reproduced.

Compare the same join with the switch off and on, including fresh and cached content. Inspect both frame-time spikes and logs; a successful build or CPU test does not establish an in-game speedup.

The first 0.1.3 live sample accelerated 32 textures and two meshes without decoding or upload fallbacks. Its worst measured original import step was 11.17 ms and its worst staged texture upload was 13.72 ms. Those remaining texture copies motivated the 0.1.4 candidate: textures larger than 1 MiB are copied in full-width row batches of at most 1 MiB (or one row), across frames under the shared work budget. Prepared mip levels use the same batches. The object stays private until the final Apply completes. All 19 native PNG fixtures must match at every mip before this path is enabled; otherwise the validated whole-texture path remains active. The log reports verification and actual batch counts. Archive snapshots use an owned bulk byte copy instead of per-byte reads. Native Apply, allocation, mesh work and registration can still exceed a frame budget. Later live logs confirmed row-upload parity and successful batched imports, including roughly 2 ms staged-upload steps. A larger outfit still exceeded the 384 MiB output cap, sending two textures back through native decoding for 300.59 ms and 257.37 ms. Version 0.1.5 raises this bounded per-import cap to 768 MiB; a six-map 4K outfit with full mip chains (about 512 MiB) now streams without that fallback in regression tests. This additional capacity can increase peak memory use. Larger or unsupported content and indivisible engine work can still hitch; the public release does not promise zero server-join stalls.

Native analysis and the earlier design are documented in [ASYNC-CONTENT-LOADING.md](../../../docs/ASYNC-CONTENT-LOADING.md).
