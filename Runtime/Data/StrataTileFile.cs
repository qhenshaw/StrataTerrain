using System;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Strata
{
    // The baked tile format: a 16-byte header (magic "STRT", version, samples per edge, section flags), the tile's
    // heights as little-endian ushorts row by row, then (flag Splats) the splat samples per edge and two RGBA8 maps:
    // the four strongest layer indices and their weights per texel, then (flag StrataMap) a two-byte map at splat
    // resolution: rock strata strength and which of the world's strata definitions applies, then (flag Trees) the
    // tile's tree count and its StrataTreeRecords, then (flag Grass) the number of grass prototypes and a byte density
    // map at splat resolution for each. Version 1 files have heights only, version 2 no strata map, version 3 no trees,
    // version 4 no grass.
    public static class StrataTileFile
    {
        public const int HeaderSize = 16;
        public const int Splats = 1;
        public const int StrataMap = 2;
        public const int Trees = 4;
        public const int Grass = 8;
        private const int Magic = 0x54525453; // "STRT"
        private const int Version = 5;

        public static long Size(StrataGrid grid, long trees = 0, int grassTypes = 0) =>
            HeaderSize + grid.SamplesPerTile * 2L + 4 + grid.SplatSamplesPerTile * (10L + grassTypes) + 8 + trees * 10L;

        // Writes a tile file, unless the file already holds exactly these bytes (so a bake rewrites only the tiles that
        // changed). Returns whether it wrote.
        public static bool Write(string path, int samples, NativeSlice<ushort> heights, int splatSamples, NativeSlice<uint> indices,
                                 NativeSlice<uint> weights, NativeSlice<ushort> strata, NativeArray<StrataTreeRecord> trees,
                                 int grassTypes, NativeArray<byte> grass)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(samples);
            writer.Write(Splats | StrataMap | Trees | Grass);
            byte[] bytes = new byte[heights.Length * 2];
            for (int i = 0; i < heights.Length; i++)
            {
                bytes[i * 2] = (byte)heights[i];
                bytes[i * 2 + 1] = (byte)(heights[i] >> 8);
            }
            writer.Write(bytes);
            writer.Write(splatSamples);
            WriteUInts(writer, indices);
            WriteUInts(writer, weights);
            byte[] strataBytes = new byte[strata.Length * 2];
            for (int i = 0; i < strata.Length; i++)
            {
                strataBytes[i * 2] = (byte)strata[i];
                strataBytes[i * 2 + 1] = (byte)(strata[i] >> 8);
            }
            writer.Write(strataBytes);
            int treeCount = trees.IsCreated ? trees.Length : 0;
            writer.Write(treeCount);
            if (treeCount > 0) writer.Write(trees.Reinterpret<byte>(UnsafeUtility.SizeOf<StrataTreeRecord>()).ToArray());
            writer.Write(grassTypes);
            if (grassTypes > 0) writer.Write(grass.ToArray());
            writer.Flush();

            byte[] content = stream.ToArray();
            if (File.Exists(path) && new FileInfo(path).Length == content.Length &&
                ((ReadOnlySpan<byte>)File.ReadAllBytes(path)).SequenceEqual(content)) return false;
            File.WriteAllBytes(path, content);
            return true;
        }

        private static void WriteUInts(BinaryWriter writer, NativeSlice<uint> values)
        {
            byte[] bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++)
            {
                uint v = values[i];
                bytes[i * 4] = (byte)v;
                bytes[i * 4 + 1] = (byte)(v >> 8);
                bytes[i * 4 + 2] = (byte)(v >> 16);
                bytes[i * 4 + 3] = (byte)(v >> 24);
            }
            writer.Write(bytes);
        }

        // Copies a tile file's heights into the given slice, which must hold samples² values.
        public static unsafe void Read(NativeArray<byte> file, int samples, NativeSlice<ushort> heights)
        {
            int* header = Header(file, samples);
            int count = samples * samples;
            if (file.Length < HeaderSize + count * 2) throw new InvalidDataException("Strata tile file is truncated.");
            if (heights.Length != count) throw new ArgumentException("Height slice is the wrong size.");
            UnsafeUtility.MemCpyStride(heights.GetUnsafePtr(), heights.Stride, (byte*)header + HeaderSize, 2, 2, count);
        }

        // Copies a tile file's splats into the given slices (splatSamples² each); false if the file has none.
        public static unsafe bool ReadSplats(NativeArray<byte> file, int samples, int splatSamples, NativeSlice<uint> indices, NativeSlice<uint> weights)
        {
            int* header = Header(file, samples);
            if (header[1] < 2 || (header[3] & Splats) == 0) return false;
            byte* section = (byte*)header + HeaderSize + samples * samples * 2;
            if (*(int*)section != splatSamples) throw new InvalidDataException($"Strata tile file has {*(int*)section} splat samples per edge, expected {splatSamples}.");
            int count = splatSamples * splatSamples;
            if (file.Length < HeaderSize + samples * samples * 2 + 4 + count * 8) throw new InvalidDataException("Strata tile file splats are truncated.");
            UnsafeUtility.MemCpyStride(indices.GetUnsafePtr(), indices.Stride, section + 4, 4, 4, count);
            UnsafeUtility.MemCpyStride(weights.GetUnsafePtr(), weights.Stride, section + 4 + count * 4, 4, 4, count);
            return true;
        }

        // Copies a tile file's strata map (splatSamples² values: low byte strength, high byte definition); false if none.
        public static unsafe bool ReadStrata(NativeArray<byte> file, int samples, int splatSamples, NativeSlice<ushort> strata)
        {
            int* header = Header(file, samples);
            if (header[1] < 3 || (header[3] & StrataMap) == 0) return false;
            int count = splatSamples * splatSamples;
            long offset = HeaderSize + samples * samples * 2L + 4 + count * 8L;
            if (file.Length < offset + count * 2L) throw new InvalidDataException("Strata tile file strata map is truncated.");
            UnsafeUtility.MemCpyStride(strata.GetUnsafePtr(), strata.Stride, (byte*)header + offset, 2, 2, count);
            return true;
        }

        // A tile file's trees (empty if it has none), in a new array the caller disposes.
        public static unsafe NativeArray<StrataTreeRecord> ReadTrees(NativeArray<byte> file, int samples, int splatSamples, Allocator allocator)
        {
            int* header = Header(file, samples);
            if (header[1] < 4 || (header[3] & Trees) == 0) return new NativeArray<StrataTreeRecord>(0, allocator);
            int count = splatSamples * splatSamples;
            long offset = HeaderSize + samples * samples * 2L + 4 + count * 10L;
            if (file.Length < offset + 4) throw new InvalidDataException("Strata tile file trees are truncated.");
            int trees = *(int*)((byte*)header + offset);
            int size = UnsafeUtility.SizeOf<StrataTreeRecord>();
            if (trees < 0 || file.Length < offset + 4 + (long)trees * size) throw new InvalidDataException("Strata tile file trees are truncated.");
            NativeArray<StrataTreeRecord> records = new(trees, allocator, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemCpy(records.GetUnsafePtr(), (byte*)header + offset + 4, (long)trees * size);
            return records;
        }

        // A tile file's trees, reading only its header and trees section from disk.
        public static unsafe NativeArray<StrataTreeRecord> ReadTrees(string path, int samples, int splatSamples, Allocator allocator)
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            if (reader.ReadInt32() != Magic) throw new InvalidDataException("Not a Strata tile file.");
            int version = reader.ReadInt32();
            if (version > Version) throw new InvalidDataException($"Strata tile file version {version} is newer than this package.");
            if (reader.ReadInt32() != samples) throw new InvalidDataException($"Strata tile file doesn't have {samples} samples per edge.");
            int flags = reader.ReadInt32();
            if (version < 4 || (flags & Trees) == 0) return new NativeArray<StrataTreeRecord>(0, allocator);
            stream.Seek(HeaderSize + samples * samples * 2L + 4 + splatSamples * splatSamples * 10L, SeekOrigin.Begin);
            int count = reader.ReadInt32();
            int size = UnsafeUtility.SizeOf<StrataTreeRecord>();
            byte[] bytes = reader.ReadBytes(count * size);
            if (count < 0 || bytes.Length != count * size) throw new InvalidDataException("Strata tile file trees are truncated.");
            NativeArray<StrataTreeRecord> records = new(count, allocator, NativeArrayOptions.UninitializedMemory);
            fixed (byte* source = bytes) UnsafeUtility.MemCpy(records.GetUnsafePtr(), source, bytes.Length);
            return records;
        }

        // A tile file's grass densities (types maps of splatSamples² bytes, one after another), in a new array the caller
        // disposes; empty with no types if it has none.
        public static unsafe NativeArray<byte> ReadGrass(NativeArray<byte> file, int samples, int splatSamples, Allocator allocator, out int types)
        {
            types = 0;
            int* header = Header(file, samples);
            if (header[1] < 5 || (header[3] & Grass) == 0) return new NativeArray<byte>(0, allocator);
            int count = splatSamples * splatSamples;
            long offset = HeaderSize + samples * samples * 2L + 4 + count * 10L;
            int trees = *(int*)((byte*)header + offset);
            offset += 4 + (long)trees * UnsafeUtility.SizeOf<StrataTreeRecord>();
            if (file.Length < offset + 4) throw new InvalidDataException("Strata tile file grass is truncated.");
            types = *(int*)((byte*)header + offset);
            if (types < 0 || file.Length < offset + 4 + (long)types * count) throw new InvalidDataException("Strata tile file grass is truncated.");
            NativeArray<byte> grass = new(types * count, allocator, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemCpy(grass.GetUnsafePtr(), (byte*)header + offset + 4, (long)types * count);
            return grass;
        }

        private static unsafe int* Header(NativeArray<byte> file, int samples)
        {
            if (file.Length < HeaderSize) throw new InvalidDataException("Strata tile file is too short.");
            int* header = (int*)file.GetUnsafeReadOnlyPtr();
            if (header[0] != Magic) throw new InvalidDataException("Not a Strata tile file.");
            if (header[1] > Version) throw new InvalidDataException($"Strata tile file version {header[1]} is newer than this package.");
            if (header[2] != samples) throw new InvalidDataException($"Strata tile file has {header[2]} samples per edge, expected {samples}.");
            return header;
        }
    }
}
