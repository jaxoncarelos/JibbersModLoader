// Mark character geometry after opaques. Only bit 0x10 is changed; colour and depth
// are untouched. Pass 1 clears our bit each frame before pass 0 marks the rider.
Shader "Hidden/JibbersAtmosphere/CharacterMask"
{
    SubShader
    {
        Pass
        {
            ZWrite Off ZTest LEqual Cull Off ColorMask 0
            Stencil { Ref 16 ReadMask 16 WriteMask 16 Comp Always Pass Replace }
            CGPROGRAM
            #pragma vertex characterVert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 characterVert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return 0; }
            ENDCG
        }
        Pass
        {
            ZWrite Off ZTest Always Cull Off ColorMask 0
            Stencil { Ref 0 ReadMask 16 WriteMask 16 Comp Always Pass Replace }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "JibbersCommon.cginc"
            float4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }
    }
}
