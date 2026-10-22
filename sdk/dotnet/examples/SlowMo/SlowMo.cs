using Jibbers.Sdk;
using Jibbers.Sdk.Game.Physics;

namespace SlowMo;

public sealed class SlowMo : JibbersMod
{
    public override string Id => "slow_mo";
    public override string Name => "Slow Motion";
    public override string Description => "Hold a key to slow the physics down.";

    [KeyBinding("Hold for slow motion", Key.V, Key = "hold")]
    public Key HoldKey { get; private set; }

    [Slider("Speed while held", 0.25, 0.05, 1, 0.05, Key = "speed")]
    public double Speed { get; private set; }

    public override void OnLoad() => Simulation.SlowWhile(() => Input.IsDown(HoldKey), () => Speed);
}
