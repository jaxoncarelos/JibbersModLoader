using System.Diagnostics;
using Jibbers.Sdk.UI;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>
/// The SDK's own Party section in the Mods tab and the Party panel: a hotkey opens a card where the
/// player makes, joins, locks and leaves parties, and joins party members' game servers
/// (<see cref="PlayTogether"/>, which every player has, so every player has the panel too).
/// </summary>
internal sealed class PartyMod : JibbersMod
{
    const double ToastLife = 4.5;

    public override string Id => "jibbers_party";
    public override string Name => "Party";
    public override string Author => "Jibbers SDK";
    public override string Description => "Make or join a party to play multiplayer mods with friends over Steam.";
    public override bool EnabledToggle => false;

    [KeyBinding("Open the Party panel", Key.F7, Key = "panel_key")]
    public Key PanelKey { get; private set; }

    [Toggle("Show party messages", Key = "toasts", Tooltip = "Short notes at the top of the screen when people join or leave.")]
    public bool ShowToasts { get; private set; }

    readonly object gate = new();
    readonly PanelUi ui = new();
    readonly List<(string Text, double At)> toasts = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    List<PanelAction> actions = new();
    OverlayPanel? panel, toastPanel;
    Thread? ticker;
    volatile bool open, stopping;
    int frames;

    public override void OnLoad()
    {
        panel = Overlay.Register("party", new OverlayOptions { Anchor = HudAnchor.MiddleRight, Offset = new Vector2(-36, 0) });
        toastPanel = Overlay.Register("party_toasts", new OverlayOptions { Anchor = HudAnchor.TopCenter, Offset = new Vector2(0, -14), SortingOrder = 930 });
        Input.OnPressed(() => PanelKey, Toggle);
        // The panel's own keys exist only while it is open (and letters only while typing a code),
        // so the game keeps every key to itself the rest of the time.
        Key WhenOpen(Key key) => open ? key : 0;
        Input.OnPressed(() => WhenOpen(Key.Up), () => Move(-1));
        Input.OnPressed(() => WhenOpen(Key.Down), () => Move(1));
        Input.OnPressed(() => WhenOpen(Key.Enter), Choose);
        Input.OnPressed(() => WhenOpen(Key.Backspace), Back);
        foreach (var key in Enumerable.Range((int)Key.A, 26).Concat(Enumerable.Range((int)Key.D2, 8)).Concat(Enumerable.Range((int)Key.Num2, 8)))
        {
            var c = key >= (int)Key.Num0 ? (char)('0' + key - (int)Key.Num0) : (char)key;
            Input.OnPressed(() => open && ui.EnteringCode ? (Key)key : 0, () => Type(c));
        }
        Party.Changed += Redraw;
        Party.Notice += Toast;
        if (PlayTogether.Current is { } server) server.Changed += Redraw;
        Party.PanelKeySource = () => PanelKey.ToString();
        ticker = new Thread(Tick) { IsBackground = true, Name = "Jibbers Party panel" };
        ticker.Start();
        Log($"press {PanelKey} for the Party panel");
    }

    public override void OnUnload()
    {
        stopping = true;
        Party.Changed -= Redraw;
        Party.Notice -= Toast;
        if (PlayTogether.Current is { } server) server.Changed -= Redraw;
        Party.Shutdown();
    }

    void Toggle()
    {
        lock (gate)
        {
            open = !open;
            ui.Selected = 0;
            ui.EnteringCode = false;
        }
        Redraw();
    }

    void Move(int by)
    {
        if (!open) return;
        lock (gate)
        {
            if (ui.EnteringCode || actions.Count == 0) return;
            ui.Selected = (ui.Selected + by + actions.Count) % actions.Count;
        }
        Redraw();
    }

    void Choose()
    {
        if (!open) return;
        Action? run = null;
        lock (gate)
        {
            if (ui.EnteringCode)
            {
                if (ui.Code.Length == 6)
                {
                    var code = ui.Code;
                    run = () => Party.Join(code);
                    ui.EnteringCode = false;
                }
            }
            else if (ui.Selected >= 0 && ui.Selected < actions.Count) run = actions[ui.Selected].Run;
        }
        run?.Invoke();
        lock (gate) ui.Selected = 0;
        Redraw();
    }

    void Back()
    {
        if (!open) return;
        lock (gate)
        {
            if (!ui.EnteringCode) return;
            if (ui.Code.Length == 0) ui.EnteringCode = false;
            else ui.Code = ui.Code[..^1];
        }
        Redraw();
    }

    void Type(char c)
    {
        if (!open) return;
        lock (gate)
        {
            if (!ui.EnteringCode || ui.Code.Length >= 6) return;
            ui.Code += c;
        }
        Redraw();
    }

    void Toast(string text)
    {
        if (!ShowToasts) return;
        lock (gate)
        {
            toasts.Insert(0, (text, clock.Elapsed.TotalSeconds));
            if (toasts.Count > 3) toasts.RemoveAt(3);
        }
        DrawToasts();
    }

    void Redraw()
    {
        if (panel is null) return;
        if (!open)
        {
            panel.Hide();
            return;
        }
        var snapshot = SnapshotNow();
        OverlayFrame frame;
        lock (gate)
        {
            var server = PlayTogether.Current;
            actions = PartyPanel.Actions(snapshot, ui, server?.PanelActions());
            if (ui.Selected >= actions.Count) ui.Selected = Math.Max(0, actions.Count - 1);
            frame = PartyPanel.Build(snapshot, ui, actions, PanelKey.ToString(), clock.Elapsed.TotalSeconds, server?.Status ?? "");
        }
        panel.Draw(frame);
        panel.Show();
    }

    void DrawToasts()
    {
        if (toastPanel is null) return;
        var now = clock.Elapsed.TotalSeconds;
        List<(string, double)> live;
        lock (gate)
        {
            toasts.RemoveAll(t => now - t.At > ToastLife);
            live = toasts.Select(t => (t.Text, now - t.At)).ToList();
        }
        if (live.Count == 0)
        {
            toastPanel.Hide();
            return;
        }
        toastPanel.Draw(PartyPanel.Toasts(live, ToastLife));
        toastPanel.Show();
    }

    /// <summary>Animates the busy spinner and fades the toasts.</summary>
    void Tick()
    {
        while (!stopping)
        {
            Thread.Sleep(70);
            try
            {
                // The spinner at ~8 frames a second: each frame re-uploads the panel's texture.
                if (open && ++frames % 2 == 0 && Party.State is PartyState.Creating or PartyState.Joining or PartyState.Unavailable) Redraw();
                bool any;
                lock (gate) any = toasts.Count > 0;
                if (any) DrawToasts();
            }
            catch (Exception e)
            {
                Sdk.Log.Error("party panel", e);
                Thread.Sleep(2000);
            }
        }
    }

    static PartySnapshot SnapshotNow() => new(Party.State, SteamId.None, Party.Code, Party.Locked, Party.Members, Party.Friends, Party.Status);
}
