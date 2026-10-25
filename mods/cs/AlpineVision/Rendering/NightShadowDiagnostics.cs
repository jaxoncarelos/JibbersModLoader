using Jibbers.Sdk;
using Jibbers.Sdk.Game.Player;
using Jibbers.Sdk.Interop;

namespace JibbersAlpineVision.Rendering;

/// <summary>One-shot, read-only observations for an explicitly enabled shadow preview.</summary>
internal static class NightShadowDiagnostics
{
    public static void Report(Action<string> log)
    {
        try
        {
            ReportTerrainContract(log);
            ReportLocalRider(log);
            ReportMapLights(log);
        }
        catch (Exception exception)
        {
            log("Night shadow observations incomplete: " + exception.Message);
        }
    }

    static void ReportTerrainContract(Action<string> log)
    {
        var requirements = new Dictionary<string, string[]> {
            ["UnityEngine.Terrain"] = new[] { "get_terrainData" },
            ["UnityEngine.TerrainData"] = new[] { "get_size", "get_heightmapResolution", "GetHeights" },
            ["UnityEngine.Mesh"] = new[] { ".ctor", "set_vertices", "set_normals", "set_triangles", "RecalculateBounds" },
            ["UnityEngine.MeshFilter"] = new[] { "set_sharedMesh" },
            ["UnityEngine.MeshRenderer"] = new[] { "set_sharedMaterial", "set_shadowCastingMode" }
        };
        foreach (var (className, methodNames) in requirements)
        {
            var nativeClass = Unity.Class(className);
            var missing = methodNames.Where(name => nativeClass.Method(name).IsNull).ToArray();
            log("Night shadow API " + className + ": " + (missing.Length == 0 ? "available" : "missing " + string.Join(", ", missing)));
        }
        foreach (var name in new[] { "get_shadowCastingMode_Injected", "set_shadowCastingMode_Injected", "get_drawHeightmap_Injected" })
            log("Night shadow engine binding " + name + ": " + (Il2Cpp.ICall("UnityEngine.Terrain::" + name) != 0 ? "available" : "unavailable"));
    }

    static void ReportLocalRider(Action<string> log)
    {
        var spawner = RagdollSpawner.First();
        if (spawner is null || Unity.IsNull(spawner.Value.Rider.Ptr))
        {
            log("Night shadow rider: not spawned yet.");
            return;
        }
        var rider = spawner.Value.Rider.Ptr;
        var renderers = Unity.GetComponentsInChildren(rider, Unity.Class("UnityEngine.Renderer"), true);
        log("Night shadow rider: " + Unity.Name(rider) + "; " + renderers.Length + " renderers.");
        foreach (var renderer in renderers.Take(80))
        {
            var nativeClass = Il2Cpp.ClassOf(renderer);
            var meshGetter = nativeClass.Method("get_sharedMesh", 0);
            var mesh = meshGetter.IsNull ? 0 : meshGetter.Invoke(renderer);
            if (Unity.IsNull(mesh))
            {
                var filter = Unity.GetComponent(renderer, Unity.Class("UnityEngine.MeshFilter"));
                if (!Unity.IsNull(filter))
                    mesh = Unity.Call(filter, "get_sharedMesh");
            }
            var gameObject = Unity.Call(renderer, "get_gameObject");
            var materialGetter = nativeClass.Method("get_sharedMaterial", 0);
            var material = materialGetter.IsNull ? 0 : materialGetter.Invoke(renderer);
            var shader = Unity.IsNull(material) ? 0 : Unity.Call(material, "get_shader");
            log($"Night shadow caster {Hierarchy(renderer)}: active={Unity.Call<bool>(gameObject, "get_activeInHierarchy")}, enabled={ReadInt(renderer, "get_enabled", true)}, cast={ReadInt(renderer, "get_shadowCastingMode")}, mesh={Unity.Name(mesh)}, vertices={(Unity.IsNull(mesh) ? "none" : ReadInt(mesh, "get_vertexCount"))}, material={Unity.Name(material)}, shader={Unity.Name(shader)}.");
        }
    }

    static void ReportMapLights(Action<string> log)
    {
        var spawner = RagdollSpawner.First();
        if (spawner is null || Unity.IsNull(spawner.Value.Rider.Ptr))
            return;
        var riderPosition = Unity.Position(spawner.Value.Rider.Ptr);
        var floodlights = Unity.FindObjects(Unity.Class("UnityEngine.Light"))
            .Where(light => Hierarchy(light).Contains("floodlight", StringComparison.OrdinalIgnoreCase))
            .OrderBy(light => (Unity.Position(light) - riderPosition).Magnitude).Take(4);
        foreach (var light in floodlights)
            log($"Night floodlight {Hierarchy(light)}: distance={(Unity.Position(light) - riderPosition).Magnitude:F1}m, shadows={ReadInt(light, "get_shadows")}, resolution={ReadInt(light, "get_shadowResolution")}, customResolution={ReadInt(light, "get_shadowCustomResolution")}, cone={ReadFloat(light, "get_spotAngle")}, range={ReadFloat(light, "get_range")}, bias={ReadFloat(light, "get_shadowBias")}, normalBias={ReadFloat(light, "get_shadowNormalBias")}.");
    }

    static string ReadFloat(nint instance, string getter)
    {
        var method = Il2Cpp.ClassOf(instance).Method(getter, 0);
        return method.IsNull ? "unavailable" : method.Invoke<float>(instance).ToString("G5", System.Globalization.CultureInfo.InvariantCulture);
    }

    static string ReadInt(nint instance, string getter, bool boolean = false)
    {
        var method = Il2Cpp.ClassOf(instance).Method(getter, 0);
        if (method.IsNull)
            return "unavailable";
        return boolean ? method.Invoke<bool>(instance).ToString() : method.Invoke<int>(instance).ToString();
    }

    static string Hierarchy(nint component)
    {
        var names = new List<string>();
        var transform = Unity.Transform(component);
        for (var depth = 0; depth < 12 && !Unity.IsNull(transform); depth++)
        {
            names.Add(Unity.Name(transform));
            transform = Unity.Call(transform, "get_parent");
        }
        names.Reverse();
        return string.Join("/", names);
    }
}
