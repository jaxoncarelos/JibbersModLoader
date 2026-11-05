# Async local content loading: feasibility and implementation design

## Goal

Prevent downloaded session content from freezing active gameplay when a player joins, while retaining its original meshes and textures. This is a local loading optimization. It does not change network messages, content permissions, server checks, or another player's simulation state.

Status: 0.1.3 passed the expanded native PNG/OBJ checks and qualified exact worker mip filtering as PairFloorRows. Its first 11 live imports accelerated 32 textures and two meshes with zero decoding or upload fallbacks. The worst measured original step was 11.17 ms; the worst staged upload was 13.72 ms. This removes the observed large normal-map decoding fallbacks, but does not eliminate noticeable stutter. The 0.1.4 candidate splits large texture copies into bounded row batches across frames, verifies every mip against all 19 native PNG fixtures before enabling that path, and replaces per-byte archive snapshots with bulk copies. Offline tests pass; native activation and a matching live performance comparison for 0.1.4 are pending. Final Apply, registration and mesh work remain indivisible calls and are still measured separately.

## 0.1.1 correction after the first live test

The 0.1.0 log proved activation (53 textures and six meshes accelerated), but also showed 51 fallback entries and original coroutine steps up to 1,204.08 ms. It did not prove the cause of each remaining stall. Inspection of local assets found grayscale and 16-bit PNGs and extended OBJ coordinate records excluded by the first decoder. These are supported by 0.1.1.

The new candidate streams preparation through a bounded one-entry channel, stages Unity uploads across frames, prepares JSON text and records per-stage upload, fallback and registration times. It no longer retains all decoded image arrays under a 128 MiB cap; a normal 192 MiB three-texture 4K fixture streams without native fallback. Native validation still runs on the original archive before staged objects are handed to the importer. Unused staging objects are destroyed on cancellation/reset/completion.

CPU tests pass on all 49 local PNG/OBJ assets. The extended native parity fixtures and a matching multiplayer join test must run after replacing the loaded DLL; performance improvements in 0.1.1 have not yet been measured.

## Implementation evidence

Read-only native inspection of the installed, matching GameAssembly confirms that the archive coroutine:

- First enumerates entries and runs the original entry validator. The PNG validator reads a header and checks dimensions.
- Loads a PNG by opening its zip entry, copying to a memory stream, constructing a texture and calling `ImageConversion.LoadImage` synchronously.
- Loads an OBJ by reading its zip entry to text and calling the native text-to-mesh parser synchronously.
- Yields after each selected PNG/OBJ entry, then performs registration/completion work afterward.

The native OBJ parser reflects the X coordinate of positions and normals, uses a reversed triangle fan, keys corners by resolved position/UV/normal indices, and calls Unity's normal/bounds/tangent recalculation. The candidate follows those conventions, with small native parity fixtures before it enables interception. Runtime bindings use metadata symbols and named Unity APIs, not addresses from the inspection.

The first candidate intentionally preserves native validation and registration rather than replacing the entire coroutine. It prepares only supported CPU data, limits pending work, and uses the original game's final asset caches. Final GPU uploads, recalculation and registration still run on the main thread and remain performance risks to measure.

## What is confirmed

The available game snapshot is `out/dumps/2026-10-18-build.json`, fingerprint `GameAssembly 111196160 bytes, header ede5061cb8201fa4, Unity 6000.3.12f1 (fca03ac9b0d5)`.

- `SessionContent` exposes coroutine-returning methods for loading content. Its iterator metadata includes a byte buffer, `ZipArchive`, archive-reader object, string array and progress indices. Coroutines are already part of this path.
- Its nested archive reader has a dictionary of zip entries and separate mesh and texture caches. It exposes `Texture(string) -> Texture2D` and `Mesh(string) -> Mesh` synchronously.
- `SessionContent` also has a zip-entry-to-text helper, a zip-entry-to-texture method, asset dictionaries and completion-related callbacks.
- The game's mod.io integration contains async task state machines. Independently, mod.io documents its Unity plugin as asynchronous.
- The SDK's convenience asset-bundle loader is synchronous, and documents that this player strips managed bundle-loading APIs. It is not evidence that session content uses asset bundles.

