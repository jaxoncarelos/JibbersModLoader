using System.Diagnostics;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace SnowShovel.Integration;

/// <summary>
/// The snow you've built on this map, as the map's own terrain: a clump or a kicker raises the
/// terrain's heightmap (as the game's curve inserts shape it), so it looks, textures and rides exactly
/// like the rest of the slope. Each build is an undo step holding the heights it changed. MuJoCo's copy
/// of the terrain is refreshed <see cref="RebuildAfter"/> after the last change. Main thread only.
/// </summary>
internal sealed unsafe class SnowWorld : IDisposable
{
    const string MuJoCo = "Mujoco.Runtime.dll";
    public static readonly TimeSpan RebuildAfter = TimeSpan.FromSeconds(1);
    readonly Action<string> log;
    GameRef? material;
    DateTime? rebuildAt;

    public SnowWorld(Action<string> log) => this.log = log;

    static Il2CppClass Class(string name) => Il2Cpp.RequireClass(MuJoCo, "Mujoco." + name);

    /// <summary>A shape type's number from MuJoCo's own enum (Sphere, Capsule, ..., Box, Ellipsoid, ...).</summary>
    static int ShapeType(string name)
    {
        foreach (var nested in Class("MjShapeComponent").NestedTypes)
            if (nested.IsEnum && !nested.Field(name).IsNull)
                return nested.Field(name).Get<int>(0);
        return name switch { "Box" => 3, "Ellipsoid" => 4, _ => throw new MissingMemberException("ShapeTypes", name) };
    }

    /// <summary>
    /// A material that draws on ordinary shapes and looks like snow: a copy of a lit (URP Lit) material
    /// the map already uses, preferring one named snow, tinted white. Not the map's own snow material
    /// as it is: that can be a terrain-only shader, which draws nothing on a shape.
    /// </summary>
    public nint SnowMaterial(nint fallbackRenderer)
    {
        if (material is not null && !Unity.IsNull(material.Target)) return material.Target;
        // Unity's own URP Lit (custom map shaders can draw nothing on a mesh of ours): from every loaded
        // material, preferring one named snow.
        nint best = 0;
        var bestScore = -1;
        var all = Unity.CallStatic(Unity.Class("UnityEngine.Resources"), "FindObjectsOfTypeAll", Unity.Class("UnityEngine.Material").SystemType);
        foreach (var m in Il2Cpp.ArrayObjects(all))
        {
            if (m == 0 || Unity.IsNull(m)) continue;
            var shader = Unity.Call(m, "get_shader");
            if (shader == 0 || Unity.IsNull(shader) || Unity.Name(shader) != "Universal Render Pipeline/Lit") continue;
            var name = Unity.Name(m).ToLowerInvariant();
            var score = name.Contains("snow") && !new[] { "cannon", "gun", "machine", "flake", "fx" }.Any(name.Contains) ? 2 : 1;
            if (score > bestScore) (best, bestScore) = (m, score);
        }
        if (best == 0)
        {
            var shader = Unity.CallStatic(Unity.Class("UnityEngine.Shader"), "Find", Il2Cpp.String("Universal Render Pipeline/Lit"));
            if (shader != 0 && !Unity.IsNull(shader))
            {
                var k = Unity.Class("UnityEngine.Material");
                var fresh = k.New();
                k.Method(".ctor", "UnityEngine.Shader").Invoke(fresh, shader);
                best = fresh;
            }
        }
        nint made;
        if (best != 0)
        {
            var k = Unity.Class("UnityEngine.Material");
            made = k.New();
            k.Method(".ctor", "UnityEngine.Material").Invoke(made, best);
            log($"snow is a white copy of the map's '{Unity.Name(best)}' ({Unity.Name(Unity.Call(best, "get_shader"))})");
        }
        else
        {
            // Last: a copy of what a plain Unity cube is drawn with.
            var cube = 3;
            var go = Unity.Class("UnityEngine.GameObject").Method("CreatePrimitive", 1).Invoke(0, (nint)(&cube));
            var shared = Unity.Call(Unity.GetComponent(go, Unity.Class("UnityEngine.MeshRenderer")), "get_sharedMaterial");
            var k = Unity.Class("UnityEngine.Material");
            made = k.New();
            k.Method(".ctor", "UnityEngine.Material").Invoke(made, shared);
            Unity.Destroy(go);
            log($"snow is a copy of Unity's default material ({Unity.Name(Unity.Call(made, "get_shader"))}): no URP Lit material loaded");
        }
        var k2 = Il2Cpp.ClassOf(made);
        bool Has(string prop) => Unity.Call<bool>(made, "HasProperty", Il2Cpp.String(prop));
        var white = new Color(1, 1, 1, 1);
        foreach (var prop in new[] { "_BaseColor", "_Color" })
            if (Has(prop)) k2.Method("SetColor", "System.String", "UnityEngine.Color").Invoke(made, Il2Cpp.String(prop), (nint)(&white));
        // Matte, like snow (a shiny white reads grey).
        foreach (var (prop, value) in new[] { ("_Smoothness", 0.08f), ("_Glossiness", 0.08f), ("_Metallic", 0f), ("_SpecularHighlights", 0f) })
            if (Has(prop)) { var v = value; k2.Method("SetFloat", "System.String", "System.Single").Invoke(made, Il2Cpp.String(prop), (nint)(&v)); }
        // Kept now, so a failure below can't make a new material every frame.
        material = new GameRef(made);
        // The terrain's own snow texture (else none: plain white, not the original's sign or bark).
        (nint texture, string layerName) = (0, "");
        try { (texture, layerName) = TerrainSnow(); }
        catch (Exception e) when (e is MissingMethodException or Il2CppException or InvalidOperationException) { log($"no snow texture: {e.Message}"); }
        foreach (var prop in new[] { "_BaseMap", "_MainTex" })
            if (Has(prop))
            {
                k2.Method("SetTexture", "System.String", "UnityEngine.Texture").Invoke(made, Il2Cpp.String(prop), texture);
                var tiling = new Vector2(1.5f, 1.5f);
                var scale = k2.Method("SetTextureScale", "System.String", "UnityEngine.Vector2");
                if (!scale.IsNull && texture != 0) scale.Invoke(made, Il2Cpp.String(prop), (nint)(&tiling));
            }
        if (texture != 0) log($"snow textured like '{layerName}'");
        return made;
    }

