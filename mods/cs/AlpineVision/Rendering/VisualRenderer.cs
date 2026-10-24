using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using JibbersAlpineVision.Weather;

namespace JibbersAlpineVision.Rendering;

/// <summary>One owned environment and post-processing stack. All calls are on the Unity main thread.</summary>
internal sealed unsafe class VisualRenderer(Action<string> log) : IDisposable
{
    const int AfterImageEffects=19, CameraTarget=2;
    EngineApi api=null!;
    AssetBundles.Bundle? bundle;
    GameRef? buffer,camera,sky,copy,extract,blurH,blurV,finish,scene,bloomA,bloomB;
    readonly Dictionary<string,Il2CppMethod> methods=new();
    GameRef? oldSky,sun;
    Color oldSunColor,oldAmbientSky,oldAmbientEquator,oldAmbientGround,oldFog;
    float oldSunIntensity;
    int oldAmbientMode,oldClear;
    bool lightingOwned;
    bool captured,failed,attached;
    int width,height,divisor,frames;
    string? error;
    string? appliedProfile;
    int materialRecoveries;
    int runtimeRetries,retryFrames;
    UrpFinish? urp;
    bool builtInBufferAttached;
    public bool Ready=>!failed && buffer is not null;
    public bool IsRendering=>Ready && attached && frames>0;
    public string Status=>$"{(failed?"failed":Ready?"ready":"waiting")}; attached={attached}; frames={frames}; size={width}x{height}"+(error is null?"":"; error="+error);
    // Weather must load independently of optional post-processing API availability.
    public AssetBundles.Bundle Bundle => bundle??=AssetBundles.Load(BundlePath());

    static string BundlePath()
    {
        var packaged=Path.Combine(Bootstrap.GameDir,"JibbersMods/packages/jibbers_atmosphere/jibbers_alpinevision.bundle");
        return File.Exists(packaged)?packaged:Path.Combine(Bootstrap.GameDir,"jibbers_alpinevision.bundle");
    }
    Il2CppMethod Need(string type,string name,params string[] args)
    {
        var key=type+"."+name+"/"+string.Join(",",args);
        if(methods.TryGetValue(key,out var m))return m;
        m=Unity.Class(type).Method(name,args);
        if(m.IsNull)throw new NotSupportedException("Game stripped required visual API: "+key);
        methods.Add(key,m);return m;
    }
    Il2CppMethod Env(string name)=>Need("UnityEngine.RenderSettings",name);
    Il2CppMethod SetEnv(string name,string type)=>Need("UnityEngine.RenderSettings",name,type);
    static GameRef Root(nint ptr)=>Unity.IsNull(ptr)?throw new InvalidDataException("Missing visual asset"):new(ptr);
    GameRef OwnedMaterial(string name)
    {
        var source=Bundle.LoadAsset(name,api.MaterialClass);
        if(Unity.IsNull(source))throw new InvalidDataException("Missing visual material: "+name);
        var instance=api.MaterialClass.New();
        Need("UnityEngine.Material",".ctor","UnityEngine.Material").Invoke(instance,source);
        var root=Root(instance);
        int flags=61; // HideAndDontSave, including DontUnloadUnusedAsset. Explicitly destroyed on unload.
        Need("UnityEngine.Object","set_hideFlags","UnityEngine.HideFlags").Invoke(instance,(nint)(&flags));
        return root;
    }
    static bool Alive(GameRef? root)=>root is not null && !Unity.IsNull(root.Target);
    bool MaterialsAlive()=>Alive(sky)&&Alive(copy)&&Alive(extract)&&Alive(blurH)&&Alive(blurV)&&Alive(finish);
    void LoadMaterials()
    {
        copy=OwnedMaterial("AV_Copy");sky=OwnedMaterial("AV_Sky");extract=OwnedMaterial("AV_Extract");
        blurH=OwnedMaterial("AV_BlurH");blurV=OwnedMaterial("AV_BlurV");finish=OwnedMaterial("AV_Finish");
    }
    void ReleaseMaterials()
    {
        foreach(var root in new[]{sky,copy,extract,blurH,blurV,finish})
        {if(root is null)continue;if(!Unity.IsNull(root.Target))Unity.Destroy(root.Target);root.Dispose();}
        sky=copy=extract=blurH=blurV=finish=null;
    }
    public void SceneChanged()
    {
        if(api is null)return;
        Suspend();
        if(buffer is not null)api.Clear.Invoke(buffer.Target);
        appliedProfile=null;
    }
    void Initialize()
    {
        if(failed)throw new InvalidOperationException(error);
        if(buffer is not null)return;
        api=new EngineApi();
        if(api.Missing.Count>0)throw new NotSupportedException(string.Join("; ",api.Missing));
        // Resolve every additional engine boundary before touching the scene.
        Need("UnityEngine.Rendering.CommandBuffer","SetRenderTarget","UnityEngine.Rendering.RenderTargetIdentifier");
        Need("UnityEngine.Rendering.CommandBuffer","DrawProcedural","UnityEngine.Matrix4x4","UnityEngine.Material","System.Int32","UnityEngine.MeshTopology","System.Int32");
        Env("get_skybox");SetEnv("set_skybox","UnityEngine.Material");
        Need("UnityEngine.Camera","get_clearFlags");Need("UnityEngine.Camera","set_clearFlags","UnityEngine.CameraClearFlags");
        Need("UnityEngine.Material",".ctor","UnityEngine.Material");
        Need("UnityEngine.Object","set_hideFlags","UnityEngine.HideFlags");
        LoadMaterials();
        var cb=api.CommandBufferClass.New();api.BufferCtor.Invoke(cb);buffer=new(cb);
        api.BufferName.Invoke(cb,Il2Cpp.String("Alpine Vision / bloom + film finish"));
        log("Unified visual bundle loaded; sky, weather, native snow and film finish available; no shadow overrides.");
    }
    nint Target(nint texture)=>api.TargetFromTexture.Invoke(0,texture)+2*sizeof(nint);
    nint Builtin(int target)=>api.TargetFromBuiltin.Invoke(0,(nint)(&target))+2*sizeof(nint);
    void Vector(nint mat,string name,Vector4 value)=>Need("UnityEngine.Material","SetVector","System.String","UnityEngine.Vector4").Invoke(mat,Il2Cpp.String(name),(nint)(&value));
    void Global(string name,Vector4 value)=>api.GlobalVector.Invoke(buffer!.Target,Il2Cpp.String(name),(nint)(&value));
    void EnvColor(string name,Color c)=>SetEnv("set_"+name,"UnityEngine.Color").Invoke(0,(nint)(&c));
    Color ReadColor(string name)=>name=="fogColor"?UnitySkyLighting.ReadFogColor():Env("get_"+name).Invoke<Color>(0);

