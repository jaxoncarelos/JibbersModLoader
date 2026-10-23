using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace JibbersAlpineVision.Rendering;

/// <summary>
/// The engine side, on the main thread. The game's build keeps only an automatically managed
/// acceleration structure, so Unity gathers the scene's renderers itself; the mod moves what must not
/// block rays (effect shells, transparents, non-shadow-casters without colliders) to a spare layer the
/// structure's layer mask leaves out, and rebuilds the terrain as an invisible proxy mesh (terrains
/// cannot be ray traced). Each frame it records a camera command buffer: dispatch rays at reduced
/// resolution, blur, and multiply the result into the lit image before transparents and fog. The editor
/// test in mods/JibbersRayTracing renders this exact recipe on Direct3D 12.
/// </summary>
internal sealed unsafe class RayTracer(Action<string> log, AssetBundles.Bundle sharedBundle) : IDisposable
{
    const int Direct3D12 = 18, BeforeForwardAlpha = 16, BuiltinCameraTarget = 2, BuiltinDepth = 3, RGHalf = 13;
    const int ProxyCellsPerSide = 512, RefreshFrames = 240;

    enum Stage { Waiting, Unsupported, Ready, Failed }
    Stage stage;
    EngineApi api = null!;
    AssetBundles.Bundle? bundle;
    GameRef? raytrace, blur, composite, debugMaterial, proxyMaterial, commandBuffer, accelerationStructure, traced, blurred;
    GameRef? attachedCamera, sunLight;
    readonly List<(GameRef Asset,int Flags)> pinnedAssets=new();
    Il2CppMethod setHideFlags;
    int? originalSunShadows;
    int blurKernel, traceWidth, traceHeight, framesUntilRefresh, frame, excludedLayer = -1, lastReport = -1;
    readonly List<GameRef> proxies = new();
    readonly List<GameRef> proxyMeshes = new();
    readonly Dictionary<nint, (GameRef GameObject, int Layer)> movedLayers = new();
    string proxyKey = "";
    string? error;
    public bool HardwareReady => stage == Stage.Ready;

    public string Status => $"{stage}; attached={attachedCamera is not null}; recorded_frames={frame}; trace={traceWidth}x{traceHeight}; terrain_proxies={proxies.Count}; excluded_objects={movedLayers.Count}" + (error is null ? "" : "; error=" + error);

    // The SDK bypasses hooks when the mod is disabled, so settings must call this explicitly.
    public void Suspend()
    {
        if (api is null) return;
        if (attachedCamera is null && sunLight is null && movedLayers.Count == 0) return;
        Cleanup(Detach, RestoreLayers);
        framesUntilRefresh = 0;
    }

    public void SceneChanged() { framesUntilRefresh = 0; proxyKey = ""; }

    public void Frame(bool on, TraceSettings settings)
    {
        if (stage is Stage.Unsupported or Stage.Failed) return;
        try
        {
            if (!on) { Suspend(); return; }
            if (stage == Stage.Waiting) Initialize();
            if (stage != Stage.Ready) return;
            var camera = FindCamera();
            if (camera == 0) { Suspend(); return; }
            if (accelerationStructure is null) CreateAccelerationStructure(camera);
            if (framesUntilRefresh-- <= 0) { RefreshScene(); framesUntilRefresh = RefreshFrames; }
            Attach(camera);
            Record(camera, settings);
        }
        catch (Exception ex)
        {
            stage = Stage.Failed;
            error = ex.Message;
            log("Ray tracing disabled after an error: " + ex);
            Cleanup(Detach, RestoreLayers, DestroyProxies, ReleaseTargets);
        }
    }

    // ---- Startup ----

