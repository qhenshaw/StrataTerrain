// Texturing for Strata/TerrainSurface's forward pass; included after StrataCommon.hlsl. A pixel finds its four
// strongest palette layers from the splat maps (merging the four nearest splat texels), samples each layer's albedo,
// normal and mask maps (top-down on gentle ground, triplanar on steep ground, optionally hex-tiled, and blended towards
// a larger tiling with distance), height-blends the layers, and returns the surface for URP's PBR lighting.
#ifndef STRATA_SURFACE_INCLUDED
#define STRATA_SURFACE_INCLUDED

TEXTURE2D_ARRAY(_StrataSplatIndices);
TEXTURE2D_ARRAY(_StrataSplatWeights);
TEXTURE2D(_StrataFarSplatIndices);      // every fourth splat texel of the whole world, where tiles aren't resident
TEXTURE2D(_StrataFarSplatWeights);
TEXTURE2D_ARRAY(_StrataLayerAlbedo);
SAMPLER(sampler_StrataLayerAlbedo);
TEXTURE2D_ARRAY(_StrataLayerNormal);
TEXTURE2D_ARRAY(_StrataLayerMask);
StructuredBuffer<float4> _StrataLayerData;  // 3 per layer: (1/tile, normal strength, blend depth, flags), (tint, smoothness), (1/far scale, occlusion)

#define STRATA_HAS_ALBEDO 1
#define STRATA_HAS_NORMAL 2
#define STRATA_HAS_MASK 4
#define STRATA_STOCHASTIC 8
#define STRATA_MASK_MHER 16
#define STRATA_NORMAL_DIRECTX 32

struct StrataLayers
{
    int4 index;
    float4 weight;
};

// Adds weight for a layer to the four slots, merging with a slot already holding it, or replacing the weakest slot
// if the new weight is larger.
// Written with whole-vector masks rather than indexing slots, which the compiler can't do in unrolled loops.
void AddLayer(inout StrataLayers layers, int index, float weight)
{
    if (weight <= 0.0) return;
    float4 same = (float4)(layers.index == index);
    if (dot(same, 1.0) > 0.0)
    {
        layers.weight += same * weight;
        return;
    }
    float weakest = min(min(layers.weight.x, layers.weight.y), min(layers.weight.z, layers.weight.w));
    if (weight <= weakest) return;
    float4 isWeakest = (float4)(layers.weight == weakest);
    float4 first = isWeakest * float4(1.0, 1.0 - isWeakest.x, (1.0 - isWeakest.x) * (1.0 - isWeakest.y),
                                      (1.0 - isWeakest.x) * (1.0 - isWeakest.y) * (1.0 - isWeakest.z));
    layers.index = (int4)round(lerp((float4)layers.index, (float)index, first));
    layers.weight = lerp(layers.weight, weight, first);
}

StrataLayers StrataSplat(float2 positionXZ)
{
    float2 unit, relative;
    int slice = StrataSlot(positionXZ, unit, relative);
    bool far = slice < 0;
    float2 resolution = far ? _StrataFar.zw : _StrataSplatParams.xx;
    float2 local = far ? saturate(relative / float2(_StrataGrid.w, _StrataLod.z)) * resolution : unit * resolution;
    int2 i0 = min((int2)floor(local), (int2)resolution - 1);
    float2 f = local - i0;

    StrataLayers layers;
    layers.index = -1;
    layers.weight = 0.0;
    [unroll] for (int t = 0; t < 4; t++)
    {
        int2 offset = int2(t & 1, t >> 1);
        float bilinear = (offset.x ? f.x : 1.0 - f.x) * (offset.y ? f.y : 1.0 - f.y);
        int4 index = (int4)round((far ? LOAD_TEXTURE2D(_StrataFarSplatIndices, i0 + offset) : LOAD_TEXTURE2D_ARRAY(_StrataSplatIndices, i0 + offset, slice)) * 255.0);
        float4 weight = (far ? LOAD_TEXTURE2D(_StrataFarSplatWeights, i0 + offset) : LOAD_TEXTURE2D_ARRAY(_StrataSplatWeights, i0 + offset, slice)) * bilinear;
        AddLayer(layers, index.x, weight.x);
        AddLayer(layers, index.y, weight.y);
        AddLayer(layers, index.z, weight.z);
        AddLayer(layers, index.w, weight.w);
    }
    layers.weight /= max(dot(layers.weight, 1.0), 1e-5);
    return layers;
}

