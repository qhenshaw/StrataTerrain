using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;
using UnityEngine.Splines.Interpolators;

namespace Strata
{
    // A tunnel along the splines of a SplineContainer on the same object: a tube with an elliptical cross section (Aspect
    // is its height over its width) and an optionally flattened floor, whose radius and floor can be scaled per point along
    // the spline. Carve makes a tunnel, opening the ground where the spline comes out of it; Add makes a rock ridge, which
    // bent over the ground is an arch. Mouth Carve also lowers the heightfield towards the tunnel's floor over the first
    // and last Mouth Length of each spline (fading out inwards), so its ends are ramps into the ground rather than
    // sheer holes; place the spline's ends where the tunnel comes out.
    [AddComponentMenu("Strata/Cave Tunnel"), RequireComponent(typeof(SplineContainer))]
    public class CaveTunnel : StrataCaveFeature
    {
        [Tooltip("Half the tunnel's width, in metres.")]
        [SerializeField, Min(0.5f)] private float _radius = 4f;
        [Tooltip("Height over width of the cross section.")]
        [SerializeField, Range(0.3f, 2f)] private float _aspect = 0.8f;
        [Tooltip("How far the floor is filled in: 0 round, 1 up to the centre line.")]
        [SerializeField, Range(0f, 1f)] private float _flatFloor = 0.3f;
        [SerializeField] private SplineData<float> _knotRadius = new() { DefaultValue = 1f };
        [SerializeField] private SplineData<float> _knotFloor = new() { DefaultValue = 1f };
        [Tooltip("Distance between samples along the spline, in metres.")]
        [SerializeField, Min(0.25f)] private float _sampleSpacing = 2f;
        [Tooltip("Lower the terrain towards the tunnel's floor at the spline's ends, so they open as ramps.")]
        [SerializeField] private bool _mouthCarve;
        [Tooltip("How far in from each end the mouth carve reaches, in metres.")]
        [SerializeField, Min(1f)] private float _mouthLength = 16f;

        private readonly List<SplineFeature.Point> _mouths = new();
        private SplineContainer _container;

        private SplineContainer Container => _container != null ? _container : _container = GetComponent<SplineContainer>();

        public bool MouthCarve => _mouthCarve && _operation == StrataCaveOperation.Carve;
        // the mouth carve shapes the heightfield, lowering it only
        public override bool AffectsHeights => MouthCarve;
        public override StrataBlend Blend => StrataBlend.Min;

        // The mouth carve as spline points (at the tunnel floor's height), a run per spline end.
        public IReadOnlyList<SplineFeature.Point> MouthPoints
        {
            get
            {
                _ = Primitives;
                return _mouths;
            }
        }

        private void OnEnable() => Spline.Changed += OnSplineChanged;
        private void OnDisable() => Spline.Changed -= OnSplineChanged;

        private void OnSplineChanged(Spline spline, int knot, SplineModification modification)
        {
            if (Container == null) return;
            foreach (Spline own in Container.Splines)
                if (own == spline) MarkChanged();
        }

        protected override void BuildPrimitives(List<StrataCavePrimitive> primitives)
        {
            _mouths.Clear();
            SplineContainer container = Container;
            if (container == null) return;
            for (int s = 0; s < container.Splines.Count; s++)
            {
                Spline spline = container.Splines[s];
                if (spline.Count < 2) continue;
                float length = container.CalculateLength(s);
                int steps = math.max(1, (int)math.ceil(length / _sampleSpacing));
                float3 previous = default;
                float previousRadius = 0f, previousFloor = 0f;
                int mouthSteps = math.min(steps / 2, (int)math.ceil(_mouthLength / (length / steps)));
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float3 position = container.EvaluatePosition(s, t);
                    float radius = _radius * math.max(0.05f, KnotValue(_knotRadius, spline, t));
                    float floor = _flatFloor * math.saturate(KnotValue(_knotFloor, spline, t));
                    // the mouths: from each end inwards, at the floor's height, fading out
                    int fromEnd = math.min(i, steps - i);
                    if (_mouthCarve && fromEnd <= mouthSteps && mouthSteps > 0)
                    {
                        float height = radius * _aspect;
                        bool runEnd = i == mouthSteps || i == steps;
                        _mouths.Add(new SplineFeature.Point
                        {
                            Position = new float4(position.x, position.y - height * (1f - floor), position.z, 0f),
                            Shape = new float4(radius * 2f, radius, math.smoothstep(0f, 1f, 1f - fromEnd / (float)mouthSteps), runEnd ? 0f : 1f)
                        });
                    }
                    if (i > 0)
                    {
                        float reach = math.max(radius, previousRadius) * math.max(1f, _aspect);
                        primitives.Add(new StrataCavePrimitive
                        {
                            Type = StrataCavePrimitive.Tube,
                            A = previous,
                            B = position,
                            Radius = new float2(previousRadius, radius),
                            Floor = new float2(previousFloor, floor),
                            Aspect = _aspect,
                            Rotation = quaternion.identity,
                            Min = math.min(previous, position) - reach,
                            Max = math.max(previous, position) + reach
                        });
                    }
                    previous = position;
                    previousRadius = radius;
                    previousFloor = floor;
                }
            }
        }

        // SplineData evaluates to zero, not its default, when it has no keys
        private static float KnotValue(SplineData<float> data, Spline spline, float t) =>
            data.Count == 0 ? data.DefaultValue : data.Evaluate(spline, t, PathIndexUnit.Normalized, new LerpFloat());

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = GizmoColour;
            foreach (StrataCavePrimitive primitive in Primitives)
            {
                Gizmos.DrawLine(primitive.A, primitive.B);
                Gizmos.DrawWireSphere(primitive.B, primitive.Radius.y);
            }
        }
    }
}
