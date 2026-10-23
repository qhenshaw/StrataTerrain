using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // Collision for the caves inside each active collision chunk: when the terrain's collision activates a chunk, every
    // cave brick whose footprint meets it gets a pooled MeshCollider of its mesh (baked for PhysX in parallel), on the
    // collision layer under one root, so physics and NavMesh building see caves with the ground; a brick's collider goes
    // back to the pool once no active chunk needs it. A tile's caves changing rebuilds the colliders over it.
    public sealed class StrataCaveColliders : IDisposable
    {
        // the bake and the collider must agree, or PhysX cooks the mesh again on assignment
        private const MeshColliderCookingOptions CookingOptions = MeshColliderCookingOptions.CookForFasterSimulation |
                                                                  MeshColliderCookingOptions.EnableMeshCleaning |
                                                                  MeshColliderCookingOptions.WeldColocatedVertices |
                                                                  MeshColliderCookingOptions.UseFastMidphase;

        private readonly StrataCaves _caves;
        private readonly StrataCollision _collision;
        private readonly Transform _root;
        private readonly int _layer;
        private readonly HashSet<int2> _chunks = new();
        // per brick: how many active chunks want it, and its collider once built
        private readonly Dictionary<StrataCaves.Brick, int> _users = new();
        // the tile each counted brick belongs to (the caves reuse brick objects once a tile's bricks are replaced)
        private readonly Dictionary<StrataCaves.Brick, int> _tiles = new();
        private readonly Dictionary<StrataCaves.Brick, MeshCollider> _active = new();
        private readonly Stack<MeshCollider> _pool = new();
        private readonly List<(StrataCaves.Brick Brick, int Tile)> _scratch = new();

        public int ActiveCount => _active.Count;
        // every cave collider lives under this, for gathering them (NavMesh source collection)
        public Transform Root => _root;

        // Colliders came or went within these bounds.
        public event Action<Bounds> Changed;

        public StrataCaveColliders(StrataCaves caves, StrataCollision collision, Transform parent, int layer)
        {
            _caves = caves;
            _collision = collision;
            _layer = layer;
            _root = new GameObject("Strata Cave Colliders") { hideFlags = HideFlags.DontSave }.transform;
            _root.SetParent(parent, false);
            collision.ChunkActivated += OnChunkActivated;
            collision.ChunkDeactivated += OnChunkDeactivated;
            caves.TileChanged += OnTileChanged;
        }

        private void OnChunkActivated(int2 chunk, Bounds bounds)
        {
            if (!_chunks.Add(chunk)) return;
            Bricks(bounds, -1);
            Use(_scratch);
        }

        private void OnChunkDeactivated(int2 chunk, Bounds bounds)
        {
            if (!_chunks.Remove(chunk)) return;
            Bricks(bounds, -1);
            foreach ((StrataCaves.Brick brick, int _) in _scratch)
            {
                if (!_users.TryGetValue(brick, out int users)) continue;
                if (users > 1)
                {
                    _users[brick] = users - 1;
                    continue;
                }
                Forget(brick);
            }
        }

        // A tile's bricks were replaced (it streamed in or out, or was previewed): drop its old bricks' colliders and
        // count its new bricks for the active chunks over it.
        private void OnTileChanged(int tile)
        {
            List<StrataCaves.Brick> gone = new();
            foreach ((StrataCaves.Brick brick, int owner) in _tiles)
                if (owner == tile) gone.Add(brick);
            foreach (StrataCaves.Brick brick in gone) Forget(brick);
            List<(StrataCaves.Brick, int)> added = new();
            foreach (int2 chunk in _chunks)
            {
                Bricks(_collision.ChunkBounds(chunk), tile);
                added.AddRange(_scratch);
            }
            Use(added);
        }

        // Counts a chunk's use of bricks, building colliders for those that had none.
        private void Use(List<(StrataCaves.Brick Brick, int Tile)> bricks)
        {
            List<StrataCaves.Brick> build = new();
            foreach ((StrataCaves.Brick brick, int tile) in bricks)
            {
                _users.TryGetValue(brick, out int users);
                _users[brick] = users + 1;
                _tiles[brick] = tile;
                if (users == 0) build.Add(brick);
            }
            Build(build);
        }

        private void Forget(StrataCaves.Brick brick)
        {
            _users.Remove(brick);
            _tiles.Remove(brick);
            Drop(brick);
        }

        // Into _scratch, the bricks (of every tile, or only the given one) whose footprint overlaps a chunk.
        private void Bricks(Bounds bounds, int onlyTile)
        {
            _scratch.Clear();
            StrataGrid grid = _caves.Grid;
            float2 min = new(bounds.min.x, bounds.min.z), max = new(bounds.max.x, bounds.max.z);
            int2 firstTile = math.clamp((int2)math.floor((min - grid.Origin) / grid.TileSize), 0, new int2(grid.TilesX - 1, grid.TilesZ - 1));
            int2 lastTile = math.clamp((int2)math.floor((max - grid.Origin) / grid.TileSize), 0, new int2(grid.TilesX - 1, grid.TilesZ - 1));
            float size = StrataCaveData.BrickCells * _caves.VoxelSize;
            for (int tz = firstTile.y; tz <= lastTile.y; tz++)
            for (int tx = firstTile.x; tx <= lastTile.x; tx++)
            {
                int tile = grid.TileIndex(tx, tz);
                if (onlyTile >= 0 && tile != onlyTile) continue;
                foreach (StrataCaves.Brick brick in _caves[tile])
                {
                    float2 corner = new(brick.Origin.x, brick.Origin.z);
                    if (math.all(corner < max) && math.all(corner + size > min)) _scratch.Add((brick, tile));
                }
            }
        }

        private void Build(List<StrataCaves.Brick> bricks)
        {
            if (bricks.Count == 0) return;
            NativeArray<EntityId> ids = new(bricks.Count, Allocator.TempJob);
            for (int i = 0; i < bricks.Count; i++) ids[i] = bricks[i].Mesh.GetEntityId();
            new BakeJob { Meshes = ids }.Schedule(ids.Length, 1).Complete();
            ids.Dispose();

            Bounds changed = default;
            for (int i = 0; i < bricks.Count; i++)
            {
                StrataCaves.Brick brick = bricks[i];
                MeshCollider collider = Take();
                collider.gameObject.name = brick.Mesh.name;
                collider.transform.SetPositionAndRotation(brick.Origin, Quaternion.identity);
                // activate first: a pooled collider given its previous mesh again while inactive comes back with none
                collider.gameObject.SetActive(true);
                collider.sharedMesh = null;
                collider.sharedMesh = brick.Mesh;
                _active[brick] = collider;
                Bounds bounds = brick.Renderer.bounds;
                if (i == 0) changed = bounds;
                else changed.Encapsulate(bounds);
            }
            Changed?.Invoke(changed);
        }

        private void Drop(StrataCaves.Brick brick)
        {
            if (!_active.Remove(brick, out MeshCollider collider)) return;
            Bounds bounds = collider.bounds;
            collider.sharedMesh = null;
            collider.gameObject.SetActive(false);
            _pool.Push(collider);
            Changed?.Invoke(bounds);
        }

        private MeshCollider Take()
        {
            MeshCollider collider = _pool.Count > 0 ? _pool.Pop() : null;
            if (collider != null) return collider;
            GameObject go = new("Cave") { hideFlags = HideFlags.DontSave, layer = _layer };
            go.SetActive(false);
            go.transform.SetParent(_root, false);
            collider = go.AddComponent<MeshCollider>();
            collider.cookingOptions = CookingOptions;
            return collider;
        }

        public void Dispose()
        {
            _collision.ChunkActivated -= OnChunkActivated;
            _collision.ChunkDeactivated -= OnChunkDeactivated;
            _caves.TileChanged -= OnTileChanged;
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            _active.Clear();
            _pool.Clear();
            _users.Clear();
            _tiles.Clear();
        }

        // not Burst: BakeMesh is a managed call, but it is thread-safe, so bricks still cook in parallel
        private struct BakeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<EntityId> Meshes;

            public void Execute(int index) => Physics.BakeMesh(Meshes[index], false, CookingOptions);
        }
    }
}