    void Initialize()
    {
        api = new EngineApi();
        if (api.Missing.Count > 0)
        {
            stage = Stage.Unsupported;
            error = "Missing engine APIs: " + string.Join("; ", api.Missing);
            log("This game build lacks engine methods ray tracing needs; nothing was changed. Missing: " + string.Join("; ", api.Missing));
            return;
        }
        var device = api.DeviceType.Invoke<int>(0);
        if (device != Direct3D12)
        {
            stage = Stage.Unsupported;
            error = "Direct3D 12 required; current API " + device;
            log($"The game is running on graphics API {device}, not Direct3D 12 (18). Add -force-d3d12 to the game's Steam launch options and restart.");
            return;
        }
        if (!api.SupportsRayTracing.Invoke<bool>(0))
        {
            stage = Stage.Unsupported;
            error = "GPU/driver does not report ray tracing support";
            log("Direct3D 12 is active but Unity reports no ray-tracing support on this GPU/driver.");
            return;
        }
        setHideFlags=Unity.Class("UnityEngine.Object").Method("set_hideFlags","UnityEngine.HideFlags");
        if(setHideFlags.IsNull)throw new NotSupportedException("Missing Object.set_hideFlags for ray-tracing asset lifetime");
        var path = "jibbers_alpinevision.bundle";
        bundle = sharedBundle;
        raytrace = Keep(bundle.LoadAsset("JibbersRT", api.RayTracingShaderClass), "ray tracing shader");
        blur = Keep(bundle.LoadAsset("JibbersRTBlur", api.ComputeShaderClass), "blur shader");
        composite = Keep(bundle.LoadAsset("JRT_Composite", api.MaterialClass), "composite material");
        debugMaterial = Keep(bundle.LoadAsset("JRT_Debug", api.MaterialClass), "debug material");
        proxyMaterial = Keep(bundle.LoadAsset("JRT_Proxy", api.MaterialClass), "proxy material");
        blurKernel = api.FindKernel.Invoke<int>(blur.Target, Il2Cpp.String("Blur"));
        var buffer = api.CommandBufferClass.New();
        api.BufferCtor.Invoke(buffer);
        api.BufferName.Invoke(buffer, Il2Cpp.String("Jibbers ray tracing"));
        commandBuffer = new GameRef(buffer);
        stage = Stage.Ready;
        log($"Direct3D 12 with ray tracing; all {nameof(EngineApi)} methods present. Loaded {Path.GetFileName(path)}.");
    }

    GameRef Keep(nint asset, string what)
    {
        if(Unity.IsNull(asset))throw new InvalidOperationException("bundle has no " + what);
        var root=new GameRef(asset);
        var flags=Unity.Class("UnityEngine.Object").Method("get_hideFlags").Invoke<int>(asset);
        var pinned=flags|32; // DontUnloadUnusedAsset: managed GC handles do not preserve native assets.
        setHideFlags.Invoke(asset,(nint)(&pinned));
        pinnedAssets.Add((root,flags));return root;
    }
    void UnpinAssets()
    {
        foreach(var (asset,original) in pinnedAssets)
        {
            if(Unity.IsNull(asset.Target))continue;
            var flags=original;setHideFlags.Invoke(asset.Target,(nint)(&flags));
        }
        pinnedAssets.Clear();
    }

    nint FindCamera()
    {
        var cameraObject = Unity.Find("WorldCamera");
        var camera = Unity.IsNull(cameraObject) ? Unity.MainCamera : Unity.GetComponent(cameraObject, api.CameraClass);
        return Unity.IsNull(camera) ? 0 : camera;
    }

    /// <summary>RayTracingAccelerationStructure.Settings, field for field (Unity 6000.3).</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct AccelerationSettings
    {
        public int ManagementMode;      // 1 Automatic: AddInstance(Renderer) was stripped from the game
        public int RayTracingModeMask;  // 30 Everything
        public int LayerMask;
        public int StaticBuildFlags;    // 1 PreferFastTrace
        public int DynamicBuildFlags;   // 2 PreferFastBuild
        public byte EnableCompaction;
    }

    /// <summary>
    /// The spare layer: one the camera draws (so moved objects still render) that no renderer uses yet.
    /// The acceleration structure's layer mask leaves it out.
    /// </summary>
    void CreateAccelerationStructure(nint camera)
    {
        var cullingMask = api.CullingMask.Invoke<int>(camera);
        var used = new HashSet<int>();
        foreach (var renderer in AllRenderers())
            used.Add(api.GetLayer.Invoke<int>(api.GameObjectOf.Invoke(renderer)));
        for (var layer = 31; layer >= 8 && excludedLayer < 0; layer--)
            if ((cullingMask & (1 << layer)) != 0 && !used.Contains(layer)) excludedLayer = layer;
        var settings = new AccelerationSettings
        {
            ManagementMode = 1, RayTracingModeMask = 30, StaticBuildFlags = 1, DynamicBuildFlags = 2,
            LayerMask = excludedLayer >= 0 ? ~(1 << excludedLayer) : -1,
        };
        var instance = api.AccelerationClass.New();
        api.AccelerationCtor.Invoke(instance, (nint)(&settings));
        var native = api.AccelerationClass.Field("m_Ptr");
        if (!native.IsNull && native.Get<nint>(instance) == 0)
            throw new InvalidOperationException("the acceleration structure has no native object");
        accelerationStructure = new GameRef(instance);
        log(excludedLayer >= 0
            ? $"Acceleration structure ready; effects are excluded on spare layer {excludedLayer}."
            : "Acceleration structure ready; no spare layer the camera draws, so nothing can be excluded.");
    }

