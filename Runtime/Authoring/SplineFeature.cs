using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;
using UnityEngine.Splines.Interpolators;

namespace Strata
{
    public enum StrataSplineMode
    {
        // flattens to the spline's height
        Road,
        // carves a channel below the spline's height, shaped by the cross section
        River,
        // raises a ridge above the spline's height, shaped by the cross section
        Ridge
    }

    // Shapes the terrain along the splines of a SplineContainer on the same object. Within half the width of the spline
    // the terrain is pulled to the spline's height (plus the cross section for rivers and ridges); beyond that it eases
    // back to the terrain over the falloff distance, shaped by the falloff curve. Width, falloff and strength can be
    // scaled per point along the spline with the knot data lists. Add and Subtract use only the cross section offset, so
    // an Add ridge follows the ground instead of the spline's height.
    [AddComponentMenu("Strata/Spline Feature"), RequireComponent(typeof(SplineContainer))]
    public class SplineFeature : StrataInlineFeature
    {
        // A sample along a spline: world position, then width, falloff, strength and 1 if a segment starts here.
        public struct Point
        {
            public float4 Position;
            public float4 Shape;
        }

        [SerializeField] private StrataSplineMode _mode = StrataSplineMode.Road;
        [SerializeField, Min(0f)] private float _width = 8f;
        [SerializeField, Min(0f)] private float _falloff = 16f;
        [SerializeField] private AnimationCurve _falloffCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
        [Tooltip("River depth or ridge height at the centre, in metres.")]
        [SerializeField] private float _depth = 6f;
        [Tooltip("Cross section from the centre (0) to the edge of the width (1): 1 is full depth or height.")]
        [SerializeField] private AnimationCurve _crossSection = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
        [SerializeField] private SplineData<float> _knotWidth = new() { DefaultValue = 1f };
        [SerializeField] private SplineData<float> _knotFalloff = new() { DefaultValue = 1f };
        [SerializeField] private SplineData<float> _knotStrength = new() { DefaultValue = 1f };
        [Tooltip("Distance between samples along the spline, in metres.")]
        [SerializeField, Min(0.25f)] private float _sampleSpacing = 2f;

        private readonly List<Point> _points = new();
        private Rect _bounds;
        private int _sampledVersion = -1;
        private Matrix4x4 _sampledMatrix;
        private SplineContainer _container;

        public StrataSplineMode Mode => _mode;
        public float Depth => _depth;
        public AnimationCurve FalloffCurve => _falloffCurve;
        public AnimationCurve CrossSection => _crossSection;

        public override Rect Bounds
        {
            get
            {
                Sample();
                return _bounds;
            }
        }

        // The sampled spline points, refreshed if the spline, settings or transform changed.
        public IReadOnlyList<Point> Points
        {
            get
            {
                Sample();
                return _points;
            }
        }

        private SplineContainer Container => _container != null ? _container : _container = GetComponent<SplineContainer>();

        private void Reset() => _blend = StrataBlend.Set;

        private void OnEnable() => Spline.Changed += OnSplineChanged;
        private void OnDisable() => Spline.Changed -= OnSplineChanged;

        private void OnSplineChanged(Spline spline, int knot, SplineModification modification)
        {
            if (Container == null) return;
            foreach (Spline own in Container.Splines)
                if (own == spline) MarkChanged();
        }

        private void Sample()
        {
            if (_sampledVersion == Version && _sampledMatrix == transform.localToWorldMatrix) return;
            _sampledVersion = Version;
            _sampledMatrix = transform.localToWorldMatrix;
            _points.Clear();

            float2 min = new(float.MaxValue), max = new(float.MinValue);
            float reach = 0f;
            SplineContainer container = Container;
            if (container != null)
            {
                for (int s = 0; s < container.Splines.Count; s++)
                {
                    Spline spline = container.Splines[s];
                    if (spline.Count < 2) continue;
                    float length = container.CalculateLength(s);
                    int steps = math.max(1, (int)math.ceil(length / _sampleSpacing));
                    for (int i = 0; i <= steps; i++)
                    {
                        float t = i / (float)steps;
                        float3 position = container.EvaluatePosition(s, t);
                        float width = _width * math.max(0f, KnotValue(_knotWidth, spline, t));
                        float falloff = _falloff * math.max(0f, KnotValue(_knotFalloff, spline, t));
                        float strength = math.saturate(KnotValue(_knotStrength, spline, t));
                        _points.Add(new Point
                        {
                            Position = new float4(position, 0f),
                            Shape = new float4(width, falloff, strength, i < steps ? 1f : 0f)
                        });
                        min = math.min(min, position.xz);
                        max = math.max(max, position.xz);
                        reach = math.max(reach, width * 0.5f + falloff);
                    }
                }
            }
            _bounds = _points.Count > 0 ? new Rect(min.x - reach, min.y - reach, max.x - min.x + reach * 2f, max.y - min.y + reach * 2f) : Rect.zero;
        }

        // SplineData evaluates to zero, not its default, when it has no keys
        private static float KnotValue(SplineData<float> data, Spline spline, float t) =>
            data.Count == 0 ? data.DefaultValue : data.Evaluate(spline, t, PathIndexUnit.Normalized, new LerpFloat());
    }
}
