using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using JibbersAlpineVision.Weather;

namespace JibbersAlpineVision.Rendering;

/// <summary>Optional tiled terrain shadow geometry. Native lighting and visible materials stay intact.</summary>
internal sealed unsafe class TerrainShadowProxy(Action<string> log) : IDisposable
{
    sealed class Surface(GameRef terrain,GameRef data,Vector3 position,Vector3 size,int resolution,int shadowMode)
    {
        public readonly GameRef Terrain=terrain,Data=data;
        public readonly Vector3 Position=position,Size=size;
        public readonly int Resolution=resolution,OriginalShadowMode=shadowMode;
        public readonly int Columns=(int)MathF.Ceiling(size.X/64),Rows=(int)MathF.Ceiling(size.Z/64);
        public uint? HeightmapVersion;
    }
    readonly List<Surface> surfaces=new();
    readonly List<GameRef> gameObjects=new(),meshes=new();
    GameRef? material;
    int surfaceIndex,tileIndex,validationFrames;
    bool initialized,ready,active,failed,restoreNeeded;
    float spacing;

    public string Status=>failed?"unavailable; native shadows retained":ready?active?"active":"ready":initialized?"preparing":"idle";

    static nint Create(string type)
    {
        var cls=Unity.Class(type);
        var instance=cls.New();
        cls.Method(".ctor",0).Invoke(instance);
        return instance;
    }
    static void SetVector(nint instance,string setter,Vector3 value)=>Unity.Call(instance,setter,(nint)(&value));
    static void SetInt(nint instance,string setter,int value)=>Unity.Call(instance,setter,(nint)(&value));
    static void SetBool(nint instance,string setter,bool value)=>Unity.Call(instance,setter,(nint)(&value));

    public void Update(bool enabled,float nightBlend,string quality,AssetBundles.Bundle bundle)
    {
        if(!enabled){if(initialized || surfaces.Count>0)Dispose();failed=false;return;}
        if(failed)return;
        try
        {
            var requestedSpacing=quality=="Ultra"?.5f:quality=="Performance"?2:1;
            if(initialized && spacing!=requestedSpacing)Dispose();
            if(!initialized)
            {
                Initialize(bundle,requestedSpacing);
                if(!initialized)return;
            }
            if(surfaces.Any(surface=>Unity.IsNull(surface.Terrain.Target) || Unity.IsNull(surface.Data.Target)))
            {
                Dispose();return;
            }
            if(validationFrames--<=0)
            {
                validationFrames=30;
                if(surfaces.Any(surface=>Unity.Call(surface.Terrain.Target,"get_terrainData")!=surface.Data.Target
                    || Unity.Position(surface.Terrain.Target)!=surface.Position
                    || Unity.Call<Vector3>(surface.Data.Target,"get_size")!=surface.Size
                    || !Unity.Call<bool>(surface.Terrain.Target,"get_enabled")
                    || !Unity.Call<bool>(surface.Terrain.Target,"get_drawHeightmap")
                    || ReadHeightmapVersion(surface.Data.Target)!=surface.HeightmapVersion))
                {
                    Reset();return;
                }
            }
            if(!ready)BuildNextTile();
            SetActive(ready && nightBlend>0);
        }
        catch(Exception exception)
        {
            try{Dispose();}finally{failed=true;}
            log("Efficient terrain shadows unavailable; original shadows retained: "+exception.Message);
        }
    }

