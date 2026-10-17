// The rock strata band function, shared by the editor's evaluation (which bakes ledges into the heights) and the
// surface shader (which pushes and shades the finer banding), so both agree on where every bed starts and ends.
// Beds are layers of a tilted "band height" (world height plus the dip slope along the dip direction); each bed
// boundary is jittered by a per-boundary hash so thicknesses vary, but always stays in order.
#ifndef STRATA_BANDS_INCLUDED
#define STRATA_BANDS_INCLUDED

// A strata definition as three float4s:
// (thickness, variation 0..0.9, seed, push distance), (dip slope x, dip slope z, terrace strength, terrace sharpness),
// (geometry range, normal range, tint strength, aligned uvs)
struct StrataDefinition
{
    float thickness;
    float variation;
    float seed;
    float push;
    float2 dip;
    float terraceStrength;
    float terraceSharpness;
    float geometryRange;
    float normalRange;
    float tint;
    float alignedUVs;
};

StrataDefinition UnpackStrata(float4 a, float4 b, float4 c)
{
    StrataDefinition d;
    d.thickness = max(a.x, 0.1);
    d.variation = a.y;
    d.seed = a.z;
    d.push = a.w;
    d.dip = b.xy;
    d.terraceStrength = b.z;
    d.terraceSharpness = b.w;
    d.geometryRange = c.x;
    d.normalRange = c.y;
    d.tint = c.z;
    d.alignedUVs = c.w;
    return d;
}

float StrataHash(float band, float seed)
{
    uint h = asuint(band) * 2654435761u ^ asuint(seed) * 2246822519u;
    h ^= h >> 15;
    h *= 2654435761u;
    h ^= h >> 13;
    return (h & 0xffffu) / 65535.0;
}

float StrataBandHeight(StrataDefinition d, float3 positionWS)
{
    return positionWS.y + dot(positionWS.xz, d.dip);
}

float StrataBoundary(StrataDefinition d, float band)
{
    return (band + (StrataHash(band, d.seed) - 0.5) * d.variation) * d.thickness;
}

// The bed holding a band height: its index, bottom and top (band heights).
void StrataBed(StrataDefinition d, float bandHeight, out float index, out float bottom, out float top)
{
    index = floor(bandHeight / d.thickness);
    bottom = StrataBoundary(d, index);
    top = StrataBoundary(d, index + 1.0);
    if (bandHeight < bottom)
    {
        index -= 1.0;
        top = bottom;
        bottom = StrataBoundary(d, index);
    }
    else if (bandHeight >= top)
    {
        index += 1.0;
        bottom = top;
        top = StrataBoundary(d, index + 1.0);
    }
}

// How far a bed's face stands out at fraction f (0 bottom, 1 top): each bed bulges out, with recessed joints at its
// boundaries, by a per-bed random fraction of the push distance. Returns the offset and its derivative along f.
float2 StrataProfile(StrataDefinition d, float index, float f)
{
    float amount = d.push * (0.45 + 0.55 * StrataHash(index + 0.5, d.seed + 7.0));
    float u = 2.0 * f - 1.0;
    float u2 = u * u;
    return float2(amount * (1.0 - u2 * u2), amount * -8.0 * u2 * u);
}

// The terraced band height: the lower part of each bed (sharpness) flattened into a ledge, the rest a riser.
float StrataTerrace(StrataDefinition d, float bandHeight)
{
    float index, bottom, top;
    StrataBed(d, bandHeight, index, bottom, top);
    float f = (bandHeight - bottom) / max(top - bottom, 1e-4);
    float s = min(d.terraceSharpness, 0.95);
    float shaped = f < s ? 0.0 : smoothstep(0.0, 1.0, (f - s) / (1.0 - s));
    return bottom + (top - bottom) * shaped;
}

#endif
