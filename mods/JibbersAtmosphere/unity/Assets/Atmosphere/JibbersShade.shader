// Coloured shading pass, drawn after opaques and before the fog: multiplies the scene by a tint.
//   - Contact shadows: occluders that rise steeply above the surface within a short reach (rails,
//     kickers, the rider, buildings on the snow). Gentle terrain curvature never qualifies.
//   - Cloud shadows: soft domain-warped cloud shapes projected along the sun, only on sunlit slopes.
// Both tint towards cool blue rather than grey, like shade on real snow. Globals come from the
// hotload core; with both strengths at 0 the pass draws nothing.
Shader "Hidden/JibbersAtmosphere/Shade"
{
    SubShader
    {
        Tags { "Queue" = "Transparent-491" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend DstColor Zero

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "JibbersCommon.cginc"

            float4 _JA_Cloud;     // x strength, y 1/cloud size (1/m), z coverage (0..1), w cloud height (world y)
            float4 _JA_Cloud2;    // xy drift (m/s), z edge softness, w debug split (1 = only the right half of the screen)
            float4 _JA_CloudTint; // rgb colour of fully shaded snow
            float4 _JA_AO;        // x strength, y reach (m), z fade-out distance (m), w min occluder angle (sin)
            float4 _JA_AOTint;    // rgb colour of a fully occluded corner

            float CloudCover(float3 wp)
            {
                float3 s = _JA_SunDir.xyz;
                float2 p = wp.xz + s.xz * ((_JA_Cloud.w - wp.y) / max(s.y, 0.15));
                p = (p - _JA_Cloud2.xy * _Time.y) * _JA_Cloud.y;
                // Domain warp: billowy, irregular cloud outlines instead of round noise blobs.
                float2 q = float2(Fbm(p), Fbm(p + float2(5.2, 1.3)));
                float n = Fbm(p + 1.4 * q);
                float t = lerp(0.22, -0.22, _JA_Cloud.z);
                float soft = max(_JA_Cloud2.z, 0.01);
                return smoothstep(t - soft, t + soft, n);
            }

            float ContactShadow(ScreenFrame f, float3 p, float3 n, float eye)
            {
                float R = _JA_AO.y;
                float pxPerMetre = abs(UNITY_MATRIX_P._m11) * 0.5 * _ScreenParams.y / eye;
                float radius = R * pxPerMetre;
                if (radius < 1.5) return 0.0; // too far away to resolve
                radius = min(radius, 80.0);
                float jitter = PixelNoise(f.uv);
                float occ = 0.0;
                const int N = 16;
                [unroll]
                for (int k = 0; k < N; k++)
                {
                    float t = (k + jitter) / N;
                    float ang = k * 2.39996 + jitter * 6.2832;
                    float2 o = float2(cos(ang), sin(ang)) * t * radius;
                    float3 v = WorldAt(f, o) - p;
                    float d = length(v);
                    float rise = dot(v, n) / max(d, 1e-4);
                    occ += saturate((rise - _JA_AO.w) / (1.0 - _JA_AO.w)) * saturate(1.0 - d / R);
                }
                occ = saturate(occ / N * 2.5);
                return occ * saturate((_JA_AO.z - eye) / (_JA_AO.z * 0.3));
            }

            float4 frag(v2f i) : SV_Target
            {
                if (_JA_Cloud.x <= 0.0 && _JA_AO.x <= 0.0) discard;
                ScreenFrame f = MakeFrame(i);
                float raw = RawDepth(f.uv);
                if (IsSky(raw) || (_JA_Cloud2.w > 0.0 && f.uv.x < 0.5)) discard;
                float eye = LinearEyeDepth(raw);
                float3 p = _WorldSpaceCameraPos + f.ray * eye;
                float3 viewDir = normalize(f.ray);
                float3 n = NormalAt(f, p, viewDir);

                float3 tint = 1.0;
                if (_JA_Cloud.x > 0.0 && _JA_SunDir.y > 0.02)
                {
                    // Only faces the sun actually lights lose light under a cloud.
                    float lit = smoothstep(0.0, 0.35, dot(n, _JA_SunDir.xyz)) * saturate(_JA_SunDir.y * 4.0);
                    tint *= lerp(1.0, _JA_CloudTint.rgb, _JA_Cloud.x * CloudCover(p) * lit);
                }
                if (_JA_AO.x > 0.0 && eye < _JA_AO.z)
                    tint *= lerp(1.0, _JA_AOTint.rgb, _JA_AO.x * ContactShadow(f, p, n, eye));
                return float4(tint, 1.0);
            }
            ENDCG
        }
    }
}
