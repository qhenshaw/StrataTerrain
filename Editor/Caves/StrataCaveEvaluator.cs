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
        // The last Mesh's coarsest bricks per tile (encoded, null for none), for the far caves file.
        public Dictionary<int, byte[]> LastFar { get; } = new();
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

        // Cuts the given tiles' holes into the heightfield and recomputes the features' ground weights. The CPU heights
        // must be current. With an area (world samples), only the holes there and the weights of features over it are
        // worked out again. Returns how many of the tiles have holes.
        public int EvaluateSurface(StrataWorld world, List<StrataCaveFeature> caves, IEnumerable<int> tiles, RectInt? area = null)
        {
            StrataHeightfield heightfield = world.Heightfield;
            StrataGrid grid = heightfield.Grid;
            StrataHoles holes = heightfield.Holes;
            heightfield.Readers.Complete();
            StrataCaveField field = Field(world, caves, VoxelSize(world));

            // each tile's presence, starting from what it has (over an area) or solid, and the samples to work out again;
            // tiles with nothing to work out are set now, as the jobs below read the holes (through the heights)
            List<(int Tile, RectInt Rect, NativeArray<byte> Presence)> work = new();
            foreach (int tile in tiles)
            {
                int2 coord = new(tile % grid.TilesX, tile / grid.TilesX);
                RectInt rect = HoleRect(grid, field, coord);
                if (area.HasValue)
                {
                    RectInt tileArea = new(coord.x * grid.Resolution, coord.y * grid.Resolution, grid.Samples, grid.Samples);
                    rect = StrataEvaluator.Intersect(area.Value, tileArea);
                    rect.position -= tileArea.position;
                }
                if (rect.width <= 0 || rect.height <= 0)
                {
                    if (!area.HasValue) heightfield.SetHoles(tile, default, false);
                    continue;
                }
                NativeArray<byte> presence = new(grid.SamplesPerTile, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                int slot = holes.TileSlots[tile];
                if (area.HasValue && slot >= 0) NativeArray<byte>.Copy(holes.Presence, slot * grid.SamplesPerTile, presence, 0, grid.SamplesPerTile);
                else
                    for (int i = 0; i < presence.Length; i++) presence[i] = StrataHoles.Solid;
                work.Add((tile, rect, presence));
            }

            List<JobHandle> handles = new();
            foreach ((int tile, RectInt rect, NativeArray<byte> presence) in work)
                handles.Add(new HoleJob
                {
                    Field = field,
                    Tile = new int2(tile % grid.TilesX, tile / grid.TilesX),
                    Rect = new int4(rect.x, rect.y, rect.width, rect.height),
                    Presence = presence
                }.Schedule(rect.height, 4));

            List<(StrataCaveFeature Cave, RectInt Area, NativeArray<float> Influence)> influences = new();
            int shapeIndex = 0;
            foreach (StrataCaveFeature cave in caves)
            {
                if (cave.Primitives.Count == 0) continue;
                RectInt rect = StrataEvaluator.FeatureRect(grid, cave);
                int shape = shapeIndex++;
                if (rect.width <= 0 || rect.height <= 0) continue;
                // over an area, a feature that doesn't reach it keeps its weights
                if (area.HasValue && _influence.ContainsKey(cave))
                {
                    RectInt overlap = StrataEvaluator.Intersect(rect, area.Value);
                    if (overlap.width <= 0 || overlap.height <= 0) continue;
                }
                NativeArray<float> influence = new(rect.width * rect.height, Allocator.TempJob);
                influences.Add((cave, rect, influence));
                handles.Add(new InfluenceJob
                {
                    Field = field,
                    Shape = shape,
                    Reach = StrataCaveFeature.InfluenceReach * math.max(1f, math.max(cave.Paint.WidthScale, cave.Trees.WidthScale)),
                    Rect = new int4(rect.x, rect.y, rect.width, rect.height),
                    Influence = influence
                }.Schedule(rect.height, 4));
            }
            NativeArray<JobHandle> all = new(handles.ToArray(), Allocator.Temp);
            JobHandle.CompleteAll(all);
            all.Dispose();

            foreach ((int tile, RectInt _, NativeArray<byte> presence) in work)
            {
                bool any = false;
                for (int i = 0; i < presence.Length && !any; i++) any = presence[i] < 128;
                heightfield.SetHoles(tile, presence.Slice(), any);
                presence.Dispose();
            }
            int holed = 0;
            foreach (int tile in tiles)
                if (holes.HasHoles(tile)) holed++;

            HashSet<StrataCaveFeature> present = new(caves);
            foreach ((StrataCaveFeature cave, RectInt rect, NativeArray<float> influence) in influences)
            {
                if (!_influence.TryGetValue(cave, out (Texture2D Texture, RectInt Area) entry) || entry.Texture == null ||
                    entry.Texture.width != rect.width || entry.Texture.height != rect.height)
                {
                    if (entry.Texture != null) UnityEngine.Object.DestroyImmediate(entry.Texture);
                    entry.Texture = new Texture2D(rect.width, rect.height, GraphicsFormat.R32_SFloat, TextureCreationFlags.DontInitializePixels)
                    {
                        name = $"Strata Cave Influence {cave.name}",
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp
                    };
                }
                entry.Texture.SetPixelData(influence, 0);
                entry.Texture.Apply(false, false);
                entry.Area = rect;
                _influence[cave] = entry;
                influence.Dispose();
            }
            foreach (StrataCaveFeature gone in _influence.Keys.Where(cave => !present.Contains(cave)).ToList())
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

        // One meshed brick, kept so the preview only meshes the bricks an edit reaches again.
        private sealed class Meshed
        {
            public StrataCaveData.Vertex[] Vertices;
            public ushort[] Indices;
            public bool Exposed;
            public float Area;
            public int Flags;
        }

        private readonly Dictionary<(int Level, int3 Coord), Meshed> _bricks = new();
        private StrataWorld _bricksWorld;
        private float _bricksVoxel;

        // Forgets the meshed bricks, so the next Mesh meshes everything it is asked for.
        public void ClearBricks() => _bricks.Clear();

        // Meshes the caves of the given tiles, shows them in the world, and returns each tile's encoded cave data (null
        // for a tile without caves). With an area (world samples), only bricks over it are meshed again; the rest come
        // from the previous call's results; fineOnly leaves the coarse levels as they were (while an edit is dragged).
        public Dictionary<int, byte[]> Mesh(StrataWorld world, List<StrataCaveFeature> caves, IEnumerable<int> tiles, RectInt? area = null,
                                            bool fineOnly = false)
        {
            Stopwatch watch = Stopwatch.StartNew();
            StrataHeightfield heightfield = world.Heightfield;
            StrataGrid grid = heightfield.Grid;
            heightfield.Readers.Complete();
            float voxel = VoxelSize(world);
            if (_bricksWorld != world || _bricksVoxel != voxel || area == null) _bricks.Clear();
            _bricksWorld = world;
            _bricksVoxel = voxel;
            StrataCaveField field = Field(world, caves, voxel);
            field.Reach = Reach(grid, StrataCaveData.CellSize(voxel, StrataCaveData.Levels - 1));
            HashSet<int> wanted = new(tiles);
            float3 lattice = StrataCaveData.LatticeOrigin(grid);
            Rect dirty = area.HasValue ? new Rect(grid.SamplePosition(new int2(area.Value.xMin, area.Value.yMin)),
                                                  (float2)new int2(area.Value.width, area.Value.height) * grid.Spacing) : default;

            // level 0: the bricks any feature's box (and the rim around it) reaches, in the wanted tiles, in lattice order
            List<int3> fine = Active(grid, field, voxel, 0, wanted, Reach(grid, voxel));
            MeshLevels(field, lattice, voxel, new() { (0, fine) }, area.HasValue, dirty, grid);

            // the coarse levels, only for regions with a level 0 brick near the ground's surface
            SortedSet<int3> regions = new(LatticeOrder);
            foreach (int3 coord in fine)
                if (_bricks.TryGetValue((0, coord), out Meshed brick) && brick.Exposed) regions.Add(StrataCaveData.Region(coord, 0));
            if (!fineOnly)
            {
                List<(int Level, List<int3> Coords)> coarse = new();
                for (int level = 1; level < StrataCaveData.Levels; level++)
                {
                    List<int3> coords = new();
                    int side = 1 << level;
                    foreach (int3 region in regions)
                    for (int y = 0; y < StrataCaveData.RegionBricks / side; y++)
                    for (int z = 0; z < StrataCaveData.RegionBricks / side; z++)
                    for (int x = 0; x < StrataCaveData.RegionBricks / side; x++)
                        coords.Add(region * (StrataCaveData.RegionBricks / side) + new int3(x, y, z));
                    coords.Sort(LatticeOrder);
                    coarse.Add((level, coords));
                }
                MeshLevels(field, lattice, voxel, coarse, area.HasValue, dirty, grid);
                Forget(grid, voxel, wanted, fine, regions);
            }
            else Forget(grid, voxel, wanted, fine, null);
            if (!fineOnly) MarkUnfaithful(regions);

            Dictionary<int, List<StrataCaveData.Source>> perTile = new();
            Dictionary<int, List<StrataCaveData.Source>> farTile = new();
            List<NativeArray<StrataCaveData.Vertex>> vertexArrays = new();
            List<NativeArray<ushort>> indexArrays = new();
            int meshed = 0;
            for (int level = 0; level < StrataCaveData.Levels; level++)
            {
                List<int3> coords = new();
                foreach ((int l, int3 coord) in _bricks.Keys)
                    if (l == level) coords.Add(coord);
                coords.Sort(LatticeOrder);
                foreach (int3 coord in coords)
                {
                    Meshed brick = _bricks[(level, coord)];
                    int tile = StrataCaveData.BrickTile(grid, voxel, coord, level);
                    if (brick.Indices.Length == 0 || !wanted.Contains(tile)) continue;
                    if (!perTile.TryGetValue(tile, out List<StrataCaveData.Source> list)) perTile[tile] = list = new();
                    NativeArray<StrataCaveData.Vertex> vertices = new(brick.Vertices, Allocator.Temp);
                    NativeArray<ushort> indices = new(brick.Indices, Allocator.Temp);
                    vertexArrays.Add(vertices);
                    indexArrays.Add(indices);
                    StrataCaveData.Source source = new() { Coord = coord, Level = level, Flags = brick.Flags, Vertices = vertices, Indices = indices };
                    list.Add(source);
                    if (level == StrataCaveData.Levels - 1)
                    {
                        if (!farTile.TryGetValue(tile, out List<StrataCaveData.Source> far)) farTile[tile] = far = new();
                        far.Add(source);
                    }
                    meshed++;
                }
            }

            Dictionary<int, byte[]> result = new();
            LastFar.Clear();
            foreach (int tile in wanted)
            {
                LastFar[tile] = farTile.TryGetValue(tile, out List<StrataCaveData.Source> far) ? StrataCaveData.Encode(voxel, far) : null;
                byte[] data = perTile.TryGetValue(tile, out List<StrataCaveData.Source> list) ? StrataCaveData.Encode(voxel, list) : null;
                result[tile] = data;
                if (world.Caves == null) continue;
                NativeArray<byte> bytes = data != null ? new NativeArray<byte>(data, Allocator.TempJob) : new NativeArray<byte>(0, Allocator.TempJob);
                world.Caves.SetTile(tile, bytes);
                bytes.Dispose();
            }
            foreach (NativeArray<StrataCaveData.Vertex> array in vertexArrays) array.Dispose();
            foreach (NativeArray<ushort> array in indexArrays) array.Dispose();
            Dispose(field);
            LastBricks = meshed;
            LastMilliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        private static readonly Comparer<int3> LatticeOrder =
            Comparer<int3>.Create((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));

        // The bricks of a level that any feature's box (grown by the rim) reaches, in the wanted tiles, in lattice order.
        private static List<int3> Active(StrataGrid grid, StrataCaveField field, float voxel, int level, HashSet<int> wanted, float reach)
        {
            float3 lattice = StrataCaveData.LatticeOrigin(grid);
            float size = StrataCaveData.BrickCells * StrataCaveData.CellSize(voxel, level);
            int maxY = (int)math.ceil(grid.HeightRange / size) - 1;
            SortedSet<int3> bricks = new(LatticeOrder);
            foreach (StrataCaveShape shape in field.Shapes)
            {
                int3 first = (int3)math.floor((shape.Min - reach - lattice) / size);
                int3 last = (int3)math.floor((shape.Max + reach - lattice) / size);
                first.y = math.max(first.y, 0);
                last.y = math.min(last.y, maxY);
                for (int y = first.y; y <= last.y; y++)
                for (int z = first.z; z <= last.z; z++)
                for (int x = first.x; x <= last.x; x++)
                {
                    int3 coord = new(x, y, z);
                    int tile = StrataCaveData.BrickTile(grid, voxel, coord, level);
                    if (tile >= 0 && wanted.Contains(tile)) bricks.Add(coord);
                }
            }
            return new List<int3>(bricks);
        }

        // Meshes the given bricks of some levels in parallel (with an area, only those over it or not meshed yet) into the
        // brick cache.
        private void MeshLevels(StrataCaveField field, float3 lattice, float voxel, List<(int Level, List<int3> Coords)> levels, bool partial,
                                Rect area, StrataGrid grid)
        {
            List<(int Level, int3 Coord, BrickJob Job)> jobs = new();
            List<JobHandle> handles = new();
            foreach ((int level, List<int3> coords) in levels)
            foreach (int3 coord in coords)
            {
                float cell = StrataCaveData.CellSize(voxel, level);
                float size = StrataCaveData.BrickCells * cell;
                if (partial && _bricks.ContainsKey((level, coord)))
                {
                    float3 corner = StrataCaveData.BrickOrigin(grid, voxel, coord, level);
                    // a cell of margin: the brick's vertices reach a cell beyond it
                    if (!new Rect(corner.x - cell, corner.z - cell, size + cell * 2f, size + cell * 2f).Overlaps(area)) continue;
                }
                // primitives only matter within this level's reach
                StrataCaveField levelField = field;
                levelField.Reach = Reach(grid, cell);
                BrickJob job = new()
                {
                    Field = levelField,
                    Coord = coord,
                    Level = level,
                    Voxel = cell,
                    Lattice = lattice,
                    Reach = Reach(grid, cell),
                    SnapDepth = SnapDepth * (1 << level),
                    BlendDepth = BlendDepth,
                    Vertices = new NativeList<StrataCaveData.Vertex>(0, Allocator.TempJob),
                    Indices = new NativeList<ushort>(0, Allocator.TempJob),
                    Stats = new NativeArray<float>(2, Allocator.TempJob)
                };
                jobs.Add((level, coord, job));
                handles.Add(job.Schedule());
            }
            NativeArray<JobHandle> all = new(handles.ToArray(), Allocator.Temp);
            JobHandle.CompleteAll(all);
            all.Dispose();
            foreach ((int level, int3 coord, BrickJob job) in jobs)
            {
                bool exposed = job.Stats[0] > 0f;
                _bricks[(level, coord)] = new Meshed
                {
                    Vertices = job.Vertices.AsArray().ToArray(),
                    Indices = job.Indices.AsArray().ToArray(),
                    Exposed = exposed,
                    Area = job.Stats[1],
                    Flags = level == 0 && exposed ? StrataCaveData.Exposed : 0
                };
                job.Vertices.Dispose();
                job.Indices.Dispose();
                job.Stats.Dispose();
            }
        }

        // Drops cached bricks the features no longer reach (or whose region no longer reaches the surface) in the wanted
        // tiles.
        private void Forget(StrataGrid grid, float voxel, HashSet<int> wanted, List<int3> fine, SortedSet<int3> regions)
        {
            HashSet<int3> active = new(fine);
            List<(int, int3)> gone = new();
            foreach ((int level, int3 coord) in _bricks.Keys)
            {
                if (!wanted.Contains(StrataCaveData.BrickTile(grid, voxel, coord, level))) continue;
                // without regions (only level 0 was meshed), coarse bricks stay as they were
                bool keep = level == 0 ? active.Contains(coord) : regions == null || regions.Contains(StrataCaveData.Region(coord, level));
                if (!keep) gone.Add((level, coord));
            }
            foreach ((int, int3) key in gone) _bricks.Remove(key);
        }

        // Flags a region's coarse bricks when they keep much less of its features' surface than its level 0 bricks (thin
        // rock that vanishes at a coarse cell size), so the renderer keeps a finer level there.
        private void MarkUnfaithful(SortedSet<int3> regions)
        {
            Dictionary<int3, float3> areas = new();
            foreach (((int level, int3 coord), Meshed brick) in _bricks)
            {
                int3 region = StrataCaveData.Region(coord, level);
                if (!regions.Contains(region)) continue;
                areas.TryGetValue(region, out float3 sum);
                sum[level] += brick.Area;
                areas[region] = sum;
            }
            foreach (((int level, int3 coord), Meshed brick) in _bricks)
            {
                if (level == 0 || !areas.TryGetValue(StrataCaveData.Region(coord, level), out float3 sum)) continue;
                bool lost = sum.x > 1f && sum[level] < sum.x * 0.7f;
                brick.Flags = lost ? StrataCaveData.Unfaithful : 0;
            }
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