    // ---- Scene: what rays can hit ----

    IEnumerable<nint> AllRenderers() =>
        Unity.FindObjects(api.MeshRendererClass).Concat(Unity.FindObjects(api.SkinnedRendererClass)).Where(r => !Unity.IsNull(r));

    void RefreshScene()
    {
        BuildTerrainProxies();
        if (excludedLayer < 0) return;
        var proxyObjects = proxies.Select(p => p.Target).ToHashSet();
        int traced = 0, excluded = 0, kept = 0;
        foreach (var renderer in AllRenderers())
        {
            var gameObject = api.GameObjectOf.Invoke(renderer);
            if (proxyObjects.Contains(gameObject) || Traceable(renderer)) { traced++; continue; }
            var layer = api.GetLayer.Invoke<int>(gameObject);
            if (layer == excludedLayer) { excluded++; continue; }
            // Never move anything with a collider: its layer can matter to physics.
            if (!Unity.IsNull(Unity.GetComponent(gameObject, api.ColliderClass))) { kept++; continue; }
            movedLayers[gameObject] = (new GameRef(gameObject), layer);
            var spare = excludedLayer;
            api.SetLayer.Invoke(gameObject, (nint)(&spare));
            excluded++;
        }
        if (traced + excluded * 1000 + kept * 1000000 != lastReport)
            log($"Ray tracing {traced} objects ({proxies.Count} terrain proxies); {excluded} effects/transparents excluded; {kept} transparent objects with colliders kept.");
        lastReport = traced + excluded * 1000 + kept * 1000000;
    }

    /// <summary>Only what casts the game's shadows, opaque, and not an effect shell (Hidden/ shaders).</summary>
    bool Traceable(nint renderer)
    {
        if (!api.RendererEnabled.Invoke<bool>(renderer)) return false;
        if (api.ShadowCasting.Invoke<int>(renderer) == 0) return false;
        var material = api.SharedMaterial.Invoke(renderer);
        if (Unity.IsNull(material) || api.RenderQueue.Invoke<int>(material) >= 2500) return false;
        var shader = api.MaterialShader.Invoke(material);
        return !Unity.IsNull(shader) && !Unity.Name(shader).StartsWith("Hidden/", StringComparison.Ordinal);
    }

