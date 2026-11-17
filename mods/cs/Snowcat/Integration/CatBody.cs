using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using SnowShovel;
using SnowShovel.Integration;

namespace Snowcat.Integration;

/// <summary>
/// The snowcat you see: the model that comes with the mod (8.5 m long, facing +z, standing on y 0)
/// with its own texture (decoded by the game), placed each frame.
/// Drawn only: it has no collisions of its own. Main thread only.
/// </summary>
internal sealed class CatBody : IDisposable
{
    static MeshData? model;
    readonly Action<string> log;
    GameRef? body, mesh, tiller, blade;
    /// <summary>A part that bends (the tiller, the blade): its mesh, where its vertices start, how much each follows, and the shift it shows now.</summary>
    sealed class Bent(GameRef mesh, Vector3[] start, Vector3[]? normals, float[] weights, Vector3 pivot)
    {
        public readonly GameRef Mesh = mesh;
        public readonly Vector3[] Start = start;
        public readonly Vector3[]? Normals = normals;
        public readonly float[] Weights = weights;
        public readonly Vector3 Pivot = pivot;
        public Parts.Pose Shown;
    }
    Bent? tillerBend, bladeBend;
    // The two belts (left, right), each with its own tread material so they can run at their own speeds.
    readonly GameRef?[] belts = new GameRef?[2];
    readonly GameRef?[] treads = new GameRef?[2];
    // Two spotlights on the cab's roof bar, pointing ahead and a little down the slope.
    readonly GameRef?[] lights = new GameRef?[2];
    static readonly Vector3[] LightAt = { new(-0.75f, 3.05f, 2.35f), new(0.75f, 3.05f, 2.35f) };
    readonly List<GameRef> materials = new();

    public CatBody(Action<string> log) => this.log = log;

    static MeshData Model => model ??= PackedAssets.Model();

    /// <summary>Unity's own URP Lit material (a map's custom shaders can draw nothing on a mesh of ours).</summary>
    static nint Lit()
    {
        // A brand-new material on Unity's lit shader: nothing carried over from a material the game (or
        // another snowcat) already has, whose texture position, scale and settings would come along too.
        if (litShader is null || Unity.IsNull(litShader.Target))
        {
            nint shader = 0;
            var all = Unity.CallStatic(Unity.Class("UnityEngine.Resources"), "FindObjectsOfTypeAll", Unity.Class("UnityEngine.Shader").SystemType);
            foreach (var sh in Il2Cpp.ArrayObjects(all))
                if (sh != 0 && !Unity.IsNull(sh) && Unity.Name(sh) == "Universal Render Pipeline/Lit") { shader = sh; break; }
            if (shader == 0) shader = Unity.CallStatic(Unity.Class("UnityEngine.Shader"), "Find", Il2Cpp.String("Universal Render Pipeline/Lit"));
            if (shader == 0 || Unity.IsNull(shader)) throw new InvalidOperationException("no lit shader to paint the snowcat with");
            litShader = new GameRef(shader);
        }
        var k = Unity.Class("UnityEngine.Material");
        var made = k.New();
        k.Method(".ctor", "UnityEngine.Shader").Invoke(made, litShader.Target);
        return made;
    }
    static GameRef? litShader;

    /// <summary>An image that comes with the mod (JPEG or PNG), decoded by the game's own <c>ImageConversion.LoadImage</c>.</summary>
    static unsafe nint Texture(string resource, byte[] bytes)
    {
        var k = Unity.Class("UnityEngine.Texture2D");
        var texture = k.New();
        int two = 2;
        k.Method(".ctor", 2).Invoke(texture, (nint)(&two), (nint)(&two));
        var array = Il2Cpp.NewArray(Il2Cpp.RequireClass("mscorlib.dll", "System.Byte"), bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>((void*)Il2Cpp.ArrayData(array), bytes.Length));
        var conversion = Il2Cpp.RequireClass("UnityEngine.ImageConversionModule.dll", "UnityEngine.ImageConversion");
        if (!conversion.Method("LoadImage", "UnityEngine.Texture2D", "System.Byte[]").Invoke<bool>(0, texture, array))
            throw new InvalidOperationException($"the game couldn't read {resource}");
        return texture;
    }

