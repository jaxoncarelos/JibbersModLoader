using Jibbers.Sdk;
using Jibbers.Sdk.Multiplayer;
using Jibbers.Sdk.UI;

static class OverlayTests
{
    sealed class Surface : IOverlaySurface
    {
        public readonly List<OverlaySnapshot> Renders = new();
        public int Hides;
        public bool Disposed;
        public void Render(OverlaySnapshot s) => Renders.Add(s);
        public void Hide() => Hides++;
        public void Dispose() => Disposed = true;
    }

    static (OverlayPanel, Surface) Panel(string name)
    {
        var surface = new Surface();
        return (Overlay.Register(name, null, () => surface, a => a(), background: false), surface);
    }

    [Test]
    static void ShapesAreRasterizedOnlyWhenTheyChange()
    {
        var (panel, surface) = Panel("raster-once");
        var frame = new OverlayFrame(100, 40).Card(0, 0, 100, 40).Text("a", 0, 0, 50, 20, 14, Theme.Text);
        panel.Draw(frame);
        Assert.Equal(0, surface.Renders.Count); // hidden: nothing built
        panel.Show();
        Assert.True(surface.Renders.Single().Pixels is { Length: 200 * 80 * 4 });
        panel.Draw(new OverlayFrame(100, 40).Card(0, 0, 100, 40).Text("b", 0, 0, 50, 20, 14, Theme.Text));
        Assert.Equal(2, surface.Renders.Count);
        Assert.True(surface.Renders[1].Pixels is null, "same shapes: only the text changed");
        Assert.Equal("b", surface.Renders[1].Texts.Single().Text);
        panel.Hide();
        Assert.Equal(1, surface.Hides);
        panel.Dispose();
        Assert.True(surface.Disposed);
        Assert.Throws<ObjectDisposedException>(() => panel.Show());
    }

    [Test]
    static void RasterEdgesAreSmoothAndShapesLandWhereAsked()
    {
        var r = new Raster(40, 20, 1);
        r.RoundRect(10, 5, 20, 10, 0, Theme.Bad, Theme.Bad);
        Assert.Equal((byte)255, r.Pixel(15, 10).A);
        Assert.Equal((byte)0, r.Pixel(5, 10).A);
        r.Circle(5, 5, 3.3f, Theme.Good);
        var edge = r.Pixel(8, 5).A;
        Assert.True(edge is > 0 and < 255, $"anti-aliased edge, got {edge}");
        var arc = new Raster(40, 40, 1);
        arc.Arc(20, 20, 15, 4, 0, 90, Theme.Ice); // 12 o'clock to 3 o'clock
        Assert.True(arc.Pixel(27, 7).A > 200, "upper right quarter drawn");
        Assert.Equal((byte)0, arc.Pixel(7, 27).A);
        var rgba = arc.ToRgba32();
        Assert.Equal(40 * 40 * 4, rgba.Length);
    }

    [Test]
    static void PartyPanelFitsEveryState()
    {
        foreach (var (name, snapshot, ui, server, status) in OverlayPreview_Scenarios())
        {
            var actions = PartyPanel.Actions(snapshot, ui, server);
            var frame = PartyPanel.Build(snapshot, ui, actions, "F7", 1.0, status);
            foreach (var t in frame.Texts)
                Assert.True(t.X >= PartyPanel.Margin && t.Y >= PartyPanel.Margin && t.X + t.W <= frame.Width - PartyPanel.Margin + 0.01f && t.Y + t.H <= frame.Height - PartyPanel.Margin + 0.01f, $"{name}: text '{t.Text}' inside the card");
        }
        Assert.Equal("SLVSH", PartyPanel.Pretty("slvsh"));
        Assert.Equal("Slow Mo", PartyPanel.Pretty("slow_mo"));
    }

