// Builds jibbers_raytracing.bundle (the inline ray-tracing compute shader, composite and proxy
// materials) and renders an editor test of the exact command-buffer recipe the mod uses in the game.
//   Unity.exe -batchmode -quit -force-d3d12 -projectPath mods/JibbersRayTracing/unity -executeMethod BuildRayTracing.TestAndBuild
// The bundle and test images land in mods/JibbersRayTracing/build/.
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildRayTracing
{
    const string Gen = "Assets/Gen";
    static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "build"));

    public static void TestAndBuild()
    {
        Build();
        Test();
    }

    [MenuItem("Jibbers/Build Ray Tracing Bundle")]
    public static void Build()
    {
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Vulkan });
        PlayerSettings.colorSpace = ColorSpace.Linear;
        if (AssetDatabase.IsValidFolder(Gen)) AssetDatabase.DeleteAsset(Gen);
        AssetDatabase.CreateFolder("Assets", "Gen");
        var composite = new Material(Shader.Find("Hidden/JibbersRT/Composite")) { name = "JRT_Composite" };
        AssetDatabase.CreateAsset(composite, Gen + "/JRT_Composite.mat");
        var debugMaterial = new Material(Shader.Find("Hidden/JibbersRT/Debug")) { name = "JRT_Debug" };
        AssetDatabase.CreateAsset(debugMaterial, Gen + "/JRT_Debug.mat");
        var proxy = new Material(Shader.Find("Hidden/JibbersRT/Proxy")) { name = "JRT_Proxy" };
        AssetDatabase.CreateAsset(proxy, Gen + "/JRT_Proxy.mat");
        AssetDatabase.SaveAssets();
        if (AssetDatabase.LoadAssetAtPath<RayTracingShader>("Assets/RayTracing/JibbersRT.raytrace") == null
            || AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RayTracing/JibbersRTBlur.compute") == null)
            throw new System.Exception("ray tracing or blur shader missing");
        Directory.CreateDirectory(OutDir);
        var build = new AssetBundleBuild
        {
            assetBundleName = "jibbers_raytracing.bundle",
            assetNames = new[] { "Assets/RayTracing/JibbersRT.raytrace", "Assets/RayTracing/JibbersRTBlur.compute", Gen + "/JRT_Composite.mat", Gen + "/JRT_Debug.mat", Gen + "/JRT_Proxy.mat" },
            addressableNames = new[] { "JibbersRT", "JibbersRTBlur", "JRT_Composite", "JRT_Debug", "JRT_Proxy" },
        };
        var manifest = BuildPipeline.BuildAssetBundles(OutDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new System.Exception("asset bundle build failed");
        Debug.Log("JibbersRT bundle built in " + OutDir);
    }

    // ---- Editor render test: the same calls the mod makes through IL2CPP, in the same order. ----
    public static void Test()
    {
        Debug.Log($"JibbersRT test: device {SystemInfo.graphicsDeviceType}, ray tracing {SystemInfo.supportsRayTracing}, inline {SystemInfo.supportsInlineRayTracing}");
        if (!SystemInfo.supportsRayTracing) { Debug.Log("JibbersRT test skipped: no ray tracing on this device"); return; }
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var snow = new Material(Shader.Find("Standard")) { color = new Color(.9f, .92f, .95f) };
        var rock = new Material(Shader.Find("Standard")) { color = new Color(.35f, .33f, .3f) };
        var renderers = new System.Collections.Generic.List<Renderer>();
        GameObject Make(PrimitiveType type, Vector3 position, Vector3 scale, Material material, float yaw = 0)
        {
            var go = GameObject.CreatePrimitive(type);
            go.transform.position = position; go.transform.localScale = scale; go.transform.rotation = Quaternion.Euler(0, yaw, 0);
            go.GetComponent<Renderer>().sharedMaterial = material;
            renderers.Add(go.GetComponent<Renderer>());
            return go;
        }
        Make(PrimitiveType.Plane, Vector3.zero, new Vector3(12, 1, 12), snow);
        Make(PrimitiveType.Cube, new Vector3(-6, 1.5f, 6), new Vector3(3, 3, 3), rock, 20);
        Make(PrimitiveType.Cube, new Vector3(4, 6, 14), new Vector3(2, 12, 2), rock);       // tall pillar: long shadow
        Make(PrimitiveType.Sphere, new Vector3(3, 1.2f, 2), new Vector3(2.4f, 2.4f, 2.4f), snow);
        Make(PrimitiveType.Cube, new Vector3(-1, .4f, -1), new Vector3(6, .8f, .5f), rock, -35); // low rail: contact shadow
        Make(PrimitiveType.Cube, new Vector3(-12, 8, 30), new Vector3(16, 16, 4), rock, 10);  // distant ridge

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional; sun.intensity = 1.2f;
        sun.transform.rotation = Quaternion.Euler(22, 160, 0);
        RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(.38f, .42f, .5f);
        QualitySettings.shadowDistance = 30;

        var cam = new GameObject("Camera").AddComponent<Camera>();
        cam.transform.position = new Vector3(0, 7, -14); cam.transform.LookAt(new Vector3(0, 0, 6));
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.55f, .7f, .9f);
        cam.nearClipPlane = .1f; cam.farClipPlane = 400; cam.fieldOfView = 60;
        cam.depthTextureMode = DepthTextureMode.Depth;
        cam.allowMSAA = false; cam.allowHDR = true;
        const int W = 960, H = 540;
        cam.targetTexture = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);

        var bundle=AssetBundle.LoadFromFile(Path.Combine(OutDir,"jibbers_alpinevision.bundle"));
        if(bundle==null)throw new System.Exception("Cannot load packaged ray tracing assets");
        var raytrace=bundle.LoadAsset<RayTracingShader>("JibbersRT");
        var compute=bundle.LoadAsset<ComputeShader>("JibbersRTBlur");
        var composite=bundle.LoadAsset<Material>("JRT_Composite");
        var debugView=bundle.LoadAsset<Material>("JRT_Debug");
        var proxyMaterial=bundle.LoadAsset<Material>("JRT_Proxy");
        var protectedAssets=new Object[]{raytrace,compute,composite,debugView,proxyMaterial};
        var originalFlags=new HideFlags[protectedAssets.Length];
        for(int i=0;i<protectedAssets.Length;i++)
        {
            if(protectedAssets[i]==null)throw new System.Exception("Missing packaged ray tracing asset");
            originalFlags[i]=protectedAssets[i].hideFlags;
            protectedAssets[i].hideFlags|=HideFlags.DontUnloadUnusedAsset;
        }
        // Same idle/disabled interval as F7 off or a map transition: no command buffer
        // references these assets when the engine unloads unused native resources.
        EditorUtility.UnloadUnusedAssetsImmediate(true);
        foreach(var asset in protectedAssets)if(asset==null)throw new System.Exception("Ray tracing asset destroyed during cleanup");
        Debug.Log("PASS packaged ray tracing shader, compute and three materials survive unused-asset cleanup before dispatch.");

        // The game's build keeps only the Settings constructor; the mod passes these exact integers.
        Debug.Log($"JibbersRT enums: Manual={(int)RayTracingAccelerationStructure.ManagementMode.Manual} Everything={(int)RayTracingAccelerationStructure.RayTracingModeMask.Everything} FastTrace={(int)RayTracingAccelerationStructureBuildFlags.PreferFastTrace} FastBuild={(int)RayTracingAccelerationStructureBuildFlags.PreferFastBuild} SubMesh={(int)(RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly)} BeforeForwardAlpha={(int)CameraEvent.BeforeForwardAlpha} Depth={(int)BuiltinRenderTextureType.Depth} CameraTarget={(int)BuiltinRenderTextureType.CameraTarget} RGHalf={(int)RenderTextureFormat.RGHalf} D3D12={(int)GraphicsDeviceType.Direct3D12} R16G16_SFloat={(int)UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_SFloat}");
        // An effect shell around the camera, like Atmosphere's fog: it must be excluded from ray tracing.
        var shell = GameObject.CreatePrimitive(PrimitiveType.Cube);
        shell.transform.localScale = Vector3.one * 3; shell.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Hidden/JibbersRT/Proxy"));
        Debug.Log($"JibbersRT modes: default rayTracingMode={(int)renderers[0].rayTracingMode} Off={(int)UnityEngine.Experimental.Rendering.RayTracingMode.Off} Automatic={(int)RayTracingAccelerationStructure.ManagementMode.Automatic}");
        // The game stripped Renderer.rayTracingMode: exclusion is by layer (camera still draws layer 30).
        shell.layer = 30;
        // The game's build kept automatic management only (AddInstance(Renderer) was stripped).
        var rtas = new RayTracingAccelerationStructure(new RayTracingAccelerationStructure.Settings
        {
            managementMode = (RayTracingAccelerationStructure.ManagementMode)1,
            rayTracingModeMask = (RayTracingAccelerationStructure.RayTracingModeMask)30,
            layerMask = ~(1 << 30),
            buildFlagsStaticGeometries = (RayTracingAccelerationStructureBuildFlags)1,
            buildFlagsDynamicGeometries = (RayTracingAccelerationStructureBuildFlags)2,
        });
        shell.transform.position = new Vector3(0, 7, -14);

        int tw = W / 2, th = H / 2;
        var traced = new RenderTexture(tw, th, 0, RenderTextureFormat.RGHalf) { enableRandomWrite = true }; traced.Create();
        var blurred = new RenderTexture(tw, th, 0, RenderTextureFormat.RGHalf) { enableRandomWrite = true }; blurred.Create();
        int blur = compute.FindKernel("Blur");
        var cb = new CommandBuffer { name = "JibbersRT" };

        // As in the game: no RTAS.Build/GetInstanceCount (stripped); the command buffer builds it.
        void Record(int pass, int frame, int debug = 0)
        {
            cb.Clear();
            var gpuProjection = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
            var inverseViewProjection = (gpuProjection * cam.worldToCameraMatrix).inverse;
            var toSun = -sun.transform.forward;
            cb.BuildRayTracingAccelerationStructure(rtas);
            cb.SetRayTracingShaderPass(raytrace, "JibbersRT");
            cb.SetRayTracingAccelerationStructure(raytrace, "_RT_Scene", rtas);
            cb.SetRayTracingMatrixParam(raytrace, "_RT_InvViewProj", inverseViewProjection);
            cb.SetRayTracingVectorParam(raytrace, "_RT_CameraPos", cam.transform.position);
            cb.SetRayTracingVectorParam(raytrace, "_RT_SunDir", new Vector4(toSun.x, toSun.y, toSun.z, .012f));
            cb.SetRayTracingVectorParam(raytrace, "_RT_Params", new Vector4(1.5f, frame, 2000, 6));
            cb.SetRayTracingVectorParam(raytrace, "_RT_Size", new Vector4(tw, th, W, H));
            cb.SetRayTracingVectorParam(raytrace, "_RT_Debug", new Vector4(debug, 0, 0, 0));
            cb.SetRayTracingTextureParam(raytrace, "_CameraDepthTexture", BuiltinRenderTextureType.Depth);
            cb.SetRayTracingTextureParam(raytrace, "_RT_Out", traced);
            cb.DispatchRays(raytrace, "Trace", (uint)tw, (uint)th, 1, cam);
            cb.SetComputeMatrixParam(compute, "_RT_InvViewProj", inverseViewProjection);
            cb.SetComputeVectorParam(compute, "_RT_CameraPos", cam.transform.position);
            cb.SetComputeVectorParam(compute, "_RT_Size", new Vector4(tw, th, W, H));
            cb.SetComputeTextureParam(compute, blur, "_CameraDepthTexture", BuiltinRenderTextureType.Depth);
            cb.SetComputeTextureParam(compute, blur, "_RT_In", traced);
            cb.SetComputeTextureParam(compute, blur, "_RT_Blurred", blurred);
            cb.DispatchCompute(compute, blur, (tw + 7) / 8, (th + 7) / 8, 1);
            cb.SetGlobalTexture("_RT_Result", blurred);
            cb.SetGlobalVector("_RT_Composite", new Vector4(.72f, .65f, 0, 0));
            // The game kept only Blit(source, dest, material): one single-pass material per view.
            cb.Blit(blurred, BuiltinRenderTextureType.CameraTarget, pass == 0 ? composite : debugView);
        }

        Color[] Save(string name)
        {
            cam.Render();
            var previous = RenderTexture.active; RenderTexture.active = cam.targetTexture;
            var image = new Texture2D(W, H, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, W, H), 0, 0); image.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes(Path.Combine(OutDir, name), image.EncodeToPNG());
            var pixels = image.GetPixels();
            Object.DestroyImmediate(image);
            return pixels;
        }

        // 1. The game as it is: shadow maps, no ray tracing.
        sun.shadows = LightShadows.Soft;
        var raster = Save("test_0_raster.png");
        // 2. Ray traced: shadow maps off, ray-traced sun shadows and AO composited before transparents.
        sun.shadows = LightShadows.None;
        cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, cb);
        Record(0, 1); var raytraced = Save("test_1_raytraced.png");
        var instances = rtas.GetInstanceCount();
        if (instances != 6) throw new System.Exception($"Expected six traceable objects and an excluded effect shell; got {instances}");
        Debug.Log($"JibbersRT test: {instances} instances (the excluded shell is not one of them)");
        // 3. Debug: red is sun occlusion; green is ambient occlusion.
        Record(1, 2); var terms = Save("test_2_debug.png");
        Record(1, 3, 1); Save("test_3_depth.png");
        Record(1, 4, 2); Save("test_4_camera_rays.png");
        cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, cb);
        sun.shadows = LightShadows.Soft;
        var restored = Save("test_5_restored.png");
        int changed = 0, sunPixels = 0, aoPixels = 0; float restoreError = 0;
        for (int i = 0; i < raster.Length; i++)
        {
            if (Mathf.Abs(raster[i].grayscale - raytraced[i].grayscale) > .025f) changed++;
            if (terms[i].r > .15f) sunPixels++;
            if (terms[i].g > .05f) aoPixels++;
            restoreError = Mathf.Max(restoreError, Mathf.Abs(raster[i].grayscale - restored[i].grayscale));
        }
        if (changed < W * H / 100 || sunPixels < 100 || aoPixels < 100 || restoreError > .01f)
            throw new System.Exception($"Render validation failed: changed={changed}, shadow={sunPixels}, AO={aoPixels}, restore error={restoreError}");
        var report = $"PASS DX12 render: {instances} instances, {changed} changed pixels, {sunPixels} shadow pixels, {aoPixels} AO pixels, restore error {restoreError:0.000000}";
        File.WriteAllText(Path.Combine(OutDir, "render-validation.txt"), report + "\n");
        Debug.Log(report);
        rtas.Dispose(); cb.Release(); traced.Release(); blurred.Release();
        Object.DestroyImmediate(traced); Object.DestroyImmediate(blurred);
        for(int i=0;i<protectedAssets.Length;i++)protectedAssets[i].hideFlags=originalFlags[i];
        bundle.Unload(false);
        Debug.Log("JibbersRT test images written to " + OutDir);
    }
}
