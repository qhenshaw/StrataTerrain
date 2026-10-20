using UnityEngine;

namespace Strata
{
    // Paints the palette's layers by their rules, in palette order, over the whole world: the first layer everywhere,
    // then each later layer where its rule passes. Like painting features it works on the finished heights, at its
    // place in the hierarchy, so features below it paint over the rules (a road's gravel) and features above it are
    // painted over. The moisture rule input mixes how much water flows through a place with how close it is to water
    // (below sea level, or within a River spline), shaped by the world settings' moisture fields.
    [AddComponentMenu("Strata/Auto Splat")]
    public class AutoSplat : StrataFeature
    {
        public override bool Unbounded => true;
        public override Rect Bounds => default;
        public override bool AffectsHeights => false;
    }
}