    internal static IEnumerable<(string, PartySnapshot, PanelUi, List<PanelAction>?, string)> OverlayPreview_Scenarios()
    {
        IReadOnlyDictionary<string, string> Mods(params string[] ids) => ids.ToDictionary(i => i, _ => "1.0.0");
        var members = new List<PartyMember>
        {
            new(new SteamId(76561198000000001), "Scott", true, true, Mods("slvsh"), 0),
            new(new SteamId(76561198000000002), "Ben Carver", false, false, Mods("slvsh", "rumble"), 38),
            new(new SteamId(76561198000000003), "mira", false, false, Mods("slvsh"), 121),
            new(new SteamId(76561198000000004), "Hugo with a very long Steam name", false, false, Mods(), 240),
        };
        var friends = new List<FriendParty>
        {
            new(new SteamId(5), "Ana", true, 3, false),
            new(new SteamId(6), "Kai", true, 1, false),
            new(new SteamId(7), "Lou", false, 0, false),
        };
        yield return ("Not in a party", new(PartyState.None, SteamId.None, "", false, Array.Empty<PartyMember>(), friends, ""), new PanelUi(), null, "");
        yield return ("Bad code", new(PartyState.None, SteamId.None, "", false, Array.Empty<PartyMember>(), friends.Take(0).ToList(), "No party found with code ZZZZZZ"), new PanelUi { Selected = 1 }, null, "");
        yield return ("Host with friends", new(PartyState.InParty, SteamId.None, "K7Q2MX", false, members, friends.Skip(2).ToList(), ""), new PanelUi { Selected = 0 }, null, "");
        yield return ("Guest", new(PartyState.InParty, SteamId.None, "K7Q2MX", true, members.Select(m => m with { IsHost = m.Name == "Ben Carver", IsLocal = m.Name == "mira" }).ToList(), Array.Empty<FriendParty>(), ""), new PanelUi { Selected = 1 }, null, "");
        yield return ("Typing a code", new(PartyState.None, SteamId.None, "", false, Array.Empty<PartyMember>(), friends, ""), new PanelUi { EnteringCode = true, Code = "K7Q" }, null, "");
        yield return ("Joining", new(PartyState.Joining, SteamId.None, "", false, Array.Empty<PartyMember>(), friends, "Looking for party K7Q2MX"), new PanelUi(), null, "");
        var guest = members.Select(m => m with { IsHost = m.Name == "Ben Carver", IsLocal = m.Name == "mira" }).ToList();
        yield return ("Friend is hosting", new(PartyState.InParty, SteamId.None, "K7Q2MX", false, guest, Array.Empty<FriendParty>(), ""), new PanelUi(),
            new List<PanelAction> { new("Join Ben Carver's server", "Play in their world", () => { }) }, "");
        yield return ("Hosting a server", new(PartyState.InParty, SteamId.None, "K7Q2MX", false, members, friends.Skip(2).ToList(), ""), new PanelUi(),
            new List<PanelAction>(), "Hosting · 2 joined through Steam");
        yield return ("On a friend's server", new(PartyState.InParty, SteamId.None, "K7Q2MX", false, guest, Array.Empty<FriendParty>(), ""), new PanelUi { Selected = 0 },
            new List<PanelAction>(), "Playing on Ben Carver's server");
    }
}

static class PreviewRunner
{
    /// <summary>Draws every Party panel state and some toasts to <c>&lt;dir&gt;/party.html</c>.</summary>
    public static int Run(string dir)
    {
        var frames = OverlayTests.OverlayPreview_Scenarios()
            .Select(s => (PartyPanel.Build(s.Item2, s.Item3, PartyPanel.Actions(s.Item2, s.Item3, s.Item4), "F7", 0.4, s.Item5), s.Item1)).ToList();
        frames.Add((PartyPanel.Toasts(new[] { ("Ben Carver is hosting: press F7 to join", 1.0), ("Party made. Code: K7Q2MX", 2.0), ("mira left the party", 4.2) }, 4.5), "Toasts"));
        OverlayPreview.Save(frames, Path.Combine(dir, "party.html"));
        Console.WriteLine(Path.GetFullPath(Path.Combine(dir, "party.html")));
        return 0;
    }
}