These are metadata observations, not recovered method bodies. They do not establish which calls execute inside one coroutine step, where decoding runs, the meaning of obfuscated fields, cache ownership or the cause of the measured hitch. No live timings were captured for this request.

## Proposed implementation

1. Measure archive extraction, image decoding, mesh parsing, Unity object creation/upload, content registration and any physics-scene rebuild separately. Correlate these with frame times during a reproducible join. Distinguish fresh downloads from already-cached content.
2. Validate the local import boundary and the iterator's completion contract before replacing it. Resolve methods structurally against the running validated build. Do not guess obfuscated names or patch global file APIs.
3. Keep the existing content acquisition and permission path. Transfer an owned immutable byte snapshot into a bounded background preparation queue. Workers may decompress archives and parse supported data into plain arrays; they must not touch Unity objects, live IL2CPP object graphs or shared game dictionaries.
4. Schedule Unity asset creation on the main thread with a small configurable work budget. Prefer engine async operations only where they actually exist in the shipped player. Never call `.Wait()`, `.Result` or a completion-polling spin loop on that thread.
5. Publish completed assets through the original loader's supported registration/completion path after dependencies are ready. Whether a native fallback appearance can be retained safely must be verified; do not return null or invented placeholders from synchronous getters.
6. Deduplicate work by content identity and revision, reuse completed assets, bound queued bytes and decoded memory, and cancel obsolete jobs on session/map teardown. Release temporary native objects on the main thread. Do not share a live `ZipArchive` between worker jobs.
7. Preserve the original importer as an explicit compatibility fallback. Report a fallback when it occurs, because it may still hitch; never silently claim the async path ran.

Suggested responsibilities: `ContentPreparationQueue` owns concurrency and byte limits; `ArchiveDecoder` produces managed data; `AssetUploadQueue` owns main-thread scheduling; `ContentCache` owns revisions/lifetimes; `GameContentAdapter` alone handles validated import and completion bindings. No new networking symbols are required by this design.

## Important limits

A per-frame budget controls when a Unity call starts; it cannot preempt an expensive call already running. Large image decode/upload, mesh creation, shader work, or a physics-scene rebuild can still dominate a frame. If one such call causes the hitch, it requires a separate engine-supported async/staging strategy. Do not reduce source texture resolution or change materials as an unrequested performance shortcut.

Simply wrapping the original importer in `Task.Run` is unsafe. Simply yielding once before an expensive synchronous call only moves the freeze to another frame. Replacing `AssetBundle.LoadFromFile` cannot be assumed to fix this zip/mesh/texture path.

## Acceptance checks

- Compare baseline and candidate frame-time distributions and worst join hitch at the same map/settings, including cached and uncached content, large outfits and several concurrent joins.
- Verify content fidelity, dependency ordering, cache reuse, teardown during load, corrupt archives, repeated joins, and bounded memory/concurrency.
- Establish that workers execute no Unity calls; capture per-stage timings to prove the intended async path ran.
- Verify the actual in-game importer before calling the mod functional. Offline queue tests alone do not verify the native integration or eliminate join freezes.

## Primary references

- [mod.io Unity initialization](https://docs.mod.io/unity/initialization): plugin async programming model.
- [mod.io UGC best practices](https://docs.mod.io/unity/ugc-best-practices): generic file content versus bundles/addressables.
- [Unity async upload pipeline](https://unity.com/blog/engine-platform/understanding-the-async-upload-pipeline): synchronous loading may block on uploads.
- [Unity InstantiateAsync](https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/object/instantiateasync): final integration and Awake still execute on the main thread; API availability in this stripped player must be validated.