    /// <summary>
    /// The terrain's snow texture: its layer named snow, else its first layer (where this build kept
    /// <c>TerrainData.terrainLayers</c>); else a loaded colour texture named snow (not a normal map,
    /// mask or particle); or 0.
    /// </summary>
    static (nint Texture, string Name) TerrainSnow()
    {
        var layersGetter = Unity.Class("UnityEngine.TerrainData").Method("get_terrainLayers", 0);
        if (layersGetter.IsNull)
        {
            var resources = Unity.Class("UnityEngine.Resources");
            var all = Unity.CallStatic(resources, "FindObjectsOfTypeAll", Unity.Class("UnityEngine.Texture2D").SystemType);
            nint best = 0;
            var bestName = "";
            foreach (var tex in Il2Cpp.ArrayObjects(all))
            {
                if (tex == 0 || Unity.IsNull(tex)) continue;
                var name = Unity.Name(tex);
                var lower = name.ToLowerInvariant();
                if (!lower.Contains("snow") || new[] { "normal", "_n", "nrm", "mask", "height", "rough", "particle", "flake", "fx", "cannon", "ao", "spec" }.Any(lower.Contains)) continue;
                // The biggest one is most likely the ground's albedo.
                if (best == 0 || Unity.Call<int>(tex, "get_width") > Unity.Call<int>(best, "get_width")) (best, bestName) = (tex, name);
            }
            return (best, bestName);
        }
        foreach (var t in Il2Cpp.ArrayObjects(Unity.CallStatic(Unity.Class("UnityEngine.Terrain"), "get_activeTerrains")))
        {
            if (Unity.IsNull(t)) continue;
            var data = Unity.Call(t, "get_terrainData");
            if (data == 0) continue;
            var layers = Il2Cpp.ArrayObjects(layersGetter.Invoke(data)).Where(l => l != 0 && !Unity.IsNull(l)).ToList();
            if (layers.Count == 0) continue;
            var pick = layers.FirstOrDefault(l => Unity.Name(l).Contains("snow", StringComparison.OrdinalIgnoreCase));
            if (pick == 0) pick = layers[0];
            var tex = Unity.Call(pick, "get_diffuseTexture");
            if (tex != 0 && !Unity.IsNull(tex)) return (tex, Unity.Name(pick));
        }
        return (0, "");
    }

