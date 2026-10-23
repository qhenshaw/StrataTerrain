using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Strata
{
    public enum StrataBiomeFilterMode
    {
        // everywhere, whatever the biome
        Any,
        // only in the listed biomes
        OnlyIn,
        // everywhere but the listed biomes
        NotIn
    }

    // Limits something (a palette layer's rule, a forest, a meadow, a cave layer or formation) to some of the world's
    // biomes. Surface things read the surface biome map and cave things the underground one; where biomes blend, the
    // filter blends with them by their weights.
    [Serializable]
    public struct StrataBiomeFilter
    {
        public StrataBiomeFilterMode Mode;
        public List<StrataBiomeArea> Biomes;

        public bool IsAny => Mode == StrataBiomeFilterMode.Any;

        public int Hash
        {
            get
            {
                int hash = (int)Mode;
                if (Biomes != null)
                    foreach (StrataBiomeArea biome in Biomes) hash = hash * 31 + (biome != null ? biome.GetHashCode() : 0);
                return hash;
            }
        }

        // Whether the filter lets through the biome at an index of the world's biome list (StrataBiomeMap.None for none).
        public bool Allows(int index, IReadOnlyList<StrataBiomeArea> biomes)
        {
            if (Mode == StrataBiomeFilterMode.Any) return true;
            bool listed = false;
            if (index >= 0 && index < biomes.Count && Biomes != null) listed = biomes[index] != null && Biomes.Contains(biomes[index]);
            return Mode == StrataBiomeFilterMode.OnlyIn ? listed : !listed;
        }

        // The filter for every biome index (StrataBiomeMap.None included), 1 where it lets the biome through.
        public void FillGate(float[] gate, int offset, IReadOnlyList<StrataBiomeArea> biomes)
        {
            for (int i = 0; i < StrataBiomeMap.Indices; i++) gate[offset + i] = Allows(i, biomes) ? 1f : 0f;
        }

        // The filter as a 256-bit mask over biome indices, for Burst jobs.
        public void Mask(IReadOnlyList<StrataBiomeArea> biomes, out uint4 low, out uint4 high)
        {
            low = high = 0u;
            for (int i = 0; i < StrataBiomeMap.Indices; i++)
            {
                if (!Allows(i, biomes)) continue;
                uint bit = 1u << (i & 31);
                switch (i >> 5)
                {
                    case 0: low.x |= bit; break;
                    case 1: low.y |= bit; break;
                    case 2: low.z |= bit; break;
                    case 3: low.w |= bit; break;
                    case 4: high.x |= bit; break;
                    case 5: high.y |= bit; break;
                    case 6: high.z |= bit; break;
                    default: high.w |= bit; break;
                }
            }
        }

        public static bool MaskAllows(uint4 low, uint4 high, int index)
        {
            uint4 half = index < 128 ? low : high;
            int word = (index >> 5) & 3;
            uint bits = word == 0 ? half.x : word == 1 ? half.y : word == 2 ? half.z : half.w;
            return (bits >> (index & 31) & 1u) != 0;
        }
    }
}
