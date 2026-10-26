// Holds the replacement snow textures for the map terrain. Never drawn (its renderer is disabled):
// the mod reads these textures off the material and puts them on the game's terrain
// material (Custom/PisteSnow), so they stay loaded after the bundle itself is unloaded.
Shader "Hidden/JibbersAtmosphere/SnowSurface"
{
    Properties
    {
        _SurfaceTex ("Snow albedo", 2D) = "white" {}
        _SnowNormal ("Snow normal", 2D) = "bump" {}
        _SnowDetailNormal ("Wind dunes normal", 2D) = "bump" {}
    }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert(float4 v : POSITION) : SV_POSITION { return float4(2, 2, 2, 1); }
            float4 frag() : SV_Target { return 0; }
            ENDCG
        }
    }
}
