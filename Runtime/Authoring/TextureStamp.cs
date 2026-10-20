using UnityEngine;

namespace Strata
{
    // A grayscale height texture pressed into the terrain. The transform places it: position is the base height for
    // Max, Min and Set (Add and Subtract use only the texture's height), y rotation turns it, and the x and z scale
    // are its footprint in metres. The edges fade out over Edge Falloff (a fraction of the footprint) shaped by the
    // falloff curve.
    [AddComponentMenu("Strata/Texture Stamp")]
    public class TextureStamp : StrataInlineFeature
    {
        [SerializeField] private Texture2D _texture;
        [Tooltip("Height in metres of a white texel.")]
        [SerializeField] private float _height = 100f;
        [SerializeField, Range(0f, 0.5f)] private float _edgeFalloff = 0.1f;
        [SerializeField] private AnimationCurve _falloffCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        public Texture2D Texture => _texture;
        public float Height => _height;
        public float EdgeFalloff => _edgeFalloff;
        public AnimationCurve FalloffCurve => _falloffCurve;

        public override Rect Bounds => FootprintBounds();

        private void Reset() => transform.localScale = new Vector3(512f, 1f, 512f);

        private void OnDrawGizmosSelected() => DrawFootprintGizmo(new Color(1f, 0.8f, 0.2f));
    }
}