    /// <summary>Unity terrains are not ray-traceable: rebuild each one's heightmap as an invisible mesh.</summary>
    void BuildTerrainProxies()
    {
        var terrains = Unity.FindObjects(api.TerrainClass).Where(t => !Unity.IsNull(t)).ToArray();
        var key = string.Join(",", terrains.Select(t => api.InstanceId.Invoke<int>(t)));
        if (key == proxyKey && proxies.All(p => !Unity.IsNull(p.Target))) return;
        DestroyProxies();
        proxyKey = key;
        foreach (var terrain in terrains)
        {
            var data = api.TerrainDataOf.Invoke(terrain);
            var resolution = api.HeightmapResolution.Invoke<int>(data);
            var size = api.TerrainSize.Invoke<Vector3>(data);
            int zero = 0;
            var heights = api.GetHeights.Invoke(data, (nint)(&zero), (nint)(&zero), (nint)(&resolution), (nint)(&resolution));
            var samples = (float*)Il2Cpp.ArrayData(heights);   // [z, x], 0..1 of the terrain height
            var step = Math.Max(1, (resolution - 1 + ProxyCellsPerSide - 1) / ProxyCellsPerSide);
            var side = (resolution - 2 + step) / step + 1;
            var vertices = Il2Cpp.NewArray(api.Vector3Class, side * side);
            var v = (Vector3*)Il2Cpp.ArrayData(vertices);
            for (var z = 0; z < side; z++)
                for (var x = 0; x < side; x++)
                {
                    var sx = Math.Min(x * step, resolution - 1);
                    var sz = Math.Min(z * step, resolution - 1);
                    v[z * side + x] = new Vector3(sx * size.X / (resolution - 1),
                        samples[sz * resolution + sx] * size.Y, sz * size.Z / (resolution - 1));
                }
            var triangles = Il2Cpp.NewArray(api.IntClass, (side - 1) * (side - 1) * 6);
            var t = (int*)Il2Cpp.ArrayData(triangles);
            var n = 0;
            for (var z = 0; z < side - 1; z++)
                for (var x = 0; x < side - 1; x++)
                {
                    int a = z * side + x, b = a + 1, c = a + side, d = c + 1;
                    t[n++] = a; t[n++] = c; t[n++] = b; t[n++] = b; t[n++] = c; t[n++] = d;
                }
            var mesh = Unity.Class("UnityEngine.Mesh").New();
            api.MeshCtor.Invoke(mesh);
            proxyMeshes.Add(new GameRef(mesh));
            var uint32 = 1;
            api.IndexFormat.Invoke(mesh, (nint)(&uint32));
            api.Vertices.Invoke(mesh, vertices);
            api.Triangles.Invoke(mesh, triangles);
            api.RecalculateBounds.Invoke(mesh);
            var proxy = Unity.Class("UnityEngine.GameObject").New();
            api.GameObjectCtor.Invoke(proxy, Il2Cpp.String("JibbersRT terrain proxy"));
            proxies.Add(new GameRef(proxy));
            var filter = api.AddComponent.Invoke(proxy, api.MeshFilterClass.SystemType);
            api.SharedMesh.Invoke(filter, mesh);
            var renderer = api.AddComponent.Invoke(proxy, api.MeshRendererClass.SystemType);
            api.SetSharedMaterial.Invoke(renderer, proxyMaterial!.Target);
            var off = 0;
            api.SetShadowCasting.Invoke(renderer, (nint)(&off));
            var position = Unity.Position(terrain);
            api.SetPosition.Invoke(Unity.Transform(proxy), (nint)(&position));
            log($"Terrain proxy: {side}x{side} vertices from a {resolution}x{resolution} heightmap, {size.X:0}x{size.Z:0} m.");
        }
    }

    void DestroyProxies()
    {
        foreach (var proxy in proxies)
        {
            if (!Unity.IsNull(proxy.Target)) Unity.Destroy(proxy.Target);
            proxy.Dispose();
        }
        proxies.Clear();
        foreach (var mesh in proxyMeshes)
        {
            if (!Unity.IsNull(mesh.Target)) Unity.Destroy(mesh.Target);
            mesh.Dispose();
        }
        proxyMeshes.Clear();
    }

    void RestoreLayers()
    {
        foreach (var (gameObject, layer) in movedLayers.Values)
        {
            var original = layer;
            if (!Unity.IsNull(gameObject.Target)) api.SetLayer.Invoke(gameObject.Target, (nint)(&original));
            gameObject.Dispose();
        }
        movedLayers.Clear();
    }

    // ---- Per frame: record the camera's command buffer ----

    void EnsureTargets(int width, int height)
    {
        if (width == traceWidth && height == traceHeight && traced is not null) return;
        ReleaseTargets();
        traced = new GameRef(CreateTarget(width, height));
        blurred = new GameRef(CreateTarget(width, height));
        (traceWidth, traceHeight) = (width, height);
    }

    nint CreateTarget(int width, int height)
    {
        var texture = api.RenderTextureClass.New();
        int depthBits = 0, format = RGHalf;
        api.TextureCtor.Invoke(texture, (nint)(&width), (nint)(&height), (nint)(&depthBits), (nint)(&format));
        int flags=61;setHideFlags.Invoke(texture,(nint)(&flags));
        var on = true;
        api.RandomWrite.Invoke(texture, (nint)(&on));
        if (!api.TextureCreate.Invoke<bool>(texture))
        {
            Unity.Destroy(texture);
            throw new InvalidOperationException($"Cannot allocate ray tracing target {width}x{height}");
        }
        return texture;
    }

