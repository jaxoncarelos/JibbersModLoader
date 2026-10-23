Shader "Hidden/AlpineVision/UrpFinish"
{
 Properties { _AV_Grade("Grade",Vector)=(1,1,1,0) _AV_Finish("Finish",Vector)=(0,.18,0,0) _AV_Mode("Mode",Vector)=(0,0,0,0) }
 SubShader { Tags { "RenderPipeline"="UniversalPipeline" } Cull Off ZWrite Off ZTest Always
 Pass { HLSLPROGRAM
 #pragma vertex Vert
 #pragma fragment FinishUrp
 #pragma target 3.5
 #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
 #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
 float4 _AV_Grade,_AV_Finish,_AV_Mode;
 float Luma(float3 c) { return dot(c,float3(.2126,.7152,.0722)); }
 float3 Film(float3 x) { return saturate((x*(2.51*x+.03))/(x*(2.43*x+.59)+.14)); }
 float3 Source(float2 uv) { return SAMPLE_TEXTURE2D_X(_BlitTexture,sampler_LinearClamp,uv).rgb; }
 float3 Highlight(float3 c) {
  float peak=max(c.r,max(c.g,c.b));float knee=saturate((peak-.75)/.5);knee=knee*knee*.125;
  return c*max(peak-1.0,knee)/max(peak,.0001);
 }
 float4 FinishUrp(Varyings i):SV_Target {
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    float2 uv=i.texcoord,px=_BlitTexture_TexelSize.xy;
    float3 c=Source(uv),n=Source(uv+float2(0,px.y)),s=Source(uv-float2(0,px.y));
    float3 e=Source(uv+float2(px.x,0)),w=Source(uv-float2(px.x,0));
    float3 lo=min(c,min(min(n,s),min(e,w))),hi=max(c,max(max(n,s),max(e,w)));
    float edge=saturate((Luma(hi)-Luma(lo)-.12)*2),averageLuma=Luma((n+s+e+w)*.25);
    float3 average=(n+s+e+w)*.25;c=lerp(c,average,edge*.22);c=clamp(c+(c-average)*_AV_Finish.y,lo,hi);
    float3 glow=Highlight(c)*.25;
    glow+=(Highlight(Source(uv+px*float2(3,3)))+Highlight(Source(uv+px*float2(-3,3)))+Highlight(Source(uv+px*float2(3,-3)))+Highlight(Source(uv-px*float2(3,3))))*.1875;
    c+=glow*_AV_Finish.x;
    c=max(0,c*_AV_Grade.x);
    // Natural retains the Atmosphere tone mapper. Film styles retain Alpine Vision's
    // established filter and disable the preceding tone mapper in configuration.
    float y=Luma(c),shadow=1-smoothstep(.08,.65,y);
    c*=lerp(float3(1.0+_AV_Grade.w,lerp(1,.995,_AV_Mode.x),1.0-_AV_Grade.w*.5),float3(.94,.985,1.055),shadow*.34*_AV_Mode.x);
    c=lerp(y.xxx,c,_AV_Grade.z);
    c=max(0,(c-.18)*_AV_Grade.y+.18);
    if(_AV_Mode.x>.5)c=Film(c);
    float2 p=uv*2-1;float vignette=1-_AV_Finish.z*smoothstep(.28,1.7,dot(p,p));
    float grain=frac(sin(dot(uv*_ScreenParams.xy,float2(12.9898,78.233))+floor(_Time.y*24))*43758.5453)-.5;
    c=c*vignette+grain*_AV_Finish.w;
    return float4(saturate(c),1);
 }
 ENDHLSL } }
}
