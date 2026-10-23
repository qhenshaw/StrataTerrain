using UnityEngine;

namespace Strata
{
    public enum StrataFormationSurface
    {
        // cave floors, growing up (stalagmites, boulders)
        Floor,
        // ceilings, hanging down (stalactites)
        Ceiling,
        // walls, growing out of them (crystals, ledges)
        Wall
    }

    // One kind of cave formation a Cave Formations feature scatters: a mesh modelled growing up its +Y axis from its base
    // at the origin, its materials (any instancing-capable shader, such as URP/Lit), the surface it grows on, how far it
    // leans from straight up or down towards the surface's normal, its size range and how deep its base sinks, how far it
    // is drawn, and whether it collides.
    [CreateAssetMenu(menuName = "Strata/Formation Prototype", fileName = "StrataFormationPrototype")]
    public class StrataFormationPrototype : ScriptableObject
    {
        [SerializeField] private Mesh _mesh;
        [Tooltip("One per submesh.")]
        [SerializeField] private Material[] _materials = new Material[0];
        [SerializeField] private StrataFormationSurface _surface = StrataFormationSurface.Floor;
        [Tooltip("0 grows straight up (or down from ceilings), 1 along the surface's normal. Wall formations always follow the normal.")]
        [SerializeField, Range(0f, 1f)] private float _align = 0.2f;
        [SerializeField, Min(0.01f)] private float _minScale = 0.6f;
        [SerializeField, Min(0.01f)] private float _maxScale = 1.4f;
        [Tooltip("How far the base sinks into the rock, as a fraction of the mesh's height.")]
        [SerializeField, Range(0f, 0.5f)] private float _sink = 0.1f;
        [Tooltip("How far it is drawn, in metres.")]
        [SerializeField, Min(1f)] private float _drawDistance = 80f;
        [SerializeField] private bool _castShadows = true;
        [Tooltip("Rendering layers it draws on (for light layers and screen-space effects that mask by them).")]
        [SerializeField] private RenderingLayerMask _renderingLayers = RenderingLayerMask.defaultRenderingLayerMask;
        [Tooltip("Collides as its mesh, turned and scaled like each instance.")]
        [SerializeField] private bool _collider;

        public Mesh Mesh => _mesh;
        public Material[] Materials => _materials;
        public StrataFormationSurface Surface => _surface;
        public float Align => _align;
        public float MinScale => _minScale;
        public float MaxScale => Mathf.Max(_minScale, _maxScale);
        public float Sink => _sink;
        public float DrawDistance => _drawDistance;
        public bool CastShadows => _castShadows;
        public uint RenderingLayers => _renderingLayers;
        public bool Collider => _collider;
        public bool IsValid => _mesh != null && _materials != null && _materials.Length > 0;
    }
}
