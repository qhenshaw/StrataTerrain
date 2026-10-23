using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Strata.Editor
{
    // Paints a world's biome maps on the CPU from its Biome Area features, tile by tile in parallel Burst jobs, at splat
    // resolution: each texel of each map (surface, underground) starts with no biome, then every area of that kind, in
    // hierarchy order, paints its biome over it with its coverage times strength, as splats paint layers (scaling what is
    // there by one minus the opacity and keeping the four strongest), and the two strongest are stored with weights that
    // add up to 1. An area covers what its closed outlines enclose, easing out over the falloff centred on the outline,
    // which wanders by world-anchored value noise; an area with no outline covers everything. Texels are placed from
    // their whole-world index, so the shared edges of neighbouring tiles get identical values. Biomes don't depend on the
    // heights, so the maps can be painted before them; the splat, tree, cave and formation evaluators then read them.
    public static class StrataBiomeEvaluator
    {
        // index 255 is no biome
        public const int MaxBiomes = StrataBiomeMap.None;

        private struct Area
        {
            public int Kind;
            public int Index;
            public float Strength;
            public float Falloff;
            public float Amplitude;
            public float Frequency;
            public uint Seed;
            public bool Unbounded;
            public float2 Min;
            public float2 Max;
            public int FirstOutline;
            public int OutlineCount;
        }

        private struct OutlineRange
        {
            public int First;
            public int Count;
            public float2 Min;
            public float2 Max;
        }

        // The world's biome areas in hierarchy order (the biome maps' indices).
        public static List<StrataBiomeArea> BiomesOf(List<StrataEvaluator.Node> nodes) =>
            StrataEvaluator.Flatten(nodes).OfType<StrataBiomeArea>().Take(MaxBiomes).ToList();

        // Changes whenever anything an area paints with changes (the preview re-evaluates every tile then).
        public static int Signature(List<StrataBiomeArea> biomes)
        {
            int signature = 17;
            foreach (StrataBiomeArea biome in biomes)
                signature = signature * 31 + biome.GetHashCode() * 7 + biome.Version * 13 + biome.transform.localToWorldMatrix.GetHashCode();
            return signature;
        }

        // Paints the given tiles' maps into the world's biome map (the tiles' slots).
        public static void Evaluate(StrataWorld world, List<StrataBiomeArea> biomes, IEnumerable<int> tiles)
        {
            StrataGrid grid = world.Heightfield.Grid;
            List<Area> areas = new();
            List<OutlineRange> outlines = new();
            List<float2> points = new();
            for (int i = 0; i < biomes.Count; i++)
            {
                StrataBiomeArea biome = biomes[i];
                IReadOnlyList<StrataBiomeArea.Outline> own = biome.Outlines;
                Area area = new()
                {
                    Kind = (int)biome.Kind,
                    Index = i,
                    Strength = biome.Strength,
                    Falloff = biome.Falloff,
                    Amplitude = biome.NoiseAmplitude,
                    Frequency = 1f / math.max(biome.NoiseScale, 1f),
                    Seed = (uint)biome.NoiseSeed,
                    Unbounded = own.Count == 0,
                    Min = ((float2)biome.Bounds.min),
                    Max = ((float2)biome.Bounds.max),
                    FirstOutline = outlines.Count,
                    OutlineCount = own.Count
                };
                foreach (StrataBiomeArea.Outline outline in own)
                {
                    outlines.Add(new OutlineRange { First = points.Count, Count = outline.Points.Length, Min = outline.Min, Max = outline.Max });
                    points.AddRange(outline.Points);
                }
                areas.Add(area);
            }

            NativeArray<Area> areaArray = new(areas.ToArray(), Allocator.TempJob);
            NativeArray<OutlineRange> outlineArray = new(outlines.ToArray(), Allocator.TempJob);
            NativeArray<float2> pointArray = new(points.ToArray(), Allocator.TempJob);
            List<(int Tile, TileJob Job)> jobs = new();
            List<JobHandle> handles = new();
            foreach (int tile in tiles)
            {
                TileJob job = new()
                {
                    Grid = grid,
                    Tile = new int2(tile % grid.TilesX, tile / grid.TilesX),
                    Areas = areaArray,
                    Outlines = outlineArray,
                    Points = pointArray,
                    Surface = new NativeArray<uint>(grid.SplatSamplesPerTile, Allocator.TempJob, NativeArrayOptions.UninitializedMemory),
                    Underground = new NativeArray<uint>(grid.SplatSamplesPerTile, Allocator.TempJob, NativeArrayOptions.UninitializedMemory)
                };
                jobs.Add((tile, job));
                handles.Add(job.Schedule(grid.SplatSamples, 4));
            }
            NativeArray<JobHandle> all = new(handles.ToArray(), Allocator.Temp);
            JobHandle.CompleteAll(all);
            all.Dispose();
            foreach ((int tile, TileJob job) in jobs)
            {
                world.Biomes.WriteTile(world.Heightfield.SlotOf(tile), job.Surface, job.Underground);
                job.Surface.Dispose();
                job.Underground.Dispose();
            }
            areaArray.Dispose();
            outlineArray.Dispose();
            pointArray.Dispose();
        }

        [BurstCompile]
        private struct TileJob : IJobParallelFor
        {
            public StrataGrid Grid;
            public int2 Tile;
            [ReadOnly] public NativeArray<Area> Areas;
            [ReadOnly] public NativeArray<OutlineRange> Outlines;
            [ReadOnly] public NativeArray<float2> Points;
            [NativeDisableParallelForRestriction] public NativeArray<uint> Surface;
            [NativeDisableParallelForRestriction] public NativeArray<uint> Underground;

            public void Execute(int z)
            {
                int samples = Grid.SplatSamples;
                for (int x = 0; x < samples; x++)
                {
                    int2 texel = Tile * Grid.SplatResolution + new int2(x, z);
                    float2 p = Grid.Origin + (float2)texel * Grid.SplatSpacing;
                    Surface[z * samples + x] = Paint(p, (int)StrataBiomeKind.Surface);
                    Underground[z * samples + x] = Paint(p, (int)StrataBiomeKind.Underground);
                }
            }

            private uint Paint(float2 p, int kind)
            {
                int4 index = StrataBiomeMap.None;
                float4 weight = new(1f, 0f, 0f, 0f);
                for (int a = 0; a < Areas.Length; a++)
                {
                    Area area = Areas[a];
                    if (area.Kind != kind) continue;
                    float coverage = area.Unbounded ? 1f : Coverage(area, p);
                    PaintSlot(ref index, ref weight, area.Index, coverage * area.Strength);
                }
                int first = 0;
                for (int i = 1; i < 4; i++)
                    if (weight[i] > weight[first]) first = i;
                int second = -1;
                for (int i = 0; i < 4; i++)
                    if (i != first && weight[i] > 0f && (second < 0 || weight[i] > weight[second])) second = i;
                if (second < 0) return StrataBiomeMap.Pack(index[first], index[first], 1f);
                float total = weight[first] + weight[second];
                return StrataBiomeMap.Pack(index[first], index[second], weight[first] / total);
            }

            // The area's coverage at p: 1 well inside its outlines, easing to 0 across the falloff.
            private float Coverage(Area area, float2 p)
            {
                float reach = area.Falloff * 0.5f + area.Amplitude;
                if (math.any(p < area.Min) || math.any(p > area.Max)) return 0f;
                float distance = float.MaxValue;
                for (int o = area.FirstOutline; o < area.FirstOutline + area.OutlineCount; o++)
                {
                    OutlineRange outline = Outlines[o];
                    if (math.any(p < outline.Min - reach) || math.any(p > outline.Max + reach)) continue;
                    distance = math.min(distance, SignedDistance(outline, p));
                }
                if (distance >= float.MaxValue) return 0f;
                if (area.Amplitude > 0f) distance += area.Amplitude * Noise(p * area.Frequency, area.Seed);
                if (area.Falloff <= 0f) return distance < 0f ? 1f : 0f;
                return math.saturate(0.5f - distance / area.Falloff);
            }

            // Distance to a closed polyline, negative inside it (even-odd).
            private float SignedDistance(OutlineRange outline, float2 p)
            {
                float best = float.MaxValue;
                bool inside = false;
                for (int i = 0; i < outline.Count; i++)
                {
                    float2 a = Points[outline.First + i], b = Points[outline.First + (i + 1) % outline.Count];
                    float2 ab = b - a;
                    float t = math.saturate(math.dot(p - a, ab) / math.max(math.dot(ab, ab), 1e-8f));
                    best = math.min(best, math.lengthsq(p - (a + ab * t)));
                    if (a.y > p.y != b.y > p.y && p.x < a.x + (p.y - a.y) / (b.y - a.y) * ab.x) inside = !inside;
                }
                float d = math.sqrt(best);
                return inside ? -d : d;
            }

            private static void PaintSlot(ref int4 index, ref float4 weight, int biome, float opacity)
            {
                opacity = math.saturate(opacity);
                if (opacity <= 0f) return;
                weight *= 1f - opacity;
                int slot = -1;
                for (int i = 0; i < 4 && slot < 0; i++)
                    if (index[i] == biome && weight[i] > 0f) slot = i;
                if (slot < 0)
                {
                    slot = 0;
                    for (int i = 1; i < 4; i++)
                        if (weight[i] < weight[slot]) slot = i;
                    index[slot] = biome;
                    weight[slot] = 0f;
                }
                weight[slot] += opacity;
            }

            // Value noise in -1..1, two octaves.
            private static float Noise(float2 p, uint seed) =>
                (Value(p, seed) * 0.65f + Value(p * 2.7f + 5.1f, seed + 7919u) * 0.35f) * 2f - 1f;

            private static float Value(float2 p, uint seed)
            {
                float2 cell = math.floor(p);
                int2 i = (int2)cell;
                float2 f = p - cell;
                f = f * f * (3f - 2f * f);
                return math.lerp(math.lerp(Hash(i, seed), Hash(i + new int2(1, 0), seed), f.x),
                                 math.lerp(Hash(i + new int2(0, 1), seed), Hash(i + new int2(1, 1), seed), f.x), f.y);
            }

            private static float Hash(int2 cell, uint seed) => (math.hash(new uint3((uint2)cell, seed)) & 0xffffffu) / 16777215f;
        }
    }
}
