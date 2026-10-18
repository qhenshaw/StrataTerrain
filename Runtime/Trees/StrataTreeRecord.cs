using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Strata
{
    // One placed tree as baked into a tile file (10 bytes): its position within the tile in 1/65535ths of the tile,
    // its prototype (an index into the world's tree prototypes), and a packed word: yaw (8 bits, 1/256 turn), scale
    // (8 bits, 1/64ths), lean about x and z (4 bits each, signed, LeanStep degrees), and how far it sinks into the
    // ground (8 bits, 1/32 m). The low 24 bits go to the GPU as they are; the height comes from the terrain at load.
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct StrataTreeRecord
    {
        public const float LeanStep = 2.5f;
        public const float ScaleStep = 1f / 64f;
        public const float SinkStep = 1f / 32f;

        public ushort X;
        public ushort Z;
        public ushort Prototype;
        public uint Packed;

        public float Scale => ((Packed >> 8) & 0xff) * ScaleStep;
        public float Sink => (Packed >> 24) * SinkStep;
        public float Yaw => (Packed & 0xff) * (360f / 256f);
        public float2 Lean => new float2((int)(Packed << 12) >> 28, (int)(Packed << 8) >> 28) * LeanStep;

        // How the instance is turned: yaw, then the lean about z and x (as StrataTreeCommon.hlsl builds it).
        public UnityEngine.Quaternion Rotation
        {
            get
            {
                float2 lean = Lean;
                return UnityEngine.Quaternion.Euler(lean.x, 0f, 0f) * UnityEngine.Quaternion.Euler(0f, 0f, lean.y) * UnityEngine.Quaternion.Euler(0f, Yaw, 0f);
            }
        }
        public uint GpuBits => Packed & 0xffffffu;

        public float2 Position(StrataGrid grid, int tile) =>
            grid.TileOrigin(tile % grid.TilesX, tile / grid.TilesX) + new float2(X, Z) * (grid.TileSize / 65535f);

        public static StrataTreeRecord Create(StrataGrid grid, int tile, float2 position, int prototype, float yaw, float scale, float2 lean, float sink)
        {
            float2 local = (position - grid.TileOrigin(tile % grid.TilesX, tile / grid.TilesX)) / grid.TileSize;
            uint yawBits = (uint)((int)math.round(yaw / 360f * 256f) & 0xff);
            uint scaleBits = (uint)math.clamp(math.round(scale / ScaleStep), 1f, 255f);
            int2 leanSteps = (int2)math.clamp(math.round(lean / LeanStep), -8f, 7f);
            uint sinkBits = (uint)math.clamp(math.round(sink / SinkStep), 0f, 255f);
            return new StrataTreeRecord
            {
                X = (ushort)math.clamp(math.round(local.x * 65535f), 0f, 65535f),
                Z = (ushort)math.clamp(math.round(local.y * 65535f), 0f, 65535f),
                Prototype = (ushort)prototype,
                Packed = yawBits | scaleBits << 8 | (uint)(leanSteps.x & 0xf) << 16 | (uint)(leanSteps.y & 0xf) << 20 | sinkBits << 24
            };
        }
    }
}
