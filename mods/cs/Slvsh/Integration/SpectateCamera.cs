using System.Text.RegularExpressions;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Player;
using Jibbers.Sdk.Interop;

namespace Slvsh.Integration;

/// <summary>What the camera is doing for the scoreboard.</summary>
internal enum SpectateStatus { Off, Watching, NotHere }

/// <summary>A rider the camera could watch: its nametag and where its body is.</summary>
internal sealed record RiderCandidate(string Nametag, Vector3 Position);

/// <summary>Which rider in this session is a given party member. Pure, so it is tested without the game.</summary>
internal static class RiderMatch
{
    /// <summary>How far (metres) a rider may be from where the player says they are.</summary>
    public const float Near = 12;

    /// <summary>
    /// The rider whose nametag is the player's Steam name; failing that, the rider nearest to the
    /// position the player reported (their own, sent over Steam), if one is close enough. -1 if none.
    /// </summary>
    public static int Pick(string name, Vector3? reported, IReadOnlyList<RiderCandidate> riders)
    {
        var wanted = Normalize(name);
        if (wanted.Length > 0)
            for (var i = 0; i < riders.Count; i++)
                if (Normalize(riders[i].Nametag) == wanted) return i;
        if (reported is not { } at) return -1;
        var best = -1;
        var bestDistance = Near;
        for (var i = 0; i < riders.Count; i++)
        {
            var d = riders[i].Position - at;
            var distance = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (distance < bestDistance) (best, bestDistance) = (i, distance);
        }
        return best;
    }

    /// <summary>Nametags may carry rich-text tags and padding; compare the bare name.</summary>
    public static string Normalize(string text) => Regex.Replace(text, "<[^>]*>", "").Trim().ToLowerInvariant();
}

/// <summary>
/// Watches another rider with the game's own follow camera, the way the game's Spectator does:
/// the camera's <c>target</c> and <c>targetRagdoll</c> point at their rider instead of yours, and
/// are put back afterwards. Only riders in your session can be watched. Main thread only.
/// </summary>
internal sealed class SpectateCamera
{
    /// <summary>The camera target's path below your rider (e.g. a body part), so theirs is followed the same way.</summary>
    string? targetPath;
    GameRef? originalTarget, originalRagdoll;
    bool active;
    readonly Dictionary<nint, (string Tag, double At)> nametags = new();

    public bool Active => active;

    /// <summary>Where your own rider's body is (what the camera follows), or null outside a map.</summary>
    public Vector3? OwnPosition()
    {
        if (RagdollSpawner.First() is not { } spawner || spawner.Rider is var me && (me.IsNull || Unity.IsNull(me.Ptr))) return null;
        Learn(spawner);
        return Unity.Position(TargetOf(spawner.Rider.Ptr));
    }