    /// <summary>The heights an edit changed on one terrain, as they were before.</summary>
    sealed record Patch(GameRef Terrain, GameRef Data, int X, int Y, int W, int H, float[] Before);

    /// <summary>One edit and (for a rake stroke, which can't be taken away by shape) the heights it changed.</summary>
    sealed record Part(SnowOp Op, List<Patch> Patches);

    /// <summary>An undo step: a whole build, a whole rake hold or a whole shovelful, by one player.</summary>
    sealed record Step(string Owner, long Id, List<Part> Parts);

    readonly List<Step> steps = new();
    readonly Dictionary<nint, (GameRef Terrain, GameRef Data, int X0, int Y0, int X1, int Y1)> dirty = new();
    bool saidSpacing;

    /// <summary>How long after the last change the physics gets the new ground.</summary>
    public TimeSpan RefreshAfter { get; set; } = RebuildAfter;

    static Il2CppClass TerrainData => Unity.Class("UnityEngine.TerrainData");

    static nint[] Terrains() => Il2Cpp.ArrayObjects(Unity.CallStatic(Unity.Class("UnityEngine.Terrain"), "get_activeTerrains"));

    /// <summary>The distance between the terrain's height samples under a point (metres), or null off every terrain.</summary>
    public static float? Spacing(Vector3 at)
    {
        foreach (var t in Terrains())
        {
            if (t == 0 || Unity.IsNull(t)) continue;
            var data = Unity.Call(t, "get_terrainData");
            if (data == 0 || Unity.IsNull(data)) continue;
            var origin = Unity.Position(t);
            var size = Unity.Call<Vector3>(data, "get_size");
            if (at.X < origin.X || at.Z < origin.Z || at.X > origin.X + size.X || at.Z > origin.Z + size.Z) continue;
            var res = Unity.Call<int>(data, "get_heightmapResolution");
            return res > 1 ? size.X / (res - 1) : null;
        }
        return null;
    }

    /// <summary>Which map this is, as far as snow goes: its terrains' names and sizes (players on different maps ignore each other's snow).</summary>
    public static string Map()
    {
        var names = new List<string>();
        foreach (var t in Terrains())
        {
            if (t == 0 || Unity.IsNull(t)) continue;
            var data = Unity.Call(t, "get_terrainData");
            var size = data == 0 ? default : Unity.Call<Vector3>(data, "get_size");
            var at = Unity.Position(t);
            names.Add($"{Unity.Name(t)}@{at.X:0},{at.Z:0}/{size.X:0}x{size.Z:0}");
        }
        names.Sort(StringComparer.Ordinal);
        return string.Join(";", names);
    }

    /// <summary>
    /// What an edit does to the heights it covers: <c>cells</c> (row-major, z rows of x, in metres above
    /// the terrain's base), <c>w</c> by <c>h</c>, and where cell (i, j) is relative to the edit's centre
    /// in its own turned frame (x right, z forward).
    /// </summary>
    public delegate void Change(Span<float> cells, int w, int h, Func<int, int, (float X, float Z)> local, float spacing, float baseY);

    /// <summary>
    /// Makes an edit to the snow (yours, <paramref name="owner"/> "", or another player's), as part of
    /// the undo step <paramref name="stepId"/> (a new step if that player has none by that id yet).
    /// False if it touched no terrain.
    /// </summary>
    public bool Apply(SnowOp op, string owner, long stepId)
    {
        var patches = Do(op, 1);
        if (patches.Count == 0) return false;
        if (op.Additive)
        {
            // Undone by taking the shape away again: the old heights aren't needed.
            Dispose(patches);
            patches.Clear();
        }
        var step = steps.FindLast(s => s.Owner == owner && s.Id == stepId);
        if (step is null) steps.Add(step = new Step(owner, stepId, new List<Part>()));
        step.Parts.Add(new Part(op, patches));
        return true;
    }

