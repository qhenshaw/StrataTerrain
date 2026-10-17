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

        [MenuItem("Assets/Strata/Create Tree Prototypes", true)]
        private static bool CanCreateFromSelection() => Selection.GetFiltered<GameObject>(SelectionMode.Assets).Length > 0;
    }
}
