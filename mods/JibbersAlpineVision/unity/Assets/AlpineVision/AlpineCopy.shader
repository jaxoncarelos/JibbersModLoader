// Full-screen scene capture using APIs retained by the IL2CPP player.
Shader "Hidden/AlpineVision/Copy"
{
 SubShader { Cull Off ZWrite Off ZTest Always Pass {
 HLSLPROGRAM
 #pragma vertex Vert
 #pragma fragment Frag
 #pragma target 3.5
 #include "UnityCG.cginc"
 sampler2D _AV_SceneSource;
 struct Varyings { float4 position:SV_POSITION;float2 uv:TEXCOORD0; };
 Varyings Vert(uint id:SV_VertexID) {
  Varyings o;float2 uv=float2((id<<1)&2,id&2);
  o.position=float4(uv*2-1,0,1);o.uv=uv;
 #if UNITY_UV_STARTS_AT_TOP
  o.uv.y=1-o.uv.y;
 #endif
  return o;
 }
 float4 Frag(Varyings i):SV_Target { return tex2D(_AV_SceneSource,i.uv); }
 ENDHLSL
 } }
}
