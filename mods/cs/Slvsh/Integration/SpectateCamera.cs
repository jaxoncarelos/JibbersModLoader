using System.Text.RegularExpressions;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Player;
using Jibbers.Sdk.Interop;
using Follow = Jibbers.Sdk.Game.Camera.Follow;
using CamState = Jibbers.Sdk.Game.Camera.CamState;

namespace Slvsh.Integration;

/// <summary>What the camera is doing for the scoreboard.</summary>
internal enum SpectateStatus { Off, Watching, NotHere }

/// <summary>A rider the camera could watch: the game's name for them, their account strings, and where they are.</summary>
internal sealed record RiderCandidate(string Name, Vector3 Position, IReadOnlyList<string>? Info = null);

/// <summary>Which rider in this session is a given party member. Pure, so it is tested without the game.</summary>
internal static class RiderMatch
{
    /// <summary>How far (metres) a rider may be from where the player says they are.</summary>
    public const float Near = 12;

    /// <summary>
    /// The rider whose account strings include the player's Steam ID; else whose name is their Steam
    /// name; else the one nearest to the position the player reported (sent over Steam), if close
    /// enough. -1 if none.
    /// </summary>
    public static int Pick(ulong steamId, string name, Vector3? reported, IReadOnlyList<RiderCandidate> riders)
    {
        var id = steamId.ToString();
        for (var i = 0; i < riders.Count; i++)
            if (riders[i].Info?.Contains(id) == true) return i;
        var wanted = Normalize(name);
        if (wanted.Length > 0)
            for (var i = 0; i < riders.Count; i++)
                if (Normalize(riders[i].Name) == wanted) return i;
        if (reported is not { } at) return -1;
        var best = -1;
        var bestDistance = Near;
        for (var i = 0; i < riders.Count; i++)
        {
            var distance = Distance(riders[i].Position, at);
            if (distance < bestDistance) (best, bestDistance) = (i, distance);
        }
        return best;
    }

    public static float Distance(Vector3 a, Vector3 b)
    {
        var d = a - b;
        return MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
    }

    /// <summary>Names may carry rich-text tags and padding; compare the bare name.</summary>
    public static string Normalize(string text) => Regex.Replace(text, "<[^>]*>", "").Trim().ToLowerInvariant();
}

/// <summary>
/// Watches another rider by flying your camera behind the puppet the game draws for them (other
/// players are puppets on your PC, not ragdolls). The game's follow camera only follows your own
/// rider, so it is switched off while you watch and back on afterwards; nothing else about your
/// rider changes. Only riders in your session can be watched. Main thread only.
/// </summary>
internal sealed unsafe class SpectateCamera(Action<string> log)
{
    /// <summary>For the first seconds of each watch, how often to log how far the watched rider moved.</summary>
    const double SampleEvery = 3;
    const int Samples = 4;

    readonly ChaseCamera chase = new();
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    GameRef? follow, view, body;
    int riderId = -1;
    ulong explained, modeExplained;
    bool failed;
    (Vector3 At, Vector3? Reported, double When, int Count) sample;
    Il2CppMethod ray, hitDistance;

    public bool Active => follow is not null;

    /// <summary>Where your own rider's body is (what the follow camera follows), or null outside a map.</summary>
    public Vector3? OwnPosition()
    {
        if (RagdollSpawner.First() is not { } spawner || spawner.Camera.IsNull || Unity.IsNull(spawner.Camera.Ptr)) return null;
        var target = spawner.Camera.Target.Ptr;
        return target == 0 || Unity.IsNull(target) ? null : Unity.Position(target);
    }

    /// <summary>Picks the player's rider to watch (no player: back to yours). Call a few times a second.</summary>
    public SpectateStatus Update(ulong steamId, string? name, Vector3? reported)
    {
        if (name is null)
        {
            Restore();
            failed = false;
            return SpectateStatus.Off;
        }
        // The camera broke while watching this turn: keep your own until the turn is over.
        if (failed) return SpectateStatus.NotHere;
        if (RagdollSpawner.First() is not { } spawner || spawner.Camera.IsNull || Unity.IsNull(spawner.Camera.Ptr))
        {
            Restore();
            return SpectateStatus.NotHere;
        }
        var riders = GamePlayers.Others();
        var candidates = riders.Select(r => new RiderCandidate(r.Name, Unity.Position(r.Body), r.Info)).ToList();
        var pick = RiderMatch.Pick(steamId, name, reported, candidates);
        if (pick < 0)
        {
            Explain(steamId, name, reported, candidates);
            Restore();
            return SpectateStatus.NotHere;
        }
        var camera = spawner.Camera;
        if (!Active)
        {
            // The drone, photo mode and the like own the camera: leave it to them.
            if (camera.Mode != CamState.Follow)
            {
                if (modeExplained != steamId) log($"SLVSH: not watching {name}: the camera is in another mode ({camera.Mode})");
                modeExplained = steamId;
                return SpectateStatus.Off;
            }
            var transform = Unity.Transform(camera.Ptr);
            follow = new GameRef(camera.Ptr);
            view = new GameRef(transform);
            UnityProperties.Set(camera.Ptr, "set_enabled", false);
            chase.Start(candidates[pick].Position, Unity.Position(transform));
        }
        var rider = riders[pick];
        if (body is null || body.Target != rider.Body || riderId != rider.Id)
        {
            body?.Dispose();
            body = new GameRef(rider.Body);
            riderId = rider.Id;
            sample = (candidates[pick].Position, reported, clock.Elapsed.TotalSeconds, 0);
            log($"SLVSH: watching {name}: rider #{rider.Id} '{rider.Name}' [{string.Join(",", rider.Info)}], following '{Unity.Name(rider.Body)}' at {Show(candidates[pick].Position)}"
                + (reported is { } at ? $", {RiderMatch.Distance(candidates[pick].Position, at):F1} m from where they say they are" : ""));
        }
        Sample(name, candidates[pick].Position, reported);
        return SpectateStatus.Watching;
    }

