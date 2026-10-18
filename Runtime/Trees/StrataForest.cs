using System;
using System.Collections.Generic;
using UnityEngine;

namespace Strata
{
    // One kind of tree in a forest: how often it is chosen against the others, how far apart its trees stand, how they
    // vary, and optionally where it prefers to grow within the forest (its own height, slope and moisture ranges).
    [Serializable]
    public class StrataTreeSpecies
    {
        public StrataTreePrototype Prototype;
        [Min(0f)] public float Weight = 1f;
        [Tooltip("Typical distance between neighbouring trees of this species at full density, in metres. Trees of " +
                 "different species keep apart by the average of their spacings, times 0.7.")]
        [Min(0.25f)] public float Spacing = 6f;
        [Tooltip("How much trees gather into groups with gaps between (0 even, 1 tight groups).")]
        [Range(0f, 1f)] public float Clumping = 0.6f;
        [Tooltip("Size of the groups and gaps, in metres (0: three times the spacing).")]
        [Min(0f)] public float ClumpSize;
        [Tooltip("How much the room each tree keeps varies from tree to tree.")]
        [Range(0f, 1f)] public float SpacingVariation = 0.5f;
        [Tooltip("Uniform scale range.")]
        public Vector2 Scale = new(0.8f, 1.2f);
        [Tooltip("Random lean, in degrees.")]
        [Range(0f, 15f)] public float Lean = 2f;
        [Tooltip("How far trees tilt towards the slope's normal (0 upright, 1 fully).")]
        [Range(0f, 1f)] public float AlignToGround;
        [Tooltip("How far the base sinks into flat ground, in metres; slopes sink it further by the trunk radius.")]
        [Min(0f)] public float Sink = 0.1f;
        public StrataRange Height = new(0f, 1000f, 20f);
        public StrataRange Slope = new(0f, 35f, 5f);
        public StrataRange Moisture = new(0f, 1f, 0.2f);
    }

    // A terrain layer a forest grows on or avoids, by the layer's weight in the splats.
    [Serializable]
    public struct StrataForestLayer
    {
        public StrataLayer Layer;
        [Tooltip("Grow only where these layers are (their weights add up), or keep away from them.")]
        public bool Avoid;
    }

    // A biome of trees: where it grows (height, slope, moisture, the terrain layers under it, and noise for clearings),
    // how densely, and which species it mixes. A Tree Scatter feature places a forest over the world or a footprint;
    // several forests can share a world. Density is the fraction of full stocking: 1 packs each species as closely as
    // its spacing allows.
    [CreateAssetMenu(menuName = "Strata/Forest", fileName = "StrataForest")]
    public class StrataForest : ScriptableObject
    {
        [SerializeField, Range(0f, 1f)] private float _density = 0.7f;
        [SerializeField] private StrataRange _height = new(0f, 1000f, 20f) { Enabled = true };
        [SerializeField] private StrataRange _slope = new(0f, 35f, 5f) { Enabled = true };
        [SerializeField] private StrataRange _moisture = new(0f, 1f, 0.2f);
        [Tooltip("Layer weight at which a grow-on layer allows full density, or an avoided layer removes it.")]
        [SerializeField, Range(0.05f, 1f)] private float _layerSensitivity = 0.5f;
        [SerializeField] private List<StrataForestLayer> _layers = new();
        [Tooltip("Wavelength of the noise that breaks the forest into stands and clearings, in metres.")]
        [SerializeField, Min(1f)] private float _noiseScale = 120f;
        [SerializeField, Range(0f, 1f)] private float _noiseAmount = 0.5f;
        [SerializeField] private int _seed = 1;
        [Tooltip("How much each species keeps to its own stands rather than mixing evenly with the others.")]
        [SerializeField, Range(0f, 1f)] private float _patchiness = 0.7f;
        [Tooltip("Size of the single-species stands, in metres.")]
        [SerializeField, Min(5f)] private float _patchSize = 120f;
        [SerializeField] private List<StrataTreeSpecies> _species = new();

        public float Density => _density;
        public StrataRange Height => _height;
        public StrataRange Slope => _slope;
        public StrataRange Moisture => _moisture;
        public float LayerSensitivity => _layerSensitivity;
        public IReadOnlyList<StrataForestLayer> Layers => _layers;
        public float NoiseScale => _noiseScale;
        public float NoiseAmount => _noiseAmount;
        public int Seed => _seed;
        public float Patchiness => _patchiness;
        public float PatchSize => _patchSize;
        public IReadOnlyList<StrataTreeSpecies> Species => _species;
        private int _version;

        // Changes whenever the forest or any of its prototypes changes.
        public int Version
        {
            get
            {
                int version = _version * 7919;
                foreach (StrataTreeSpecies species in _species)
                    version = version * 31 + (species?.Prototype != null ? species.Prototype.Version + species.Prototype.GetHashCode() : 0);
                return version;
            }
        }

        public void Set(float density, StrataRange height, StrataRange slope, StrataRange moisture, IEnumerable<StrataForestLayer> layers,
                        float noiseScale, float noiseAmount, IEnumerable<StrataTreeSpecies> species)
        {
            _density = density;
            _height = height;
            _slope = slope;
            _moisture = moisture;
            _layers = new List<StrataForestLayer>(layers);
            _noiseScale = noiseScale;
            _noiseAmount = noiseAmount;
            _species = new List<StrataTreeSpecies>(species);
            _version++;
        }

        private void OnValidate() => _version++;
    }
}
