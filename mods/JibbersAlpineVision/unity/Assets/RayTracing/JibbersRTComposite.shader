// Applies the ray-traced result to the lit opaque image before transparents and fog: lit pixels the
// sun cannot reach fall to the ambient share of their light, and crevices lose ambient light.
// One pass: the game's build only has Blit(source, dest, material), which draws every pass.
Shader "Hidden/JibbersRT/Composite"
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
            Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float2 r = Result(i.uv);
                float m = (1 - r.x * _RT_Composite.x) * (1 - r.y * _RT_Composite.y);
                return float4(m, m, m, 1);
            }
            ENDCG
        }
    }
}
