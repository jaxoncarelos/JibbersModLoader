using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace JibbersAlpineVision.Rendering;

// Visual APIs only. No ray-tracing, acceleration-structure or shadow override APIs.
internal sealed class EngineApi
{
    readonly List<string> missing=new();
    public IReadOnlyList<string> Missing=>missing;
    public readonly Il2CppClass MaterialClass,CameraClass,LightClass,CommandBufferClass,RenderTextureClass;
    public readonly Il2CppMethod BufferCtor,BufferName,Clear,BufferRelease,GlobalTexture,GlobalVector,Blit,
        TextureCtor,TextureCreate,TextureRelease,TargetFromTexture,TargetFromBuiltin,
        PixelWidth,PixelHeight,AddCommandBuffer,RemoveCommandBuffer,Sun,LightType;
    public readonly bool BlitTakesTexture;
    Il2CppMethod Need(Il2CppClass type,string name,params string[] args)
    {
        var m=type.Method(name,args);
        if(m.IsNull)missing.Add(type+"."+name+"("+string.Join(",",args)+")");
        return m;
    }
    public EngineApi()
    {
        const string RTI="UnityEngine.Rendering.RenderTargetIdentifier",Str="System.String";
        MaterialClass=Unity.Class("UnityEngine.Material");CameraClass=Unity.Class("UnityEngine.Camera");
        LightClass=Unity.Class("UnityEngine.Light");CommandBufferClass=Unity.Class("UnityEngine.Rendering.CommandBuffer");
        RenderTextureClass=Unity.Class("UnityEngine.RenderTexture");
        var cb=CommandBufferClass;
        BufferCtor=Need(cb,".ctor");BufferName=Need(cb,"set_name",Str);Clear=Need(cb,"Clear");BufferRelease=Need(cb,"Release");
        GlobalTexture=Need(cb,"SetGlobalTexture",Str,RTI);GlobalVector=Need(cb,"SetGlobalVector",Str,"UnityEngine.Vector4");
        Blit=cb.Method("Blit","UnityEngine.Texture",RTI,"UnityEngine.Material");BlitTakesTexture=!Blit.IsNull;
        if(Blit.IsNull)Blit=Need(cb,"Blit",RTI,RTI,"UnityEngine.Material");
        TextureCtor=Need(RenderTextureClass,".ctor","System.Int32","System.Int32","System.Int32","UnityEngine.RenderTextureFormat");
        TextureCreate=Need(RenderTextureClass,"Create");TextureRelease=Need(RenderTextureClass,"Release");
        var target=Unity.Class(RTI);
        TargetFromTexture=Need(target,"op_Implicit","UnityEngine.Texture");
        TargetFromBuiltin=Need(target,"op_Implicit","UnityEngine.Rendering.BuiltinRenderTextureType");
        PixelWidth=Need(CameraClass,"get_pixelWidth");PixelHeight=Need(CameraClass,"get_pixelHeight");
        AddCommandBuffer=Need(CameraClass,"AddCommandBuffer","UnityEngine.Rendering.CameraEvent","UnityEngine.Rendering.CommandBuffer");
        RemoveCommandBuffer=Need(CameraClass,"RemoveCommandBuffer","UnityEngine.Rendering.CameraEvent","UnityEngine.Rendering.CommandBuffer");
        Sun=Need(Unity.Class("UnityEngine.RenderSettings"),"get_sun");LightType=Need(LightClass,"get_type");
    }
}
