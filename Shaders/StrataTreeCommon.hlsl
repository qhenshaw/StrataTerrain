// Shared by Strata/Tree and Strata/TreeImpostor (included after URP's Core.hlsl and the shader's UnityPerMaterial,
// which must declare float4 _StrataTree): unpacking a tree instance, wind, the per-tree colour variation, LOD
// crossfade dithering, and the hemi-octahedral layout of impostor frames.
#ifndef STRATA_TREE_COMMON_INCLUDED
#define STRATA_TREE_COMMON_INCLUDED

#ifdef UNITY_DOTS_INSTANCING_ENABLED
    UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
        UNITY_DOTS_INSTANCED_PROP(float4, _StrataTree)
    UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
    #define _StrataTree UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _StrataTree)
#endif

#define STRATA_LEAN_STEP 2.5
#define STRATA_SCALE_STEP (1.0 / 64.0)

// Where one tree stands: its base, how it's turned, leant and scaled (StrataTreeRecord's low 24 bits, as a float).
struct StrataTreeSpace
{
    float3 origin;
    float3x3 rotation;
    float scale;
    float2 sinCos;
    float fade;         // LOD crossfade: 0 none, > 0 drawn where the dither is below it, < 0 where it is above -fade
};

StrataTreeSpace StrataTreeInstance()
{
    float4 tree = _StrataTree;
    uint bits = (uint)tree.w;
    StrataTreeSpace space;
    space.origin = tree.xyz;
    float yaw = (bits & 0xffu) * (TWO_PI / 256.0);
    space.scale = max(((bits >> 8) & 0xffu) * STRATA_SCALE_STEP, STRATA_SCALE_STEP);
    int2 leanSteps = int2((int)(bits << 12) >> 28, (int)(bits << 8) >> 28);
    float2 lean = radians((float2)leanSteps * STRATA_LEAN_STEP);

    sincos(yaw, space.sinCos.x, space.sinCos.y);
    float sx, cx, sz, cz;
    sincos(lean.x, sx, cx);
    sincos(lean.y, sz, cz);
    // Quaternion.Euler(0, yaw, 0) first, then the lean about z and x
    float3x3 rotateY = float3x3(space.sinCos.y, 0, space.sinCos.x, 0, 1, 0, -space.sinCos.x, 0, space.sinCos.y);
    float3x3 rotateZ = float3x3(cz, -sz, 0, sz, cz, 0, 0, 0, 1);
    float3x3 rotateX = float3x3(1, 0, 0, 0, cx, -sx, 0, sx, cx);
    space.rotation = mul(rotateX, mul(rotateZ, rotateY));
    #ifdef UNITY_DOTS_INSTANCING_ENABLED
        space.fade = unity_LODFade.x;
    #else
        space.fade = 0.0;
    #endif
    return space;
}

float3 StrataTreeToWorld(StrataTreeSpace space, float3 positionOS)
{
    return space.origin + mul(space.rotation, positionOS * space.scale);
}

float3 StrataTreeNormalToWorld(StrataTreeSpace space, float3 normalOS)
{
    return normalize(mul(space.rotation, normalOS));
}

// Rotates about y the way Quaternion.Euler(0, yaw, 0) does, and back.
float3 StrataRotateY(float3 v, float2 sinCos)
{
    return float3(sinCos.y * v.x + sinCos.x * v.z, v.y, -sinCos.x * v.x + sinCos.y * v.z);
}

float3 StrataUnrotateY(float3 v, float2 sinCos)
{
    return float3(sinCos.y * v.x - sinCos.x * v.z, v.y, sinCos.x * v.x + sinCos.y * v.z);
}

// Slow gusts that roll across the land, with a quicker flutter on top; weight is how freely the vertex moves.
float3 StrataWind(float3 positionWS, float3 origin, float weight, float strength, float speed, float scale, float2 direction)
{
    float t = _Time.y * speed;
    float phase = dot(origin.xz, float2(scale, scale * 0.7));
    float gust = sin(t + phase) * 0.7 + sin(t * 2.3 + phase * 1.7) * 0.3;
    float flutter = sin(t * 6.1 + dot(positionWS, float3(0.9, 0.7, 1.1))) * 0.15;
    positionWS.xz += normalize(direction + float2(0.0001, 0)) * ((gust * 0.5 + 0.5) + flutter) * strength * weight;
    return positionWS;
}

float StrataTreeHash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

// Each tree a little brighter or darker, its leaves shifted towards a cooler or a warmer green.
float3 StrataTreeTint(float3 colour, float3 origin, float foliage, float hueVariation, float brightnessVariation)
{
    float brightness = StrataTreeHash(origin.xz * 0.013 + 3.1);
    float hue = StrataTreeHash(origin.xz * 0.029 + 17.0);
    colour *= 1.0 + (brightness - 0.5) * 2.0 * brightnessVariation;
    float3 warm = colour * float3(1.2, 1.06, 0.68);
    float3 cool = colour * float3(0.78, 0.97, 1.14);
    return lerp(colour, hue > 0.5 ? warm : cool, abs(hue - 0.5) * 4.0 * hueVariation * foliage);
}

// The two LODs of a crossfading tree carry the same fade with opposite signs, so every pixel is drawn by exactly one.
void StrataFadeClip(float2 pixel, float fade)
{
    if (fade == 0.0) return;
    static const float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
    uint2 cell = (uint2)pixel % 4u;
    float dither = (bayer[cell.y * 4 + cell.x] + 0.5) / 16.0;
    clip(fade > 0.0 ? fade - dither : dither + fade);
}

// Impostor frames cover the upper hemisphere of view directions, laid out octahedrally: the middle of the grid looks
// straight down, its corners and edges look in from the horizon.
float2 StrataHemiOctEncode(float3 direction)
{
    direction.y = max(direction.y, 0.0);
    direction /= abs(direction.x) + abs(direction.y) + abs(direction.z);
    return float2(direction.x + direction.z, direction.x - direction.z);
}

float3 StrataHemiOctDecode(float2 encoded)
{
    float x = (encoded.x + encoded.y) * 0.5;
    float z = (encoded.x - encoded.y) * 0.5;
    return normalize(float3(x, 1.0 - abs(x) - abs(z), z));
}

#endif
