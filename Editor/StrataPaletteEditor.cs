using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Strata.Editor
{
    // The StrataPalette inspector: its layer list, any problems packing the layers' textures into arrays, and a button
    // that gives every layer texture the import settings packing needs (one size, mipmaps, albedo as sRGB BC7, normals
    // as BC5 normal maps, masks as linear BC7).
    [CustomEditor(typeof(StrataPalette))]
    public class StrataPaletteEditor : UnityEditor.Editor
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
            DrawDefaultInspector();
            StrataPalette palette = (StrataPalette)target;

            EditorGUILayout.Space();
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
                    Debug.Log(FixImportSettings(palette, _size), palette);
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
            return $"Strata: set import settings on {changed} textures of {palette.name} ({size}x{size}).";
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
