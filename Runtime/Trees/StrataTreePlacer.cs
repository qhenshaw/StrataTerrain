using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Strata
{
    // Turns forest density into trees, one tile at a time, the same way whichever tiles are placed together. Every
    // species has world-anchored candidate spots, CandidatesPerCell random ones in each cell its spacing wide (so they
    // fall at random, not on a grid), each with a random priority and its own room: a quarter of the spacing, varied, and
    // less in the middle of a group. A candidate survives thinning if a random draw is
    // below its forest's density there times the species' share of the forest at that spot (its weight times its own
    // rule and its stand noise, over all the forest's species') times its clump noise (groups and gaps averaging out to
    // the plain density). A survivor then stands unless a higher-priority survivor of its own species, or an already
    // standing tree of an earlier species (forests in hierarchy order, wider-spaced species first), is closer than their
    // two rooms added. Trees in the middle of groups are a little
    // bigger, at their edges a little smaller. Planted trees (along splines) stand first, thinned by what the tree effects on
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
            public float Clumping;
            public float ClumpSize;
            public float SpacingVariation;
            public float Patchiness;        // the forest's stands
            public float PatchSize;
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

        public const float Room = 0.25f;
        // random candidates per spacing² cell; more than one lets groups stand closer than one tree per spacing²
        public const int CandidatesPerCell = 4;

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

        [BurstCompile(DisableSafetyChecks = true)]
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
                public float Room;
            }

            private struct Candidate
            {
                public float2 Position;
                public float Priority;
                public float Room;
                public float Clump;
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
                    uint at = Mix(math.asuint(p.Position.x), math.asuint(p.Position.y), 0x3c6ef372u);
                    if (Unit(at) >= DensityAt(Slices - 2, p.Position)) continue;
                    if (Heights.Sample(p.Position) < SeaLevel) continue;
                    Species s = Species[p.Species];
                    Stand(ref area, p.Position, s.Spacing * Room);
                    if (InTile(p.Position, tileXZ)) Emit(p.Position, p.Species, Mix(at ^ 0xa54ff53au), 0.5f);
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
                    float cellSize = spacing;
                    // each candidate stands for part of its cell, so it survives proportionally less often (and a little less
                    // again: trees in groups give up less room than evenly spaced ones, so more of them stand)
                    const float cellShare = 0.85f / CandidatesPerCell;
                    int2 first = (int2)math.floor((area.Min - Grid.Origin) / cellSize);
                    int2 last = (int2)math.floor((areaMax - Grid.Origin) / cellSize);
                    int2 size = last - first + 1;
                    candidates.Clear();
                    candidates.Resize(size.x * size.y * CandidatesPerCell, NativeArrayOptions.UninitializedMemory);
                    for (int z = 0; z < size.y; z++)
                    for (int x = 0; x < size.x; x++)
                    for (int k = 0; k < CandidatesPerCell; k++)
                    {
                        int2 cell = first + new int2(x, z);
                        int slot = (z * size.x + x) * CandidatesPerCell + k;
                        int2 block = math.clamp((int2)math.floor((Grid.Origin + ((float2)cell + 0.5f) * cellSize - area.Min) / blockSize), 0, blocks - 1);
                        if (reach[(s.Forest * blocks.y + block.y) * blocks.x + block.x] == 0)
                        {
                            candidates[slot] = default;
                            continue;
                        }
                        uint4 h = Hash(cell, s.Seed + (uint)k * 0x632be5abu);
                        float2 position = Grid.Origin + ((float2)cell + Unit(h.xy)) * cellSize;
                        bool inWorld = math.all(position >= Grid.Origin) && math.all(position <= Grid.Origin + Grid.Size);
                        float clump = Contrast(Noise(position / s.ClumpSize, s.Seed ^ 0x1b873593u));
                        // 0 in gaps to 2.5 in group cores, averaging about 1 so the count holds
                        float group = math.smoothstep(0.4f, 0.8f, clump);
                        float gather = math.lerp(1f, 2.5f * group * group, s.Clumping);
                        bool kept = inWorld && Survives(si, position, Unit(h.z), cellShare * gather);
                        // trees in the middle of a group keep less room, so the group can close up
                        float core = math.smoothstep(0.45f, 0.8f, clump) * s.Clumping;
                        float vary = math.lerp(1f, 0.4f + 1.2f * Unit(Mix(h.x ^ 0x85ebca6bu)), s.SpacingVariation);
                        float room = spacing * Room * vary * (1f - 0.7f * core);
                        candidates[slot] = new Candidate { Position = position, Priority = Unit(h.w), Room = room, Clump = clump, Kept = kept };
                    }

                    for (int z = 0; z < size.y; z++)
                    for (int x = 0; x < size.x; x++)
                    for (int k = 0; k < CandidatesPerCell; k++)
                    {
                        int slot = (z * size.x + x) * CandidatesPerCell + k;
                        Candidate c = candidates[slot];
                        if (!c.Kept) continue;
                        bool blocked = false;
                        // two rooms reach at most 0.65 of the spacing, so the cells around are enough
                        for (int dz = -1; dz <= 1 && !blocked; dz++)
                        for (int dx = -1; dx <= 1 && !blocked; dx++)
                        {
                            int2 n = new(x + dx, z + dz);
                            if (math.any(n < 0) || math.any(n >= size)) continue;
                            for (int j = 0; j < CandidatesPerCell && !blocked; j++)
                            {
                                int other = (n.y * size.x + n.x) * CandidatesPerCell + j;
                                if (other == slot) continue;
                                Candidate o = candidates[other];
                                float apart = c.Room + o.Room;
                                if (!o.Kept || math.distancesq(o.Position, c.Position) >= apart * apart) continue;
                                // the higher priority wins; on a tie, the later slot
                                blocked = o.Priority > c.Priority || o.Priority == c.Priority && other > slot;
                            }
                        }
                        if (blocked || Crowded(ref area, c.Position, c.Room)) continue;
                        Stand(ref area, c.Position, c.Room);
                        if (InTile(c.Position, tileXZ)) Emit(c.Position, si, Hash(first + new int2(x, z), (s.Seed + (uint)k * 0x632be5abu) ^ 0x9e3779b9u).x, c.Clump);
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

            // Whether a draw is below the forest's density times the species' share times scale (the candidate's cell and
            // clump), checking the density alone first (the share is at most 1), which turns most candidates away before
            // the terrain is sampled.
            private bool Survives(int index, float2 position, float draw, float scale)
            {
                Species s = Species[index];
                if (s.Forest < 0) return false;
                float density = DensityAt(s.Forest, position) * scale;
                if (draw >= density) return false;
                float height = Heights.Sample(position);
                if (height < SeaLevel) return false;
                float slope = math.degrees(math.atan(math.length(Gradient(position))));
                float moisture = DensityAt(Slices - 1, position);
                float total = 0f, own = 0f;
                for (int i = s.ForestFirst; i < s.ForestFirst + s.ForestCount; i++)
                {
                    Species o = Species[i];
                    float stand = Contrast(Noise(position / o.PatchSize, o.Seed ^ 0xcc9e2d51u));
                    float w = o.Weight * Range(o.Height, height) * Range(o.Slope, slope) * Range(o.Moisture, moisture) *
                              math.lerp(1f, 3f * stand * stand, o.Patchiness);
                    total += w;
                    if (i == index) own = w;
                }
                return total > 0f && draw < density * own / total;
            }

            private bool Crowded(ref Area area, float2 position, float room)
            {
                float reach = room + MaxSpacing * Room * 1.5f;
                int2 min = math.max(0, (int2)math.floor((position - reach - area.Min) / area.Bucket));
                int2 max = math.min(area.Buckets - 1, (int2)math.floor((position + reach - area.Min) / area.Bucket));
                for (int z = min.y; z <= max.y; z++)
                for (int x = min.x; x <= max.x; x++)
                    for (int i = area.Heads[z * area.Buckets.x + x]; i >= 0; i = area.Next[i])
                    {
                        Standing s = area.Standing[i];
                        float limit = room + s.Room;
                        if (math.distancesq(s.Position, position) < limit * limit) return true;
                    }
                return false;
            }

            private static void Stand(ref Area area, float2 position, float room)
            {
                int2 b = math.clamp((int2)math.floor((position - area.Min) / area.Bucket), 0, area.Buckets - 1);
                int bucket = b.y * area.Buckets.x + b.x;
                area.Standing.Add(new Standing { Position = position, Room = room });
                area.Next.Add(area.Heads[bucket]);
                area.Heads[bucket] = area.Standing.Length - 1;
            }

            // clump: the clump noise where it stands (0.5 when not grouped), which nudges trees in group cores bigger
            private void Emit(float2 position, int index, uint seed, float clump)
            {
                Species s = Species[index];
                Random random = new(Mix(seed ^ 0x68e31da4u) | 1u);
                float scale = math.lerp(s.Scale.x, s.Scale.y, math.saturate(math.lerp(random.NextFloat(), clump, 0.4f * s.Clumping)));
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

            // Smoothed value noise bunches up around a half; this spreads it back over 0..1.
            private static float Contrast(float n) => math.saturate((n - 0.5f) * 2.6f + 0.5f);

            // Smooth value noise in 0..1 (two octaves), anchored to the world.
            private static float Noise(float2 p, uint seed) => Value(p, seed) * 0.65f + Value(p * 2.13f + 17.3f, seed ^ 0x27d4eb2fu) * 0.35f;

            private static float Value(float2 p, uint seed)
            {
                float2 i = math.floor(p), f = p - i;
                f = f * f * (3f - 2f * f);
                int2 c = (int2)i;
                float a = Unit(Mix((uint)c.x, (uint)c.y, seed));
                float b = Unit(Mix((uint)(c.x + 1), (uint)c.y, seed));
                float d = Unit(Mix((uint)c.x, (uint)(c.y + 1), seed));
                float e = Unit(Mix((uint)(c.x + 1), (uint)(c.y + 1), seed));
                return math.lerp(math.lerp(a, b, f.x), math.lerp(d, e, f.x), f.y);
            }

            // math.hash is a plain weighted sum (neighbouring cells' values step by a constant, which lines candidates up
            // in rows), so every draw goes through this avalanching mix instead (lowbias32).
            private static uint Mix(uint x)
            {
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }

            private static uint Mix(uint a, uint b, uint c) => Mix(a ^ Mix(b ^ Mix(c + 0x9e3779b9u)));

            private static uint4 Hash(int2 cell, uint seed)
            {
                uint h = Mix((uint)cell.x, (uint)cell.y, seed ^ 0x51ed270bu);
                return new uint4(h, Mix(h ^ 0x1b873593u), Mix(h + 0xcc9e2d51u), Mix(h ^ 0xe6546b64u));
            }

            private static float Unit(uint value) => (value >> 8) * (1f / 16777216f);
            private static float2 Unit(uint2 value) => new(Unit(value.x), Unit(value.y));
        }
    }
}
