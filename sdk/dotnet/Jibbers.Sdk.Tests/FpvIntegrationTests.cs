using Jibbers.Sdk.Features.Fpv;
using Jibbers.Sdk;
using Jibbers.Sdk.Symbols;

static class FpvIntegrationTests
{
    sealed class EmptyWorld : IWorld { public Hit? Sweep(V3 o, V3 d, float length, float radius) => null; }
    [Test] static void InvalidTimeCannotPoisonTheCameraAndRecallResetsACrash()
    {
        var d = new Drone(new(0, 20, 0), Quat.Identity, Mode.Angle);
        d.Step(float.NaN, default, null, new EmptyWorld(), new());
        Assert.Equal(new V3(0, 20, 0), d.Position);
        Assert.True(d.Camera(new(), 1).Finite);
        d.Disarmed = 1; d.Velocity = new(10, -20, 5);
        d.Recall(default, V3.Forward);
        Assert.Equal(new V3(0, 2, -4), d.Position);
        Assert.Equal(default, d.Velocity); Assert.Equal(0f, d.Disarmed);
    }
    [Test] static void ControllerActionsUseEdgesAndLosingFocusCancelsALockTap()
    {
        var input = new PilotInput(); var c = new PilotConfig();
        Pad P(ushort buttons) => new(0, 0, 0, 0, 0, 0, buttons);
        var (_, a) = input.Update(0.016f, true, P(0xc0), default, _ => false, c); Assert.True(a.Toggle);
        (_, a) = input.Update(0.016f, true, P(0xc0), default, _ => false, c); Assert.True(!a.Toggle);
        input.Update(0.1f, true, P(0x200), default, _ => false, c);
        (_, a) = input.Update(0.1f, true, P(0), default, _ => false, c); Assert.True(a.Lock);
        input.Update(0.1f, true, P(0x200), default, _ => false, c);
        (_, a) = input.Update(0.1f, false, P(0), default, _ => false, c); Assert.True(!a.Lock && !a.Unlock);
        for (var i = 0; i < 5; i++) (_, a) = input.Update(0.1f, true, P(0x200), default, _ => false, c);
        Assert.True(a.Unlock);
        (_, a) = input.Update(0.1f, true, P(0), default, _ => false, c); Assert.True(!a.Lock);
    }
    [Test] static void KeyboardOverridesAbsoluteThrottleAndMouseInvertIsApplied()
    {
        var input = new PilotInput(); var c = new PilotConfig { Throttle = Throttle.Trigger, InvertPitch = true };
        var p = new Pad(0, 0, 0, 1, 0.2f, 0.7f, 0);
        var (ctl, _) = input.Update(0.1f, true, p, new(5, 5, true), _ => false, c);
        Assert.Equal(0.7f, ctl.Throttle); Assert.Equal(-1f, ctl.Pitch); Assert.True(ctl.LookPitch > 0 && ctl.Precision);
        (ctl, _) = input.Update(0.1f, true, p, default, k => k == (int)Key.W, c);
        Assert.True(ctl.Throttle is null);
    }
    [Test] static void RiderNamesCannotInjectHudMarkup()
    {
        var blocks = Effects.Text(new(Mode.Cine, 0, 0, null, 10, "<size=999>rider", false, false, false));
        Assert.True(!blocks[2].Contains("<size=999>"));
    }
    [Test] static void FpvSymbolsAreIncludedAndValidAlongsideTheSdkCatalogue()
    {
        using var stream = typeof(DroneController).Assembly.GetManifestResourceStream("Fpv.symbols.json")!;
        using var reader = new StreamReader(stream);
        var cat = Catalogue.Parse(SymbolTable.BuiltInJson);
        cat.Symbols.AddRange(Catalogue.Parse(reader.ReadToEnd()).Symbols);
        Assert.Equal(0, Shape.Lint(cat).Count);
    }
}
