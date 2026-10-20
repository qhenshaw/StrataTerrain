using UnityEngine;

namespace Strata
{
    // A meadow, placed over the whole world or within this object's footprint (scale = size in metres) with a soft
    // edge. The Meadow block says where and how densely it grows and which grass prototypes it mixes; Strength fades it
    // for this placement. With Replace on, it also clears the grass of Grass Scatters above it by its own weight.
    // Features below it thin or clear grass with tree effects that apply to every forest (no Tree Scatter chosen), so a
    // road or river clearing trees clears grass too.
    [AddComponentMenu("Strata/Grass Scatter")]
    public class GrassScatter : StrataFeature
    {
        [SerializeField] private StrataMeadow _meadow = new();
        [SerializeField] private bool _unbounded = true;
        [Tooltip("Clear the grass of Grass Scatters above this one, by this one's weight.")]
        [SerializeField] private bool _replace;
        [SerializeField, Range(0f, 0.5f)] private float _edgeFalloff = 0.2f;
        [SerializeField] private AnimationCurve _falloffCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        public StrataMeadow Meadow => _meadow;
        public bool Replace => _replace;
        public float EdgeFalloff => _edgeFalloff;
        public AnimationCurve FalloffCurve => _falloffCurve;

        public override bool Unbounded => _unbounded;
        public override Rect Bounds => FootprintBounds();
        public override bool AffectsHeights => false;

        private void Reset() => transform.localScale = new Vector3(200f, 1f, 200f);

        protected override void OnValidate()
        {
            base.OnValidate();
            _meadow.Touch();
        }

        private void OnDrawGizmosSelected()
        {
            if (!_unbounded) DrawFootprintGizmo(new Color(0.5f, 0.9f, 0.3f));
        }
    }
}