    void ReleaseTargets()
    {
        foreach (var target in new[] { traced, blurred })
        {
            if (target is null) continue;
            if (!Unity.IsNull(target.Target)) { api.TextureRelease.Invoke(target.Target); Unity.Destroy(target.Target); }
            target.Dispose();
        }
        traced = blurred = null;
        traceWidth = traceHeight = 0;
    }

    /// <summary>A RenderTargetIdentifier, passed by pointer to its boxed value (after the 16-byte object header).</summary>
    static nint Unboxed(nint boxed) => boxed + 2 * sizeof(nint);
    nint TargetOf(nint texture) => Unboxed(api.TargetFromTexture.Invoke(0, texture));
    nint TargetOf(int builtin) => Unboxed(api.TargetFromBuiltin.Invoke(0, (nint)(&builtin)));

    void Record(nint camera, TraceSettings settings)
    {
        var width = api.PixelWidth.Invoke<int>(camera);
        var height = api.PixelHeight.Invoke<int>(camera);
        int traceW = Math.Max(8, (int)(width * settings.ResolutionScale)), traceH = Math.Max(8, (int)(height * settings.ResolutionScale));
        EnsureTargets(traceW, traceH);
        var depthMode = api.DepthMode.Invoke<int>(camera);
        if ((depthMode & 1) == 0) { depthMode |= 1; api.SetDepthMode.Invoke(camera, (nint)(&depthMode)); }

        var projection = api.Projection.Invoke<Matrix4x4>(camera);
        var view = api.WorldToCamera.Invoke<Matrix4x4>(camera);
        var flip = false;
        var gpuProjection = api.GpuProjection.Invoke<Matrix4x4>(0, (nint)(&projection), (nint)(&flip));
        var inverseViewProjection = CameraMath.InverseViewProjection(gpuProjection, view);
        var cameraPosition = Unity.Position(camera);
        var toSun = SunDirection();
        var cameraPos = new Vector4(cameraPosition.X, cameraPosition.Y, cameraPosition.Z, 0);
        var sun = new Vector4(toSun.X, toSun.Y, toSun.Z, Math.Max(.0005f, settings.SunConeRadians));
        var parameters = new Vector4(settings.OcclusionRadius, ++frame, 4000, settings.OcclusionRays);
        var size = new Vector4(traceW, traceH, width, height);
        var debug = new Vector4(0, 0, 0, 0);
        var compositeParameters = new Vector4(settings.ShadowStrength, settings.AmbientOcclusion, 0, 0);

        var cb = commandBuffer!.Target; var rt = raytrace!.Target; var cs = blur!.Target; var rtas = accelerationStructure!.Target;
        nint S(string s) => Il2Cpp.String(s);
        api.Clear.Invoke(cb);
        api.BuildAccelerationStructure.Invoke(cb, rtas);
        api.ShaderPass.Invoke(cb, rt, S("JibbersRT"));
        api.SetAccelerationStructure.Invoke(cb, rt, S("_RT_Scene"), rtas);
        api.RayMatrix.Invoke(cb, rt, S("_RT_InvViewProj"), (nint)(&inverseViewProjection));
        api.RayVector.Invoke(cb, rt, S("_RT_CameraPos"), (nint)(&cameraPos));
        api.RayVector.Invoke(cb, rt, S("_RT_SunDir"), (nint)(&sun));
        api.RayVector.Invoke(cb, rt, S("_RT_Params"), (nint)(&parameters));
        api.RayVector.Invoke(cb, rt, S("_RT_Size"), (nint)(&size));
        api.RayVector.Invoke(cb, rt, S("_RT_Debug"), (nint)(&debug));
        api.RayTexture.Invoke(cb, rt, S("_CameraDepthTexture"), TargetOf(BuiltinDepth));
        api.RayTexture.Invoke(cb, rt, S("_RT_Out"), TargetOf(traced!.Target));
        uint w = (uint)traceW, h = (uint)traceH, one = 1;
        api.DispatchRays.Invoke(cb, rt, S("Trace"), (nint)(&w), (nint)(&h), (nint)(&one), camera);

        var kernel = blurKernel;
        api.ComputeMatrix.Invoke(cb, cs, S("_RT_InvViewProj"), (nint)(&inverseViewProjection));
        api.ComputeVector.Invoke(cb, cs, S("_RT_CameraPos"), (nint)(&cameraPos));
        api.ComputeVector.Invoke(cb, cs, S("_RT_Size"), (nint)(&size));
        api.ComputeTexture.Invoke(cb, cs, (nint)(&kernel), S("_CameraDepthTexture"), TargetOf(BuiltinDepth));
        api.ComputeTexture.Invoke(cb, cs, (nint)(&kernel), S("_RT_In"), TargetOf(traced.Target));
        api.ComputeTexture.Invoke(cb, cs, (nint)(&kernel), S("_RT_Blurred"), TargetOf(blurred!.Target));
        int groupsX = (traceW + 7) / 8, groupsY = (traceH + 7) / 8, groupsZ = 1;
        api.DispatchCompute.Invoke(cb, cs, (nint)(&kernel), (nint)(&groupsX), (nint)(&groupsY), (nint)(&groupsZ));

        api.GlobalTexture.Invoke(cb, S("_RT_Result"), TargetOf(blurred.Target));
        api.GlobalVector.Invoke(cb, S("_RT_Composite"), (nint)(&compositeParameters));
        // Only Blit(source, dest, material) survived, which draws every pass: one single-pass material per view.
        var material = settings.DebugView ? debugMaterial!.Target : composite!.Target;
        var source = api.BlitTakesTexture ? blurred.Target : TargetOf(blurred.Target);
        api.Blit.Invoke(cb, source, TargetOf(BuiltinCameraTarget), material);
    }

