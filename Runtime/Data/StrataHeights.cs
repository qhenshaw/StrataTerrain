using Unity.Collections;
using Unity.Mathematics;

namespace Strata
{
    // A read-only view of a world's CPU heights for jobs and queries. Full-resolution samples live in slots (one tile's
    // samples each), found through the tile-to-slot map; where a tile isn't resident, heights come from the far copy:
    // every FarStep-th sample of the whole world, which is exactly the vertex grid of the CDLOD levels drawn beyond the
    // resident tiles, so near and far patches meet without cracks.
    public struct StrataHeights
    {
        // far heights keep every FarStep-th sample; far splats every FarSplatStep-th texel
        public const int FarStep = 8;
        public const int FarSplatStep = 4;

        public StrataGrid Grid;
        [ReadOnly] public NativeArray<ushort> Slots;
        [ReadOnly] public NativeArray<int> TileSlots;
        [ReadOnly] public NativeArray<ushort> Far;

        public static int2 FarSamples(StrataGrid grid) => new int2(grid.TilesX, grid.TilesZ) * grid.Resolution / FarStep + 1;
        public static int2 FarSplatSamples(StrataGrid grid) => new int2(grid.TilesX, grid.TilesZ) * grid.SplatResolution / FarSplatStep + 1;

        // Whether a world sample's full-resolution height is resident.
        public bool IsResident(int2 sample)
        {
            sample = math.clamp(sample, 0, new int2(Grid.WorldSamplesX - 1, Grid.WorldSamplesZ - 1));
            int2 tile = Grid.SampleTile(sample);
            return TileSlots[Grid.TileIndex(tile.x, tile.y)] >= 0;
        }

        // The stored height of a world sample (clamped to the world), at full resolution if resident, else from the far
        // copy (bilinear between far samples).
        public float At(int2 sample)
        {
            sample = math.clamp(sample, 0, new int2(Grid.WorldSamplesX - 1, Grid.WorldSamplesZ - 1));
            int2 tile = Grid.SampleTile(sample);
            int slot = TileSlots[Grid.TileIndex(tile.x, tile.y)];
            if (slot >= 0)
            {
                int2 local = sample - tile * Grid.Resolution;
                return Grid.ToHeight(Slots[slot * Grid.SamplesPerTile + local.y * Grid.Samples + local.x]);
            }
            return FarAt((float2)sample / FarStep);
        }

        // Bilinear height at a world position, clamped to the world's edges.
        public float Sample(float2 position)
        {
            float2 sample = (position - Grid.Origin) / Grid.Spacing;
            int2 cell = (int2)math.floor(sample);
            float2 t = sample - cell;
            return math.lerp(math.lerp(At(cell), At(cell + new int2(1, 0)), t.x), math.lerp(At(cell + new int2(0, 1)), At(cell + new int2(1, 1)), t.x), t.y);
        }

        private float FarAt(float2 farSample)
        {
            if (!Far.IsCreated || Far.Length == 0) return Grid.MinHeight;
            int2 size = FarSamples(Grid);
            farSample = math.clamp(farSample, 0f, size - 1);
            int2 i0 = math.min((int2)math.floor(farSample), size - 2);
            float2 f = farSample - i0;
            float a = Grid.ToHeight(Far[i0.y * size.x + i0.x]), b = Grid.ToHeight(Far[i0.y * size.x + i0.x + 1]);
            float c = Grid.ToHeight(Far[(i0.y + 1) * size.x + i0.x]), d = Grid.ToHeight(Far[(i0.y + 1) * size.x + i0.x + 1]);
            return math.lerp(math.lerp(a, b, f.x), math.lerp(c, d, f.x), f.y);
        }
    }
}
