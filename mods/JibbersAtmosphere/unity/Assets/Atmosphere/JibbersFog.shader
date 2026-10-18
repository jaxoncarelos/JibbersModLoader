// Height + distance fog for Jibbers, drawn after the shade pass. Integrates exponential height fog
// and distance haze along each view ray to the scene depth, blends towards the game's sky-synced
// horizon colour, hazes a band of sky along the horizon and adds a glow around the sun, with light
// shafts where ridges and buildings break the sun up (depth-only: sky pixels along the line towards
// the sun's screen position are light, anything in front blocks it). Around the map, a low cloud
// sea fills the valleys (a raymarched height slab of drifting noise outside the map's rectangle that
// stops at the scene depth, so it meets the terrain softly). At high speed a faint cold frost creeps in at the screen edges.
// Globals come
// from the hotload core; they all default to 0, so nothing is drawn until the core turns it on.
Shader "Hidden/JibbersAtmosphere/Fog"
{
    SubShader
    {
        Tags { "Queue" = "Transparent-490" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off // convex shell around the camera: one face per pixel either way
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "JibbersCommon.cginc"

            float4 _JA_FogColor; // rgb fog colour at the horizon, a master opacity (0 = off)
            float4 _JA_FogTop;   // rgb colour looking up through the fog, a horizon band sharpness over the sky
            float4 _JA_Fog;      // x density (1/m) at base height, y height falloff (1/m), z base height (m), w max opacity
            float4 _JA_Fog2;     // x start distance (m), y haze over the sky (0..1), z sky ray length (m), w distance-only density (1/m)
            float4 _JA_Shafts;   // x intensity, y reach (fraction of the way to the sun on screen), z falloff per step, w lobe width
            float4 _JA_Bank;     // x cloud bank density (0 = off), y base height, z top height (world y), w 1/lump size (1/m)
            float4 _JA_Bank2;    // xy drift (m/s), z coverage (0..1), w fade-in distance past the map edge (m)
            float4 _JA_MapRect;  // map terrain xz rectangle: min x, min z, max x, max z
            float4 _JA_Speed;    // x snow streaks (unused here), y edge frost opacity

            // Optical depth of exponential height fog along a ray of length L from the camera.
            float HeightFog(float3 dir, float L)
            {
                float k = max(_JA_Fog.y, 1e-5);
                float h0 = _WorldSpaceCameraPos.y - _JA_Fog.z;
                float base = _JA_Fog.x * exp(-k * clamp(h0, -60.0, 400.0));
                float f = k * dir.y * L;
                float len = abs(f) > 1e-3 ? (1.0 - exp(-f)) / (k * dir.y) : L;
                return base * len;
            }

            // 0..1: how much open sky lies between this pixel and the sun on screen.
            float SunShafts(float2 uv, float3 dir)
            {
                float facing = dot(dir, _JA_SunDir.xyz);
                if (_JA_Shafts.x <= 0.0 || facing <= 0.0 || _JA_SunDir.y < -0.05) return 0.0;
                float4 sc = mul(UNITY_MATRIX_VP, float4(_WorldSpaceCameraPos + _JA_SunDir.xyz * 1000.0, 1.0));
                if (sc.w <= 0.0) return 0.0;
                float2 sunUV = ComputeScreenPos(sc).xy / sc.w;
                float2 d = (sunUV - uv) * _JA_Shafts.y;
                float jitter = PixelNoise(uv);
                float light = 0.0, total = 0.0, w = 1.0;
                const int N = 24;
                [unroll]
                for (int k = 0; k < N; k++)
                {
                    float2 s = uv + d * ((k + jitter) / N);
                    if (all(s > 0.0) && all(s < 1.0))
                    {
                        light += IsSky(RawDepth(s)) ? w : 0.0;
                        total += w;
                    }
                    w *= _JA_Shafts.z;
                }
                float lobe = pow(saturate(facing), max(_JA_Shafts.w, 1.0));
                return total > 0.0 ? light / total * lobe * saturate(_JA_SunDir.y * 6.0 + 0.3) : 0.0;
            }

            // Premultiplied colour + opacity of the valley cloud sea along this ray, up to `maxDist`.
            float4 CloudBank(float2 uv, float3 dir, float maxDist)
            {
                float base = _JA_Bank.y, top = _JA_Bank.z, cy = _WorldSpaceCameraPos.y;
                float t0, t1;
                if (abs(dir.y) < 1e-4)
                {
                    if (cy < base || cy > top) return 0;
                    t0 = 0.0; t1 = 1e5;
                }
                else
                {
                    float a = (base - cy) / dir.y, b = (top - cy) / dir.y;
                    t0 = max(min(a, b), 0.0);
                    t1 = max(a, b);
                }
                t1 = min(t1, min(maxDist, 9000.0));
                if (t1 <= t0) return 0;

                const int N = 20;
                float stepL = (t1 - t0) / N;
                float jitter = PixelNoise(uv);
                float threshold = lerp(0.62, 0.38, _JA_Bank2.z);
                float sigma = _JA_Bank.x * 0.025;
                float T = 1.0;
                float3 acc = 0;
                [loop]
                for (int k = 0; k < N; k++)
                {
                    float t = t0 + (k + jitter) * stepL;
                    float3 p = _WorldSpaceCameraPos + dir * t;
                    float n = Fbm((p.xz - _JA_Bank2.xy * _Time.y) * _JA_Bank.w) + 0.5;
                    float h = saturate((p.y - base) / max(top - base, 1.0));
                    // lumpy tops: each column's cloud top rises with its noise
                    float ceilH = 0.45 + 0.55 * n;
                    float dens = smoothstep(threshold - 0.08, threshold + 0.12, n) * smoothstep(ceilH, ceilH - 0.3, h) * smoothstep(0.0, 0.12, h);
                    dens *= saturate((t - 40.0) / 120.0); // never right in front of the camera
                    // Outside the map it fades in past the edge. Inside, only in low ground far from
                    // and well below the camera (valleys under the park), never near the rider's run.
                    float2 outside = max(max(_JA_MapRect.xy - p.xz, p.xz - _JA_MapRect.zw), 0.0);
                    float edge = smoothstep(0.0, max(_JA_Bank2.w, 1.0), length(outside));
                    float below = smoothstep(cy - 60.0, cy - 160.0, p.y) * smoothstep(350.0, 700.0, t);
                    dens *= max(edge, below);
                    float ext = dens * sigma * stepL;
                    // sunlit tops, cooler and darker underneath
                    float3 c = lerp(_JA_FogColor.rgb * 0.72, _JA_FogColor.rgb * 1.02 + _JA_SunColor.rgb * 0.3, h * h);
                    c *= 0.82 + 0.3 * n; // thicker lumps catch more light: billows instead of a flat sheet
                    float absorbed = 1.0 - exp(-ext);
                    acc += T * absorbed * c;
                    T *= 1.0 - absorbed;
                    if (T < 0.02) break;
                }
                return float4(acc, 1.0 - T);
            }

            float4 frag(v2f i) : SV_Target
            {
                if (_JA_FogColor.a <= 0.0 && _JA_Shafts.x <= 0.0 && _JA_Bank.x <= 0.0 && _JA_Speed.y <= 0.0) discard;
                float3 dir = normalize(i.ray);
                float2 uv = i.scr.xy / i.scr.w;
                float raw = RawDepth(uv);
                bool sky = IsSky(raw);
                float3 fwd = -UNITY_MATRIX_V[2].xyz;
                float dist = LinearEyeDepth(raw) / max(dot(dir, fwd), 1e-3);

                float L = sky ? _JA_Fog2.z : max(dist - _JA_Fog2.x, 0.0);
                float a = 1.0 - exp(-(HeightFog(dir, L) + _JA_Fog2.w * L));
                if (sky)
                {
                    // Haze a band along the horizon only; higher up the game's sky stays in charge.
                    a *= _JA_Fog2.y * exp(-max(dir.y, 0.0) * _JA_FogTop.a);
                }
                a = min(a, _JA_Fog.w) * _JA_FogColor.a;

                float up = saturate(dir.y * 1.5);
                float3 col = lerp(_JA_FogColor.rgb, _JA_FogTop.rgb, up * up);
                float sun = pow(saturate(dot(dir, _JA_SunDir.xyz)), max(_JA_SunColor.w, 1.0));
                col += _JA_SunColor.rgb * sun * _JA_SunDir.w;
                float3 shafts = _JA_SunColor.rgb * SunShafts(uv, dir) * _JA_Shafts.x;

                // cloud sea first, then the fog over it: fog * a + cloud * (1 - a) + scene * (1 - cloud)(1 - a)
                float4 bank = _JA_Bank.x > 0.0 ? CloudBank(uv, dir, sky ? 9000.0 : dist) : 0;
                float3 rgb = col * a + bank.rgb * (1.0 - a) + shafts;
                float alpha = 1.0 - (1.0 - bank.a) * (1.0 - a);

                if (_JA_Speed.y > 0.0)
                {
                    // frost: corners first, broken up by crystal-like noise
                    float aspect = _ScreenParams.x / _ScreenParams.y;
                    float2 c = (uv - 0.5) * float2(aspect, 1.0);
                    float edge = smoothstep(0.5, 1.05, length(c));
                    float crystals = smoothstep(0.35, 0.75, Fbm(uv * float2(aspect, 1.0) * 14.0) + 0.5);
                    float m = saturate(edge * (0.45 + 0.55 * crystals) * _JA_Speed.y);
                    float3 frost = float3(0.86, 0.93, 1.0) * max(dot(_JA_FogColor.rgb, 0.333), 0.6);
                    rgb = rgb * (1.0 - m) + frost * m;
                    alpha = 1.0 - (1.0 - alpha) * (1.0 - m);
                }
                return float4(rgb, alpha);
            }
            ENDCG
        }
    }
}