    /// <summary>Moves the camera behind the watched rider. Every frame, on the main thread.</summary>
    public void Frame()
    {
        if (!Active || failed) return;
        try
        {
            if (body is null || view is null || Unity.IsNull(body.Target) || Unity.IsNull(view.Target))
            {
                Restore();
                return;
            }
            chase.Step(Math.Clamp(Unity.DeltaTime, 0, 0.1f), Unity.Position(body.Target));
            var focus = chase.Focus;
            var eye = KeepClear(focus, chase.Eye);
            var rotation = ChaseCamera.Look(focus - eye);
            Unity.Call(view.Target, "SetPositionAndRotation", (nint)(&eye), (nint)(&rotation));
        }
        catch (Exception e) when (e is InvalidOperationException or MissingMethodException or Il2CppException)
        {
            // Once, not every frame: give the camera back until the next pick.
            failed = true;
            log($"SLVSH: stopped watching: {e.Message}");
            Restore();
        }
    }

    /// <summary>Gives the camera back to your rider. Safe to call when not spectating.</summary>
    public void Restore()
    {
        if (!Active) return;
        try
        {
            if (follow is { } f && !Unity.IsNull(f.Target)) UnityProperties.Set(f.Target, "set_enabled", true);
        }
        finally
        {
            follow?.Dispose();
            view?.Dispose();
            body?.Dispose();
            follow = view = body = null;
            riderId = -1;
        }
    }

    /// <summary>
    /// Pulls the camera in front of anything between it and the rider (the slope behind them, a
    /// rail, a wall), so it never ends up inside the ground.
    /// </summary>
    Vector3 KeepClear(Vector3 focus, Vector3 eye)
    {
        var offset = eye - focus;
        var length = offset.Magnitude;
        if (length < 0.01f || follow is null) return eye;
        if (ray.IsNull)
        {
            var physics = Il2Cpp.RequireClass("UnityEngine.PhysicsModule.dll", "UnityEngine.Physics");
            ray = physics.Method("Raycast", "UnityEngine.Vector3", "UnityEngine.Vector3", "UnityEngine.RaycastHit&", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction");
            hitDistance = Il2Cpp.RequireClass("UnityEngine.PhysicsModule.dll", "UnityEngine.RaycastHit").Method("get_distance", 0);
            if (ray.IsNull || hitDistance.IsNull) throw new MissingMethodException("Physics.Raycast");
        }
        var direction = offset * (1 / length);
        var buffer = stackalloc byte[512];
        new Span<byte>(buffer, 512).Clear();
        var mask = new Follow(follow.Target).GroundLayer is var layers and not 0 ? layers : -5;
        var ignoreTriggers = 1;
        var hit = ray.Invoke(0, (nint)(&focus), (nint)(&direction), (nint)buffer, (nint)(&length), (nint)(&mask), (nint)(&ignoreTriggers));
        if (hit == 0 || !Il2Cpp.Unbox<bool>(hit)) return eye;
        var distance = hitDistance.Invoke<float>((nint)buffer);
        return float.IsFinite(distance) ? focus + direction * Math.Max(distance - 0.3f, 0.5f) : eye;
    }

    /// <summary>For the first seconds of a watch, logs how far the rider moved, so a log shows whether the camera had something moving to follow.</summary>
    void Sample(string name, Vector3 at, Vector3? reported)
    {
        var now = clock.Elapsed.TotalSeconds;
        if (sample.Count >= Samples || now - sample.When < SampleEvery) return;
        var theirs = reported is { } r && sample.Reported is { } before ? $", they say they moved {RiderMatch.Distance(r, before):F1} m" : "";
        log($"SLVSH: {name}'s rider moved {RiderMatch.Distance(at, sample.At):F1} m in {now - sample.When:F0} s{theirs}");
        sample = (at, reported, now, sample.Count + 1);
    }

    static string Show(Vector3 v) => $"({v.X:F0}, {v.Y:F0}, {v.Z:F0})";

    /// <summary>Says once per player why they couldn't be found, so a log shows what the game had.</summary>
    void Explain(ulong steamId, string name, Vector3? reported, List<RiderCandidate> candidates)
    {
        if (explained == steamId) return;
        explained = steamId;
        var seen = candidates.Count == 0 ? "no other riders in this session"
            : string.Join("; ", candidates.Select(c => $"'{c.Name}' [{string.Join(",", c.Info ?? Array.Empty<string>())}]" + (reported is { } at ? $" {RiderMatch.Distance(c.Position, at):F0}m away" : "")));
        log($"SLVSH: can't find {name} ({steamId}) to watch: {seen}");
    }
}
