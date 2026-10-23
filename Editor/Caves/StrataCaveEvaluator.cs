using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static Strata.Editor.StrataCaveMesher;

namespace Strata.Editor
{
    // Evaluates a world's cave features (StrataCaveFeature) on the CPU, after its heights are final. EvaluateSurface cuts
    // the heightfield's holes for a set of tiles and works out each feature's weight on the ground, uploaded as a texture
    // the splat evaluator reads for the feature's paint and tree effect; Mesh meshes the caves of a set of tiles brick by
    // brick in parallel Burst jobs, shows them in the world, and returns each tile's encoded cave data for the bake to
    // store. The live preview and the bake share both.
    public sealed class StrataCaveEvaluator : IDisposable
    {
        // how far under the terrain the rim of cave mesh around features sits, in metres
        private const float SnapDepth = 0.05f;
        // over what distance from the ground surface cave walls blend into the ground's layers, in metres
        private const float BlendDepth = 3f;

        private readonly Dictionary<StrataCaveFeature, (Texture2D Texture, RectInt Area)> _influence = new();

        public double LastMilliseconds { get; private set; }
        public int LastBricks { get; private set; }

        public static List<StrataCaveFeature> CavesOf(List<StrataEvaluator.Node> nodes) =>
            StrataEvaluator.Flatten(nodes).OfType<StrataCaveFeature>().ToList();

        // A feature's weight on the ground (world samples covered), for its paint and tree effect.
        public bool TryGetInfluence(StrataCaveFeature cave, out Texture texture, out RectInt area)
        {
            bool found = _influence.TryGetValue(cave, out (Texture2D Texture, RectInt Area) entry);
            texture = entry.Texture;
            area = entry.Area;
            return found && texture != null;
        }

        public void Dispose()
        {
            foreach ((Texture2D texture, RectInt _) in _influence.Values)
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            _influence.Clear();
        }

        // The size cave cells are meshed at, and how far beyond a feature the mesh's rim reaches.
        public static float VoxelSize(StrataWorld world) => world.Settings.CaveVoxelSize;
        private static float Reach(StrataGrid grid, float voxel) => math.max(2f * voxel, 2f * grid.Spacing);

        // The field over the features, in hierarchy order (skipping any without a shape); release it with Dispose.
        private static StrataCaveField Field(StrataWorld world, List<StrataCaveFeature> caves, float voxel)
        {
            List<StrataCaveShape> shapes = new();
            List<StrataCavePrimitive> primitives = new();
            StrataPalette palette = world.Palette;
            foreach (StrataCaveFeature cave in caves)
            {
                IReadOnlyList<StrataCavePrimitive> own = cave.Primitives;
                if (own.Count == 0) continue;
                Bounds bounds = cave.Bounds3D;
                StrataCaveNoise noise = cave.Noise;
                shapes.Add(new StrataCaveShape
                {
                    Operation = cave.Operation,
                    Smoothness = cave.Smoothness,
                    NoiseAmplitude = noise.Amplitude,
                    NoiseFrequency = 1f / math.max(noise.Scale, 0.5f),
                    NoiseOctaves = math.clamp(noise.Octaves, 1, 5),
                    Seed = (uint)noise.Seed,
                    First = primitives.Count,
                    Count = own.Count,
                    Min = bounds.min,
                    Max = bounds.max,
                    WallLayer = cave.WallLayer != null ? palette.IndexOf(cave.WallLayer) : -1,
                    FloorLayer = cave.FloorLayer != null ? palette.IndexOf(cave.FloorLayer) : -1,
                    FloorCos = math.cos(math.radians(cave.FloorSlope))
                });
                primitives.AddRange(own);
            }
            return new StrataCaveField
            {
                Heights = world.Heightfield.View,
                Shapes = new NativeArray<StrataCaveShape>(shapes.ToArray(), Allocator.TempJob),
                Primitives = new NativeArray<StrataCavePrimitive>(primitives.ToArray(), Allocator.TempJob),
                Reach = Reach(world.Heightfield.Grid, voxel)
            };
        }

        private static void Dispose(StrataCaveField field)
        {
            field.Shapes.Dispose();
            field.Primitives.Dispose();
        }

