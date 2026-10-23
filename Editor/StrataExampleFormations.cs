using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Strata.Editor
{
    // Makes a few placeholder cave formation prototypes to test with: a stalactite (hangs from ceilings), a stalagmite
    // (grows on floors) and a crystal cluster (grows out of walls), each a small flat-shaded mesh of a few hundred
    // vertices growing up its +Y axis, with URP/Lit materials (the crystals glow faintly).
    public static class StrataExampleFormations
    {
        [MenuItem("Tools/Strata/Generate Example Formations")]
        private static void GenerateFromMenu()
        {
            string folder = EditorUtility.SaveFolderPanel("Example formations folder", "Assets", "");
            if (string.IsNullOrEmpty(folder)) return;
            string relative = FileUtil.GetProjectRelativePath(folder);
            if (string.IsNullOrEmpty(relative))
            {
                Debug.LogError("Choose a folder inside the project.");
                return;
            }
            Debug.Log($"Strata: generated {Generate(relative).Count} example formation prototypes in {relative}.");
        }

        public static List<StrataFormationPrototype> Generate(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                string parent = System.IO.Path.GetDirectoryName(folder)!.Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
            }
            Material rock = MakeMaterial(folder, "Formation Rock", new Color(0.55f, 0.5f, 0.44f), 0.25f, Color.black);
            Material crystal = MakeMaterial(folder, "Formation Crystal", new Color(0.35f, 0.75f, 0.85f), 0.85f, new Color(0.05f, 0.25f, 0.3f));
            List<StrataFormationPrototype> prototypes = new()
            {
                Save(folder, "Stalactite", Spike(9, 0.35f, 2.4f, 1), rock, StrataFormationSurface.Ceiling, 0.1f, new Vector2(0.5f, 1.6f), 0.15f, false),
                Save(folder, "Stalagmite", Spike(9, 0.5f, 1.6f, 2), rock, StrataFormationSurface.Floor, 0.15f, new Vector2(0.6f, 1.5f), 0.1f, true),
                Save(folder, "Crystal Cluster", Crystals(3), crystal, StrataFormationSurface.Wall, 1f, new Vector2(0.6f, 1.4f), 0.15f, false)
            };
            AssetDatabase.SaveAssets();
            return prototypes;
        }

        private static Material MakeMaterial(string folder, string name, Color colour, float smoothness, Color emission)
        {
            string path = $"{folder}/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetColor("_BaseColor", colour);
            material.SetFloat("_Smoothness", smoothness);
            if (emission.maxColorComponent > 0f)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static StrataFormationPrototype Save(string folder, string name, Mesh mesh, Material material, StrataFormationSurface surface,
                                                     float align, Vector2 scale, float sink, bool collider)
        {
            string meshPath = $"{folder}/{name} Mesh.asset";
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing != null)
            {
                existing.Clear();
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);
                mesh = existing;
            }
            else AssetDatabase.CreateAsset(mesh, meshPath);

            string path = $"{folder}/{name}.asset";
            StrataFormationPrototype prototype = AssetDatabase.LoadAssetAtPath<StrataFormationPrototype>(path);
            if (prototype == null)
            {
                prototype = ScriptableObject.CreateInstance<StrataFormationPrototype>();
                AssetDatabase.CreateAsset(prototype, path);
            }
            SerializedObject serialized = new(prototype);
            serialized.FindProperty("_mesh").objectReferenceValue = mesh;
            SerializedProperty materials = serialized.FindProperty("_materials");
            materials.arraySize = 1;
            materials.GetArrayElementAtIndex(0).objectReferenceValue = material;
            serialized.FindProperty("_surface").enumValueIndex = (int)surface;
            serialized.FindProperty("_align").floatValue = align;
            serialized.FindProperty("_minScale").floatValue = scale.x;
            serialized.FindProperty("_maxScale").floatValue = scale.y;
            serialized.FindProperty("_sink").floatValue = sink;
            serialized.FindProperty("_collider").boolValue = collider;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return prototype;
        }

        // A jagged, tapering cone up +Y: rings of jittered points, flat shaded.
        private static Mesh Spike(int sides, float radius, float height, int seed)
        {
            Random.State state = Random.state;
            Random.InitState(seed);
            const int rings = 5;
            Vector3[,] points = new Vector3[rings, sides];
            for (int r = 0; r < rings; r++)
            {
                float t = r / (float)(rings - 1);
                float ringRadius = radius * Mathf.Pow(1f - t, 1.4f) + 0.02f;
                for (int s = 0; s < sides; s++)
                {
                    float angle = (s + Random.Range(-0.25f, 0.25f)) / sides * Mathf.PI * 2f;
                    float jitter = Random.Range(0.8f, 1.15f);
                    points[r, s] = new Vector3(Mathf.Cos(angle) * ringRadius * jitter, t * height * Random.Range(0.97f, 1.03f), Mathf.Sin(angle) * ringRadius * jitter);
                }
            }
            Random.state = state;
            List<Vector3> vertices = new();
            for (int r = 0; r < rings - 1; r++)
            for (int s = 0; s < sides; s++)
            {
                int n = (s + 1) % sides;
                Quad(vertices, points[r, s], points[r + 1, s], points[r + 1, n], points[r, n]);
            }
            Vector3 tip = new(0f, height * 1.04f, 0f);
            for (int s = 0; s < sides; s++) Triangle(vertices, points[rings - 1, s], tip, points[rings - 1, (s + 1) % sides]);
            return Build(vertices, "Spike");
        }

        // A few hexagonal crystals with pointed tips, splayed out from the base.
        private static Mesh Crystals(int seed)
        {
            Random.State state = Random.state;
            Random.InitState(seed);
            List<Vector3> vertices = new();
            for (int c = 0; c < 5; c++)
            {
                float radius = Random.Range(0.08f, 0.16f), length = Random.Range(0.5f, 1.1f);
                Quaternion lean = Quaternion.Euler(Random.Range(-35f, 35f), Random.Range(0f, 360f), Random.Range(-35f, 35f));
                if (c == 0) lean = Quaternion.identity;
                Vector3[] bottom = new Vector3[6], top = new Vector3[6];
                for (int s = 0; s < 6; s++)
                {
                    float angle = s / 6f * Mathf.PI * 2f;
                    Vector3 ring = new(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                    bottom[s] = lean * (ring + Vector3.down * 0.1f);
                    top[s] = lean * (ring + Vector3.up * length);
                }
                Vector3 tip = lean * (Vector3.up * (length + radius * 2.5f));
                for (int s = 0; s < 6; s++)
                {
                    int n = (s + 1) % 6;
                    Quad(vertices, bottom[s], top[s], top[n], bottom[n]);
                    Triangle(vertices, top[s], tip, top[n]);
                }
            }
            Random.state = state;
            return Build(vertices, "Crystals");
        }

        // clockwise seen from outside, so the faces point out
        private static void Quad(List<Vector3> vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Triangle(vertices, a, b, c);
            Triangle(vertices, a, c, d);
        }

        private static void Triangle(List<Vector3> vertices, Vector3 a, Vector3 b, Vector3 c)
        {
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
        }

        private static Mesh Build(List<Vector3> vertices, string name)
        {
            int[] indices = new int[vertices.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            Mesh mesh = new() { name = name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
