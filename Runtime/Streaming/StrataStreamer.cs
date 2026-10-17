using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Strata
{
    // Keeps the tiles near the streaming centres (cameras and collision foci) resident in a streamed world. Each update
    // it ranks tiles by their distance from the nearest centre: tiles beyond the radius plus a margin give up their slot,
    // and tiles within the radius that aren't resident are read from their files with AsyncReadManager, nearest first, a
    // few at a time. A finished read puts the tile's heights, splats, strata and grass in a free slot (taking the
    // farthest resident tile's if none is free) and re-seats its trees on the full heights, a couple of tiles per update
    // so loading doesn't hitch. Tiles within the immediate radius (collision) are read straight away when first wanted.
    public sealed unsafe class StrataStreamer : IDisposable
    {
        private sealed class Load
        {
            public int Tile;
            public NativeArray<byte> Buffer;
            public ReadHandle Handle;
            public NativeArray<ReadCommand> Commands;
        }

        private readonly StrataWorldData _data;
        private readonly StrataHeightfield _heights;
        private readonly StrataSplatmap _splats;
        private readonly StrataGrassMap _grass;
        private readonly StrataTrees _trees;
        private readonly float _radius;
        private readonly float _immediateRadius;
        private readonly int _maxLoads;
        private readonly List<Load> _loads = new();
        private readonly long[] _sizes;
        private readonly float[] _distance;
        private readonly List<int> _order = new();

        public int ResidentCount { get; private set; }
        public int PendingCount => _loads.Count;
        public int LoadedTotal { get; private set; }
        public double LastApplyMilliseconds { get; private set; }

        // A tile became resident (after its data was installed).
        public event Action<int> TileLoaded;

        public StrataStreamer(StrataWorldData data, StrataHeightfield heights, StrataSplatmap splats, StrataGrassMap grass, StrataTrees trees,
                              float radius, float immediateRadius, int maxLoads)
        {
            _data = data;
            _heights = heights;
            _splats = splats;
            _grass = grass;
            _trees = trees;
            _radius = radius;
            _immediateRadius = immediateRadius;
            _maxLoads = math.max(1, maxLoads);
            _sizes = new long[heights.Grid.TileCount];
            _distance = new float[heights.Grid.TileCount];
        }

        public void Update(IReadOnlyList<float3> centres)
        {
            StrataGrid grid = _heights.Grid;
            Finish(2);
            if (centres.Count == 0) return;

            for (int tile = 0; tile < grid.TileCount; tile++)
            {
                float2 min = grid.TileOrigin(tile % grid.TilesX, tile / grid.TilesX), max = min + grid.TileSize;
                float best = float.MaxValue;
                foreach (float3 centre in centres)
                    best = math.min(best, math.length(math.max(0f, math.max(min - centre.xz, centre.xz - max))));
                _distance[tile] = best;
            }

            // give up slots well beyond the radius
            float keep = _radius + grid.TileSize * 0.5f;
            for (int tile = 0; tile < grid.TileCount; tile++)
                if (_heights.IsResident(tile) && _distance[tile] > keep) Release(tile);

            _order.Clear();
            for (int tile = 0; tile < grid.TileCount; tile++)
                if (_distance[tile] <= _radius && !_heights.IsResident(tile) && !Pending(tile)) _order.Add(tile);
            _order.Sort((a, b) => _distance[a].CompareTo(_distance[b]));
            foreach (int tile in _order)
            {
                if (_distance[tile] <= _immediateRadius)
                {
                    if (!TryFreeSlot(tile, out int slot)) break;
                    NativeArray<byte> file = _data.ReadTile(tile, Allocator.Temp);
                    Apply(tile, slot, file);
                    file.Dispose();
                    continue;
                }
                if (_loads.Count >= _maxLoads) break;
                Start(tile);
            }
            ResidentCount = 0;
            for (int tile = 0; tile < grid.TileCount; tile++)
                if (_heights.IsResident(tile)) ResidentCount++;
        }

        private bool Pending(int tile)
        {
            foreach (Load load in _loads)
                if (load.Tile == tile) return true;
            return false;
        }

        private void Start(int tile)
        {
            string path = _data.TilePath(tile);
            if (_sizes[tile] == 0) _sizes[tile] = new FileInfo(path).Length;
            Load load = new()
            {
                Tile = tile,
                Buffer = new NativeArray<byte>((int)_sizes[tile], Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Commands = new NativeArray<ReadCommand>(1, Allocator.Persistent)
            };
            load.Commands[0] = new ReadCommand { Buffer = load.Buffer.GetUnsafePtr(), Offset = 0, Size = _sizes[tile] };
            load.Handle = AsyncReadManager.Read(path, (ReadCommand*)load.Commands.GetUnsafePtr(), 1);
            _loads.Add(load);
        }

        // Installs up to count finished reads (nearest first, as they were started).
        private void Finish(int count)
        {
            for (int i = 0; i < _loads.Count && count > 0; i++)
            {
                Load load = _loads[i];
                ReadStatus status = load.Handle.Status;
                if (status == ReadStatus.InProgress) continue;
                _loads.RemoveAt(i--);
                if (status == ReadStatus.Complete && _distance[load.Tile] <= _radius + _heights.Grid.TileSize && TryFreeSlot(load.Tile, out int slot))
                {
                    Apply(load.Tile, slot, load.Buffer);
                    count--;
                }
                Dispose(load);
            }
        }

        private void Apply(int tile, int slot, NativeArray<byte> file)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            StrataGrid grid = _heights.Grid;
            NativeArray<ushort> samples = new(grid.SamplesPerTile, Allocator.Temp);
            StrataTileFile.Read(file, grid.Samples, samples.Slice());
            _heights.SetTile(tile, slot, samples.Slice());
            samples.Dispose();
            _splats.SetTile(tile, slot, file, _data.Strata);
            _grass?.SetTile(slot, file);
            _trees.RebuildTile(tile);
            LoadedTotal++;
            LastApplyMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            TileLoaded?.Invoke(tile);
        }

        // A free slot, or the slot of the farthest resident tile if it is farther than this one.
        private bool TryFreeSlot(int tile, out int slot)
        {
            int farthest = -1;
            for (slot = 0; slot < _heights.SlotCount; slot++)
            {
                int holder = _heights.TileOf(slot);
                if (holder < 0) return true;
                if (farthest < 0 || _distance[holder] > _distance[_heights.TileOf(farthest)]) farthest = slot;
            }
            slot = farthest;
            if (farthest < 0 || _distance[_heights.TileOf(farthest)] <= _distance[tile]) return false;
            Release(_heights.TileOf(farthest));
            return true;
        }

        private void Release(int tile)
        {
            _heights.ReleaseTile(tile);
            _splats.ReleaseTile(tile);
        }

        private static void Dispose(Load load)
        {
            if (load.Handle.IsValid())
            {
                load.Handle.JobHandle.Complete();
                load.Handle.Dispose();
            }
            if (load.Buffer.IsCreated) load.Buffer.Dispose();
            if (load.Commands.IsCreated) load.Commands.Dispose();
        }

        public void Dispose()
        {
            foreach (Load load in _loads)
            {
                if (load.Handle.IsValid() && load.Handle.Status == ReadStatus.InProgress) load.Handle.Cancel();
                Dispose(load);
            }
            _loads.Clear();
        }
    }
}
