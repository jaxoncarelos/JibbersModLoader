using Snowcat;

static class PlowTests
{
    // A patch 8 m wide, from 2 m to 7 m ahead of the snowcat's middle, 0.25 m a cell.
    const float Spacing = 0.25f;
    const int W = 33, H = 21;
    static (float X, float Z) Local(int i, int j) => (-4 + i * Spacing, 2 + j * Spacing);

    [Test]
    static void TheBladeCutsWhatStandsAboveItAndPilesItAhead()
    {
        var cells = new float[W * H];
        // A 0.2 m bump right at the blade edge (less than one pass cuts).
        for (var j = 0; j < H; j++)
            for (var i = 0; i < W; i++)
            {
                var (x, z) = Local(i, j);
                if (MathF.Abs(x) < 1 && z > Plow.Edge - 0.5f && z <= Plow.Edge) cells[j * W + i] = 0.2f;
            }
        var before = cells.Sum();
        var moved = Plow.Push(cells, W, H, Local, (_, _) => 0, Spacing, 1);
        Assert.True(moved > 0.08f, $"moved {moved}");
        // Nothing lost or made: the snow's just moved.
        Assert.True(MathF.Abs(cells.Sum() - before) < 1e-3f, $"{cells.Sum()} vs {before}");
        // The cut's level with the blade; the pile's ahead of the edge.
        for (var j = 0; j < H; j++)
            for (var i = 0; i < W; i++)
            {
                var (x, z) = Local(i, j);
                if (z > Plow.Edge - 0.5f && z <= Plow.Edge && MathF.Abs(x) < 1) Assert.True(MathF.Abs(cells[j * W + i]) < 1e-5f);
                if (cells[j * W + i] > 1e-4f) Assert.True(z > Plow.Edge && z < Plow.Edge + Plow.Pile, $"snow at z {z}");
            }
    }

    [Test]
    static void ItLeavesGroundBelowTheBladeAlone()
    {
        var cells = Enumerable.Repeat(-0.3f, W * H).ToArray();
        Assert.Equal(0f, Plow.Push(cells, W, H, Local, (_, _) => 0, Spacing, 1));
        Assert.True(cells.All(c => c == -0.3f));
    }

    [Test]
    static void TheTillerSmoothsOnlyBehind()
    {
        static (float X, float Z) Behind(int i, int j) => (-4 + i * Spacing, -5 + j * Spacing);
        var cells = new float[W * H];
        cells[10 * W + 16] = 1; // a lump at z -2.5 (outside the tiller), another inside
        cells[5 * W + 16] = 1;  // z -3.75
        var blurred = new float[W * H];
        Plow.Groom(cells, blurred, W, H, Behind, 0.5f);
        Assert.Equal(1f, cells[10 * W + 16]);
        Assert.Equal(0.5f, cells[5 * W + 16]);
    }
}

static class PlowCapTests
{
    const float Spacing = 0.25f;
    const int W = 49, H = 37;
    static (float X, float Z) Local(int i, int j) => (-6 + i * Spacing, 0 + j * Spacing);

    [Test]
    static void PushingAMountainNeverPilesItSkyHigh()
    {
        var cells = new float[W * H];
        // A big wall of snow under the blade, pushed pass after pass.
        for (var pass = 0; pass < 200; pass++)
        {
            for (var j = 0; j < H; j++)
                for (var i = 0; i < W; i++)
                {
                    var (x, z) = Local(i, j);
                    if (MathF.Abs(x) < Plow.HalfWidth && z > Plow.Edge - Plow.Strip && z <= Plow.Edge) cells[j * W + i] = 2;
                }
            Plow.Push(cells, W, H, Local, (_, _) => 0, Spacing, 0.6f);
        }
        var highest = 0f;
        for (var j = 0; j < H; j++)
            for (var i = 0; i < W; i++)
            {
                var (x, z) = Local(i, j);
                if (z > Plow.Edge) highest = MathF.Max(highest, cells[j * W + i]);
            }
        Assert.True(highest <= Plow.MaxPile + 1e-4f, $"pile {highest} m high");
        // And the berms off the ends got the overflow.
        var berm = 0f;
        for (var j = 0; j < H; j++)
            for (var i = 0; i < W; i++)
            {
                var (x, _) = Local(i, j);
                if (MathF.Abs(x) > Plow.HalfWidth) berm = MathF.Max(berm, cells[j * W + i]);
            }
        Assert.True(berm > 0.3f && berm <= Plow.MaxBerm + 1e-4f, $"berm {berm} m high");
    }
}
