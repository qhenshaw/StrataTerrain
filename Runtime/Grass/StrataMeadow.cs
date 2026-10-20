using System;
using System.Collections.Generic;
using UnityEngine;

namespace Strata
{
    // One kind of grass in a meadow: its share against the others, and optionally where it prefers to grow.
    [Serializable]
    public class StrataMeadowEntry
    {
        public StrataGrassPrototype Prototype;
        [Min(0f)] public float Weight = 1f;
        public StrataRange Height = new(0f, 1000f, 20f);
        public StrataRange Slope = new(0f, 35f, 5f);
        public StrataRange Moisture = new(0f, 1f, 0.2f);
    }

    // A ground cover of grass and details: where it grows (height, slope, moisture, terrain layers, noise), how densely
    // (fraction of each prototype's full density), and which prototypes it mixes. It is a block on a Grass Scatter,
    // which places it.
    [Serializable]
    public class StrataMeadow
    {
        [SerializeField, Range(0f, 1f)] private float _density = 1f;
        [SerializeField] private StrataRange _height = new(0f, 1000f, 20f) { Enabled = true };
        [SerializeField] private StrataRange _slope = new(0f, 35f, 5f) { Enabled = true };
        [SerializeField] private StrataRange _moisture = new(0f, 1f, 0.2f);
        [Tooltip("Layer weight at which a grow-on layer allows full density, or an avoided layer removes it.")]
        [SerializeField, Range(0.05f, 1f)] private float _layerSensitivity = 0.5f;
        [SerializeField] private List<StrataForestLayer> _layers = new();
        [Tooltip("Wavelength of the noise that patches the meadow, in metres.")]
        [SerializeField, Min(1f)] private float _noiseScale = 30f;
        [SerializeField, Range(0f, 1f)] private float _noiseAmount = 0.3f;
        [SerializeField] private int _seed = 1;
        [SerializeField] private List<StrataMeadowEntry> _entries = new();

        public float Density => _density;
        public StrataRange Height => _height;
        public StrataRange Slope => _slope;
        public StrataRange Moisture => _moisture;
        public float LayerSensitivity => _layerSensitivity;
        public IReadOnlyList<StrataForestLayer> Layers => _layers;
        public float NoiseScale => _noiseScale;
        public float NoiseAmount => _noiseAmount;
        public int Seed => _seed;
        public IReadOnlyList<StrataMeadowEntry> Entries => _entries;
        private int _version;

        public int Version
        {
            get
            {
                int version = _version * 7919;
                foreach (StrataMeadowEntry entry in _entries)
                    version = version * 31 + (entry?.Prototype != null ? entry.Prototype.Version + entry.Prototype.GetHashCode() : 0);
                return version;
            }
        }

        public void Set(float density, StrataRange height, StrataRange slope, StrataRange moisture, IEnumerable<StrataForestLayer> layers,
                        float noiseScale, float noiseAmount, IEnumerable<StrataMeadowEntry> entries)
        {
            _density = density;
            _height = height;
            _slope = slope;
            _moisture = moisture;
            _layers = new List<StrataForestLayer>(layers);
            _noiseScale = noiseScale;
            _noiseAmount = noiseAmount;
            _entries = new List<StrataMeadowEntry>(entries);
            _version++;
        }

        // Called by the owning Grass Scatter when it is edited.
        internal void Touch() => _version++;
    }
}
