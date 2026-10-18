// Shared by the screen-space atmosphere passes: a camera-centred shell whose pixels know their view
// ray, plus helpers to rebuild world positions and normals from the scene depth.
#ifndef JIBBERS_COMMON
#define JIBBERS_COMMON

#include "UnityCG.cginc"

UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

float4 _JA_SunDir;   // xyz towards the sun, w forward scattering strength
float4 _JA_SunColor; // rgb sun tint, w glow lobe sharpness

struct v2f
{
    float4 pos : SV_POSITION;
    float3 ray : TEXCOORD0;
    float4 scr : TEXCOORD1;
};

v2f vert(float4 vertex : POSITION)
{
    v2f o;
    // Unit cube scaled to sit inside the far plane (corners at 0.9 far).
    float s = _ProjectionParams.z * 0.52;
    float3 w = _WorldSpaceCameraPos + vertex.xyz * s;
    o.pos = UnityWorldToClipPos(w);
    o.ray = w - _WorldSpaceCameraPos;
    o.scr = ComputeScreenPos(o.pos);
    return o;
}

float RawDepth(float2 uv)
{
    return SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0));
}

bool IsSky(float raw)
{
    return Linear01Depth(raw) > 0.9995;
}

// Screen-space frame of the current pixel: its uv, its view ray scaled to unit eye depth (affine in
// screen space, so any pixel's ray follows from the derivatives), and both per-pixel steps.
struct ScreenFrame
{
    float2 uv;
    float2 duvX;
    float2 duvY;
    float3 ray;
    float3 drayX;
    float3 drayY;
};

ScreenFrame MakeFrame(v2f i)
{
    ScreenFrame f;
    f.uv = i.scr.xy / i.scr.w;
    f.duvX = ddx(f.uv);
    f.duvY = ddy(f.uv);
    float3 fwd = -UNITY_MATRIX_V[2].xyz;
    f.ray = i.ray / dot(i.ray, fwd);
    f.drayX = ddx(f.ray);
    f.drayY = ddy(f.ray);
    return f;
}

// World position of the surface `o` pixels away from the current one.
float3 WorldAt(ScreenFrame f, float2 o)
{
    float2 uv = f.uv + f.duvX * o.x + f.duvY * o.y;
    float eye = LinearEyeDepth(RawDepth(uv));
    return _WorldSpaceCameraPos + (f.ray + f.drayX * o.x + f.drayY * o.y) * eye;
}

// Surface normal from the neighbours on the smoother side (no smearing across silhouettes).
float3 NormalAt(ScreenFrame f, float3 p, float3 viewDir)
{
    float3 l = WorldAt(f, float2(-1, 0));
    float3 r = WorldAt(f, float2(1, 0));
    float3 d = WorldAt(f, float2(0, -1));
    float3 u = WorldAt(f, float2(0, 1));
    float3 dx = dot(r - p, r - p) < dot(p - l, p - l) ? r - p : p - l;
    float3 dy = dot(u - p, u - p) < dot(p - d, p - d) ? u - p : p - d;
    float3 n = normalize(cross(dx, dy));
    return dot(n, viewDir) < 0.0 ? n : -n; // face the camera
}

// Interleaved gradient noise: per-pixel jitter that reads as fine grain, not blotches.
float PixelNoise(float2 uv)
{
    return frac(52.9829189 * frac(dot(uv * _ScreenParams.xy, float2(0.06711056, 0.00583715))));
}

float2 Hash22(float2 p)
{
    float3 p3 = frac(p.xyx * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy) * 2.0 - 1.0;
}

// Gradient noise in about -0.5..0.5.
float GradientNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = dot(Hash22(i), f);
    float b = dot(Hash22(i + float2(1, 0)), f - float2(1, 0));
    float c = dot(Hash22(i + float2(0, 1)), f - float2(0, 1));
    float d = dot(Hash22(i + float2(1, 1)), f - float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float Fbm(float2 p)
{
    const float2x2 rot = float2x2(0.8, -0.6, 0.6, 0.8);
    float s = 0.0;
    float a = 0.5;
    [unroll]
    for (int k = 0; k < 5; k++)
    {
        s += a * GradientNoise(p);
        p = mul(rot, p) * 2.02 + 11.7;
        a *= 0.5;
    }
    return s;
}

#endif
