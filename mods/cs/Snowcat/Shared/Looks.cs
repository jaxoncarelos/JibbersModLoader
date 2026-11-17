using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace SnowShovel.Integration;

/// <summary>Making materials and meshes from code. Main thread only.</summary>
internal static unsafe class Looks
{
    static Il2CppClass Material => Unity.Class("UnityEngine.Material");

    /// <summary>A new material copying <paramref name="source"/>.</summary>
    public static nint Copy(nint source)
    {
        var made = Material.New();
        Material.Method(".ctor", "UnityEngine.Material").Invoke(made, source);
        return made;
    }

    static bool Has(nint m, string prop) => Unity.Call<bool>(m, "HasProperty", Il2Cpp.String(prop));

    public static void Colour(nint m, Color c)
    {
        foreach (var prop in new[] { "_BaseColor", "_Color" })
            if (Has(m, prop)) Il2Cpp.ClassOf(m).Method("SetColor", "System.String", "UnityEngine.Color").Invoke(m, Il2Cpp.String(prop), (nint)(&c));
    }

    /// <summary>Makes a lit material glow a little (its emission), where the shader has it.</summary>
    public static void Glow(nint m, Color c)
    {
        if (!Has(m, "_EmissionColor")) return;
        Il2Cpp.ClassOf(m).Method("SetColor", "System.String", "UnityEngine.Color").Invoke(m, Il2Cpp.String("_EmissionColor"), (nint)(&c));
        Il2Cpp.ClassOf(m).Method("EnableKeyword", "System.String").Invoke(m, Il2Cpp.String("_EMISSION"));
    }

    public static void Float(nint m, string prop, float value)
    {
        if (Has(m, prop)) Il2Cpp.ClassOf(m).Method("SetFloat", "System.String", "System.Single").Invoke(m, Il2Cpp.String(prop), (nint)(&value));
    }

    public static void NoTexture(nint m)
    {
        foreach (var prop in new[] { "_BaseMap", "_MainTex" })
            if (Has(m, prop)) Il2Cpp.ClassOf(m).Method("SetTexture", "System.String", "UnityEngine.Texture").Invoke(m, Il2Cpp.String(prop), 0);
    }

    /// <summary>Sets a texture on a material (where it has that property).</summary>
    public static void Texture(nint m, string prop, nint texture)
    {
        if (Has(m, prop)) Il2Cpp.ClassOf(m).Method("SetTexture", "System.String", "UnityEngine.Texture").Invoke(m, Il2Cpp.String(prop), texture);
    }

    /// <summary>A plain coloured material: a copy of <paramref name="lit"/> (a URP Lit material) with no texture.</summary>
    public static nint Plain(nint lit, Color colour, float smoothness, float metallic)
    {
        var m = Copy(lit);
        NoTexture(m);
        Colour(m, colour);
        Float(m, "_Smoothness", smoothness);
        Float(m, "_Glossiness", smoothness);
        Float(m, "_Metallic", metallic);
        return m;
    }

