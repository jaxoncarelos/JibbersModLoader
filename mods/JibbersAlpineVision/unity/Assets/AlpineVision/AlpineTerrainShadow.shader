Shader "Hidden/AlpineVision/TerrainShadow"
{
 SubShader
 {
  Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
  Pass
  {
   Name "ShadowCaster"
   Tags { "LightMode"="ShadowCaster" }
   ZWrite On ZTest LEqual ColorMask 0 Cull Off
   HLSLPROGRAM
   #pragma target 3.0
   #pragma vertex ShadowVertex
   #pragma fragment ShadowFragment
   #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
   float3 _LightDirection;
   float3 _LightPosition;
   struct Attributes { float3 positionOS:POSITION;float3 normalOS:NORMAL; };
   float4 ShadowVertex(Attributes input):SV_POSITION
   {
    float3 positionWS=TransformObjectToWorld(input.positionOS);
    float3 normalWS=TransformObjectToWorldNormal(input.normalOS);
    #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
     float3 lightDirection=normalize(_LightPosition-positionWS);
    #else
     float3 lightDirection=_LightDirection;
    #endif
    float4 positionCS=TransformWorldToHClip(ApplyShadowBias(positionWS,normalWS,lightDirection));
    return ApplyShadowClamping(positionCS);
   }
   half4 ShadowFragment():SV_TARGET { return 0; }
   ENDHLSL
  }
 }
 Fallback Off
}
