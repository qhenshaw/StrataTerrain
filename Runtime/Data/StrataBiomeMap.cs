using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;

namespace Strata
{
    // A world's biome maps, on the CPU, in slots like its splats (slot i is whichever tile the heightfield put there) at
    // splat resolution with shared edges: one for the surface and one for underground. Each texel packs its two
    // strongest biomes (indices into the world's biome list, None for none) and their weights, which add up to 1, as
    // bytes b0 | b1 << 8 | w0 << 16 | w1 << 24, the strongest first. Beyond the resident tiles queries fall back to the
    // far maps (every FarSplatStep-th texel of the whole world). The editor's biome evaluator writes them; a bake stores
    // each tile's in its file (run-length coded, as they are mostly uniform) and the far maps in the far file.
    public sealed class StrataBiomeMap : IDisposable
    {
        public const int None = 255;
        // how many indices a filter covers: every biome and None
        public const int Indices = 256;
        public const uint NoneTexel = 0xffu | 0xffu << 8 | 0xffu << 16;

        public StrataGrid Grid { get; }
        public int SlotCount { get; }
        public NativeArray<uint> CpuSurface { get; private set; }
        public NativeArray<uint> CpuUnderground { get; private set; }
        public NativeArray<uint> CpuFarSurface { get; private set; }
        public NativeArray<uint> CpuFarUnderground { get; private set; }

        // The maps and what a query needs to read them, for Burst jobs.
        public struct View
        {
            public StrataGrid Grid;
            [ReadOnly] public NativeArray<int> TileSlots;
            [ReadOnly] public NativeArray<uint> Surface;
            [ReadOnly] public NativeArray<uint> Underground;
            [ReadOnly] public NativeArray<uint> FarSurface;
            [ReadOnly] public NativeArray<uint> FarUnderground;

            public bool IsCreated => Surface.IsCreated;

            // The packed texel nearest a position: the tile's where resident, else the far map's.
            public uint Sample(float2 position, bool underground)
            {
                if (!Surface.IsCreated) return NoneTexel;
                float2 relative = (position - Grid.Origin) / Grid.TileSize;
                int2 tile = math.clamp((int2)math.floor(relative), 0, new int2(Grid.TilesX - 1, Grid.TilesZ - 1));
                int slot = TileSlots[Grid.TileIndex(tile.x, tile.y)];
                if (slot >= 0)
                {
                    int2 texel = (int2)math.round(math.saturate(relative - tile) * Grid.SplatResolution);
                    int index = slot * Grid.SplatSamplesPerTile + texel.y * Grid.SplatSamples + texel.x;
                    return underground ? Underground[index] : Surface[index];
                }
                int2 size = StrataHeights.FarSplatSamples(Grid);
                int2 far = (int2)math.round(math.saturate((position - Grid.Origin) / Grid.Size) * (size - 1));
                return underground ? FarUnderground[far.y * size.x + far.x] : FarSurface[far.y * size.x + far.x];
            }
        }

        public StrataBiomeMap(StrataGrid grid, int slotCount)
        {
            Grid = grid;
            SlotCount = slotCount;
            int2 far = StrataHeights.FarSplatSamples(grid);
            CpuSurface = new(grid.SplatSamplesPerTile * slotCount, Allocator.Persistent);
            CpuUnderground = new(grid.SplatSamplesPerTile * slotCount, Allocator.Persistent);
            CpuFarSurface = new(far.x * far.y, Allocator.Persistent);
            CpuFarUnderground = new(far.x * far.y, Allocator.Persistent);
            Fill();
        }

        public View GetView(NativeArray<int> tileSlots) => new()
        {
            Grid = Grid,
            TileSlots = tileSlots,
            Surface = CpuSurface,
            Underground = CpuUnderground,
            FarSurface = CpuFarSurface,
            FarUnderground = CpuFarUnderground
        };

        public static int Strongest(uint texel, out float weight)
        {
            weight = ((texel >> 16) & 0xff) / 255f;
            return (int)(texel & 0xff);
        }

        public static uint Pack(int b0, int b1, float w0)
        {
            uint first = (uint)math.round(math.saturate(w0) * 255f);
            return (uint)b0 | (uint)b1 << 8 | first << 16 | (255u - first) << 24;
        }

        // No biome anywhere.
        public void Fill()
        {
            Set(CpuSurface, NoneTexel);
            Set(CpuUnderground, NoneTexel);
            Set(CpuFarSurface, NoneTexel);
            Set(CpuFarUnderground, NoneTexel);
        }

        private static void Set(NativeArray<uint> values, uint value)
        {
            for (int i = 0; i < values.Length; i++) values[i] = value;
        }

        // Loads every tile's maps from a bake into the slot of the same number (resident worlds).
        public bool Load(StrataWorldData data)
        {
            if (data == null || !data.IsComplete || !data.Grid.Equals(Grid) || SlotCount != Grid.TileCount) return false;
            if (data.Biomes.Length == 0) return true;
            for (int tile = 0; tile < Grid.TileCount; tile++)
            {
                NativeArray<byte> file = data.ReadTile(tile, Allocator.Temp);
                SetTile(tile, file);
                file.Dispose();
            }
            return true;
        }

