using Jibbers.Sdk;
using Jibbers.Sdk.UI;

static class HudTests
{
    sealed class Surface : IHudSurface
    {
        public HudSnapshot? Snapshot;
        public bool OnMain, Fail, Disposed;
        public int Renders, Hides;
        public void Render(HudSnapshot snapshot)
        {
            Assert.True(OnMain);
            Renders++;
            if (Fail) throw new InvalidOperationException("render");
            Snapshot = snapshot;
        }
        public void Hide() { Assert.True(OnMain); Hides++; }
        public void Dispose() { Assert.True(OnMain); Disposed = true; }
    }

    sealed class Fixture
    {
        public readonly Surface Surface = new();
        public int Creations, Unregistrations;
        public HudPanel Panel() => new("Test", "hud_test", new(), () => { Creations++; return Surface; }, action =>
        {
            Surface.OnMain = true;
            try { action(); }
            finally { Surface.OnMain = false; }
        }, _ => Unregistrations++);
    }

    [Test]
    static void RegistrationAndHiddenUpdatesDoNotNeedUnity()
    {
        var fixture = new Fixture();
        using var panel = fixture.Panel();
        var label = panel.AddText("Progress");
        label.Text = "ready";
        panel.Hide();
        Assert.Equal(0, fixture.Creations);
        panel.Show();
        Assert.Equal(1, fixture.Creations);
        Assert.Equal("ready", fixture.Surface.Snapshot!.Texts.Single().Text);
    }

    [Test]
    static void TextLayoutAndVisibilitySurviveHideAndShow()
    {
        var fixture = new Fixture();
        using var panel = fixture.Panel();
        var label = panel.AddText("Progress", new() { Anchor = HudAnchor.BottomCenter, Offset = new(0, 194) });
        panel.Show();
        panel.Hide();
        label.Text = "Rewinding";
        panel.Show();
        Assert.Equal(1, fixture.Creations);
        var shown = fixture.Surface.Snapshot!.Texts.Single();
        Assert.Equal("Rewinding", shown.Text);
        Assert.Equal(HudAnchor.BottomCenter, shown.Options.Anchor);
        Assert.Equal(new Vector2(0, 194), shown.Options.Offset);
        Assert.True(fixture.Surface.Hides > 0);
    }

    [Test]
    static void FailedRenderCanRetryAndDisposedHandlesCannotRecreateUi()
    {
        var fixture = new Fixture();
        var panel = fixture.Panel();
        var label = panel.AddText("Progress");
        fixture.Surface.Fail = true;
        Assert.Throws<InvalidOperationException>(panel.Show);
        fixture.Surface.Fail = false;
        label.Text = "recovered";
        Assert.Equal("recovered", fixture.Surface.Snapshot!.Texts.Single().Text);
        panel.Dispose();
        panel.Dispose();
        Assert.Equal(1, fixture.Unregistrations);
        Assert.True(fixture.Surface.Disposed);
        Assert.Throws<ObjectDisposedException>(() => label.Text = "stale");
        Assert.Throws<ObjectDisposedException>(panel.Show);
    }

    [Test]
    static void UnchangedTextStillReachesRendererForSceneRecovery()
    {
        var fixture = new Fixture();
        using var panel = fixture.Panel();
        var label = panel.AddText("Status");
        label.Text = "ready";
        panel.Show();
        var renders = fixture.Surface.Renders;
        label.Text = "ready";
        Assert.Equal(renders + 1, fixture.Surface.Renders);
    }

    [Test]
    static void InvalidLayoutsAndDuplicateElementNamesFailBeforeRendering()
    {
        var fixture = new Fixture();
        using var panel = fixture.Panel();
        panel.AddText("Progress");
        Assert.Throws<ArgumentException>(() => panel.AddText("Progress"));
        Assert.Throws<ArgumentOutOfRangeException>(() => panel.AddText("Bad font", new() { FontSize = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => panel.AddText("Bad size", new() { Size = new(0, 100) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Hud.Register("Invalid", new() { MatchWidthOrHeight = 2 }));
        Assert.Equal(0, fixture.Creations);
    }

    sealed class TestMod : JibbersMod
    {
        public override string Id => "hud_owner_test";
        public override string Name => "HUD owner test";
    }

    [Test]
    static void ModUnloadUnregistersHudAndRejectsStaleHandles()
    {
        var mod = new TestMod();
        var panel = ModContext.With(mod, () => Hud.Register("Test"));
        var label = panel.AddText("Status");
        Assert.Throws<InvalidOperationException>(() => ModContext.With(mod, () => Hud.Register("Test")));
        Hud.RemoveOwner(mod.Id);
        Assert.Throws<ObjectDisposedException>(() => label.Text = "late callback");
        using var replacement = ModContext.With(mod, () => Hud.Register("Test"));
    }

    [Test]
    static void HudRegisteredByInputCallbackRetainsModOwnership()
    {
        var mod = new TestMod();
        HudPanel? panel = null;
        var binding = new Input.Binding
        {
            Owner = mod.Id, Key = () => (int)Key.X, Modifiers = () => true,
            Trigger = Input.Trigger.Pressed, Callback = () => panel = Hud.Register("Callback HUD"),
        };
        binding.Dispatch();
        Assert.Equal(mod.Id, panel!.Owner);
        Assert.True(ModContext.CurrentId is null);
        Hud.RemoveOwner(mod.Id);
        Assert.Throws<ObjectDisposedException>(() => panel.AddText("Late"));
    }

    [Test]
    static void ScreenAnchorsHavePredictableCoordinates()
    {
        Assert.Equal(new Vector2(0, 0), HudValidation.Anchor(HudAnchor.BottomLeft));
        Assert.Equal(new Vector2(0.5f, 0), HudValidation.Anchor(HudAnchor.BottomCenter));
        Assert.Equal(new Vector2(1, 1), HudValidation.Anchor(HudAnchor.TopRight));
    }
}
