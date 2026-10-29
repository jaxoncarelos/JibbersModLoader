# Coverage: every mod can be written in C#

Every technique the existing mods use, and the C# API for it. If a mod could do it in Rust, it can
do it in C#.

| Mod | Technique | C# |
| --- | --- | --- |
| **Atmosphere** | per-frame work in `EnvironmentManager.Update` | `Game.Env.Manager.UpdateMethod.Before(...)` |
| | map loaded (which map, its terrains) | `Game.Map.LoadEvent.Subscribe(e => e.Map.Name ...)` |
| | post-processing (Beautify) fields | `Game.Env.Manager.Beautify_Field.GetObject(manager.Ptr)`, then `Il2Cpp.ClassOf(...).Field("contrast")` |
| | load an asset bundle, spawn a prefab | `AssetBundles.Load(path)`, `.Instantiate("Prefab", parent)`, `Dispose()` |
| | shader globals, render settings | `Unity.CallStatic(Unity.Class("UnityEngine.Shader"), "SetGlobalFloat", ...)`, `Il2Cpp.ICall` for stripped setters |
| | renderers of every rider | `Unity.GetComponentsInChildren(rider.Ptr, Unity.Class("UnityEngine.Renderer"))` |
| **Reshred** | record and restore physics state each step | `Game.Physics.Scene.FixedUpdateMethod.Before(...)`; MuJoCo's `mj_getState`/`mj_setState` via `[DllImport("pcore.dll")]` |
| | the replay recorder and its clip | `Game.Replay.Recorder.Instance`, `.Clip`, `Game.Replay.Clip` |
| | rider state fields | `Game.Player.Ragdoll` + `.Raw` (every field) |
| | read and disable controller bindings | `Input.Action`, `Input.Axis`, `Input.ActionNames`; Rewired through `Unity.CallStatic` / raw bindings |
| **CollisionStream** | hook the map import | `Game.Map.Importer.ImportMethod.Before(...)` |
| | gate the physics step | `Game.Physics.Scene.StepSceneMethod.Detour(fn)` (see the SlowMo example) |
| | replace scene compilation, swap the model | `Game.Physics.Scene.CompileSceneMethod.Detour(fn)`; MuJoCo exports via `NativeLibrary.GetExport` + `Detour.Create` |
| | the rider's body and the selected spawn | `Game.Player.RagdollSpawner.First()?.Rider`, `.SelectedSpawn()` |
| | geoms, shapes, bodies | raw `Jibbers.Interop.Mujoco.MjGeom`, `MjShapeComponent`, `MjBody` |
| **FastLoad** | cache compiled physics | detours of `CompileScene`, `MujocoSpec.ParseXmlString`, `mj_compile` (raw `Jibbers.Interop.Mujoco.*` method refs, `NativeCode`) |
| | loading timings | `Game.Physics.Scene` + raw `MjScene.get_LastRebuildMs` |
| **Filmer** | per-frame camera control | `Game.Camera.Follow.LateUpdateMethod.Before(...)`, `.Target`, `.LockInput`, `.PhotoMode`, `.Mode` |
| | hotkeys | `Input.OnPressed(Key.F6, ...)`, `Input.OnSetting(this, "key", ...)` |
| **SkiSmooth** | find ski meshes and edit vertices | `Game.Ski.Segment.All()`, `.MeshTransform`, raw `UnityEngine.Mesh` methods via `Unity.Call` |
| **FPV** | fly a camera, read sticks | `Gamepad.Read().WithDeadzone(...)`, `Input.IsDown`, `Game.Camera.Follow` (`.Mode`, `.Target`, `.GroundLayer`), typed `Jibbers.Interop.UnityEngine.Transform` / `Camera` |
| | raycasts and sphere casts | `Jibbers.Interop.UnityEngine.Physics` raw bindings (engine classes, below) |
| | audio prefab from a bundle | as Atmosphere |
| **Mods tab** | clone game widgets, register buttons | `Game.Ui.NavigatableMenu.AddButton`, `.Layout()`, `Game.Ui.Behaviour.SetInteractable`, `Game.Ui.Button.Create(...)` |
| | the game's own options | `GameVars.Get*`, `GameVars.Set`, `GameVars.Watch` |
| **All** | settings, Mods tab section, persistence | `Settings`, `Get*`, `OnSettingChanged` |
| | commands from tools and other mods | `OnCommand` |
| | logging | `Log(...)`, `Jibbers.Sdk.Log.Info` |

## The mods, ported

The retained gameplay mods are FPV, Atmosphere and Rewind (Alpine Vision extends Atmosphere). Each
lives entirely in its own project under `mods/cs/` (gameplay, engine adapters and a `<Mod>.Tests` project).
Gameplay, state and parameter calculations are tested without running the game.

| Project | Mod | Tests | Notes |
| --- | --- | --- | --- |
| `mods/cs/Atmosphere` | Atmosphere | cloud bank, presets, merge, fog slider, Mods-tab layout | needs `jibbers_atmosphere.bundle` beside `Jibbers.exe` |
| `mods/cs/Reshred` | Rewind | scrub, capacity, Rewired reservation | MuJoCo state through `pcore.dll` |
| `mods/cs/FPV` | FPV | math, sound synthesis, flight model (Acro/Angle/Cine, collisions, follow), input ramps and deadzone, OSD, bundled clips match the synth | needs `jibbers_fpv.bundle` in `JibbersMods` |

The source and binary packages include only FPV, Atmosphere and Rewind. The other mod folders
have been removed. See [rewrite status](../CSHARP-REWRITE.md) for packaging and the native-to-C# switch.

Build one with `dotnet build mods/cs/<Name> -c Release` (it copies itself into the game's
`JibbersMods\cs` when the game is installed; add `-p:JibbersNoInstall=true` to skip that).

## Engine classes

Every `UnityEngine.*` module the game ships (82 of them: Core, Physics, Animation, Audio, UI, Terrain,
Particles, ...) has raw bindings under `Jibbers.Interop.UnityEngine`, with real parameter names:
`new Transform(t).set_position(v)`, `Physics.Raycast(...)`, `Object.Instantiate(original, position, rotation)`.
Every wrapper converts to `nint`, so it passes straight to `Unity.Call` and the IL2CPP API.

Unity's names are not obfuscated, so these come from Unity 6000.3's own player assemblies
(`jibbers-gen import`, see the reverse-engineering guide) and resolve against the game by name when
first used. The game's build strips engine code it never calls: such a member throws
`SymbolException` ("not found") when used, and `Il2Cpp.ICall` is the fallback (FastLoad and
Atmosphere do this).

## Not covered on purpose

The catalogue and the generated bindings leave out the game's networking (FishNet, `NetBus`,
broadcast messages), server-side checks and other players' state. Mods that change those can affect
other players and mostly help cheating. Nothing stops you reaching the IL2CPP API directly, but it
is outside what the SDK supports.

Mods that play *together* use the SDK's own Steam party instead (`Jibbers.Sdk.Multiplayer`, see
[multiplayer](multiplayer.md)): messages between copies of the same mod, only among players who
joined a party. That is separate from the game's networking and never touches it. One
deliberate exception: SLVSH's spectating finds other riders in your session (their nametag or
position) to point *your own* follow camera at them; it changes nothing about them.
