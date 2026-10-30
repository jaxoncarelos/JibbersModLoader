using Jibbers.Sdk;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Physics;
using Jibbers.Sdk.UI;

namespace ChairLift;

/// <summary>
/// Mod entry point: ride the map's chair lifts. At a chair lift's bottom station press A (Xbox),
/// Cross (PlayStation through Steam Input) or G; sit in a free seat of the next chair (four to a
/// chair, lined up, so you can ride with friends), open the phone on the way up, and get
/// off near the top (the phone: Y or H). T-bars and rope tows are left alone.
/// </summary>
public sealed class ChairLiftMod : JibbersMod
{
    public override string Id => "chairlift";
    public override string Name => "Chair Lift";
    public override string Author => "scotttucker1599";
    public override string Description => "Ride the chair lifts: sit with friends, use the phone on the way up, get off at the top.";

    [Toggle("Controller: A to ride, Y for the phone", Key = "controller", Section = "Buttons")]
    public bool UseController { get; private set; }

    [KeyBinding("Keyboard key to ride", Key.G, Key = "ride_key", Section = "Buttons")]
    public Key RideKey { get; private set; }

    [KeyBinding("Keyboard key for the phone on the chair", Key.H, Key = "phone_key", Section = "Buttons")]
    public Key PhoneKey { get; private set; }

    [Toggle("Get off automatically at the top", Key = "auto_off", Section = "Riding")]
    public bool AutoOff { get; private set; }

    [Slider("Get off this far before the top wheel (metres)", 12, 3, 40, 1, Key = "unload_distance", Section = "Riding")]
    public double UnloadDistance { get; private set; }

    [Toggle("Sit down on the chair", Key = "sit", Section = "Sitting")]
    public bool Sit { get; private set; }

    [Slider("Sitting: hip bend (%)", 85, 0, 100, 5, Key = "sit_hip", Section = "Sitting")]
    public double HipBend { get; private set; }

    [Slider("Sitting: knee bend (%)", 55, 0, 100, 5, Key = "sit_knee", Section = "Sitting")]
    public double KneeBend { get; private set; }

    [Slider("Seat height (metres above the chair's bottom)", 0.8, -3, 4, 0.1, Key = "seat_height", Section = "Fit the seats")]
    public double SeatHeight { get; private set; }

    [Slider("Seat forward / back (metres)", -0.35, -2, 3, 0.05, Key = "seat_back_m", Section = "Fit the seats")]
    public double SeatForward { get; private set; }

    [Slider("Space between seats (metres)", 0.6, 0.3, 1.5, 0.05, Key = "seat_spacing", Section = "Fit the seats")]
    public double SeatSpacing { get; private set; }

    RideRuntime? ride;
    HudPanel? hud;
    HudLabel? prompt;
    readonly List<IDisposable> subscriptions = new();
    bool active, rideWas, phoneWas;
    Stage shown = (Stage)(-1);

    public override void OnLoad()
    {
        ride = new RideRuntime();
        active = true;
        hud = Hud.Register("Chair Lift");
        prompt = hud.AddText("Prompt", new HudTextOptions
        {
            Anchor = HudAnchor.BottomCenter,
            Offset = new(0, 150),
            Size = new(1100, 60),
            FontSize = 26,
        });
        Manager.UpdateMethod.Before((_, _) => Guard(OnFrame));
        Scene.FixedUpdateMethod.Before((scene, _) => Guard(() => ride?.PhysicsStep(scene)));
        subscriptions.Add(Events.Subscribe("event.respawn", _ => ride?.Respawned()));
        subscriptions.Add(Events.Subscribe("event.reset", _ => ride?.Respawned()));
        subscriptions.Add(Events.Subscribe("event.player_spawn", _ => ride?.MapChanged()));
        subscriptions.Add(Events.Subscribe("event.pause", _ => { if (ride is { } r) r.PhoneOpen = true; }));
        subscriptions.Add(Events.Subscribe("event.unpause", _ => { if (ride is { } r) r.PhoneOpen = false; }));
    }

    /// <summary>Runs a hook callback unless the mod is off or unloading, logging failures once.</summary>
    void Guard(Action a)
    {
        if (!active || !Enabled)
            return;
        try
        {
            a();
        }
        catch (Exception e)
        {
            if (lastError != e.Message)
            {
                lastError = e.Message;
                Log($"error: {e.Message}");
            }
        }
    }
    string? lastError;

    void OnFrame()
    {
        if (ride is null)
            return;
        var focused = Input.GameFocused && !Input.Typing;
        var pad = focused && UseController ? Gamepad.Read() : default;
        var rideDown = focused && (Input.IsDown(RideKey) || pad.Held(PadButton.A));
        var phoneDown = focused && (Input.IsDown(PhoneKey) || pad.Held(PadButton.Y));
        var options = new RideOptions(new SeatFit((float)SeatHeight, (float)SeatForward, (float)SeatSpacing), Sit, HipBend, KneeBend, AutoOff, (float)UnloadDistance);
        ride.Frame(rideDown && !rideWas, phoneDown && !phoneWas, options);
        rideWas = rideDown;
        phoneWas = phoneDown;
        ShowPrompt(ride.Stage);
    }

    void ShowPrompt(Stage stage)
    {
        if (stage == shown || hud is null || prompt is null)
            return;
        shown = stage;
        var buttons = $"A / Cross / {RideKey}";
        var text = stage switch
        {
            Stage.Near => $"{buttons}: ride the chair lift",
            Stage.Waiting => $"Waiting for a chair… ({buttons} to cancel)",
            Stage.Riding => $"Riding up • {buttons}: jump off • Y / {PhoneKey}: phone",
            Stage.Arrived => "Top of the lift!",
            _ => null,
        };
        if (text is null)
            hud.Hide();
        else
        {
            prompt.Text = text;
            hud.Show();
        }
    }

    public override void OnUnload()
    {
        active = false;
        foreach (var s in subscriptions)
            s.Dispose();
        subscriptions.Clear();
        MainThread.Run(() => ride?.Dispose());
        ride = null;
    }
}
