// Falling snow around the camera, entirely on the GPU. The mesh holds N camera-facing quads; each
// quad's position (0..1 seed) is advanced by time, wind and fall speed and wrapped into a box that
// travels with the camera, so flakes stay put in the world while the rider moves through them.
// Globals are set by the Atmosphere mod; with _JA_Snow.x = 0 every quad collapses and nothing draws.
Shader "Hidden/JibbersAtmosphere/Snow"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+50" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            float4 _JA_Snow;      // x fraction of flakes shown (0..1), y box size (m), z fall speed (m/s), w flake radius (m)
            float4 _JA_Wind;      // xyz wind (m/s), w flutter (m)
            float4 _JA_SnowColor; // rgb lit flake colour, a opacity
            float4 _JA_SunDir;    // xyz towards the sun
            float4 _JA_SunColor;  // rgb sun colour (sparkle)
            float4 _JA_CamVel;    // xyz camera velocity (m/s), w speed
            float4 _JA_Speed;     // x streak amount

            struct appdata
            {
                float4 vertex : POSITION;   // xyz seed in 0..1
                float2 corner : TEXCOORD0;  // -1..1
                float4 rnd : TEXCOORD1;     // x size, y speed, z phase, w visibility threshold
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 corner : TEXCOORD0;
                float2 fadeSpark : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.corner = v.corner;
                o.fadeSpark = 0;
                if (v.rnd.w >= _JA_Snow.x || _JA_Snow.y <= 0.0)
                {
                    o.pos = float4(2, 2, 2, 1); // outside the clip volume: culled
                    return o;
                }
                float t = _Time.y;
                float B = _JA_Snow.y;
                float speed = _JA_Snow.z * (0.65 + 0.7 * v.rnd.y);
                float3 p = v.vertex.xyz * B;
                p += _JA_Wind.xyz * t * (0.85 + 0.3 * v.rnd.y);
                p.y -= speed * t;
                float ph = v.rnd.z * 6.2832 + t * (0.8 + 0.9 * v.rnd.y);
                p.xz += float2(sin(ph), cos(ph * 0.73)) * _JA_Wind.w;

                float3 rel = (frac((p - _WorldSpaceCameraPos) / B) - 0.5) * B;
                float d = length(rel);
                float fade = saturate((d - 0.35) / 1.2) * saturate((B * 0.5 - d) / (B * 0.2));

                float size = _JA_Snow.w * (0.55 + 0.9 * v.rnd.x);
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;
                // Motion streak: the flake's velocity relative to the camera, across the view, smeared
                // over a short exposure. At rest (or moving straight at a flake) it stays round.
                float3 flakeVel = _JA_Wind.xyz - float3(0, speed, 0);
                float3 relVel = flakeVel - _JA_CamVel.xyz;
                float3 viewDir = normalize(rel);
                float3 across = relVel - viewDir * dot(relVel, viewDir);
                // (minus a flake's width, so drifting snow seen standing still stays round)
                float smear = max(length(across) * (1.0 / 50.0) - size * 0.6, 0.0) * _JA_Speed.x;
                float3 axisA = right, axisB = up;
                float lenA = size;
                if (smear > 0.001)
                {
                    axisA = across / length(across);
                    axisB = normalize(cross(axisA, viewDir));
                    lenA = size + smear;
                }
                float3 w = _WorldSpaceCameraPos + rel + (axisA * v.corner.x * lenA + axisB * v.corner.y * size);
                o.pos = UnityWorldToClipPos(w);
                // A streak spreads the same light over a longer shape.
                fade *= sqrt(size / lenA);

                // A few flakes glint when they tumble towards the sun.
                float glint = pow(saturate(sin(t * (2.0 + 4.0 * v.rnd.x) + v.rnd.z * 40.0)), 24.0);
                float facing = saturate(dot(normalize(-rel), _JA_SunDir.xyz) * 0.5 + 0.5);
                o.fadeSpark = float2(fade * _JA_SnowColor.a, glint * facing);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float r2 = dot(i.corner, i.corner);
                float a = saturate(1.0 - r2);
                a *= a * i.fadeSpark.x;
                if (a <= 0.001) discard;
                float3 col = _JA_SnowColor.rgb + _JA_SunColor.rgb * i.fadeSpark.y * 1.5;
                return float4(col * a, a);
            }
            ENDCG
        }
    }
}