    /// <summary>Points the camera at <paramref name="name"/>'s rider (null: back at yours). Call a few times a second.</summary>
    public SpectateStatus Update(string? name, Vector3? reported, double now)
    {
        if (name is null)
        {
            Restore();
            return SpectateStatus.Off;
        }
        if (RagdollSpawner.First() is not { } spawner || spawner.Camera.IsNull || spawner.Rider.IsNull || Unity.IsNull(spawner.Camera.Ptr))
            return SpectateStatus.NotHere;
        Learn(spawner);
        var me = spawner.Rider.Ptr;
        var others = Ragdoll.All().Where(r => r.Ptr != me && !Unity.IsNull(r.Ptr)).ToList();
        var candidates = others.Select(r => new RiderCandidate(Nametag(r.Ptr, now), Unity.Position(TargetOf(r.Ptr)))).ToList();
        var pick = RiderMatch.Pick(name, reported, candidates);
        if (pick < 0)
        {
            Restore();
            return SpectateStatus.NotHere;
        }
        var camera = spawner.Camera;
        if (!active)
        {
            originalTarget = new GameRef(camera.Target.Ptr);
            originalRagdoll = new GameRef(camera.Raw.targetRagdoll.Ptr);
            active = true;
        }
        var target = TargetOf(others[pick].Ptr);
        if (camera.Target.Ptr != target) camera.Target = new Jibbers.Interop.UnityEngine.Transform(target);
        var raw = camera.Raw; // the camera's other target field, by its real name (not in the symbol catalogue)
        if (raw.targetRagdoll.Ptr != others[pick].Ptr) raw.targetRagdoll = others[pick].Raw;
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
            var rider = spawner.Rider.Ptr;
            // Your rider may have respawned while you watched: then follow the new one the same way.
            var target = originalTarget is { } t && !Unity.IsNull(t.Target) ? t.Target : rider != 0 ? TargetOf(rider) : 0;
            var ragdoll = originalRagdoll is { } r && !Unity.IsNull(r.Target) ? r.Target : rider;
            if (target != 0) camera.Target = new Jibbers.Interop.UnityEngine.Transform(target);
            var raw = camera.Raw;
            if (ragdoll != 0) raw.targetRagdoll = new Jibbers.Interop.Game.RagdollController(ragdoll);
        }
        finally
        {
            originalTarget?.Dispose();
            originalRagdoll?.Dispose();
            (originalTarget, originalRagdoll) = (null, null);
        }
    }

    /// <summary>Learns, while the camera still follows you, which part of your rider it targets.</summary>
    void Learn(RagdollSpawner spawner)
    {
        if (active || targetPath is not null || spawner.Camera.IsNull || spawner.Rider.IsNull) return;
        var root = Unity.Transform(spawner.Rider.Ptr);
        var target = spawner.Camera.Target.Ptr;
        var names = new List<string>();
        for (var t = target; t != 0 && !Unity.IsNull(t); t = Unity.Call(t, "get_parent"))
        {
            if (t == root)
            {
                names.Reverse();
                targetPath = string.Join("/", names);
                return;
            }
            names.Add(Unity.Name(t));
        }
        targetPath = ""; // not under the rider: follow the rider itself
    }

    nint TargetOf(nint ragdoll)
    {
        var root = Unity.Transform(ragdoll);
        if (string.IsNullOrEmpty(targetPath)) return root;
        var part = Unity.Call(root, "Find", Il2Cpp.String(targetPath));
        return part != 0 && !Unity.IsNull(part) ? part : root;
    }

    /// <summary>The nametag above a rider (a TextMeshPro text in its character), cached for a few seconds.</summary>
    string Nametag(nint ragdoll, double now)
    {
        if (nametags.TryGetValue(ragdoll, out var cached) && now - cached.At < 3) return cached.Tag;
        if (nametags.Count > 64) nametags.Clear();
        var tag = "";
        try
        {
            var tmp = Unity.Class("TMPro.TMP_Text");
            foreach (var text in Unity.GetComponentsInChildren(CharacterRoot(ragdoll), tmp, includeInactive: true))
            {
                var s = Il2Cpp.ReadString(Unity.Call(text, "get_text")).Trim();
                if (s.Length > 0) { tag = s; break; }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or MissingMethodException) { }
        nametags[ragdoll] = (tag, now);
        return tag;
    }

    /// <summary>The instantiated character a rider belongs to: its topmost "(Clone)" ancestor, else the child of the scene root.</summary>
    static nint CharacterRoot(nint component)
    {
        var t = Unity.Transform(component);
        nint clone = 0, belowRoot = t;
        while (t != 0 && !Unity.IsNull(t))
        {
            if (Unity.Name(t).EndsWith("(Clone)", StringComparison.Ordinal)) clone = t;
            var parent = Unity.Call(t, "get_parent");
            if (parent != 0 && !Unity.IsNull(parent)) belowRoot = t;
            t = parent;
        }
        return clone != 0 ? clone : belowRoot;
    }
}
