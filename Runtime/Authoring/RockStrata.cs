using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // Layered rock on steep ground, inside the transform's footprint (x and z scale in metres) or everywhere when
    // unbounded. Where the slope is within the slope range, it terraces the heights into ledges along the rock beds
    // (so collision and navigation follow them) and marks the ground as this strata for the surface shader, which
    // pushes each bed's face out, shades the beds even beyond the pushed geometry, tints each bed and lines the
    // texture up with the beds. Beds are horizontal layers tilted by the dip angle towards the dip direction (the
    // transform's y rotation), with jittered thicknesses. Painting (with Source Influence) paints the ledge tops.
    [AddComponentMenu("Strata/Rock Strata")]
    public class RockStrata : StrataInlineFeature
    {
        [SerializeField] private bool _unbounded;
        [SerializeField, Range(0f, 0.5f)] private float _edgeFalloff = 0.15f;
        [SerializeField] private AnimationCurve _falloffCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Where")]
        [Tooltip("Slope, in degrees, at which strata start to appear.")]
        [SerializeField, Range(0f, 90f)] private float _slopeStart = 35f;
        [Tooltip("Slope, in degrees, at which strata are at full strength.")]
        [SerializeField, Range(0f, 90f)] private float _slopeFull = 50f;

        [Header("Beds")]
        [SerializeField, Min(0.25f)] private float _thickness = 4f;
        [Tooltip("How much bed thicknesses vary.")]
        [SerializeField, Range(0f, 0.9f)] private float _variation = 0.6f;
        [Tooltip("Tilt of the beds, in degrees, rising towards the transform's forward (z) direction.")]
        [SerializeField, Range(-45f, 45f)] private float _dipAngle;
        [SerializeField] private int _seed = 1;

        [Header("Ledges (heights)")]
        [SerializeField, Range(0f, 1f)] private float _terraceStrength = 0.6f;
        [Tooltip("Fraction of each bed flattened into a ledge; the rest is the riser.")]
        [SerializeField, Range(0f, 0.95f)] private float _terraceSharpness = 0.35f;

        [Header("Banding (surface)")]
        [Tooltip("How far each bed's face is pushed out, in metres.")]
        [SerializeField, Range(0f, 2f)] private float _push = 0.5f;
        [Tooltip("Pushed geometry within this distance of the camera, in metres; the detail patches cost triangles.")]
        [SerializeField, Range(10f, 400f)] private float _geometryRange = 120f;
        [Tooltip("Bands still shaded (normals only) out to this distance, in metres.")]
        [SerializeField, Range(10f, 4000f)] private float _normalRange = 1000f;
        [Tooltip("Brightness and hue difference between beds.")]
        [SerializeField, Range(0f, 1f)] private float _tint = 0.3f;
        [Tooltip("On steep faces, line textures up with the beds and offset each bed's texture.")]
        [SerializeField] private bool _alignedUVs = true;

        public float EdgeFalloff => _edgeFalloff;
        public AnimationCurve FalloffCurve => _falloffCurve;
        public float SlopeStart => _slopeStart;
        public float SlopeFull => math.max(_slopeFull, _slopeStart + 0.5f);

        public override bool Unbounded => _unbounded;
        public override Rect Bounds => FootprintBounds();
        // slope is read from the neighbours
        public override float Apron => 2f;

        public StrataBandDefinition Definition
        {
            get
            {
                float yaw = math.radians(transform.eulerAngles.y);
                float2 forward = new(math.sin(yaw), math.cos(yaw));
                return new StrataBandDefinition
                {
                    Thickness = _thickness,
                    Variation = _variation,
                    Seed = _seed,
                    Push = _push,
                    DipSlope = -forward * math.tan(math.radians(_dipAngle)),
                    TerraceStrength = _terraceStrength,
                    TerraceSharpness = _terraceSharpness,
                    GeometryRange = _geometryRange,
                    NormalRange = math.max(_normalRange, _geometryRange),
                    Tint = _tint,
                    AlignedUVs = _alignedUVs
                };
            }
        }

        private void Reset()
        {
            transform.localScale = new Vector3(512f, 1f, 512f);
            _mask.UseHeight = false;
            _mask.UseSlope = false;
        }

        private void OnDrawGizmosSelected()
        {
            if (!_unbounded) DrawFootprintGizmo(new Color(0.8f, 0.5f, 0.3f));
        }
    }
}
