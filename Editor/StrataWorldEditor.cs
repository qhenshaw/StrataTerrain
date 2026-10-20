using UnityEditor;
using UnityEngine;

namespace Strata.Editor
{
    // The StrataWorld inspector: its fields, the live preview switch, Bake, and what the grid costs.
    [CustomEditor(typeof(StrataWorld))]
    public class StrataWorldEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            StrataWorld world = (StrataWorld)target;
            if (EditorGUI.EndChangeCheck() && !Application.isPlaying)
            {
                serializedObject.ApplyModifiedProperties();
                world.Rebuild();
            }

            EditorGUILayout.Space();
            if (world.Settings == null)
            {
                EditorGUILayout.HelpBox("Assign a Strata World Settings asset (Create > Strata > World Settings).", MessageType.Info);
                return;
            }
            StrataGrid grid = world.Settings.Grid;
            EditorGUILayout.HelpBox($"{grid}\n{StrataBaker.Estimate(grid)}", MessageType.None);

            bool preview = EditorGUILayout.Toggle("Live Preview", StrataPreview.Enabled);
            if (preview != StrataPreview.Enabled) StrataPreview.Enabled = preview;
            if (StrataPreview.LastArea.width > 0)
                EditorGUILayout.LabelField("Last Preview", $"{StrataPreview.LastArea.width}x{StrataPreview.LastArea.height} samples, {StrataPreview.LastMilliseconds:0.0} ms (CPU)");

            if (world.Renderer != null) EditorGUILayout.LabelField("Last Cull", $"{world.Renderer.LastCullMilliseconds:0.000} ms per view");
            if (world.Trees != null && world.TreeRenderer != null)
            {
                EditorGUILayout.LabelField("Trees", $"{world.Trees.Count} of {world.Trees.Prototypes.Count} prototypes, {world.TreeRenderer.GpuBytes / (1024f * 1024f):0.0} MB GPU");
                EditorGUILayout.LabelField("Last Tree Cull", $"{world.TreeRenderer.LastCullMilliseconds:0.000} ms, {world.TreeRenderer.LastVisibleTrees} drawn");
                EditorGUILayout.LabelField("Last Tree Placement", $"{StrataPreview.Trees.LastPlaceMilliseconds:0.0} ms");
            }
            if (world.Grass != null)
                EditorGUILayout.LabelField("Grass", $"{world.GrassPrototypes.Count} prototypes, {world.Grass.Texture.volumeDepth * (long)world.Grass.Texture.width * world.Grass.Texture.height / (1024f * 1024f):0.0} MB density maps");

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Bake")) Debug.Log(StrataBaker.Bake(world), world);
                if (GUILayout.Button("Re-evaluate All")) StrataPreview.RefreshAll();
                using (new EditorGUILayout.HorizontalScope())
                using (new EditorGUI.DisabledScope(StrataProbeBaker.IsBaking))
                {
                    if (GUILayout.Button("Bake Probes")) Debug.Log(StrataProbeBaker.Bake(world), world);
                    if (GUILayout.Button("Clear Probes")) Debug.Log(StrataProbeBaker.Clear(world), world);
                }
            }
        }
    }
}
