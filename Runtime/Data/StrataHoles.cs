using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Strata
{
    // Where caves cut through a heightfield: a byte of terrain presence per height sample (255 solid, below 128 a hole,
    // bilinear in between so hole edges are smooth), for the tiles that have holes. Like the heights it lives in slots on
    // the CPU (for collision, tree placement and ground queries) and in an R8 texture array (for the surface shader, which
    // clips holes, and grass), found through a tile-to-slot map that is -1 for tiles without holes. A heightfield with a
    // slot per tile maps tile i to slot i; a streamed one has a few slots, taken by holed tiles as they load. It also keeps
    // a flag per CDLOD node, set where any of the node's samples is a hole, so the renderer draws only those nodes with the
    // clipping shader variant. Its owner, the StrataHeightfield, makes the changes (after the jobs reading it finish).
    public sealed class StrataHoles : IDisposable
    {
        public const byte Solid = 255;

        private NativeArray<int> _tileSlots;
        private readonly int[] _slotTiles;
        private readonly bool _identity;
        private readonly bool[] _holed;

        public StrataGrid Grid { get; }
        public int SlotCount { get; }
        // Presence, slot by slot (slot * SamplesPerTile).
        public NativeArray<byte> Presence { get; private set; }
        public NativeArray<int> TileSlots => _tileSlots;
        // 1 for CDLOD nodes over a hole.
        public NativeArray<byte> NodeHoles { get; private set; }
        public RenderTexture Texture { get; private set; }
        public GraphicsBuffer TileSlotBuffer { get; private set; }
        // How many tiles have a hole.
        public int HoledCount { get; private set; }

        // A slot for every tile (slotCount 0), or that many slots for the tiles that turn out to have holes.
        public StrataHoles(StrataGrid grid, int slotCount = 0)
        {
            Grid = grid;
            _identity = slotCount <= 0 || slotCount >= grid.TileCount;
            SlotCount = _identity ? grid.TileCount : slotCount;
            Presence = new(grid.SamplesPerTile * SlotCount, Allocator.Persistent);
            NodeHoles = new(grid.NodeCount, Allocator.Persistent);
            _tileSlots = new(grid.TileCount, Allocator.Persistent);
            _slotTiles = new int[SlotCount];
            _holed = new bool[grid.TileCount];
            for (int i = 0; i < grid.TileCount; i++) _tileSlots[i] = _identity ? i : -1;
            for (int i = 0; i < SlotCount; i++) _slotTiles[i] = _identity ? i : -1;
            NativeArray<byte> presence = Presence;
            for (int i = 0; i < presence.Length; i++) presence[i] = Solid;
            Texture = new(grid.Samples, grid.Samples, GraphicsFormat.R8_UNorm, GraphicsFormat.None)
            {
                name = "Strata Holes",
                dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                volumeDepth = SlotCount,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            Texture.Create();
            for (int slot = 0; slot < SlotCount; slot++) UploadSlot(slot);
            TileSlotBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, grid.TileCount, 4);
            TileSlotBuffer.SetData(_tileSlots);
        }

        public bool HasHoles(int tile) => _holed[tile];

        private void SetHoled(int tile, bool holed)
        {
            if (_holed[tile] == holed) return;
            _holed[tile] = holed;
            HoledCount += holed ? 1 : -1;
        }

        // Puts a tile's presence (SamplesPerTile bytes, or none for a tile without holes) in place.
        public void SetTile(int tile, NativeSlice<byte> presence, bool any)
        {
            int slot = _tileSlots[tile];
            if (!any && !_identity)
            {
                ReleaseTile(tile);
                return;
            }
            if (slot < 0)
            {
                slot = Array.IndexOf(_slotTiles, -1);
                if (slot < 0)
                {
                    Debug.LogWarning($"Strata: no free hole slot for tile {tile}; its caves have no entrances until one frees up.");
                    return;
                }
                _slotTiles[slot] = tile;
                _tileSlots[tile] = slot;
                TileSlotBuffer.SetData(_tileSlots);
            }
            NativeArray<byte> all = Presence;
            NativeSlice<byte> target = new(all, slot * Grid.SamplesPerTile, Grid.SamplesPerTile);
            if (any) target.CopyFrom(presence);
            else
                for (int i = 0; i < target.Length; i++) target[i] = Solid;
            UploadSlot(slot);
            UpdateNodes(tile);
            SetHoled(tile, any);
        }

        // Reads a tile's holes from its file (resident or streamed tiles as they load).
        public void SetTile(int tile, NativeArray<byte> file, int splatSamples)
        {
            NativeArray<byte> presence = new(Grid.SamplesPerTile, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            bool any = StrataTileFile.ReadHoles(file, Grid.Samples, splatSamples, presence.Slice());
            SetTile(tile, presence.Slice(), any);
            presence.Dispose();
        }

        // A tile without holes again: its slot is freed (streamed) or filled solid.
        public void ReleaseTile(int tile)
        {
            int slot = _tileSlots[tile];
            if (slot < 0) return;
            if (_identity)
            {
                NativeArray<byte> all = Presence;
                for (int i = slot * Grid.SamplesPerTile; i < (slot + 1) * Grid.SamplesPerTile; i++) all[i] = Solid;
                UploadSlot(slot);
            }
            else
            {
                _tileSlots[tile] = -1;
                _slotTiles[slot] = -1;
                TileSlotBuffer.SetData(_tileSlots);
            }
            ClearNodes(tile);
            SetHoled(tile, false);
        }

        private void UploadSlot(int slot) =>
            StrataSliceUpload.Copy(Presence.GetSubArray(slot * Grid.SamplesPerTile, Grid.SamplesPerTile), Grid.Samples, GraphicsFormat.R8_UNorm, Texture, slot);

        private void ClearNodes(int tile)
        {
            NativeArray<byte> nodes = NodeHoles;
            for (int i = tile * Grid.NodesPerTile; i < (tile + 1) * Grid.NodesPerTile; i++) nodes[i] = 0;
        }

        private void UpdateNodes(int tile)
        {
            new NodeJob { Grid = Grid, Presence = Presence, Slot = _tileSlots[tile], Tile = tile, NodeHoles = NodeHoles }.Run();
        }

        public void Dispose()
        {
            if (Presence.IsCreated) Presence.Dispose();
            if (NodeHoles.IsCreated) NodeHoles.Dispose();
            if (_tileSlots.IsCreated) _tileSlots.Dispose();
            TileSlotBuffer?.Dispose();
            TileSlotBuffer = null;
            if (Texture != null) UnityEngine.Object.DestroyImmediate(Texture);
            Texture = null;
        }

        // A leaf is holed if any of its samples (edges included) is below half presence; coarser nodes if any child is.
        [BurstCompile]
        private struct NodeJob : IJob
        {
            public StrataGrid Grid;
            [ReadOnly] public NativeArray<byte> Presence;
            public int Slot;
            public int Tile;
            public NativeArray<byte> NodeHoles;

            public void Execute()
            {
                int samples = Grid.Samples;
                int baseSample = Slot * Grid.SamplesPerTile;
                int leaves = Grid.LeavesPerSide;
                for (int z = 0; z < leaves; z++)
                for (int x = 0; x < leaves; x++)
                {
                    byte holed = 0;
                    for (int sz = 0; sz <= StrataGrid.PatchQuads && holed == 0; sz++)
                    {
                        int row = baseSample + (z * StrataGrid.PatchQuads + sz) * samples + x * StrataGrid.PatchQuads;
                        for (int sx = 0; sx <= StrataGrid.PatchQuads; sx++)
                            if (Presence[row + sx] < 128)
                            {
                                holed = 1;
                                break;
                            }
                    }
                    NodeHoles[Grid.NodeIndex(Tile, 0, x, z)] = holed;
                }

                for (int level = 1; level < Grid.Levels; level++)
                {
                    int side = Grid.NodesPerSide(level);
                    for (int z = 0; z < side; z++)
                    for (int x = 0; x < side; x++)
                        NodeHoles[Grid.NodeIndex(Tile, level, x, z)] = (byte)(NodeHoles[Grid.NodeIndex(Tile, level - 1, x * 2, z * 2)] |
                                                                              NodeHoles[Grid.NodeIndex(Tile, level - 1, x * 2 + 1, z * 2)] |
                                                                              NodeHoles[Grid.NodeIndex(Tile, level - 1, x * 2, z * 2 + 1)] |
                                                                              NodeHoles[Grid.NodeIndex(Tile, level - 1, x * 2 + 1, z * 2 + 1)]);
                }
            }
        }
    }
}
