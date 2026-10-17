using UnityEngine;

namespace Strata
{
    // Paints the palette's layers by their rules, in palette order, over the whole world: the first layer everywhere,
    // then each later layer where its rule passes. Like painting features it works on the finished heights, at its
    // place in the hierarchy, so features below it paint over the rules (a road's gravel) and features above it are
    // painted over. The moisture settings shape the moisture rule input, which mixes how much water flows through a
    // place with how close it is to water (below sea level, or within a River spline).
    [AddComponentMenu("Strata/Auto Splat")]
    public class AutoSplat : StrataFeature
    {
        [Tooltip("Upstream area, in square metres, at which flow counts as fully moist.")]
        [SerializeField, Min(1f)] private float _flowForFullMoisture = 40000f;
        [Tooltip("Distance from water, in metres, over which moisture fades out.")]
        [SerializeField, Min(0f)] private float _waterReach = 40f;

        public float FlowForFullMoisture => _flowForFullMoisture;
        public float WaterReach => _waterReach;

        public override bool Unbounded => true;
        public override Rect Bounds => default;
        public override bool AffectsHeights => false;
    }
}
