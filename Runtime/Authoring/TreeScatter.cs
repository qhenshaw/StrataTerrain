using UnityEngine;

namespace Strata
{
    // Places a forest: over the whole world, or within this object's footprint (scale = size in metres) with a soft
    // edge. Strength and the feature's mask scale the forest's density. With Replace on, it also clears the forests
    // placed by Tree Scatters above it by its own weight, so a footprint can swap in a different species mix (a birch
    // grove in a pine forest). Its paint, when enabled, follows the forest's density, which puts a forest floor layer
    // under dense stands. Features below it can thin, clear or plant trees with their tree effects.
    [AddComponentMenu("Strata/Tree Scatter")]
    public class TreeScatter : StrataFeature
    {
        [SerializeField] private StrataForest _forest;
        [SerializeField] private bool _unbounded = true;
        [Tooltip("Clear the forests of Tree Scatters above this one, by this one's weight.")]
        [SerializeField] private bool _replace;
        [SerializeField, Range(0f, 0.5f)] private float _edgeFalloff = 0.2f;
        [SerializeField] private AnimationCurve _falloffCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        public StrataForest Forest => _forest;
        public bool Replace => _replace;
        public float EdgeFalloff => _edgeFalloff;
        public AnimationCurve FalloffCurve => _falloffCurve;

        public override bool Unbounded => _unbounded;
        public override Rect Bounds => FootprintBounds();
        public override bool AffectsHeights => false;

        private void Reset()
        {
            _mask.UseHeight = false;
            _mask.UseSlope = false;
            transform.localScale = new Vector3(500f, 1f, 500f);
        }

        private void OnDrawGizmosSelected()
        {
            if (!_unbounded) DrawFootprintGizmo(new Color(0.3f, 0.8f, 0.3f));
        }
    }
}
