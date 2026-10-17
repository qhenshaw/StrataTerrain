// Shared code for Strata/TerrainSurface: the per-material and per-instance properties, height lookups across the
// world's tiles (each tile's slot in the texture arrays, or the far copy where it isn't resident), and CDLOD placement
// of the shared patch mesh. Included after URP's Core.hlsl.
#ifndef STRATA_COMMON_INCLUDED
#define STRATA_COMMON_INCLUDED

CBUFFER_START(UnityPerMaterial)
    float4 _StrataNode;         // per instance: origin x, origin z, size, level
    float4 _StrataGrid;         // origin x, origin z, tile size, tiles x
    float4 _StrataHeightParams; // min height, height range, quads per tile, sample spacing
    float4 _StrataLod;          // leaf range, morph start (fraction of range), tiles z, unused
    float4 _StrataSplatParams;  // splat quads per tile, layer count, unused, unused
    float4 _StrataFar;          // far height quads x, z, far splat quads x, z
    float _TriplanarStart;
    float _TriplanarEnd;
    float _TriplanarSharpness;
    float _FarStart;
    float _FarEnd;
    float _MacroScale;
    float _MacroStrength;
    float _GridSpacing;
    float _GridStrength;
    float _DebugView;
CBUFFER_END

#ifdef UNITY_DOTS_INSTANCING_ENABLED
    UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
        UNITY_DOTS_INSTANCED_PROP(float4, _StrataNode)
    UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
    #define _StrataNode UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _StrataNode)
#endif

#include "StrataBands.hlsl"

TEXTURE2D_ARRAY(_StrataHeights);
TEXTURE2D(_StrataFarHeights);
StructuredBuffer<int> _StrataTileSlots;     // per tile: its slot in the arrays, or -1
SAMPLER(sampler_linear_clamp);

#define STRATA_FAR_STEP 8.0

// The camera the CDLOD levels were chosen for; set per camera so shadow passes morph exactly like the main view.
float3 _StrataCameraPosition;

