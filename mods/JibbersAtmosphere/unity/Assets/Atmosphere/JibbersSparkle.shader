// Snow glitter: tiny ice crystals that flash when they line up between the sun and the camera.
// Space is cut into small world cells; a few cells hold a randomly tilted crystal facet, which
// glints when its normal matches the half vector between view and sun. Cells never shrink below
// the pixel footprint, so distant snow doesn't shimmer. Additive, drawn before the shade and fog
// passes so cloud shadows and fog dim it. Globals come from the Atmosphere mod.
Shader "Hidden/JibbersAtmosphere/Sparkle"
{
    SubShader
    {
        Tags { "Queue" = "Transparent-492" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "JibbersCommon.cginc"

            float4 _JA_Sparkle; // x intensity, y fraction of cells with a crystal, z fade-out distance (m), w crystal size (m)
            // World boxes around the nearest characters (centre C, extents E), refreshed every
            // frame by the core. Individual vectors: the game's build has no global array setter,
            // and its scriptable pipeline ignores camera command buffers (no stencil mask).
            float4 _JA_CharBox0C, _JA_CharBox0E, _JA_CharBox1C, _JA_CharBox1E;
            float4 _JA_CharBox2C, _JA_CharBox2E, _JA_CharBox3C, _JA_CharBox3E;
            float4 _JA_CharBox4C, _JA_CharBox4E, _JA_CharBox5C, _JA_CharBox5E;
            float4 _JA_CharBox6C, _JA_CharBox6E, _JA_CharBox7C, _JA_CharBox7E;
            float _JA_CharBoxCount;

            bool InBox(float3 p, float4 c, float4 e) { return all(abs(p - c.xyz) <= e.xyz); }
            bool OnCharacter(float3 p)
            {
                float n = _JA_CharBoxCount;
                return (n > 0.5 && InBox(p, _JA_CharBox0C, _JA_CharBox0E))
                    || (n > 1.5 && InBox(p, _JA_CharBox1C, _JA_CharBox1E))
                    || (n > 2.5 && InBox(p, _JA_CharBox2C, _JA_CharBox2E))
                    || (n > 3.5 && InBox(p, _JA_CharBox3C, _JA_CharBox3E))
                    || (n > 4.5 && InBox(p, _JA_CharBox4C, _JA_CharBox4E))
                    || (n > 5.5 && InBox(p, _JA_CharBox5C, _JA_CharBox5E))
                    || (n > 6.5 && InBox(p, _JA_CharBox6C, _JA_CharBox6E))
                    || (n > 7.5 && InBox(p, _JA_CharBox7C, _JA_CharBox7E));
            }

            float4 Hash43(float3 p)
            {
                float4 p4 = frac(p.xyzx * float4(0.1031, 0.1030, 0.0973, 0.1099));
                p4 += dot(p4, p4.wzxy + 33.33);
                return frac((p4.xxyz + p4.yzzw) * p4.zywx);
            }

            float4 frag(v2f i) : SV_Target
            {
                if (_JA_Sparkle.x <= 0.0 || _JA_SunDir.y <= 0.0) discard;
                ScreenFrame f = MakeFrame(i);
                float raw = RawDepth(f.uv);
                if (IsSky(raw)) discard;
                float eye = LinearEyeDepth(raw);
                if (eye > _JA_Sparkle.z) discard;
                float3 p = _WorldSpaceCameraPos + f.ray * eye;
                if (OnCharacter(p)) discard; // clothing, skis and boots never glitter
                float3 viewDir = normalize(f.ray);
                float3 n = NormalAt(f, p, viewDir);
                if (n.y < 0.3) discard; // snow lies on fairly flat ground

                float footprint = eye * 2.0 / (abs(UNITY_MATRIX_P._m11) * _ScreenParams.y);
                float cell = max(_JA_Sparkle.w, footprint * 1.5);
                float4 h = Hash43(floor(p / cell) + 0.5);
                if (h.x > _JA_Sparkle.y) discard;

                float3 facet = normalize(n + (h.yzw * 2.0 - 1.0) * 0.7);
                float3 halfV = normalize(_JA_SunDir.xyz - viewDir);
                float glint = pow(saturate(dot(facet, halfV)), 400.0);
                float fade = saturate((_JA_Sparkle.z - eye) / (_JA_Sparkle.z * 0.4)) * saturate(n.y * 2.0 - 0.6);
                float lit = saturate(dot(n, _JA_SunDir.xyz) * 3.0);
                float3 col = _JA_SunColor.rgb * glint * fade * lit * _JA_Sparkle.x * 12.0;
                return float4(col, 0);
            }
            ENDCG
        }
    }
}
