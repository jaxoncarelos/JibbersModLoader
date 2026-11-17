namespace Snowcat;

/// <summary>
/// The snowcat's blade and tiller, on a patch of height samples (metres, row-major) whose cells sit at
/// <c>local(i, j)</c> in the snowcat's own frame (x right, z forward from its middle). The blade
/// cuts what stands above its edge and drops it in a ridge just ahead (so driving on pushes the pile);
/// the tiller smooths behind. No game calls.
/// </summary>
internal static class Plow
{
    /// <summary>The blade: half its width, and how far ahead of the middle its cutting edge is (metres).</summary>
    public const float HalfWidth = 2.3f, Edge = 4.1f;
    /// <summary>How far ahead of the edge the pushed snow piles up, and how deep a strip the blade works (metres).</summary>
    public const float Pile = 1.2f, Strip = 0.6f;
    /// <summary>The tiller behind: half its width and the strip it grooms (metres behind the middle).</summary>
    public const float TillerHalfWidth = 2.2f, TillerFrom = -4.4f, TillerTo = -3.2f;

    /// <summary>How high the pile in front of the blade gets, and the berms off its ends, above the blade's bottom (metres); and the most it cuts in one pass.</summary>
    public const float MaxPile = 0.9f, MaxBerm = 0.7f, MaxCut = 0.25f;
    /// <summary>How far out past the blade's ends the berms reach (metres).</summary>
    public const float Berm = 1.3f;

    /// <summary>
    /// A point in the snowcat's frame, in the blade's own frame once the blade is swung round by
    /// <paramref name="angle"/> degrees (+ to the right) about <paramref name="pivotZ"/> (metres ahead of
    /// the middle): so the blade pushes along where it's pointing.
    /// </summary>
    public static (float X, float Z) BladeFrame(float x, float z, float angle, float pivotZ)
    {
        var a = angle * MathF.PI / 180;
        var (c, s) = (MathF.Cos(a), MathF.Sin(a));
        var dz = z - pivotZ;
        // Turning the blade by +angle (as Drive's yaw turns +z towards +x): undo that turn.
        return (x * c - dz * s, x * s + dz * c + pivotZ);
    }

    /// <summary>The reverse: a point in the blade's frame back in the snowcat's.</summary>
    public static (float X, float Z) FromBlade(float x, float z, float angle, float pivotZ) => BladeFrame(x, z, -angle, pivotZ);

    /// <summary>
    /// One pass of the blade: cells in the strip behind its edge higher than <paramref name="ground"/>
    /// (the blade's bottom, metres, at that spot) lose <paramref name="bite"/> of the excess (at most
    /// <see cref="MaxCut"/>); it goes in a ridge ahead of the edge, no higher than <see cref="MaxPile"/>
    /// above the blade, and what won't fit spills off the blade's ends into berms (no higher than
    /// <see cref="MaxBerm"/>). Anything left after that is snow lost off the sides. Returns the volume cut (m³).
    /// </summary>
    public static float Push(Span<float> cells, int w, int h, Func<int, int, (float X, float Z)> local, Func<float, float, float> ground, float spacing, float bite)
    {
        var area = spacing * spacing;
        var moved = 0f;
        for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                var (x, z) = local(i, j);
                if (MathF.Abs(x) > HalfWidth || z > Edge || z < Edge - Strip) continue;
                ref var c = ref cells[j * w + i];
                var excess = c - ground(x, z);
                if (excess <= 0) continue;
                var cut = MathF.Min(excess * bite, MaxCut);
                c -= cut;
                moved += cut * area;
            }
        if (moved <= 0) return 0;
        // The ridge: highest in its middle, easing off to nothing at its ends and at the blade's corners.
        static float Ridge(float x, float z)
        {
            if (z <= Edge || z >= Edge + Pile || MathF.Abs(x) >= HalfWidth) return 0;
            var along = MathF.Sin(MathF.PI * (z - Edge) / Pile);
            var across = MathF.Cos(MathF.PI / 2 * x / HalfWidth);
            return along * MathF.Sqrt(MathF.Max(across, 0));
        }
        // The berms: off both ends of the blade, from just behind its edge to just ahead of it.
        static float Berms(float x, float z)
        {
            var out_ = MathF.Abs(x) - HalfWidth;
            if (out_ <= 0 || out_ >= Berm || z <= Edge - 1.5f || z >= Edge + 0.6f) return 0;
            return MathF.Sin(MathF.PI * out_ / Berm) * MathF.Sin(MathF.PI * (z - (Edge - 1.5f)) / 2.1f);
        }
        var left = Spread(cells, w, h, local, ground, area, moved, Ridge, MaxPile);
        Spread(cells, w, h, local, ground, area, left, Berms, MaxBerm);
        return moved;
    }

    /// <summary>
    /// Spreads <paramref name="volume"/> (m³) over the cells by <paramref name="weight"/>, no cell rising
    /// past <paramref name="cap"/> above <paramref name="ground"/>. Returns what didn't fit.
    /// </summary>
    static float Spread(Span<float> cells, int w, int h, Func<int, int, (float X, float Z)> local, Func<float, float, float> ground, float area, float volume, Func<float, float, float> weight, float cap)
    {
        // A few rounds: what one round can't place (cells full) goes round again to the cells with room.
        for (var round = 0; round < 4 && volume > 1e-6f; round++)
        {
            var total = 0f;
            for (var j = 0; j < h; j++)
                for (var i = 0; i < w; i++)
                {
                    var (x, z) = local(i, j);
                    var wt = weight(x, z);
                    if (wt > 0 && cells[j * w + i] < ground(x, z) + cap) total += wt;
                }
            if (total <= 0) break;
            var perWeight = volume / (total * area);
            var placed = 0f;
            for (var j = 0; j < h; j++)
                for (var i = 0; i < w; i++)
                {
                    var (x, z) = local(i, j);
                    var wt = weight(x, z);
                    if (wt <= 0) continue;
                    ref var c = ref cells[j * w + i];
                    var room = ground(x, z) + cap - c;
                    if (room <= 0) continue;
                    var add = MathF.Min(wt * perWeight, room);
                    c += add;
                    placed += add * area;
                }
            volume -= placed;
        }
        return MathF.Max(volume, 0);
    }

    /// <summary>The tiller: smooths the strip behind the snowcat towards its neighbours' average (<paramref name="blurred"/>).</summary>
    public static void Groom(Span<float> cells, ReadOnlySpan<float> blurred, int w, int h, Func<int, int, (float X, float Z)> local, float strength)
    {
        for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                var (x, z) = local(i, j);
                if (MathF.Abs(x) > TillerHalfWidth || z < TillerFrom || z > TillerTo) continue;
                ref var c = ref cells[j * w + i];
                c += (blurred[j * w + i] - c) * strength;
            }
    }
}
