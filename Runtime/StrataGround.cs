using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // Ground queries against whichever active Strata world covers a position, for spawning and gameplay code: the height
    // and normal (full resolution where the tile is resident, else the far copy) and the strongest terrain layer (from
    // the baked splats: the tile's where resident, else the far splats). Each returns false outside every world.
    public static class StrataGround
    {
        public static bool TryGetWorld(Vector3 position, out StrataWorld world)
        {
            foreach (StrataWorld candidate in StrataWorld.Active)
            {
                if (candidate == null || candidate.Heightfield == null) continue;
                StrataGrid grid = candidate.Heightfield.Grid;
                float2 p = new(position.x, position.z);
                if (math.all(p >= grid.Origin) && math.all(p <= grid.Origin + grid.Size))
                {
                    world = candidate;
                    return true;
                }
            }
            world = null;
            return false;
        }

        public static bool TryGetHeight(Vector3 position, out float height)
        {
            height = 0f;
            if (!TryGetWorld(position, out StrataWorld world)) return false;
            height = world.Heightfield.SampleHeight(new float2(position.x, position.z));
            return true;
        }

        // The point on the ground under a position (same x and z).
        public static bool TryGetGround(Vector3 position, out Vector3 ground)
        {
            bool found = TryGetHeight(position, out float height);
            ground = new Vector3(position.x, height, position.z);
            return found;
        }

        public static bool TryGetNormal(Vector3 position, out Vector3 normal)
        {
            normal = Vector3.up;
            if (!TryGetWorld(position, out StrataWorld world)) return false;
            StrataHeightfield heights = world.Heightfield;
            float2 p = new(position.x, position.z);
            float step = heights.Grid.Spacing * (heights.View.IsResident(heights.Grid.WorldSample(p)) ? 1f : StrataHeights.FarStep);
            float dx = heights.SampleHeight(p + new float2(step, 0f)) - heights.SampleHeight(p - new float2(step, 0f));
            float dz = heights.SampleHeight(p + new float2(0f, step)) - heights.SampleHeight(p - new float2(0f, step));
            normal = math.normalize(new float3(-dx, 2f * step, -dz));
            return true;
        }

        // The strongest palette layer at a position and its weight (nearest splat texel of the bake).
        public static bool TryGetLayer(Vector3 position, out StrataLayer layer, out float weight)
        {
            layer = null;
            weight = 0f;
            if (!TryGetWorld(position, out StrataWorld world) || world.Settings.Palette == null || world.Splatmap == null) return false;
            StrataGrid grid = world.Heightfield.Grid;
            StrataSplatmap splats = world.Splatmap;
            float2 relative = (new float2(position.x, position.z) - grid.Origin) / grid.TileSize;
            int2 tile = math.clamp((int2)math.floor(relative), 0, new int2(grid.TilesX - 1, grid.TilesZ - 1));
            int slot = world.Heightfield.SlotOf(grid.TileIndex(tile.x, tile.y));
            uint indices, weights;
            if (slot >= 0)
            {
                int2 texel = (int2)math.round(math.saturate(relative - tile) * grid.SplatResolution);
                int index = slot * grid.SplatSamplesPerTile + texel.y * grid.SplatSamples + texel.x;
                indices = splats.CpuIndices[index];
                weights = splats.CpuWeights[index];
            }
            else
            {
                int2 size = StrataHeights.FarSplatSamples(grid);
                int2 texel = (int2)math.round(math.saturate((new float2(position.x, position.z) - grid.Origin) / grid.Size) * (size - 1));
                indices = splats.CpuFarIndices[texel.y * size.x + texel.x];
                weights = splats.CpuFarWeights[texel.y * size.x + texel.x];
            }
            int best = 0;
            for (int i = 1; i < 4; i++)
                if (((weights >> (i * 8)) & 0xff) > ((weights >> (best * 8)) & 0xff)) best = i;
            int layerIndex = (int)((indices >> (best * 8)) & 0xff);
            if (layerIndex >= world.Settings.Palette.Layers.Count) return false;
            layer = world.Settings.Palette.Layers[layerIndex];
            weight = ((weights >> (best * 8)) & 0xff) / 255f;
            return true;
        }
    }
}
