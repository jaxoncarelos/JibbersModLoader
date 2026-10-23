Shader "Hidden/AlpineVision/Blur"
{
 Properties { _MainTex("Source",2D)="black"{} _AV_Axis("Axis",Vector)=(1,0,0,0) }
 SubShader { Cull Off ZWrite Off ZTest Always Pass {
 CGPROGRAM
 #pragma vertex vert_img
 #pragma fragment Blur
 #pragma target 3.0
 #include "AlpineCommon.cginc"
 ENDCG
 } }
}
