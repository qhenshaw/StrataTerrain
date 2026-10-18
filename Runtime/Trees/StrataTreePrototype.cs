using System;
using UnityEngine;

namespace Strata
{
    // One kind of tree (or bush) Strata can place: the mesh and Strata/Tree materials of each LOD and the distance it
    // reaches, the impostor drawn beyond the last mesh LOD, shadow settings, and its collider: a trunk capsule, or for
    // rocks and other solid shapes a mesh turned and scaled with each instance.
    // It is filled from a source LODGroup prefab by the prototype's inspector (Rebuild From Source, Bake Impostor);
    // the source prefab itself is never drawn. Distances are for a tree of scale 1 at a 60 degree field of view.
    public enum StrataColliderKind
    {
        Capsule,
        Mesh
    }

    [CreateAssetMenu(menuName = "Strata/Tree Prototype", fileName = "StrataTreePrototype")]
    public class StrataTreePrototype : ScriptableObject
    {
        public const int MaxLods = 4;

        [Serializable]
        public struct Lod
        {
            public Mesh Mesh;
            [Tooltip("One per submesh, using the Strata/Tree shader.")]
            public Material[] Materials;
            [Tooltip("How far this LOD is drawn, in metres.")]
            [Min(1f)] public float Distance;
        }

        [SerializeField] private GameObject _source;
        [SerializeField] private Lod[] _lods = Array.Empty<Lod>();
        [Tooltip("Fraction of each LOD's distance over which it crossfades into the next.")]
        [SerializeField, Range(0f, 0.5f)] private float _crossfade = 0.1f;

        [Header("Impostor")]
        [SerializeField] private Material _impostorMaterial;
        [Tooltip("How far the impostor is drawn, in metres; beyond this the tree is culled.")]
        [SerializeField, Min(1f)] private float _impostorDistance = 2000f;
        [Tooltip("The baked frames' bounding sphere in the tree's own space: centre and radius.")]
        [SerializeField] private Vector4 _impostorBounds = new(0f, 5f, 0f, 6f);
        [Tooltip("Darkens or brightens the impostor to match the meshes, which shade themselves where impostors can't.")]
        [SerializeField, Range(0.2f, 1.5f)] private float _impostorBrightness = 1f;

        [Header("Shadows")]
        [SerializeField] private bool _castShadows = true;
        [Tooltip("Impostors cast shadows too (flat cards facing the light).")]
        [SerializeField] private bool _impostorShadows = true;

        [Header("Collision")]
        [SerializeField] private bool _collider = true;
        [SerializeField] private StrataColliderKind _colliderKind = StrataColliderKind.Capsule;
        [SerializeField] private Mesh _colliderMesh;
        [SerializeField, Min(0.01f)] private float _colliderRadius = 0.3f;
        [SerializeField, Min(0.02f)] private float _colliderHeight = 4f;

        [Header("Bounds")]
        [Tooltip("Height of the tree above its base, and its horizontal reach from the trunk, at scale 1.")]
        [SerializeField, Min(0.1f)] private float _height = 10f;
        [SerializeField, Min(0.1f)] private float _reach = 4f;

        public GameObject Source { get => _source; set => _source = value; }
        public Lod[] Lods => _lods;
        public int LodCount => _lods.Length;
        public float Crossfade => _crossfade;
        public Material ImpostorMaterial => _impostorMaterial;
        public bool HasImpostor => _impostorMaterial != null;
        public float ImpostorDistance => _impostorDistance;
        public Vector4 ImpostorBounds => _impostorBounds;
        public float ImpostorBrightness => _impostorBrightness;
        public bool CastShadows => _castShadows;
        public bool ImpostorShadows => _impostorShadows;
        public bool HasCollider => _collider && (_colliderKind == StrataColliderKind.Capsule || _colliderMesh != null);
        public StrataColliderKind ColliderKind => _colliderKind;
        public Mesh ColliderMesh => _colliderMesh;
        public float ColliderRadius => _colliderRadius;
        public float ColliderHeight => _colliderHeight;
        public float Height => _height;
        public float Reach => _reach;
        // How far anything of this tree is drawn.
        public float MaxDistance => HasImpostor ? _impostorDistance : _lods.Length > 0 ? _lods[^1].Distance : 0f;
        public bool IsValid => _lods.Length > 0 && Array.TrueForAll(_lods, l => l.Mesh != null && l.Materials != null && l.Materials.Length >= l.Mesh.subMeshCount);
        public int Version { get; private set; }

        public void SetLods(Lod[] lods, float height, float reach)
        {
            _lods = lods;
            _height = height;
            _reach = reach;
            Version++;
        }

        public void SetMeshCollider(Mesh mesh)
        {
            _collider = mesh != null;
            _colliderKind = StrataColliderKind.Mesh;
            _colliderMesh = mesh;
            Version++;
        }

        public void SetCollider(bool enabled, float radius, float height)
        {
            _collider = enabled;
            _colliderKind = StrataColliderKind.Capsule;
            _colliderRadius = radius;
            _colliderHeight = height;
            Version++;
        }

        public void SetImpostor(Material material, Vector4 bounds)
        {
            _impostorMaterial = material;
            _impostorBounds = bounds;
            Version++;
        }

        private void OnValidate()
        {
            if (_impostorMaterial != null && _impostorMaterial.HasProperty("_Brightness")) _impostorMaterial.SetFloat("_Brightness", _impostorBrightness);
            if (_lods.Length > MaxLods) Array.Resize(ref _lods, MaxLods);
            for (int i = 1; i < _lods.Length; i++) _lods[i].Distance = Mathf.Max(_lods[i].Distance, _lods[i - 1].Distance + 1f);
            if (_lods.Length > 0) _impostorDistance = Mathf.Max(_impostorDistance, _lods[^1].Distance + 1f);
            Version++;
        }
    }
}
