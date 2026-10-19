# Coverage: every mod can be written in C#

Every technique the existing mods use, and the C# API for it. If a mod could do it in Rust, it can
do it in C#.

| Mod | Technique | C# |
| --- | --- | --- |
| **Atmosphere** | per-frame work in `EnvironmentManager.Update` | `Game.Env.Manager.UpdateMethod.Before(...)` |
| | map loaded (which map, its terrains) | `Game.Map.LoadEvent.Subscribe(e => e.Map.Name ...)` |
| | post-processing (Beautify) fields | `Game.Env.Manager.Beautify_Field.GetObject(manager.Ptr)`, then `Il2Cpp.ClassOf(...).Field("contrast")` |
| | load an asset bundle, spawn a prefab | `Il2Cpp.ICall("UnityEngine.AssetBundle::LoadFromFile_Internal_Injected")` with a detour-style function pointer, `Unity.Instantiate` |
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
| **FPV** | fly a camera, read sticks | `Input.Axis`, `Input.IsDown`, `Unity.Transform` + `set_position`/`set_rotation` through `Unity.Call` |
| | audio prefab from a bundle | as Atmosphere |
| **Mods tab** | clone game widgets, register buttons | `Game.Ui.NavigatableMenu.AddButton`, `.Layout()`, `Game.Ui.Behaviour.SetInteractable`, `Game.Ui.Button.Create(...)` |
| | the game's own options | `GameVars.Get*`, `GameVars.Set`, `GameVars.Watch` |
| **All** | settings, Mods tab section, persistence | `Settings`, `Get*`, `OnSettingChanged` |
| | commands from tools and other mods | `OnCommand` |
| | logging | `Log(...)`, `Jibbers.Sdk.Log.Info` |

## The mods, ported

Every original Rust mod except FPV has a C# port in `mods/cs/`, built against this SDK. Their
game-free logic carries the Rust tests over (`sdk/dotnet/Jibbers.Sdk.Tests`), and the native parts
are tested against the game's own MuJoCo (`pcore.dll`) outside the game.

| Port | Rust original | Tests ported | Notes |
| --- | --- | --- | --- |
| `mods/cs/Atmosphere` | JibbersAtmosphere | cloud bank, presets, merge, fog slider, Mods-tab layout | needs `jibbers_atmosphere.bundle` beside `Jibbers.exe` |
| `mods/cs/Reshred` | JibbersReshred | scrub, capacity, Rewired reservation | MuJoCo state through `pcore.dll` |
| `mods/cs/FastLoad` | JibbersFastLoad | load clock, cacheability, cache round trip on real MuJoCo | four detours, same cache format |
| `mods/cs/CollisionStream` | JibbersCollisionStream | planner, step gate, background compile on real MuJoCo | off by default, as in Rust; spawner lookup fixed (uses `player.ragdoll_spawner`) |
| `mods/cs/Filmer` | JibbersFilmer | director camera math | |
| `mods/cs/SkiSmooth` | JibbersSkiSmooth | smoothing, subdivision, normals | |
| — | JibbersFPV | — | still Rust; being changed separately. Everything it uses is listed above |

Build one with `dotnet build mods/cs/<Name> -c Release` (it copies itself into the game's
`JibbersMods\cs` when the game is installed; add `-p:JibbersNoInstall=true` to skip that).

## Not covered on purpose

The catalogue and the generated bindings leave out the game's networking (FishNet, `NetBus`,
broadcast messages), server-side checks and other players' state. Mods that change those can affect
other players and mostly help cheating. Nothing stops you reaching the IL2CPP API directly, but it
is outside what the SDK supports.