    public void Frame(VisualProfile profile,string quality,float bloom,float sharpness,float exposure,bool grading=true)
    {
        if(failed)return;
        if(retryFrames>0){retryFrames--;return;}
        try
        {
            Initialize();
            if(!MaterialsAlive())
            {
                Suspend();api.Clear.Invoke(buffer!.Target);ReleaseMaterials();LoadMaterials();
                materialRecoveries++;log("Recreated visual materials after scene cleanup; renderer remains active.");
            }
            var go=Unity.Find("WorldCamera");
            var cam=Unity.IsNull(go)?Unity.MainCamera:Unity.GetComponent(go,api.CameraClass);
            if(Unity.IsNull(cam)){Suspend();return;}
            if(camera?.Target!=cam)
            {
                Suspend();camera=new(cam);Capture(cam);
                var pipeline=Need("UnityEngine.Rendering.GraphicsSettings","get_currentRenderPipeline").Invoke(0);
                if(Unity.IsNull(pipeline))
                {
                    int evt=AfterImageEffects;api.AddCommandBuffer.Invoke(cam,(nint)(&evt),buffer!.Target);
                    builtInBufferAttached=true;
                }
                else urp??=new UrpFinish();
                attached=true;
                log("Alpine Vision attached: sky/lighting + weather + post-processing; "+profile.Name);
            }
            ApplyEnvironment(cam,profile);
            var w=api.PixelWidth.Invoke<int>(cam);var h=api.PixelHeight.Invoke<int>(cam);
            if(w<1 || h<1)return;
            if(urp is not null && !builtInBufferAttached)
            {
                urp.Update(cam,Bundle,profile,bloom,sharpness,exposure,grading,quality);
                width=w;height=h;
            }
            else
            {
                EnsureTargets(w,h,quality=="Performance"?4:2);
                Record(profile,bloom,sharpness,exposure,quality,grading);
            }
            frames++;error=null;
            if(appliedProfile!=profile.Name)
            {
                appliedProfile=profile.Name;
                log($"Configured visual style {profile.Name} via {(builtInBufferAttached?"builtin camera buffer":"URP fullscreen feature")}: frame {frames}, {width}x{height}, grading={grading}, owned_materials=6, recoveries={materialRecoveries}.");
            }
        }
        catch(Exception ex)
        {
            error=ex.Message;
            try{Suspend();}catch(Exception cleanup){log("visual cleanup: "+cleanup.Message);}
            if(ex is Il2CppException && runtimeRetries++<3)
            {
                // Scene teardown can invalidate native objects between the liveness check and
                // submission. Retry at a bounded rate; never flood the frame loop with errors.
                Cleanup.Run(()=>{if(buffer is not null)api.Clear.Invoke(buffer.Target);},ReleaseTargets,ReleaseMaterials);
                retryFrames=60;appliedProfile=null;
                log("Visual scene resources invalidated; rebuilding after 60 camera updates (attempt "+runtimeRetries+"/3): "+ex.Message);
            }
            else {failed=true;log("Visual overhaul stopped safely: "+ex);}
        }
    }
    void Capture(nint cam)
    {
        oldClear=Need("UnityEngine.Camera","get_clearFlags").Invoke<int>(cam);
        var prior=Env("get_skybox").Invoke(0);oldSky=Unity.IsNull(prior)?null:new(prior);
        captured=true;
    }
    void RestoreLighting()
    {
        if(!lightingOwned)return;
        var mode=oldAmbientMode;SetEnv("set_ambientMode","UnityEngine.Rendering.AmbientMode").Invoke(0,(nint)(&mode));
        EnvColor("ambientSkyColor",oldAmbientSky);EnvColor("ambientEquatorColor",oldAmbientEquator);
        EnvColor("ambientGroundColor",oldAmbientGround);EnvColor("fogColor",oldFog);
        if(sun is not null && !Unity.IsNull(sun.Target))
        {
            var c=oldSunColor;var intensity=oldSunIntensity;
            Need("UnityEngine.Light","set_color","UnityEngine.Color").Invoke(sun.Target,(nint)(&c));
            Need("UnityEngine.Light","set_intensity","System.Single").Invoke(sun.Target,(nint)(&intensity));
        }
        sun?.Dispose();sun=null;lightingOwned=false;
    }
    void ApplyEnvironment(nint cam,VisualProfile p)
    {
        SetEnv("set_skybox","UnityEngine.Material").Invoke(0,sky!.Target);
        int skyFlag=1;
        Need("UnityEngine.Camera","set_clearFlags","UnityEngine.CameraClearFlags").Invoke(cam,(nint)(&skyFlag));
        var light=api.Sun.Invoke(0);
        if(Unity.IsNull(light))light=Unity.FindObjects(api.LightClass).FirstOrDefault(l=>api.LightType.Invoke<int>(l)==1);
        if(p.Name=="Natural")RestoreLighting();
        else
        {
            if(!lightingOwned)
            {
                oldAmbientMode=Env("get_ambientMode").Invoke<int>(0);
                oldAmbientSky=ReadColor("ambientSkyColor");oldAmbientEquator=ReadColor("ambientEquatorColor");
                oldAmbientGround=ReadColor("ambientGroundColor");oldFog=ReadColor("fogColor");
                lightingOwned=true;
            }
            int tri=1;SetEnv("set_ambientMode","UnityEngine.Rendering.AmbientMode").Invoke(0,(nint)(&tri));
            EnvColor("ambientSkyColor",p.AmbientSky);EnvColor("ambientEquatorColor",new(p.AmbientSky.R*.8f,p.AmbientSky.G*.8f,p.AmbientSky.B*.8f,1));
            EnvColor("ambientGroundColor",p.AmbientGround);EnvColor("fogColor",p.Horizon);
            if(!Unity.IsNull(light))
            {
                if(!Alive(sun) || sun!.Target!=light)
                {
                    sun?.Dispose();
                    sun=new(light);oldSunColor=Need("UnityEngine.Light","get_color").Invoke<Color>(light);
                    oldSunIntensity=Need("UnityEngine.Light","get_intensity").Invoke<float>(light);
                }
                var c=p.Sun;var intensity=p.SunIntensity;
                Need("UnityEngine.Light","set_color","UnityEngine.Color").Invoke(light,(nint)(&c));
                Need("UnityEngine.Light","set_intensity","System.Single").Invoke(light,(nint)(&intensity));
            }
        }
        // Follow the same light the game uses. Never move it to a preset-specific angle,
        // or draw a second disc at ReadSunlight's fallback direction when get_sun is unset.
        var sunAlignment=Unity.IsNull(light)?default:
            SunAlignment.FromLightForward(Need("UnityEngine.Transform","get_forward").Invoke<Vector3>(Unity.Transform(light)));
        Vector(sky.Target,"_AV_Sun",sunAlignment);
        Vector(sky.Target,"_AV_Zenith",new(p.SkyTop.R,p.SkyTop.G,p.SkyTop.B,1));
        var horizon=p.Name=="Natural"?UnitySkyLighting.ReadFogColor():p.Horizon;
        Vector(sky.Target,"_AV_Horizon",new(horizon.R,horizon.G,horizon.B,1));
        Vector(sky.Target,"_AV_Clouds",new(p.Clouds,.006f,0,0));
    }
    nint Create(int w,int h)
    {
        var tex=api.RenderTextureClass.New();int depth=0,format=2;
        api.TextureCtor.Invoke(tex,(nint)(&w),(nint)(&h),(nint)(&depth),(nint)(&format));
        int flags=61;Need("UnityEngine.Object","set_hideFlags","UnityEngine.HideFlags").Invoke(tex,(nint)(&flags));
        if(!api.TextureCreate.Invoke<bool>(tex)){Unity.Destroy(tex);throw new InvalidOperationException("Visual target allocation failed");}
        return tex;
    }
    void EnsureTargets(int w,int h,int div)
    {
        if(w==width && h==height && div==divisor && Alive(scene) && Alive(bloomA) && Alive(bloomB))return;
        api.Clear.Invoke(buffer!.Target);ReleaseTargets();
        scene=new(Create(w,h));bloomA=new(Create(Math.Max(1,w/div),Math.Max(1,h/div)));bloomB=new(Create(Math.Max(1,w/div),Math.Max(1,h/div)));
        width=w;height=h;divisor=div;
    }
    void Blit(nint source,nint dest,nint material)=>api.Blit.Invoke(buffer!.Target,api.BlitTakesTexture?source:Target(source),dest,material);
    void Record(VisualProfile p,float bloom,float sharpness,float ev,string quality,bool grading)
    {
        var cb=buffer!.Target;api.Clear.Invoke(cb);
        // Explicit scene copy prevents sampling and writing the camera target simultaneously.
        api.GlobalTexture.Invoke(cb,Il2Cpp.String("_AV_SceneSource"),Builtin(CameraTarget));
        Need("UnityEngine.Rendering.CommandBuffer","SetRenderTarget","UnityEngine.Rendering.RenderTargetIdentifier").Invoke(cb,Target(scene!.Target));
        var identity=new Matrix4x4();identity.M[0]=identity.M[5]=identity.M[10]=identity.M[15]=1;
        int pass=0,triangles=0,vertices=3;
        Need("UnityEngine.Rendering.CommandBuffer","DrawProcedural","UnityEngine.Matrix4x4","UnityEngine.Material","System.Int32","UnityEngine.MeshTopology","System.Int32")
            .Invoke(cb,(nint)(&identity),copy!.Target,(nint)(&pass),(nint)(&triangles),(nint)(&vertices));
        var film=grading && p.Name!="Natural";
        Global("_AV_Grade",grading?new(p.Exposure*MathF.Pow(2,ev),p.Contrast,p.Saturation,p.Warmth):new(1,1,1,0));
        Global("_AV_Mode",new(film?1:0,0,0,0));
        Global("_AV_Finish",new(grading?bloom:0,sharpness,film?.10f:0,film&&quality!="Performance"?.002f:0));
        Global("_AV_SourceSize",new(1f/width,1f/height,width,height));
        Blit(scene.Target,Target(bloomA!.Target),extract!.Target);
        Blit(bloomA.Target,Target(bloomB!.Target),blurH!.Target);
        Blit(bloomB.Target,Target(bloomA.Target),blurV!.Target);
        api.GlobalTexture.Invoke(cb,Il2Cpp.String("_AV_Bloom"),Target(bloomA.Target));
        Blit(scene.Target,Builtin(CameraTarget),finish!.Target);
    }
    void ReleaseTargets()
    {
        foreach(var target in new[]{scene,bloomA,bloomB})
        {
            if(target is null)continue;
            if(!Unity.IsNull(target.Target)){api.TextureRelease.Invoke(target.Target);Unity.Destroy(target.Target);}target.Dispose();
        }
        scene=bloomA=bloomB=null;width=height=0;
    }
    public void Suspend()
    {
        if(api is null)return;
        if(builtInBufferAttached && camera is not null && !Unity.IsNull(camera.Target))
        {int evt=AfterImageEffects;api.RemoveCommandBuffer.Invoke(camera.Target,(nint)(&evt),buffer!.Target);}
        attached=false;builtInBufferAttached=false;urp?.Detach();
        if(captured)
        {
            var clear=oldClear;
            if(camera is not null && !Unity.IsNull(camera.Target))Need("UnityEngine.Camera","set_clearFlags","UnityEngine.CameraClearFlags").Invoke(camera.Target,(nint)(&clear));
            SetEnv("set_skybox","UnityEngine.Material").Invoke(0,Alive(oldSky)?oldSky!.Target:0);
            captured=false;
        }
        RestoreLighting();oldSky?.Dispose();oldSky=null;camera?.Dispose();camera=null;
    }
    public void Dispose()
    {
        Cleanup.Run(Suspend,()=>{urp?.Dispose();urp=null;},ReleaseTargets,()=>{if(buffer is not null)api.BufferRelease.Invoke(buffer.Target);},
            ReleaseMaterials,()=>{buffer?.Dispose();buffer=null;},
            ()=>{bundle?.Dispose();bundle=null;});
    }
}