    nint Build()
    {
        if (body is not null && !Unity.IsNull(body.Target)) return body.Target;
        var m = Model;
        var lit = Lit();
        materials.Add(new GameRef(lit));
        // The tiller and the blade bend off the body (their arms follow smoothly), so they can be raised and lowered.
        var (withBlade, back, tillerWeights) = Parts.Bendable(m, Parts.TillerWeight);
        var (rest, front, bladeWeights) = Parts.Bendable(withBlade, Parts.BladeWeight);
        var made = Looks.NewMesh();
        Looks.Fill(made, rest.Vertices, rest.Normals.Length == rest.Vertices.Length ? rest.Normals : null, rest.Submeshes, rest.Uvs);
        mesh = new GameRef(made);
        var tillerMesh = Looks.NewMesh();
        Looks.Fill(tillerMesh, back.Vertices, back.Normals.Length == back.Vertices.Length ? back.Normals : null, back.Submeshes, back.Uvs);
        materials.Add(new GameRef(tillerMesh));
        tillerBend = new Bent(new GameRef(tillerMesh), back.Vertices, null, tillerWeights, default);
        var bladeMesh = Looks.NewMesh();
        Looks.Fill(bladeMesh, front.Vertices, front.Normals.Length == front.Vertices.Length ? front.Normals : null, front.Submeshes, front.Uvs);
        materials.Add(new GameRef(bladeMesh));
        bladeBend = new Bent(new GameRef(bladeMesh), front.Vertices, front.Normals.Length == front.Vertices.Length ? front.Normals : null, bladeWeights, Parts.BladePivot);
        // One textured material for the lot (the texture is an atlas of every part).
        var paint = Painted(lit, Texture("the snowcat's paint", PackedAssets.Paint()), 0.35f, 0.1f);
        var mats = m.Submeshes.Select(_ => paint).ToArray();
        body = new GameRef(Looks.Drawn("Mod_Snowcat", made, mats, shadows: true));
        tiller = new GameRef(Looks.Drawn("Mod_SnowcatTiller", tillerMesh, mats, shadows: true));
        blade = new GameRef(Looks.Drawn("Mod_SnowcatBlade", bladeMesh, mats, shadows: true));
        // The belts over the tracks, left and right.
        var tread = Texture("the tread", PackedAssets.Tread());
        for (var side = 0; side < 2; side++)
        {
            var loop = side == 0 ? Belt.Loop(-Belt.Outer, -Belt.Inner) : Belt.Loop(Belt.Inner, Belt.Outer);
            var beltMesh = Looks.NewMesh();
            Looks.Fill(beltMesh, loop.Vertices, loop.Normals, loop.Submeshes, loop.Uvs);
            materials.Add(new GameRef(beltMesh));
            var t = Painted(lit, tread, 0.3f, 0.2f);
            treads[side] = new GameRef(t);
            belts[side] = new GameRef(Looks.Drawn(side == 0 ? "Mod_SnowcatBeltLeft" : "Mod_SnowcatBeltRight", beltMesh, new[] { t }, shadows: true));
        }
        log($"snowcat model: {m.Vertices.Length} vertices, textured, with running tracks");
        return body.Target;
    }

    /// <summary>The headlights: switched on (made the first time) and aimed with the snowcat, or off.</summary>
    unsafe void Lights(Vector3 at, Quaternion rotation, bool on)
    {
        for (var i = 0; i < 2; i++)
        {
            if (!on)
            {
                if (lights[i] is { } off) Looks.Active(off.Target, false);
                continue;
            }
            if (lights[i] is null || Unity.IsNull(lights[i]!.Target))
            {
                var go = Looks.NewObject(i == 0 ? "Mod_SnowcatHeadlightLeft" : "Mod_SnowcatHeadlightRight");
                var light = Il2Cpp.ClassOf(go).Method("AddComponent", "System.Type").Invoke(go, Unity.Class("UnityEngine.Light").SystemType);
                UnityProperties.Set(light, "set_type", 0); // a spotlight
                UnityProperties.Set(light, "set_range", 60f);
                UnityProperties.Set(light, "set_spotAngle", 70f);
                UnityProperties.Set(light, "set_innerSpotAngle", 35f);
                UnityProperties.Set(light, "set_intensity", 6f);
                var warm = new Color(1f, 0.95f, 0.85f, 1);
                Unity.Call(light, "set_color", (nint)(&warm));
                lights[i] = new GameRef(go);
            }
            // Ahead and 12 degrees down, from the roof bar.
            var aim = Drive.Mul(rotation, Drive.Rotation(0, -12, 0));
            Looks.Place(lights[i]!.Target, at + Drive.Rotate(rotation, LightAt[i]), aim);
            Looks.Active(lights[i]!.Target, true);
        }
    }

    /// <summary>A copy of the lit material showing <paramref name="texture"/> (set every way Unity takes it: the URP property and its main texture).</summary>
    nint Painted(nint lit, nint texture, float smooth, float metal)
    {
        materials.Add(new GameRef(texture));
        var paint = Looks.Plain(lit, new Color(1, 1, 1, 1), smooth, metal);
        foreach (var prop in new[] { "_BaseMap", "_MainTex" })
            Looks.Texture(paint, prop, texture);
        var main = Il2Cpp.ClassOf(paint).Method("set_mainTexture", 1);
        if (!main.IsNull) main.Invoke(paint, texture);
        // The material copied may be another snowcat's tread, scrolled along and scaled: start square.
        Unshift(paint);
        materials.Add(new GameRef(paint));
        return paint;
    }

