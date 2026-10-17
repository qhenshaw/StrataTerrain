using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Strata
{
    // Turns forest density into trees, one tile at a time, the same way whichever tiles are placed together. Every
    // species has a world-anchored grid of candidate spots (cells its spacing wide, one jittered spot each, with a
    // random priority). A candidate survives thinning if a random draw is below its forest's density there times the
    // species' share of the forest at that spot (its weight times its own rule, over all the forest's species'). A
    // survivor then stands unless a higher-priority survivor of its own species is within 0.7 of the spacing, or an
    // already standing tree of an earlier species (forests in hierarchy order, wider-spaced species first) is within
    // 0.35 of their two spacings added. Planted trees (along splines) stand first, thinned by what the tree effects on
    // every forest leave (so a river clears a hedgerow crossing it). No tree stands below the sea level, if given. Each tile works over itself plus a
    // margin, so trees near its edges see their neighbours' candidates, and keeps the trees whose bases are inside it.
    public static class StrataTreePlacer
    {
        public struct Species
        {
            public int Forest;              // density slice
            public int Prototype;
            public float Weight;
            public float Spacing;
            public float2 Scale;
            public float Lean;
            public float Align;
            public float Sink;
            public float TrunkRadius;
            public float4 Height;           // StrataRange.Packed
            public float4 Slope;
            public float4 Moisture;
            public uint Seed;
            public int ForestFirst;         // the forest's species, for shares
            public int ForestCount;
        }

        // A tree planted along a spline: position xz and species index.
        public struct Planted
        {
            public float2 Position;
            public int Species;
        }

        public const float SameSpecies = 0.7f;
        public const float OtherSpecies = 0.35f;

        // The density cache layout: per tile, Slices maps of SplatSamples² bytes (the forests, what effects on all forests
    // leave, then moisture).
        public static int CacheIndex(StrataGrid grid, int slices, int tile, int slice) => (tile * slices + slice) * grid.SplatSamplesPerTile;

        // Places the given tiles, in parallel. Each output list receives that tile's records.
        public static void Place(StrataGrid grid, float seaLevel, StrataHeights heights, NativeArray<byte> density, int slices,
                                 NativeArray<Species> species, NativeArray<Planted> planted, int[] tiles, NativeList<StrataTreeRecord>[] output)
        {
            float maxSpacing = 1f;
            for (int i = 0; i < species.Length; i++) maxSpacing = math.max(maxSpacing, species[i].Spacing);
            NativeArray<JobHandle> handles = new(tiles.Length, Allocator.Temp);
            for (int i = 0; i < tiles.Length; i++)
            {
                handles[i] = new PlaceJob
                {
                    Grid = grid,
                    SeaLevel = seaLevel,
                    Tile = tiles[i],
                    Heights = heights,
                    Density = density,
                    Slices = slices,
                    Species = species,
                    Planted = planted,
                    MaxSpacing = maxSpacing,
                    Output = output[i]
                }.Schedule();
            }
            JobHandle.CompleteAll(handles);
            handles.Dispose();
        }

        [BurstCompile]
        private struct PlaceJob : IJob
        {
            public StrataGrid Grid;
            public float SeaLevel;
            public int Tile;
            public StrataHeights Heights;
            [ReadOnly] public NativeArray<byte> Density;
            public int Slices;
            [ReadOnly] public NativeArray<Species> Species;
            [ReadOnly] public NativeArray<Planted> Planted;
            public float MaxSpacing;
            public NativeList<StrataTreeRecord> Output;

            private struct Standing
            {
                public float2 Position;
                public float Spacing;
            }

            private struct Candidate
            {
                public float2 Position;
                public float Priority;
                public bool Kept;
            }

            // standing trees bucketed by a grid over the working area (made in Execute: a job's own fields must all
            // be valid containers when it is scheduled)
            private struct Area
            {
                public NativeList<Standing> Standing;
                public NativeArray<int> Heads;
                public NativeList<int> Next;
                public float2 Min;
                public float Bucket;
                public int2 Buckets;
            }

            public void Execute()
            {
                int2 tileXZ = new(Tile % Grid.TilesX, Tile / Grid.TilesX);
                float2 tileMin = Grid.TileOrigin(tileXZ.x, tileXZ.y);
                float2 tileMax = tileMin + Grid.TileSize;
                float margin = MaxSpacing * 3f;
                Area area = new() { Min = tileMin - margin, Bucket = MaxSpacing };
                float2 areaMax = tileMax + margin;
                area.Buckets = (int2)math.ceil((areaMax - area.Min) / area.Bucket);
                area.Heads = new NativeArray<int>(area.Buckets.x * area.Buckets.y, Allocator.Temp);
                for (int i = 0; i < area.Heads.Length; i++) area.Heads[i] = -1;
                area.Next = new NativeList<int>(4096, Allocator.Temp);
                area.Standing = new NativeList<Standing>(4096, Allocator.Temp);

                for (int i = 0; i < Planted.Length; i++)
                {
                    Planted p = Planted[i];
                    if (math.any(p.Position < area.Min) || math.any(p.Position >= areaMax)) continue;
                    if (Unit(math.hash(new float3(p.Position, 3.3f))) >= DensityAt(Slices - 2, p.Position)) continue;
                    if (Heights.Sample(p.Position) < SeaLevel) continue;
                    Species s = Species[p.Species];
                    Stand(ref area, p.Position, s.Spacing);
                    if (InTile(p.Position, tileXZ)) Emit(p.Position, p.Species, math.hash(new float3(p.Position, 7.1f)));
                }

                // the most each forest reaches in each block of the area, spread to the blocks around it, so candidates
                // in bare ground are skipped without drawing them
                int forests = Slices - 2;
                float blockSize = math.max(16f, MaxSpacing);
                int2 blocks = (int2)math.ceil((areaMax - area.Min) / blockSize);
                NativeArray<byte> reach = BlockMaxima(area.Min, areaMax, blockSize, blocks, forests);

                NativeList<Candidate> candidates = new(4096, Allocator.Temp);
                for (int si = 0; si < Species.Length; si++)
                {
                    Species s = Species[si];
                    if (s.Weight <= 0f || s.Forest < 0) continue;
                    float spacing = s.Spacing;
                    int2 first = (int2)math.floor((area.Min - Grid.Origin) / spacing);
                    int2 last = (int2)math.floor((areaMax - Grid.Origin) / spacing);
                    int2 size = last - first + 1;
                    candidates.Clear();
                    candidates.Resize(size.x * size.y, NativeArrayOptions.UninitializedMemory);
                    for (int z = 0; z < size.y; z++)
                    for (int x = 0; x < size.x; x++)
                    {
                        int2 cell = first + new int2(x, z);
                        int2 block = math.clamp((int2)math.floor((Grid.Origin + ((float2)cell + 0.5f) * spacing - area.Min) / blockSize), 0, blocks - 1);
                        if (reach[(s.Forest * blocks.y + block.y) * blocks.x + block.x] == 0)
                        {
                            candidates[z * size.x + x] = default;
                            continue;
                        }
                        uint4 h = Hash(cell, s.Seed);
                        float2 position = Grid.Origin + ((float2)cell + Unit(h.xy)) * spacing;
                        bool inWorld = math.all(position >= Grid.Origin) && math.all(position <= Grid.Origin + Grid.Size);
                        bool kept = inWorld && Survives(si, position, Unit(h.z));
                        candidates[z * size.x + x] = new Candidate { Position = position, Priority = Unit(h.w), Kept = kept };
                    }

                    float minSame = spacing * SameSpecies;
                    for (int z = 0; z < size.y; z++)
                    for (int x = 0; x < size.x; x++)
                    {
                        Candidate c = candidates[z * size.x + x];
                        if (!c.Kept) continue;
                        bool blocked = false;
                        for (int dz = -1; dz <= 1 && !blocked; dz++)
                        for (int dx = -1; dx <= 1 && !blocked; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int2 n = new(x + dx, z + dz);
                            if (math.any(n < 0) || math.any(n >= size)) continue;
                            Candidate o = candidates[n.y * size.x + n.x];
                            if (!o.Kept || math.distancesq(o.Position, c.Position) >= minSame * minSame) continue;
                            // the higher priority wins; on a tie, the later cell
                            blocked = o.Priority > c.Priority || o.Priority == c.Priority && n.y * size.x + n.x > z * size.x + x;
                        }
                        if (blocked || Crowded(ref area, c.Position, spacing)) continue;
                        Stand(ref area, c.Position, spacing);
                        if (InTile(c.Position, tileXZ)) Emit(c.Position, si, Hash(first + new int2(x, z), s.Seed ^ 0x9e3779b9u).x);
                    }
                }
                candidates.Dispose();
                reach.Dispose();
                area.Standing.Dispose();
                area.Heads.Dispose();
                area.Next.Dispose();
            }

            // Per forest and block, the largest density byte within the block or the blocks beside it.
            private NativeArray<byte> BlockMaxima(float2 min, float2 max, float blockSize, int2 blocks, int forests)
            {
                NativeArray<byte> own = new(math.max(1, forests * blocks.x * blocks.y), Allocator.Temp);
                NativeArray<byte> spread = new(own.Length, Allocator.Temp);
                float spacing = Grid.SplatSpacing;
                int2 first = math.max(0, (int2)math.floor((min - Grid.Origin) / spacing));
                int2 last = math.min(new int2(Grid.TilesX, Grid.TilesZ) * Grid.SplatResolution, (int2)math.ceil((max - Grid.Origin) / spacing));
                for (int f = 0; f < forests; f++)
                for (int tz = first.y; tz <= last.y; tz++)
                for (int tx = first.x; tx <= last.x; tx++)
                {
                    int2 texel = new(tx, tz);
                    int2 tile = math.min(texel / Grid.SplatResolution, new int2(Grid.TilesX - 1, Grid.TilesZ - 1));
                    int2 local = texel - tile * Grid.SplatResolution;
                    byte value = Density[CacheIndex(Grid, Slices, Grid.TileIndex(tile.x, tile.y), f) + local.y * Grid.SplatSamples + local.x];
                    if (value == 0) continue;
                    int2 block = math.clamp((int2)math.floor((Grid.Origin + (float2)texel * spacing - min) / blockSize), 0, blocks - 1);
                    int index = (f * blocks.y + block.y) * blocks.x + block.x;
                    if (value > own[index]) own[index] = value;
                }
                for (int f = 0; f < forests; f++)
                for (int bz = 0; bz < blocks.y; bz++)
                for (int bx = 0; bx < blocks.x; bx++)
                {
                    byte best = 0;
                    for (int dz = math.max(0, bz - 1); dz <= math.min(blocks.y - 1, bz + 1); dz++)
                    for (int dx = math.max(0, bx - 1); dx <= math.min(blocks.x - 1, bx + 1); dx++)
                        best = (byte)math.max((int)best, own[(f * blocks.y + dz) * blocks.x + dx]);
                    spread[(f * blocks.y + bz) * blocks.x + bx] = best;
                }
                own.Dispose();
                return spread;
            }

            // whose tile a base belongs to: half-open, except at the world's far edges
            private bool InTile(float2 position, int2 tileXZ)
            {
                int2 tile = math.clamp((int2)math.floor((position - Grid.Origin) / Grid.TileSize), 0, new int2(Grid.TilesX - 1, Grid.TilesZ - 1));
                return math.all(tile == tileXZ);
            }

            // Whether a draw is below the forest's density times the species' share, checking the density alone first
            // (the share is at most 1), which turns most candidates away before the terrain is sampled.
            private bool Survives(int index, float2 position, float draw)
            {
                Species s = Species[index];
                if (s.Forest < 0) return false;
                float density = DensityAt(s.Forest, position);
                if (draw >= density) return false;
                float height = Heights.Sample(position);
                if (height < SeaLevel) return false;
                float slope = math.degrees(math.atan(math.length(Gradient(position))));
                float moisture = DensityAt(Slices - 1, position);
                float total = 0f, own = 0f;
                for (int i = s.ForestFirst; i < s.ForestFirst + s.ForestCount; i++)
                {
                    Species o = Species[i];
                    float w = o.Weight * Range(o.Height, height) * Range(o.Slope, slope) * Range(o.Moisture, moisture);
                    total += w;
                    if (i == index) own = w;
                }
                return total > 0f && draw < density * own / total;
            }

            private bool Crowded(ref Area area, float2 position, float spacing)
            {
                float reach = (spacing + MaxSpacing) * OtherSpecies;
                int2 min = math.max(0, (int2)math.floor((position - reach - area.Min) / area.Bucket));
                int2 max = math.min(area.Buckets - 1, (int2)math.floor((position + reach - area.Min) / area.Bucket));
                for (int z = min.y; z <= max.y; z++)
                for (int x = min.x; x <= max.x; x++)
                    for (int i = area.Heads[z * area.Buckets.x + x]; i >= 0; i = area.Next[i])
                    {
                        Standing s = area.Standing[i];
                        float limit = (spacing + s.Spacing) * OtherSpecies;
                        if (math.distancesq(s.Position, position) < limit * limit) return true;
                    }
                return false;
            }

            private static void Stand(ref Area area, float2 position, float spacing)
            {
                int2 b = math.clamp((int2)math.floor((position - area.Min) / area.Bucket), 0, area.Buckets - 1);
                int bucket = b.y * area.Buckets.x + b.x;
                area.Standing.Add(new Standing { Position = position, Spacing = spacing });
                area.Next.Add(area.Heads[bucket]);
                area.Heads[bucket] = area.Standing.Length - 1;
            }

            private void Emit(float2 position, int index, uint seed)
            {
                Species s = Species[index];
                Random random = new(math.hash(new uint2(seed, 0x68e31da4u)) | 1u);
                float scale = math.lerp(s.Scale.x, s.Scale.y, random.NextFloat());
                float yaw = random.NextFloat(360f);
                float2 gradient = Gradient(position);
                float2 lean = random.NextFloat2Direction() * random.NextFloat(s.Lean);
                lean += s.Align * math.degrees(new float2(math.atan(-gradient.y), math.atan(gradient.x)));
                float sink = s.Sink + s.TrunkRadius * scale * math.length(gradient);
                Output.Add(StrataTreeRecord.Create(Grid, Tile, position, s.Prototype, yaw, scale, lean, sink));
            }

            private float2 Gradient(float2 p)
            {
                float step = Grid.Spacing;
                float dx = Heights.Sample(p + new float2(step, 0f)) - Heights.Sample(p - new float2(step, 0f));
                float dz = Heights.Sample(p + new float2(0f, step)) - Heights.Sample(p - new float2(0f, step));
                return new float2(dx, dz) / (2f * step);
            }

            // Bilinear density (or moisture) from the cache, in whichever tile holds the position.
            private float DensityAt(int slice, float2 position)
            {
                float2 relative = (position - Grid.Origin) / Grid.TileSize;
                int2 tile = math.clamp((int2)math.floor(relative), 0, new int2(Grid.TilesX - 1, Grid.TilesZ - 1));
                int resolution = Grid.SplatResolution;
                float2 local = math.clamp(relative - tile, 0f, 1f) * resolution;
                int2 i0 = math.min((int2)math.floor(local), resolution - 1);
                float2 f = local - i0;
                int samples = Grid.SplatSamples;
                int baseIndex = CacheIndex(Grid, Slices, Grid.TileIndex(tile.x, tile.y), slice);
                float a = Density[baseIndex + i0.y * samples + i0.x];
                float b = Density[baseIndex + i0.y * samples + i0.x + 1];
                float c = Density[baseIndex + (i0.y + 1) * samples + i0.x];
                float d = Density[baseIndex + (i0.y + 1) * samples + i0.x + 1];
                return math.lerp(math.lerp(a, b, f.x), math.lerp(c, d, f.x), f.y) / 255f;
            }

            private static float Range(float4 range, float value)
            {
                if (range.x < 0.5f) return 1f;
                float falloff = math.max(range.w, 1e-4f);
                return math.saturate((value - range.y) / falloff + 1f) * math.saturate((range.z - value) / falloff + 1f);
            }

            private static uint4 Hash(int2 cell, uint seed)
            {
                uint h = math.hash(new uint4((uint)cell.x, (uint)cell.y, seed, 0x51ed270bu));
                return new uint4(h, math.hash(new uint2(h, 1u)), math.hash(new uint2(h, 2u)), math.hash(new uint2(h, 3u)));
            }

            private static float Unit(uint value) => (value >> 8) * (1f / 16777216f);
            private static float2 Unit(uint2 value) => new(Unit(value.x), Unit(value.y));
        }
    }
}
