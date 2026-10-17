using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    public enum StrataFilter
    {
        Smooth,
        Terrace,
        Thermal,
        Hydraulic
    }

    // Reworks the terrain built so far inside the transform's footprint (x and z scale in metres), or everywhere when
    // unbounded: a blur, terracing, thermal erosion (material sliding down slopes steeper than the talus angle) or
    // hydraulic erosion (rain, flowing water carrying and dropping sediment, on a grid of virtual pipes). The result is
    // blended back with the edge falloff, strength and mask; the blend mode doesn't apply. Erosion is confined to the
    // footprint, so the live preview reruns the whole footprint whenever anything inside it changes.
    [AddComponentMenu("Strata/Filter Feature")]
    public class FilterFeature : StrataFeature
    {
        [SerializeField] private StrataFilter _filter = StrataFilter.Smooth;
        [SerializeField] private bool _unbounded;
        [SerializeField, Range(0f, 0.5f)] private float _edgeFalloff = 0.15f;
        [SerializeField] private AnimationCurve _falloffCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Smooth")]
        [SerializeField, Min(0.5f)] private float _radius = 4f;

        [Header("Terrace")]
        [SerializeField, Min(0.1f)] private float _stepHeight = 12f;
        [SerializeField, Range(0f, 1f)] private float _sharpness = 0.7f;

        [Header("Erosion")]
        [SerializeField, Range(1, 2000)] private int _iterations = 200;
        [Tooltip("Thermal, and hydraulic slippage: steepest stable slope for loose material, degrees.")]
        [SerializeField, Range(5f, 80f)] private float _talusAngle = 35f;
        [Tooltip("Hydraulic: rain per iteration, metres.")]
        [SerializeField, Min(0f)] private float _rain = 0.01f;
        [Tooltip("Hydraulic: how much sediment water can carry, per unit of speed and slope.")]
        [SerializeField, Min(0f)] private float _capacity = 0.5f;
        [Tooltip("Hydraulic: fraction of the spare capacity picked up per second.")]
        [SerializeField, Range(0f, 2f)] private float _erosionRate = 0.5f;
        [Tooltip("Hydraulic: fraction of the excess sediment dropped per second.")]
        [SerializeField, Range(0f, 2f)] private float _depositionRate = 0.5f;
        [SerializeField, Range(0f, 0.2f)] private float _evaporation = 0.015f;

        public StrataFilter Filter => _filter;
        public float EdgeFalloff => _edgeFalloff;
        public AnimationCurve FalloffCurve => _falloffCurve;
        public float Radius => _radius;
        public float StepHeight => _stepHeight;
        public float Sharpness => _sharpness;
        public int Iterations => _iterations;
        public float TalusAngle => _talusAngle;
        public float Rain => _rain;
        public float Capacity => _capacity;
        public float ErosionRate => _erosionRate;
        public float DepositionRate => _depositionRate;
        public float Evaporation => _evaporation;

        public override bool Unbounded => _unbounded;
        public override Rect Bounds => FootprintBounds();
        // a blur reads three radii out; terrace reads nothing around it, erosion only inside its footprint
        public override float Apron => _filter == StrataFilter.Smooth ? _radius * 3f : 0f;

        private void Reset()
        {
            transform.localScale = new Vector3(512f, 1f, 512f);
            _blend = StrataBlend.Set;
            _mask.UseHeight = false;
            _mask.UseSlope = false;
        }

        private void OnDrawGizmosSelected()
        {
            if (!_unbounded) DrawFootprintGizmo(new Color(0.6f, 1f, 0.5f));
        }
    }
}
