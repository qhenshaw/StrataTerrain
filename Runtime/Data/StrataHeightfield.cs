using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Strata
{
    // A world's heights in the forms its users need: full-resolution samples in slots on the CPU (for collision, trees
    // and queries) and in a GPU texture array (one R16 slice per slot, for the surface shader and the editor's compute
    // preview to write into), a tile-to-slot map on both (-1 where a tile isn't resident), a coarse far copy of the
    // whole world on both (StrataHeights.FarStep), and the min/max height of every CDLOD node (for culling). A resident
    // heightfield has a slot per tile, slot i holding tile i; a streamed one has fewer and the streamer fills them as
    // tiles come near, its node bounds and far copy coming from the bake's far file.
    public sealed class StrataHeightfield : IDisposable
    {
        private NativeArray<int> _tileSlots;
        private readonly int[] _slotTiles;
        private NativeArray<ushort> _far;

        public StrataGrid Grid { get; }
        public int SlotCount { get; }
        public bool Streamed { get; }
        // Full-resolution samples, slot by slot (slot * SamplesPerTile).
        public NativeArray<ushort> Heights { get; private set; }
        public NativeArray<float2> NodeMinMax { get; private set; }
        public RenderTexture Texture { get; private set; }
        public Texture2D FarTexture { get; private set; }
        public GraphicsBuffer TileSlotBuffer { get; private set; }
        public NativeArray<int> TileSlots => _tileSlots;
        public NativeArray<ushort> Far => _far;
        public StrataHeights View => new() { Grid = Grid, Slots = Heights, TileSlots = _tileSlots, Far = _far };

        // Jobs that read Heights or NodeMinMax (culling, collision); writers complete these first.
        public JobHandle Readers { get; set; }

        // A slot for every tile (slotCount 0), or a streamed heightfield with that many slots, all empty.
        public StrataHeightfield(StrataGrid grid, int slotCount = 0)
        {
            Grid = grid;
            Streamed = slotCount > 0 && slotCount < grid.TileCount;
            SlotCount = Streamed ? slotCount : grid.TileCount;
            Heights = new(grid.SamplesPerTile * SlotCount, Allocator.Persistent);
            NodeMinMax = new(grid.NodeCount, Allocator.Persistent);
            _tileSlots = new NativeArray<int>(grid.TileCount, Allocator.Persistent);
            _slotTiles = new int[SlotCount];
            for (int i = 0; i < grid.TileCount; i++) _tileSlots[i] = Streamed ? -1 : i;
            for (int i = 0; i < SlotCount; i++) _slotTiles[i] = Streamed ? -1 : i;
            int2 far = StrataHeights.FarSamples(grid);
            _far = new NativeArray<ushort>(far.x * far.y, Allocator.Persistent);
            Texture = new(grid.Samples, grid.Samples, GraphicsFormat.R16_UNorm, GraphicsFormat.None)
            {
                name = "Strata Heights",
                dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                volumeDepth = SlotCount,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            Texture.Create();
            FarTexture = new Texture2D(far.x, far.y, GraphicsFormat.R16_UNorm, TextureCreationFlags.DontInitializePixels)
            {
                name = "Strata Far Heights",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            TileSlotBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, grid.TileCount, 4);
            UploadTileSlots();
            Fill(math.clamp(0f, grid.MinHeight, grid.MaxHeight));
        }

        public int SlotOf(int tile) => _tileSlots[tile];
        public int TileOf(int slot) => _slotTiles[slot];
        public bool IsResident(int tile) => _tileSlots[tile] >= 0;

        public void Fill(float height)
        {
            Readers.Complete();
            NativeArray<ushort> heights = Heights;
            ushort value = Grid.ToStored(height);
            for (int i = 0; i < heights.Length; i++) heights[i] = value;
            for (int i = 0; i < _far.Length; i++) _far[i] = value;
            NativeArray<float2> nodes = NodeMinMax;
            for (int i = 0; i < nodes.Length; i++) nodes[i] = new float2(height, height);
            Upload();
            UploadFar();
        }

        // Loads every tile from a bake (resident heightfields). Returns false (leaving heights alone) if the bake doesn't
        // match this grid.
        public bool Load(StrataWorldData data)
        {
            if (Streamed || data == null || !data.IsComplete || !data.Grid.Equals(Grid)) return false;
            Readers.Complete();
            for (int tile = 0; tile < Grid.TileCount; tile++)
            {
                NativeArray<byte> file = data.ReadTile(tile, Allocator.Temp);
                StrataTileFile.Read(file, Grid.Samples, Heights.Slice(tile * Grid.SamplesPerTile, Grid.SamplesPerTile));
                file.Dispose();
            }
            UpdateBounds();
            Upload();
            BuildFar();
            return true;
        }

        // The far copy and node bounds of a streamed heightfield, from the bake's far file.
        public void LoadFar(StrataFarFile.Data far)
        {
            Readers.Complete();
            NativeArray<ushort>.Copy(far.Heights, _far, _far.Length);
            NativeArray<float2>.Copy(far.NodeMinMax, NodeMinMax, NodeMinMax.Length);
            UploadFar();
        }

        // Puts a tile's samples (SamplesPerTile of them) in a slot, on the CPU and GPU, and points the tile at it.
        public void SetTile(int tile, int slot, NativeSlice<ushort> samples)
        {
            Readers.Complete();
            if (_slotTiles[slot] >= 0 && _slotTiles[slot] != tile) _tileSlots[_slotTiles[slot]] = -1;
            NativeArray<ushort> heights = Heights;
            new NativeSlice<ushort>(heights, slot * Grid.SamplesPerTile, Grid.SamplesPerTile).CopyFrom(samples);
            UploadSlot(slot);
            _slotTiles[slot] = tile;
            _tileSlots[tile] = slot;
            UploadTileSlots();
        }

        // Stops a tile using its slot, so it falls back to the far copy.
        public void ReleaseTile(int tile)
        {
            Readers.Complete();
            int slot = _tileSlots[tile];
            if (slot < 0) return;
            _tileSlots[tile] = -1;
            _slotTiles[slot] = -1;
            UploadTileSlots();
        }

        // Copies the CPU heights of every slot to the GPU texture.
        public void Upload()
        {
            Texture2DArray staging = new(Grid.Samples, Grid.Samples, SlotCount, GraphicsFormat.R16_UNorm, TextureCreationFlags.DontInitializePixels);
            for (int slot = 0; slot < SlotCount; slot++)
                staging.SetPixelData(Heights, 0, slot, slot * Grid.SamplesPerTile);
            staging.Apply(false, false);
            Graphics.CopyTexture(staging, Texture);
            UnityEngine.Object.DestroyImmediate(staging);
        }

        private void UploadSlot(int slot)
        {
            StrataSliceUpload.Copy(Heights.GetSubArray(slot * Grid.SamplesPerTile, Grid.SamplesPerTile), Grid.Samples, GraphicsFormat.R16_UNorm, Texture, slot);
        }

        private void UploadFar()
        {
            FarTexture.SetPixelData(_far, 0);
            FarTexture.Apply(false, false);
        }

        private void UploadTileSlots() => TileSlotBuffer.SetData(_tileSlots);

        // The far copy from the full heights (resident heightfields: every FarStep-th world sample).
        public void BuildFar()
        {
            if (Streamed) return;
            Readers.Complete();
            int2 size = StrataHeights.FarSamples(Grid);
            StrataHeights view = View;
            for (int z = 0; z < size.y; z++)
            for (int x = 0; x < size.x; x++)
                _far[z * size.x + x] = Grid.ToStored(view.At(new int2(x, z) * StrataHeights.FarStep));
            UploadFar();
        }

        // Recomputes node bounds for every resident tile, or only the given ones.
        public void UpdateBounds(NativeArray<int> tiles = default)
        {
            Readers.Complete();
            NativeArray<int> all = default;
            if (!tiles.IsCreated)
            {
                all = new(Grid.TileCount, Allocator.TempJob);
                for (int i = 0; i < all.Length; i++) all[i] = i;
                tiles = all;
            }
            new BoundsJob { Grid = Grid, Heights = Heights, TileSlots = _tileSlots, Tiles = tiles, NodeMinMax = NodeMinMax }.Schedule(tiles.Length, 1).Complete();
            if (all.IsCreated) all.Dispose();
        }

        // Bilinear height at a world position, clamped to the world's edges (from the far copy where not resident).
        public float SampleHeight(float2 position)
        {
            Readers.Complete();
            return View.Sample(position);
        }

        public void Dispose()
        {
            Readers.Complete();
            if (Heights.IsCreated) Heights.Dispose();
            if (NodeMinMax.IsCreated) NodeMinMax.Dispose();
            if (_tileSlots.IsCreated) _tileSlots.Dispose();
            if (_far.IsCreated) _far.Dispose();
            TileSlotBuffer?.Dispose();
            TileSlotBuffer = null;
            if (Texture != null) UnityEngine.Object.DestroyImmediate(Texture);
            if (FarTexture != null) UnityEngine.Object.DestroyImmediate(FarTexture);
            Texture = null;
            FarTexture = null;
        }

        // Leaf nodes take the min/max of their samples (edges included, so neighbours overlap by one sample); each
        // coarser node takes the min/max of its four children.
        [BurstCompile]
        private struct BoundsJob : IJobParallelFor
        {
            public StrataGrid Grid;
            [ReadOnly] public NativeArray<ushort> Heights;
            [ReadOnly] public NativeArray<int> TileSlots;
            [ReadOnly] public NativeArray<int> Tiles;
            [NativeDisableParallelForRestriction] public NativeArray<float2> NodeMinMax;

            public void Execute(int index)
            {
                int tile = Tiles[index];
                int slot = TileSlots[tile];
                if (slot < 0) return;
                int samples = Grid.Samples;
                int baseSample = slot * Grid.SamplesPerTile;
                int leaves = Grid.LeavesPerSide;
                for (int z = 0; z < leaves; z++)
                for (int x = 0; x < leaves; x++)
                {
                    int lo = 65535, hi = 0;
                    for (int sz = 0; sz <= StrataGrid.PatchQuads; sz++)
                    {
                        int row = baseSample + (z * StrataGrid.PatchQuads + sz) * samples + x * StrataGrid.PatchQuads;
                        for (int sx = 0; sx <= StrataGrid.PatchQuads; sx++)
                        {
                            int h = Heights[row + sx];
                            lo = math.min(lo, h);
                            hi = math.max(hi, h);
                        }
                    }
                    NodeMinMax[Grid.NodeIndex(tile, 0, x, z)] = new float2(Grid.ToHeight((ushort)lo), Grid.ToHeight((ushort)hi));
                }

                for (int level = 1; level < Grid.Levels; level++)
                {
                    int side = Grid.NodesPerSide(level);
                    for (int z = 0; z < side; z++)
                    for (int x = 0; x < side; x++)
                    {
                        float2 a = NodeMinMax[Grid.NodeIndex(tile, level - 1, x * 2, z * 2)];
                        float2 b = NodeMinMax[Grid.NodeIndex(tile, level - 1, x * 2 + 1, z * 2)];
                        float2 c = NodeMinMax[Grid.NodeIndex(tile, level - 1, x * 2, z * 2 + 1)];
                        float2 d = NodeMinMax[Grid.NodeIndex(tile, level - 1, x * 2 + 1, z * 2 + 1)];
                        NodeMinMax[Grid.NodeIndex(tile, level, x, z)] = new float2(math.min(math.min(a.x, b.x), math.min(c.x, d.x)),
                                                                                    math.max(math.max(a.y, b.y), math.max(c.y, d.y)));
                    }
                }
            }
        }
    }
}