    void Initialize(AssetBundles.Bundle bundle,float requestedSpacing)
    {
        var terrainObjects=UnityTerrain.MapTerrains();
        if(terrainObjects.Count==0)return;
        foreach(var terrain in terrainObjects)
        {
            if(!Unity.Call<bool>(terrain,"get_enabled") || !Unity.Call<bool>(terrain,"get_drawHeightmap"))continue;
            var data=Unity.Call(terrain,"get_terrainData");
            if(Unity.IsNull(data))continue;
            var originalShadowMode=Unity.Call<int>(terrain,"get_shadowCastingMode");
            if(originalShadowMode==0)continue;
            var size=Unity.Call<Vector3>(data,"get_size");
            var scale=Unity.Call<Vector3>(Unity.Transform(terrain),"get_lossyScale");
            var rotation=Unity.Call<Quaternion>(Unity.Transform(terrain),"get_rotation");
            if(!float.IsFinite(size.X+size.Y+size.Z) || size.X<=0 || size.Y<=0 || size.Z<=0
                || MathF.Abs(scale.X-1)>.001f || MathF.Abs(scale.Y-1)>.001f || MathF.Abs(scale.Z-1)>.001f
                || MathF.Abs(rotation.X)+MathF.Abs(rotation.Y)+MathF.Abs(rotation.Z)>.001f)
                throw new NotSupportedException("Terrain transform is unsupported");
            var surface=new Surface(new(terrain),new(data),Unity.Position(terrain),size,
                Unity.Call<int>(data,"get_heightmapResolution"),originalShadowMode);
            surfaces.Add(surface);
            surface.HeightmapVersion=ReadHeightmapVersion(data);
        }
        if(surfaces.Count==0)return;
        if(surfaces.Sum(surface=>(long)surface.Columns*surface.Rows)>4096)
            throw new NotSupportedException("Terrain tile budget exceeded");
        var source=bundle.LoadAsset("AV_TerrainShadow",Unity.Class("UnityEngine.Material"));
        if(Unity.IsNull(source))throw new NotSupportedException("Terrain shadow material unavailable");
        material=new(source);
        spacing=requestedSpacing;initialized=true;
        log("Preparing tiled terrain shadow geometry; native terrain remains active until ready.");
    }

    void BuildNextTile()
    {
        if(surfaceIndex>=surfaces.Count)
        {
            ready=true;log($"Terrain shadow geometry ready: {gameObjects.Count} tiles at {spacing:F1} m spacing.");return;
        }
        var surface=surfaces[surfaceIndex];
        var column=tileIndex%surface.Columns;var row=tileIndex/surface.Columns;
        var startX=column*(surface.Resolution-1)/surface.Columns;
        var endX=(column+1)*(surface.Resolution-1)/surface.Columns;
        var startZ=row*(surface.Resolution-1)/surface.Rows;
        var endZ=(row+1)*(surface.Resolution-1)/surface.Rows;
        var width=endX-startX+1;var depth=endZ-startZ+1;
        using var heightArray=new GameRef(Unity.Call(surface.Data.Target,"GetHeights",
            (nint)(&startX),(nint)(&startZ),(nint)(&width),(nint)(&depth)));
        var worldWidth=(float)(endX-startX)/(surface.Resolution-1)*surface.Size.X;
        var worldDepth=(float)(endZ-startZ)/(surface.Resolution-1)*surface.Size.Z;
        var meshData=TerrainShadowMesh.Build(Il2Cpp.ArrayValues<float>(heightArray.Target),width,depth,worldWidth,worldDepth,surface.Size.Y,spacing);
        var mesh=new GameRef(Create("UnityEngine.Mesh"));meshes.Add(mesh);
        SetArray(mesh.Target,"set_vertices",Unity.Class("UnityEngine.Vector3"),meshData.Vertices);
        SetArray(mesh.Target,"set_normals",Unity.Class("UnityEngine.Vector3"),meshData.Normals);
        SetArray(mesh.Target,"set_triangles",Il2Cpp.RequireClass("mscorlib.dll","System.Int32"),meshData.Triangles);
        Unity.Call(mesh.Target,"RecalculateBounds");
        var upload=Il2Cpp.ClassOf(mesh.Target).Method("UploadMeshData",1);
        if(!upload.IsNull){var discardCpuCopy=true;upload.Invoke(mesh.Target,(nint)(&discardCpuCopy));}
        var gameObjectClass=Unity.Class("UnityEngine.GameObject");
        var gameObject=gameObjectClass.New();
        gameObjectClass.Method(".ctor","System.String").Invoke(gameObject,Il2Cpp.String("AlpineVision.TerrainShadow"));
        var ownedObject=new GameRef(gameObject);gameObjects.Add(ownedObject);
        SetBool(gameObject,"SetActive",false);
        var terrainObject=Unity.Call(surface.Terrain.Target,"get_gameObject");
        SetInt(gameObject,"set_layer",Unity.Call<int>(terrainObject,"get_layer"));
        var meshFilter=gameObjectClass.Method("AddComponent","System.Type").Invoke(gameObject,Unity.Class("UnityEngine.MeshFilter").SystemType);
        Unity.Call(meshFilter,"set_sharedMesh",mesh.Target);
        var renderer=gameObjectClass.Method("AddComponent","System.Type").Invoke(gameObject,Unity.Class("UnityEngine.MeshRenderer").SystemType);
        Unity.Call(renderer,"set_sharedMaterial",material!.Target);
        SetInt(renderer,"set_shadowCastingMode",3); // ShadowsOnly: no colour pass or replacement material on the visible terrain.
        SetBool(renderer,"set_receiveShadows",false);
        SetInt(renderer,"set_lightProbeUsage",0);SetInt(renderer,"set_reflectionProbeUsage",0);
        var renderingLayers=Il2Cpp.ClassOf(surface.Terrain.Target).Method("get_renderingLayerMask",0);
        if(!renderingLayers.IsNull)
        {
            var mask=renderingLayers.Invoke<uint>(surface.Terrain.Target);
            Unity.Call(renderer,"set_renderingLayerMask",(nint)(&mask));
        }
        var position=surface.Position+new Vector3((float)startX/(surface.Resolution-1)*surface.Size.X,0,(float)startZ/(surface.Resolution-1)*surface.Size.Z);
        SetVector(Unity.Transform(gameObject),"set_position",position);
        tileIndex++;
        if(tileIndex==surface.Columns*surface.Rows){surfaceIndex++;tileIndex=0;}
    }

