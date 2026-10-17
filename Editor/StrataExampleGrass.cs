using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata.Editor
{
    // Makes a few low-poly grass prototypes to test with: a short clump, tall grass and two flowers. Each is a small
    // flat-shaded mesh whose vertex colour is its albedo (sRGB) with alpha the wind sway (rising up the blade), and uv x
    // 1 marking foliage; normals lean upwards so clumps light softly. They share a two-sided Strata/Tree material.
    public static class StrataExampleGrass
    {
        [MenuItem("Tools/Strata/Generate Example Grass")]
        private static void GenerateFromMenu()
        {
            string folder = EditorUtility.SaveFolderPanel("Example grass folder", "Assets", "");
            if (string.IsNullOrEmpty(folder)) return;
            string relative = FileUtil.GetProjectRelativePath(folder);
            if (string.IsNullOrEmpty(relative))
            {
                Debug.LogError("Choose a folder inside the project.");
                return;
            }
            Debug.Log($"Strata: generated {Generate(relative).Count} example grass prototypes in {relative}.");
        }

        public static List<StrataGrassPrototype> Generate(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                string parent = System.IO.Path.GetDirectoryName(folder)!.Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
            }
            Material material = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/Grass.mat");
            if (material == null)
            {
                material = new Material(Shader.Find(StrataTreeBuilder.TreeShader)) { name = "Grass" };
                AssetDatabase.CreateAsset(material, $"{folder}/Grass.mat");
            }
            material.SetFloat("_VertexColour", 1f);
            material.SetFloat("_FoliageSource", 1f);
            material.SetFloat("_WindSource", 0f);
            material.SetFloat("_WindStrength", 0.12f);
            material.SetFloat("_WindSpeed", 1.6f);
            material.SetFloat("_WindScale", 0.05f);
            material.SetFloat("_Translucency", 0.5f);
            material.SetFloat("_HueVariation", 0.15f);
            material.SetFloat("_BrightnessVariation", 0.1f);
            material.SetFloat("_Smoothness", 0.05f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            EditorUtility.SetDirty(material);

            Color baseGreen = new(0.16f, 0.30f, 0.08f), tipGreen = new(0.45f, 0.62f, 0.22f);
            List<StrataGrassPrototype> prototypes = new()
            {
                Save(folder, "Grass Clump", Clump(11, 0.22f, new Vector2(0.3f, 0.55f), baseGreen, tipGreen, 1), material, 6f, 90f, new Vector2(0.8f, 1.25f)),
                Save(folder, "Tall Grass", Clump(7, 0.18f, new Vector2(0.7f, 1.1f), new Color(0.24f, 0.30f, 0.10f), new Color(0.70f, 0.66f, 0.32f), 2),
                     material, 2f, 110f, new Vector2(0.8f, 1.3f)),
                Save(folder, "Yellow Flowers", Flowers(new Color(0.95f, 0.78f, 0.15f), 3), material, 0.6f, 60f, new Vector2(0.8f, 1.2f)),
                Save(folder, "White Flowers", Flowers(new Color(0.92f, 0.92f, 0.86f), 4), material, 0.6f, 60f, new Vector2(0.8f, 1.2f))
            };
            AssetDatabase.SaveAssets();
            return prototypes;
        }

        private static StrataGrassPrototype Save(string folder, string name, Mesh mesh, Material material, float density, float range, Vector2 scale)
        {
            string meshPath = $"{folder}/{name} Mesh.asset";
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);
                mesh = existing;
            }
            else AssetDatabase.CreateAsset(mesh, meshPath);

            string path = $"{folder}/{name}.asset";
            StrataGrassPrototype prototype = AssetDatabase.LoadAssetAtPath<StrataGrassPrototype>(path);
            if (prototype == null)
            {
                prototype = ScriptableObject.CreateInstance<StrataGrassPrototype>();
                AssetDatabase.CreateAsset(prototype, path);
            }
            prototype.Set(mesh, new[] { material }, density, range, scale);
            EditorUtility.SetDirty(prototype);
            return prototype;
        }

        // Blades fanned round a small circle, each a two-segment strip narrowing to a tip and curving outwards.
        private static Mesh Clump(int blades, float radius, Vector2 height, Color root, Color tip, int seed)
        {
            System.Random random = new(seed);
            MeshParts parts = new();
            for (int b = 0; b < blades; b++)
            {
                float angle = (b + (float)random.NextDouble() * 0.6f) / blades * Mathf.PI * 2f;
                Vector3 outward = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                Vector3 side = Vector3.Cross(Vector3.up, outward);
                Vector3 foot = outward * radius * (float)random.NextDouble();
                float h = Mathf.Lerp(height.x, height.y, (float)random.NextDouble());
                float width = 0.035f + 0.02f * (float)random.NextDouble();
                float bend = 0.15f + 0.25f * (float)random.NextDouble();
                Vector3 mid = foot + Vector3.up * h * 0.55f + outward * h * bend * 0.35f;
                Vector3 top = foot + Vector3.up * h + outward * h * bend;
                Color middle = Color.Lerp(root, tip, 0.55f);
                parts.Quad(foot - side * width, foot + side * width, mid + side * width * 0.6f, mid - side * width * 0.6f, root, middle, 0f, 0.3f);
                parts.Triangle(mid - side * width * 0.6f, mid + side * width * 0.6f, top, middle, tip, 0.3f, 1f);
            }
            return parts.Build("Grass");
        }

        // A few stems with a small pyramid of petals each.
        private static Mesh Flowers(Color petal, int seed)
        {
            System.Random random = new(seed);
            MeshParts parts = new();
            Color stem = new(0.22f, 0.36f, 0.10f);
            for (int f = 0; f < 3; f++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                Vector3 foot = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 0.12f * (float)random.NextDouble();
                float h = 0.3f + 0.2f * (float)random.NextDouble();
                Vector3 top = foot + new Vector3(0.03f, h, 0.02f);
                Vector3 side = new(0.012f, 0f, 0f);
                parts.Triangle(foot - side, foot + side, top, stem, stem, 0f, 1f);
                float size = 0.045f;
                Vector3 crown = top + Vector3.up * size * 0.6f;
                for (int p = 0; p < 5; p++)
                {
                    float a0 = p / 5f * Mathf.PI * 2f, a1 = (p + 1) / 5f * Mathf.PI * 2f;
                    Vector3 e0 = top + new Vector3(Mathf.Cos(a0), 0.2f, Mathf.Sin(a0)) * size;
                    Vector3 e1 = top + new Vector3(Mathf.Cos(a1), 0.2f, Mathf.Sin(a1)) * size;
                    parts.Triangle(e0, e1, crown, petal, petal, 1f, 1f);
                }
            }
            return parts.Build("Flowers");
        }

        private sealed class MeshParts
        {
            private readonly List<Vector3> _positions = new();
            private readonly List<Vector3> _normals = new();
            private readonly List<Color> _colours = new();
            private readonly List<Vector2> _uvs = new();

            // Flat shaded, normals pulled upwards; colour from bottom (a, b) to top (c), sway from the bottom's to the top's.
            public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color bottom, Color top, float swayBottom, float swayTop)
            {
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                if (normal.y < 0f) normal = -normal;
                normal = Vector3.Lerp(normal, Vector3.up, 0.6f).normalized;
                Add(a, normal, bottom, swayBottom);
                Add(b, normal, bottom, swayBottom);
                Add(c, normal, top, swayTop);
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color bottom, Color top, float swayBottom, float swayTop)
            {
                Triangle(a, b, c, bottom, top, swayBottom, swayTop);
                Vector3 normal = Vector3.Cross(c - a, d - a).normalized;
                if (normal.y < 0f) normal = -normal;
                normal = Vector3.Lerp(normal, Vector3.up, 0.6f).normalized;
                Add(a, normal, bottom, swayBottom);
                Add(c, normal, top, swayTop);
                Add(d, normal, top, swayTop);
            }

            private void Add(Vector3 position, Vector3 normal, Color colour, float sway)
            {
                _positions.Add(position);
                _normals.Add(normal);
                colour.a = sway;
                _colours.Add(colour);
                _uvs.Add(new Vector2(1f, 0f));
            }

            public Mesh Build(string name)
            {
                Mesh mesh = new() { name = name };
                mesh.SetVertices(_positions);
                mesh.SetNormals(_normals);
                mesh.SetColors(_colours);
                mesh.SetUVs(0, _uvs);
                int[] indices = new int[_positions.Count];
                for (int i = 0; i < indices.Length; i++) indices[i] = i;
                mesh.SetIndices(indices, MeshTopology.Triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
