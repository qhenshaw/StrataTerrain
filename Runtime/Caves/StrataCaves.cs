using System;
using System.Collections.Generic;
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
    // renderer. The meshes are built from the compact tile data when a tile loads and destroyed when it goes; they stay
    // CPU-readable so StrataCaveColliders can bake them for physics. Nothing here is ever saved: it all comes from the
    // tile files.
    public sealed class StrataCaves : IDisposable
    {
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
            public Vector3 Origin;
            public Mesh Mesh;
            public MeshRenderer Renderer;
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

        // A tile's bricks changed (loaded, released or previewed).
        public event Action<int> TileChanged;

        public StrataCaves(StrataGrid grid, Transform parent, Material material, int layer, bool castShadows)
        {
            _grid = grid;
            _material = material;
            _layer = layer;
            _castShadows = castShadows;
            _tiles = new List<Brick>[grid.TileCount];
            for (int i = 0; i < _tiles.Length; i++) _tiles[i] = new List<Brick>();
            _root = new GameObject("Strata Caves") { hideFlags = HideFlags.HideAndDontSave, layer = layer }.transform;
            _root.SetParent(parent, false);
        }

        public IReadOnlyList<Brick> this[int tile] => _tiles[tile];

        public void SetLook(Material material, int layer, bool castShadows)
        {
            _material = material;
            _layer = layer;
            _castShadows = castShadows;
            foreach (List<Brick> tile in _tiles)
                foreach (Brick brick in tile) Style(brick);
        }

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
            Clear(tile);
            if (StrataCaveData.Decode(data, out float voxel, _table))
            {
                VoxelSize = voxel;
                Build(tile, data, voxel);
            }
            TileChanged?.Invoke(tile);
        }

        public void ReleaseTile(int tile)
        {
            if (_tiles[tile].Count == 0) return;
            Clear(tile);
            TileChanged?.Invoke(tile);
        }

        private void Clear(int tile)
        {
            foreach (Brick brick in _tiles[tile])
            {
                BrickCount--;
                TriangleCount -= brick.Mesh.GetIndexCount(0) / 3;
                brick.Renderer.gameObject.SetActive(false);
                _pool.Push(brick);
            }
            _tiles[tile].Clear();
        }

        private unsafe void Build(int tile, NativeArray<byte> data, float voxel)
        {
            int count = _table.Count;
            Mesh.MeshDataArray meshData = Mesh.AllocateWritableMeshData(count);
            NativeArray<JobHandle> jobs = new(count, Allocator.Temp);
            for (int i = 0; i < count; i++)
            {
                StrataCaveData.Brick entry = _table[i];
                Mesh.MeshData mesh = meshData[i];
                mesh.SetVertexBufferParams(entry.VertexCount, s_layout);
                mesh.SetIndexBufferParams(entry.IndexCount, IndexFormat.UInt16);
                jobs[i] = new DecodeJob { Data = data, Brick = entry, Voxel = voxel, Mesh = mesh }.Schedule();
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
                brick.Origin = StrataCaveData.BrickOrigin(_grid, voxel, brick.Coord);
                brick.Mesh.name = $"Strata Cave {brick.Coord.x},{brick.Coord.y},{brick.Coord.z}";
                brick.Mesh.RecalculateBounds();
                brick.Renderer.transform.SetPositionAndRotation(brick.Origin, Quaternion.identity);
                brick.Renderer.gameObject.name = brick.Mesh.name;
                brick.Renderer.gameObject.SetActive(true);
                _tiles[tile].Add(brick);
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
            for (int tile = 0; tile < _tiles.Length; tile++) Clear(tile);
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
            public float Voxel;
            public Mesh.MeshData Mesh;

            public void Execute()
            {
                byte* bytes = (byte*)Data.GetUnsafeReadOnlyPtr();
                StrataCaveData.Vertex* source = (StrataCaveData.Vertex*)(bytes + Brick.VertexOffset);
                NativeArray<MeshVertex> vertices = Mesh.GetVertexData<MeshVertex>();
                for (int i = 0; i < Brick.VertexCount; i++)
                    vertices[i] = new MeshVertex { Position = StrataCaveData.Unpack(source[i], Voxel), Normal = source[i].Normal, Surface = source[i].Surface };
                NativeArray<ushort> indices = Mesh.GetIndexData<ushort>();
                UnsafeUtility.MemCpy(indices.GetUnsafePtr(), bytes + Brick.IndexOffset, Brick.IndexCount * 2L);
            }
        }
    }
}
