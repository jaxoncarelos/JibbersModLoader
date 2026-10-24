// Builds jibbers_atmosphere.bundle: one prefab ("JibbersAtmosphere") with the shade and fog shells
// and the snowfall mesh, their materials and shaders. Run headless:
//   Unity.exe -batchmode -quit -projectPath mods/JibbersAtmosphere/unity -executeMethod BuildAtmosphere.Build
// The bundle lands in mods/JibbersAtmosphere/build/.
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor.SceneManagement;

public static class BuildAtmosphere
{
    const string Gen = "Assets/Gen";
    const int Flakes = 14000;

    // Standalone editor render test; never starts or connects to the game.
    public static void TestAndBuild()
    {
        TestCharacterMask(false);
        TestCharacterMask(true);
        Build();
    }

    static void TestCharacterMask(bool skinned)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cam = new GameObject("TestCamera").AddComponent<Camera>();
        cam.transform.position = new Vector3(0, 4, -5);
        cam.transform.LookAt(new Vector3(0, 0.6f, 0));
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.nearClipPlane = 0.1f; cam.farClipPlane = 100;
        cam.depthTextureMode = DepthTextureMode.Depth;
        cam.allowHDR = false; cam.allowMSAA = false;
        cam.targetTexture = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture.Create();
        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional; light.transform.rotation = Quaternion.Euler(65, 0, 0);
        RenderSettings.ambientLight = Color.gray;
        var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.transform.position = new Vector3(0, 0.6f, 0);
        body.transform.localScale = new Vector3(1.5f, 1.2f, 1.5f);
        Renderer bodyRenderer = body.GetComponent<Renderer>();
        if (skinned)
        {
            var mesh = Object.Instantiate(body.GetComponent<MeshFilter>().sharedMesh);
            var weights = new BoneWeight[mesh.vertexCount];
            for (int vertex = 0; vertex < weights.Length; vertex++)
                weights[vertex] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
            mesh.boneWeights = weights; mesh.bindposes = new[] { Matrix4x4.identity };
            Object.DestroyImmediate(bodyRenderer);
            Object.DestroyImmediate(body.GetComponent<MeshFilter>());
            var skin = body.AddComponent<SkinnedMeshRenderer>();
            var bone = new GameObject("Animated clothing bone").transform;
            bone.SetParent(body.transform, false);
            bone.localRotation = Quaternion.Euler(0, 0, 12);
            skin.sharedMesh = mesh; skin.bones = new[] { bone }; skin.rootBone = bone;
            skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 4);
            bodyRenderer = skin;
        }
        var snow = new Material(Shader.Find("Standard")); snow.color = Color.gray;
        var clothes = new Material(Shader.Find("Standard")); clothes.color = new Color(0.025f, 0.025f, 0.025f);
        plane.GetComponent<Renderer>().sharedMaterial = snow; bodyRenderer.sharedMaterial = clothes;
        var shell = FogShell();
        var glitter = new Material(Shader.Find("Hidden/JibbersAtmosphere/Sparkle"));
        var mask = new Material(Shader.Find("Hidden/JibbersAtmosphere/CharacterMask"));
        if (ShaderUtil.ShaderHasError(glitter.shader) || ShaderUtil.ShaderHasError(mask.shader))
            throw new System.Exception("character mask/glitter shader compilation failed");
        var shellObject = new GameObject("Effects");
        var shellRenderer = Child(shellObject, "Glitter", shell, glitter).GetComponent<Renderer>();
        Shader.SetGlobalVector("_JA_SunDir", new Vector4(0, 1, 0, 0));
        Shader.SetGlobalVector("_JA_SunColor", new Vector4(1, 1, 1, 10));
        Shader.SetGlobalVector("_JA_Sparkle", new Vector4(1, 1, 40, 0.015f));
        Shader.SetGlobalFloat("_JA_CharBoxCount", 0);
        Color[] Render()
        {
            cam.Render();
            var previous = RenderTexture.active; RenderTexture.active = cam.targetTexture;
            var image = new Texture2D(512, 512, TextureFormat.RGBA32, false, true);
            image.ReadPixels(new Rect(0, 0, 512, 512), 0, 0); image.Apply();
            var pixels = image.GetPixels(); Object.DestroyImmediate(image);
            RenderTexture.active = previous; return pixels;
        }
        shellRenderer.enabled = false;
        cam.Render(); cam.Render(); // settle freshly created skinned vertex buffers
        var baseline = Render();
        shellRenderer.enabled = true;
        var unmasked = Render();
        var commands = new CommandBuffer { name = "Glitter character exclusion render test" };
        commands.DrawMesh(shell, Matrix4x4.identity, mask, 0, 1);
        commands.DrawRenderer(bodyRenderer, mask, 0, 0);
        cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, commands);
        var masked = Render();
        cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, commands); commands.Release();
        // The camera-independent layer alone: the box the core sends from the renderer bounds.
        var b = bodyRenderer.bounds;
        Shader.SetGlobalVector("_JA_CharBox0C", b.center);
        Shader.SetGlobalVector("_JA_CharBox0E", b.extents + Vector3.one * 0.15f);
        Shader.SetGlobalFloat("_JA_CharBoxCount", 1);
        var boxed = Render();
        Shader.SetGlobalFloat("_JA_CharBoxCount", 0);
        shellRenderer.enabled = false;
        // Keep ground as a black depth occluder: tilted/skinned vertices below
        // the ground are not visible clothing and must not suppress snow glitter.
        var groundId = new Material(Shader.Find("Unlit/Color")); groundId.color = Color.black;
        plane.GetComponent<Renderer>().sharedMaterial = groundId;
        var id = new Material(Shader.Find("Unlit/Color")); id.color = Color.white;
        bodyRenderer.sharedMaterial = id;
        var bodyPixels = Render();
        int bodyGlints = 0, leaked = 0, groundGlints = 0, retained = 0, boxLeaked = 0, boxRetained = 0;
        float Difference(Color a, Color b) => Mathf.Max(Mathf.Abs(a.r-b.r), Mathf.Abs(a.g-b.g), Mathf.Abs(a.b-b.b));
        for (int y = 2; y < 510; y++) for (int x = 2; x < 510; x++)
        {
            int pixel = y * 512 + x;
            bool inside = true, outside = true;
            for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
            {
                bool rider = bodyPixels[pixel + dy * 512 + dx].r > 0.8f;
                inside &= rider; outside &= !rider;
            }
            bool glint = Difference(unmasked[pixel], baseline[pixel]) > 0.01f;
            if (inside && glint) bodyGlints++;
            if (inside && Difference(masked[pixel], baseline[pixel]) > 0.005f) leaked++;
            if (inside && Difference(boxed[pixel], baseline[pixel]) > 0.005f) boxLeaked++;
            if (outside && glint)
            {
                groundGlints++;
                if (Difference(masked[pixel], unmasked[pixel]) < 0.005f) retained++;
                if (Difference(boxed[pixel], unmasked[pixel]) < 0.005f) boxRetained++;
            }
        }
        Debug.Log($"CHARACTER_MASK_RENDER_TEST skinned={skinned}: body glints before={bodyGlints}, after={leaked}, after boxes only={boxLeaked}; snow glints={groundGlints}, preserved={retained}, preserved with boxes={boxRetained}");
        if (bodyGlints < 5 || groundGlints < 20)
            throw new System.Exception("Render test produced no glints to check");
        // Boxes also cover the snow right around the feet, so they may drop a few ground glints.
        if (boxLeaked != 0 || boxRetained < groundGlints * 0.8f)
            throw new System.Exception("Character boxes failed to exclude clothing while retaining snow glitter");
        cam.targetTexture.Release();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [MenuItem("Jibbers/Build Atmosphere Bundle")]
    public static void Build()
    {
        var mac=System.Environment.GetEnvironmentVariable("JIBBERS_ASSET_PLATFORM")=="macos";
        var target=mac?BuildTarget.StandaloneOSX:BuildTarget.StandaloneWindows64;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, mac?new[]{GraphicsDeviceType.Metal}:new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Vulkan });
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
        var characterMaskMat = new Material(Shader.Find("Hidden/JibbersAtmosphere/CharacterMask")) { name = "JA_CharacterMask" };
        AssetDatabase.CreateAsset(characterMaskMat, Gen + "/JA_CharacterMask.mat");

        var surfaceMat = SnowSurfaceMaterial();
        AssetDatabase.CreateAsset(surfaceMat, Gen + "/JA_SnowSurface.mat");

        var root = new GameObject("JibbersAtmosphere");
        Child(root, "Sparkle", shell, sparkleMat);
        Child(root, "Shade", shell, shadeMat);
        Child(root, "Fog", shell, fogMat);
        Child(root, "Snow", snow, snowMat);
        Child(root, "CharacterMask", shell, characterMaskMat).GetComponent<MeshRenderer>().enabled = false;
        // Never drawn: keeps the replacement terrain snow textures loaded for the core.
        Child(root, "SnowSurface", shell, surfaceMat).GetComponent<MeshRenderer>().enabled = false;
        PrefabUtility.SaveAsPrefabAsset(root, Gen + "/JibbersAtmosphere.prefab");
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();

        var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", mac?"build/macos":"build"));
        Directory.CreateDirectory(outDir);
        var build = new AssetBundleBuild
        {
            assetBundleName = "jibbers_atmosphere.bundle",
            assetNames = new[] { Gen + "/JibbersAtmosphere.prefab" },
            addressableNames = new[] { "JibbersAtmosphere" },
        };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            target);
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
