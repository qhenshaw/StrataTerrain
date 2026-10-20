using UnityEngine;

namespace Strata
{
    // A forest, placed over the whole world or within this object's footprint (scale = size in metres) with a soft
    // edge. The Forest block says where and how densely it grows (height, slope, moisture, layers, noise, density) and
    // which species it mixes; Strength fades it for this placement. With Replace on, it also clears the forests of Tree
    // Scatters above it by its own weight, so a footprint can swap in a different species mix (a birch grove in a pine
    // forest). The forest's floor paint, when enabled, follows its density, which puts a forest floor layer under dense
    // stands. Tree effects of other features point at a Tree Scatter to thin, clear or plant its forest; a disabled Tree
    // Scatter places nothing itself but can still be planted that way (a hedgerow's species).
    [AddComponentMenu("Strata/Tree Scatter")]
    public class TreeScatter : StrataFeature
    {
        [SerializeField] private StrataForest _forest = new();
        [SerializeField] private bool _unbounded = true;
        [Tooltip("Clear the forests of Tree Scatters above this one, by this one's weight.")]
        [SerializeField] private bool _replace;
        [SerializeField, Range(0f, 0.5f)] private float _edgeFalloff = 0.2f;
        [SerializeField] private AnimationCurve _falloffCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        public StrataForest Forest => _forest;
        public bool Replace => _replace;
        public float EdgeFalloff => _edgeFalloff;
        public AnimationCurve FalloffCurve => _falloffCurve;

        public override StrataPaint Paint => _forest.FloorPaint;
        public override bool Unbounded => _unbounded;
        public override Rect Bounds => FootprintBounds();
        public override bool AffectsHeights => false;

        private void Reset() => transform.localScale = new Vector3(500f, 1f, 500f);

        protected override void OnValidate()
        {
            base.OnValidate();
            _forest.Touch();
        }

        private void OnDrawGizmosSelected()
        {
            if (!_unbounded) DrawFootprintGizmo(new Color(0.3f, 0.8f, 0.3f));
        }
    }
}
