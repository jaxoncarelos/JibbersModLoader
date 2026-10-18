// Builds jibbers_atmosphere.bundle: one prefab ("JibbersAtmosphere") with the shade and fog shells
// and the snowfall mesh, their materials and shaders. Run headless:
//   Unity.exe -batchmode -quit -projectPath mods/JibbersAtmosphere/unity -executeMethod BuildAtmosphere.Build
// The bundle lands in mods/JibbersAtmosphere/build/.
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildAtmosphere
{
    const string Gen = "Assets/Gen";
    const int Flakes = 14000;

    [MenuItem("Jibbers/Build Atmosphere Bundle")]
    public static void Build()
    {
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Vulkan });
        PlayerSettings.colorSpace = ColorSpace.Linear;

        if (AssetDatabase.IsValidFolder(Gen)) AssetDatabase.DeleteAsset(Gen);
        AssetDatabase.CreateFolder("Assets", "Gen");

        var shell = FogShell();
        AssetDatabase.CreateAsset(shell, Gen + "/FogShell.asset");
        var snow = SnowMesh();
        AssetDatabase.CreateAsset(snow, Gen + "/Snow.asset");

        var fogMat = new Material(Shader.Find("Hidden/JibbersAtmosphere/Fog")) { name = "JA_Fog" };
        AssetDatabase.CreateAsset(fogMat, Gen + "/JA_Fog.mat");
        var shadeMat = new Material(Shader.Find("Hidden/JibbersAtmosphere/Shade")) { name = "JA_Shade" };
        AssetDatabase.CreateAsset(shadeMat, Gen + "/JA_Shade.mat");
        var sparkleMat = new Material(Shader.Find("Hidden/JibbersAtmosphere/Sparkle")) { name = "JA_Sparkle" };
        AssetDatabase.CreateAsset(sparkleMat, Gen + "/JA_Sparkle.mat");
        var snowMat = new Material(Shader.Find("Hidden/JibbersAtmosphere/Snow")) { name = "JA_Snow" };
        AssetDatabase.CreateAsset(snowMat, Gen + "/JA_Snow.mat");

        var surfaceMat = SnowSurfaceMaterial();
        AssetDatabase.CreateAsset(surfaceMat, Gen + "/JA_SnowSurface.mat");

        var root = new GameObject("JibbersAtmosphere");
        Child(root, "Sparkle", shell, sparkleMat);
        Child(root, "Shade", shell, shadeMat);
        Child(root, "Fog", shell, fogMat);
        Child(root, "Snow", snow, snowMat);
        // Never drawn: keeps the replacement terrain snow textures loaded for the core.
        Child(root, "SnowSurface", shell, surfaceMat).GetComponent<MeshRenderer>().enabled = false;
        PrefabUtility.SaveAsPrefabAsset(root, Gen + "/JibbersAtmosphere.prefab");
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();

        var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "build"));
        Directory.CreateDirectory(outDir);
        var build = new AssetBundleBuild
        {
            assetBundleName = "jibbers_atmosphere.bundle",
            assetNames = new[] { Gen + "/JibbersAtmosphere.prefab" },
            addressableNames = new[] { "JibbersAtmosphere" },
        };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new System.Exception("asset bundle build failed");
        Debug.Log("JibbersAtmosphere bundle built in " + outDir);
    }

    const string SnowDir = "Assets/Atmosphere/Snow/";

    // CC0 ambientCG snow (see Snow/LICENSE.txt), imported the way the game's own terrain textures are.
    static Material SnowSurfaceMaterial()
    {
        Texture2D Tex(string file, bool normal, bool srgb)
        {
            var path = SnowDir + file;
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = srgb;
            imp.mipmapEnabled = true;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.anisoLevel = 8;
            imp.maxTextureSize = 2048;
            imp.textureCompression = TextureImporterCompression.CompressedHQ;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        var m = new Material(Shader.Find("Hidden/JibbersAtmosphere/SnowSurface")) { name = "JA_SnowSurface" };
        m.SetTexture("_SurfaceTex", Tex("JA_SnowAlbedo.png", false, true));
        m.SetTexture("_SnowNormal", Tex("JA_SnowNormal.png", true, false));
        m.SetTexture("_SnowDetailNormal", Tex("JA_SnowDunesNormal.png", true, false));
        return m;
    }

    static GameObject Child(GameObject root, string name, Mesh mesh, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        r.allowOcclusionWhenDynamic = false;
        return go;
    }

    // Both meshes are placed by their shaders around the camera, so their bounds are huge to keep
    // them from ever being culled.
    static Bounds Everywhere => new Bounds(Vector3.zero, Vector3.one * 1e7f);

    static Mesh FogShell()
    {
        var m = new Mesh { name = "JA_FogShell" };
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) * 2 - 1, ((i >> 1) & 1) * 2 - 1, ((i >> 2) & 1) * 2 - 1);
        m.vertices = v;
        m.triangles = new[]
        {
            0, 2, 1, 1, 2, 3, // z-
            4, 5, 6, 5, 7, 6, // z+
            0, 1, 4, 1, 5, 4, // y-
            2, 6, 3, 3, 6, 7, // y+
            0, 4, 2, 2, 4, 6, // x-
            1, 3, 5, 3, 7, 5, // x+
        };
        m.bounds = Everywhere;
        return m;
    }

    static Mesh SnowMesh()
    {
        var rng = new System.Random(1234);
        float R() => (float)rng.NextDouble();
        var pos = new Vector3[Flakes * 4];
        var corner = new Vector2[Flakes * 4];
        var rnd = new Vector4[Flakes * 4];
        var idx = new int[Flakes * 6];
        Vector2[] corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
        for (int f = 0; f < Flakes; f++)
        {
            var seed = new Vector3(R(), R(), R());
            // Threshold spread evenly so the shown fraction scales smoothly.
            var r = new Vector4(R(), R(), R(), (f + R()) / Flakes);
            for (int c = 0; c < 4; c++)
            {
                pos[f * 4 + c] = seed;
                corner[f * 4 + c] = corners[c];
                rnd[f * 4 + c] = r;
            }
            int b = f * 4;
            idx[f * 6 + 0] = b; idx[f * 6 + 1] = b + 1; idx[f * 6 + 2] = b + 2;
            idx[f * 6 + 3] = b; idx[f * 6 + 4] = b + 2; idx[f * 6 + 5] = b + 3;
        }
        var m = new Mesh { name = "JA_Snow" };
        m.indexFormat = IndexFormat.UInt32;
        m.vertices = pos;
        m.uv = corner;
        m.SetUVs(1, rnd);
        m.triangles = idx;
        m.bounds = Everywhere;
        return m;
    }
}
