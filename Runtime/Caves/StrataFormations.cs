using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata
{
    // A world's cave formations, per tile: the records from each tile's file (or the editor's placement), grouped by
    // prototype into batches over cells of the tile, and drawn with Graphics.RenderMeshInstanced for each camera just
    // before it culls, skipping batches outside its frustum or beyond the prototype's draw distance. The prototypes'
    // materials are drawn through instancing copies, so their assets are left alone.
    public sealed class StrataFormations : IDisposable
    {
        private const float CellSize = 64f;
        private const int MaxBatch = 1023;

        private sealed class Batch
        {
            public int Prototype;
            public readonly List<Matrix4x4> Matrices = new();
            public Matrix4x4[] Array;
            public Bounds Bounds;
        }

        private readonly StrataGrid _grid;
        private readonly List<Batch>[] _tiles;
        private readonly List<StrataFormationRecord>[] _records;
        private readonly Plane[] _planes = new Plane[6];
        private StrataFormationPrototype[] _prototypes = System.Array.Empty<StrataFormationPrototype>();
        private Material[][] _materials = System.Array.Empty<Material[]>();

        public int Layer { get; set; }
        public bool CastShadows { get; set; } = true;
        public int Count { get; private set; }
        public IReadOnlyList<StrataFormationPrototype> Prototypes => _prototypes;

        // A tile's formations changed.
        public event Action<int> TileChanged;

        public StrataFormations(StrataGrid grid)
        {
            _grid = grid;
            _tiles = new List<Batch>[grid.TileCount];
            _records = new List<StrataFormationRecord>[grid.TileCount];
            for (int i = 0; i < _tiles.Length; i++)
            {
                _tiles[i] = new List<Batch>();
                _records[i] = new List<StrataFormationRecord>();
            }
        }

        public StrataGrid Grid => _grid;

        // The prototypes the records refer to by index; every tile is cleared.
        public void SetPrototypes(IReadOnlyList<StrataFormationPrototype> prototypes)
        {
            DestroyMaterials();
            _prototypes = new StrataFormationPrototype[prototypes?.Count ?? 0];
            _materials = new Material[_prototypes.Length][];
            for (int i = 0; i < _prototypes.Length; i++)
            {
                StrataFormationPrototype prototype = prototypes[i];
                _prototypes[i] = prototype;
                if (prototype == null || !prototype.IsValid) continue;
                _materials[i] = new Material[prototype.Materials.Length];
                for (int m = 0; m < prototype.Materials.Length; m++)
                {
                    Material source = prototype.Materials[m];
                    if (source == null) continue;
                    _materials[i][m] = new Material(source) { name = source.name + " (Strata Instanced)", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                }
            }
            for (int tile = 0; tile < _tiles.Length; tile++) ReleaseTile(tile);
        }

        // A tile's formations, read from its file (none if it has no formations section).
        public void SetTile(int tile, NativeArray<byte> file, int splatSamples)
        {
            NativeArray<StrataFormationRecord> records = StrataTileFile.ReadFormations(file, _grid.Samples, splatSamples, Allocator.Temp);
            SetTile(tile, records);
            records.Dispose();
        }

        public void SetTile(int tile, IEnumerable<StrataFormationRecord> records)
        {
            Clear(tile);
            int2 coord = new(tile % _grid.TilesX, tile / _grid.TilesX);
            Dictionary<(int, int2), List<Batch>> batches = new();
            foreach (StrataFormationRecord record in records)
            {
                if (record.Prototype >= _prototypes.Length || _materials[record.Prototype] == null) continue;
                _records[tile].Add(record);
                float3 position = record.Position(_grid, coord);
                int2 cell = (int2)math.floor((position.xz - _grid.TileOrigin(coord.x, coord.y)) / CellSize);
                if (!batches.TryGetValue((record.Prototype, cell), out List<Batch> list)) batches[(record.Prototype, cell)] = list = new List<Batch>();
                if (list.Count == 0 || list[^1].Matrices.Count >= MaxBatch) list.Add(new Batch { Prototype = record.Prototype });
                Batch batch = list[^1];
                Matrix4x4 matrix = record.Matrix(_grid, coord);
                Bounds bounds = GeometryUtility.CalculateBounds(BoundsCorners(_prototypes[record.Prototype].Mesh.bounds), matrix);
                if (batch.Matrices.Count == 0) batch.Bounds = bounds;
                else batch.Bounds.Encapsulate(bounds);
                batch.Matrices.Add(matrix);
            }
            foreach (List<Batch> list in batches.Values)
                foreach (Batch batch in list)
                {
                    batch.Array = batch.Matrices.ToArray();
                    _tiles[tile].Add(batch);
                }
            Count += _records[tile].Count;
            TileChanged?.Invoke(tile);
        }

        public void ReleaseTile(int tile)
        {
            bool had = _records[tile].Count > 0;
            Clear(tile);
            if (had) TileChanged?.Invoke(tile);
        }

        // A tile's records, for colliders.
        public IReadOnlyList<StrataFormationRecord> this[int tile] => _records[tile];

        private void Clear(int tile)
        {
            Count -= _records[tile].Count;
            _records[tile].Clear();
            _tiles[tile].Clear();
        }

        private static Vector3[] BoundsCorners(Bounds bounds)
        {
            Vector3[] corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                corners[i] = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) != 0 ? 1f : -1f, (i & 2) != 0 ? 1f : -1f, (i & 4) != 0 ? 1f : -1f));
            return corners;
        }

        // Draws the formations a camera can see, just before it culls.
        public void Draw(Camera camera)
        {
            if (Count == 0) return;
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            Vector3 position = camera.transform.position;
            foreach (List<Batch> tile in _tiles)
                foreach (Batch batch in tile)
                {
                    StrataFormationPrototype prototype = _prototypes[batch.Prototype];
                    float distance = Mathf.Sqrt(batch.Bounds.SqrDistance(position));
                    if (distance > prototype.DrawDistance || !GeometryUtility.TestPlanesAABB(_planes, batch.Bounds)) continue;
                    Mesh mesh = prototype.Mesh;
                    Material[] materials = _materials[batch.Prototype];
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        Material material = materials[Mathf.Min(sub, materials.Length - 1)];
                        if (material == null) continue;
                        RenderParams parameters = new(material)
                        {
                            camera = camera,
                            layer = Layer,
                            worldBounds = batch.Bounds,
                            shadowCastingMode = CastShadows && prototype.CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                            receiveShadows = true,
                            lightProbeUsage = LightProbeUsage.Off
                        };
                        Graphics.RenderMeshInstanced(parameters, mesh, sub, batch.Array);
                    }
                }
        }

        private void DestroyMaterials()
        {
            foreach (Material[] materials in _materials)
                if (materials != null)
                    foreach (Material material in materials)
                        if (material != null) UnityEngine.Object.DestroyImmediate(material);
            _materials = System.Array.Empty<Material[]>();
        }

        public void Dispose()
        {
            for (int tile = 0; tile < _tiles.Length; tile++) Clear(tile);
            DestroyMaterials();
        }
    }
}
