// SlowMo: hold a key to run the physics at a fraction of its speed.
//
// Shows a detour: the game's MuJoCo step (MjScene.StepScene) is replaced by a C# function with the
// same native signature, which calls the original only on some frames. This is the technique the
// Rust CollisionStream and FastLoad mods use, here in a few lines of C#.

using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Physics;

namespace SlowMo;

public sealed unsafe class SlowMo : JibbersMod
{
    public override string Id => "slow_mo";
    public override string Name => "Slow Motion";
    public override string Description => "Hold the key to slow the physics down.";

    public override IEnumerable<Setting> Settings => new[]
    {
        Setting.KeyBinding("hold", "Hold for slow motion", (int)Key.V),
        Setting.Slider("speed", "Speed while held", 0.25, 0.05, 1, 0.05),
    };

    static SlowMo? me;
    static Detour? step;
    static double owed;

    public override void OnLoad()
    {
        me = this;
        // Game methods compiled by IL2CPP take (this, args..., MethodInfo*): StepScene() is (nint, nint).
        step = Scene.StepSceneMethod.Detour((nint)(delegate* unmanaged<nint, nint, void>)&Step);
        step.Enable();
        Log("physics step detoured");
    }

    [UnmanagedCallersOnly]
    static void Step(nint scene, nint method)
    {
        var original = (delegate* unmanaged<nint, nint, void>)step!.Original;
        var m = me;
        if (m is null || !m.Enabled || !Input.GameFocused || !Input.IsDown(m.GetKey("hold")))
        {
            original(scene, method);
            return;
        }
        // Run the original on a fraction of the frames: speed 0.25 = one step in four.
        owed += m.GetDouble("speed");
        if (owed >= 1)
        {
            owed -= 1;
            original(scene, method);
        }
    }

    public override void OnUnload() => step?.Dispose();
}
