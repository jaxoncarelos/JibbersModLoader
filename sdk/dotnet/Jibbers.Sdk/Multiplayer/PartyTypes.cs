using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>Where the local player is with parties.</summary>
public enum PartyState
{
    /// <summary>Steam is not available (yet): the game has not started it, or it is not running.</summary>
    Unavailable,
    /// <summary>Not in a party.</summary>
    None,
    /// <summary>Making a new party.</summary>
    Creating,
    /// <summary>Looking up or entering someone else's party.</summary>
    Joining,
    /// <summary>In a party: messages can be sent.</summary>
    InParty,
}

/// <summary>How a message travels.</summary>
public enum Delivery
{
    /// <summary>Arrives once, in order (resent until acknowledged). Use for game events and state changes.</summary>
    Reliable,
    /// <summary>May be lost or arrive out of order, but never waits on a lost packet. Use for frequent updates (positions, timers).</summary>
    Unreliable,
}

/// <summary>A player in the party.</summary>
public sealed record PartyMember(SteamId Id, string Name, bool IsHost, bool IsLocal, IReadOnlyDictionary<string, string> Mods, int PingMs)
{
    /// <summary>Whether this player runs the mod with id <paramref name="modId"/> (any version).</summary>
    public bool HasMod(string modId) => Mods.ContainsKey(modId);
    /// <summary>The version of a mod this player runs, or null.</summary>
    public string? ModVersion(string modId) => Mods.TryGetValue(modId, out var v) ? v : null;
}

/// <summary>An online Steam friend, and the party they are in (if any) that you could join.</summary>
public sealed record FriendParty(SteamId Friend, string Name, bool InParty, int PartySize, bool Locked);

/// <summary>A message a party member sent to your mod.</summary>
public sealed class PartyMessage
{
    internal PartyMessage(PartyMember from, ReadOnlyMemory<byte> data) { From = from; Data = data; }
    /// <summary>Who sent it.</summary>
    public PartyMember From { get; }
    /// <summary>The bytes as sent.</summary>
    public ReadOnlyMemory<byte> Data { get; }
    /// <summary>The bytes as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Data.Span);
    /// <summary>The bytes parsed as JSON, or null if they are not valid JSON.</summary>
    public JsonNode? Json()
    {
        try { return JsonNode.Parse(Data.Span); }
        catch (JsonException) { return null; }
    }
}
