using System.Collections.Generic;
using UnityEngine;

namespace Strata
{
    // The ordered list of layers a world paints with. The first layer is the base everywhere nothing else paints; the
    // Auto Splat feature then applies each later layer's rule in order, each painting over those before it. Splats
    // store indices into this list, so reordering or removing layers needs a re-bake.
    [CreateAssetMenu(menuName = "Strata/Palette", fileName = "StrataPalette")]
    public class StrataPalette : ScriptableObject
    {
        public const int MaxLayers = 64;

        [SerializeField] private List<StrataLayer> _layers = new();

        public IReadOnlyList<StrataLayer> Layers => _layers;
        private int _version;

        // Changes whenever the list or any layer in it changes.
        public int Version
        {
            get
            {
                int version = _version * 7919;
                foreach (StrataLayer layer in _layers)
                    version = version * 31 + (layer != null ? layer.Version + layer.GetHashCode() : 0);
                return version;
            }
        }

        public int IndexOf(StrataLayer layer) => layer != null ? _layers.IndexOf(layer) : -1;

        public void SetLayers(IEnumerable<StrataLayer> layers)
        {
            _layers = new List<StrataLayer>(layers);
            _version++;
        }

        private void OnValidate()
        {
            if (_layers.Count > MaxLayers) _layers.RemoveRange(MaxLayers, _layers.Count - MaxLayers);
            _version++;
        }
    }
}
