using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // Trunk capsules for the trees inside each active collision chunk: when the terrain's collision activates a chunk,
    // every tree standing in it whose prototype has a collider gets a pooled CapsuleCollider (on the collision layer, so
    // physics and NavMesh building see it with the ground); when the chunk deactivates they go back to the pool. Trees
    // changing in a tile rebuild the capsules of the chunks over it.
    public sealed class StrataTreeColliders : IDisposable
    {
        private readonly StrataTrees _trees;
        private readonly StrataCollision _collision;
        private readonly Transform _root;
        private readonly int _layer;
        private readonly Dictionary<int2, List<CapsuleCollider>> _active = new();
        private readonly Stack<CapsuleCollider> _pool = new();

        public int ActiveCount { get; private set; }

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
            List<CapsuleCollider> colliders = new();
            _active[chunk] = colliders;
            Fill(bounds, colliders);
        }

        private void OnChunkDeactivated(int2 chunk, Bounds bounds)
        {
            if (!_active.Remove(chunk, out List<CapsuleCollider> colliders)) return;
            Release(colliders);
        }

        private void OnTreesChanged(int tile)
        {
            List<int2> chunks = new(_active.Keys);
            foreach (int2 chunk in chunks)
            {
                List<CapsuleCollider> colliders = _active[chunk];
                Release(colliders);
                Fill(_collision.ChunkBounds(chunk), colliders);
            }
        }

        // Capsules for the trees whose bases lie in [min, max) of the chunk on x and z.
        private void Fill(Bounds bounds, List<CapsuleCollider> colliders)
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
                            float scale = tile.Records[i].Scale;
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

        private void Release(List<CapsuleCollider> colliders)
        {
            foreach (CapsuleCollider capsule in colliders)
            {
                capsule.gameObject.SetActive(false);
                _pool.Push(capsule);
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
