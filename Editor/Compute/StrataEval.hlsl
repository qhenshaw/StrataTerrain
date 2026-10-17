// Shared code for Strata's evaluation compute shaders. Every dispatch works on a "region": a block of world samples
// held in an RFloat texture of world heights, starting at world sample _Region.xy. A feature's dispatch covers only
// _Clip (region-local min and size), the part of the region it can touch.
#ifndef STRATA_EVAL_INCLUDED
#define STRATA_EVAL_INCLUDED

#define BLEND_ADD 0
#define BLEND_SUBTRACT 1
#define BLEND_MAX 2
#define BLEND_MIN 3
#define BLEND_SET 4

int4 _Region;           // world sample origin xy, size zw
int4 _Clip;             // region-local min xy, size zw
float4 _Grid;           // world origin x, z, sample spacing, unused
int _Blend;
float _Strength;
float4 _MaskHeight;     // enabled, min, max, falloff (m)
float4 _MaskSlope;      // enabled, min, max, falloff (degrees)
StructuredBuffer<float> _Curve;     // 64 samples of a 0..1 curve
StructuredBuffer<float> _Profile;   // 64 samples of a 0..1 curve

// The heights as they were before this dispatch, for reading neighbours (slope masks).
Texture2D<float> _Before;
SamplerState sampler_linear_clamp;

float2 WorldPosition(int2 local)
{
    return _Grid.xy + (float2)(_Region.xy + local) * _Grid.z;
}

bool ClipThread(uint2 id, out int2 local)
{
    local = _Clip.xy + (int2)id;
    return any((int2)id >= _Clip.zw);
}

float SampleCurve(StructuredBuffer<float> curve, float t)
{
    float x = saturate(t) * 63.0;
    int i = min((int)x, 62);
    return lerp(curve[i], curve[i + 1], x - i);
}

float BeforeAt(int2 local)
{
    return _Before[clamp(local, 0, _Region.zw - 1)];
}

// 0..1: how much the mask lets a feature act on this sample.
float MaskWeight(int2 local, float height)
{
    float weight = 1.0;
    if (_MaskHeight.x > 0.5)
    {
        float falloff = max(_MaskHeight.w, 1e-3);
        weight *= saturate((height - _MaskHeight.y) / falloff + 1.0) * saturate((_MaskHeight.z - height) / falloff + 1.0);
    }
    if (_MaskSlope.x > 0.5)
    {
        float dx = (BeforeAt(local + int2(1, 0)) - BeforeAt(local - int2(1, 0))) / (2.0 * _Grid.z);
        float dz = (BeforeAt(local + int2(0, 1)) - BeforeAt(local - int2(0, 1))) / (2.0 * _Grid.z);
        float slope = degrees(atan(length(float2(dx, dz))));
        float falloff = max(_MaskSlope.w, 1e-3);
        weight *= saturate((slope - _MaskSlope.y) / falloff + 1.0) * saturate((_MaskSlope.z - slope) / falloff + 1.0);
    }
    return weight;
}

// amount is used by Add and Subtract, target by Max, Min and Set.
float ApplyBlend(float height, float amount, float target, float weight)
{
    switch (_Blend)
    {
        case BLEND_ADD: return height + amount * weight;
        case BLEND_SUBTRACT: return height - amount * weight;
        case BLEND_MAX: return lerp(height, max(height, target), weight);
        case BLEND_MIN: return lerp(height, min(height, target), weight);
        default: return lerp(height, target, weight);
    }
}

// A footprint on the ground: centre xz, y rotation as (cos, sin), size. Returns uv in 0..1 inside it.
float4 _Footprint;      // centre x, centre z, cos yaw, sin yaw
float4 _FootprintSize;  // 1 / size x, 1 / size z, edge falloff (fraction of size), base height

float2 FootprintUV(float2 position)
{
    float2 d = position - _Footprint.xy;
    float2 axisX = float2(_Footprint.z, -_Footprint.w);
    float2 axisZ = float2(_Footprint.w, _Footprint.z);
    return float2(dot(d, axisX), dot(d, axisZ)) * _FootprintSize.xy + 0.5;
}

// 0 outside the footprint, rising through the falloff curve over the edge band to 1 inside.
float FootprintWeight(float2 uv)
{
    if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
    float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
    return _FootprintSize.z > 0.0 ? SampleCurve(_Curve, edge / _FootprintSize.z) : 1.0;
}

#endif
