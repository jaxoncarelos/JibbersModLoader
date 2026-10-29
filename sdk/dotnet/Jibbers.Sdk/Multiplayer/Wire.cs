using System.Text;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>
/// The SDK's message framing on its one Steam channel: <c>'J' 'B' version kind idLength id payload</c>.
/// Kind 0 carries a mod's message (id = mod id); kind 1 is the SDK's own (id = "party").
/// </summary>
internal static class Wire
{
    public const byte Version = 1;
    public const byte KindMod = 0, KindControl = 1;
    /// <summary>Largest payload a mod may send in one message.</summary>
    public const int MaxPayload = 64 * 1024;
    const int Header = 5;

    public static byte[] Frame(byte kind, string id, ReadOnlySpan<byte> payload)
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        if (idBytes.Length is 0 or > 64) throw new ArgumentException("message id must be 1-64 bytes", nameof(id));
        if (payload.Length > MaxPayload) throw new ArgumentException($"messages are limited to {MaxPayload / 1024} KB", nameof(payload));
        var frame = new byte[Header + idBytes.Length + payload.Length];
        frame[0] = (byte)'J';
        frame[1] = (byte)'B';
        frame[2] = Version;
        frame[3] = kind;
        frame[4] = (byte)idBytes.Length;
        idBytes.CopyTo(frame, Header);
        payload.CopyTo(frame.AsSpan(Header + idBytes.Length));
        return frame;
    }

    /// <summary>Splits a frame; false for anything that is not a well-formed frame of this version.</summary>
    public static bool TryParse(byte[] frame, out byte kind, out string id, out ReadOnlyMemory<byte> payload)
    {
        kind = 0;
        id = "";
        payload = default;
        if (frame.Length < Header || frame[0] != 'J' || frame[1] != 'B' || frame[2] != Version) return false;
        int idLength = frame[4];
        if (idLength == 0 || idLength > 64 || frame.Length < Header + idLength || frame.Length - Header - idLength > MaxPayload) return false;
        try { id = new UTF8Encoding(false, true).GetString(frame, Header, idLength); }
        catch (DecoderFallbackException) { return false; }
        kind = frame[3];
        payload = frame.AsMemory(Header + idLength);
        return true;
    }
}
