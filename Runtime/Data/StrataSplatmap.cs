using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Strata
{
    // A world's surface maps, in slots like its heights (slot i is whichever tile the heightfield put there) at splat
    // resolution with shared edges: two RGBA8 arrays holding each texel's four strongest palette layer indices and their
    // weights, and an RG8 array holding the rock strata strength and which strata definition applies. The surface
    // shader reads them, falling back to the far splats (every FarSplatStep-th texel of the whole world, no strata) where
    // a tile isn't resident; the editor's evaluator writes them. The CPU keeps the splats it loaded (for ground queries)
    // and, per CDLOD leaf node of every tile, the largest geometry range of any strata in it (0 for none), which culling
    // uses to decide where to draw the finer detail patches.
    public sealed class StrataSplatmap : IDisposable
    {
        public StrataGrid Grid { get; }
        public int SlotCount { get; }
        public RenderTexture Indices { get; }
        public RenderTexture Weights { get; }
        public RenderTexture Strata { get; }
        public Texture2D FarIndices { get; }
        public Texture2D FarWeights { get; }
        public NativeArray<float> LeafDetailRange { get; private set; }
        // The splats as loaded from the bake, per slot, and the far splats.
        public NativeArray<uint> CpuIndices { get; private set; }
        public NativeArray<uint> CpuWeights { get; private set; }
        public NativeArray<uint> CpuFarIndices { get; private set; }
        public NativeArray<uint> CpuFarWeights { get; private set; }

        public StrataSplatmap(StrataGrid grid, int slotCount)
        {
            Grid = grid;
            SlotCount = slotCount;
            Indices = Create(grid, slotCount, "Strata Splat Indices", GraphicsFormat.R8G8B8A8_UNorm);
            Weights = Create(grid, slotCount, "Strata Splat Weights", GraphicsFormat.R8G8B8A8_UNorm);
            Strata = Create(grid, slotCount, "Strata Rock Map", GraphicsFormat.R8G8_UNorm);
            int2 far = StrataHeights.FarSplatSamples(grid);
            FarIndices = new Texture2D(far.x, far.y, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.DontInitializePixels)
                { name = "Strata Far Splat Indices", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            FarWeights = new Texture2D(far.x, far.y, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.DontInitializePixels)
                { name = "Strata Far Splat Weights", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            LeafDetailRange = new(grid.TileCount * grid.LeavesPerSide * grid.LeavesPerSide, Allocator.Persistent);
            CpuIndices = new(grid.SplatSamplesPerTile * slotCount, Allocator.Persistent);
            CpuWeights = new(grid.SplatSamplesPerTile * slotCount, Allocator.Persistent);
            CpuFarIndices = new(far.x * far.y, Allocator.Persistent);
            CpuFarWeights = new(far.x * far.y, Allocator.Persistent);
            Fill();
        }

        private static RenderTexture Create(StrataGrid grid, int slots, string name, GraphicsFormat format)
        {
            RenderTexture texture = new(grid.SplatSamples, grid.SplatSamples, format, GraphicsFormat.None)
            {
                name = name,
                dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                volumeDepth = slots,
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            return texture;
        }

        // The first layer, fully, everywhere, and no strata.
        public void Fill()
        {
            NativeArray<uint> indices = CpuIndices, weights = CpuWeights, farIndices = CpuFarIndices, farWeights = CpuFarWeights;
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = 0;
                weights[i] = 0xffu;
            }
            for (int i = 0; i < farIndices.Length; i++)
            {
                farIndices[i] = 0;
                farWeights[i] = 0xffu;
            }
            NativeArray<ushort> strata = new(indices.Length, Allocator.Temp);
            Upload(indices, weights, strata);
            strata.Dispose();
            UploadFar();
            NativeArray<float> leaves = LeafDetailRange;
            for (int i = 0; i < leaves.Length; i++) leaves[i] = 0f;
        }

        // Loads every tile's splats and strata from a bake into the slot of the same number (resident worlds); tiles
        // baked without them keep the first layer and no strata. False if the bake doesn't match.
        public bool Load(StrataWorldData data)
        {
            if (data == null || !data.IsComplete || !data.Grid.Equals(Grid) || SlotCount != Grid.TileCount) return false;
            int perTile = Grid.SplatSamplesPerTile;
            NativeArray<ushort> strata = new(CpuIndices.Length, Allocator.Temp);
            for (int tile = 0; tile < Grid.TileCount; tile++)
            {
                NativeArray<byte> file = data.ReadTile(tile, Allocator.Temp);
                Read(file, tile, tile, strata.GetSubArray(tile * perTile, perTile), data.Strata);
                file.Dispose();
            }
            Upload(CpuIndices, CpuWeights, strata);
            strata.Dispose();
            return true;
        }

        public void LoadFar(StrataFarFile.Data far)
        {
            NativeArray<uint>.Copy(far.SplatIndices, CpuFarIndices, CpuFarIndices.Length);
            NativeArray<uint>.Copy(far.SplatWeights, CpuFarWeights, CpuFarWeights.Length);
            UploadFar();
        }

        // Puts a tile file's splats and strata in a slot (the heightfield's slot for that tile).
        public void SetTile(int tile, int slot, NativeArray<byte> file, IReadOnlyList<StrataBandDefinition> definitions)
        {
            int perTile = Grid.SplatSamplesPerTile;
            NativeArray<ushort> strata = new(perTile, Allocator.Temp);
            Read(file, tile, slot, strata, definitions);
            Copy(CpuIndices.GetSubArray(slot * perTile, perTile), Indices, slot, GraphicsFormat.R8G8B8A8_UNorm);
            Copy(CpuWeights.GetSubArray(slot * perTile, perTile), Weights, slot, GraphicsFormat.R8G8B8A8_UNorm);
            Copy(strata, Strata, slot, GraphicsFormat.R8G8_UNorm);
            strata.Dispose();
        }

        // A tile leaving its slot draws no detail patches.
        public void ReleaseTile(int tile)
        {
            NativeArray<float> leaves = LeafDetailRange;
            int count = Grid.LeavesPerSide * Grid.LeavesPerSide;
            for (int i = 0; i < count; i++) leaves[tile * count + i] = 0f;
        }

        private void Read(NativeArray<byte> file, int tile, int slot, NativeArray<ushort> strata, IReadOnlyList<StrataBandDefinition> definitions)
        {
            int perTile = Grid.SplatSamplesPerTile;
            NativeArray<uint> indices = CpuIndices, weights = CpuWeights;
            bool read = StrataTileFile.ReadSplats(file, Grid.Samples, Grid.SplatSamples, indices.Slice(slot * perTile, perTile), weights.Slice(slot * perTile, perTile));
            if (!read)
                for (int i = 0; i < perTile; i++)
                {
                    indices[slot * perTile + i] = 0;
                    weights[slot * perTile + i] = 0xffu;
                }
            if (!StrataTileFile.ReadStrata(file, Grid.Samples, Grid.SplatSamples, strata.Slice()))
                for (int i = 0; i < strata.Length; i++) strata[i] = 0;
            UpdateLeaves(tile, strata, definitions);
        }

        // Recomputes one tile's leaf detail ranges from its strata map (low byte strength, high byte definition).
        public void UpdateLeaves(int tile, NativeArray<ushort> strata, IReadOnlyList<StrataBandDefinition> definitions)
        {
            int leaves = Grid.LeavesPerSide;
            int texelsPerLeaf = Grid.SplatResolution / leaves;
            NativeArray<float> ranges = LeafDetailRange;
            for (int lz = 0; lz < leaves; lz++)
            for (int lx = 0; lx < leaves; lx++)
            {
                float range = 0f;
                for (int z = lz * texelsPerLeaf; z <= (lz + 1) * texelsPerLeaf; z++)
                for (int x = lx * texelsPerLeaf; x <= (lx + 1) * texelsPerLeaf; x++)
                {
                    ushort value = strata[z * Grid.SplatSamples + x];
                    int definition = value >> 8;
                    if ((value & 0xff) == 0 || definitions == null || definition >= definitions.Count) continue;
                    StrataBandDefinition d = definitions[definition];
                    if (d.Push > 0f) range = math.max(range, d.GeometryRange);
                }
                ranges[(tile * leaves + lz) * leaves + lx] = range;
            }
        }

        private void Upload(NativeArray<uint> indices, NativeArray<uint> weights, NativeArray<ushort> strata)
        {
            CopyAll(indices, Indices, GraphicsFormat.R8G8B8A8_UNorm);
            CopyAll(weights, Weights, GraphicsFormat.R8G8B8A8_UNorm);
            CopyAll(strata, Strata, GraphicsFormat.R8G8_UNorm);
        }

        private void UploadFar()
        {
            FarIndices.SetPixelData(CpuFarIndices, 0);
            FarIndices.Apply(false, false);
            FarWeights.SetPixelData(CpuFarWeights, 0);
            FarWeights.Apply(false, false);
        }

        private void CopyAll<T>(NativeArray<T> values, RenderTexture target, GraphicsFormat format) where T : struct
        {
            Texture2DArray staging = new(Grid.SplatSamples, Grid.SplatSamples, SlotCount, format, TextureCreationFlags.DontInitializePixels);
            for (int slot = 0; slot < SlotCount; slot++)
                staging.SetPixelData(values, 0, slot, slot * Grid.SplatSamplesPerTile);
            staging.Apply(false, false);
            Graphics.CopyTexture(staging, target);
            UnityEngine.Object.DestroyImmediate(staging);
        }

        private void Copy<T>(NativeArray<T> values, RenderTexture target, int slot, GraphicsFormat format) where T : struct
        {
            Texture2D staging = new(Grid.SplatSamples, Grid.SplatSamples, format, TextureCreationFlags.DontInitializePixels);
            staging.SetPixelData(values, 0);
            staging.Apply(false, false);
            Graphics.CopyTexture(staging, 0, 0, target, slot, 0);
            UnityEngine.Object.DestroyImmediate(staging);
        }

        public void Dispose()
        {
            if (Indices != null) UnityEngine.Object.DestroyImmediate(Indices);
            if (Weights != null) UnityEngine.Object.DestroyImmediate(Weights);
            if (Strata != null) UnityEngine.Object.DestroyImmediate(Strata);
            if (FarIndices != null) UnityEngine.Object.DestroyImmediate(FarIndices);
            if (FarWeights != null) UnityEngine.Object.DestroyImmediate(FarWeights);
            if (LeafDetailRange.IsCreated) LeafDetailRange.Dispose();
            if (CpuIndices.IsCreated) CpuIndices.Dispose();
            if (CpuWeights.IsCreated) CpuWeights.Dispose();
            if (CpuFarIndices.IsCreated) CpuFarIndices.Dispose();
            if (CpuFarWeights.IsCreated) CpuFarWeights.Dispose();
        }
    }
}