    /// <summary>
    /// Changes the snow without an undo step (a machine at work, such as the snowcat's blade):
    /// <paramref name="change"/> over every terrain within <paramref name="reach"/> of <paramref name="at"/>,
    /// in a frame turned by <paramref name="yaw"/>. False if it touched nothing.
    /// </summary>
    public bool Reshape(Vector3 at, float yaw, float reach, Change change, string? owner = null, int run = 0)
    {
        if (owner is not null)
        {
            if (!runs.TryGetValue((owner, run), out var tiles)) runs[(owner, run)] = tiles = new();
            recording = tiles;
        }
        try
        {
            var made = Edit(at, yaw, reach, change);
            Dispose(made);
            return made.Count > 0;
        }
        finally { recording = null; }
    }

    /// <summary>
    /// A machine's runs of work (one player's, one stretch with the blade or tiller down): the ground
    /// as it was before the run first touched it, a tile at a time, so the run can be undone.
    /// </summary>
    readonly Dictionary<(string Owner, int Run), Dictionary<(nint Data, int X, int Y), Saved>> runs = new();
    Dictionary<(nint Data, int X, int Y), Saved>? recording;
    sealed record Saved(GameRef Terrain, GameRef Data, int W, int H, float[] Heights);

    /// <summary>
    /// Undoes one run of a machine's work: the ground it touched goes back to how it was before the run
    /// (anything else built on those same spots since goes too). Returns how many tiles it put back.
    /// </summary>
    public int UndoRun(string owner, int run)
    {
        if (!runs.Remove((owner, run), out var tiles)) return 0;
        var put = 0;
        foreach (var ((_, tx, ty), o) in tiles)
        {
            if (!Unity.IsNull(o.Data.Target))
            {
                int x = tx * Tile, y = ty * Tile, w = o.W, h = o.H;
                var heights = TerrainData.Method("GetHeights", 4).Invoke(o.Data.Target, (nint)(&x), (nint)(&y), (nint)(&w), (nint)(&h));
                if (heights != 0 && Il2Cpp.ArrayLength(heights) >= w * h)
                {
                    o.Heights.CopyTo(new Span<float>((float*)Il2Cpp.ArrayData(heights), w * h));
                    SetHeights(o.Data.Target, x, y, heights);
                    MarkDirty(new Patch(o.Terrain, o.Data, x, y, w, h, o.Heights));
                    put++;
                }
            }
            o.Terrain.Dispose();
            o.Data.Dispose();
        }
        if (put > 0) rebuildAt = DateTime.UtcNow + RefreshAfter;
        return put;
    }

    /// <summary>A player's runs that can still be undone, oldest first.</summary>
    public List<int> RunsOf(string owner) => runs.Keys.Where(k => k.Owner == owner).Select(k => k.Run).OrderBy(r => r).ToList();

    void ForgetRuns()
    {
        foreach (var tiles in runs.Values) foreach (var o in tiles.Values) { o.Terrain.Dispose(); o.Data.Dispose(); }
        runs.Clear();
    }

    static void SaveTiles(Dictionary<(nint Data, int X, int Y), Saved> into, nint terrain, nint data, int res, int x0, int y0, int x1, int y1)
    {
        for (var ty = y0 / Tile; ty <= y1 / Tile; ty++)
            for (var tx = x0 / Tile; tx <= x1 / Tile; tx++)
            {
                if (into.ContainsKey((data, tx, ty))) continue;
                int x = tx * Tile, y = ty * Tile, w = Math.Min(Tile, res - x), h = Math.Min(Tile, res - y);
                if (w <= 0 || h <= 0) continue;
                var heights = TerrainData.Method("GetHeights", 4).Invoke(data, (nint)(&x), (nint)(&y), (nint)(&w), (nint)(&h));
                if (heights == 0 || Il2Cpp.ArrayLength(heights) < w * h) continue;
                into[(data, tx, ty)] = new Saved(new GameRef(terrain), new GameRef(data), w, h, new Span<float>((float*)Il2Cpp.ArrayData(heights), w * h).ToArray());
            }
    }

