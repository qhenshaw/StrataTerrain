using System;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    public enum StrataBlend
    {
        Add,
        Subtract,
        Max,
        Min,
        Set
    }

    // Restricts a feature to where the terrain it is applied to (the result of everything above it) is within a height
    // and/or slope range, each with a soft edge.
    [Serializable]
    public struct StrataMask
    {
        public bool UseHeight;
        public float MinHeight;
        public float MaxHeight;
        [Min(0f)] public float HeightFalloff;
        public bool UseSlope;
        [Range(0f, 90f)] public float MinSlope;
        [Range(0f, 90f)] public float MaxSlope;
        [Range(0f, 45f)] public float SlopeFalloff;

        public static StrataMask Default => new() { MinHeight = 0f, MaxHeight = 500f, HeightFalloff = 20f, MinSlope = 30f, MaxSlope = 90f, SlopeFalloff = 5f };
    }

    public enum StrataPaintSource
    {
        // where the feature acts (its footprint, falloff, strength and mask)
        Influence,
        // filters: where the filter raised the ground (eroded sediment settling)
        Deposition,
        // filters: where the filter lowered the ground
        Erosion
    }

    // Optional splat painting by a feature: its layer is painted over the splats with the feature's own weight
    // (remapped by the curve), after the heights are final, in hierarchy order.
    [Serializable]
    public struct StrataPaint
    {
        public bool Enabled;
        public StrataLayer Layer;
        [Range(0f, 1f)] public float Strength;
        [Tooltip("Maps the feature's weight (0..1) to paint opacity.")]
        public AnimationCurve Curve;
        [Tooltip("Splines: paint width as a multiple of the feature's width plus falloff.")]
        [Min(0f)] public float WidthScale;
        [Tooltip("Filters: paint where the filter acted, or only where it deposited or eroded.")]
        public StrataPaintSource Source;
        [Tooltip("Filters: metres of deposition or erosion for full paint.")]
        [Min(0.01f)] public float Sensitivity;

        public static StrataPaint Default => new()
        {
            Strength = 1f,
            Curve = AnimationCurve.Linear(0f, 0f, 1f, 1f),
            WidthScale = 1f,
            Source = StrataPaintSource.Influence,
            Sensitivity = 1f
        };
    }

    public enum StrataTreeMode
    {
        None,
        // multiplies tree density by the feature's weight: 0 clears, 1 leaves it, 2 doubles it
        Density,
        // splines: plants a line of the forest's species along the spline; other features: grows the forest (by its own
        // rules) where the feature acts
        Plant
    }

    // What a feature does to trees, after the heights and splats are final, in hierarchy order: scale the density of
    // all forests (or one) where it acts, plant a line of trees along a spline, or grow a forest where it acts.
    [Serializable]
    public struct StrataTreeEffect
    {
        public StrataTreeMode Mode;
        [Tooltip("Density: 0 clears, 1 leaves the density as it is, 2 doubles it.")]
        [Range(0f, 2f)] public float Density;
        [Tooltip("Density: only this Tree Scatter's forest (none for all). Plant: the Tree Scatter whose forest is planted " +
                 "(a line along a spline, or grown by its own rules where any other feature acts).")]
        public TreeScatter Scatter;
        [Tooltip("Splines: width of the effect as a multiple of the feature's width plus falloff.")]
        [Min(0f)] public float WidthScale;
        [Tooltip("Plant on splines: distance between trees along the spline, in metres.")]
        [Min(0.5f)] public float Spacing;
        [Tooltip("Plant on splines: distance from the spline's centre, in metres; non-zero plants a row on each side.")]
        [Min(0f)] public float Offset;
        [Tooltip("Plant on splines: random displacement of each tree, as a fraction of the spacing.")]
        [Range(0f, 0.5f)] public float Jitter;

        // The forest of the chosen Tree Scatter, or null for none.
        public StrataForest TargetForest => Scatter != null ? Scatter.Forest : null;

        public static StrataTreeEffect Default => new() { Density = 0f, WidthScale = 1.2f, Spacing = 8f, Offset = 0f, Jitter = 0.15f };
    }

    // Base for everything that shapes a Strata world: a scene object under the StrataWorld, applied in hierarchy order
    // on top of what the features above it produced. A feature reports the world XZ area it can touch, so the editor's
    // live preview only re-evaluates what an edit affects, and a version that bumps whenever its settings change
    // (transform changes are tracked by the preview itself). Features that shape or paint the ground keep their blend,
    // mask, paint and tree effect on the component (StrataInlineFeature); scatters and Auto Splat don't use them.
    [ExecuteAlways]
    public abstract class StrataFeature : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] protected float _strength = 1f;

        private int _version;

        public virtual StrataBlend Blend => StrataBlend.Add;
        public float Strength => _strength;
        public virtual StrataMask Mask => default;
        public virtual StrataPaint Paint => default;
        public virtual StrataTreeEffect Trees => default;
        public bool Paints => Paint.Enabled && Paint.Layer != null && Paint.Strength > 0f;
        public bool AffectsTrees => Trees.Mode == StrataTreeMode.Density || Trees.Mode == StrataTreeMode.Plant && Trees.TargetForest != null;
        public virtual int Version => _version;
        // False for features that only paint splats (Auto Splat), so edits to them don't re-evaluate heights.
        public virtual bool AffectsHeights => true;

        // The world XZ rectangle this feature can change; ignored when Unbounded.
        public abstract Rect Bounds { get; }
        public virtual bool Unbounded => false;
        // How far beyond its bounds this feature reads heights, in metres (filters).
        public virtual float Apron => 0f;

        public void MarkChanged() => _version++;

        protected virtual void OnValidate() => _version++;

        // The world-space rectangle covered by this transform's XZ footprint (scale = size in metres), rotated about y.
        protected Rect FootprintBounds()
        {
            float2 half = new float2(math.abs(transform.lossyScale.x), math.abs(transform.lossyScale.z)) * 0.5f;
            float yaw = math.radians(transform.eulerAngles.y);
            float2 extent = new(half.x * math.abs(math.cos(yaw)) + half.y * math.abs(math.sin(yaw)),
                                half.x * math.abs(math.sin(yaw)) + half.y * math.abs(math.cos(yaw)));
            Vector3 p = transform.position;
            return new Rect(p.x - extent.x, p.z - extent.y, extent.x * 2f, extent.y * 2f);
        }

        protected void DrawFootprintGizmo(Color colour)
        {
            Gizmos.color = colour;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                                          new Vector3(transform.lossyScale.x, 1f, transform.lossyScale.z));
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(1f, 0f, 1f));
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
