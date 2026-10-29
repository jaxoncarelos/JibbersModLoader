using System.Text.RegularExpressions;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Player;
using Jibbers.Sdk.Interop;

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
/// Watches another rider with the game's own follow camera: the camera's <c>target</c> points at
/// the puppet the game draws for them (other players are puppets on your PC, not ragdolls) and is
/// put back afterwards. Only riders in your session can be watched. Main thread only.
/// </summary>
internal sealed class SpectateCamera(Action<string> log)
{
    GameRef? originalTarget;
    bool active;
    ulong explained;

    public bool Active => active;

    /// <summary>Where your own rider's body is (what the camera follows), or null outside a map.</summary>
    public Vector3? OwnPosition()
    {
        if (RagdollSpawner.First() is not { } spawner || spawner.Camera.IsNull || Unity.IsNull(spawner.Camera.Ptr)) return null;
        var target = active && originalTarget is { } t ? t.Target : spawner.Camera.Target.Ptr;
        return target == 0 || Unity.IsNull(target) ? null : Unity.Position(target);
    }

    /// <summary>Points the camera at the player's rider (no player: back at yours). Call a few times a second.</summary>
    public SpectateStatus Update(ulong steamId, string? name, Vector3? reported)
    {
        if (name is null)
        {
            Restore();
            return SpectateStatus.Off;
        }
        if (RagdollSpawner.First() is not { } spawner || spawner.Camera.IsNull || Unity.IsNull(spawner.Camera.Ptr))
            return SpectateStatus.NotHere;
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
        if (!active)
        {
            originalTarget = new GameRef(camera.Target.Ptr);
            active = true;
        }
        var body = riders[pick].Body;
        if (camera.Target.Ptr != body) camera.Target = new Jibbers.Interop.UnityEngine.Transform(body);
        return SpectateStatus.Watching;
    }

    /// <summary>Gives the camera back to your rider. Safe to call when not spectating.</summary>
    public void Restore()
    {
        if (!active) return;
        active = false;
        try
        {
            if (RagdollSpawner.First() is not { } spawner || spawner.Camera.IsNull || Unity.IsNull(spawner.Camera.Ptr)) return;
            var camera = spawner.Camera;
            // Your rider may have respawned while you watched: then follow the new one.
            var target = originalTarget is { } t && !Unity.IsNull(t.Target) ? t.Target
                : !spawner.Rider.IsNull && !Unity.IsNull(spawner.Rider.Ptr) ? Unity.Transform(spawner.Rider.Ptr) : 0;
            if (target != 0) camera.Target = new Jibbers.Interop.UnityEngine.Transform(target);
        }
        finally
        {
            originalTarget?.Dispose();
            originalTarget = null;
        }
    }

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