    List<Patch> Do(SnowOp op, float sign) => op.Kind switch
    {
        OpKind.Rake or OpKind.Level => Edit(op.At, 0, op.Reach, (cells, w, h, local, spacing, baseY) =>
        {
            var level = op.Kind == OpKind.Level;
            var target = op.At.Y - baseY;
            var smooth = level ? null : SnowShovel.Sculpt.Blur(cells, w, h, Math.Max(1, (int)MathF.Round(0.5f / spacing)));
            for (var j = 0; j < h; j++)
                for (var i = 0; i < w; i++)
                {
                    var (x, z) = local(i, j);
                    var weight = SnowShovel.Sculpt.RakeWeight(MathF.Sqrt(x * x + z * z), op.Radius) * (level ? 0.35f : 0.6f);
                    if (weight <= 0) continue;
                    ref var c = ref cells[j * w + i];
                    c += ((level ? target : smooth![j * w + i]) - c) * weight;
                }
        }),
        _ => Edit(op.At, op.Yaw, op.Reach, (cells, w, h, local, _, _) =>
        {
            for (var j = 0; j < h; j++)
                for (var i = 0; i < w; i++)
                {
                    var (x, z) = local(i, j);
                    cells[j * w + i] += sign * op.Shape(x, z);
                }
        }),
    };

    /// <summary>The ground as the map had it, saved a tile (<see cref="Tile"/> samples square) at a time before its first change.</summary>
    readonly Dictionary<(nint Data, int X, int Y), (GameRef Terrain, GameRef Data, int W, int H, float[] Heights)> originals = new();
    const int Tile = 64;

    void SaveOriginal(nint terrain, nint data, int res, int x0, int y0, int x1, int y1)
    {
        for (var ty = y0 / Tile; ty <= y1 / Tile; ty++)
            for (var tx = x0 / Tile; tx <= x1 / Tile; tx++)
            {
                if (originals.ContainsKey((data, tx, ty))) continue;
                int x = tx * Tile, y = ty * Tile, w = Math.Min(Tile, res - x), h = Math.Min(Tile, res - y);
                if (w <= 0 || h <= 0) continue;
                var heights = TerrainData.Method("GetHeights", 4).Invoke(data, (nint)(&x), (nint)(&y), (nint)(&w), (nint)(&h));
                if (heights == 0 || Il2Cpp.ArrayLength(heights) < w * h) continue;
                var copy = new Span<float>((float*)Il2Cpp.ArrayData(heights), w * h).ToArray();
                originals[(data, tx, ty)] = (new GameRef(terrain), new GameRef(data), w, h, copy);
            }
    }

    /// <summary>
    /// Puts every bit of ground this has changed back as the map had it (everyone's snow, every machine's
    /// pushing and grooming), and forgets the undo steps. Returns how many tiles it put back.
    /// </summary>
    public int ResetAll()
    {
        var put = 0;
        foreach (var ((_, tx, ty), o) in originals)
        {
            if (Unity.IsNull(o.Data.Target)) continue;
            int x = tx * Tile, y = ty * Tile, w = o.W, h = o.H;
            var heights = TerrainData.Method("GetHeights", 4).Invoke(o.Data.Target, (nint)(&x), (nint)(&y), (nint)(&w), (nint)(&h));
            if (heights == 0 || Il2Cpp.ArrayLength(heights) < w * h) continue;
            o.Heights.CopyTo(new Span<float>((float*)Il2Cpp.ArrayData(heights), w * h));
            SetHeights(o.Data.Target, x, y, heights);
            MarkDirty(new Patch(o.Terrain, o.Data, x, y, w, h, o.Heights));
            put++;
        }
        foreach (var o in originals.Values) { o.Terrain.Dispose(); o.Data.Dispose(); }
        originals.Clear();
        ForgetRuns();
        foreach (var st in steps) foreach (var part in st.Parts) Dispose(part.Patches);
        steps.Clear();
        // A reset touches a lot: the whole ground in one go.
        rebuildAt = DateTime.UtcNow;
        Refresh(force: true, wholeGround: true);
        return put;
    }

