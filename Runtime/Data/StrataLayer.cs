using System;
using UnityEngine;

namespace Strata
{
    // A soft 0..1 range test: full weight between Min and Max, fading to zero over Falloff outside them.
    [Serializable]
    public struct StrataRange
    {
        public bool Enabled;
        public float Min;
        public float Max;
        [Min(0f)] public float Falloff;

        public StrataRange(float min, float max, float falloff)
        {
            Enabled = false;
            Min = min;
            Max = max;
            Falloff = falloff;
        }

        public Vector4 Packed => new(Enabled ? 1f : 0f, Min, Max, Falloff);
    }

    // Where a layer paints itself when the palette's rules run (the Auto Splat feature). Each enabled range must pass;
    // noise breaks up the result. Height is in metres, slope in degrees, curvature from -1 (ridges) to 1 (hollows),
    // moisture from 0 (dry) to 1 (in or beside water, or where much water collects).
    [Serializable]
    public struct StrataLayerRule
    {
        public bool Enabled;
        [Range(0f, 1f)] public float Opacity;
        public StrataRange Height;
        public StrataRange Slope;
        public StrataRange Curvature;
        public StrataRange Moisture;
        [Tooltip("Wavelength of the noise that breaks up this layer's edges, in metres.")]
        [Min(0.1f)] public float NoiseScale;
        [Range(0f, 1f)] public float NoiseAmount;

        public static StrataLayerRule Default => new()
        {
            Enabled = true,
            Opacity = 1f,
            Height = new StrataRange(0f, 500f, 20f),
            Slope = new StrataRange(0f, 30f, 5f),
            Curvature = new StrataRange(-1f, 1f, 0.1f),
            Moisture = new StrataRange(0.5f, 1f, 0.2f),
            NoiseScale = 20f,
            NoiseAmount = 0f
        };
    }

    public enum StrataMaskLayout
    {
        // R metallic, G occlusion, B height, A smoothness (Unity's terrain and HDRP mask maps)
        MetallicOcclusionHeightSmoothness,
        // R metallic, G height, B emission (unused), A roughness
        MetallicHeightEmissionRoughness
    }

    // One terrain material: albedo, a normal map (OpenGL-style, or DirectX with its green flipped) and a mask map in one
    // of the StrataMaskLayout channel orders, tiled every Tile Size metres, plus its automatic placement rule. Layers are listed in a
    // StrataPalette; all of a palette's textures of one kind must share size and format so they can be packed into
    // texture arrays (the palette's inspector can fix the import settings).
    [CreateAssetMenu(menuName = "Strata/Layer", fileName = "StrataLayer")]
    public class StrataLayer : ScriptableObject
    {
        [SerializeField] private Texture2D _albedo;
        [SerializeField] private Texture2D _normal;
        [SerializeField] private Texture2D _mask;
        [SerializeField] private StrataMaskLayout _maskLayout;
        [Tooltip("The normal map's green channel points down (DirectX convention).")]
        [SerializeField] private bool _directXNormal;
        [SerializeField, Min(0.1f)] private float _tileSize = 4f;
        [SerializeField] private Color _tint = Color.white;
        [SerializeField, Range(0f, 2f)] private float _normalStrength = 1f;
        [SerializeField, Range(0f, 2f)] private float _smoothness = 1f;
        [SerializeField, Range(0f, 1f)] private float _occlusion = 1f;
        [Tooltip("How sharply this layer's height map cuts into its neighbours where they blend; small is sharp.")]
        [SerializeField, Range(0.01f, 1f)] private float _blendDepth = 0.2f;
        [Tooltip("Sample with hexagonal tile blending, which hides repetition at about three times the cost.")]
        [SerializeField] private bool _stochastic;
        [Tooltip("Far away, blend towards the texture tiled this many times larger, to hide repetition.")]
        [SerializeField, Range(1f, 8f)] private float _farScale = 4f;
        [SerializeField] private StrataLayerRule _rule = StrataLayerRule.Default;

        public Texture2D Albedo => _albedo;
        public Texture2D Normal => _normal;
        public Texture2D Mask => _mask;
        public StrataMaskLayout MaskLayout => _maskLayout;
        public bool DirectXNormal => _directXNormal;
        public float TileSize => _tileSize;
        public Color Tint => _tint;
        public float NormalStrength => _normalStrength;
        public float Smoothness => _smoothness;
        public float Occlusion => _occlusion;
        public float BlendDepth => _blendDepth;
        public bool Stochastic => _stochastic;
        public float FarScale => _farScale;
        public StrataLayerRule Rule => _rule;
        public int Version { get; private set; }

        public void Set(Texture2D albedo, Texture2D normal, Texture2D mask, float tileSize)
        {
            _albedo = albedo;
            _normal = normal;
            _mask = mask;
            _tileSize = tileSize;
            Version++;
        }

        public void SetFormat(StrataMaskLayout maskLayout, bool directXNormal, Color tint)
        {
            _maskLayout = maskLayout;
            _directXNormal = directXNormal;
            _tint = tint;
            Version++;
        }

        public void SetRule(StrataLayerRule rule)
        {
            _rule = rule;
            Version++;
        }

        private void OnValidate() => Version++;
    }
}
