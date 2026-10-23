using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Strata
{
    // The baked cave meshes of one tile, as its tile file stores them. Caves are meshed in bricks of 32 cells on a lattice
    // anchored at the world's origin and minimum height (cells VoxelSize across); a brick belongs to the tile holding its
    // minimum corner. The data is a header (magic "STRC", voxel size, brick count), a table of bricks (lattice
    // coordinate, vertex and index counts), then each brick's vertices and its indices (16-bit, padded to four bytes).
    // A vertex is 16 bytes: its position in the brick's frame (a box four cells wider than the brick on every side) as
    // 16-bit fractions, its normal as signed bytes, and four bytes of surface: the wall and floor layer (255 for the
    // ground's own), how much it is floor, and how much it blends into the ground's layers near the surface.
    public static class StrataCaveData
    {
        public const int BrickCells = 32;
        public const int FramePadding = 4;
        public const int FrameCells = BrickCells + FramePadding * 2;
        public const byte NoLayer = 255;
        private const int Magic = 0x43525453; // "STRC"
        private const int HeaderSize = 12;
        private const int BrickSize = 20;

        public struct Vertex
        {
            public ushort X, Y, Z, W;
            public uint Normal;
            public uint Surface;
        }

        public struct Brick
        {
            public int3 Coord;
            public int VertexCount;
            public int IndexCount;
            // where the brick's vertices and indices start in the data
            public int VertexOffset;
            public int IndexOffset;
        }

        // The lattice's origin: the world's origin on x and z, its minimum height on y.
        public static float3 LatticeOrigin(StrataGrid grid) => new(grid.Origin.x, grid.MinHeight, grid.Origin.y);

        // A brick's minimum corner in world space.
        public static float3 BrickOrigin(StrataGrid grid, float voxel, int3 coord) => LatticeOrigin(grid) + (float3)(coord * BrickCells) * voxel;

        // The tile a brick belongs to, or -1 outside the world.
        public static int BrickTile(StrataGrid grid, float voxel, int3 coord)
        {
            float2 corner = (float2)(coord.xz * BrickCells) * voxel;
            int2 tile = (int2)math.floor(corner / grid.TileSize + 1e-4f);
            if (math.any(tile < 0) || tile.x >= grid.TilesX || tile.y >= grid.TilesZ) return -1;
            return grid.TileIndex(tile.x, tile.y);
        }

        public static Vertex Pack(float3 local, float3 normal, uint surface, float voxel)
        {
            float3 frame = math.saturate((local / voxel + FramePadding) / FrameCells) * 65535f;
            int3 n = (int3)math.round(math.clamp(normal, -1f, 1f) * 127f);
            return new Vertex
            {
                X = (ushort)math.round(frame.x),
                Y = (ushort)math.round(frame.y),
                Z = (ushort)math.round(frame.z),
                Normal = (uint)(n.x & 0xff) | (uint)(n.y & 0xff) << 8 | (uint)(n.z & 0xff) << 16,
                Surface = surface
            };
        }

        // A vertex's position relative to its brick's minimum corner.
        public static float3 Unpack(Vertex vertex, float voxel) =>
            (new float3(vertex.X, vertex.Y, vertex.Z) / 65535f * FrameCells - FramePadding) * voxel;

        public static uint PackSurface(int wall, int floor, float floorWeight, float blend) =>
            (uint)(wall < 0 ? NoLayer : math.min(wall, 254)) | (uint)(floor < 0 ? NoLayer : math.min(floor, 254)) << 8 |
            (uint)math.round(math.saturate(floorWeight) * 255f) << 16 | (uint)math.round(math.saturate(blend) * 255f) << 24;

        public static byte[] Encode(float voxel, IReadOnlyList<(int3 Coord, NativeArray<Vertex> Vertices, NativeArray<ushort> Indices)> bricks)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(Magic);
            writer.Write(voxel);
            writer.Write(bricks.Count);
            foreach ((int3 coord, NativeArray<Vertex> vertices, NativeArray<ushort> indices) in bricks)
            {
                writer.Write(coord.x);
                writer.Write(coord.y);
                writer.Write(coord.z);
                writer.Write(vertices.Length);
                writer.Write(indices.Length);
            }
            foreach ((int3 _, NativeArray<Vertex> vertices, NativeArray<ushort> indices) in bricks)
            {
                writer.Write(vertices.Reinterpret<byte>(UnsafeUtility.SizeOf<Vertex>()).ToArray());
                writer.Write(indices.Reinterpret<byte>(2).ToArray());
                if ((indices.Length & 1) != 0) writer.Write((ushort)0);
            }
            writer.Flush();
            return stream.ToArray();
        }

        // The voxel size and brick table of encoded cave data; false if it is empty.
        public static unsafe bool Decode(NativeArray<byte> data, out float voxel, List<Brick> bricks)
        {
            voxel = 0f;
            bricks.Clear();
            if (data.Length == 0) return false;
            if (data.Length < HeaderSize) throw new InvalidDataException("Strata cave data is too short.");
            byte* bytes = (byte*)data.GetUnsafeReadOnlyPtr();
            if (*(int*)bytes != Magic) throw new InvalidDataException("Not Strata cave data.");
            voxel = *(float*)(bytes + 4);
            int count = *(int*)(bytes + 8);
            int offset = HeaderSize + count * BrickSize;
            if (count < 0 || data.Length < offset) throw new InvalidDataException("Strata cave data is truncated.");
            for (int i = 0; i < count; i++)
            {
                int* entry = (int*)(bytes + HeaderSize + i * BrickSize);
                Brick brick = new() { Coord = new int3(entry[0], entry[1], entry[2]), VertexCount = entry[3], IndexCount = entry[4], VertexOffset = offset };
                brick.IndexOffset = offset + brick.VertexCount * UnsafeUtility.SizeOf<Vertex>();
                offset = brick.IndexOffset + (brick.IndexCount + 1) / 2 * 4;
                if (brick.VertexCount < 0 || brick.IndexCount < 0 || data.Length < offset) throw new InvalidDataException("Strata cave data is truncated.");
                bricks.Add(brick);
            }
            return count > 0;
        }
    }
}
