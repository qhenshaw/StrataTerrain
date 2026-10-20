using System;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    public enum StrataSplatSpacing
    {
        Full = 1,
        Half = 2,
        Quarter = 4
    }

    // The authored shape of a Strata world, as a block on its StrataWorld: the tile grid and height range, how far each
    // CDLOD level reaches, and how collision streams. A bake copies the grid into the world's baked data so the baked
    // tiles keep working even if these settings are edited later.
    [Serializable]
    public class StrataWorldSettings
    {
        [Header("Grid")]
        [SerializeField] private Vector2 _origin;
        [SerializeField, Min(1)] private int _tilesX = 4;
        [SerializeField, Min(1)] private int _tilesZ = 4;
        [SerializeField, Min(1f)] private float _tileSize = 512f;
        [Tooltip("Quads across a tile; a power of two, at least 32. Samples per tile edge are this plus one.")]
        [SerializeField] private int _tileResolution = 512;
        [SerializeField] private float _minHeight = -100f;
        [SerializeField] private float _maxHeight = 900f;

        [Header("Texturing")]
        [Tooltip("Height samples per splat texel: 1 gives splats at the height spacing, 2 at twice it, 4 at four times.")]
        [SerializeField] private StrataSplatSpacing _splatSpacing = StrataSplatSpacing.Half;
        [Tooltip("Terrain below this height counts as water for the moisture rule input.")]
        [SerializeField] private bool _useSeaLevel = true;
        [SerializeField] private float _seaLevel;
        [Tooltip("Spacing of the world-wide moisture map (flow and distance to water), in metres.")]
        [SerializeField, Min(1f)] private float _moistureSpacing = 8f;
        [Tooltip("Upstream area, in square metres, at which flow counts as fully moist.")]
        [SerializeField, Min(1f)] private float _flowForFullMoisture = 40000f;
        [Tooltip("Distance from water, in metres, over which moisture fades out.")]
        [SerializeField, Min(0f)] private float _waterReach = 40f;

        [Header("Rendering")]
        // below about 5, a patch can still be morphing where it meets a finer neighbour, which opens cracks
        [Tooltip("How far the finest LOD reaches, in leaf patch widths. Each coarser level reaches twice as far.")]
        [SerializeField, Range(5f, 16f)] private float _lodDistanceRatio = 6f;

        [Header("Trees")]
        [Tooltip("Multiplies every tree prototype's LOD and impostor distances.")]
        [SerializeField, Range(0.25f, 4f)] private float _treeDistanceScale = 1f;
        [Tooltip("Changes every forest's placement.")]
        [SerializeField] private int _treeSeed = 1;

        [Header("Streaming")]
        [Tooltip("In play mode, keep only the tiles near cameras and foci at full resolution (the rest come from the far copy).")]
        [SerializeField] private bool _streaming = true;
        [Tooltip("Tiles within this distance of a camera or focus are loaded, in metres.")]
        [SerializeField, Min(64f)] private float _streamingRadius = 1536f;
        [Tooltip("How many tiles can be resident at once (GPU slots).")]
        [SerializeField, Min(4)] private int _residentTiles = 64;
        [SerializeField, Range(1, 16)] private int _maxConcurrentLoads = 4;

        [Header("Collision")]
        [Tooltip("Collision chunk width in metres; a multiple of the sample spacing.")]
        [SerializeField, Min(8f)] private float _collisionChunkSize = 64f;
        [Tooltip("Chunks within this distance of a focus transform have collision.")]
        [SerializeField, Min(0f)] private float _collisionRadius = 192f;
        [SerializeField] private int _collisionLayer;

        [Header("Probes")]
        [Tooltip("The closest baked light probes get (near the terrain and trees), in metres; the farthest is 27 times this. " +
                 "Closer is costlier: per square kilometre, 9 m baked about 6.7 MB of cell data and 27 m about 1 MB.")]
        [SerializeField, Min(1f)] private float _probeMinSpacing = 9f;

        public StrataGrid Grid => new()
        {
            Origin = _origin,
            TilesX = _tilesX,
            TilesZ = _tilesZ,
            TileSize = _tileSize,
            Resolution = _tileResolution,
            MinHeight = _minHeight,
            MaxHeight = _maxHeight,
            SplatDivisor = (int)_splatSpacing
        };

        public bool UseSeaLevel => _useSeaLevel;
        public float SeaLevel => _seaLevel;
        public float MoistureSpacing => _moistureSpacing;
        public float FlowForFullMoisture => _flowForFullMoisture;
        public float WaterReach => _waterReach;
        public float LodDistanceRatio => _lodDistanceRatio;
        public float TreeDistanceScale => _treeDistanceScale;
        public int TreeSeed => _treeSeed;
        public bool Streaming => _streaming;
        public float StreamingRadius => _streamingRadius;
        public int ResidentTiles => _residentTiles;
        public int MaxConcurrentLoads => _maxConcurrentLoads;
        public float CollisionChunkSize => _collisionChunkSize;
        public float CollisionRadius => _collisionRadius;
        public int CollisionLayer => _collisionLayer;
        public float ProbeMinSpacing => _probeMinSpacing;

        // Clamps the settings to what the world can use; called by the StrataWorld when it is edited.
        public void Validate()
        {
            _tileResolution = math.max(StrataGrid.PatchQuads, math.ceilpow2(_tileResolution));
            _maxHeight = math.max(_maxHeight, _minHeight + 1f);
            float spacing = _tileSize / _tileResolution;
            _collisionChunkSize = math.max(spacing, math.round(_collisionChunkSize / spacing) * spacing);
            _collisionLayer = math.clamp(_collisionLayer, 0, 31);
        }
    }
}
