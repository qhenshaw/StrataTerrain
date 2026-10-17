using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Strata
{
    // A cell of a tile's trees for culling and collision: the box holding them and its runs (one per prototype).
    public struct StrataTreeCell
    {
        public float3 Min;
        public float3 Max;
        public int RunStart;
        public int RunCount;
    }

    // Consecutive instances of one prototype within a cell.
    public struct StrataTreeRun
    {
        public int Prototype;
        public int Start;
        public int Count;
    }

    // A world's placed trees. Per tile it keeps the baked records and what the renderer and colliders use: every tree
    // as a float4 (base position on the terrain, sunk by its sink, and the record's low 24 bits as a float), sorted by
    // cell (CellSize metres square) then prototype, with each cell's bounds and per-prototype runs. Instances and runs
    // are indexed from the start of their tile. Tiles are rebuilt when their records or their heights change; every
    // tile's trees are kept, standing on the far heights until their tile streams in.
    public sealed class StrataTrees : IDisposable
    {
        public const float CellSize = 64f;

        public sealed class Tile : IDisposable
        {
            public NativeArray<StrataTreeRecord> Records;
            public NativeArray<float4> Instances;
            public NativeArray<StrataTreeCell> Cells;
            public NativeArray<StrataTreeRun> Runs;

            public void Dispose()
            {
                if (Records.IsCreated) Records.Dispose();
                if (Instances.IsCreated) Instances.Dispose();
                if (Cells.IsCreated) Cells.Dispose();
                if (Runs.IsCreated) Runs.Dispose();
            }
        }

        private readonly StrataHeightfield _heights;
        private readonly Tile[] _tiles;
        private NativeArray<float2> _extents;

        public StrataGrid Grid { get; }
        public int CellsPerSide { get; }
        public IReadOnlyList<StrataTreePrototype> Prototypes { get; private set; } = Array.Empty<StrataTreePrototype>();
        public int Count { get; private set; }
        public Tile this[int tile] => _tiles[tile];

        // A tile's trees changed (or all of them, with -1), after the tile was rebuilt.
        public event Action<int> Changed;

        public StrataTrees(StrataHeightfield heights)
        {
            _heights = heights;
            Grid = heights.Grid;
            CellsPerSide = math.max(1, (int)math.round(Grid.TileSize / CellSize));
            _tiles = new Tile[Grid.TileCount];
            for (int i = 0; i < _tiles.Length; i++) _tiles[i] = new Tile();
            _extents = new NativeArray<float2>(1, Allocator.Persistent);
            for (int i = 0; i < _tiles.Length; i++) Build(i, new NativeArray<StrataTreeRecord>(0, Allocator.Temp));
        }

        // The prototypes the records' indices refer to; tiles are rebuilt for their new bounds.
        public void SetPrototypes(IReadOnlyList<StrataTreePrototype> prototypes)
        {
            Prototypes = prototypes ?? Array.Empty<StrataTreePrototype>();
            _extents.Dispose();
            _extents = new NativeArray<float2>(math.max(1, Prototypes.Count), Allocator.Persistent);
            for (int i = 0; i < Prototypes.Count; i++)
                _extents[i] = Prototypes[i] != null ? new float2(Prototypes[i].Height, Prototypes[i].Reach) : new float2(1f, 1f);
            RebuildAll();
        }

        // Replaces a tile's trees (the records are copied).
        public void SetTile(int tile, NativeArray<StrataTreeRecord> records)
        {
            Build(tile, records);
            Changed?.Invoke(tile);
        }

        // Re-seats a tile's trees on its current heights.
        public void RebuildTile(int tile)
        {
            NativeArray<StrataTreeRecord> records = new(_tiles[tile].Records, Allocator.Temp);
            Build(tile, records);
            records.Dispose();
            Changed?.Invoke(tile);
        }

        public void RebuildAll()
        {
            for (int i = 0; i < _tiles.Length; i++)
            {
                NativeArray<StrataTreeRecord> records = new(_tiles[i].Records, Allocator.Temp);
                Build(i, records);
                records.Dispose();
            }
            Changed?.Invoke(-1);
        }

        public void Clear()
        {
            for (int i = 0; i < _tiles.Length; i++) Build(i, new NativeArray<StrataTreeRecord>(0, Allocator.Temp));
            Changed?.Invoke(-1);
        }

        // Loads every tile's trees from a bake; false if the bake doesn't match (trees are left empty).
        public bool Load(StrataWorldData data)
        {
            if (data == null || !data.IsComplete || !data.Grid.Equals(Grid)) return false;
            SetPrototypes(data.TreePrototypes);
            for (int tile = 0; tile < Grid.TileCount; tile++)
            {
                NativeArray<StrataTreeRecord> records = StrataTileFile.ReadTrees(data.TilePath(tile), Grid.Samples, Grid.SplatSamples, Allocator.Temp);
                Build(tile, records);
                records.Dispose();
            }
            Changed?.Invoke(-1);
            return true;
        }

        private void Build(int tile, NativeArray<StrataTreeRecord> records)
        {
            Tile t = _tiles[tile];
            Count -= t.Records.IsCreated ? t.Records.Length : 0;
            t.Dispose();
            _heights.Readers.Complete();
            int cells = CellsPerSide * CellsPerSide;
            t.Records = new NativeArray<StrataTreeRecord>(records.Length, Allocator.Persistent);
            t.Instances = new NativeArray<float4>(records.Length, Allocator.Persistent);
            t.Cells = new NativeArray<StrataTreeCell>(cells, Allocator.Persistent);
            NativeList<StrataTreeRun> runs = new(64, Allocator.TempJob);
            // jobs can't take Temp memory, which callers often pass
            NativeArray<StrataTreeRecord> input = new(records, Allocator.TempJob);
            new BuildJob
            {
                Grid = Grid,
                Tile = tile,
                CellsPerSide = CellsPerSide,
                Heights = _heights.View,
                Extents = _extents,
                Input = input,
                Records = t.Records,
                Instances = t.Instances,
                Cells = t.Cells,
                Runs = runs
            }.Run();
            t.Runs = new NativeArray<StrataTreeRun>(runs.AsArray(), Allocator.Persistent);
            runs.Dispose();
            input.Dispose();
            Count += records.Length;
        }

        public void Dispose()
        {
            foreach (Tile tile in _tiles) tile.Dispose();
            if (_extents.IsCreated) _extents.Dispose();
        }

        [BurstCompile]
        private struct BuildJob : IJob
        {
            public StrataGrid Grid;
            public int Tile;
            public int CellsPerSide;
            public StrataHeights Heights;
            [ReadOnly] public NativeArray<float2> Extents;
            [ReadOnly] public NativeArray<StrataTreeRecord> Input;
            public NativeArray<StrataTreeRecord> Records;
            public NativeArray<float4> Instances;
            public NativeArray<StrataTreeCell> Cells;
            public NativeList<StrataTreeRun> Runs;

            public void Execute()
            {
                // sort by cell, then prototype, then input order (so equal inputs give equal outputs)
                NativeArray<long> keys = new(Input.Length, Allocator.Temp);
                for (int i = 0; i < Input.Length; i++)
                {
                    StrataTreeRecord r = Input[i];
                    int2 cell = math.min(new int2(r.X, r.Z) * CellsPerSide / 65536, CellsPerSide - 1);
                    long key = ((long)(cell.y * CellsPerSide + cell.x) << 40) | ((long)r.Prototype << 24) | (uint)i;
                    keys[i] = key;
                }
                keys.Sort();

                for (int c = 0; c < Cells.Length; c++)
                    Cells[c] = new StrataTreeCell { Min = new float3(float.MaxValue), Max = new float3(float.MinValue) };
                int lastCell = -1, lastPrototype = -1;
                for (int i = 0; i < keys.Length; i++)
                {
                    long key = keys[i];
                    int source = (int)(key & 0xffffff);
                    int cellIndex = (int)(key >> 40);
                    StrataTreeRecord r = Input[source];
                    Records[i] = r;
                    float2 xz = r.Position(Grid, Tile);
                    float y = Heights.Sample(xz) - r.Sink;
                    Instances[i] = new float4(xz.x, y, xz.y, r.GpuBits);

                    if (cellIndex != lastCell || r.Prototype != lastPrototype)
                    {
                        StrataTreeCell started = Cells[cellIndex];
                        if (cellIndex != lastCell) started.RunStart = Runs.Length;
                        started.RunCount++;
                        Cells[cellIndex] = started;
                        Runs.Add(new StrataTreeRun { Prototype = r.Prototype, Start = i, Count = 0 });
                        lastCell = cellIndex;
                        lastPrototype = r.Prototype;
                    }
                    StrataTreeRun run = Runs[^1];
                    run.Count++;
                    Runs[^1] = run;

                    float2 extent = r.Prototype < Extents.Length ? Extents[r.Prototype] * r.Scale : new float2(1f);
                    StrataTreeCell cell = Cells[cellIndex];
                    cell.Min = math.min(cell.Min, new float3(xz.x - extent.y, y, xz.y - extent.y));
                    cell.Max = math.max(cell.Max, new float3(xz.x + extent.y, y + extent.x, xz.y + extent.y));
                    Cells[cellIndex] = cell;
                }
                keys.Dispose();
            }
        }
    }
}
