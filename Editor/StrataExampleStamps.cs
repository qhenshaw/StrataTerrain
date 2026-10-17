using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Strata.Editor
{
    // Writes a few procedural height stamps (mountain, crater, mesa, ridge) as 16-bit grayscale PNGs and sets them to
    // import as linear, uncompressed R16, the way stamp textures should be imported. Each fades to black at its edges.
    public static class StrataExampleStamps
    {
        private const int Size = 512;

        [MenuItem("Tools/Strata/Generate Example Stamps")]
        private static void GenerateFromMenu()
        {
            string folder = EditorUtility.SaveFolderPanel("Example stamp folder", "Assets", "");
            if (string.IsNullOrEmpty(folder)) return;
            string relative = FileUtil.GetProjectRelativePath(folder);
            if (string.IsNullOrEmpty(relative))
            {
                Debug.LogError("Choose a folder inside the project.");
                return;
            }
            Debug.Log(Generate(relative));
        }

        public static string Generate(string folder)
        {
            Directory.CreateDirectory(folder);
            Write(folder, "Mountain", uv =>
            {
                float r = math.length(uv);
                float n = Noise(uv * 6f) * 0.15f + Noise(uv * 17f) * 0.05f;
                return math.pow(math.saturate(1f - r + n), 2.2f);
            });
            Write(folder, "Crater", uv =>
            {
                float r = math.length(uv) + Noise(uv * 9f) * 0.04f;
                float rim = math.exp(-math.pow((r - 0.55f) / 0.12f, 2f));
                float bowl = math.saturate(1f - r / 0.55f);
                return math.saturate(rim * 0.9f - bowl * bowl * 0.25f + 0.25f) * math.saturate((1f - r) * 4f);
            });
            Write(folder, "Mesa", uv =>
            {
                float r = math.length(uv) + Noise(uv * 5f) * 0.12f;
                float top = math.smoothstep(0.62f, 0.5f, r);
                float slope = math.smoothstep(0.95f, 0.6f, r) * 0.25f;
                return math.saturate(math.max(top, slope) * 0.95f + Noise(uv * 23f) * 0.02f);
            });
            Write(folder, "Ridge", uv =>
            {
                float across = math.abs(uv.y + Noise(new float2(uv.x * 3f, 0f)) * 0.15f);
                float along = math.saturate(1f - math.abs(uv.x) * 1.1f);
                float crest = math.exp(-across * across / 0.03f);
                return math.saturate(crest * math.pow(along, 0.6f) * (0.8f + Noise(uv * 12f) * 0.2f));
            });
            AssetDatabase.Refresh();
            foreach (string name in new[] { "Mountain", "Crater", "Mesa", "Ridge" }) ConfigureImport($"{folder}/{name}.png");
            return $"Strata: wrote 4 example stamps ({Size}x{Size}, 16-bit) to {folder}";
        }

        private static void Write(string folder, string name, System.Func<float2, float> height)
        {
            Texture2D texture = new(Size, Size, GraphicsFormat.R16_UNorm, TextureCreationFlags.None);
            ushort[] pixels = new ushort[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float2 uv = (new float2(x, y) + 0.5f) / Size * 2f - 1f;
                float edge = math.saturate((1f - math.max(math.abs(uv.x), math.abs(uv.y))) * 8f);
                pixels[y * Size + x] = (ushort)math.round(math.saturate(height(uv) * edge) * 65535f);
            }
            texture.SetPixelData(pixels, 0);
            texture.Apply();
            File.WriteAllBytes($"{folder}/{name}.png", texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
        }

        private static void ConfigureImport(string path)
        {
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer) return;
            importer.textureType = TextureImporterType.SingleChannel;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            TextureImporterPlatformSettings settings = importer.GetDefaultPlatformTextureSettings();
            settings.format = TextureImporterFormat.R16;
            importer.SetPlatformTextureSettings(settings);
            importer.SaveAndReimport();
        }

        // smooth value noise, about 0..1, deterministic
        private static float Noise(float2 p)
        {
            float2 i = math.floor(p), f = p - i;
            float2 u = f * f * (3f - 2f * f);
            return math.lerp(math.lerp(Hash(i), Hash(i + new float2(1, 0)), u.x), math.lerp(Hash(i + new float2(0, 1)), Hash(i + new float2(1, 1)), u.x), u.y);
        }

        private static float Hash(float2 p) => math.frac(math.sin(math.dot(p, new float2(127.1f, 311.7f))) * 43758.5453f);
    }
}
