using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace JibbersAlpineVision.Rendering;

/// <summary>
/// Every engine method the mod calls, resolved once by exact parameter types. The game's build strips
/// unused engine code, so nothing is touched until all of them resolve; otherwise the log lists what is
/// missing. tools/verify_raytracing_api.py checks the same list against the game's metadata offline.
/// </summary>
internal sealed class EngineApi
{
    const string Str = "System.String", Int = "System.Int32", UInt = "System.UInt32", Bool = "System.Boolean",
        V3 = "UnityEngine.Vector3", V4 = "UnityEngine.Vector4", M4 = "UnityEngine.Matrix4x4",
        RTI = "UnityEngine.Rendering.RenderTargetIdentifier", RTS = "UnityEngine.Rendering.RayTracingShader",
        RTAS = "UnityEngine.Rendering.RayTracingAccelerationStructure", CS = "UnityEngine.ComputeShader";

    readonly List<string> missing = new();
    public IReadOnlyList<string> Missing => missing;

    public readonly Il2CppMethod DeviceType, SupportsRayTracing, FindKernel;
    public readonly Il2CppMethod BufferCtor, BufferName, Clear, BufferRelease, BuildAccelerationStructure, ShaderPass,
        SetAccelerationStructure, RayMatrix, RayVector, RayTexture, DispatchRays, ComputeMatrix, ComputeVector,
        ComputeTexture, DispatchCompute, GlobalTexture, GlobalVector, Blit;
    public readonly bool BlitTakesTexture;
    public readonly Il2CppMethod AccelerationCtor, AccelerationDispose;
    public readonly Il2CppMethod RendererEnabled, ShadowCasting, SetShadowCasting, SharedMaterial, SetSharedMaterial,
        RenderQueue, MaterialShader, InstanceId, GameObjectOf, GameObjectCtor, AddComponent, GetLayer, SetLayer;
    public readonly Il2CppMethod TerrainDataOf, HeightmapResolution, TerrainSize, GetHeights;
    public readonly Il2CppMethod MeshCtor, IndexFormat, Vertices, Triangles, RecalculateBounds, SharedMesh, SetPosition, Forward;
    public readonly Il2CppMethod TextureCtor, RandomWrite, TextureCreate, TextureRelease, TargetFromTexture, TargetFromBuiltin;
    public readonly Il2CppMethod PixelWidth, PixelHeight, DepthMode, SetDepthMode, Projection, WorldToCamera, CullingMask,
        AddCommandBuffer, RemoveCommandBuffer, GpuProjection, Sun, LightType, Shadows, SetShadows;
    public readonly Il2CppClass ColliderClass, CameraClass, MeshRendererClass, SkinnedRendererClass, MeshFilterClass,
        TerrainClass, LightClass, RenderTextureClass, AccelerationClass, CommandBufferClass, RayTracingShaderClass,
        ComputeShaderClass, MaterialClass, Vector3Class, IntClass;

    Il2CppClass Type(string name, string? image = null)
    {
        try { return image is null ? Unity.Class(name) : Il2Cpp.RequireClass(image, name); }
        catch (Exception) { missing.Add("class " + name); return default; }
    }

    Il2CppMethod Exact(Il2CppClass type, string name, params string[] parameters)
    {
        if (type.IsNull) return default;
        var method = type.Method(name, parameters);
        if (method.IsNull) missing.Add($"{type}.{name}({string.Join(", ", parameters)})");
        return method;
    }

    public EngineApi()
    {
        var systemInfo = Type("UnityEngine.SystemInfo");
        DeviceType = Exact(systemInfo, "get_graphicsDeviceType");
        SupportsRayTracing = Exact(systemInfo, "get_supportsRayTracing");
        ComputeShaderClass = Type(CS);
        FindKernel = Exact(ComputeShaderClass, "FindKernel", Str);
        RayTracingShaderClass = Type(RTS);
        MaterialClass = Type("UnityEngine.Material");

        CommandBufferClass = Type("UnityEngine.Rendering.CommandBuffer");
        var cb = CommandBufferClass;
        BufferCtor = Exact(cb, ".ctor");
        BufferName = Exact(cb, "set_name", Str);
        Clear = Exact(cb, "Clear");
        BufferRelease = Exact(cb, "Release");
        BuildAccelerationStructure = Exact(cb, "BuildRayTracingAccelerationStructure", RTAS);
        ShaderPass = Exact(cb, "SetRayTracingShaderPass", RTS, Str);
        SetAccelerationStructure = Exact(cb, "SetRayTracingAccelerationStructure", RTS, Str, RTAS);
        RayMatrix = Exact(cb, "SetRayTracingMatrixParam", RTS, Str, M4);
        RayVector = Exact(cb, "SetRayTracingVectorParam", RTS, Str, V4);
        RayTexture = Exact(cb, "SetRayTracingTextureParam", RTS, Str, RTI);
        DispatchRays = Exact(cb, "DispatchRays", RTS, Str, UInt, UInt, UInt, "UnityEngine.Camera");
        ComputeMatrix = Exact(cb, "SetComputeMatrixParam", CS, Str, M4);
        ComputeVector = Exact(cb, "SetComputeVectorParam", CS, Str, V4);
        ComputeTexture = Exact(cb, "SetComputeTextureParam", CS, Int, Str, RTI);
        DispatchCompute = Exact(cb, "DispatchCompute", CS, Int, Int, Int, Int);
        GlobalTexture = Exact(cb, "SetGlobalTexture", Str, RTI);
        GlobalVector = Exact(cb, "SetGlobalVector", Str, V4);
        // Only Blit(source, dest, material) survived; its source is either a Texture or a target id.
        if (!cb.IsNull)
        {
            Blit = cb.Method("Blit", "UnityEngine.Texture", RTI, "UnityEngine.Material");
            BlitTakesTexture = !Blit.IsNull;
            if (Blit.IsNull) Blit = cb.Method("Blit", RTI, RTI, "UnityEngine.Material");
            if (Blit.IsNull) missing.Add("CommandBuffer.Blit(source, dest, Material)");
        }

        AccelerationClass = Type(RTAS);
        AccelerationCtor = AccelerationClass.IsNull ? default : AccelerationClass.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.ParameterCount == 1);
        if (AccelerationCtor.IsNull) missing.Add("RayTracingAccelerationStructure(Settings)");
        AccelerationDispose = Exact(AccelerationClass, "Dispose");

