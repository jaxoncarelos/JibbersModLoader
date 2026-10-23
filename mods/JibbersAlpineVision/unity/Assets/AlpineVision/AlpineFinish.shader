Shader "Hidden/AlpineVision/Finish"
{
 Properties { _MainTex("Source",2D)="black"{} }
 SubShader { Cull Off ZWrite Off ZTest Always Pass {
 CGPROGRAM
 #pragma vertex vert_img
 #pragma fragment Finish
 #pragma target 3.0
 #include "AlpineCommon.cginc"
 ENDCG
 } }
}
