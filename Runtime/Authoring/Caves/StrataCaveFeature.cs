using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // Base for features that change the world in 3D: they carve out (tunnels, caverns) or add rock (arches, overhangs)
    // in a signed distance field that starts as the heightfield's ground, applied in hierarchy order with smooth unions so
    // they fillet into the ground and each other. The bake meshes the field near these features into cave meshes, and
    // cuts holes in the heightfield where a carve opens it, so the terrain shows the cave beneath. Walls and floors take
    // the chosen palette layers (blending into the ground's own layers near the surface), and the feature's paint and
    // tree effect act on the ground around entrances and under rock (by default clearing trees and grass there).
    public abstract class StrataCaveFeature : StrataFeature
    {
        // how far the paint and tree effect reach beyond the shape at the ground, in metres (times their width scale)
        public const float InfluenceReach = 4f;

        [SerializeField] protected StrataCaveOperation _operation = StrataCaveOperation.Carve;
        [Tooltip("Fillet radius where the shape meets the ground and the features above it, in metres.")]
        [SerializeField, Min(0f)] protected float _smoothness = 2f;
        [SerializeField] protected StrataCaveNoise _noise = StrataCaveNoise.Default;
        [Tooltip("Palette layer for walls and ceilings; none shows the ground's layers.")]
        [SerializeField] protected StrataLayer _wallLayer;
        [Tooltip("Palette layer for floors; none uses the wall layer.")]
        [SerializeField] protected StrataLayer _floorLayer;
        [Tooltip("Surfaces facing up at less than this slope are floor, in degrees.")]
        [SerializeField, Range(0f, 90f)] protected float _floorSlope = 35f;
        [SerializeField] protected StrataPaint _paint = StrataPaint.Default;
        [SerializeField] protected StrataTreeEffect _trees = DefaultTrees;

        private readonly List<StrataCavePrimitive> _primitives = new();
        private Bounds _shapeBounds;
        private int _builtVersion = -1;
        private Matrix4x4 _builtMatrix;

        private static StrataTreeEffect DefaultTrees
        {
            get
            {
                StrataTreeEffect effect = StrataTreeEffect.Default;
                effect.Mode = StrataTreeMode.Density;
                return effect;
            }
        }

        public StrataCaveOperation Operation => _operation;
        public float Smoothness => _smoothness;
        public StrataCaveNoise Noise => _noise;
        public StrataLayer WallLayer => _wallLayer;
        public StrataLayer FloorLayer => _floorLayer != null ? _floorLayer : _wallLayer;
        public float FloorSlope => _floorSlope;
        public override StrataPaint Paint => _paint;
        public override StrataTreeEffect Trees => _trees;
        // caves don't change the heightfield, only where it shows
        public override bool AffectsHeights => false;

        // The world box the feature can change (its primitives, grown by noise and smoothing).
        public Bounds Bounds3D
        {
            get
            {
                Build();
                Bounds bounds = _shapeBounds;
                bounds.Expand((_smoothness + _noise.Amplitude) * 2f);
                return bounds;
            }
        }

        // The ground it can change: its box, plus the reach of its paint and tree effect.
        public override Rect Bounds
        {
            get
            {
                Bounds bounds = Bounds3D;
                float reach = InfluenceReach * math.max(1f, math.max(_paint.WidthScale, _trees.WidthScale));
                return new Rect(bounds.min.x - reach, bounds.min.z - reach, bounds.size.x + reach * 2f, bounds.size.z + reach * 2f);
            }
        }

        // The feature's primitives in world space, rebuilt if its settings or transform changed.
        public IReadOnlyList<StrataCavePrimitive> Primitives
        {
            get
            {
                Build();
                return _primitives;
            }
        }

        private void Build()
        {
            if (_builtVersion == Version && _builtMatrix == transform.localToWorldMatrix) return;
            _builtVersion = Version;
            _builtMatrix = transform.localToWorldMatrix;
            _primitives.Clear();
            BuildPrimitives(_primitives);
            if (_primitives.Count == 0)
            {
                _shapeBounds = new Bounds(transform.position, Vector3.zero);
                return;
            }
            float3 min = _primitives[0].Min, max = _primitives[0].Max;
            foreach (StrataCavePrimitive primitive in _primitives)
            {
                min = math.min(min, primitive.Min);
                max = math.max(max, primitive.Max);
            }
            _shapeBounds = new Bounds((min + max) * 0.5f, max - min);
        }

        // Adds the feature's primitives, in world space.
        protected abstract void BuildPrimitives(List<StrataCavePrimitive> primitives);

        protected Color GizmoColour => _operation == StrataCaveOperation.Carve ? new Color(1f, 0.55f, 0.1f) : new Color(0.55f, 0.75f, 1f);
    }
}
