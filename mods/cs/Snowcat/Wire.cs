using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Jibbers.Sdk;

namespace Snowcat;

/// <summary>
/// One snowcat, as sent to the other players about twenty times a second: which map, where it is and
/// how it sits, how fast it's going, how far its tracks have run, where its tiller is, whether its
/// blade, tiller and lights are on, where its driver's body is (so players on other servers of the
/// same map can tell it isn't on theirs), and which run of work it's on (for undo). No game calls.
/// </summary>
internal readonly record struct CatPacket(int Map, Vector3 At, float Yaw, float Pitch, float Roll, float Speed, float LeftTread, float RightTread, float Tiller, bool Blade, bool Grooming, bool Lights = false, Vector3 Driver = default, int Run = 0, bool BladeLowered = false)
{
    const uint Magic = 0x43415432; // "CAT2"
    const int Floats = 13;
    public const int Size = 4 + 4 + 4 * Floats + 4 + 1;

    public byte[] Encode()
    {
        var b = new byte[Size];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], Map);
        var f = new[] { At.X, At.Y, At.Z, Yaw, Pitch, Roll, Speed, LeftTread, RightTread, Tiller, Driver.X, Driver.Y, Driver.Z };
        for (var i = 0; i < f.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(s[(8 + 4 * i)..], f[i]);
        BinaryPrimitives.WriteInt32LittleEndian(s[(8 + 4 * Floats)..], Run);
        b[Size - 1] = (byte)((Blade ? 1 : 0) | (Grooming ? 2 : 0) | (Lights ? 4 : 0) | (BladeLowered ? 16 : 0));
        return b;
    }

    /// <summary>A packet back, or null if it isn't one (or carries nonsense).</summary>
    public static CatPacket? Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(s) != Magic) return null;
        var map = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        var f = new float[Floats];
        for (var i = 0; i < f.Length; i++)
        {
            f[i] = BinaryPrimitives.ReadSingleLittleEndian(s[(8 + 4 * i)..]);
            if (!float.IsFinite(f[i]) || MathF.Abs(f[i]) > 1e6f) return null;
        }
        if (MathF.Abs(f[6]) > 50 || f[9] is < 0 or > 1) return null;
        var run = BinaryPrimitives.ReadInt32LittleEndian(s[(8 + 4 * Floats)..]);
        var flags = s[Size - 1];
        return new CatPacket(map, new Vector3(f[0], f[1], f[2]), f[3], f[4], f[5], f[6], f[7], f[8], f[9],
            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, new Vector3(f[10], f[11], f[12]), run, (flags & 16) != 0);
    }

    /// <summary>Part way (<paramref name="t"/>) from one packet to the next, for drawing between updates (yaw the short way round).</summary>
    public static CatPacket Between(CatPacket a, CatPacket b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        float L(float x, float y) => x + (y - x) * t;
        var dyaw = ((b.Yaw - a.Yaw) % 360 + 540) % 360 - 180;
        return b with
        {
            At = a.At + (b.At - a.At) * t,
            Yaw = a.Yaw + dyaw * t,
            Pitch = L(a.Pitch, b.Pitch),
            Roll = L(a.Roll, b.Roll),
            LeftTread = L(a.LeftTread, b.LeftTread),
            RightTread = L(a.RightTread, b.RightTread),
            Tiller = L(a.Tiller, b.Tiller),
        };
    }

    /// <summary>A short, stable number for a map's name (so players on different maps ignore each other).</summary>
    public static int MapId(string map)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in map) h = (h ^ c) * 16777619;
            return h;
        }
    }
}

/// <summary>
/// The blade's swing and tilt (degrees), sent beside the snowcat's own packet: a message of its own, so
/// players on the version before it still see the snowcat (with its blade straight). No game calls.
/// </summary>
internal static class BladePacket
{
    const uint Magic = 0x424C4431; // "BLD1"
    public const int Size = 12;

    public static byte[] Encode(float angle, float tilt)
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), angle);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), tilt);
        return b;
    }

    public static (float Angle, float Tilt)? Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(s) != Magic) return null;
        var (a, t) = (BinaryPrimitives.ReadSingleLittleEndian(s[4..]), BinaryPrimitives.ReadSingleLittleEndian(s[8..]));
        return float.IsFinite(a) && float.IsFinite(t) && MathF.Abs(a) <= 90 && MathF.Abs(t) <= 45 ? (a, t) : null;
    }
}

/// <summary>
/// "I have Snowcat, I'm on this map, my rider is here": sent every couple of seconds to every Snowcat
/// player, with or without a snowcat out. It opens the Steam connection both ways (Steam drops a
/// message from someone you've never written to) and tells each side who's on the same server. No game calls.
/// </summary>
internal static class HelloPacket
{
    const uint Magic = 0x48454C31; // "HEL1"
    public const int Size = 20;

    public static byte[] Encode(int map, Vector3 at)
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), map);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), at.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), at.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), at.Z);
        return b;
    }

    public static (int Map, Vector3 At)? Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(s) != Magic) return null;
        var at = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(s[8..]), BinaryPrimitives.ReadSingleLittleEndian(s[12..]), BinaryPrimitives.ReadSingleLittleEndian(s[16..]));
        return float.IsFinite(at.X) && float.IsFinite(at.Y) && float.IsFinite(at.Z) ? (BinaryPrimitives.ReadInt32LittleEndian(s[4..]), at) : null;
    }
}

/// <summary>"Undo my run": take back the pushing and grooming one player's snowcat did in one run. No game calls.</summary>
internal static class UndoPacket
{
    const uint Magic = 0x554E4430; // "UND0"
    public const int Size = 12;

    public static byte[] Encode(int map, int run)
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), map);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), run);
        return b;
    }

    public static (int Map, int Run)? Decode(ReadOnlySpan<byte> s) =>
        s.Length == Size && BinaryPrimitives.ReadUInt32LittleEndian(s) == Magic
            ? (BinaryPrimitives.ReadInt32LittleEndian(s[4..]), BinaryPrimitives.ReadInt32LittleEndian(s[8..])) : null;
}

/// <summary>
/// "Reset the slopes" (admins only): the map, and the admin password, which every other player's
/// Snowcat checks against the admin password's fingerprint before it puts anything back. No game calls.
/// </summary>
internal static class ResetPacket
{
    const uint Magic = 0x52535432; // "RST2"

    public static byte[] Encode(int map, string password)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        if (pw.Length > 255) throw new ArgumentException("password too long");
        var b = new byte[9 + pw.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), map);
        b[8] = (byte)pw.Length;
        pw.CopyTo(b, 9);
        return b;
    }

    /// <summary>The map it's for and the password it carries, or null if it isn't one.</summary>
    public static (int Map, string Password)? Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length < 9 || BinaryPrimitives.ReadUInt32LittleEndian(s) != Magic || s.Length != 9 + s[8]) return null;
        return (BinaryPrimitives.ReadInt32LittleEndian(s[4..]), Encoding.UTF8.GetString(s[9..]));
    }
}

/// <summary>
/// The admin password, kept only as a fingerprint (a salted SHA-256): nobody can read the password
/// back out of the mod or its file. No game calls.
/// </summary>
internal static class AdminPassword
{
    public static string Fingerprint(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("jibbers-snowcat-admin:" + password))).ToLowerInvariant();

    public static bool IsFingerprint(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    public static bool Matches(string password, string fingerprint) =>
        IsFingerprint(fingerprint) && password.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Fingerprint(password)), Encoding.ASCII.GetBytes(fingerprint.ToLowerInvariant()));
}
