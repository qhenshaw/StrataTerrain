// The colour of the terrain at a world xz, for things standing on it (grass blending into the ground): the nearest
// splat texel's layers weighted by each layer's average albedo. StrataWorld sets these globals before each camera
// renders, from the first active world; where its tile isn't resident the far splats are used.
#ifndef STRATA_GROUND_INCLUDED
#define STRATA_GROUND_INCLUDED

StructuredBuffer<int> _StrataGroundSlots;
StructuredBuffer<float4> _StrataGroundColours;
Texture2DArray<float4> _StrataGroundIndices;
Texture2DArray<float4> _StrataGroundWeights;
Texture2D<float4> _StrataGroundFarIndices;
Texture2D<float4> _StrataGroundFarWeights;
float4 _StrataGroundGrid;       // origin x, origin z, tile size, tiles x
float4 _StrataGroundParams;     // tiles z, splat quads per tile, far splat quads x, z

half3 StrataGroundColour(float2 positionXZ)
{
    float2 tiles = float2(_StrataGroundGrid.w, _StrataGroundParams.x);
    float2 relative = (positionXZ - _StrataGroundGrid.xy) / _StrataGroundGrid.z;
    float2 tile = clamp(floor(relative), 0.0, tiles - 1.0);
    int slot = _StrataGroundSlots[(int)(tile.y * tiles.x + tile.x)];
    float4 index, weight;
    if (slot >= 0)
    {
        int2 texel = (int2)round(saturate(relative - tile) * _StrataGroundParams.y);
        index = _StrataGroundIndices.Load(int4(texel, slot, 0));
        weight = _StrataGroundWeights.Load(int4(texel, slot, 0));
    }
    else
    {
        int2 texel = (int2)round(saturate(relative / tiles) * _StrataGroundParams.zw);
        index = _StrataGroundFarIndices.Load(int3(texel, 0));
        weight = _StrataGroundFarWeights.Load(int3(texel, 0));
    }
    int4 layers = (int4)round(index * 255.0);
    float3 colour = _StrataGroundColours[layers.x].rgb * weight.x + _StrataGroundColours[layers.y].rgb * weight.y +
                    _StrataGroundColours[layers.z].rgb * weight.z + _StrataGroundColours[layers.w].rgb * weight.w;
    return colour / max(dot(weight, 1.0), 1e-4);
}

#endif
