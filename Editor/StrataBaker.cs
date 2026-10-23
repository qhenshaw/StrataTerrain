using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata.Editor
{
    // Bakes a world: evaluates every feature over the whole world on the GPU (heights), reads the heights back, paints
    // the biome maps, cuts the caves' holes and works out their ground weights, evaluates the moisture map, splats and forest and grass densities,
    // places the trees, meshes the caves, writes each tile to
    // StreamingAssets/Strata/<world>/tile_x_z.bytes (rewriting only tiles whose contents changed) with the far file
    // beside them (the whole world's heights and splats coarsely, and every CDLOD node's bounds), and points the world's
    // Baked Data block at them, which dirties the scene. Returns a one-line report.
    public static class StrataBaker
    {
        public static string Estimate(StrataGrid grid)
        {
            float tileMb = StrataTileFile.Size(grid) / (1024f * 1024f);
            float evalMb = grid.WorldSamplesX * (float)grid.WorldSamplesZ * 4f / (1024f * 1024f);
            return $"{grid.TileCount} tiles, {tileMb * grid.TileCount:0.#} MB on disk and, in the editor, GPU memory (play mode streams them), " +
                   $"{grid.NodeCount} CDLOD nodes, about {evalMb * 2f:0} MB of GPU scratch to evaluate";
        }

        public static string Bake(StrataWorld world)
        {
            if (world.Heightfield == null) return "Strata bake: the world has no valid settings.";
            Stopwatch watch = Stopwatch.StartNew();
            StrataHeightfield heightfield = world.Heightfield;
            StrataGrid grid = heightfield.Grid;
            StrataEvaluator evaluator = StrataPreview.Evaluator;
            StrataSplatEvaluator splats = StrataPreview.Splats;
            List<StrataEvaluator.Node> nodes = StrataEvaluator.Collect(world);

            RectInt all = StrataEvaluator.WorldRect(grid);
            RenderTexture heights = evaluator.Evaluate(grid, nodes, all);
            evaluator.WriteTiles(grid, heights, all, all, heightfield.Texture);
            RenderTexture.ReleaseTemporary(heights);

            // the CPU heights first, which caves are carved from and tree placement stands the trees on
            for (int tile = 0; tile < grid.TileCount; tile++)
            {
                AsyncGPUReadbackRequest heightRequest = AsyncGPUReadback.Request(heightfield.Texture, 0, 0, grid.Samples, 0, grid.Samples, tile, 1);
                heightRequest.WaitForCompletion();
                if (heightRequest.hasError) return $"Strata bake: reading back the heights of tile {tile} failed.";
                StrataPreview.CopyTile(heightfield, tile, heightRequest.GetData<ushort>());
            }
            // biomes before everything that is limited to them (splats, trees, grass, cave layers, formations)
            List<StrataBiomeArea> biomes = StrataBiomeEvaluator.BiomesOf(nodes);
            world.SetBiomeAreas(biomes);
            if (biomes.Count > 0) StrataBiomeEvaluator.Evaluate(world, biomes, StrataPreview.AllTiles(grid));
            else world.Biomes.Fill();
            List<StrataCaveFeature> caves = StrataCaveEvaluator.CavesOf(nodes);
            int holedTiles = StrataPreview.Caves.EvaluateSurface(world, caves, StrataPreview.AllTiles(grid));
            if (StrataSplatEvaluator.NeedsMoisture(world, nodes)) splats.UpdateMoisture(world, nodes);
            StrataBandDefinition[] strata = StrataSplatEvaluator.Definitions(StrataSplatEvaluator.RockStrataOf(nodes));
            world.SetStrataDefinitions(strata);
            List<StrataForest> forests = StrataTreeEvaluator.AllForests(nodes);
            StrataTreeEvaluator.Context trees = new();
            trees.Forests.AddRange(StrataTreeEvaluator.ScatteredForests(nodes));
            StrataGrassPrototype[] grassPrototypes = StrataTreeEvaluator.GrassPrototypes(nodes).ToArray();
            if (!StrataTreeEvaluator.SameList(world.GrassPrototypes, grassPrototypes)) world.SetGrassPrototypes(grassPrototypes);
            splats.Evaluate(world, nodes, StrataPreview.AllTiles(grid), evaluator, forests.Count > 0 ? trees : null, StrataPreview.GrassTarget(world));

            StrataTreePrototype[] prototypes = StrataTreeEvaluator.Prototypes(forests).ToArray();
            if (forests.Count > 0)
            {
                foreach ((int tile, AsyncGPUReadbackRequest request) in trees.Requests)
                {
                    request.WaitForCompletion();
                    StrataPreview.Trees.Store(world, StrataTreeEvaluator.Slices(trees.Forests.Count), tile, request);
                }
                StrataPreview.Trees.Place(world, nodes, StrataPreview.AllTiles(grid));
            }
            else world.Trees.Clear();
            Dictionary<int, byte[]> caveData = StrataPreview.Caves.Mesh(world, caves, StrataPreview.AllTiles(grid));
            int caveBricks = StrataPreview.Caves.LastBricks;
            List<CaveFormations> formations = StrataCaveEvaluator.FormationsOf(nodes);
            List<StrataFormationPrototype> formationPrototypes = StrataFormationPlacer.Prototypes(formations);
            world.SetFormationPrototypes(formationPrototypes);
            Dictionary<int, StrataFormationRecord[]> formationData =
                StrataPreview.Caves.PlaceFormations(world, formations, formationPrototypes, StrataPreview.AllTiles(grid));
            long formationCount = 0;
            foreach (StrataFormationRecord[] records in formationData.Values) formationCount += records?.Length ?? 0;

            string dataFolder = $"Strata/{world.name}";
            string tileFolder = StrataWorldData.FolderPath(dataFolder);
            Directory.CreateDirectory(tileFolder);
            heightfield.BuildFar();
            Unity.Mathematics.int2 farSplats = StrataHeights.FarSplatSamples(grid);
            StrataFarFile.Data far = new()
            {
                Heights = new NativeArray<ushort>(heightfield.Far, Allocator.Temp),
                SplatIndices = new NativeArray<uint>(farSplats.x * farSplats.y, Allocator.Temp),
                SplatWeights = new NativeArray<uint>(farSplats.x * farSplats.y, Allocator.Temp),
                NodeMinMax = new NativeArray<Unity.Mathematics.float2>(heightfield.NodeMinMax, Allocator.Temp)
            };
            if (biomes.Count > 0)
            {
                far.BiomeSurface = new NativeArray<uint>(farSplats.x * farSplats.y, Allocator.Temp);
                far.BiomeUnderground = new NativeArray<uint>(farSplats.x * farSplats.y, Allocator.Temp);
            }

            long treeCount = 0;
            int written = 0;
            for (int tz = 0; tz < grid.TilesZ; tz++)
            for (int tx = 0; tx < grid.TilesX; tx++)
            {
                int tile = grid.TileIndex(tx, tz);
                AsyncGPUReadbackRequest indexRequest = AsyncGPUReadback.Request(world.Splatmap.Indices, 0, 0, grid.SplatSamples, 0, grid.SplatSamples, tile, 1);
                AsyncGPUReadbackRequest weightRequest = AsyncGPUReadback.Request(world.Splatmap.Weights, 0, 0, grid.SplatSamples, 0, grid.SplatSamples, tile, 1);
                AsyncGPUReadbackRequest strataRequest = AsyncGPUReadback.Request(world.Splatmap.Strata, 0, 0, grid.SplatSamples, 0, grid.SplatSamples, tile, 1);
                strataRequest.WaitForCompletion();
                weightRequest.WaitForCompletion();
                indexRequest.WaitForCompletion();
                if (indexRequest.hasError || weightRequest.hasError || strataRequest.hasError)
                    return $"Strata bake: reading back tile {tx},{tz} failed.";
                NativeArray<ushort> strataMap = strataRequest.GetData<ushort>();
                world.UpdateStrataLeaves(tile, strataMap);
                NativeArray<byte> grass = new(grassPrototypes.Length * grid.SplatSamplesPerTile, Allocator.Temp);
                if (grassPrototypes.Length > 0)
                {
                    AsyncGPUReadbackRequest grassRequest = AsyncGPUReadback.Request(world.Grass.Texture, 0, 0, grid.SplatSamples, 0, grid.SplatSamples,
                                                                                    tile * grassPrototypes.Length, grassPrototypes.Length);
                    grassRequest.WaitForCompletion();
                    if (grassRequest.hasError) return $"Strata bake: reading back the grass of tile {tx},{tz} failed.";
                    for (int type = 0; type < grassPrototypes.Length; type++)
                        NativeArray<byte>.Copy(grassRequest.GetData<byte>(type), 0, grass, type * grid.SplatSamplesPerTile, grid.SplatSamplesPerTile);
                }
                NativeArray<uint> indices = indexRequest.GetData<uint>();
                NativeArray<uint> weights = weightRequest.GetData<uint>();
                // every FarSplatStep-th texel into the far splats (shared edges write the same texel twice)
                for (int z = 0; z <= grid.SplatResolution; z += StrataHeights.FarSplatStep)
                for (int x = 0; x <= grid.SplatResolution; x += StrataHeights.FarSplatStep)
                {
                    int farIndex = (tz * grid.SplatResolution + z) / StrataHeights.FarSplatStep * farSplats.x + (tx * grid.SplatResolution + x) / StrataHeights.FarSplatStep;
                    far.SplatIndices[farIndex] = indices[z * grid.SplatSamples + x];
                    far.SplatWeights[farIndex] = weights[z * grid.SplatSamples + x];
                    if (biomes.Count == 0) continue;
                    int texel = heightfield.SlotOf(tile) * grid.SplatSamplesPerTile + z * grid.SplatSamples + x;
                    far.BiomeSurface[farIndex] = world.Biomes.CpuSurface[texel];
                    far.BiomeUnderground[farIndex] = world.Biomes.CpuUnderground[texel];
                }
                StrataTileExtras extras = StrataCaveEvaluator.Extras(world, tile, caveData[tile], formationData[tile]);
                if (biomes.Count > 0)
                {
                    extras ??= new StrataTileExtras();
                    extras.Biomes = world.Biomes.Encode(heightfield.SlotOf(tile));
                }
                if (StrataTileFile.Write(StrataWorldData.TilePath(dataFolder, tx, tz), grid.Samples,
                                     heightfield.Heights.Slice(tile * grid.SamplesPerTile, grid.SamplesPerTile),
                                     grid.SplatSamples, indices.Slice(), weights.Slice(), strataMap.Slice(), world.Trees[tile].Records,
                                     grassPrototypes.Length, grass, extras)) written++;
                grass.Dispose();
                treeCount += world.Trees[tile].Records.Length;
            }

            StrataFarFile.Write(Path.Combine(tileFolder, StrataFarFile.Name), grid, far);
            // the coarse caves that reach the surface, for a streamed world to show beyond its resident tiles
            string farCaves = Path.Combine(tileFolder, StrataCaves.FarName);
            List<byte[]> farCaveTiles = new();
            for (int tile = 0; tile < grid.TileCount; tile++) farCaveTiles.Add(StrataPreview.Caves.LastFar.GetValueOrDefault(tile));
            if (farCaveTiles.Exists(data => data != null)) StrataCaves.WriteFar(farCaves, farCaveTiles);
            else if (File.Exists(farCaves))
            {
                File.Delete(farCaves);
                if (File.Exists(farCaves + ".meta")) File.Delete(farCaves + ".meta");
            }
            long farBytes = new FileInfo(Path.Combine(tileFolder, StrataFarFile.Name)).Length;
            far.Dispose();
            AssetDatabase.Refresh();

            Undo.RecordObject(world, "Bake Strata World");
            world.Data.Set(grid, dataFolder, strata, prototypes, grassPrototypes, StrataCaveEvaluator.VoxelSize(world), holedTiles,
                          formationPrototypes.ToArray(), biomes.ToArray());
            EditorUtility.SetDirty(world);
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            world.NotifyHeightsChanged();

            float mb = (grid.TileCount * StrataTileFile.Size(grid, 0, grassPrototypes.Length) + treeCount * 10L + farBytes) / (1024f * 1024f);
            return $"Strata bake: {grid.TileCount} tiles ({grid}; {written} changed), {treeCount} trees of {prototypes.Length} prototypes, " +
                   $"{grassPrototypes.Length} grass prototypes, {caveBricks} cave bricks, {holedTiles} holed tiles, {formationCount} formations, {biomes.Count} biomes, {mb:0.#} MB, " +
                   $"{watch.Elapsed.TotalSeconds:0.00} s, to {tileFolder}; save the scene to keep the baked data";
        }
    }
}
