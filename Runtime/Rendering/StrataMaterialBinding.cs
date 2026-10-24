using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // Sets everything Strata's surface shaders read from a world (heights, holes, splats, palette, rock strata) on a
    // material, for the terrain's materials and the caves', which texture their walls from the same palette and blend
    // into the terrain's splats near the surface. Unity drops textures and buffers a shader doesn't declare when the
    // shader is reimported, so the world binds them again before each camera renders.
    public static class StrataMaterialBinding
    {
        private static readonly int HeightsId = Shader.PropertyToID("_StrataHeights");
        private static readonly int GridId = Shader.PropertyToID("_StrataGrid");
        private static readonly int HeightParamsId = Shader.PropertyToID("_StrataHeightParams");
        private static readonly int LodId = Shader.PropertyToID("_StrataLod");
        private static readonly int SplatIndicesId = Shader.PropertyToID("_StrataSplatIndices");
        private static readonly int SplatWeightsId = Shader.PropertyToID("_StrataSplatWeights");
        private static readonly int SplatParamsId = Shader.PropertyToID("_StrataSplatParams");
        private static readonly int LayerAlbedoId = Shader.PropertyToID("_StrataLayerAlbedo");
        private static readonly int LayerNormalId = Shader.PropertyToID("_StrataLayerNormal");
        private static readonly int LayerMaskId = Shader.PropertyToID("_StrataLayerMask");
        private static readonly int LayerDataId = Shader.PropertyToID("_StrataLayerData");
        private static readonly int RockMapId = Shader.PropertyToID("_StrataRockMap");
        private static readonly int BandsId = Shader.PropertyToID("_StrataBands");
        private static readonly int FarHeightsId = Shader.PropertyToID("_StrataFarHeights");
        private static readonly int FarSplatIndicesId = Shader.PropertyToID("_StrataFarSplatIndices");
        private static readonly int FarSplatWeightsId = Shader.PropertyToID("_StrataFarSplatWeights");
        private static readonly int FarId = Shader.PropertyToID("_StrataFar");
        private static readonly int TileSlotsId = Shader.PropertyToID("_StrataTileSlots");
        private static readonly int HolesId = Shader.PropertyToID("_StrataHoles");
        private static readonly int HoleSlotsId = Shader.PropertyToID("_StrataHoleSlots");

        // The material settings the cave material takes from the terrain's, so walls are textured like the cliffs.
        private static readonly int[] s_look =
        {
            Shader.PropertyToID("_TriplanarStart"), Shader.PropertyToID("_TriplanarEnd"), Shader.PropertyToID("_TriplanarSharpness"),
            Shader.PropertyToID("_FarStart"), Shader.PropertyToID("_FarEnd"), Shader.PropertyToID("_MacroScale"),
            Shader.PropertyToID("_MacroStrength"), Shader.PropertyToID("_ParallaxStrength")
        };

        public static void Bind(Material material, StrataHeightfield heights, StrataSplatmap splats, StrataPaletteTextures palette,
                                GraphicsBuffer bands, Vector4 lod)
        {
            StrataParallax.EnsureDefaults();
            StrataGrid grid = heights.Grid;
            material.SetTexture(HeightsId, heights.Texture);
            material.SetTexture(FarHeightsId, heights.FarTexture);
            material.SetBuffer(TileSlotsId, heights.TileSlotBuffer);
            material.SetTexture(HolesId, heights.Holes.Texture);
            material.SetBuffer(HoleSlotsId, heights.Holes.TileSlotBuffer);
            int2 far = StrataHeights.FarSamples(grid) - 1, farSplats = StrataHeights.FarSplatSamples(grid) - 1;
            material.SetVector(FarId, new Vector4(far.x, far.y, farSplats.x, farSplats.y));
            material.SetVector(GridId, new Vector4(grid.Origin.x, grid.Origin.y, grid.TileSize, grid.TilesX));
            material.SetVector(HeightParamsId, new Vector4(grid.MinHeight, grid.HeightRange, grid.Resolution, grid.Spacing));
            material.SetVector(LodId, lod);
            if (bands != null) material.SetBuffer(BandsId, bands);
            if (splats != null)
            {
                material.SetTexture(SplatIndicesId, splats.Indices);
                material.SetTexture(SplatWeightsId, splats.Weights);
                material.SetTexture(RockMapId, splats.Strata);
                material.SetTexture(FarSplatIndicesId, splats.FarIndices);
                material.SetTexture(FarSplatWeightsId, splats.FarWeights);
            }
            if (palette != null)
            {
                material.SetVector(SplatParamsId, new Vector4(grid.SplatResolution, palette.LayerCount, 0f, 0f));
                material.SetTexture(LayerAlbedoId, palette.Albedo);
                material.SetTexture(LayerNormalId, palette.Normal);
                material.SetTexture(LayerMaskId, palette.Mask);
                material.SetBuffer(LayerDataId, palette.LayerData);
            }
        }

        public static void SetBands(Material material, GraphicsBuffer bands) => material.SetBuffer(BandsId, bands);

        public static void CopyLook(Material from, Material to)
        {
            foreach (int id in s_look)
                if (from.HasFloat(id) && to.HasFloat(id)) to.SetFloat(id, from.GetFloat(id));
        }
    }
}
