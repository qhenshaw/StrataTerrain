using System;
using System.Collections.Generic;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata
{
    // A world's cave meshes, per tile: each brick of a tile's baked (or previewed) cave data becomes a Mesh drawn by a
    // hidden MeshRenderer with the world's cave material, so caves get culling, shadows and probe lighting like any
    // renderer. Bricks are grouped by region (a level 2 brick's box): before each camera renders, a region that reaches
    // the ground's surface shows one level by distance (0 near, then 1 and 2, keeping a finer level where a coarse one
    // lost thin rock), and a region that is all underground shows its level 0 bricks only within the underground
    // distance. A streamed world also keeps the coarse bricks of the whole world (the far caves file), shown for tiles that
    // aren't resident, so arches and cave mouths are seen from afar. The meshes are built from the compact tile data when
    // a tile loads and destroyed when it goes; they stay CPU-readable so StrataCaveColliders can bake level 0 for physics.
    // Nothing here is ever saved: it all comes from the tile files.
    public sealed class StrataCaves : IDisposable
    {
        public const string FarName = "caves_far.bytes";

        private static readonly VertexAttributeDescriptor[] s_layout =
        {
            new(VertexAttribute.Position),
            new(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
            new(VertexAttribute.TexCoord0, VertexAttributeFormat.UNorm8, 4)
        };

        // A brick's mesh and renderer; the mesh is relative to Origin, the brick's minimum corner.
        public sealed class Brick
        {
            public int3 Coord;
            public int Level;
            public int Flags;
            public Vector3 Origin;
            public Mesh Mesh;
            public MeshRenderer Renderer;
        }

        // The bricks of one region of a tile, and which levels it may show.
        private sealed class Region
        {
            public int3 Coord;
            public Bounds Bounds;
            public bool Exposed;
            public readonly List<Brick> Bricks = new();
            public readonly bool[] Allowed = new bool[StrataCaveData.Levels];
        }

        private struct MeshVertex
        {
            public float3 Position;
            public uint Normal;
            public uint Surface;
        }

        private readonly StrataGrid _grid;
        private readonly Transform _root;
        private readonly List<Brick>[] _tiles;
        private readonly List<Region>[] _regions;
        private readonly List<Brick>[] _far;
        private readonly bool[] _resident;
        private readonly List<StrataCaveData.Brick> _table = new();
        private readonly Stack<Brick> _pool = new();
        private Material _material;
        private bool _castShadows;
        private int _layer;

        public StrataGrid Grid => _grid;
        public float VoxelSize { get; private set; }
        public int BrickCount { get; private set; }
        public long TriangleCount { get; private set; }
        // every brick renderer lives under this
        public Transform Root => _root;
        // how far regions at the surface show level 0 (and 2.5 times that, level 1), and how far caves that are all
        // underground are drawn, in metres
        public float LodDistance { get; set; } = 64f;
        public float UndergroundDistance { get; set; } = 200f;

        // A tile's bricks changed (loaded, released or previewed).
        public event Action<int> TileChanged;

        public StrataCaves(StrataGrid grid, Transform parent, Material material, int layer, bool castShadows)
        {
            _grid = grid;
            _material = material;
            _layer = layer;
            _castShadows = castShadows;
            _tiles = new List<Brick>[grid.TileCount];
            _regions = new List<Region>[grid.TileCount];
            _far = new List<Brick>[grid.TileCount];
            _resident = new bool[grid.TileCount];
            for (int i = 0; i < _tiles.Length; i++)
            {
                _tiles[i] = new List<Brick>();
                _regions[i] = new List<Region>();
                _far[i] = new List<Brick>();
            }
            _root = new GameObject("Strata Caves") { hideFlags = HideFlags.HideAndDontSave, layer = layer }.transform;
            _root.SetParent(parent, false);
        }

        // A tile's bricks of every level.
        public IReadOnlyList<Brick> this[int tile] => _tiles[tile];

        // Loads a tile's caves from its file (none if it has no caves section).
        public void SetTile(int tile, NativeArray<byte> file, int splatSamples)
        {
            NativeArray<byte> caves = StrataTileFile.ReadCaves(file, _grid.Samples, splatSamples, Allocator.TempJob);
            SetTile(tile, caves);
            caves.Dispose();
        }

        // Replaces a tile's bricks with those in encoded cave data (StrataCaveData; empty for none), which jobs read, so not
        // Temp memory.
        public void SetTile(int tile, NativeArray<byte> data)
        {
            Clear(_tiles[tile]);
            _regions[tile].Clear();
            Build(data, _tiles[tile]);
            Dictionary<int3, Region> regions = new();
            foreach (Brick brick in _tiles[tile])
            {
                int3 coord = StrataCaveData.Region(brick.Coord, brick.Level);
                if (!regions.TryGetValue(coord, out Region region))
                {
                    float size = StrataCaveData.BrickCells * StrataCaveData.CellSize(VoxelSize, StrataCaveData.Levels - 1);
                    float3 corner = StrataCaveData.BrickOrigin(_grid, VoxelSize, coord, StrataCaveData.Levels - 1);
                    regions[coord] = region = new Region { Coord = coord, Bounds = new Bounds(corner + size * 0.5f, new float3(size)) };
                    _regions[tile].Add(region);
                }
                region.Bricks.Add(brick);
                region.Exposed |= brick.Level > 0 || (brick.Flags & StrataCaveData.Exposed) != 0;
            }
            foreach (Region region in _regions[tile]) Finish(region);
            SetResident(tile, true);
            TileChanged?.Invoke(tile);
        }

        public void ReleaseTile(int tile)
        {
            bool had = _tiles[tile].Count > 0;
            Clear(_tiles[tile]);
            _regions[tile].Clear();
            SetResident(tile, false);
            if (had) TileChanged?.Invoke(tile);
        }

        // The coarse bricks of every tile (a streamed world's far caves file), shown for tiles that aren't resident.
        public unsafe void LoadFar(string path)
        {
            if (!File.Exists(path)) return;
            NativeArray<byte> file = new(File.ReadAllBytes(path), Allocator.TempJob);
            try
            {
                byte* bytes = (byte*)file.GetUnsafeReadOnlyPtr();
                int tiles = file.Length >= 4 ? *(int*)bytes : 0;
                if (tiles != _grid.TileCount) throw new InvalidDataException($"Strata far caves are for {tiles} tiles, not {_grid.TileCount}.");
                int offset = 4;
                for (int tile = 0; tile < tiles; tile++)
                {
                    if (offset + 4 > file.Length) throw new InvalidDataException("Strata far caves are truncated.");
                    int length = *(int*)(bytes + offset);
                    offset += 4;
                    if (length < 0 || offset + length > file.Length) throw new InvalidDataException("Strata far caves are truncated.");
                    if (length > 0)
                    {
                        NativeArray<byte> data = new(length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                        UnsafeUtility.MemCpy(data.GetUnsafePtr(), bytes + offset, length);
                        Build(data, _far[tile]);
                        data.Dispose();
                        foreach (Brick brick in _far[tile]) brick.Renderer.gameObject.SetActive(!_resident[tile]);
                    }
                    offset += length;
                }
            }
            finally
            {
                file.Dispose();
            }
        }

        // Writes the far caves file: the tile count, then each tile's encoded coarse bricks (a length, 0 for none).
        public static void WriteFar(string path, IReadOnlyList<byte[]> tiles)
        {
            using BinaryWriter writer = new(File.Create(path));
            writer.Write(tiles.Count);
            foreach (byte[] data in tiles)
            {
                writer.Write(data?.Length ?? 0);
                if (data != null) writer.Write(data);
            }
        }

        private void SetResident(int tile, bool resident)
        {
            _resident[tile] = resident;
            foreach (Brick brick in _far[tile]) brick.Renderer.gameObject.SetActive(!resident);
        }

        // A coarse level is allowed where the region has bricks at it and none of them lost shape.
        private static void Finish(Region region)
        {
            for (int level = 0; level < StrataCaveData.Levels; level++) region.Allowed[level] = level == 0;
            foreach (Brick brick in region.Bricks)
                if (brick.Level > 0) region.Allowed[brick.Level] = true;
            foreach (Brick brick in region.Bricks)
                if ((brick.Flags & StrataCaveData.Unfaithful) != 0) region.Allowed[brick.Level] = false;
        }

        // Shows each region at the level its distance from a camera asks for, just before that camera culls.
        public void Select(Vector3 camera)
        {
            float middle = LodDistance * 2.5f;
            for (int tile = 0; tile < _regions.Length; tile++)
                foreach (Region region in _regions[tile])
                {
                    float distance = math.sqrt(region.Bounds.SqrDistance(camera));
                    int level;
                    if (!region.Exposed) level = distance < UndergroundDistance ? 0 : -1;
                    else
                    {
                        level = distance < LodDistance ? 0 : distance < middle ? 1 : 2;
                        while (level > 0 && !region.Allowed[level]) level--;
                    }
                    foreach (Brick brick in region.Bricks) brick.Renderer.forceRenderingOff = brick.Level != level;
                }
        }

        private void Clear(List<Brick> bricks)
        {
            foreach (Brick brick in bricks)
            {
                BrickCount--;
                TriangleCount -= brick.Mesh.GetIndexCount(0) / 3;
                brick.Renderer.gameObject.SetActive(false);
                brick.Renderer.forceRenderingOff = false;
                _pool.Push(brick);
            }
            bricks.Clear();
        }

        private void Build(NativeArray<byte> data, List<Brick> into)
        {
            if (!StrataCaveData.Decode(data, out float voxel, _table)) return;
            VoxelSize = voxel;
            int count = _table.Count;
            Mesh.MeshDataArray meshData = Mesh.AllocateWritableMeshData(count);
            NativeArray<JobHandle> jobs = new(count, Allocator.Temp);
            for (int i = 0; i < count; i++)
            {
                StrataCaveData.Brick entry = _table[i];
                Mesh.MeshData mesh = meshData[i];
                mesh.SetVertexBufferParams(entry.VertexCount, s_layout);
                mesh.SetIndexBufferParams(entry.IndexCount, IndexFormat.UInt16);
                jobs[i] = new DecodeJob { Data = data, Brick = entry, Cell = StrataCaveData.CellSize(voxel, entry.Level), Mesh = mesh }.Schedule();
            }
            JobHandle.CompleteAll(jobs);
            jobs.Dispose();

            Mesh[] meshes = new Mesh[count];
            Brick[] bricks = new Brick[count];
            for (int i = 0; i < count; i++)
            {
                Mesh.MeshData mesh = meshData[i];
                mesh.subMeshCount = 1;
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, _table[i].IndexCount), MeshUpdateFlags.DontRecalculateBounds);
                bricks[i] = Take();
                meshes[i] = bricks[i].Mesh;
            }
            Mesh.ApplyAndDisposeWritableMeshData(meshData, meshes, MeshUpdateFlags.DontRecalculateBounds);
            for (int i = 0; i < count; i++)
            {
                Brick brick = bricks[i];
                brick.Coord = _table[i].Coord;
                brick.Level = _table[i].Level;
                brick.Flags = _table[i].Flags;
                brick.Origin = StrataCaveData.BrickOrigin(_grid, voxel, brick.Coord, brick.Level);
                brick.Mesh.name = $"Strata Cave {brick.Level}: {brick.Coord.x},{brick.Coord.y},{brick.Coord.z}";
                brick.Mesh.RecalculateBounds();
                brick.Renderer.transform.SetPositionAndRotation(brick.Origin, Quaternion.identity);
                brick.Renderer.gameObject.name = brick.Mesh.name;
                brick.Renderer.forceRenderingOff = false;
                brick.Renderer.gameObject.SetActive(true);
                into.Add(brick);
                BrickCount++;
                TriangleCount += _table[i].IndexCount / 3;
            }
        }

        private Brick Take()
        {
            if (_pool.Count > 0)
            {
                Brick pooled = _pool.Pop();
                if (pooled.Renderer != null) return pooled;
            }
            GameObject go = new("Strata Cave") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false);
            go.transform.SetParent(_root, false);
            Brick brick = new()
            {
                Mesh = new Mesh { name = "Strata Cave", hideFlags = HideFlags.HideAndDontSave },
                Renderer = go.AddComponent<MeshRenderer>()
            };
            go.AddComponent<MeshFilter>().sharedMesh = brick.Mesh;
            Style(brick);
            return brick;
        }

        private void Style(Brick brick)
        {
            brick.Renderer.gameObject.layer = _layer;
            brick.Renderer.sharedMaterial = _material;
            brick.Renderer.shadowCastingMode = _castShadows ? ShadowCastingMode.TwoSided : ShadowCastingMode.Off;
            brick.Renderer.receiveShadows = true;
            brick.Renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
        }

        public void Dispose()
        {
            for (int tile = 0; tile < _tiles.Length; tile++)
            {
                Clear(_tiles[tile]);
                Clear(_far[tile]);
            }
            foreach (Brick brick in _pool)
            {
                if (brick.Mesh != null) UnityEngine.Object.DestroyImmediate(brick.Mesh);
                if (brick.Renderer != null) UnityEngine.Object.DestroyImmediate(brick.Renderer.gameObject);
            }
            _pool.Clear();
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root.gameObject);
        }

        // Unpacks one brick's vertices (positions to floats, relative to the brick's corner) and copies its indices.
        [BurstCompile]
        private unsafe struct DecodeJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Data;
            public StrataCaveData.Brick Brick;
            public float Cell;
            public Mesh.MeshData Mesh;

            public void Execute()
            {
                byte* bytes = (byte*)Data.GetUnsafeReadOnlyPtr();
                StrataCaveData.Vertex* source = (StrataCaveData.Vertex*)(bytes + Brick.VertexOffset);
                NativeArray<MeshVertex> vertices = Mesh.GetVertexData<MeshVertex>();
                for (int i = 0; i < Brick.VertexCount; i++)
                    vertices[i] = new MeshVertex { Position = StrataCaveData.Unpack(source[i], Cell), Normal = source[i].Normal, Surface = source[i].Surface };
                NativeArray<ushort> indices = Mesh.GetIndexData<ushort>();
                UnsafeUtility.MemCpy(indices.GetUnsafePtr(), bytes + Brick.IndexOffset, Brick.IndexCount * 2L);
            }
        }
    }
}
