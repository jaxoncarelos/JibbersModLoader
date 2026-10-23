// Debug view of the ray-traced terms (red: sun occlusion, green: ambient occlusion).
Shader "Hidden/JibbersRT/Debug"
{
    Properties { _MainTex ("", 2D) = "white" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _RT_Result;
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    float4 _RT_Composite; // x shadow strength (1 - ambient share), y AO strength
    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
    float2 Result(float2 uv)
    {
        float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
    #if UNITY_REVERSED_Z
        if (raw <= 0.0) return 0;
    #else
        if (raw >= 1.0) return 0;
    #endif
        return tex2D(_RT_Result, uv).rg;
    }
    ENDCG
    SubShader
    {
        ZTest Always ZWrite Off Cull Off
        Pass
        {
            Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float2 r = Result(i.uv);
                return float4(r.x, r.y, 0, 1);
            }
            ENDCG
        }
    }
}