        // Cuts the given tiles' holes into the heightfield and recomputes every feature's ground weight. The CPU heights
        // must be current. Returns how many of the tiles have holes.
        public int EvaluateSurface(StrataWorld world, List<StrataCaveFeature> caves, IEnumerable<int> tiles)
        {
            StrataHeightfield heightfield = world.Heightfield;
            StrataGrid grid = heightfield.Grid;
            heightfield.Readers.Complete();
            StrataCaveField field = Field(world, caves, VoxelSize(world));

            // tiles no feature reaches lose their holes first, as the jobs below read the holes (through the heights)
            List<(int Tile, RectInt Rect)> reached = new();
            foreach (int tile in tiles)
            {
                RectInt rect = HoleRect(grid, field, new int2(tile % grid.TilesX, tile / grid.TilesX));
                if (rect.width > 0 && rect.height > 0) reached.Add((tile, rect));
                else heightfield.SetHoles(tile, default, false);
            }

            List<(int Tile, HoleJob Job)> holes = new();
            List<JobHandle> handles = new();
            foreach ((int tile, RectInt rect) in reached)
            {
                int2 coord = new(tile % grid.TilesX, tile / grid.TilesX);
                HoleJob job = new()
                {
                    Field = field,
                    Tile = coord,
                    Rect = new int4(rect.x, rect.y, rect.width, rect.height),
                    Presence = new NativeArray<byte>(grid.SamplesPerTile, Allocator.TempJob),
                    Holed = new NativeArray<int>(1, Allocator.TempJob)
                };
                holes.Add((tile, job));
                handles.Add(job.Schedule());
            }

            List<(StrataCaveFeature Cave, RectInt Area, InfluenceJob Job)> influences = new();
            int shapeIndex = 0;
            foreach (StrataCaveFeature cave in caves)
            {
                if (cave.Primitives.Count == 0) continue;
                RectInt area = StrataEvaluator.FeatureRect(grid, cave);
                int shape = shapeIndex++;
                if (area.width <= 0 || area.height <= 0) continue;
                InfluenceJob job = new()
                {
                    Field = field,
                    Shape = shape,
                    Reach = StrataCaveFeature.InfluenceReach * math.max(1f, math.max(cave.Paint.WidthScale, cave.Trees.WidthScale)),
                    Rect = new int4(area.x, area.y, area.width, area.height),
                    Influence = new NativeArray<float>(area.width * area.height, Allocator.TempJob)
                };
                influences.Add((cave, area, job));
                handles.Add(job.Schedule());
            }
            NativeArray<JobHandle> all = new(handles.ToArray(), Allocator.Temp);
            JobHandle.CompleteAll(all);
            all.Dispose();

            int holed = 0;
            foreach ((int tile, HoleJob job) in holes)
            {
                bool any = job.Holed[0] != 0;
                if (any) holed++;
                heightfield.SetHoles(tile, job.Presence.Slice(), any);
                job.Presence.Dispose();
                job.Holed.Dispose();
            }

            HashSet<StrataCaveFeature> seen = new();
            foreach ((StrataCaveFeature cave, RectInt area, InfluenceJob job) in influences)
            {
                seen.Add(cave);
                if (!_influence.TryGetValue(cave, out (Texture2D Texture, RectInt Area) entry) || entry.Texture == null ||
                    entry.Texture.width != area.width || entry.Texture.height != area.height)
                {
                    if (entry.Texture != null) UnityEngine.Object.DestroyImmediate(entry.Texture);
                    entry.Texture = new Texture2D(area.width, area.height, GraphicsFormat.R32_SFloat, TextureCreationFlags.DontInitializePixels)
                    {
                        name = $"Strata Cave Influence {cave.name}",
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp
                    };
                }
                entry.Texture.SetPixelData(job.Influence, 0);
                entry.Texture.Apply(false, false);
                entry.Area = area;
                _influence[cave] = entry;
                job.Influence.Dispose();
            }
            foreach (StrataCaveFeature gone in _influence.Keys.Where(cave => !seen.Contains(cave)).ToList())
            {
                if (_influence[gone].Texture != null) UnityEngine.Object.DestroyImmediate(_influence[gone].Texture);
                _influence.Remove(gone);
            }

            Dispose(field);
            return holed;
        }

        // The samples of a tile that any feature's box covers (tile-local), or an empty rectangle.
        private static RectInt HoleRect(StrataGrid grid, StrataCaveField field, int2 tile)
        {
            float2 tileMin = grid.TileOrigin(tile.x, tile.y);
            float2 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (StrataCaveShape shape in field.Shapes)
            {
                if (shape.Operation != StrataCaveOperation.Carve) continue;
                min = math.min(min, shape.Min.xz - field.Reach);
                max = math.max(max, shape.Max.xz + field.Reach);
            }
            if (math.any(min > max)) return default;
            int2 first = math.max(0, (int2)math.floor((min - tileMin) / grid.Spacing));
            int2 last = math.min(grid.Resolution, (int2)math.ceil((max - tileMin) / grid.Spacing));
            return math.any(first > last) ? default : new RectInt(first.x, first.y, last.x - first.x + 1, last.y - first.y + 1);
        }

