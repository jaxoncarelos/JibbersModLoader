using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Event;
using Jibbers.Sdk.Game.Player;

namespace Slvsh.Integration;

/// <summary>A spawn point: where you appear, which way you face (Euler degrees), and how fast you start.</summary>
internal sealed record SpawnSpot(Vector3 Position, Vector3 Rotation, Vector3 Velocity)
{
    public JsonArray ToJson() => new(new[] { Position, Rotation, Velocity }.SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    /// <summary>Reads a spot; null for anything malformed or absurd (it came over the network).</summary>
    public static SpawnSpot? FromJson(JsonNode? n)
    {
        try
        {
            if (n is not JsonArray { Count: 9 } a) return null;
            var x = a.Select(v => v!.GetValue<float>()).ToArray();
            if (x.Any(f => !float.IsFinite(f) || MathF.Abs(f) > 100000)) return null;
            return new SpawnSpot(new(x[0], x[1], x[2]), new(x[3], x[4], x[5]), new(x[6], x[7], x[8]));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Puts your rider back at a spawn point with the game's own reset (as if you pressed reset). To
/// start at someone else's spawn, your selected spawn point is moved to theirs for your turn and put
/// back afterwards, so respawning during your turn also takes you there. Main thread only.
/// </summary>
internal sealed class SpawnTeleport(Action<string> log)
{
    (GameRef Spawn, SpawnSpot Original)? moved;

    /// <summary>Your selected spawn point (where a reset takes you), or null outside a map.</summary>
    public SpawnSpot? Current()
    {
        if (Selected() is not { } spawn) return null;
        if (moved is { } m && m.Spawn.Target == spawn.Ptr) return m.Original; // yours, not the one borrowed for a turn
        return Read(spawn);
    }

    /// <summary>Resets your rider to your selected spawn point.</summary>
    public void Respawn() => Reset.RaiseEmpty();

    /// <summary>Moves your selected spawn point to <paramref name="spot"/> (until <see cref="Restore"/>) and resets you there.</summary>
    public void RespawnAt(SpawnSpot spot)
    {
        Restore();
        if (Selected() is not { } spawn)
        {
            log("SLVSH: can't move you to the setter's spawn: no spawn point selected");
            return;
        }
        moved = (new GameRef(spawn.Ptr), Read(spawn));
        Write(spawn, spot);
        Respawn();
    }

    /// <summary>Puts your spawn point back where it was. Safe to call any time.</summary>
    public void Restore()
    {
        if (moved is not { } m) return;
        moved = null;
        try
        {
            if (!Unity.IsNull(m.Spawn.Target)) Write(new Jibbers.Sdk.Game.Map.SpawnPoint(m.Spawn.Target), m.Original);
        }
        finally
        {
            m.Spawn.Dispose();
        }
    }

    static Jibbers.Sdk.Game.Map.SpawnPoint? Selected()
    {
        if (RagdollSpawner.First() is not { } spawner) return null;
        var spawn = spawner.SelectedSpawn();
        return spawn.IsNull ? null : spawn;
    }

    static SpawnSpot Read(Jibbers.Sdk.Game.Map.SpawnPoint spawn)
    {
        var raw = spawn.Raw;
        return new SpawnSpot(spawn.Position, raw.rotation, raw.velocity);
    }

    static void Write(Jibbers.Sdk.Game.Map.SpawnPoint spawn, SpawnSpot spot)
    {
        spawn.Position = spot.Position;
        var raw = spawn.Raw;
        raw.rotation = spot.Rotation;
        raw.velocity = spot.Velocity;
    }
}