        var renderer = Type("UnityEngine.Renderer");
        RendererEnabled = Exact(renderer, "get_enabled");
        ShadowCasting = Exact(renderer, "get_shadowCastingMode");
        SetShadowCasting = Exact(renderer, "set_shadowCastingMode", "UnityEngine.Rendering.ShadowCastingMode");
        SharedMaterial = Exact(renderer, "get_sharedMaterial");
        SetSharedMaterial = Exact(renderer, "set_sharedMaterial", "UnityEngine.Material");
        RenderQueue = Exact(MaterialClass, "get_renderQueue");
        MaterialShader = Exact(MaterialClass, "get_shader");
        InstanceId = Exact(Type("UnityEngine.Object"), "GetInstanceID");
        GameObjectOf = Exact(Type("UnityEngine.Component"), "get_gameObject");
        var gameObject = Type("UnityEngine.GameObject");
        GameObjectCtor = Exact(gameObject, ".ctor", Str);
        AddComponent = Exact(gameObject, "AddComponent", "System.Type");
        GetLayer = Exact(gameObject, "get_layer");
        SetLayer = Exact(gameObject, "set_layer", Int);
        ColliderClass = Type("UnityEngine.Collider");
        CameraClass = Type("UnityEngine.Camera");
        MeshRendererClass = Type("UnityEngine.MeshRenderer");
        SkinnedRendererClass = Type("UnityEngine.SkinnedMeshRenderer");
        MeshFilterClass = Type("UnityEngine.MeshFilter");
        LightClass = Type("UnityEngine.Light");
        Vector3Class = Type(V3);
        IntClass = Type("System.Int32", "mscorlib.dll");

        TerrainClass = Type("UnityEngine.Terrain", "UnityEngine.TerrainModule.dll");
        TerrainDataOf = Exact(TerrainClass, "get_terrainData");
        var terrainData = Type("UnityEngine.TerrainData", "UnityEngine.TerrainModule.dll");
        HeightmapResolution = Exact(terrainData, "get_heightmapResolution");
        TerrainSize = Exact(terrainData, "get_size");
        GetHeights = Exact(terrainData, "GetHeights", Int, Int, Int, Int);

        var mesh = Type("UnityEngine.Mesh");
        MeshCtor = Exact(mesh, ".ctor");
        IndexFormat = Exact(mesh, "set_indexFormat", "UnityEngine.Rendering.IndexFormat");
        Vertices = Exact(mesh, "set_vertices", "UnityEngine.Vector3[]");
        Triangles = Exact(mesh, "set_triangles", "System.Int32[]");
        RecalculateBounds = Exact(mesh, "RecalculateBounds");
        SharedMesh = Exact(MeshFilterClass, "set_sharedMesh", "UnityEngine.Mesh");
        var transform = Type("UnityEngine.Transform");
        SetPosition = Exact(transform, "set_position", V3);
        Forward = Exact(transform, "get_forward");

        RenderTextureClass = Type("UnityEngine.RenderTexture");
        TextureCtor = Exact(RenderTextureClass, ".ctor", Int, Int, Int, "UnityEngine.RenderTextureFormat");
        RandomWrite = Exact(RenderTextureClass, "set_enableRandomWrite", Bool);
        TextureCreate = Exact(RenderTextureClass, "Create");
        TextureRelease = Exact(RenderTextureClass, "Release");
        var target = Type(RTI);
        TargetFromTexture = Exact(target, "op_Implicit", "UnityEngine.Texture");
        TargetFromBuiltin = Exact(target, "op_Implicit", "UnityEngine.Rendering.BuiltinRenderTextureType");

        PixelWidth = Exact(CameraClass, "get_pixelWidth");
        PixelHeight = Exact(CameraClass, "get_pixelHeight");
        DepthMode = Exact(CameraClass, "get_depthTextureMode");
        SetDepthMode = Exact(CameraClass, "set_depthTextureMode", "UnityEngine.DepthTextureMode");
        Projection = Exact(CameraClass, "get_projectionMatrix");
        WorldToCamera = Exact(CameraClass, "get_worldToCameraMatrix");
        CullingMask = Exact(CameraClass, "get_cullingMask");
        AddCommandBuffer = Exact(CameraClass, "AddCommandBuffer", "UnityEngine.Rendering.CameraEvent", "UnityEngine.Rendering.CommandBuffer");
        RemoveCommandBuffer = Exact(CameraClass, "RemoveCommandBuffer", "UnityEngine.Rendering.CameraEvent", "UnityEngine.Rendering.CommandBuffer");
        GpuProjection = Exact(Type("UnityEngine.GL"), "GetGPUProjectionMatrix", M4, Bool);
        Sun = Exact(Type("UnityEngine.RenderSettings"), "get_sun");
        LightType = Exact(LightClass, "get_type");
        Shadows = Exact(LightClass, "get_shadows");
        SetShadows = Exact(LightClass, "set_shadows", "UnityEngine.LightShadows");
    }
}
