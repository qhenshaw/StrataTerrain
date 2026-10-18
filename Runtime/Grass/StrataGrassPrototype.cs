using UnityEngine;

namespace Strata
{
    // One kind of grass or small detail (a clump of blades, a flower, a fern): a small mesh drawn with a Strata/Tree
    // material, how many clumps a square metre holds at full density, how far it is drawn (thinning out, with the
    // remaining clumps growing, past 30% of the range), and how clumps vary. Clumps are generated on the GPU around each camera every frame from the
    // world's grass density maps, so nothing per clump is stored.
    [CreateAssetMenu(menuName = "Strata/Grass Prototype", fileName = "StrataGrassPrototype")]
    public class StrataGrassPrototype : ScriptableObject
    {
        [SerializeField] private Mesh _mesh;
        [Tooltip("One per submesh, using the Strata/Tree shader.")]
        [SerializeField] private Material[] _materials = new Material[1];
        [Tooltip("Clumps per square metre at full density.")]
        [SerializeField, Range(0.05f, 12f)] private float _density = 4f;
        [Tooltip("How far clumps are drawn, in metres; past 30% of it they thin out, the rest growing to keep the ground covered.")]
        [SerializeField, Range(5f, 300f)] private float _range = 100f;
        [SerializeField] private Vector2 _scale = new(0.8f, 1.2f);
        [Tooltip("How far clumps tilt towards the ground's normal (0 upright, 1 fully).")]
        [SerializeField, Range(0f, 1f)] private float _alignToGround = 0.6f;
        [Tooltip("Random lean, in degrees.")]
        [SerializeField, Range(0f, 15f)] private float _lean = 5f;

        public Mesh Mesh => _mesh;
        public Material[] Materials => _materials;
        public float Density => _density;
        public float Range => _range;
        public Vector2 Scale => _scale;
        public float AlignToGround => _alignToGround;
        public float Lean => _lean;
        public bool IsValid => _mesh != null && _materials != null && _materials.Length >= _mesh.subMeshCount &&
                               System.Array.TrueForAll(_materials, m => m != null);
        public int Version { get; private set; }

        public void Set(Mesh mesh, Material[] materials, float density, float range, Vector2 scale)
        {
            _mesh = mesh;
            _materials = materials;
            _density = density;
            _range = range;
            _scale = scale;
            Version++;
        }

        private void OnValidate() => Version++;
    }
}
