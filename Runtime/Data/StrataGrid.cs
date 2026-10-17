using System;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // The layout of a world's height samples: a grid of square tiles, each Resolution quads across, whose edge samples
    // are shared with the neighbouring tile. Heights are stored as 16-bit fractions of [MinHeight, MaxHeight].
    // Everything that converts between world positions, world samples, tiles and CDLOD nodes goes through here, so the
    // evaluator, renderer, collision and bake all agree. It is a plain struct so Burst jobs can take it.
    [Serializable]
    public struct StrataGrid : IEquatable<StrataGrid>
    {
        // CDLOD leaf patches are this many quads across, the size of the shared patch mesh.
        public const int PatchQuads = 32;

        public float2 Origin;
        public int TilesX;
        public int TilesZ;
        public float TileSize;
        public int Resolution;
        public float MinHeight;
        public float MaxHeight;
        // Height samples per splat texel along an edge: 1, 2 or 4.
        public int SplatDivisor;

        public int Samples => Resolution + 1;
        public int SplatResolution => Resolution / SplatDivisor;
        public int SplatSamples => SplatResolution + 1;
        public int SplatSamplesPerTile => SplatSamples * SplatSamples;
        public float SplatSpacing => TileSize / SplatResolution;
        public int SamplesPerTile => Samples * Samples;
        public int TileCount => TilesX * TilesZ;
        public float Spacing => TileSize / Resolution;
        public float HeightRange => MaxHeight - MinHeight;
        public int WorldSamplesX => TilesX * Resolution + 1;
        public int WorldSamplesZ => TilesZ * Resolution + 1;
        public float2 Size => new(TilesX * TileSize, TilesZ * TileSize);

        // CDLOD: level 0 is the leaf (one quad per sample), the top level is a whole tile.
        public int LeavesPerSide => Resolution / PatchQuads;
        public int Levels => math.tzcnt(LeavesPerSide) + 1;
        public int NodesPerTile => ((1 << (2 * Levels)) - 1) / 3;
        public int NodeCount => NodesPerTile * TileCount;

        public bool IsValid => TilesX > 0 && TilesZ > 0 && TileSize > 0f && Resolution >= PatchQuads &&
                               math.ispow2(Resolution) && MaxHeight > MinHeight &&
                               (SplatDivisor == 1 || SplatDivisor == 2 || SplatDivisor == 4);

        public int TileIndex(int tileX, int tileZ) => tileZ * TilesX + tileX;
        public float2 TileOrigin(int tileX, int tileZ) => Origin + new float2(tileX, tileZ) * TileSize;

        // Nodes are stored per tile, leaf level first, each level row by row.
        public int LevelOffset(int level) => (((1 << (2 * Levels)) - 1) - ((1 << (2 * (Levels - level))) - 1)) / 3;
        public int NodesPerSide(int level) => LeavesPerSide >> level;
        public float NodeSize(int level) => Spacing * (PatchQuads << level);
        public int NodeIndex(int tile, int level, int x, int z) => tile * NodesPerTile + LevelOffset(level) + z * NodesPerSide(level) + x;

        public float ToHeight(ushort value) => MinHeight + value * (HeightRange / 65535f);
        public ushort ToStored(float height) => (ushort)math.clamp(math.round((height - MinHeight) / HeightRange * 65535f), 0f, 65535f);

        // The world sample nearest a position; may be outside the world.
        public int2 WorldSample(float2 position) => (int2)math.round((position - Origin) / Spacing);
        public float2 SamplePosition(int2 sample) => Origin + (float2)sample * Spacing;

        // Which tile holds a world sample, and where in it. Samples on a shared edge go to the tile further from the
        // origin, except on the world's far edges.
        public int2 SampleTile(int2 sample) => math.clamp(sample / Resolution, 0, new int2(TilesX - 1, TilesZ - 1));

        public int SampleIndex(int2 sample)
        {
            sample = math.clamp(sample, 0, new int2(WorldSamplesX - 1, WorldSamplesZ - 1));
            int2 tile = SampleTile(sample);
            int2 local = sample - tile * Resolution;
            return TileIndex(tile.x, tile.y) * SamplesPerTile + local.y * Samples + local.x;
        }

        public bool Equals(StrataGrid other) => Origin.Equals(other.Origin) && TilesX == other.TilesX && TilesZ == other.TilesZ &&
                                               TileSize == other.TileSize && Resolution == other.Resolution &&
                                               MinHeight == other.MinHeight && MaxHeight == other.MaxHeight &&
                                               SplatDivisor == other.SplatDivisor;
        public override bool Equals(object obj) => obj is StrataGrid other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Origin, TilesX, TilesZ, TileSize, Resolution, MinHeight, MaxHeight, SplatDivisor);
        public override string ToString() => $"{TilesX}x{TilesZ} tiles of {TileSize} m at {Spacing} m (splats {SplatSpacing} m), heights {MinHeight}..{MaxHeight} m";
    }
}
