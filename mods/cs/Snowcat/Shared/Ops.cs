using System.Globalization;
using System.Text.Json.Nodes;
using Jibbers.Sdk;

namespace SnowShovel;

/// <summary>What an edit to the snow does.</summary>
internal enum OpKind { Mound, Kicker, Rake, Level }

/// <summary>
/// One edit to the snow, everything needed to make it again on another player's game: a mound
/// (<see cref="Sign"/> -1 digs) of <see cref="Radius"/> and <see cref="Height"/>, a kicker turned by
/// <see cref="Yaw"/>, or a rake stroke (smooth, or level to <see cref="At"/>'s height). No game calls.
/// </summary>
internal sealed record SnowOp(OpKind Kind, Vector3 At, float Yaw = 0, float Radius = 0, float Height = 0, KickerSize Kicker = default, float Sign = 1)
{
    /// <summary>True for edits that add (or take away) a fixed shape, so taking the shape away again undoes them exactly, whatever was done since.</summary>
    public bool Additive => Kind is OpKind.Mound or OpKind.Kicker;

    /// <summary>How far from <see cref="At"/> it reaches.</summary>
    public float Reach => Kind switch
    {
        OpKind.Kicker => MathF.Max(Sculpt.KickerReach(Kicker), Kicker.Width / 2 + 0.7f) + 0.5f,
        _ => Radius + 0.5f,
    };

    /// <summary>The height it adds (metres) at a point in its own frame, for additive edits.</summary>
    public float Shape(float x, float z) => Kind switch
    {
        OpKind.Kicker => Sign * Sculpt.Kicker(x, z, Kicker),
        OpKind.Mound => Sign * Sculpt.Mound(x, z, Radius, Height),
        _ => 0,
    };

    static string N(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    public JsonObject ToJson() => new()
    {
        ["k"] = (int)Kind,
        ["p"] = new JsonArray(N(At.X), N(At.Y), N(At.Z)),
        ["y"] = N(Yaw), ["r"] = N(Radius), ["h"] = N(Height), ["g"] = N(Sign),
        ["kh"] = N(Kicker.Height), ["kw"] = N(Kicker.Width), ["ka"] = N(Kicker.Angle),
    };

    public static SnowOp? FromJson(JsonNode? j)
    {
        try
        {
            if (j is null) return null;
            float F(JsonNode? n) => float.Parse(n!.GetValue<string>(), CultureInfo.InvariantCulture);
            var p = j["p"]!.AsArray();
            var kind = (OpKind)j["k"]!.GetValue<int>();
            if (!Enum.IsDefined(kind)) return null;
            var op = new SnowOp(kind, new Vector3(F(p[0]), F(p[1]), F(p[2])), F(j["y"]), F(j["r"]), F(j["h"]),
                new KickerSize(F(j["kh"]), F(j["kw"]), F(j["ka"])), F(j["g"]));
            // Nothing silly from another player's game: sizes within what the tools make.
            bool Ok(float v, float max) => float.IsFinite(v) && MathF.Abs(v) <= max;
            if (!Ok(op.At.X, 1e5f) || !Ok(op.At.Y, 1e5f) || !Ok(op.At.Z, 1e5f) || !Ok(op.Yaw, 1e4f) || !Ok(op.Radius, 6) || !Ok(op.Height, 4)
                || !Ok(op.Kicker.Height, 4) || !Ok(op.Kicker.Width, 6) || op.Kicker.Angle is < 0 or > 60 || (op.Kind == OpKind.Kicker && op.Kicker.Height <= 0) || op.Sign is not (1 or -1))
                return null;
            return op;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or NullReferenceException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }
}

/// <summary>The volume of snow a mound moves.</summary>
internal static class Volume
{
    /// <summary>A mound of radius <paramref name="r"/> and height <paramref name="h"/>: h·r²·2π(1/4 - 1/π²).</summary>
    public static float Mound(float r, float h) => h * r * r * 2 * MathF.PI * (0.25f - 1 / (MathF.PI * MathF.PI));

    /// <summary>The height a mound of radius <paramref name="r"/> needs to hold <paramref name="volume"/>.</summary>
    public static float MoundHeight(float r, float volume) => volume / Mound(r, 1);
}
