using System;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // One kind of rock strata, as baked into a world's data and read by the surface shader: bed thickness and
    // variation, the dip of the beds, how far each bed's face is pushed out, the ledges baked into the heights, the
    // ranges for pushed geometry and shaded bands, and how the beds are tinted and textured. Packed as three float4s in
    // the order StrataBands.hlsl unpacks them.
    [Serializable]
    public struct StrataBandDefinition
    {
        public const int Float4s = 3;

        public float Thickness;
        public float Variation;
        public float Seed;
        public float Push;
        public float2 DipSlope;
        public float TerraceStrength;
        public float TerraceSharpness;
        public float GeometryRange;
        public float NormalRange;
        public float Tint;
        public bool AlignedUVs;

        public void Pack(Vector4[] target, int index)
        {
            target[index * Float4s] = new Vector4(Thickness, Variation, Seed, Push);
            target[index * Float4s + 1] = new Vector4(DipSlope.x, DipSlope.y, TerraceStrength, TerraceSharpness);
            target[index * Float4s + 2] = new Vector4(GeometryRange, NormalRange, Tint, AlignedUVs ? 1f : 0f);
        }
    }
}