    /// <summary>
    /// A see-through material for the brush preview: Unity's sprite shader, tinted by vertex colour; else
    /// a copy of a see-through URP material the map already draws, else <paramref name="lit"/> switched
    /// to transparent (both tinted <paramref name="tint"/>).
    /// </summary>
    public static nint SeeThrough(nint lit, Color tint, Action<string> log)
    {
        // A sprite shader blends by vertex colour and is in every Unity game (unlit, so the tint shows true).
        foreach (var name in new[] { "Sprites/Default", "UI/Default" })
        {
            var shader = Unity.CallStatic(Unity.Class("UnityEngine.Shader"), "Find", Il2Cpp.String(name));
            if (shader == 0 || Unity.IsNull(shader)) continue;
            var m = Material.New();
            Material.Method(".ctor", "UnityEngine.Shader").Invoke(m, shader);
            Colour(m, new Color(1, 1, 1, 1));
            log($"brush preview drawn with '{name}'");
            return m;
        }
        nint best = 0;
        var bestScore = -1;
        foreach (var r in Unity.FindObjects(Unity.Class("UnityEngine.MeshRenderer")))
        {
            if (Unity.IsNull(r)) continue;
            var m = Unity.Call(r, "get_sharedMaterial");
            if (m == 0 || Unity.IsNull(m) || Unity.Call<int>(m, "get_renderQueue") < 2900) continue;
            var shader = Unity.Call(m, "get_shader");
            var name = shader == 0 ? "" : Unity.Name(shader);
            if (!name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal) || name.Contains("Particle") || name.Contains("Terrain")) continue;
            var score = name.EndsWith("/Unlit") ? 2 : name.EndsWith("/Lit") ? 1 : 0;
            if (score > bestScore) (best, bestScore) = (m, score);
        }
        nint made;
        if (best != 0)
        {
            made = Copy(best);
            log($"brush preview is a tinted copy of the map's '{Unity.Name(best)}' ({Unity.Name(Unity.Call(best, "get_shader"))})");
        }
        else
        {
            made = Copy(lit);
            Float(made, "_Surface", 1);
            Float(made, "_Blend", 0);
            Float(made, "_SrcBlend", 5);
            Float(made, "_DstBlend", 10);
            Float(made, "_SrcBlendAlpha", 1);
            Float(made, "_DstBlendAlpha", 10);
            Float(made, "_ZWrite", 0);
            Float(made, "_AlphaClip", 0);
            var k = Il2Cpp.ClassOf(made);
            k.Method("EnableKeyword", "System.String").Invoke(made, Il2Cpp.String("_SURFACE_TYPE_TRANSPARENT"));
            var tag = k.Method("SetOverrideTag", "System.String", "System.String");
            if (!tag.IsNull) tag.Invoke(made, Il2Cpp.String("RenderType"), Il2Cpp.String("Transparent"));
            UnityProperties.Set(made, "set_renderQueue", 3000);
            log("brush preview is the snow material made see-through (no see-through material on this map to copy)");
        }
        NoTexture(made);
        Colour(made, tint);
        Float(made, "_Smoothness", 0.2f);
        return made;
    }

    public static void SetArray<T>(nint target, string setter, Il2CppClass element, T[] values) where T : unmanaged
    {
        using var array = new GameRef(Il2Cpp.NewArray(element, values.Length));
        values.AsSpan().CopyTo(new Span<T>((void*)Il2Cpp.ArrayData(array.Target), values.Length));
        Unity.Call(target, setter, array.Target);
    }

    public static nint NewMesh()
    {
        var k = Unity.Class("UnityEngine.Mesh");
        var mesh = k.New();
        k.Method(".ctor", 0).Invoke(mesh);
        return mesh;
    }

    public static Il2CppClass Vector3Class => Unity.Class("UnityEngine.Vector3");
    public static Il2CppClass IntClass => Il2Cpp.RequireClass("mscorlib.dll", "System.Int32");

    /// <summary>Fills a mesh: vertices, normals (or worked out), and one triangle list per submesh.</summary>
    public static void Fill(nint mesh, Vector3[] vertices, Vector3[]? normals, int[][] submeshes, Vector2[]? uvs = null)
    {
        Unity.Call(mesh, "Clear");
        // Past 65k vertices needs 32-bit indices.
        if (vertices.Length > 65000) UnityProperties.Set(mesh, "set_indexFormat", 1);
        SetArray(mesh, "set_vertices", Vector3Class, vertices);
        UnityProperties.Set(mesh, "set_subMeshCount", submeshes.Length);
        var setTriangles = Il2Cpp.ClassOf(mesh).Methods.FirstOrDefault(m => m.Name == "SetTriangles" && m.ParameterCount == 2 && m.ParameterTypes[0].Contains("Int32[]") && m.ParameterTypes[1].Contains("Int32"));
        for (var i = 0; i < submeshes.Length; i++)
        {
            if (submeshes.Length == 1 || setTriangles.IsNull) { SetArray(mesh, "set_triangles", IntClass, submeshes[i]); continue; }
            using var array = new GameRef(Il2Cpp.NewArray(IntClass, submeshes[i].Length));
            submeshes[i].AsSpan().CopyTo(new Span<int>((void*)Il2Cpp.ArrayData(array.Target), submeshes[i].Length));
            var sub = i;
            setTriangles.Invoke(mesh, array.Target, (nint)(&sub));
        }
        if (uvs is not null && uvs.Length == vertices.Length) SetArray(mesh, "set_uv", Unity.Class("UnityEngine.Vector2"), uvs);
        if (normals is not null) SetArray(mesh, "set_normals", Vector3Class, normals);
        else Unity.Call(mesh, "RecalculateNormals");
        Unity.Call(mesh, "RecalculateBounds");
    }

    /// <summary>A new GameObject drawing <paramref name="mesh"/> with <paramref name="materials"/> (one per submesh), casting no shadow if <paramref name="shadows"/> is false.</summary>
    public static nint Drawn(string name, nint mesh, nint[] materials, bool shadows)
    {
        var go = NewObject(name);
        var k = Il2Cpp.ClassOf(go);
        var filter = k.Method("AddComponent", "System.Type").Invoke(go, Unity.Class("UnityEngine.MeshFilter").SystemType);
        Unity.Call(filter, "set_sharedMesh", mesh);
        var renderer = k.Method("AddComponent", "System.Type").Invoke(go, Unity.Class("UnityEngine.MeshRenderer").SystemType);
        if (materials.Length == 1) Unity.Call(renderer, "set_sharedMaterial", materials[0]);
        else
        {
            using var array = new GameRef(Il2Cpp.NewArray(Material, materials.Length));
            materials.AsSpan().CopyTo(new Span<nint>((void*)Il2Cpp.ArrayData(array.Target), materials.Length));
            Unity.Call(renderer, "set_sharedMaterials", array.Target);
        }
        if (!shadows)
        {
            UnityProperties.Set(renderer, "set_shadowCastingMode", 0);
            UnityProperties.Set(renderer, "set_receiveShadows", false);
        }
        return go;
    }

    public static nint NewObject(string name)
    {
        var k = Unity.Class("UnityEngine.GameObject");
        var go = k.New();
        k.Method(".ctor", "System.String").Invoke(go, Il2Cpp.String(name));
        return go;
    }

    public static void Place(nint go, Vector3 at, Quaternion rotation)
    {
        var tr = Unity.Transform(go);
        Unity.Call(tr, "SetPositionAndRotation", (nint)(&at), (nint)(&rotation));
    }

    public static void Active(nint go, bool on)
    {
        if (go != 0 && !Unity.IsNull(go)) UnityProperties.Set(go, "SetActive", on);
    }
}
