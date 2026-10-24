using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using Object=UnityEngine.Object;

public static class BuildAlpineVision
{
    const string Gen="Assets/Gen";
    static bool Mac=>Environment.GetEnvironmentVariable("JIBBERS_ASSET_PLATFORM")=="macos";
    static BuildTarget Target=>Mac?BuildTarget.StandaloneOSX:BuildTarget.StandaloneWindows64;
    static string OutDir=>Path.GetFullPath(Path.Combine(Application.dataPath,"..","..",Mac?"build/macos":"build"));
    [Serializable] public class Profile { public string name;public float[] sun,top,horizon,ambient,ground;public float intensity,elevation,exposure,contrast,saturation,warmth,clouds; }
    [Serializable] public class Profiles { public Profile[] profiles; }
    public static void BuildAndTest()
    {
        Build();Test();TestUrp();TestSunAlignment();NightRenderingTests.Run();
    }
    public static void TestSunAlignment()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        GraphicsSettings.defaultRenderPipeline=null;QualitySettings.renderPipeline=null;
        var camera=new GameObject("SunAlignmentCamera").AddComponent<Camera>();
        camera.clearFlags=CameraClearFlags.Skybox;camera.fieldOfView=30;
        var target=new RenderTexture(128,128,24,RenderTextureFormat.ARGBHalf);target.Create();
        camera.targetTexture=target;
        var material=new Material(Shader.Find("AlpineVision/AlpineSky"));
        material.SetColor("_AV_Zenith",Color.black);material.SetColor("_AV_Horizon",Color.black);
        material.SetVector("_AV_Clouds",Vector4.zero);RenderSettings.skybox=material;
        var light=new GameObject("NativeSun").AddComponent<Light>();light.type=LightType.Directional;
        RenderSettings.sun=light;
        float CenterBrightness()
        {
            camera.Render();var previous=RenderTexture.active;RenderTexture.active=target;
            var image=new Texture2D(128,128,TextureFormat.RGBAFloat,false,true);
            image.ReadPixels(new Rect(0,0,128,128),0,0);image.Apply();
            var brightness=image.GetPixel(64,64).maxColorComponent;
            Object.DestroyImmediate(image);RenderTexture.active=previous;return brightness;
        }
        foreach(var rotation in new[]{new Vector3(17,155,0),new Vector3(48,30,0),new Vector3(32,260,0)})
        {
            light.transform.rotation=Quaternion.Euler(rotation);
            var direction=-light.transform.forward;
            material.SetVector("_AV_Sun",new Vector4(direction.x,direction.y,direction.z,1));
            camera.transform.rotation=Quaternion.LookRotation(direction);
            if(CenterBrightness()<1)throw new Exception("Enhanced sun is not centred on the native light: "+rotation);
            camera.transform.rotation=Quaternion.LookRotation(-direction);
            if(CenterBrightness()>.01f)throw new Exception("Enhanced sun appears away from the native light");
        }
        material.SetVector("_AV_Sun",Vector4.zero);
        camera.transform.rotation=Quaternion.LookRotation(Vector3.up,Vector3.forward);
        if(CenterBrightness()>.01f)throw new Exception("Unavailable native sun draws a fallback disc");
        RenderSettings.skybox=null;RenderSettings.sun=null;camera.targetTexture=null;
        target.Release();Object.DestroyImmediate(target);Object.DestroyImmediate(material);
        Object.DestroyImmediate(camera.gameObject);Object.DestroyImmediate(light.gameObject);
        Debug.Log("PASS sun enhancement follows three native light angles; no opposite or fallback sun.");
    }
    public static void Build()
    {
        BuildAtmosphere.Build();
        // No replacement terrain maps in this bundle; retain the game's native snow/track material.
        var weatherPrefab=PrefabUtility.LoadPrefabContents(Gen+"/JibbersAtmosphere.prefab");
        var surface=weatherPrefab.transform.Find("SnowSurface");
        if(surface!=null)Object.DestroyImmediate(surface.gameObject);
        PrefabUtility.SaveAsPrefabAsset(weatherPrefab,Gen+"/JibbersAtmosphere.prefab");
        PrefabUtility.UnloadPrefabContents(weatherPrefab);
        Material Mat(string name,string shader)
        {
            var s=Shader.Find(shader);
            if(s==null || ShaderUtil.ShaderHasError(s))throw new Exception("Shader failed: "+shader);
            var m=new Material(s){name=name};AssetDatabase.CreateAsset(m,Gen+"/"+name+".mat");return m;
        }
        Mat("AV_Copy","Hidden/AlpineVision/Copy");Mat("AV_Sky","AlpineVision/AlpineSky");Mat("AV_Extract","Hidden/AlpineVision/Extract");
        Mat("AV_BlurH","Hidden/AlpineVision/Blur").SetVector("_AV_Axis",new Vector4(1,0,0,0));
        Mat("AV_BlurV","Hidden/AlpineVision/Blur").SetVector("_AV_Axis",new Vector4(0,1,0,0));
        Mat("AV_Finish","Hidden/AlpineVision/Finish");
        Mat("AV_UrpFinish","Hidden/AlpineVision/UrpFinish");
        Mat("AV_TerrainShadow","Hidden/AlpineVision/TerrainShadow");
        AssetDatabase.SaveAssets();
        string[] names={"JibbersAtmosphere","AV_Copy","AV_Sky","AV_Extract","AV_BlurH","AV_BlurV","AV_Finish","AV_UrpFinish","AV_TerrainShadow"};
        var assets=names.Select(n=>n=="JibbersAtmosphere"?Gen+"/JibbersAtmosphere.prefab":Gen+"/"+n+".mat").ToArray();
        Directory.CreateDirectory(OutDir);
        var manifest=BuildPipeline.BuildAssetBundles(OutDir,new[]{new AssetBundleBuild { assetBundleName="jibbers_alpinevision.bundle",assetNames=assets,addressableNames=names }},
            BuildAssetBundleOptions.ChunkBasedCompression|BuildAssetBundleOptions.ForceRebuildAssetBundle,Target);
        if(manifest==null)throw new Exception("Unified bundle build failed");
        if(Mac){ Debug.Log("PASS macOS Metal bundle built: "+OutDir); return; }
        var loaded=AssetBundle.LoadFromFile(Path.Combine(OutDir,"jibbers_alpinevision.bundle"));
        if(loaded==null)throw new Exception("Cannot reload unified bundle");
        if(loaded.GetAllAssetNames().Any(n=>n.EndsWith(".raytrace") || n.EndsWith(".compute") || n.Contains("jrt_")))
            throw new Exception("Removed ray tracing asset is still packaged");
        foreach(var n in names)if(loaded.LoadAsset(n)==null)throw new Exception("Missing packaged asset: "+n);
        if(loaded.LoadAsset<GameObject>("JibbersAtmosphere").transform.Find("SnowSurface")!=null)
            throw new Exception("Replacement terrain texture holder is still packaged");
        Debug.Log("PASS native snow: replacement texture holder excluded from the weather prefab.");
        loaded.Unload(false);
        Debug.Log($"PASS unified bundle: all {names.Length} weather/visual assets; no ray tracing assets.");
    }
    public static void TestUrp()
    {
        var reports=new System.Collections.Generic.List<string>();
        var priorPipeline=GraphicsSettings.defaultRenderPipeline;
        var priorQuality=QualitySettings.renderPipeline;
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var rendererData=ScriptableObject.CreateInstance<UnityEngine.Rendering.Universal.UniversalRendererData>();
        rendererData.hideFlags=HideFlags.HideAndDontSave;
        var pipeline=UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset.Create(rendererData);
        pipeline.hideFlags=HideFlags.HideAndDontSave;
        GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
        typeof(RenderPipelineManager).GetMethod("TryPrepareRenderPipeline",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)
            .Invoke(null,new object[]{pipeline});
        var camera=new GameObject("UrpWorldCamera").AddComponent<Camera>();
        camera.transform.position=new Vector3(0,3,-8);camera.transform.LookAt(Vector3.zero);
        camera.backgroundColor=new Color(.2f,.35f,.55f);camera.clearFlags=CameraClearFlags.SolidColor;
        var light=new GameObject("Sun").AddComponent<Light>();light.type=LightType.Directional;
        light.transform.rotation=Quaternion.Euler(35,130,0);RenderSettings.sun=light;
        var lit=Shader.Find("Universal Render Pipeline/Lit");
        if(lit==null)throw new Exception("URP Lit shader missing");
        for(int i=0;i<3;i++)
        {
            var obj=GameObject.CreatePrimitive(i==0?PrimitiveType.Plane:PrimitiveType.Cube);
            obj.transform.position=i==0?new Vector3(0,-1,0):new Vector3(i==1?-2:2,0,0);
            var material=new Material(lit);material.SetColor("_BaseColor",i==0?new Color(.75f,.8f,.86f):i==1?new Color(.8f,.15f,.1f):new Color(.1f,.3f,.8f));
            obj.GetComponent<Renderer>().sharedMaterial=material;
        }
        const int W=640,H=360;
        var target=new RenderTexture(W,H,24,RenderTextureFormat.ARGBHalf){hideFlags=HideFlags.HideAndDontSave};target.Create();
        Color[] Render(string name)
        {
            var request=new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination=target };
            RenderPipeline.SubmitRenderRequest(camera,request);
            var previous=RenderTexture.active;RenderTexture.active=target;
            var image=new Texture2D(W,H,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,W,H),0,0);image.Apply();
            RenderTexture.active=previous;var pixels=image.GetPixels();File.WriteAllBytes(Path.Combine(OutDir,name),image.EncodeToPNG());Object.DestroyImmediate(image);return pixels;
        }
        GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
        var prepared=typeof(RenderPipelineManager).GetMethod("TryPrepareRenderPipeline",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)
            .Invoke(null,new object[]{pipeline});
        Debug.Log("URP test pipeline: prepared="+prepared+" current="+RenderPipelineManager.currentPipeline+" asset="+GraphicsSettings.currentRenderPipeline);
        var baseline=Render("urp-original.png");
        var data=UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(camera);
        var active=data.scriptableRenderer;
        var featureList=(System.Collections.Generic.List<UnityEngine.Rendering.Universal.ScriptableRendererFeature>)
            typeof(UnityEngine.Rendering.Universal.ScriptableRenderer).GetField("m_RendererFeatures",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(active);
        // Match the runtime attachment, using the renderer instance's feature list.
        var feature=ScriptableObject.CreateInstance<UnityEngine.Rendering.Universal.FullScreenPassRendererFeature>();
        var finish=new Material(AssetDatabase.LoadAssetAtPath<Material>(Gen+"/AV_UrpFinish.mat")){hideFlags=HideFlags.HideAndDontSave};
        feature.hideFlags=HideFlags.HideAndDontSave;feature.passMaterial=finish;
        feature.fetchColorBuffer=true;feature.requirements=UnityEngine.Rendering.Universal.ScriptableRenderPassInput.None;
        feature.injectionPoint=UnityEngine.Rendering.Universal.FullScreenPassRendererFeature.InjectionPoint.AfterRenderingPostProcessing;
        feature.Create();featureList.Add(feature);
        Color[] previousResult=baseline;
        foreach(var profile in JsonUtility.FromJson<Profiles>(File.ReadAllText(Path.Combine(OutDir,"profiles.json"))).profiles)
        {
            finish.SetVector("_AV_Grade",new Vector4(profile.exposure,profile.contrast,profile.saturation,profile.warmth));
            finish.SetVector("_AV_Mode",new Vector4(profile.name=="Natural"?0:1,0,0,0));
            finish.SetVector("_AV_Finish",new Vector4(.16f,.18f,profile.name=="Natural"?0:.1f,0));
            var pixels=Render("urp-"+profile.name.ToLowerInvariant().Replace(' ','-')+".png");
            int changed=0,black=0;
            for(int i=0;i<pixels.Length;i++)
            {if(Mathf.Max(Mathf.Abs(pixels[i].r-previousResult[i].r),Mathf.Max(Mathf.Abs(pixels[i].g-previousResult[i].g),Mathf.Abs(pixels[i].b-previousResult[i].b)))>.005f)changed++;if(pixels[i].maxColorComponent<.01f)black++;}
            if(profile.name!="Natural" && changed<W*H/20 || black>W*H*.4f)throw new Exception("URP feature failed: "+profile.name+" changed="+changed+" black="+black);
            reports.Add($"PASS URP runtime fullscreen feature {profile.name}: {changed} visibly changed pixels; {black} black pixels");previousResult=pixels;
        }
        featureList.Remove(feature);
        RenderSettings.skybox=null;
        EditorUtility.UnloadUnusedAssetsImmediate(true);
        if(finish==null || feature==null)throw new Exception("URP runtime feature/material lost during cleanup");
        featureList.Add(feature);
        Render("urp-after-cleanup.png");
        featureList.Remove(feature);
        var restored=Render("urp-restored.png");float maxError=0;
        for(int i=0;i<baseline.Length;i++)maxError=Mathf.Max(maxError,Mathf.Abs(baseline[i].grayscale-restored[i].grayscale));
        if(maxError>.01f)throw new Exception("URP feature detach did not restore image: "+maxError);
        reports.Add("PASS URP cleanup/re-attach and detach restoration: max error "+maxError);
        feature.Dispose();Object.DestroyImmediate(feature);Object.DestroyImmediate(finish);target.Release();Object.DestroyImmediate(target);
        GraphicsSettings.defaultRenderPipeline=priorPipeline;QualitySettings.renderPipeline=priorQuality;
        Object.DestroyImmediate(pipeline);Object.DestroyImmediate(rendererData);
        File.WriteAllLines(Path.Combine(OutDir,"urp-render-validation.txt"),reports);
        foreach(var report in reports)Debug.Log(report);
    }
    public static void Test()
    {
        var profiles=JsonUtility.FromJson<Profiles>(File.ReadAllText(Path.Combine(OutDir,"profiles.json"))).profiles;
        if(profiles.Length!=4)throw new Exception("Expected four authored profiles");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        Color C(float[] a)=>new Color(a[0],a[1],a[2],1);
        var sun=new GameObject("Sun").AddComponent<Light>();sun.type=LightType.Directional;
        sun.shadows=LightShadows.Soft;sun.shadowBias=.035f;sun.transform.rotation=Quaternion.Euler(52,155,0);
        sun.color=Color.white;sun.intensity=1;RenderSettings.sun=sun;
        QualitySettings.shadowDistance=800;
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.45f,.45f,.45f);
        var terrainData=new TerrainData {heightmapResolution=257,size=new Vector3(900,180,900)};
        var heights=new float[257,257];
        for(int z=0;z<257;z++)for(int x=0;x<257;x++)
        {
            float nx=x/256f,nz=z/256f;
            float valley=Mathf.Abs(nx-.5f)*1.0f;
            heights[z,x]=Mathf.Clamp01(.03f+valley*(.45f+Mathf.PerlinNoise(nx*7,nz*7)*.65f)+Mathf.PerlinNoise(nx*18,nz*18)*.025f);
        }
        terrainData.SetHeights(0,0,heights);
        var terrain=Terrain.CreateTerrainGameObject(terrainData).GetComponent<Terrain>();
        terrain.materialTemplate=new Material(Shader.Find("Nature/Terrain/Standard"));
        var layer=new TerrainLayer { diffuseTexture=Texture2D.whiteTexture,tileSize=new Vector2(3,3),smoothness=.22f,metallic=0 };
        var normalPath="Assets/Atmosphere/Snow/";
        var normal=AssetDatabase.FindAssets("t:Texture2D",new[]{normalPath.TrimEnd('/')}).Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(p=>p.ToLowerInvariant().Contains("normal"));
        if(normal!=null)layer.normalMapTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(normal);
        terrainData.terrainLayers=new[]{layer};
        var rock=new Material(Shader.Find("Standard")){color=new Color(.13f,.15f,.18f)};
        void Block(Vector3 pos,Vector3 scale,Material mat)
        {var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.transform.position=pos;go.transform.localScale=scale;go.GetComponent<Renderer>().sharedMaterial=mat;}
        Block(new Vector3(455,12,462),new Vector3(18,.5f,.5f),rock);
        Block(new Vector3(447,10,462),new Vector3(.3f,4,.3f),rock);
        Block(new Vector3(463,10,462),new Vector3(.3f,4,.3f),rock);
        for(int i=0;i<18;i++)
        {
            float x=i%2==0?415:490,z=470+i*14;
            var y=terrain.SampleHeight(new Vector3(x,0,z));
            Block(new Vector3(x,y+2,z),new Vector3(.8f,4,.8f),rock);
            var crown=GameObject.CreatePrimitive(PrimitiveType.Sphere);crown.transform.position=new Vector3(x,y+6,z);crown.transform.localScale=new Vector3(4,9,4);
            crown.GetComponent<Renderer>().sharedMaterial=rock;
        }
        var cam=new GameObject("WorldCamera").AddComponent<Camera>();
        cam.transform.position=new Vector3(450,19,430);cam.transform.LookAt(new Vector3(450,22,650));
        cam.nearClipPlane=.1f;cam.farClipPlane=2400;cam.fieldOfView=72;cam.allowHDR=true;cam.allowMSAA=false;
        const int W=960,H=540;
        cam.targetTexture=new RenderTexture(W,H,24,RenderTextureFormat.ARGBHalf);cam.targetTexture.Create();
        cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=new Color(.45f,.6f,.8f);
        Color[] Save(string file)
        {
            cam.Render();var prior=RenderTexture.active;RenderTexture.active=cam.targetTexture;
            var image=new Texture2D(W,H,TextureFormat.RGB24,false);
            image.ReadPixels(new Rect(0,0,W,H),0,0);image.Apply();RenderTexture.active=prior;
            File.WriteAllBytes(Path.Combine(OutDir,file),image.EncodeToPNG());var colors=image.GetPixels();Object.DestroyImmediate(image);return colors;
        }
        var original=Save("alpine-0-original.png");
        Material Owned(string name)=>new Material(AssetDatabase.LoadAssetAtPath<Material>(Gen+"/"+name+".mat")){hideFlags=HideFlags.HideAndDontSave};
        var sky=Owned("AV_Sky");
        var copy=Owned("AV_Copy");
        var extract=Owned("AV_Extract");
        var hmat=Owned("AV_BlurH");
        var vmat=Owned("AV_BlurV");
        var finish=Owned("AV_Finish");
        var scene=new RenderTexture(W,H,0,RenderTextureFormat.ARGBHalf);scene.Create();
        var a=new RenderTexture(W/2,H/2,0,RenderTextureFormat.ARGBHalf);a.Create();
        var b=new RenderTexture(W/2,H/2,0,RenderTextureFormat.ARGBHalf);b.Create();
        var cb=new CommandBuffer {name="Alpine Vision test"};cam.AddCommandBuffer(CameraEvent.AfterImageEffects,cb);
        cb.SetGlobalTexture("_AV_SceneSource",BuiltinRenderTextureType.CameraTarget);
        cb.SetRenderTarget(scene);cb.DrawProcedural(Matrix4x4.identity,copy,0,MeshTopology.Triangles,3);
        cam.Render();
        var copyPrevious=RenderTexture.active;RenderTexture.active=scene;
        var copyImage=new Texture2D(W,H,TextureFormat.RGB24,false);copyImage.ReadPixels(new Rect(0,0,W,H),0,0);copyImage.Apply();
        RenderTexture.active=copyPrevious;var copied=copyImage.GetPixels();Object.DestroyImmediate(copyImage);
        float copyError=0;
        for(int i=0;i<original.Length;i++)copyError=Mathf.Max(copyError,Mathf.Abs(original[i].grayscale-copied[i].grayscale));
        if(copyError>.01f)throw new Exception("Procedural scene capture differs from original: "+copyError);
        // Reproduce the runtime failure: scene transition detaches the renderer, then Unity
        // collects assets while no camera references our materials. Managed roots alone
        // do not protect their native Unity objects; owned HideAndDontSave clones do.
        cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects,cb);cb.Clear();
        RenderSettings.skybox=null;
        foreach(var target in new[]{cam.targetTexture,scene,a,b})target.hideFlags=HideFlags.HideAndDontSave;
        EditorUtility.UnloadUnusedAssetsImmediate(true);
        foreach(var material in new[]{sky,copy,extract,hmat,vmat,finish})
        {
            if(material==null)throw new Exception("Owned material destroyed during unused-asset cleanup");
            material.SetVector("_AV_LifetimeProbe",Vector4.one);
        }
        var previousCamera=cam;
        cam=new GameObject("ReplacementWorldCamera").AddComponent<Camera>();
        cam.CopyFrom(previousCamera);cam.transform.SetPositionAndRotation(previousCamera.transform.position,previousCamera.transform.rotation);
        Object.DestroyImmediate(previousCamera.gameObject);
        cam.AddCommandBuffer(CameraEvent.AfterImageEffects,cb);
        var reports=new System.Collections.Generic.List<string>();
        reports.Add("PASS all six owned materials survive detach/unused-asset cleanup and accept SetVector; camera replaced before preset renders.");
        Color[] previousResult=null;
        reports.Add("PASS retained-API procedural scene capture: max error "+copyError);
        reports.Add($"Device {SystemInfo.graphicsDeviceType}; AfterImageEffects={(int)CameraEvent.AfterImageEffects}; Trilight={(int)AmbientMode.Trilight}; ARGBHalf={(int)RenderTextureFormat.ARGBHalf}");
        foreach(var p in profiles)
        {
            // Profiles change colour/intensity; the game's sun angle remains authoritative.
            if(p.name!="Natural")
            {
                sun.color=C(p.sun);sun.intensity=p.intensity;
                RenderSettings.ambientMode=AmbientMode.Trilight;RenderSettings.ambientSkyColor=C(p.ambient);
                RenderSettings.ambientEquatorColor=C(p.ambient)*.8f;RenderSettings.ambientGroundColor=C(p.ground);
            }
            RenderSettings.skybox=sky;cam.clearFlags=CameraClearFlags.Skybox;
            sky.SetVector("_AV_Zenith",C(p.top));sky.SetVector("_AV_Horizon",C(p.horizon));
            var sunDirection=-sun.transform.forward;
            sky.SetVector("_AV_Sun",new Vector4(sunDirection.x,sunDirection.y,sunDirection.z,1));sky.SetVector("_AV_Clouds",new Vector4(p.clouds,0,0,0));
            cb.Clear();cb.SetGlobalTexture("_AV_SceneSource",BuiltinRenderTextureType.CameraTarget);
            cb.SetRenderTarget(scene);cb.DrawProcedural(Matrix4x4.identity,copy,0,MeshTopology.Triangles,3);
            cb.SetGlobalVector("_AV_Grade",new Vector4(p.exposure,p.contrast,p.saturation,p.warmth));
            cb.SetGlobalVector("_AV_Mode",new Vector4(p.name=="Natural"?0:1,0,0,0));
            cb.SetGlobalVector("_AV_Finish",new Vector4(.16f,.18f,p.name=="Natural"?0:.10f,0));
            cb.Blit(scene,a,extract);cb.Blit(a,b,hmat);cb.Blit(b,a,vmat);cb.SetGlobalTexture("_AV_Bloom",a);cb.Blit(scene,BuiltinRenderTextureType.CameraTarget,finish);
            var result=Save("alpine-"+p.name.ToLowerInvariant().Replace(' ','-')+".png");
            int changed=0,black=0,white=0;
            for(int i=0;i<result.Length;i++)
            {
                if(Mathf.Abs(result[i].grayscale-original[i].grayscale)>.035f)changed++;
                if(result[i].maxColorComponent<.01f)black++;
                if(Mathf.Min(result[i].r,Mathf.Min(result[i].g,result[i].b))>.99f)white++;
            }
            if(changed<W*H/5 || black>W*H*.4f || white>W*H*.4f)throw new Exception($"Bad render {p.name}: changed={changed}, black={black}, white={white}");
            if(previousResult!=null)
            {
                int switched=0;
                for(int i=0;i<result.Length;i++)if(Mathf.Abs(result[i].grayscale-previousResult[i].grayscale)>.035f)switched++;
                if(switched<W*H/20)throw new Exception("Preset switch has too little visible effect: "+p.name+" pixels="+switched);
                reports.Add($"PASS immediate preset switch to {p.name}: {switched} visibly changed pixels");
            }
            previousResult=result;
            reports.Add($"PASS {p.name}: {changed} changed pixels; {black} black; {white} white");
        }
        cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects,cb);
        cam.clearFlags=CameraClearFlags.SolidColor;sun.color=Color.white;sun.intensity=1;sun.transform.rotation=Quaternion.Euler(52,155,0);
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.45f,.45f,.45f);
        var restored=Save("alpine-restored.png");float error=0;
        for(int i=0;i<original.Length;i++)error=Mathf.Max(error,Mathf.Abs(original[i].grayscale-restored[i].grayscale));
        if(error>.01f)throw new Exception("Render restore mismatch: "+error);
        reports.Add("PASS detach/restoration: max error "+error);
        cb.Release();scene.Release();a.Release();b.Release();
        RenderSettings.skybox=null;
        foreach(var material in new[]{sky,copy,extract,hmat,vmat,finish})Object.DestroyImmediate(material);
        File.WriteAllLines(Path.Combine(OutDir,"alpine-render-validation.txt"),reports);
        foreach(var report in reports)Debug.Log(report);
    }
}
