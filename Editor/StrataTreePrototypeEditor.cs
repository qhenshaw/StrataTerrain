using UnityEditor;
using UnityEngine;

namespace Strata.Editor
{
    // The tree prototype inspector: its fields, what its LODs cost, and buttons to rebuild it from its source prefab
    // and to bake its impostor. Assets > Strata > Create Tree Prototypes makes prototypes for the selected prefabs.
    [CustomEditor(typeof(StrataTreePrototype))]
    public class StrataTreePrototypeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            StrataTreePrototype prototype = (StrataTreePrototype)target;
            EditorGUILayout.Space();
            string lods = "";
            foreach (StrataTreePrototype.Lod lod in prototype.Lods)
                lods += lod.Mesh != null ? $"{lod.Mesh.vertexCount} verts, {lod.Mesh.subMeshCount} submeshes to {lod.Distance:0} m\n" : "missing mesh\n";
            EditorGUILayout.HelpBox(lods + (prototype.HasImpostor ? $"impostor to {prototype.ImpostorDistance:0} m" : "no impostor: culled after the last LOD"), MessageType.None);
            using (new EditorGUI.DisabledScope(prototype.Source == null))
            {
                if (GUILayout.Button("Rebuild From Source")) Debug.Log(StrataTreeBuilder.Rebuild(prototype), prototype);
            }
            using (new EditorGUI.DisabledScope(!prototype.IsValid))
            {
                if (GUILayout.Button("Bake Impostor")) Debug.Log(StrataTreeBuilder.BakeImpostor(prototype), prototype);
            }
        }

        [MenuItem("Assets/Strata/Create Tree Prototypes", false, 30)]
        private static void CreateFromSelection()
        {
            foreach (GameObject prefab in Selection.GetFiltered<GameObject>(SelectionMode.Assets))
            {
                string folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(prefab))!.Replace('\\', '/') + "/Strata Trees";
                StrataTreePrototype prototype = StrataTreeBuilder.CreatePrototype(prefab, folder);
                Debug.Log($"Strata: created tree prototype {AssetDatabase.GetAssetPath(prototype)}", prototype);
            }
        }

        // grass prototypes, at a density that roughly covers the ground once with clumps of this size
        [MenuItem("Assets/Strata/Create Grass Prototypes", false, 31)]
        private static void CreateGrassFromSelection()
        {
            foreach (GameObject prefab in Selection.GetFiltered<GameObject>(SelectionMode.Assets))
            {
                MeshFilter filter = prefab.GetComponentInChildren<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                Vector3 size = filter.sharedMesh.bounds.size;
                float density = Mathf.Clamp(0.6f / Mathf.Max(size.x * size.z, 0.01f), 0.05f, 8f);
                string folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(prefab))!.Replace('\\', '/') + "/Strata Grass";
                StrataGrassPrototype prototype = StrataTreeBuilder.CreateGrassPrototype(prefab, folder, density, 80f, new Vector2(0.8f, 1.2f));
                Debug.Log($"Strata: created grass prototype {AssetDatabase.GetAssetPath(prototype)} at {density:0.##} per square metre", prototype);
            }
        }

        [MenuItem("Assets/Strata/Create Grass Prototypes", true)]
        private static bool CanCreateGrassFromSelection() => Selection.GetFiltered<GameObject>(SelectionMode.Assets).Length > 0;

        [MenuItem("Assets/Strata/Create Tree Prototypes", true)]
        private static bool CanCreateFromSelection() => Selection.GetFiltered<GameObject>(SelectionMode.Assets).Length > 0;
    }
}
