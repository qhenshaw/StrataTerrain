using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace Strata
{
    public enum StrataBiomeKind
    {
        // the ground's surface
        Surface,
        // caves: what a point below the surface is in
        Underground
    }

    // A biome: a named region of the world, painted into the world's surface or underground biome map over the areas
    // above it in hierarchy order, as palette layers paint over each other. Its outline is every closed spline in the
    // SplineContainers on this object and its children (several splines make one biome of separate patches); with no
    // outline it covers the whole world, the default biome, so place that first. The edge eases over the falloff
    // (centred on the outline) and wanders by the edge noise. Palette biome masks, forests, meadows, cave layers and
    // cave formations can be limited to biomes, and gameplay code asks which biome is where with StrataGround.TryGetBiome
    // (the strongest at a point). Biomes only paint the maps; they don't change heights.
    [AddComponentMenu("Strata/Biome Area")]
    public class StrataBiomeArea : StrataFeature
    {
        // A closed outline in world XZ, and its bounds.
        public sealed class Outline
        {
            public float2[] Points;
            public float2 Min;
            public float2 Max;
        }

        [SerializeField] private string _displayName = "Biome";
        [SerializeField] private Color _colour = new(0.4f, 0.8f, 0.4f);
        [SerializeField] private StrataBiomeKind _kind = StrataBiomeKind.Surface;
        [Tooltip("Width of the edge, centred on the outline, in metres (0 for a hard edge).")]
        [SerializeField, Min(0f)] private float _falloff = 16f;
        [Tooltip("How far the edge wanders in and out, in metres.")]
        [SerializeField, Min(0f)] private float _noiseAmplitude = 6f;
        [Tooltip("Wavelength of the edge's wander, in metres.")]
        [SerializeField, Min(1f)] private float _noiseScale = 40f;
        [SerializeField] private int _noiseSeed = 1;
        [Tooltip("Distance between samples along the outline, in metres.")]
        [SerializeField, Min(0.5f)] private float _sampleSpacing = 2f;

        private readonly List<Outline> _outlines = new();
        private readonly List<SplineContainer> _containers = new();
        private int _sampledHash = int.MinValue;
        private Rect _bounds;

        public string DisplayName => string.IsNullOrEmpty(_displayName) ? name : _displayName;
        public Color Colour => _colour;
        public StrataBiomeKind Kind => _kind;
        public float Falloff => _falloff;
        public float NoiseAmplitude => _noiseAmplitude;
        public float NoiseScale => _noiseScale;
        public int NoiseSeed => _noiseSeed;
        public override bool AffectsHeights => false;
        public override bool Unbounded => Outlines.Count == 0;

        // the outline's bounds, grown by everything that reaches beyond it
        public override Rect Bounds
        {
            get
            {
                Sample();
                return _bounds;
            }
        }

        // Folds in the child splines' transforms, which the preview doesn't track.
        public override int Version
        {
            get
            {
                int version = base.Version;
                foreach (SplineContainer container in Containers())
                    version = version * 31 + container.transform.localToWorldMatrix.GetHashCode();
                return version;
            }
        }

        public IReadOnlyList<Outline> Outlines
        {
            get
            {
                Sample();
                return _outlines;
            }
        }

        private void OnEnable() => Spline.Changed += OnSplineChanged;
        private void OnDisable() => Spline.Changed -= OnSplineChanged;

        private void OnSplineChanged(Spline spline, int knot, SplineModification modification)
        {
            foreach (SplineContainer container in Containers())
                foreach (Spline own in container.Splines)
                    if (own == spline)
                    {
                        MarkChanged();
                        return;
                    }
        }

        private List<SplineContainer> Containers()
        {
            GetComponentsInChildren(false, _containers);
            return _containers;
        }

        private void Sample()
        {
            int hash = Version * 31 + transform.localToWorldMatrix.GetHashCode();
            if (hash == _sampledHash) return;
            _sampledHash = hash;
            _outlines.Clear();
            float2 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (SplineContainer container in Containers())
                for (int s = 0; s < container.Splines.Count; s++)
                {
                    if (container.Splines[s].Count < 3) continue;
                    float length = container.CalculateLength(s);
                    int steps = math.max(3, (int)math.ceil(length / _sampleSpacing));
                    Outline outline = new() { Points = new float2[steps], Min = new float2(float.MaxValue), Max = new float2(float.MinValue) };
                    // the last point closes back onto the first, so it isn't repeated
                    for (int i = 0; i < steps; i++)
                    {
                        float2 p = ((float3)container.EvaluatePosition(s, i / (float)steps)).xz;
                        outline.Points[i] = p;
                        outline.Min = math.min(outline.Min, p);
                        outline.Max = math.max(outline.Max, p);
                    }
                    _outlines.Add(outline);
                    min = math.min(min, outline.Min);
                    max = math.max(max, outline.Max);
                }
            float reach = _falloff * 0.5f + _noiseAmplitude;
            _bounds = _outlines.Count > 0 ? new Rect(min.x - reach, min.y - reach, max.x - min.x + reach * 2f, max.y - min.y + reach * 2f) : Rect.zero;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = _colour;
            foreach (Outline outline in Outlines) DrawOutline(outline, 0f);
        }

        private void OnDrawGizmosSelected()
        {
            if (_falloff <= 0f) return;
            Gizmos.color = new Color(_colour.r, _colour.g, _colour.b, 0.35f);
            foreach (Outline outline in Outlines)
            {
                DrawOutline(outline, _falloff * 0.5f);
                DrawOutline(outline, -_falloff * 0.5f);
            }
        }

        // the outline, pushed out (positive) or in along its normals, drawn at the transform's height
        private void DrawOutline(Outline outline, float offset)
        {
            float y = transform.position.y;
            int count = outline.Points.Length;
            float winding = 0f;
            for (int i = 0; i < count; i++)
            {
                float2 a = outline.Points[i], b = outline.Points[(i + 1) % count];
                winding += (b.x - a.x) * (b.y + a.y);
            }
            float side = winding > 0f ? 1f : -1f;
            Vector3 Offset(int i)
            {
                float2 before = outline.Points[(i + count - 1) % count], after = outline.Points[(i + 1) % count];
                float2 tangent = math.normalizesafe(after - before);
                float2 p = outline.Points[i] + new float2(-tangent.y, tangent.x) * offset * side;
                return new Vector3(p.x, y, p.y);
            }
            for (int i = 0; i < count; i++) Gizmos.DrawLine(Offset(i), Offset((i + 1) % count));
        }
    }
}
