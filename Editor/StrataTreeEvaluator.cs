using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Strata.Editor
{
    // Places a world's trees while editing and baking. The splat evaluator works out each forest's density per tile on
    // the GPU (Context.Forests are its slices) and reads it back; Store copies each readback into a world-wide byte cache
    // (forest densities, what all-forest effects leave, and moisture, per tile), and Place runs StrataTreePlacer over the cache and the CPU heights for
    // the given tiles and hands the records to the world's trees. Species come from every forest a Tree Scatter places
    // or a spline plants, in hierarchy order; prototypes are numbered in the order their species first appear.
    public sealed class StrataTreeEvaluator : IDisposable
    {
        public sealed class Context
        {
            public readonly List<StrataForest> Forests = new();
            public readonly List<(int Tile, AsyncGPUReadbackRequest Request)> Requests = new();
        }

        private sealed class Cache
        {
            public StrataGrid Grid;
            public int Slices;
            public NativeArray<byte> Density;
        }

        private readonly Dictionary<StrataWorld, Cache> _caches = new();

        // The last Place: in total, and of that, the placement jobs.
        public double LastPlaceMilliseconds { get; private set; }
        public double LastJobMilliseconds { get; private set; }

        public void Dispose()
        {
            foreach (Cache cache in _caches.Values)
                if (cache.Density.IsCreated) cache.Density.Dispose();
            _caches.Clear();
        }

        // The forests Tree Scatters place, in hierarchy order: the density slices.
        public static List<StrataForest> ScatteredForests(List<StrataEvaluator.Node> nodes)
        {
            List<StrataForest> forests = new();
            foreach (StrataFeature feature in StrataEvaluator.Flatten(nodes))
                if (feature is TreeScatter { Forest: not null } scatter && !forests.Contains(scatter.Forest)) forests.Add(scatter.Forest);
            return forests;
        }

        // Every forest whose species can appear: the scattered ones, then those only planted along splines.
        public static List<StrataForest> AllForests(List<StrataEvaluator.Node> nodes)
        {
            List<StrataForest> forests = ScatteredForests(nodes);
            foreach (StrataFeature feature in StrataEvaluator.Flatten(nodes))
                if (feature is SplineFeature && feature.Trees.Mode == StrataTreeMode.Plant && feature.Trees.Forest != null && !forests.Contains(feature.Trees.Forest))
                    forests.Add(feature.Trees.Forest);
            return forests;
        }

        public static List<StrataTreePrototype> Prototypes(List<StrataForest> forests)
        {
            List<StrataTreePrototype> prototypes = new();
            foreach (StrataForest forest in forests)
            foreach (StrataTreeSpecies species in forest.Species)
                if (species?.Prototype != null && species.Prototype.IsValid && !prototypes.Contains(species.Prototype))
                    prototypes.Add(species.Prototype);
            return prototypes;
        }

        // Changes whenever anything that decides trees or grass, other than feature footprints and heights, changes.
        public static int Signature(List<StrataEvaluator.Node> nodes, int seed)
        {
            int signature = seed;
            foreach (StrataForest forest in AllForests(nodes)) signature = signature * 31 + forest.GetHashCode() * 17 + forest.Version;
            foreach (StrataFeature feature in StrataEvaluator.Flatten(nodes))
                if (feature is GrassScatter { Meadow: not null } grass) signature = signature * 31 + grass.Meadow.GetHashCode() * 13 + grass.Meadow.Version;
            return signature;
        }

        public const int MaxGrassTypes = 8;

        // The grass prototypes the Grass Scatters' meadows use, in order of first appearance: the grass map's types.
        public static List<StrataGrassPrototype> GrassPrototypes(List<StrataEvaluator.Node> nodes)
        {
            List<StrataGrassPrototype> prototypes = new();
            foreach (StrataFeature feature in StrataEvaluator.Flatten(nodes))
            {
                if (feature is not GrassScatter { Meadow: not null } grass) continue;
                foreach (StrataMeadowEntry entry in grass.Meadow.Entries)
                    if (entry?.Prototype != null && entry.Prototype.IsValid && !prototypes.Contains(entry.Prototype))
                    {
                        if (prototypes.Count == MaxGrassTypes)
                        {
                            Debug.LogWarning($"Strata: a world can have {MaxGrassTypes} grass prototypes; {entry.Prototype.name} is left out.", grass);
                            continue;
                        }
                        prototypes.Add(entry.Prototype);
                    }
            }
            return prototypes;
        }

        public static bool SameList<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : Object
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // Density slices for a number of scattered forests: one each, what all-forest effects leave, and moisture.
        public static int Slices(int forests) => forests + 2;

        // Copies one tile's density readback into the world's cache.
        public void Store(StrataWorld world, int slices, int tile, AsyncGPUReadbackRequest request)
        {
            if (request.hasError || world.Heightfield == null) return;
            Cache cache = CacheFor(world, slices);
            StrataGrid grid = cache.Grid;
            for (int slice = 0; slice < slices && slice < request.layerCount; slice++)
            {
                NativeArray<byte> data = request.GetData<byte>(slice);
                NativeArray<byte>.Copy(data, 0, cache.Density, StrataTreePlacer.CacheIndex(grid, slices, tile, slice), grid.SplatSamplesPerTile);
            }
        }

        private Cache CacheFor(StrataWorld world, int slices)
        {
            StrataGrid grid = world.Heightfield.Grid;
            if (_caches.TryGetValue(world, out Cache cache) && cache.Grid.Equals(grid) && cache.Slices == slices) return cache;
            if (cache != null && cache.Density.IsCreated) cache.Density.Dispose();
            cache = new Cache
            {
                Grid = grid,
                Slices = slices,
                Density = new NativeArray<byte>(grid.TileCount * slices * grid.SplatSamplesPerTile, Allocator.Persistent)
            };
            _caches[world] = cache;
            return cache;
        }

        // Places trees on the given tiles from the cached densities; tiles whose density was never stored get none.
        public void Place(StrataWorld world, List<StrataEvaluator.Node> nodes, ICollection<int> tiles)
        {
            if (world.Trees == null || tiles.Count == 0) return;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            List<StrataForest> scattered = ScatteredForests(nodes);
            List<StrataForest> forests = AllForests(nodes);
            List<StrataTreePrototype> prototypes = Prototypes(forests);
            Cache cache = CacheFor(world, Slices(scattered.Count));
            int seed = world.Settings.TreeSeed;

            List<StrataTreePlacer.Species> table = new();
            Dictionary<StrataForest, (int First, int Count)> ranges = new();
            foreach (StrataForest forest in forests)
            {
                int first = table.Count;
                List<(StrataTreeSpecies Species, int Index)> members = new();
                for (int i = 0; i < forest.Species.Count; i++)
                {
                    StrataTreeSpecies species = forest.Species[i];
                    if (species?.Prototype != null && prototypes.Contains(species.Prototype)) members.Add((species, i));
                }
                // wider-spaced species first, so smaller ones fill in around them
                members.Sort((a, b) => b.Species.Spacing != a.Species.Spacing ? b.Species.Spacing.CompareTo(a.Species.Spacing) : a.Index.CompareTo(b.Index));
                foreach ((StrataTreeSpecies species, int index) in members)
                {
                    table.Add(new StrataTreePlacer.Species
                    {
                        Forest = scattered.IndexOf(forest),
                        Prototype = prototypes.IndexOf(species.Prototype),
                        Weight = species.Weight,
                        Spacing = math.max(species.Spacing, 0.25f),
                        Scale = new float2(math.min(species.Scale.x, species.Scale.y), math.max(species.Scale.x, species.Scale.y)),
                        Lean = species.Lean,
                        Align = species.AlignToGround,
                        Sink = species.Sink,
                        TrunkRadius = species.Prototype.ColliderRadius,
                        Height = species.Height.Packed,
                        Slope = species.Slope.Packed,
                        Moisture = species.Moisture.Packed,
                        Seed = math.hash(new int3(seed, forest.Seed, index))
                    });
                }
                ranges[forest] = (first, table.Count - first);
            }
            for (int i = 0; i < table.Count; i++)
            {
                StrataTreePlacer.Species species = table[i];
                foreach ((int first, int count) in ranges.Values)
                    if (i >= first && i < first + count)
                    {
                        species.ForestFirst = first;
                        species.ForestCount = count;
                    }
                table[i] = species;
            }

            NativeArray<StrataTreePlacer.Species> speciesArray = new(table.ToArray(), Allocator.TempJob);
            NativeArray<StrataTreePlacer.Planted> planted = new(Plant(nodes, ranges, table, seed).ToArray(), Allocator.TempJob);
            int[] tileArray = new int[tiles.Count];
            tiles.CopyTo(tileArray, 0);
            NativeList<StrataTreeRecord>[] output = new NativeList<StrataTreeRecord>[tileArray.Length];
            for (int i = 0; i < output.Length; i++) output[i] = new NativeList<StrataTreeRecord>(1024, Allocator.TempJob);
            world.Heightfield.Readers.Complete();
            double beforeJobs = watch.Elapsed.TotalMilliseconds;
            if (table.Count > 0)
                StrataTreePlacer.Place(cache.Grid, world.Settings.UseSeaLevel ? world.Settings.SeaLevel : float.MinValue, world.Heightfield.View, cache.Density, cache.Slices, speciesArray, planted, tileArray, output);

            LastJobMilliseconds = watch.Elapsed.TotalMilliseconds - beforeJobs;
            if (!SameList(world.Trees.Prototypes, prototypes)) world.SetTreePrototypes(prototypes);
            for (int i = 0; i < tileArray.Length; i++)
            {
                world.Trees.SetTile(tileArray[i], output[i].AsArray());
                output[i].Dispose();
            }
            speciesArray.Dispose();
            planted.Dispose();
            LastPlaceMilliseconds = watch.Elapsed.TotalMilliseconds;
        }

        // Trees along Plant splines: every Spacing metres along each spline (in one row on the centre line, or a row
        // each side at Offset), nudged by Jitter, each a species of the spline's forest chosen by weight.
        private static List<StrataTreePlacer.Planted> Plant(List<StrataEvaluator.Node> nodes, Dictionary<StrataForest, (int First, int Count)> ranges,
                                                            List<StrataTreePlacer.Species> table, int seed)
        {
            List<StrataTreePlacer.Planted> planted = new();
            foreach (StrataFeature feature in StrataEvaluator.Flatten(nodes))
            {
                if (feature is not SplineFeature spline || feature.Trees.Mode != StrataTreeMode.Plant || feature.Trees.Forest == null) continue;
                if (!ranges.TryGetValue(feature.Trees.Forest, out (int First, int Count) range) || range.Count == 0) continue;
                StrataTreeEffect effect = feature.Trees;
                float totalWeight = 0f;
                for (int i = range.First; i < range.First + range.Count; i++) totalWeight += table[i].Weight;
                if (totalWeight <= 0f) continue;

                IReadOnlyList<SplineFeature.Point> points = spline.Points;
                if (points.Count < 2) continue;
                Unity.Mathematics.Random random = new(math.hash(new float4(points[0].Position.xyz, seed)) | 1u);
                float travelled = effect.Spacing * 0.5f;
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    if (points[i].Shape.w < 0.5f) continue;
                    float2 a = points[i].Position.xz, b = points[i + 1].Position.xz;
                    float length = math.distance(a, b);
                    if (length <= 1e-4f) continue;
                    float2 along = (b - a) / length, side = new(-along.y, along.x);
                    for (; travelled < length; travelled += effect.Spacing)
                    {
                        float2 centre = a + along * travelled;
                        for (int row = effect.Offset > 0f ? -1 : 0; row <= (effect.Offset > 0f ? 1 : 0); row += 2)
                        {
                            float2 position = centre + side * (row * effect.Offset) + (random.NextFloat2() - 0.5f) * 2f * effect.Jitter * effect.Spacing;
                            float pick = random.NextFloat(totalWeight);
                            int chosen = range.First;
                            for (int s = range.First; s < range.First + range.Count; s++)
                            {
                                chosen = s;
                                pick -= table[s].Weight;
                                if (pick < 0f) break;
                            }
                            planted.Add(new StrataTreePlacer.Planted { Position = position, Species = chosen });
                            if (effect.Offset <= 0f) break;
                        }
                    }
                    travelled -= length;
                }
            }
            return planted;
        }

    }
}
