#include "UnityCG.cginc"
sampler2D _MainTex, _AV_Bloom;
float4 _MainTex_TexelSize, _AV_SourceSize, _AV_Grade, _AV_Finish, _AV_Mode;
float Luma(float3 c) { return dot(c,float3(.2126,.7152,.0722)); }
float3 Film(float3 x) { return saturate((x*(2.51*x+.03))/(x*(2.43*x+.59)+.14)); }
float4 Extract(v2f_img i):SV_Target
{
    float3 c=tex2D(_MainTex,i.uv).rgb;
    // Smooth highlight knee. Snow retains local contrast rather than becoming a white glow.
    float peak=max(c.r,max(c.g,c.b));
    float knee=saturate((peak-.75)/.5);knee=knee*knee*.125;
    float contribution=max(peak-1.0,knee)/max(peak,.0001);
    return float4(c*contribution,1);
}
float4 _AV_Axis;
float4 Blur(v2f_img i):SV_Target
{
    float2 d=_MainTex_TexelSize.xy*_AV_Axis.xy*1.8;
    float3 c=tex2D(_MainTex,i.uv).rgb*.227027;
    c+=(tex2D(_MainTex,i.uv+d*1.384615).rgb+tex2D(_MainTex,i.uv-d*1.384615).rgb)*.316216;
    c+=(tex2D(_MainTex,i.uv+d*3.230769).rgb+tex2D(_MainTex,i.uv-d*3.230769).rgb)*.070270;
    return float4(c,1);
}
float4 Finish(v2f_img i):SV_Target
{
    float2 uv=i.uv,px=_MainTex_TexelSize.xy;
    float3 c=tex2D(_MainTex,uv).rgb;
    float3 n=tex2D(_MainTex,uv+float2(0,px.y)).rgb;
    float3 s=tex2D(_MainTex,uv-float2(0,px.y)).rgb;
    float3 e=tex2D(_MainTex,uv+float2(px.x,0)).rgb;
    float3 w=tex2D(_MainTex,uv-float2(px.x,0)).rgb;
    float3 lo=min(c,min(min(n,s),min(e,w))), hi=max(c,max(max(n,s),max(e,w)));
    float edge=saturate((Luma(hi)-Luma(lo)-.12)*2);
    // Edge smoothing followed by bounded local contrast avoids bright sharpening halos.
    float3 average=(n+s+e+w)*.25;
    c=lerp(c,average,edge*.22);
    c=clamp(c+(c-average)*_AV_Finish.y,lo,hi);
    c+=tex2D(_AV_Bloom,uv).rgb*_AV_Finish.x;
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
