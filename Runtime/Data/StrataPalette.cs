using System;
using System.Collections.Generic;
using UnityEngine;

namespace Strata
{
    // Limits a palette layer's rule to some of the world's surface biomes.
    [Serializable]
    public class StrataLayerBiomeMask
    {
        public StrataLayer Layer;
        public StrataBiomeFilter Filter;
    }

    // The ordered list of layers a world paints with, as a block on its StrataWorld. The first layer is the base
    // everywhere nothing else paints; the Auto Splat feature then applies each later layer's rule in order, each
    // painting over those before it, limited to the biomes its biome mask allows (layers are assets shared between
    // worlds, so the masks live here with the world's biome areas). Splats store indices into this list, so reordering
    // or removing layers needs a re-bake.
    [Serializable]
    public class StrataPalette
    {
        public const int MaxLayers = 64;

        [SerializeField] private List<StrataLayer> _layers = new();
        [Tooltip("Limits layers' rules to biomes (a layer without a mask paints in every biome).")]
        [SerializeField] private List<StrataLayerBiomeMask> _biomeMasks = new();

        public IReadOnlyList<StrataLayer> Layers => _layers;
        public IReadOnlyList<StrataLayerBiomeMask> BiomeMasks => _biomeMasks;
        public bool IsEmpty => _layers.Count == 0;
        private int _version;

        // Changes whenever the list or any layer in it changes.
        public int Version
        {
            get
            {
                int version = _version * 7919;
                foreach (StrataLayer layer in _layers)
                    version = version * 31 + (layer != null ? layer.Version + layer.GetHashCode() : 0);
                foreach (StrataLayerBiomeMask mask in _biomeMasks)
                    if (mask != null) version = version * 31 + (mask.Layer != null ? mask.Layer.GetHashCode() : 0) * 7 + mask.Filter.Hash;
                return version;
            }
        }

        public int IndexOf(StrataLayer layer) => layer != null ? _layers.IndexOf(layer) : -1;

        // The biome filter on a layer's rule (Any without a mask).
        public StrataBiomeFilter FilterFor(StrataLayer layer)
        {
            foreach (StrataLayerBiomeMask mask in _biomeMasks)
                if (mask != null && mask.Layer == layer && layer != null) return mask.Filter;
            return default;
        }

        public void SetBiomeMasks(IEnumerable<StrataLayerBiomeMask> masks)
        {
            _biomeMasks = new List<StrataLayerBiomeMask>(masks);
            _version++;
        }

        public void SetLayers(IEnumerable<StrataLayer> layers)
        {
            _layers = new List<StrataLayer>(layers);
            _version++;
        }

        // Called by the StrataWorld when it is edited.
        public void Validate()
        {
            if (_layers.Count > MaxLayers) _layers.RemoveRange(MaxLayers, _layers.Count - MaxLayers);
            _version++;
        }
    }
}
