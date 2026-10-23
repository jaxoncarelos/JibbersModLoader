// Material for ray-tracing proxy geometry (the terrain rebuilt as a mesh): never draws anything.
Shader "Hidden/JibbersRT/Proxy"
{
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }
        Pass
        {
            ColorMask 0 ZWrite Off ZTest Never Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 v : POSITION) : SV_POSITION { return float4(2, 2, 2, 1); }
            float4 frag() : SV_Target { return 0; }
            ENDCG
        }
    }
}