    Vector3 SunDirection()
    {
        var sun = api.Sun.Invoke(0);
        if (Unity.IsNull(sun))
            sun = Unity.FindObjects(api.LightClass).FirstOrDefault(light => api.LightType.Invoke<int>(light) == 1);
        if (Unity.IsNull(sun)) return new Vector3(.3f, .8f, .2f);
        if (sunLight?.Target != sun)
        {
            RestoreSunShadows();
            sunLight = new GameRef(sun);
        }
        if (originalSunShadows is null)
        {
            // Ray-traced shadows replace the sun's shadow maps, so nothing is shadowed twice.
            originalSunShadows = api.Shadows.Invoke<int>(sun);
            var none = 0;
            api.SetShadows.Invoke(sun, (nint)(&none));
        }
        var forward = api.Forward.Invoke<Vector3>(Unity.Transform(sun));
        return new Vector3(-forward.X, -forward.Y, -forward.Z);
    }

    void RestoreSunShadows()
    {
        if (sunLight is not null && originalSunShadows is { } shadows && !Unity.IsNull(sunLight.Target))
            api.SetShadows.Invoke(sunLight.Target, (nint)(&shadows));
        originalSunShadows = null;
        sunLight?.Dispose(); sunLight = null;
    }

    void Attach(nint camera)
    {
        if (attachedCamera?.Target == camera) return;
        Detach();
        var cameraEvent = BeforeForwardAlpha;
        api.AddCommandBuffer.Invoke(camera, (nint)(&cameraEvent), commandBuffer!.Target);
        attachedCamera = new GameRef(camera);
        log("Hardware ray tracing attached to " + Unity.Name(camera) + "; controlled by Alpine Vision.");
    }

    void Detach()
    {
        if (attachedCamera is not null)
        {
            if (!Unity.IsNull(attachedCamera.Target) && commandBuffer is not null)
            {
                var cameraEvent = BeforeForwardAlpha;
                api.RemoveCommandBuffer.Invoke(attachedCamera.Target, (nint)(&cameraEvent), commandBuffer.Target);
            }
            attachedCamera.Dispose(); attachedCamera = null;
        }
        RestoreSunShadows();
    }

    public void Dispose()
    {
        if (api is not null)
            Cleanup(Detach, RestoreLayers, DestroyProxies, ReleaseTargets,
                () => { if (accelerationStructure is { } a && !Unity.IsNull(a.Target)) api.AccelerationDispose.Invoke(a.Target); },
                () => { if (commandBuffer is { } c && !Unity.IsNull(c.Target)) api.BufferRelease.Invoke(c.Target); },
                UnpinAssets);
        foreach (var handle in new[] { raytrace, blur, composite, debugMaterial, proxyMaterial, commandBuffer, accelerationStructure }) handle?.Dispose();
        stage = Stage.Failed;
    }

    static void Cleanup(params Action[] steps)
    {
        foreach (var step in steps)
            try { step(); } catch (Exception) { /* keep releasing the rest */ }
    }
}