        public void LoadFar(StrataFarFile.Data far)
        {
            if (far.BiomeSurface.IsCreated && far.BiomeSurface.Length == CpuFarSurface.Length)
                NativeArray<uint>.Copy(far.BiomeSurface, CpuFarSurface, CpuFarSurface.Length);
            if (far.BiomeUnderground.IsCreated && far.BiomeUnderground.Length == CpuFarUnderground.Length)
                NativeArray<uint>.Copy(far.BiomeUnderground, CpuFarUnderground, CpuFarUnderground.Length);
        }

        // Puts a tile file's maps in a slot (the heightfield's slot for that tile); none where the file has none.
        public void SetTile(int slot, NativeArray<byte> file)
        {
            int perTile = Grid.SplatSamplesPerTile;
            NativeSlice<uint> surface = CpuSurface.Slice(slot * perTile, perTile), underground = CpuUnderground.Slice(slot * perTile, perTile);
            if (!StrataTileFile.ReadBiomes(file, Grid.Samples, Grid.SplatSamples, surface, underground))
                for (int i = 0; i < perTile; i++)
                {
                    surface[i] = NoneTexel;
                    underground[i] = NoneTexel;
                }
        }

        // Writes one slot's maps (the editor's evaluator).
        public void WriteTile(int slot, NativeArray<uint> surface, NativeArray<uint> underground)
        {
            int perTile = Grid.SplatSamplesPerTile;
            NativeArray<uint>.Copy(surface, 0, CpuSurface, slot * perTile, perTile);
            NativeArray<uint>.Copy(underground, 0, CpuUnderground, slot * perTile, perTile);
        }

        // A slot's maps as a tile file's biome section stores them: a format number, which maps follow (1 surface,
        // 2 underground; a map with no biome is left out), then each map's run count and (count, texel) runs.
        public byte[] Encode(int slot)
        {
            int perTile = Grid.SplatSamplesPerTile;
            NativeSlice<uint> surface = CpuSurface.Slice(slot * perTile, perTile), underground = CpuUnderground.Slice(slot * perTile, perTile);
            bool hasSurface = !Uniform(surface, NoneTexel), hasUnderground = !Uniform(underground, NoneTexel);
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(1);
            writer.Write((hasSurface ? 1 : 0) | (hasUnderground ? 2 : 0));
            if (hasSurface) WriteRuns(writer, surface);
            if (hasUnderground) WriteRuns(writer, underground);
            writer.Flush();
            return stream.ToArray();
        }

        private static bool Uniform(NativeSlice<uint> values, uint value)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] != value) return false;
            return true;
        }

        private static void WriteRuns(BinaryWriter writer, NativeSlice<uint> values)
        {
            List<(int Count, uint Value)> runs = new();
            for (int i = 0; i < values.Length;)
            {
                int start = i;
                uint value = values[i];
                while (i < values.Length && values[i] == value) i++;
                runs.Add((i - start, value));
            }
            writer.Write(runs.Count);
            foreach ((int count, uint value) in runs)
            {
                writer.Write(count);
                writer.Write(value);
            }
        }

        // Decodes a biome section (see Encode) into the two maps; false if it is malformed.
        public static unsafe bool Decode(byte* section, int length, NativeSlice<uint> surface, NativeSlice<uint> underground)
        {
            if (length < 8 || *(int*)section != 1) return false;
            int flags = *(int*)(section + 4);
            long offset = 8;
            return ReadRuns(section, length, ref offset, (flags & 1) != 0, surface) && ReadRuns(section, length, ref offset, (flags & 2) != 0, underground);
        }

        private static unsafe bool ReadRuns(byte* section, int length, ref long offset, bool present, NativeSlice<uint> values)
        {
            if (!present)
            {
                for (int i = 0; i < values.Length; i++) values[i] = NoneTexel;
                return true;
            }
            if (offset + 4 > length) return false;
            int runs = *(int*)(section + offset);
            offset += 4;
            if (runs < 0 || offset + runs * 8L > length) return false;
            int written = 0;
            for (int r = 0; r < runs; r++)
            {
                int count = *(int*)(section + offset);
                uint value = *(uint*)(section + offset + 4);
                offset += 8;
                if (count < 0 || written + count > values.Length) return false;
                for (int i = 0; i < count; i++) values[written + i] = value;
                written += count;
            }
            return written == values.Length;
        }

        public void Dispose()
        {
            if (CpuSurface.IsCreated) CpuSurface.Dispose();
            if (CpuUnderground.IsCreated) CpuUnderground.Dispose();
            if (CpuFarSurface.IsCreated) CpuFarSurface.Dispose();
            if (CpuFarUnderground.IsCreated) CpuFarUnderground.Dispose();
        }
    }
}
