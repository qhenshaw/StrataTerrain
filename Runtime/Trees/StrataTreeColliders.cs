using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // Colliders for the trees inside each active collision chunk: when the terrain's collision activates a chunk, every
    // tree standing in it whose prototype has a collider gets a pooled trunk CapsuleCollider, or a MeshCollider turned
    // and scaled like the instance (rocks), on the collision layer so physics and NavMesh building see them with the
    // ground; when the chunk deactivates they go back to the pools. Trees changing rebuild the colliders of every chunk.
    public sealed class StrataTreeColliders : IDisposable
    {
        private readonly StrataTrees _trees;
        private readonly StrataCollision _collision;
        private readonly Transform _root;
        private readonly int _layer;
        private readonly Dictionary<int2, List<Collider>> _active = new();
        private readonly Stack<CapsuleCollider> _pool = new();
        private readonly Stack<MeshCollider> _meshPool = new();

        public int ActiveCount { get; private set; }
        // every tree collider lives under this, for gathering them (NavMesh source collection)
        public Transform Root => _root;

        public StrataTreeColliders(StrataTrees trees, StrataCollision collision, Transform parent, int layer)
        {
            _trees = trees;
            _collision = collision;
            _layer = layer;
            GameObject root = new("Strata Tree Colliders") { hideFlags = HideFlags.DontSave };
            root.transform.SetParent(parent, false);
            _root = root.transform;
            collision.ChunkActivated += OnChunkActivated;
            collision.ChunkDeactivated += OnChunkDeactivated;
            trees.Changed += OnTreesChanged;
        }

        private void OnChunkActivated(int2 chunk, Bounds bounds)
        {
            if (_active.ContainsKey(chunk)) return;
            List<Collider> colliders = new();
            _active[chunk] = colliders;
            Fill(bounds, colliders);
        }

        private void OnChunkDeactivated(int2 chunk, Bounds bounds)
        {
            if (!_active.Remove(chunk, out List<Collider> colliders)) return;
            Release(colliders);
        }

        private void OnTreesChanged(int tile)
        {
            List<int2> chunks = new(_active.Keys);
            foreach (int2 chunk in chunks)
            {
                List<Collider> colliders = _active[chunk];
                Release(colliders);
                Fill(_collision.ChunkBounds(chunk), colliders);
            }
        }

        // Capsules for the trees whose bases lie in [min, max) of the chunk on x and z.
        private void Fill(Bounds bounds, List<Collider> colliders)
        {
            StrataGrid grid = _trees.Grid;
            IReadOnlyList<StrataTreePrototype> prototypes = _trees.Prototypes;
            float2 min = new(bounds.min.x, bounds.min.z), max = new(bounds.max.x, bounds.max.z);
            int2 firstTile = math.clamp((int2)math.floor((min - grid.Origin) / grid.TileSize), 0, new int2(grid.TilesX - 1, grid.TilesZ - 1));
            int2 lastTile = math.clamp((int2)math.floor((max - grid.Origin) / grid.TileSize), 0, new int2(grid.TilesX - 1, grid.TilesZ - 1));
            float cellSize = grid.TileSize / _trees.CellsPerSide;
            for (int tz = firstTile.y; tz <= lastTile.y; tz++)
            for (int tx = firstTile.x; tx <= lastTile.x; tx++)
            {
                StrataTrees.Tile tile = _trees[grid.TileIndex(tx, tz)];
                float2 origin = grid.TileOrigin(tx, tz);
                int2 firstCell = math.clamp((int2)math.floor((min - origin) / cellSize), 0, _trees.CellsPerSide - 1);
                int2 lastCell = math.clamp((int2)math.floor((max - origin) / cellSize), 0, _trees.CellsPerSide - 1);
                for (int cz = firstCell.y; cz <= lastCell.y; cz++)
                for (int cx = firstCell.x; cx <= lastCell.x; cx++)
                {
                    StrataTreeCell cell = tile.Cells[cz * _trees.CellsPerSide + cx];
                    for (int r = cell.RunStart; r < cell.RunStart + cell.RunCount; r++)
                    {
                        StrataTreeRun run = tile.Runs[r];
                        StrataTreePrototype prototype = run.Prototype < prototypes.Count ? prototypes[run.Prototype] : null;
                        if (prototype == null || !prototype.HasCollider) continue;
                        for (int i = run.Start; i < run.Start + run.Count; i++)
                        {
                            float4 tree = tile.Instances[i];
                            if (tree.x < min.x || tree.x >= max.x || tree.z < min.y || tree.z >= max.y) continue;
                            StrataTreeRecord record = tile.Records[i];
                            float scale = record.Scale;
                            if (prototype.ColliderKind == StrataColliderKind.Mesh)
                            {
                                MeshCollider shape = TakeMesh();
                                shape.sharedMesh = prototype.ColliderMesh;
                                shape.transform.SetPositionAndRotation(new Vector3(tree.x, tree.y, tree.z), record.Rotation);
                                shape.transform.localScale = Vector3.one * scale;
                                colliders.Add(shape);
                                continue;
                            }
                            CapsuleCollider capsule = Take();
                            capsule.radius = prototype.ColliderRadius * scale;
                            capsule.height = math.max(prototype.ColliderHeight * scale, capsule.radius * 2f);
                            capsule.center = new Vector3(0f, capsule.height * 0.5f, 0f);
                            capsule.transform.position = new Vector3(tree.x, tree.y, tree.z);
                            colliders.Add(capsule);
                        }
                    }
                }
            }
            ActiveCount += colliders.Count;
        }

        private CapsuleCollider Take()
        {
            if (_pool.Count > 0)
            {
                CapsuleCollider pooled = _pool.Pop();
                pooled.gameObject.SetActive(true);
                return pooled;
            }
            GameObject go = new("Tree Collider") { layer = _layer, hideFlags = HideFlags.DontSave };
            go.transform.SetParent(_root, false);
            CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            return capsule;
        }

        private MeshCollider TakeMesh()
        {
            if (_meshPool.Count > 0)
            {
                MeshCollider pooled = _meshPool.Pop();
                pooled.gameObject.SetActive(true);
                return pooled;
            }
            GameObject go = new("Tree Mesh Collider") { layer = _layer, hideFlags = HideFlags.DontSave };
            go.transform.SetParent(_root, false);
            return go.AddComponent<MeshCollider>();
        }

        private void Release(List<Collider> colliders)
        {
            foreach (Collider collider in colliders)
            {
                collider.gameObject.SetActive(false);
                if (collider is MeshCollider shape) _meshPool.Push(shape);
                else _pool.Push((CapsuleCollider)collider);
            }
            ActiveCount -= colliders.Count;
            colliders.Clear();
        }

        public void Dispose()
        {
            _collision.ChunkActivated -= OnChunkActivated;
            _collision.ChunkDeactivated -= OnChunkDeactivated;
            _trees.Changed -= OnTreesChanged;
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }
    }
}