    // Terrains with no physics (a map's far-off background): left alone, nobody rides them.
    readonly Dictionary<nint, bool> solid = new();

    bool Solid(nint terrain)
    {
        if (solid.TryGetValue(terrain, out var s)) return s;
        s = HeightField(terrain) != 0;
        if (!s) log($"leaving terrain '{Unity.Name(terrain)}' alone: it has no physics (scenery)");
        return solid[terrain] = s;
    }

    List<Patch> Edit(Vector3 at, float yaw, float reach, Change change)
    {
        var made = new List<Patch>();
        foreach (var t in Terrains())
        {
            if (t == 0 || Unity.IsNull(t) || !Solid(t)) continue;
            var data = Unity.Call(t, "get_terrainData");
            if (data == 0 || Unity.IsNull(data)) continue;
            var origin = Unity.Position(t);
            var size = Unity.Call<Vector3>(data, "get_size");
            var res = Unity.Call<int>(data, "get_heightmapResolution");
            if (res < 2 || size.Y <= 0) continue;
            float sx = size.X / (res - 1), sz = size.Z / (res - 1);
            if (!saidSpacing)
            {
                saidSpacing = true;
                log($"terrain heightmap: {res}x{res} over {size.X:0}x{size.Z:0} m ({sx:0.00} m a sample), {size.Y:0} m tall");
            }
            int Clamp(float v) => Math.Clamp((int)v, 0, res - 1);
            int x0 = Clamp(MathF.Floor((at.X - reach - origin.X) / sx)), x1 = Clamp(MathF.Ceiling((at.X + reach - origin.X) / sx));
            int y0 = Clamp(MathF.Floor((at.Z - reach - origin.Z) / sz)), y1 = Clamp(MathF.Ceiling((at.Z + reach - origin.Z) / sz));
            if (x1 <= x0 || y1 <= y0) continue;
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            var heights = TerrainData.Method("GetHeights", 4).Invoke(data, (nint)(&x0), (nint)(&y0), (nint)(&w), (nint)(&h));
            if (heights == 0 || Il2Cpp.ArrayLength(heights) < w * h) throw new InvalidOperationException("the terrain gave back no heights");
            var cells = new Span<float>((float*)Il2Cpp.ArrayData(heights), w * h);
            var before = cells.ToArray();
            // Work in metres; back to the terrain's 0..1 after.
            var metres = new float[w * h];
            for (var k = 0; k < metres.Length; k++) metres[k] = before[k] * size.Y;
            var (ox, oz, ax, az, bx, bz) = (origin.X, origin.Z, at.X, at.Z, x0, y0);
            var turn = -yaw;
            change(metres, w, h, (i, j) =>
            {
                var l = Shapes.Yaw(new Vector3(ox + (bx + i) * sx - ax, 0, oz + (bz + j) * sz - az), turn);
                return (l.X, l.Z);
            }, sx, origin.Y);
            var changed = false;
            for (var k = 0; k < metres.Length; k++)
            {
                var v = Math.Clamp(metres[k] / size.Y, 0, 1);
                if (MathF.Abs(v - before[k]) > 1e-7f) changed = true;
                cells[k] = v;
            }
            if (!changed) continue;
            SaveOriginal(t, data, res, x0, y0, x1, y1);
            if (recording is not null) SaveTiles(recording, t, data, res, x0, y0, x1, y1);
            SetHeights(data, x0, y0, heights);
            var patch = new Patch(new GameRef(t), new GameRef(data), x0, y0, w, h, before);
            made.Add(patch);
            MarkDirty(patch);
        }
        if (made.Count > 0) rebuildAt = DateTime.UtcNow + RefreshAfter;
        return made;
    }

    /// <summary>
    /// Writes heights into the terrain. Unity's delayed write when it has one (the terrain's level-of-detail
    /// is worked out once, at the next physics update, not on every little change), else the plain one.
    /// </summary>
    static void SetHeights(nint data, int x, int y, nint heights)
    {
        setHeights ??= TerrainData.Methods.FirstOrDefault(m => m.Name == "SetHeightsDelayLOD" && m.ParameterCount == 3) is var d && !d.IsNull
            ? d : TerrainData.Methods.FirstOrDefault(m => m.Name == "SetHeights" && m.ParameterCount == 3);
        if (setHeights.Value.IsNull) throw new MissingMethodException("TerrainData", "SetHeights");
        setHeights.Value.Invoke(data, (nint)(&x), (nint)(&y), heights);
    }
    static Il2CppMethod? setHeights;

