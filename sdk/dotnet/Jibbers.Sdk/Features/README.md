# Reading the public mods

Start with the mod's controller, then its runtime. Those files describe gameplay using ordinary C#.
The `Integration` directories contain the Unity, IL2CPP and native physics adapters.
You do not need to learn those adapters to change the gameplay flow.

| Feature | Start here | Gameplay | Engine boundary |
| --- | --- | --- | --- |
| FPV | [DroneController](FPV/DroneController.cs) | [DroneRuntime](FPV/DroneRuntime.cs), [DroneFlightSession](FPV/DroneFlightSession.cs), [Drone](FPV/Drone.cs) | [Integration](FPV/Integration) |
| Atmosphere | [AtmosphereEffects](Atmosphere/AtmosphereEffects.cs) | [AtmosphereRuntime](Atmosphere/AtmosphereRuntime.cs), [settings store](Atmosphere/AtmosphereConfigurationStore.cs), [shader calculations](Atmosphere/AtmosphereShaderParameters.cs) | [Integration](Atmosphere/Integration) |
| Rewind | [RewindController](Reshred/RewindController.cs) | [RewindRuntime](Reshred/RewindRuntime.cs), [RewindPlayback](Reshred/RewindPlayback.cs), [RewindHistory](Reshred/RewindHistory.cs) | [Integration](Reshred/Integration) |

## Rewind: record, hold, restore, resume

`RewindRuntime` connects two game events. Physics steps advance `RewindHistory`; rendered frames
read input and ask `RewindPlayback` whether the hold threshold has been reached.

```csharp
var controls = input.Read(options);
var feedback = playback.Update(controls, history.Count, gameLoop.FixedStepSeconds,
    elapsedSeconds, gameLoop.IsPaused, options, inputError);
history.IsRewinding = feedback.IsRewinding;
```

`RewindHistory` owns the six-second buffer. During recording it captures a snapshot, reusing the
oldest allocation when full. During rewind it restores an earlier snapshot and discards frames
after it. Releasing the button resumes recording from that point. A changed scene identity clears
the buffer. Only the snapshot adapter knows how MuJoCo state and rider/replay scalar fields are stored.

To change the hold interaction, edit `RewindPlayback`. To change storage policy, edit
`RewindHistory`. To add another input source, implement `IRewindInput`.

## FPV: one camera, one flight session

`DroneRuntime` replaces a session when the active camera changes. `DroneFlightSession` reads pilot
input, decides whether to take off or land, advances the flight model, and presents telemetry.
`Drone` owns flight physics and collision response. Its inputs are typed controls, settings,
an optional target and `ICollisionWorld`; it has no game-object pointers.

Camera restoration, target tracking, HUD and audio have independent owners. HUD or audio failure
can disable that component without ending a flight. An unexpected session failure lands and
releases the session before stopping further updates.

To add a flight rule, edit the flight model. To change takeoff/landing behavior, edit the session.
To change a control binding, start with `PilotSettings`, `PilotInput` and `DroneModSettings`.

## Atmosphere: settings and measurements produce shader parameters

`AtmosphereConfigurationStore` owns presets, persistence and menu translation. Its typed snapshot
is reused until settings change. `AtmosphereRuntime` coordinates surface changes, quality settings,
grading, prefab availability and rendering. Scene measurement and shader packing are separate:
`AtmosphereFrame` contains measurements, and `AtmosphereShaderParameters` calculates shader values.

To add an effect setting, extend the configuration and typed render settings, then its parameter
calculation. To change when effects apply, edit the runtime. The renderer owns character-mask roots;
grading owns the environment reference and its original values.

## Boundaries and ownership

- Controllers translate the SDK lifecycle and saved setting keys into typed options.
- Runtimes describe gameplay policy and depend on small interfaces in each `Contracts` file.
- Pure state, math, formatting and parameter calculations can run without the game.
- Engine adapters perform native calls on the game thread and own the references they retain.
- `AssetBundles`, `OwnedGameObject`, `ResourceScope` and `Cleanup` provide shared backend plumbing.
  Partial construction rolls back; unloading attempts every cleanup even if one fails.

Avoid adding native calls to a runtime or copying bundle loading and object cleanup into a mod.
Name values for their role (`fixedStepSeconds`, `desiredVelocity`, `surfaceNormal`), and keep saved
setting keys unchanged when renaming a C# member.

The adjacent `Jibbers.Sdk.Tests` project exercises these flows with fake scenes, input, cameras and
effect services. Run `dotnet run --project sdk/dotnet/Jibbers.Sdk.Tests -c Release
-p:JibbersNoInstall=true` from the repository root. Engine integration still needs in-game testing.
