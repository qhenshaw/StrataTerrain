using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Strata
{
    // The GPU form of a palette: the layers' albedo, normal and mask textures copied (GPU to GPU, every mip) into one
    // Texture2DArray each, and a buffer of per-layer settings for the surface shader. Copying needs every texture of a
    // kind to share size, format and mip count; a layer whose texture doesn't match (or is missing) is flagged so the
    // shader skips that map for it, and Problems lists why. It also keeps each layer's average albedo (tinted), which the
    // tree shader's ground tint reads.
    public sealed class StrataPaletteTextures : IDisposable
    {
        // float4s per layer in LayerData: tiling/normal/blend/flags, tint/smoothness, far scale/occlusion
        public const int Float4sPerLayer = 3;
        private const int HasAlbedo = 1, HasNormal = 2, HasMask = 4, IsStochastic = 8, MaskMher = 16, NormalDirectX = 32;

        public Texture2DArray Albedo { get; }
        public Texture2DArray Normal { get; }
        public Texture2DArray Mask { get; }
        public GraphicsBuffer LayerData { get; }
        // Per layer: its average albedo, linear, times its tint.
        public GraphicsBuffer LayerColours { get; }
        public int LayerCount { get; }
        public List<string> Problems { get; } = new();

        public StrataPaletteTextures(StrataPalette palette)
        {
            IReadOnlyList<StrataLayer> layers = palette != null ? palette.Layers : Array.Empty<StrataLayer>();
            LayerCount = Mathf.Max(1, layers.Count);
            int[] flags = new int[LayerCount];
            Albedo = Pack(layers, l => l.Albedo, "albedo", HasAlbedo, flags, true);
            Normal = Pack(layers, l => l.Normal, "normal", HasNormal, flags, false);
            Mask = Pack(layers, l => l.Mask, "mask", HasMask, flags, false);

            Vector4[] data = new Vector4[LayerCount * Float4sPerLayer];
            for (int i = 0; i < layers.Count; i++)
            {
                StrataLayer layer = layers[i];
                if (layer == null) continue;
                if (layer.Stochastic) flags[i] |= IsStochastic;
                if (layer.MaskLayout == StrataMaskLayout.MetallicHeightEmissionRoughness) flags[i] |= MaskMher;
                if (layer.DirectXNormal) flags[i] |= NormalDirectX;
                Color tint = layer.Tint.linear;
                data[i * Float4sPerLayer] = new Vector4(1f / layer.TileSize, layer.NormalStrength, layer.BlendDepth, flags[i]);
                data[i * Float4sPerLayer + 1] = new Vector4(tint.r, tint.g, tint.b, layer.Smoothness);
                data[i * Float4sPerLayer + 2] = new Vector4(1f / layer.FarScale, layer.Occlusion, 0f, 0f);
            }
            if (layers.Count == 0) data[0] = new Vector4(0.25f, 1f, 0.2f, 0f);
            LayerData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.Length, 16);
            LayerData.SetData(data);

            Vector4[] colours = new Vector4[LayerCount];
            for (int i = 0; i < layers.Count; i++)
                if (layers[i] != null) colours[i] = Average(layers[i].Albedo) * layers[i].Tint.linear;
            LayerColours = new GraphicsBuffer(GraphicsBuffer.Target.Structured, colours.Length, 16);
            LayerColours.SetData(colours);
        }

        // The mean colour of a texture (linear), from a small blit read back.
        private static Color Average(Texture2D texture)
        {
            if (texture == null) return new Color(0.2f, 0.2f, 0.2f);
            RenderTexture target = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(texture, target);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            Texture2D read = new(32, 32, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, 32, 32), 0, 0, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Color sum = Color.clear;
            foreach (Color pixel in read.GetPixels()) sum += pixel;
            UnityEngine.Object.DestroyImmediate(read);
            return sum / 1024f;
        }

        // What packing a palette would report, without packing it (no GPU copies or readbacks), and the albedo size.
        public static List<string> Check(StrataPalette palette, out Vector2Int albedoSize)
        {
            List<string> problems = new();
            IReadOnlyList<StrataLayer> layers = palette != null ? palette.Layers : Array.Empty<StrataLayer>();
            albedoSize = Vector2Int.zero;
            foreach ((Func<StrataLayer, Texture2D> get, string kind) in new (Func<StrataLayer, Texture2D>, string)[] { (l => l.Albedo, "albedo"), (l => l.Normal, "normal"), (l => l.Mask, "mask") })
            {
                Texture2D first = null;
                foreach (StrataLayer layer in layers)
                {
                    Texture2D texture = layer != null ? get(layer) : null;
                    if (texture == null) continue;
                    if (first == null)
                    {
                        first = texture;
                        if (kind == "albedo") albedoSize = new Vector2Int(texture.width, texture.height);
                        continue;
                    }
                    string problem = Mismatch(layer, texture, first, kind);
                    if (problem != null) problems.Add(problem);
                }
            }
            return problems;
        }

        private static string Mismatch(StrataLayer layer, Texture2D texture, Texture2D first, string kind) =>
            texture.width != first.width || texture.height != first.height || texture.graphicsFormat != first.graphicsFormat || texture.mipmapCount != first.mipmapCount
                ? $"{layer.name}: {kind} is {texture.width}x{texture.height} {texture.graphicsFormat} with {texture.mipmapCount} mips, " +
                  $"expected {first.width}x{first.height} {first.graphicsFormat} with {first.mipmapCount}"
                : null;

        private Texture2DArray Pack(IReadOnlyList<StrataLayer> layers, Func<StrataLayer, Texture2D> get, string kind, int flag, int[] flags, bool srgb)
        {
            Texture2D first = null;
            foreach (StrataLayer layer in layers)
                if (layer != null && get(layer) != null)
                {
                    first = get(layer);
                    break;
                }
            if (first == null)
                return new Texture2DArray(4, 4, LayerCount, srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None)
                       { name = $"Strata {kind} (empty)", hideFlags = HideFlags.HideAndDontSave };

            Texture2DArray array = new(first.width, first.height, LayerCount, first.graphicsFormat, TextureCreationFlags.MipChain, first.mipmapCount)
            {
                name = $"Strata {kind}",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8
            };
            for (int i = 0; i < layers.Count; i++)
            {
                Texture2D texture = layers[i] != null ? get(layers[i]) : null;
                if (texture == null) continue;
                string problem = Mismatch(layers[i], texture, first, kind);
                if (problem != null)
                {
                    Problems.Add(problem);
                    continue;
                }
                Graphics.CopyTexture(texture, 0, array, i);
                flags[i] |= flag;
            }
            return array;
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(Albedo);
            UnityEngine.Object.DestroyImmediate(Normal);
            UnityEngine.Object.DestroyImmediate(Mask);
            LayerData.Dispose();
            LayerColours.Dispose();
        }
    }
}