    void MarkDirty(Patch p)
    {
        var key = p.Data.Target;
        var (x1, y1) = (p.X + p.W - 1, p.Y + p.H - 1);
        dirty[key] = dirty.TryGetValue(key, out var d)
            ? (d.Terrain, d.Data, Math.Min(d.X0, p.X), Math.Min(d.Y0, p.Y), Math.Max(d.X1, x1), Math.Max(d.Y1, y1))
            : (new GameRef(p.Terrain.Target), new GameRef(p.Data.Target), p.X, p.Y, x1, y1);
    }

    /// <summary>Puts the heights a rake stroke changed back as they were.</summary>
    void Restore(List<Patch> made)
    {
        for (var k = made.Count - 1; k >= 0; k--)
        {
            var p = made[k];
            if (Unity.IsNull(p.Data.Target)) continue;
            var (x, y, w, h) = (p.X, p.Y, p.W, p.H);
            var heights = TerrainData.Method("GetHeights", 4).Invoke(p.Data.Target, (nint)(&x), (nint)(&y), (nint)(&w), (nint)(&h));
            if (heights == 0 || Il2Cpp.ArrayLength(heights) < w * h) continue;
            p.Before.CopyTo(new Span<float>((float*)Il2Cpp.ArrayData(heights), w * h));
            SetHeights(p.Data.Target, x, y, heights);
            MarkDirty(p);
        }
        rebuildAt = DateTime.UtcNow + RefreshAfter;
    }

    public bool Pending => rebuildAt is not null;

    /// <summary>Every undo step on this map, everyone's.</summary>
    public int Count => steps.Count;
    /// <summary>One player's undo steps ("" for yours).</summary>
    public int CountOf(string owner) => steps.Count(s => s.Owner == owner);

    /// <summary>
    /// Undoes a player's last step (or the one with <paramref name="id"/>): shapes are taken away again,
    /// rake strokes put back. Returns the step's id, or null if they have none.
    /// </summary>
    public long? Undo(string owner, long? id = null)
    {
        var i = steps.FindLastIndex(s => s.Owner == owner && (id is null || s.Id == id));
        if (i < 0) return null;
        var step = steps[i];
        steps.RemoveAt(i);
        for (var k = step.Parts.Count - 1; k >= 0; k--)
        {
            var part = step.Parts[k];
            if (part.Op.Additive) Dispose(Do(part.Op, -1));
            else
            {
                Restore(part.Patches);
                Dispose(part.Patches);
            }
        }
        return step.Id;
    }

    /// <summary>Undoes every step of a player's. Returns their ids.</summary>
    public List<long> Clear(string owner)
    {
        var ids = new List<long>();
        while (Undo(owner) is { } id) ids.Add(id);
        return ids;
    }

    static void Dispose(List<Patch> made)
    {
        foreach (var p in made) { p.Terrain.Dispose(); p.Data.Dispose(); }
    }

    /// <summary>A new map: the snow went with the old one's terrain.</summary>
    public void Forget()
    {
        foreach (var s in steps) foreach (var p in s.Parts) Dispose(p.Patches);
        steps.Clear();
        solid.Clear();
        foreach (var o in originals.Values) { o.Terrain.Dispose(); o.Data.Dispose(); }
        originals.Clear();
        ForgetRuns();
        foreach (var (_, d) in dirty) { d.Terrain.Dispose(); d.Data.Dispose(); }
        dirty.Clear();
        material?.Dispose();
        material = null;
        rebuildAt = null;
    }

