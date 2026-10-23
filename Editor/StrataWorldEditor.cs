using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Strata.Editor
{
    // The StrataWorld inspector: its fields and blocks, any problems packing the palette's layer textures into arrays
    // with a button that gives every layer texture the import settings packing needs (one size, mipmaps, albedo as sRGB
    // BC7, normals as BC5 normal maps, masks as linear BC7), the live preview switch, Bake, and what the grid costs.
    [CustomEditor(typeof(StrataWorld))]
    public class StrataWorldEditor : UnityEditor.Editor
    {
        private static readonly int[] Sizes = { 512, 1024, 2048, 4096 };
        private int _size = 1024;
        // the packing check, redone only when the palette or a layer changes (packing for real on every repaint copies
        // every texture and reads colours back from the GPU, which stalls the inspector)
        private int _checkedVersion = int.MinValue;
        private List<string> _problems = new();
        private Vector2Int _albedoSize;

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
            PaletteGUI(world.Palette);

            EditorGUILayout.Space();
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
            if (world.Caves != null && (world.Caves.BrickCount > 0 || world.Heightfield.Holes.HoledCount > 0))
            {
                EditorGUILayout.LabelField("Caves", $"{world.Caves.BrickCount} bricks, {world.Caves.TriangleCount} triangles, {world.Heightfield.Holes.HoledCount} holed tiles");
                EditorGUILayout.LabelField("Last Cave Preview", $"{StrataPreview.LastCaveMilliseconds:0.0} ms ({StrataPreview.Caves.LastMilliseconds:0.0} ms meshing)");
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

        private void PaletteGUI(StrataPalette palette)
        {
            if (palette.Version != _checkedVersion)
            {
                _checkedVersion = palette.Version;
                _problems = StrataPaletteTextures.Check(palette, out _albedoSize);
            }
            if (_problems.Count == 0)
                EditorGUILayout.HelpBox($"{palette.Layers.Count} layers pack into {_albedoSize.x}x{_albedoSize.y} arrays.", MessageType.None);
            foreach (string problem in _problems) EditorGUILayout.HelpBox(problem, MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                _size = EditorGUILayout.IntPopup("Texture Size", _size, System.Array.ConvertAll(Sizes, s => s.ToString()), Sizes);
                if (GUILayout.Button("Fix Import Settings"))
                {
                    Debug.Log(FixImportSettings(palette, _size), target);
                    _checkedVersion = int.MinValue;
                }
            }
        }

        public static string FixImportSettings(StrataPalette palette, int size)
        {
            int changed = 0;
            foreach (StrataLayer layer in palette.Layers)
            {
                if (layer == null) continue;
                changed += Fix(layer.Albedo, size, TextureImporterType.Default, true, TextureImporterFormat.BC7);
                changed += Fix(layer.Normal, size, TextureImporterType.NormalMap, false, TextureImporterFormat.BC5);
                changed += Fix(layer.Mask, size, TextureImporterType.Default, false, TextureImporterFormat.BC7);
            }
            return $"Strata: set import settings on {changed} palette textures ({size}x{size}).";
        }

        private static int Fix(Texture2D texture, int size, TextureImporterType type, bool srgb, TextureImporterFormat format)
        {
            if (texture == null || AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is not TextureImporter importer) return 0;
            importer.textureType = type;
            importer.sRGBTexture = srgb;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.npotScale = TextureImporterNPOTScale.ToNearest;
            importer.maxTextureSize = size;
            if (type == TextureImporterType.Default) importer.alphaSource = TextureImporterAlphaSource.FromInput;
            // the default platform only takes a quality, so the exact format goes in the desktop overrides
            TextureImporterPlatformSettings settings = importer.GetDefaultPlatformTextureSettings();
            settings.maxTextureSize = size;
            settings.format = TextureImporterFormat.Automatic;
            settings.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SetPlatformTextureSettings(settings);
            foreach (string platform in new[] { "Standalone", "Windows Store Apps" })
            {
                TextureImporterPlatformSettings platformSettings = importer.GetPlatformTextureSettings(platform);
                platformSettings.overridden = true;
                platformSettings.maxTextureSize = size;
                platformSettings.format = format;
                importer.SetPlatformTextureSettings(platformSettings);
            }
            importer.SaveAndReimport();
            return 1;
        }
    }
}