    static void SetArray<T>(nint mesh,string setter,Il2CppClass element,T[] values) where T:unmanaged
    {
        using var array=new GameRef(Il2Cpp.NewArray(element,values.Length));
        values.AsSpan().CopyTo(new Span<T>((void*)Il2Cpp.ArrayData(array.Target),values.Length));
        Unity.Call(mesh,setter,array.Target);
    }

    static uint? ReadHeightmapVersion(nint data)
    {
        var getter=Il2Cpp.ClassOf(data).Method("get_heightmapTexture",0);
        if(getter.IsNull)return null;
        var texture=getter.Invoke(data);
        if(Unity.IsNull(texture))return null;
        var version=Il2Cpp.ClassOf(texture).Method("get_updateCount",0);
        return version.IsNull?null:version.Invoke<uint>(texture);
    }

    void SetActive(bool value)
    {
        if(active==value && !(restoreNeeded && !value))return;
        if(!value)
        {
            var restoration=surfaces.Select(surface=>(Action)(()=>
            {
                if(!Unity.IsNull(surface.Terrain.Target))SetInt(surface.Terrain.Target,"set_shadowCastingMode",surface.OriginalShadowMode);
            })).Concat(gameObjects.Select(gameObject=>(Action)(()=>
            {
                if(!Unity.IsNull(gameObject.Target))SetBool(gameObject.Target,"SetActive",false);
            }))).ToArray();
            Cleanup.Run(restoration);
            active=restoreNeeded=false;return;
        }
        if(value)restoreNeeded=true;
        // Switch only after every tile is ready. Restoration always precedes destruction.
        foreach(var surface in surfaces)
            if(!Unity.IsNull(surface.Terrain.Target))SetInt(surface.Terrain.Target,"set_shadowCastingMode",value?0:surface.OriginalShadowMode);
        foreach(var gameObject in gameObjects)
            if(!Unity.IsNull(gameObject.Target))SetBool(gameObject.Target,"SetActive",value);
        active=value;
        if(!value)restoreNeeded=false;
    }

    public void Dispose()
    {
        var actions=new List<Action> {()=>SetActive(false)};
        actions.AddRange(gameObjects.Select(reference=>(Action)(()=>
        {
            try{if(!Unity.IsNull(reference.Target)){SetBool(reference.Target,"SetActive",false);Unity.Destroy(reference.Target);}}
            finally{reference.Dispose();}
        })));
        actions.AddRange(meshes.Select(reference=>(Action)(()=>
        {
            try{if(!Unity.IsNull(reference.Target))Unity.Destroy(reference.Target);}
            finally{reference.Dispose();}
        })));
        actions.AddRange(surfaces.SelectMany(surface=>new Action[]{surface.Terrain.Dispose,surface.Data.Dispose}));
        actions.Add(()=>material?.Dispose());
        try{Cleanup.Run(actions.ToArray());}
        finally
        {
            gameObjects.Clear();meshes.Clear();surfaces.Clear();material=null;
            initialized=ready=active=restoreNeeded=false;surfaceIndex=tileIndex=validationFrames=0;
        }
    }
    public void Reset() { Dispose();failed=false; }
}
