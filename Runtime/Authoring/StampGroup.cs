using UnityEngine;

namespace Strata
{
    public enum StrataGroupSource
    {
        // children start from flat zero, e.g. a composite shape added on top
        Empty,
        // children start from the terrain so far, e.g. a set of edits faded in together
        Current
    }

    // Evaluates its child features into a layer of their own, then blends that layer into the terrain as one result,
    // with the group's blend mode, strength and mask. Its bounds are its children's, so it has no footprint of its own.
    [AddComponentMenu("Strata/Stamp Group")]
    public class StampGroup : StrataInlineFeature
    {
        [SerializeField] private StrataGroupSource _source = StrataGroupSource.Empty;

        public StrataGroupSource Source => _source;

        // Set by the evaluator from the children each time it walks the hierarchy.
        public Rect ChildBounds { get; set; }
        public bool ChildrenUnbounded { get; set; }

        public override Rect Bounds => ChildBounds;
        public override bool Unbounded => ChildrenUnbounded;

        private void Reset()
        {
            _mask.UseHeight = false;
            _mask.UseSlope = false;
        }
    }
}