    /// <summary>The MuJoCo height field shape made from a terrain, or 0.</summary>
    static nint HeightField(nint terrain)
    {
        var hfield = ShapeType("HField");
        var data = Unity.Call(terrain, "get_terrainData");
        var at = Unity.Position(terrain);
        nint near = 0;
        var nearest = float.MaxValue;
        foreach (var g in Unity.FindObjects(Class("MjGeom")))
        {
            if (Unity.IsNull(g) || Class("MjShapeComponent").Field("ShapeType").Get<int>(g) != hfield) continue;
            var shape = Class("MjShapeComponent").Field("HField").GetObject(g);
            if (shape == 0) continue;
            // The terrain it was made from: the same object, the same heights, or on the same GameObject.
            var t = Class("MjHeightFieldShape").Field("Terrain").GetObject(shape);
            if (t == terrain || (t != 0 && !Unity.IsNull(t) && Unity.Call(t, "get_terrainData") == data)) return shape;
            if (Unity.GetComponent(g, Unity.Class("UnityEngine.Terrain")) == terrain) return shape;
            // Else the height field sitting closest to this terrain.
            var d = Unity.Position(g) - at;
            var dist = d.X * d.X + d.Z * d.Z;
            if (t == 0 && dist < nearest) (near, nearest) = (shape, dist);
        }
        return nearest < 4 ? near : 0;
    }

    /// <summary>
    /// Every frame: once things have settled (or now, <paramref name="force"/>), hands MuJoCo the
    /// changed heights in place. Never rebuilds the physics scene: that respawns every rider.
    /// </summary>
    public void Tick(bool force = false) => Refresh(force, wholeGround: false);

    /// <summary>
    /// Hands the changed ground over: to the terrain's drawing (its level of detail, after delayed
    /// writes) and to MuJoCo, just the changed rectangle where it can (a few milliseconds) rather than
    /// the whole map's ground (over 100 ms on a big map) unless <paramref name="wholeGround"/>.
    /// </summary>
    void Refresh(bool force, bool wholeGround)
    {
        if (rebuildAt is not { } at || (!force && DateTime.UtcNow < at)) return;
        rebuildAt = null;
        var timer = Stopwatch.StartNew();
        var hfClass = Class("MjHeightFieldShape");
        var full = hfClass.Method("UpdateHeightFieldData", 0);
        var region = hfClass.Method("UpdateHeightFieldRegion", 1);
        var invalidate = hfClass.Method("InvalidateCache", 1);
        var rect = stackalloc int[4]; // a RectInt: x, y, width, height
        var done = new List<string>();
        var sync = TerrainData.Method("SyncHeightmap", 0);
        foreach (var (_, d) in dirty)
        {
            if (Unity.IsNull(d.Data.Target)) continue;
            if (!sync.IsNull) sync.Invoke(d.Data.Target);
            if (!invalidate.IsNull) invalidate.Invoke(0, d.Data.Target);
            var shape = Unity.IsNull(d.Terrain.Target) ? 0 : HeightField(d.Terrain.Target);
            var name = Unity.IsNull(d.Terrain.Target) ? "a gone terrain" : $"terrain '{Unity.Name(d.Terrain.Target)}'";
            if (shape == 0) { done.Add($"{name}: no MuJoCo height field found"); continue; }
            if (!region.IsNull && !wholeGround)
            {
                (rect[0], rect[1], rect[2], rect[3]) = (d.X0, d.Y0, d.X1 - d.X0 + 1, d.Y1 - d.Y0 + 1);
                region.Invoke(shape, (nint)rect);
                done.Add($"{name}: {rect[2]}x{rect[3]} of the ground updated");
            }
            else if (!full.IsNull) { full.Invoke(shape); done.Add($"{name}: ground updated"); }
            else done.Add($"{name}: this game build can't update the ground in place");
        }
        foreach (var (_, d) in dirty) { d.Terrain.Dispose(); d.Data.Dispose(); }
        dirty.Clear();
        if (done.Count > 0) log($"packed the snow: {string.Join("; ", done)} in {timer.ElapsedMilliseconds} ms ({Count} builds)");
    }

    public void Dispose()
    {
        try
        {
            // Everyone's snow comes off this game's terrain as the mod goes.
            while (steps.Count > 0) Undo(steps[^1].Owner);
            Tick(force: true);
        }
        catch (Exception e) when (e is Il2CppException or InvalidOperationException) { }
        material?.Dispose();
    }
}
