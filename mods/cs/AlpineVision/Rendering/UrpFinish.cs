using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace JibbersAlpineVision.Rendering;

/// <summary>Use the player's own IL2CPP fullscreen renderer feature, including its RenderGraph path.</summary>
internal sealed unsafe class UrpFinish : IDisposable
{
    GameRef? material,feature,features;
    nint renderer;
    readonly Il2CppClass featureClass=Unity.Class("UnityEngine.Rendering.Universal.FullScreenPassRendererFeature");
    static Il2CppMethod Need(Il2CppClass cls,string name,params string[] args)
    {
        var m=cls.Method(name,args);
        return m.IsNull?throw new NotSupportedException("Missing URP API "+cls+"."+name):m;
    }
    static Il2CppField Field(Il2CppClass cls,string name)
    {
        var f=cls.Field(name);
        return f.IsNull?throw new NotSupportedException("Missing URP field "+cls+"."+name):f;
    }
    public bool Attached=>features is not null && feature is not null && !Unity.IsNull(feature.Target);
    public void Update(nint camera,AssetBundles.Bundle bundle,VisualProfile profile,float bloom,float sharpness,float ev,bool grading,string quality)
    {
        var data=Unity.GetComponent(camera,Unity.Class("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData"));
        if(Unity.IsNull(data))throw new NotSupportedException("WorldCamera has no URP camera data");
        var active=Need(Il2Cpp.ClassOf(data),"get_scriptableRenderer").Invoke(data);
        if(active==0)throw new InvalidOperationException("URP camera renderer is not ready");
        if(feature is null || Unity.IsNull(feature.Target) || material is null || Unity.IsNull(material.Target))
        {
            Dispose();
            var source=bundle.LoadAsset("AV_UrpFinish",Unity.Class("UnityEngine.Material"));
            if(Unity.IsNull(source))throw new InvalidDataException("Missing AV_UrpFinish material");
            var mat=Unity.Class("UnityEngine.Material").New();
            Need(Unity.Class("UnityEngine.Material"),".ctor","UnityEngine.Material").Invoke(mat,source);material=new(mat);
            var f=Need(Unity.Class("UnityEngine.ScriptableObject"),"CreateInstance","System.Type").Invoke(0,featureClass.SystemType);
            feature=new(f);
            var flags=61;
            foreach(var obj in new[]{mat,f})Need(Unity.Class("UnityEngine.Object"),"set_hideFlags","UnityEngine.HideFlags").Invoke(obj,(nint)(&flags));
            Field(featureClass,"passMaterial").SetObject(f,mat);
            Field(featureClass,"fetchColorBuffer").Set(f,true);
            Field(featureClass,"injectionPoint").Set(f,600); // AfterRenderingPostProcessing; game shadow/depth passes stay intact.
            Field(featureClass,"passIndex").Set(f,0);
            Field(featureClass,"requirements").Set(f,0);
            Need(featureClass,"Create").Invoke(f);
        }
        if(renderer!=active || features is null)
        {
            Detach();
            var list=Field(Unity.Class("UnityEngine.Rendering.Universal.ScriptableRenderer"),"m_RendererFeatures").GetObject(active);
            if(list==0)throw new InvalidOperationException("URP renderer feature list is absent");
            Need(Il2Cpp.ClassOf(list),"Add","UnityEngine.Rendering.Universal.ScriptableRendererFeature").Invoke(list,feature!.Target);
            features=new(list);renderer=active;
        }
        var film=grading && profile.Name!="Natural";
        Set("_AV_Grade",grading?new(profile.Exposure*MathF.Pow(2,ev),profile.Contrast,profile.Saturation,profile.Warmth):new(1,1,1,0));
        Set("_AV_Mode",new(film?1:0,0,0,0));
        Set("_AV_Finish",new(grading?bloom:0,sharpness,film?.10f:0,film&&quality!="Performance"?.002f:0));
    }
    void Set(string name,Vector4 value)=>Need(Unity.Class("UnityEngine.Material"),"SetVector","System.String","UnityEngine.Vector4").Invoke(material!.Target,Il2Cpp.String(name),(nint)(&value));
    public void Detach()
    {
        if(features is not null && feature is not null && !Unity.IsNull(feature.Target))
            Need(Il2Cpp.ClassOf(features.Target),"Remove","UnityEngine.Rendering.Universal.ScriptableRendererFeature").Invoke(features.Target,feature.Target);
        features?.Dispose();features=null;renderer=0;
    }
    public void Dispose()
    {
        Detach();
        if(feature is not null && !Unity.IsNull(feature.Target))
        {
            var dispose=featureClass.Method("Dispose",Array.Empty<string>());
            if(!dispose.IsNull)dispose.Invoke(feature.Target);
        }
        foreach(var obj in new[]{feature,material})
        {if(obj is null)continue;if(!Unity.IsNull(obj.Target))Unity.Destroy(obj.Target);obj.Dispose();}
        feature=material=null;
    }
}