struct Attributes
{
    float4 positionOS : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// The tile holding a world xz, its position within the tile (0..1), and the tile's slot (-1 if not resident).
int StrataSlot(float2 positionXZ, out float2 local, out float2 relative)
{
    float2 tiles = float2(_StrataGrid.w, _StrataLod.z);
    relative = (positionXZ - _StrataGrid.xy) / _StrataGrid.z;
    float2 tile = clamp(floor(relative), 0.0, tiles - 1.0);
    local = clamp(relative - tile, 0.0, 1.0);
    return _StrataTileSlots[(int)(tile.y * tiles.x + tile.x)];
}

bool StrataResident(float2 positionXZ)
{
    float2 local, relative;
    return StrataSlot(positionXZ, local, relative) >= 0;
}

// World height at a world xz, bilinear within whichever tile holds it (tiles share their edge samples), or from the
// far copy (every eighth sample of the whole world) where that tile isn't resident.
float StrataHeight(float2 positionXZ)
{
    float2 local, relative;
    int slot = StrataSlot(positionXZ, local, relative);
    if (slot < 0)
    {
        float2 farUV = (saturate(relative / float2(_StrataGrid.w, _StrataLod.z)) * _StrataFar.xy + 0.5) / (_StrataFar.xy + 1.0);
        return _StrataHeightParams.x + SAMPLE_TEXTURE2D_LOD(_StrataFarHeights, sampler_linear_clamp, farUV, 0).r * _StrataHeightParams.y;
    }
    float2 uv = (local * _StrataHeightParams.z + 0.5) / (_StrataHeightParams.z + 1.0);
    return _StrataHeightParams.x + SAMPLE_TEXTURE2D_ARRAY_LOD(_StrataHeights, sampler_linear_clamp, uv, slot, 0).r * _StrataHeightParams.y;
}

float3 StrataNormal(float2 positionXZ)
{
    float step = _StrataHeightParams.w * (StrataResident(positionXZ) ? 1.0 : STRATA_FAR_STEP);
    float left = StrataHeight(positionXZ - float2(step, 0.0));
    float right = StrataHeight(positionXZ + float2(step, 0.0));
    float down = StrataHeight(positionXZ - float2(0.0, step));
    float up = StrataHeight(positionXZ + float2(0.0, step));
    return normalize(float3(left - right, 2.0 * step, down - up));
}

// Rock strata --------------------------------------------------------------------------------------------------------
TEXTURE2D_ARRAY(_StrataRockMap);            // at splat resolution: strength, definition index / 255
StructuredBuffer<float4> _StrataBands;      // StrataBands.hlsl's three float4s per definition

struct StrataRock
{
    float weight;
    StrataDefinition definition;
};

// Strata strength (bilinear) at a world xz, and the definition of the strongest of the four nearest texels.
StrataRock StrataRockAt(float2 positionXZ)
{
    float2 unit, relative;
    int slice = StrataSlot(positionXZ, unit, relative);
    StrataRock none;
    none.weight = 0.0;
    none.definition = UnpackStrata(_StrataBands[0], _StrataBands[1], _StrataBands[2]);
    if (slice < 0) return none;
    float resolution = _StrataSplatParams.x;
    float2 local = unit * resolution;
    int2 i0 = min((int2)floor(local), (int)resolution - 1);
    float2 f = local - i0;
    float2 a = LOAD_TEXTURE2D_ARRAY_LOD(_StrataRockMap, i0, slice, 0).rg;
    float2 b = LOAD_TEXTURE2D_ARRAY_LOD(_StrataRockMap, i0 + int2(1, 0), slice, 0).rg;
    float2 c = LOAD_TEXTURE2D_ARRAY_LOD(_StrataRockMap, i0 + int2(0, 1), slice, 0).rg;
    float2 d = LOAD_TEXTURE2D_ARRAY_LOD(_StrataRockMap, i0 + int2(1, 1), slice, 0).rg;
    StrataRock rock;
    rock.weight = lerp(lerp(a.x, b.x, f.x), lerp(c.x, d.x, f.x), f.y);
    float2 strongest = a;
    if (b.x > strongest.x) strongest = b;
    if (c.x > strongest.x) strongest = c;
    if (d.x > strongest.x) strongest = d;
    int index = (int)round(strongest.y * 255.0);
    rock.definition = UnpackStrata(_StrataBands[index * 3], _StrataBands[index * 3 + 1], _StrataBands[index * 3 + 2]);
    return rock;
}

// How far a point on a steep face is pushed out horizontally by its rock bed, fading out towards the geometry range.
float3 StrataPush(float3 positionWS, float distance, StrataRock rock)
{
    StrataDefinition d = rock.definition;
    if (rock.weight <= 0.0 || d.push <= 0.0) return 0.0;
    float fade = saturate((d.geometryRange - distance) / (d.geometryRange * 0.25));
    if (fade <= 0.0) return 0.0;
    float3 normal = StrataNormal(positionWS.xz);
    float steep = length(normal.xz);
    float2 outward = normal.xz / max(steep, 1e-4);
    // A heightfield face only has vertices every (detail spacing x gradient) metres up it; once that passes a quarter
    // of a bed, pushing would fold the mesh, so very steep faces keep only the shaded bands. The detail spacing is used
    // for every patch level, so vertices that patches share are pushed alike.
    float rise = _StrataHeightParams.w * 0.5 * steep / max(normal.y, 1e-3);
    float resolvable = saturate(2.0 - rise / (d.thickness * 0.25));
    float bandHeight = StrataBandHeight(d, positionWS);
    float index, bottom, top;
    StrataBed(d, bandHeight, index, bottom, top);
    float offset = StrataProfile(d, index, (bandHeight - bottom) / max(top - bottom, 1e-4)).x * rock.weight * fade * resolvable *
                   saturate(steep * 1.5);
    return float3(outward.x * offset, 0.0, outward.y * offset);
}

// The surface normal with the rock beds' shape added (out to the strata's normal range), so bands shade even where
// they aren't pushed geometry.
float3 StrataBandNormal(float3 normal, float3 positionWS, float distance, StrataRock rock)
{
    StrataDefinition d = rock.definition;
    if (rock.weight <= 0.0 || d.push <= 0.0) return normal;
    float fade = saturate((d.normalRange - distance) / (d.normalRange * 0.25));
    float bandHeight = StrataBandHeight(d, positionWS);
    float index, bottom, top;
    StrataBed(d, bandHeight, index, bottom, top);
    float slope = StrataProfile(d, index, (bandHeight - bottom) / max(top - bottom, 1e-4)).y / max(top - bottom, 1e-4);
    float steep = saturate(length(normal.xz) * 1.5);
    return normalize(normal - float3(0.0, 1.0, 0.0) * slope * rock.weight * fade * steep);
}

// Places a patch vertex: scales the 0..32 grid to the node, then slides odd vertices onto the next coarser grid as the
// vertex nears the end of its level's range, so it matches a coarser neighbour exactly where they meet. Detail patches
// (level -1, over rock strata) morph by the strata's geometry range and always keep their edges on the leaf grid, so
// they meet the ordinary leaves around them. Finally steep rock faces are pushed out by their beds.
float3 StrataPlace(float3 positionOS)
{
    float4 node = _StrataNode;
    float quad = node.z / 32.0;
    float2 grid = positionOS.xz;
    float2 positionXZ = node.xy + grid * quad;

    float distance = length(_StrataCameraPosition - float3(positionXZ.x, StrataHeight(positionXZ), positionXZ.y));
    StrataRock rock = StrataRockAt(positionXZ);
    float range = node.w < 0.0 ? (rock.weight > 0.0 ? min(rock.definition.geometryRange, _StrataLod.w) : 0.0) : _StrataLod.x * exp2(node.w);
    float morph = range > 0.0 ? saturate((distance - range * _StrataLod.y) / (range * (1.0 - _StrataLod.y))) : 1.0;
    if (node.w < 0.0 && (any(grid <= 0.0) || any(grid >= 32.0))) morph = 1.0;
    grid -= frac(grid * 0.5) * 2.0 * morph;

    positionXZ = node.xy + grid * quad;
    float3 positionWS = float3(positionXZ.x, StrataHeight(positionXZ), positionXZ.y);
    // from the final position, so vertices that neighbouring patches share are pushed identically
    return positionWS + StrataPush(positionWS, length(_StrataCameraPosition - positionWS), StrataRockAt(positionXZ));
}

#endif