float2 StrataHash2(int2 p)
{
    uint2 v = asuint(p) * uint2(1597334677u, 3812015801u);
    uint h = (v.x ^ v.y) * 1597334677u;
    return float2(h & 0xffffu, h >> 16) / 65535.0;
}

// One map sampled at uv: plainly, or hex-tiled (three randomly offset copies blended over a triangle grid).
float4 SampleMap(TEXTURE2D_ARRAY_PARAM(map, samp), float2 uv, int layer, bool stochastic)
{
    float2 dx = ddx(uv), dy = ddy(uv);
    if (!stochastic) return SAMPLE_TEXTURE2D_ARRAY_GRAD(map, samp, uv, layer, dx, dy);

    float2 skewed = mul(float2x2(1.0, 0.0, -0.57735027, 1.15470054), uv * 3.4641016);
    int2 base = (int2)floor(skewed);
    float3 t = float3(frac(skewed), 0.0);
    t.z = 1.0 - t.x - t.y;
    float3 w;
    int2 v1, v2, v3;
    if (t.z > 0.0) { w = float3(t.z, t.y, t.x); v1 = base; v2 = base + int2(0, 1); v3 = base + int2(1, 0); }
    else { w = float3(-t.z, 1.0 - t.y, 1.0 - t.x); v1 = base + int2(1, 1); v2 = base + int2(1, 0); v3 = base + int2(0, 1); }
    w = w * w * w * w;
    w /= dot(w, 1.0);
    return SAMPLE_TEXTURE2D_ARRAY_GRAD(map, samp, uv + StrataHash2(v1) * 7.0, layer, dx, dy) * w.x +
           SAMPLE_TEXTURE2D_ARRAY_GRAD(map, samp, uv + StrataHash2(v2) * 7.0, layer, dx, dy) * w.y +
           SAMPLE_TEXTURE2D_ARRAY_GRAD(map, samp, uv + StrataHash2(v3) * 7.0, layer, dx, dy) * w.z;
}

struct LayerSurface
{
    half3 albedo;
    half3 normalTS;     // tangent space of the projection
    half4 mask;         // metallic, occlusion, height, smoothness
};

// A layer at one projection's uv: near tiling, far tiling, or a distance blend of both. Only the near tiling samples
// stochastically (three taps a map): far away the larger tiling already hides repeats.
LayerSurface SampleProjection(int layer, float2 uv, float far, float4 data0, float4 data2)
{
    int flags = (int)data0.w;
    bool stochastic = (flags & STRATA_STOCHASTIC) != 0;
    float2 nearUV = uv * data0.x;
    float2 farUV = nearUV * data2.x;
    LayerSurface s;
    s.albedo = 0.5;
    s.normalTS = half3(0, 0, 1);
    s.mask = half4(0, 1, 0.5, 0.2);
    [unroll] for (int step = 0; step < 2; step++)
    {
        float amount = step == 0 ? 1.0 - far : far;
        if (amount <= 0.001) continue;
        float2 at = step == 0 ? nearUV : farUV;
        bool scatter = stochastic && step == 0;
        half3 albedo = (flags & STRATA_HAS_ALBEDO) ? SampleMap(TEXTURE2D_ARRAY_ARGS(_StrataLayerAlbedo, sampler_StrataLayerAlbedo), at, layer, scatter).rgb : 0.5;
        half3 normal = (flags & STRATA_HAS_NORMAL) ? UnpackNormal(SampleMap(TEXTURE2D_ARRAY_ARGS(_StrataLayerNormal, sampler_StrataLayerAlbedo), at, layer, scatter)) : half3(0, 0, 1);
        half4 mask = (flags & STRATA_HAS_MASK) ? SampleMap(TEXTURE2D_ARRAY_ARGS(_StrataLayerMask, sampler_StrataLayerAlbedo), at, layer, scatter) : half4(0, 1, 0.5, 0.2);
        // metallic, height, emission, roughness to metallic, occlusion, height, smoothness
        if ((flags & (STRATA_HAS_MASK | STRATA_MASK_MHER)) == (STRATA_HAS_MASK | STRATA_MASK_MHER)) mask = half4(mask.r, 1.0, mask.g, 1.0 - mask.a);
        if (flags & STRATA_NORMAL_DIRECTX) normal.y = -normal.y;
        if (step == 0 || amount >= 0.999) { s.albedo = albedo; s.normalTS = normal; s.mask = mask; }
        else { s.albedo = lerp(s.albedo, albedo, amount); s.normalTS = normalize(lerp(s.normalTS, normal, amount)); s.mask = lerp(s.mask, mask, amount); }
    }
    s.normalTS.xy *= data0.y;
    return s;
}

