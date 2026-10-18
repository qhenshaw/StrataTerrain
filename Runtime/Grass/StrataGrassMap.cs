using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Strata
{
    // A world's grass densities on the GPU: one R8 slice per heightfield slot and grass prototype (slice = slot * Types +
    // type), at splat resolution with shared edges, 0..1 of each prototype's full density. Tiles that aren't resident
    // have no grass (it only reaches a hundred metres or so). The grass renderer reads it; the editor's evaluator writes
    // it, and the bake reads it back into the tile files.
    public sealed class StrataGrassMap : IDisposable
    {
        public StrataGrid Grid { get; }
        public int Types { get; }
        public RenderTexture Texture { get; }

        public int SlotCount { get; }

        public StrataGrassMap(StrataGrid grid, int types, int slotCount)
        {
            Grid = grid;
            Types = types;
            SlotCount = slotCount;
            Texture = new RenderTexture(grid.SplatSamples, grid.SplatSamples, GraphicsFormat.R8_UNorm, GraphicsFormat.None)
            {
                name = "Strata Grass Density",
                dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                volumeDepth = Mathf.Max(1, slotCount * types),
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            Texture.Create();
            Clear();
        }

        public void Clear()
        {
            RenderTexture active = RenderTexture.active;
            for (int slice = 0; slice < Texture.volumeDepth; slice++)
            {
                Graphics.SetRenderTarget(Texture, 0, CubemapFace.Unknown, slice);
                GL.Clear(false, true, Color.clear);
            }
            RenderTexture.active = active;
        }

        // Loads every tile's densities from a bake into the slot of the same number (resident worlds); tiles without grass
        // stay bare.
        public void Load(StrataWorldData data)
        {
            if (Types == 0 || data == null || !data.IsComplete || !data.Grid.Equals(Grid) || SlotCount != Grid.TileCount) return;
            int perTile = Grid.SplatSamplesPerTile;
            Texture2DArray staging = new(Grid.SplatSamples, Grid.SplatSamples, Texture.volumeDepth, GraphicsFormat.R8_UNorm, TextureCreationFlags.DontInitializePixels);
            NativeArray<byte> empty = new(perTile, Allocator.Temp);
            for (int tile = 0; tile < Grid.TileCount; tile++)
            {
                NativeArray<byte> file = data.ReadTile(tile, Allocator.Temp);
                NativeArray<byte> grass = StrataTileFile.ReadGrass(file, Grid.Samples, Grid.SplatSamples, Allocator.Temp, out int types);
                file.Dispose();
                for (int type = 0; type < Types; type++)
                    staging.SetPixelData(type < types ? grass.GetSubArray(type * perTile, perTile) : empty, 0, tile * Types + type);
                grass.Dispose();
            }
            empty.Dispose();
            staging.Apply(false, false);
            Graphics.CopyTexture(staging, Texture);
            UnityEngine.Object.DestroyImmediate(staging);
        }

        // Puts a tile file's densities in a slot.
        public void SetTile(int slot, NativeArray<byte> file)
        {
            if (Types == 0) return;
            int perTile = Grid.SplatSamplesPerTile;
            NativeArray<byte> grass = StrataTileFile.ReadGrass(file, Grid.Samples, Grid.SplatSamples, Allocator.Temp, out int types);
            NativeArray<byte> empty = new(perTile, Allocator.Temp);
            for (int type = 0; type < Types; type++)
                StrataSliceUpload.Copy(type < types ? grass.GetSubArray(type * perTile, perTile) : empty, Grid.SplatSamples, GraphicsFormat.R8_UNorm, Texture, slot * Types + type);
            empty.Dispose();
            grass.Dispose();
        }

        public void Dispose()
        {
            if (Texture != null) UnityEngine.Object.DestroyImmediate(Texture);
        }
    }
}
