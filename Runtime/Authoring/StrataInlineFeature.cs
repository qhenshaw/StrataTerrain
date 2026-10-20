using UnityEngine;

namespace Strata
{
    // A feature that keeps its blend, mask, paint and tree effect on the component itself, for features placed one by
    // one with little to share (texture stamps, stamp groups).
    public abstract class StrataInlineFeature : StrataFeature
    {
        [SerializeField] protected StrataBlend _blend = StrataBlend.Add;
        [SerializeField] protected StrataMask _mask = StrataMask.Default;
        [SerializeField] protected StrataPaint _paint = StrataPaint.Default;
        [SerializeField] protected StrataTreeEffect _trees = StrataTreeEffect.Default;

        public override StrataBlend Blend => _blend;
        public override StrataMask Mask => _mask;
        public override StrataPaint Paint => _paint;
        public override StrataTreeEffect Trees => _trees;
    }
}