struct StrataSurfaceOutput
{
    half3 albedo;
    float3 normalWS;
    half metallic;
    half occlusion;
    half smoothness;
};

float StrataValueNoise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = StrataHash2((int2)i).x, b = StrataHash2((int2)i + int2(1, 0)).x;
    float c = StrataHash2((int2)i + int2(0, 1)).x, d = StrataHash2((int2)i + int2(1, 1)).x;
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// distinct colours for the first eight layers (red, green, blue, yellow, cyan, magenta, white, orange), then cycling
half3 StrataDebugColour(int index)
{
    static const half3 colours[8] = { half3(1, 0, 0), half3(0, 1, 0), half3(0, 0, 1), half3(1, 1, 0),
                                      half3(0, 1, 1), half3(1, 0, 1), half3(1, 1, 1), half3(1, 0.5, 0) };
    return colours[index % 8] * (index < 8 ? 1.0 : 0.6);
}

// The surface from the given layers and rock strata, at a position with its geometric normal, seen from a distance;
// forceTriplanar 1 projects triplanar whatever the slope (cave walls and ceilings).
StrataSurfaceOutput StrataSurfaceWith(StrataLayers layers, StrataRock rock, float3 positionWS, float3 geometricNormal, float distance,
                                      float forceTriplanar)
{
    // rock strata: the beds shape the normal; on steep faces each bed gets its own tint and, if asked, textures that
    // run along the bed (v follows the bed, u shifted per bed) instead of plain triplanar
    geometricNormal = StrataBandNormal(geometricNormal, positionWS, distance, rock);
    float sideV = positionWS.y;
    float sideShift = 0.0;
    half bandBrightness = 1.0;
    half3 bandHue = 1.0;
    if (rock.weight > 0.0)
    {
        StrataDefinition d = rock.definition;
        float bandHeight = StrataBandHeight(d, positionWS);
        float bed, bottom, bedTop;
        StrataBed(d, bandHeight, bed, bottom, bedTop);
        if (d.alignedUVs > 0.5 && rock.weight > 0.5)
        {
            sideV = bandHeight;
            sideShift = StrataHash(bed, d.seed + 3.0) * 37.0;
        }
        float tint = d.tint * rock.weight * saturate(length(geometricNormal.xz) * 1.5);
        bandBrightness = 1.0 + (StrataHash(bed, d.seed + 11.0) - 0.5) * 1.2 * tint;
        bandHue = lerp(1.0, lerp(half3(1.06, 1.0, 0.92), half3(0.94, 0.98, 1.05), StrataHash(bed, d.seed + 23.0)), tint);
    }

    // projection weights: straight down on gentle ground, triplanar as it steepens
    float steep = max(saturate((_TriplanarStart - geometricNormal.y) / max(_TriplanarStart - _TriplanarEnd, 1e-3)), forceTriplanar);
    float3 tri = pow(abs(geometricNormal), _TriplanarSharpness);
    tri /= dot(tri, 1.0);
    float3 projection = lerp(float3(0, 1, 0), tri, steep);
    float3 axisSign = sign(geometricNormal + 1e-5);
    float far = saturate((distance - _FarStart) / max(_FarEnd - _FarStart, 1e-3));

    half3 albedo[4];
    float3 normal[4];
    half4 mask[4];
    float blendHeight[4];
    float top = -1e5;
    float depth = 0.0;
    [unroll] for (int k = 0; k < 4; k++)
    {
        albedo[k] = 0;
        normal[k] = geometricNormal;
        mask[k] = half4(0, 1, 0, 0);
        blendHeight[k] = -1e5;
        float weight = layers.weight[k];
        int layer = layers.index[k];
        if (weight < 0.003 || layer < 0) continue;
        float4 data0 = _StrataLayerData[layer * 3];
        float4 data1 = _StrataLayerData[layer * 3 + 1];
        float4 data2 = _StrataLayerData[layer * 3 + 2];

        half3 a = 0;
        half4 m = 0;
        float3 n = 0;
        // whiteout-blended triplanar normals (Ben Golus), each projection's tangent normal lifted onto its axis
        if (projection.x > 0.01)
        {
            LayerSurface s = SampleProjection(layer, float2(positionWS.z * axisSign.x + sideShift, sideV), far, data0, data2);
            s.normalTS.x *= axisSign.x;
            float3 nx = float3(s.normalTS.xy + geometricNormal.zy, abs(s.normalTS.z) * geometricNormal.x);
            a += s.albedo * projection.x; m += s.mask * projection.x; n += nx.zyx * projection.x;
        }
        if (projection.y > 0.01)
        {
            LayerSurface s = SampleProjection(layer, positionWS.xz * float2(axisSign.y, 1), far, data0, data2);
            s.normalTS.x *= axisSign.y;
            float3 ny = float3(s.normalTS.xy + geometricNormal.xz, abs(s.normalTS.z) * geometricNormal.y);
            a += s.albedo * projection.y; m += s.mask * projection.y; n += ny.xzy * projection.y;
        }
        if (projection.z > 0.01)
        {
            LayerSurface s = SampleProjection(layer, float2(positionWS.x * -axisSign.z + sideShift, sideV), far, data0, data2);
            s.normalTS.x *= -axisSign.z;
            float3 nz = float3(s.normalTS.xy + geometricNormal.xy, abs(s.normalTS.z) * geometricNormal.z);
            a += s.albedo * projection.z; m += s.mask * projection.z; n += nz * projection.z;
        }
        albedo[k] = a * data1.rgb;
        normal[k] = normalize(n);
        mask[k] = half4(m.r, lerp(1.0, m.g, data2.y), m.b, m.a * data1.a);
        blendHeight[k] = m.b + weight;
        top = max(top, blendHeight[k]);
        depth += data0.z * weight;
    }

    // height blending: only layers whose height (plus weight) comes within the blend depth of the top one show
    float4 blend;
    [unroll] for (int k2 = 0; k2 < 4; k2++) blend[k2] = max(blendHeight[k2] - (top - depth), 0.0);
    blend /= max(dot(blend, 1.0), 1e-5);

    StrataSurfaceOutput o;
    o.albedo = 0;
    o.normalWS = 0;
    half4 m = 0;
    [unroll] for (int k3 = 0; k3 < 4; k3++)
    {
        o.albedo += albedo[k3] * blend[k3];
        o.normalWS += normal[k3] * blend[k3];
        m += mask[k3] * blend[k3];
    }
    o.normalWS = normalize(o.normalWS);
    o.metallic = m.r;
    o.occlusion = m.g;
    o.smoothness = m.a;

    o.albedo *= bandBrightness * bandHue;

    // large-scale brightness variation so distant tiling doesn't read as a pattern
    float macro = StrataValueNoise(positionWS.xz / _MacroScale) * 0.65 + StrataValueNoise(positionWS.xz / (_MacroScale * 0.27)) * 0.35;
    o.albedo *= 1.0 + (macro - 0.5) * 2.0 * _MacroStrength;

    if (_DebugView > 2.5)
        o.albedo = half3(rock.weight, rock.weight > 0.5 && rock.definition.alignedUVs > 0.5 ? 1.0 : 0.0, 0.0);
    else if (_DebugView > 1.5)
        o.albedo = projection;
    else if (_DebugView > 0.5)
    {
        o.albedo = 0;
        [unroll] for (int k4 = 0; k4 < 4; k4++)
            if (layers.index[k4] >= 0) o.albedo += StrataDebugColour(layers.index[k4]) * blend[k4];
    }
    return o;
}

// The terrain's surface: its splats' layers and rock strata at the position.
StrataSurfaceOutput StrataSurface(float3 positionWS, float3 geometricNormal, float distance)
{
    return StrataSurfaceWith(StrataSplat(positionWS.xz), StrataRockAt(positionWS.xz), positionWS, geometricNormal, distance, 0.0);
}

#endif
