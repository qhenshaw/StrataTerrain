using System;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Strata
{
    // The baked far file: what a streaming world keeps resident for the whole world. A header (magic "STRF", version,
    // far height samples x and z, far splat samples x and z, node count), then the far heights (every
    // StrataHeights.FarStep-th world sample, ushorts), the far splats (every FarSplatStep-th splat texel: four layer
    // indices, then four weights, RGBA8 each), and every CDLOD node's min and max height (float2s, as StrataGrid numbers
    // them).
    public static class StrataFarFile
    {
        public const string Name = "far.bytes";
        private const int Magic = 0x46525453; // "STRF"
        private const int Version = 1;

        public struct Data : IDisposable
        {
            public NativeArray<ushort> Heights;
            public NativeArray<uint> SplatIndices;
            public NativeArray<uint> SplatWeights;
            public NativeArray<float2> NodeMinMax;

            public void Dispose()
            {
                if (Heights.IsCreated) Heights.Dispose();
                if (SplatIndices.IsCreated) SplatIndices.Dispose();
                if (SplatWeights.IsCreated) SplatWeights.Dispose();
                if (NodeMinMax.IsCreated) NodeMinMax.Dispose();
            }
        }

        public static void Write(string path, StrataGrid grid, Data data)
        {
            int2 far = StrataHeights.FarSamples(grid), farSplats = StrataHeights.FarSplatSamples(grid);
            using FileStream stream = File.Create(path);
            using BinaryWriter writer = new(stream);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(far.x);
            writer.Write(far.y);
            writer.Write(farSplats.x);
            writer.Write(farSplats.y);
            writer.Write(data.NodeMinMax.Length);
            writer.Write(data.Heights.Reinterpret<byte>(2).ToArray());
            writer.Write(data.SplatIndices.Reinterpret<byte>(4).ToArray());
            writer.Write(data.SplatWeights.Reinterpret<byte>(4).ToArray());
            writer.Write(data.NodeMinMax.Reinterpret<byte>(8).ToArray());
        }

        public static unsafe Data Read(string path, StrataGrid grid, Allocator allocator)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int2 far = StrataHeights.FarSamples(grid), farSplats = StrataHeights.FarSplatSamples(grid);
            fixed (byte* start = bytes)
            {
                int* header = (int*)start;
                if (bytes.Length < 28 || header[0] != Magic) throw new InvalidDataException("Not a Strata far file.");
                if (header[1] > Version) throw new InvalidDataException($"Strata far file version {header[1]} is newer than this package.");
                if (header[2] != far.x || header[3] != far.y || header[4] != farSplats.x || header[5] != farSplats.y || header[6] != grid.NodeCount)
                    throw new InvalidDataException("Strata far file doesn't match the world's grid; bake again.");
                Data data = new()
                {
                    Heights = new NativeArray<ushort>(far.x * far.y, allocator, NativeArrayOptions.UninitializedMemory),
                    SplatIndices = new NativeArray<uint>(farSplats.x * farSplats.y, allocator, NativeArrayOptions.UninitializedMemory),
                    SplatWeights = new NativeArray<uint>(farSplats.x * farSplats.y, allocator, NativeArrayOptions.UninitializedMemory),
                    NodeMinMax = new NativeArray<float2>(grid.NodeCount, allocator, NativeArrayOptions.UninitializedMemory)
                };
                long offset = 28;
                Copy(start, bytes.Length, ref offset, data.Heights.GetUnsafePtr(), data.Heights.Length * 2L);
                Copy(start, bytes.Length, ref offset, data.SplatIndices.GetUnsafePtr(), data.SplatIndices.Length * 4L);
                Copy(start, bytes.Length, ref offset, data.SplatWeights.GetUnsafePtr(), data.SplatWeights.Length * 4L);
                Copy(start, bytes.Length, ref offset, data.NodeMinMax.GetUnsafePtr(), data.NodeMinMax.Length * 8L);
                return data;
            }
        }

        private static unsafe void Copy(byte* source, long length, ref long offset, void* destination, long size)
        {
            if (offset + size > length) throw new InvalidDataException("Strata far file is truncated.");
            UnsafeUtility.MemCpy(destination, source + offset, size);
            offset += size;
        }
    }
}
