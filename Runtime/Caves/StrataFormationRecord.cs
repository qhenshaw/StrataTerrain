using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // One placed formation as a tile file stores it, in 16 bytes: its base position in the tile (x and z over the tile's
    // size, y over the world's height range, as 16-bit fractions), its prototype, its rotation (the largest component of
    // the quaternion dropped, the other three in 10 bits each), and its scale (a 16-bit half).
    public struct StrataFormationRecord
    {
        public ushort X, Y, Z;
        public ushort Prototype;
        public uint Rotation;
        public ushort Scale;
        public ushort Pad;

        public static StrataFormationRecord Pack(StrataGrid grid, int2 tile, float3 position, quaternion rotation, float scale, int prototype)
        {
            float2 local = (position.xz - grid.TileOrigin(tile.x, tile.y)) / grid.TileSize;
            float height = (position.y - grid.MinHeight) / grid.HeightRange;
            return new StrataFormationRecord
            {
                X = (ushort)math.round(math.saturate(local.x) * 65535f),
                Y = (ushort)math.round(math.saturate(height) * 65535f),
                Z = (ushort)math.round(math.saturate(local.y) * 65535f),
                Prototype = (ushort)prototype,
                Rotation = PackRotation(rotation),
                Scale = (ushort)math.f32tof16(scale)
            };
        }

        public float3 Position(StrataGrid grid, int2 tile)
        {
            float2 origin = grid.TileOrigin(tile.x, tile.y);
            return new float3(origin.x + X / 65535f * grid.TileSize, grid.MinHeight + Y / 65535f * grid.HeightRange, origin.y + Z / 65535f * grid.TileSize);
        }

        public quaternion RotationValue => UnpackRotation(Rotation);
        public float ScaleValue => math.f16tof32(Scale);

        public Matrix4x4 Matrix(StrataGrid grid, int2 tile) => Matrix4x4.TRS(Position(grid, tile), RotationValue, Vector3.one * ScaleValue);

        // smallest three: the index of the largest component, then the other three in 10 bits each over -1/√2..1/√2
        private static uint PackRotation(quaternion q)
        {
            float4 v = math.normalize(q.value);
            float4 a = math.abs(v);
            int largest = 0;
            for (int i = 1; i < 4; i++)
                if (a[i] > a[largest]) largest = i;
            if (v[largest] < 0f) v = -v;
            uint packed = (uint)largest << 30;
            int shift = 20;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                uint bits = (uint)math.round((math.clamp(v[i] * math.SQRT2, -1f, 1f) * 0.5f + 0.5f) * 1023f);
                packed |= bits << shift;
                shift -= 10;
            }
            return packed;
        }

        private static quaternion UnpackRotation(uint packed)
        {
            int largest = (int)(packed >> 30);
            float4 v = 0f;
            float sum = 0f;
            int shift = 20;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                float c = (((packed >> shift) & 1023u) / 1023f * 2f - 1f) / math.SQRT2;
                v[i] = c;
                sum += c * c;
                shift -= 10;
            }
            v[largest] = math.sqrt(math.max(0f, 1f - sum));
            return new quaternion(math.normalize(v));
        }
    }
}