        // Meshes the caves of the given tiles, shows them in the world, and returns each tile's encoded cave data (null
        // for a tile without caves).
        public Dictionary<int, byte[]> Mesh(StrataWorld world, List<StrataCaveFeature> caves, IEnumerable<int> tiles)
        {
            Stopwatch watch = Stopwatch.StartNew();
            StrataHeightfield heightfield = world.Heightfield;
            StrataGrid grid = heightfield.Grid;
            heightfield.Readers.Complete();
            float voxel = VoxelSize(world);
            StrataCaveField field = Field(world, caves, voxel);
            HashSet<int> wanted = new(tiles);
            float3 lattice = StrataCaveData.LatticeOrigin(grid);
            float brickSize = StrataCaveData.BrickCells * voxel;
            int maxY = (int)math.ceil(grid.HeightRange / brickSize) - 1;

            // the bricks any feature's box (and the rim around it) reaches, in the wanted tiles, in lattice order
            SortedSet<int3> bricks = new(Comparer<int3>.Create((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x)));
            foreach (StrataCaveShape shape in field.Shapes)
            {
                int3 first = (int3)math.floor((shape.Min - field.Reach - lattice) / brickSize);
                int3 last = (int3)math.floor((shape.Max + field.Reach - lattice) / brickSize);
                first.y = math.max(first.y, 0);
                last.y = math.min(last.y, maxY);
                for (int y = first.y; y <= last.y; y++)
                for (int z = first.z; z <= last.z; z++)
                for (int x = first.x; x <= last.x; x++)
                {
                    int3 coord = new(x, y, z);
                    int tile = StrataCaveData.BrickTile(grid, voxel, coord);
                    if (tile >= 0 && wanted.Contains(tile)) bricks.Add(coord);
                }
            }

            List<BrickJob> jobs = new();
            NativeArray<JobHandle> handles = new(bricks.Count, Allocator.Temp);
            foreach (int3 coord in bricks)
            {
                BrickJob job = new()
                {
                    Field = field,
                    Coord = coord,
                    Voxel = voxel,
                    Lattice = lattice,
                    SnapDepth = SnapDepth,
                    BlendDepth = BlendDepth,
                    Vertices = new NativeList<StrataCaveData.Vertex>(0, Allocator.TempJob),
                    Indices = new NativeList<ushort>(0, Allocator.TempJob)
                };
                handles[jobs.Count] = job.Schedule();
                jobs.Add(job);
            }
            JobHandle.CompleteAll(handles);
            handles.Dispose();

            Dictionary<int, List<(int3, NativeArray<StrataCaveData.Vertex>, NativeArray<ushort>)>> perTile = new();
            foreach (BrickJob job in jobs)
            {
                if (job.Indices.Length == 0) continue;
                int tile = StrataCaveData.BrickTile(grid, voxel, job.Coord);
                if (!perTile.TryGetValue(tile, out var list)) perTile[tile] = list = new();
                list.Add((job.Coord, job.Vertices.AsArray(), job.Indices.AsArray()));
            }

            Dictionary<int, byte[]> result = new();
            int meshed = 0;
            foreach (int tile in wanted)
            {
                byte[] data = perTile.TryGetValue(tile, out var list) ? StrataCaveData.Encode(voxel, list) : null;
                meshed += list?.Count ?? 0;
                result[tile] = data;
                if (world.Caves == null) continue;
                NativeArray<byte> bytes = data != null ? new NativeArray<byte>(data, Allocator.TempJob) : new NativeArray<byte>(0, Allocator.TempJob);
                world.Caves.SetTile(tile, bytes);
                bytes.Dispose();
            }

            foreach (BrickJob job in jobs)
            {
                job.Vertices.Dispose();
                job.Indices.Dispose();
            }
            Dispose(field);
            LastBricks = meshed;
            LastMilliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        // A tile's holes as its file stores them: the rectangle of its samples that aren't plainly solid, and their
        // presence; null when it has no hole.
        public static StrataTileExtras Extras(StrataWorld world, int tile, byte[] caves)
        {
            StrataHoles holes = world.Heightfield.Holes;
            StrataGrid grid = world.Heightfield.Grid;
            StrataTileExtras extras = new() { Caves = caves };
            int slot = holes.TileSlots[tile];
            if (slot >= 0)
            {
                NativeArray<byte> presence = holes.Presence.GetSubArray(slot * grid.SamplesPerTile, grid.SamplesPerTile);
                int2 min = new(int.MaxValue), max = new(int.MinValue);
                bool any = false;
                for (int z = 0; z < grid.Samples; z++)
                for (int x = 0; x < grid.Samples; x++)
                {
                    byte value = presence[z * grid.Samples + x];
                    if (value == StrataHoles.Solid) continue;
                    any |= value < 128;
                    min = math.min(min, new int2(x, z));
                    max = math.max(max, new int2(x, z));
                }
                if (any)
                {
                    extras.HoleRect = new RectInt(min.x, min.y, max.x - min.x + 1, max.y - min.y + 1);
                    extras.Holes = new byte[extras.HoleRect.width * extras.HoleRect.height];
                    for (int z = 0; z < extras.HoleRect.height; z++)
                    for (int x = 0; x < extras.HoleRect.width; x++)
                        extras.Holes[z * extras.HoleRect.width + x] = presence[(min.y + z) * grid.Samples + min.x + x];
                }
            }
            return extras.IsEmpty ? null : extras;
        }
    }
}
