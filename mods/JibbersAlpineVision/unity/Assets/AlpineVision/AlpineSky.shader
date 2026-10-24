Shader "AlpineVision/AlpineSky"
{
 Properties {
  _AV_Zenith("Zenith",Color)=(.075,.2,.38,1)
  _AV_Horizon("Horizon",Color)=(.76,.83,.89,1)
  _AV_Sun("Sun direction",Vector)=(.3,.6,.4,0)
  _AV_Clouds("Clouds",Vector)=(.32,.006,0,0)
  _AV_Night("Night blend",Vector)=(0,0,0,0)
 }
 SubShader {
 Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
 Cull Off ZWrite Off
 Pass {
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #pragma target 3.0
 #include "UnityCG.cginc"
 float4 _AV_Zenith,_AV_Horizon,_AV_Sun,_AV_Clouds,_AV_Night;
 struct v2f { float4 pos:SV_POSITION;float3 direction:TEXCOORD0; };
 v2f vert(float4 v:POSITION) { v2f o;o.pos=UnityObjectToClipPos(v);o.direction=v.xyz;return o; }
 float Hash(float2 p) { return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453); }
 float Noise(float2 p) {
  float2 a=floor(p),f=frac(p);f=f*f*(3-2*f);
  return lerp(lerp(Hash(a),Hash(a+float2(1,0)),f.x),lerp(Hash(a+float2(0,1)),Hash(a+1),f.x),f.y);
 }
 float Fbm(float2 p) { float v=0,a=.5;for(int i=0;i<5;i++){v+=Noise(p)*a;p=p*2.03+17.1;a*=.5;}return v; }
 float4 frag(v2f i):SV_Target {
  // No invented sun when the scene has no usable directional light.
  float3 d=normalize(i.direction),sun=normalize(_AV_Sun.xyz+float3(0,1e-8,0));
  float altitude=saturate(d.y);
  float3 c=lerp(_AV_Horizon.rgb,_AV_Zenith.rgb,pow(altitude,.43));
  float facing=max(0,dot(d,sun));
  c+=float3(1,.71,.41)*pow(facing,18)*.18*_AV_Sun.w;
  c+=float3(3.8,3.25,2.4)*smoothstep(.99988,.99998,facing)*_AV_Sun.w;
  // A wind-driven high cloud sheet with a lit edge and a shaded underside.
  float2 p=d.xz/max(d.y+.12,.035)*2.4+_Time.y*_AV_Clouds.y*float2(1,.27);
  float shape=Fbm(p),threshold=lerp(.78,.40,_AV_Clouds.x);
  float cloud=smoothstep(threshold,threshold+.13,shape)*smoothstep(0,.15,d.y);
  float shade=Fbm(p+sun.xz*.22);
  float3 cloudHighlight=lerp(float3(1.0,.98,.94),_AV_Horizon.rgb*1.25,saturate(_AV_Night.x));
  float3 cloudColor=lerp(_AV_Horizon.rgb*.70,cloudHighlight,saturate((shape-shade)*3+.66));
  c=lerp(c,cloudColor,cloud*.87);
  c=lerp(_AV_Horizon.rgb*.55,c,smoothstep(-.3,0,d.y));
  return float4(c,1);
 }
 ENDCG
 } }
 Fallback Off
}
