using UnityEngine;

namespace Strata
{
    public enum StrataNoise
    {
        Fbm,
        Ridged,
        Billow
    }

    // Procedural fractal noise, 0..1 times Height. Bounded, it covers the transform's footprint (x and z scale in metres)
    // with faded edges like a texture stamp; unbounded, it covers the whole world, which makes it the usual first
    // feature (the base terrain). Position offsets the noise for Max, Min and Set, as with texture stamps.
    [AddComponentMenu("Strata/Noise Stamp")]
    public class NoiseStamp : StrataInlineFeature
    {
        [SerializeField] private StrataNoise _noise = StrataNoise.Fbm;
        [SerializeField] private bool _unbounded;
        [SerializeField] private float _height = 200f;
        [Tooltip("Wavelength of the first octave, in metres.")]
        [SerializeField, Min(1f)] private float _scale = 800f;
        [SerializeField, Range(1, 12)] private int _octaves = 6;
        [SerializeField, Range(1.5f, 3f)] private float _lacunarity = 2f;
        [SerializeField, Range(0.2f, 0.8f)] private float _gain = 0.5f;
        [SerializeField] private int _seed = 1;
        [SerializeField, Range(0f, 0.5f)] private float _edgeFalloff = 0.2f;
        [SerializeField] private AnimationCurve _falloffCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        public StrataNoise Noise => _noise;
        public float Height => _height;
        public float Scale => _scale;
        public int Octaves => _octaves;
        public float Lacunarity => _lacunarity;
        public float Gain => _gain;
        public int Seed => _seed;
        public float EdgeFalloff => _edgeFalloff;
        public AnimationCurve FalloffCurve => _falloffCurve;

        public override bool Unbounded => _unbounded;
        public override Rect Bounds => FootprintBounds();

        private void Reset() => transform.localScale = new Vector3(1024f, 1f, 1024f);

        private void OnDrawGizmosSelected()
        {
            if (!_unbounded) DrawFootprintGizmo(new Color(0.4f, 0.9f, 1f));
        }
    }
}
