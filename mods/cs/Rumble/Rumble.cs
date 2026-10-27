using Jibbers.Sdk;
using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Event;
using Jibbers.Sdk.Game.Physics;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Rumble;

/// <summary>Mod entry point: measures the rider each frame and drives every controller's vibration motors.</summary>
public sealed class RumbleMod : JibbersMod
{
    public override string Id => "rumble";
    public override string Name => "Rumble";
    public override string Author => "Jaxon";
    public override string Description => "Controller vibration that follows the game's own sounds: landings, rails, crashes and takeoffs, plus the side you're turning to in a hard carve.";

    [Slider("Strength", 1, 0, 2, 0.1, Tooltip = "How strong every vibration is (0 turns it off).")]
    public double Strength { get; private set; }

    [Toggle("Jumps and landings", Key = "landings", Tooltip = "A pop on takeoff and a thump on landing, scaled to how hard you hit the slope.")]
    public bool Landings { get; private set; }

    [Toggle("Crashes", Tooltip = "A hit for every impact when you crash, and when a ski comes off.")]
    public bool Crashes { get; private set; }

    [Toggle("Rails and boxes", Key = "rails", Tooltip = "A clank onto the rail, then a buzz for as long as you slide.")]
    public bool Rails { get; private set; }

    [Toggle("Carving", Tooltip = "In a really hard carve, the side of the controller you're turning to vibrates: left for a left carve.")]
    public bool Carving { get; private set; }

    [Toggle("Snow texture", false, Key = "snow_texture", Tooltip = "A faint constant texture from the snow, growing with speed.")]
    public bool SnowTexture { get; private set; }

    /// <summary>How long a slide trick counts after the game last reported one: the rail buzz stops this soon after.</summary>
    const double SlideWindowSeconds = 0.3;

    readonly RumbleDirector director = new();
    RiderProbe? probe;
    GameAudio? audio;
    ControllerMotors? motors;
    GameRef? follow;
    long lastFrame, lastSlide;
    bool active, replaying, reportedFailure;
    int tricksLogged;
    string? loggedObject;

    RumbleOptions Options => new()
    {
        Strength = (float)Strength,
        Landings = Landings,
        Crashes = Crashes,
        Rails = Rails,
        Carving = Carving,
        SnowTexture = SnowTexture,
    };

    public override void OnLoad()
    {
        probe = new RiderProbe();
        try
        {
            audio = new GameAudio(Log);
        }
        catch (Exception exception)
        {
            Log("Rumble: the game's sounds are unavailable, so everything is measured: " + exception.Message);
        }
        motors = new ControllerMotors(Log);
        TrickPerformed.Subscribe(trick =>
        {
            var (name, details) = (trick.Name ?? "", trick.Details ?? "");
            if (SlideTricks.IsSlide(name, details))
                lastSlide = Stopwatch.GetTimestamp();
            // The first few tricks, so the log shows how the game names slides.
            if (tricksLogged++ < 8)
                Log($"Rumble: trick \"{name}\" ({details}){(SlideTricks.IsSlide(name, details) ? ", a slide" : "")}.");
        });
        EnterReplay.Subscribe(_ => replaying = true);
        ExitReplay.Subscribe(_ => replaying = false);
        active = true;
        Manager.UpdateMethod.Before((_, _) =>
        {
            if (active)
                Frame();
        });
    }

    void Frame()
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = lastFrame == 0 ? 0 : (float)Math.Min(Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds, 0.1);
            lastFrame = now;
            if (Scene.IsPaused || replaying || !Input.GameFocused || FollowCamera() is not { } camera || probe!.Read(camera, seconds) is not { } measured)
            {
                director.Reset();
                audio?.Discard();
                motors!.Set(Motors.Off, seconds);
                return;
            }
            // A grind is the game's rail sound playing. Without it: a slide the game is scoring, or
            // an object under your feet named like a rail or box.
            var recentSlide = lastSlide != 0 && Stopwatch.GetElapsedTime(lastSlide, now).TotalSeconds < SlideWindowSeconds;
            var namedRail = probe.ObjectUnderFeet is { } under && SlideTricks.IsSlide(under, "");
            var frame = measured with { SlideTrick = recentSlide || namedRail, RailSlide = audio?.RailSlide(seconds) };
            var options = Options;
            if (audio is not null)
                foreach (var (name, volume) in audio.Played())
                    if (GameSounds.Classify(name) is { } sound)
                    {
                        director.Hear(sound, volume, options);
                        if (sound is GameSound.Land or GameSound.LandPowder && director.LastImpact > 2.5f)
                            Log($"Rumble: landed at {director.LastImpact:0.0} m/s into the slope.");
                    }
            if (probe.ObjectUnderFeet != loggedObject && probe.ObjectUnderFeet is not null)
                Log($"Rumble: riding on \"{probe.ObjectUnderFeet}\"{(frame.SlideTrick ? ", sliding" : "")}.");
            loggedObject = probe.ObjectUnderFeet;
            var state = director.State;
            motors!.Set(director.Update(frame, seconds, options), seconds);
            if (!director.SoundDriven && director.State == RumbleState.Riding && state == RumbleState.Airborne && director.LastImpact > 2.5f)
                Log($"Rumble: landed at {director.LastImpact:0.0} m/s into the slope.");
            reportedFailure = false;
        }
        catch (Exception exception)
        {
            if (!reportedFailure)
                Log("Rumble paused: " + exception.Message);
            reportedFailure = true;
            director.Reset();
            try { motors?.Stop(); } catch (Exception) { }
        }
    }

    Follow? FollowCamera()
    {
        if (follow is null || Unity.IsNull(follow.Target))
        {
            follow?.Dispose();
            follow = Follow.First() is { } found ? new GameRef(found.Ptr) : null;
        }
        return follow is null ? null : new Follow(follow.Target);
    }

    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (key == "enabled" && !value.GetValue<bool>())
            MainThread.Later(() => motors?.Stop());
    }

    public override JsonNode? OnCommand(string command, JsonObject request) => command != "rumble.info" ? null : new JsonObject
    {
        ["state"] = director.State.ToString(),
        ["speed"] = director.Velocity.Length(),
        ["lastLanding"] = director.LastImpact,
        ["soundDriven"] = director.SoundDriven,
    };

    public override void OnUnload()
    {
        active = false;
        MainThread.Run(() =>
        {
            motors?.Dispose();
            probe?.Dispose();
            audio?.Dispose();
            follow?.Dispose();
        });
    }
}