    static unsafe void Unshift(nint paint)
    {
        var (one, zero) = (new Vector2(1, 1), new Vector2(0, 0));
        var k = Il2Cpp.ClassOf(paint);
        var offset = k.Method("SetTextureOffset", "System.String", "UnityEngine.Vector2");
        var scale = k.Method("SetTextureScale", "System.String", "UnityEngine.Vector2");
        foreach (var prop in new[] { "_BaseMap", "_MainTex" })
        {
            if (!Unity.Call<bool>(paint, "HasProperty", Il2Cpp.String(prop))) continue;
            if (!offset.IsNull) offset.Invoke(paint, Il2Cpp.String(prop), (nint)(&zero));
            if (!scale.IsNull) scale.Invoke(paint, Il2Cpp.String(prop), (nint)(&one));
        }
    }

    /// <summary>Slides a tread along its belt to <paramref name="offset"/> (pattern repeats).</summary>
    static unsafe void Run(nint tread, float offset)
    {
        var at = new Vector2(0, offset - MathF.Floor(offset));
        var set = Il2Cpp.ClassOf(tread).Method("SetTextureOffset", "System.String", "UnityEngine.Vector2");
        foreach (var prop in new[] { "_BaseMap", "_MainTex" })
            if (Unity.Call<bool>(tread, "HasProperty", Il2Cpp.String(prop))) set.Invoke(tread, Il2Cpp.String(prop), (nint)(&at));
        var main = Il2Cpp.ClassOf(tread).Method("set_mainTextureOffset", 1);
        if (!main.IsNull) main.Invoke(tread, (nint)(&at));
    }

    /// <summary>
    /// Puts the snowcat at <paramref name="at"/> (the ground under its middle), turned by
    /// <paramref name="rotation"/>, its tracks run on to <paramref name="left"/> and <paramref name="right"/> (pattern repeats).
    /// </summary>
    public void Show(Vector3 at, Quaternion rotation, float left = 0, float right = 0, Vector3 tillerShift = default, Parts.Pose blade_ = default, bool headlights = false)
    {
        Lights(at, rotation, headlights);
        var go = Build();
        Looks.Place(go, at, rotation);
        Looks.Active(go, true);
        foreach (var (part, bend, shift) in new[] { (tiller, tillerBend, new Parts.Pose(tillerShift)), (blade, bladeBend, blade_) })
        {
            if (part is not { } pt || Unity.IsNull(pt.Target)) continue;
            Looks.Place(pt.Target, at, rotation);
            Looks.Active(pt.Target, true);
            Bend(bend, shift);
        }
        for (var side = 0; side < 2; side++)
        {
            if (belts[side] is not { } b || Unity.IsNull(b.Target)) continue;
            Looks.Place(b.Target, at, rotation);
            Looks.Active(b.Target, true);
            if (treads[side] is { } t) Run(t.Target, side == 0 ? left : right);
        }
    }

    /// <summary>Bends a part to <paramref name="pose"/> (only when it has moved: a few thousand vertices).</summary>
    static void Bend(Bent? b, Parts.Pose pose)
    {
        if (b is null || Unity.IsNull(b.Mesh.Target)) return;
        var d = b.Shown.Shift - pose.Shift;
        if (d.X * d.X + d.Y * d.Y + d.Z * d.Z < 1e-6f && MathF.Abs(b.Shown.Angle - pose.Angle) < 0.05f && MathF.Abs(b.Shown.Tilt - pose.Tilt) < 0.05f) return;
        var wasTurned = b.Shown.Angle != 0 || b.Shown.Tilt != 0;
        b.Shown = pose;
        var (vertices, normals) = Parts.Bend(b.Start, b.Normals, b.Weights, b.Pivot, pose);
        Looks.SetArray(b.Mesh.Target, "set_vertices", Looks.Vector3Class, vertices);
        // Turned (or just straightened), its faces light the way they now face.
        if (normals is not null && (wasTurned || pose.Angle != 0 || pose.Tilt != 0)) Looks.SetArray(b.Mesh.Target, "set_normals", Looks.Vector3Class, normals);
        Unity.Call(b.Mesh.Target, "RecalculateBounds");
    }

    public void Hide()
    {
        if (body is not null) Looks.Active(body.Target, false);
        foreach (var b in belts) if (b is not null) Looks.Active(b.Target, false);
        foreach (var l in lights) if (l is not null) Looks.Active(l.Target, false);
        if (tiller is not null) Looks.Active(tiller.Target, false);
        if (blade is not null) Looks.Active(blade.Target, false);
    }

    public void Dispose()
    {
        foreach (var r in new[] { body, mesh, tiller, blade }.Concat(belts).Concat(lights).Concat(materials))
        {
            if (r is null) continue;
            if (!Unity.IsNull(r.Target)) Unity.Destroy(r.Target);
            r.Dispose();
        }
        materials.Clear();
        body = mesh = tiller = blade = null;
        tillerBend = bladeBend = null;
        Array.Clear(belts);
        Array.Clear(treads);
        Array.Clear(lights);
    }

    /// <summary>A new map: the snowcat went with the old one.</summary>
    public void Forget()
    {
        body?.Dispose();
        tiller?.Dispose();
        blade?.Dispose();
        body = tiller = blade = null;
        tillerBend = bladeBend = null;
        for (var side = 0; side < 2; side++) { belts[side]?.Dispose(); belts[side] = null; lights[side]?.Dispose(); lights[side] = null; }
    }
}
