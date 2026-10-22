# Writing C# mods

Start with `sdk/dotnet/template/MyMod`: one class, typed settings, and an ordinary C# callback.
The SDK saves properties marked with `[Toggle]`, `[Slider]`, `[Choice]`, or `[KeyBinding]` and
shows them in the phone menu. Defaults and saved values are available before `OnLoad`.
Use `Key = "existing_key"` when renaming a property whose settings already ship to players.
Changes from the menu update the property before `OnSettingChanged` runs.

```csharp
[Slider("Distance", 3.5, 1, 10)]
public float CameraDistance { get; private set; }

[KeyBinding("Apply", Key.F9)]
public Key ApplyKey { get; private set; }

public override void OnLoad() =>
    Input.OnPressed(() => ApplyKey, () => CameraSettings.FollowDistance = CameraDistance);
```

Use `Jibbers.Sdk.Game.Camera.CameraSettings` for camera options and
`Jibbers.Sdk.Game.Physics.Simulation.SlowWhile` for conditional slow motion. These APIs own their
main-thread dispatch or hooks. Input callbacks run while the game is focused and the player is
not typing. SDK callbacks belong to the mod and are removed on unload.
Bindings normally pause when the mod is disabled. For a shortcut that turns the mod back on,
use `Input.OnPressed(() => ToggleKey, Toggle).AllowWhenDisabled()`. The same option works with
`Input.OnSetting`; it still respects focus, text fields and unloading.

## Creating a HUD

Register your HUD in `OnLoad`. Registration and layout declarations need no Unity calls;
`Show`, `Hide`, and text changes dispatch to the main thread automatically.

```csharp
using Jibbers.Sdk.UI;

HudPanel hud = null!;
HudLabel progress = null!;

public override void OnLoad()
{
    hud = Hud.Register("Rewind");
    progress = hud.AddText("Progress", new HudTextOptions
    {
        Anchor = HudAnchor.BottomCenter,
        Offset = new(0, 194),
        Size = new(1400, 100),
        FontSize = 30,
    });
}

void ShowProgress(string message)
{
    progress.Text = message;
    hud.Show();
}

void HideProgress() => hud.Hide();
```

Each registered HUD owns an independent screen overlay. Layout coordinates scale from
1920×1080 by default; `HudOptions` changes resolution and draw order. `HudTextOptions`
controls anchors, offsets, size, alignment, color, wrapping, and rich text. The SDK chooses a
loaded game font, recreates UI after scene changes on the next update, and keeps HUD elements
from intercepting mouse input. `hud.Dispose()` removes it early; mod unload also unregisters
and releases every HUD automatically. Names must be unique within the owning mod.

## Public gameplay features

The public FPV, Atmosphere and Rewind entry points live in `mods/cs`. Their reusable controllers
live in `Jibbers.Sdk/Features`. `FeatureMod<TFeature>` forwards settings, metadata, commands and
lifecycle to a controller; `Configure` can customize a controller before startup. Controllers use
the host's settings store and identity so hook ownership and enabled toggles keep working.

| Mod entry point | SDK controller | Backend responsibilities |
| --- | --- | --- |
| `FpvMod` | `DroneController` | Camera ownership, input, physics, sound and display |
| `AtmosphereMod` | `AtmosphereEffects` | Bundles, shaders, presets and environment state |
| `ReshredMod` | `RewindController` | Physics snapshots, rewind and replay restoration |

These three DLLs require the matching SDK from the same build. Keep the runtime and mods packages
together when testing. These are the three retained gameplay mods and the only mods included in
the public source kit. Native loader, bridge and updater code remains Rust. The Rust starter was removed;
the native FPV sound generator is an asset build utility under `src/bin`, not a mod starter.

For the complete implementations, start with the
[public mod walkthrough](../../sdk/dotnet/Jibbers.Sdk/Features/README.md).
Each controller connects settings and lifecycle to a runtime. Gameplay services depend on narrow
interfaces; native calls and object ownership stay in `Integration` adapters. For example, Rewind's
hold interaction and history buffer can be read and tested without knowing MuJoCo or byte layouts.
FPV separates its flight session from camera, targets and presentation. Atmosphere separates
configuration, scene measurements, shader calculations and effect restoration.

Inspector delegates its probes to `Jibbers.Sdk.Diagnostics.InspectorCommands`, which dispatches
game calls and owns its event subscriptions. Advanced native signatures remain available in the
SDK's low-level APIs, but ordinary mods do not need them.

See [getting started](getting-started.md), [concepts](concepts.md), and
[native integration](native.md) for additional detail.
