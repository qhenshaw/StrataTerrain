using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata.Editor
{
    // Keeps every Strata world's heights and splats in step with its features while editing. Each editor update it
    // compares the features under each world with what it saw last (version, transform, enabled, order), turns the
    // differences into a dirty area (the old and new bounds of whatever changed), and re-evaluates just that area into
    // the heightfield's GPU tiles, so the surface updates the same frame; the touched tiles are then read back to the
    // CPU heights (for node bounds and collision) asynchronously. Splats are repainted for the tiles the height change
    // touched, or for every tile when a paint-only feature (Auto Splat) or the palette changed, or when the rules read
    // moisture (which a height change anywhere can alter). Trees follow the splats: the forest densities of the
    // repainted tiles are read back, and as they arrive the trees of those tiles and their neighbours are placed again;
    // a change to the forests themselves repaints and re-places everything. Caves follow the heights: once the CPU heights
    // of a change have arrived, the tiles a cave edit (or a height change under a cave) touched have their holes cut and
    // caves meshed again, and their splats and trees repainted for the caves' paint and tree effects. Biome areas don't
    // depend on the heights: when any of them changes, every tile's biome maps are painted again at once and the splats
    // and trees repainted (and the caves meshed again, if underground biomes are involved). A world is evaluated in full
    // when first seen or rebuilt.
    [InitializeOnLoad]
    public static class StrataPreview
    {
        private const string EnabledKey = "Strata.LivePreview";

        private struct FeatureState
        {
            public int Version;
            public Matrix4x4 Matrix;
            public int Order;
            public RectInt Area;
            public bool Heights;
        }

        private sealed class WorldState
        {
            public StrataHeightfield Heightfield;
            public readonly Dictionary<StrataFeature, FeatureState> Features = new();
            public bool NeedsFull = true;
            public int PaletteVersion;
            public int MoistureSignature;
            public int DensitySignature;
            public int PlacementSignature;
            public int BiomeSignature;
            public bool HadUnderground;
            // world samples whose caves need evaluating again, once no height readback is outstanding
            public RectInt CaveDirty;
            // the caves are meshed again in full (first sight, palette change) rather than just the bricks under CaveDirty
            public bool CaveFull = true;
            // world samples whose coarse cave levels wait for the edit being dragged to be let go
            public RectInt CoarseDirty;
            // world samples whose formations need placing again
            public RectInt FormationDirty;
            public int PendingReadbacks;
            // tree density readbacks not yet stored, oldest first, with how many slices each has
            public readonly List<(int Tile, AsyncGPUReadbackRequest Request, int Slices)> PendingTrees = new();
        }

        private static readonly Dictionary<StrataWorld, WorldState> s_worlds = new();
        private static readonly HashSet<StrataFeature> s_seen = new();
        private static readonly List<StrataFeature> s_removed = new();
        private static StrataEvaluator s_evaluator;
        private static StrataSplatEvaluator s_splats;
        private static StrataTreeEvaluator s_trees;
        private static StrataCaveEvaluator s_caves;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set
            {
                EditorPrefs.SetBool(EnabledKey, value);
                if (value) RefreshAll();
            }
        }

        public static double LastMilliseconds { get; private set; }
        public static double LastSplatMilliseconds { get; private set; }
        public static RectInt LastArea { get; private set; }
        public static double LastCaveMilliseconds { get; private set; }

        static StrataPreview()
        {
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting += Release;
        }

        public static StrataEvaluator Evaluator => s_evaluator ??= new StrataEvaluator();
        public static StrataSplatEvaluator Splats => s_splats ??= new StrataSplatEvaluator();
        public static StrataTreeEvaluator Trees => s_trees ??= new StrataTreeEvaluator();
        public static StrataCaveEvaluator Caves => s_caves ??= new StrataCaveEvaluator();

        // Marks every world for a full evaluation on the next update.
        public static void RefreshAll()
        {
            foreach (WorldState state in s_worlds.Values) state.NeedsFull = true;
        }

        private static void Release()
        {
            s_evaluator?.Dispose();
            s_evaluator = null;
            s_splats?.Dispose();
            s_splats = null;
            s_trees?.Dispose();
            s_trees = null;
            s_caves?.Dispose();
            s_caves = null;
            s_worlds.Clear();
        }

        private static void Update()
        {
            // isPlayingOrWillChangePlaymode turns false as soon as play mode is asked to stop, while the play mode worlds
            // (streamed, a slot per resident tile) are still about until Application.isPlaying follows
            if (!Enabled || EditorApplication.isPlayingOrWillChangePlaymode || Application.isPlaying) return;
            foreach (StrataWorld world in StrataWorld.Active)
            {
                if (world == null || world.Heightfield == null) continue;
                // a world still holding streamed slots (in case one outlives play mode) is rebuilt with every tile resident
                if (!world.Settings.Grid.Equals(world.Heightfield.Grid) || world.Heightfield.Streamed)
                {
                    world.Rebuild();
                    if (world.Heightfield == null) continue;
                }
                if (!s_worlds.TryGetValue(world, out WorldState state)) s_worlds[world] = state = new WorldState();
                if (state.Heightfield != world.Heightfield)
                {
                    state.Heightfield = world.Heightfield;
                    state.Features.Clear();
                    state.NeedsFull = true;
                }

                StrataGrid grid = world.Heightfield.Grid;
                List<StrataEvaluator.Node> nodes = StrataEvaluator.Collect(world);
                RectInt dirty = Diff(state, grid, nodes, out bool splatAll, out RectInt caveDirty, out RectInt formationDirty);
                List<CaveFormations> formations = StrataCaveEvaluator.FormationsOf(nodes);
                List<StrataFormationPrototype> formationPrototypes = StrataFormationPlacer.Prototypes(formations);
                if (world.Formations != null && !StrataTreeEvaluator.SameList(world.Formations.Prototypes, formationPrototypes))
                {
                    world.SetFormationPrototypes(formationPrototypes);
                    formationDirty = StrataEvaluator.WorldRect(grid);
                }
                if (formationDirty.width > 0 && formationDirty.height > 0) state.FormationDirty = StrataEvaluator.Union(state.FormationDirty, formationDirty);
                List<StrataCaveFeature> caves = StrataCaveEvaluator.CavesOf(nodes);
                bool hasCaves = caves.Count > 0 || world.Caves?.BrickCount > 0 || world.Heightfield.Holes.HoledCount > 0;
                int paletteVersion = world.Palette.Version;
                if (paletteVersion != state.PaletteVersion)
                {
                    state.PaletteVersion = paletteVersion;
                    world.RebuildPalette();
                    splatAll = true;
                    // cave vertices hold palette indices
                    if (hasCaves) caveDirty = StrataEvaluator.WorldRect(grid);
                    state.CaveFull = true;
                }
                List<StrataBiomeArea> biomes = StrataBiomeEvaluator.BiomesOf(nodes);
                int biomeSignature = StrataBiomeEvaluator.Signature(biomes);
                if (biomeSignature != state.BiomeSignature || state.NeedsFull)
                {
                    bool underground = biomes.Exists(b => b.Kind == StrataBiomeKind.Underground);
                    // cave layers and formations follow the underground biomes
                    if (hasCaves && (underground || state.HadUnderground)) caveDirty = StrataEvaluator.WorldRect(grid);
                    state.BiomeSignature = biomeSignature;
                    state.HadUnderground = underground;
                    world.SetBiomeAreas(biomes);
                    if (biomes.Count > 0) StrataBiomeEvaluator.Evaluate(world, biomes, AllTiles(grid));
                    else world.Biomes.Fill();
                    splatAll = true;
                }
                StrataWorldSettings settings = world.Settings;
                int moistureSignature = System.HashCode.Combine(settings.UseSeaLevel, settings.SeaLevel, settings.MoistureSpacing,
                                                                settings.FlowForFullMoisture, settings.WaterReach);
                if (moistureSignature != state.MoistureSignature)
                {
                    state.MoistureSignature = moistureSignature;
                    splatAll = true;
                }
                int densitySignature = StrataTreeEvaluator.DensitySignature(nodes);
                int placementSignature = StrataTreeEvaluator.PlacementSignature(nodes, world.Settings.TreeSeed);
                List<StrataGrassPrototype> grass = StrataTreeEvaluator.GrassPrototypes(nodes);
                if (!StrataTreeEvaluator.SameList(world.GrassPrototypes, grass))
                {
                    world.SetGrassPrototypes(grass);
                    splatAll = true;
                }
                if (densitySignature != state.DensitySignature)
                {
                    state.DensitySignature = densitySignature;
                    state.PlacementSignature = placementSignature;
                    splatAll = true;
                }
                else if (placementSignature != state.PlacementSignature)
                {
                    state.PlacementSignature = placementSignature;
                    // only species changed: the densities stand, so trees are placed again from them
                    int slices = StrataTreeEvaluator.Slices(StrataTreeEvaluator.ScatteredForests(nodes).Count);
                    if (state.PendingTrees.Count == 0 && Trees.HasDensities(world, slices))
                    {
                        Trees.Place(world, nodes, AllTiles(grid));
                        SceneView.RepaintAll();
                    }
                    else splatAll = true;
                }
                PlacePendingTrees(world, state, nodes);
                if (state.NeedsFull)
                {
                    state.NeedsFull = false;
                    // a world with no features keeps its baked (or flat) heights and splats
                    if (nodes.Count == 0) continue;
                    dirty = StrataEvaluator.WorldRect(grid);
                    splatAll = true;
                    if (hasCaves) caveDirty = StrataEvaluator.WorldRect(grid);
                    state.CaveFull = true;
                }
                if (nodes.Count == 0) continue;

                bool heightsChanged = dirty.width > 0 && dirty.height > 0;
                RectInt commit = heightsChanged ? Evaluate(world, state, nodes, dirty) : default;
                if (heightsChanged || splatAll) EvaluateSplats(world, state, nodes, commit, splatAll);
                if (heightsChanged)
                    foreach (StrataCaveFeature cave in caves)
                    {
                        RectInt under = StrataEvaluator.Intersect(StrataEvaluator.FeatureRect(grid, cave), commit);
                        if (under.width > 0 && under.height > 0) caveDirty = StrataEvaluator.Union(caveDirty, under);
                    }
                if (caveDirty.width > 0 && caveDirty.height > 0) state.CaveDirty = StrataEvaluator.Union(state.CaveDirty, caveDirty);
                if (state.CaveDirty.width > 0 && state.CaveDirty.height > 0 && state.PendingReadbacks == 0)
                {
                    // the formations of the tiles whose caves change are placed again with them
                    state.FormationDirty = StrataEvaluator.Union(state.FormationDirty, state.CaveDirty);
                    EvaluateCaves(world, state, nodes, caves);
                }
                else if (state.CoarseDirty.width > 0 && state.CoarseDirty.height > 0 && !Dragging && state.PendingReadbacks == 0)
                {
                    // the drag ended: the coarse levels catch up
                    RectInt coarse = state.CoarseDirty;
                    state.CoarseDirty = default;
                    Caves.Mesh(world, caves, TilesTouching(grid, coarse), coarse);
                    SceneView.RepaintAll();
                }
                if (state.FormationDirty.width > 0 && state.FormationDirty.height > 0 && state.CaveDirty.width == 0 && state.PendingReadbacks == 0)
                {
                    List<int> tiles = TilesTouching(grid, state.FormationDirty);
                    state.FormationDirty = default;
                    // bricks the cave preview hasn't meshed yet are meshed first (an empty area keeps the rest)
                    if (formations.Count > 0) Caves.Mesh(world, caves, tiles, new RectInt(0, 0, 0, 0), Dragging);
                    Caves.PlaceFormations(world, formations, formationPrototypes, tiles);
                    SceneView.RepaintAll();
                }
            }
        }

        private static bool Dragging => GUIUtility.hotControl != 0;

        // Cuts the holes and meshes the caves of the tiles the pending cave area touches, then repaints their splats and
        // trees (the caves' paint and tree effects, and no trees in holes).
        private static void EvaluateCaves(StrataWorld world, WorldState state, List<StrataEvaluator.Node> nodes, List<StrataCaveFeature> caves)
        {
            Stopwatch watch = Stopwatch.StartNew();
            StrataGrid grid = world.Heightfield.Grid;
            RectInt area = state.CaveDirty;
            state.CaveDirty = default;
            List<int> tiles = TilesTouching(grid, area);
            Caves.EvaluateSurface(world, caves, tiles, state.CaveFull ? null : area);
            // while a handle is dragged only level 0 follows; the coarse levels catch up once it is let go
            bool fineOnly = Dragging && !state.CaveFull;
            Caves.Mesh(world, caves, tiles, state.CaveFull ? null : area, fineOnly);
            if (fineOnly) state.CoarseDirty = StrataEvaluator.Union(state.CoarseDirty, area);
            state.CaveFull = false;
            LastCaveMilliseconds = watch.Elapsed.TotalMilliseconds;
            world.NotifyHeightsChanged();
            EvaluateSplats(world, state, nodes, area, false);
        }

        // Stores the tree densities that have arrived (in order) and places trees on their tiles and the tiles beside
        // them, whose edge trees see the changed densities through their margins.
        private static void PlacePendingTrees(StrataWorld world, WorldState state, List<StrataEvaluator.Node> nodes)
        {
            if (state.PendingTrees.Count == 0) return;
            StrataGrid grid = world.Heightfield.Grid;
            HashSet<int> tiles = new();
            int done = 0;
            for (; done < state.PendingTrees.Count; done++)
            {
                (int tile, AsyncGPUReadbackRequest request, int slices) = state.PendingTrees[done];
                if (!request.done) break;
                Trees.Store(world, slices, tile, request);
                int tx = tile % grid.TilesX, tz = tile / grid.TilesX;
                for (int z = Mathf.Max(0, tz - 1); z <= Mathf.Min(grid.TilesZ - 1, tz + 1); z++)
                for (int x = Mathf.Max(0, tx - 1); x <= Mathf.Min(grid.TilesX - 1, tx + 1); x++)
                    tiles.Add(grid.TileIndex(x, z));
            }
            state.PendingTrees.RemoveRange(0, done);
            if (tiles.Count == 0) return;
            Trees.Place(world, nodes, tiles);
            SceneView.RepaintAll();
        }

        private static void EvaluateSplats(StrataWorld world, WorldState state, List<StrataEvaluator.Node> nodes, RectInt commit, bool all)
        {
            Stopwatch watch = Stopwatch.StartNew();
            StrataGrid grid = world.Heightfield.Grid;
            if (StrataSplatEvaluator.NeedsMoisture(world, nodes))
            {
                Splats.UpdateMoisture(world, nodes);
                all = true;
            }
            world.SetStrataDefinitions(StrataSplatEvaluator.Definitions(StrataSplatEvaluator.RockStrataOf(nodes)));
            List<int> tiles = all ? AllTiles(grid) : TilesTouching(grid, commit);
            StrataTreeEvaluator.Context trees = null;
            if (StrataTreeEvaluator.AllForests(nodes).Count > 0)
            {
                trees = new StrataTreeEvaluator.Context();
                trees.Forests.AddRange(StrataTreeEvaluator.ScatteredForests(nodes));
            }
            else if (world.Trees != null && world.Trees.Count > 0) world.Trees.Clear();
            Splats.Evaluate(world, nodes, tiles, Evaluator, trees, GrassTarget(world));
            if (trees != null)
                foreach ((int tile, AsyncGPUReadbackRequest request) in trees.Requests)
                    state.PendingTrees.Add((tile, request, StrataTreeEvaluator.Slices(trees.Forests.Count)));
            LastSplatMilliseconds = watch.Elapsed.TotalMilliseconds;

            // where detail patches go follows the strata maps, read back as they finish
            StrataSplatmap splatmap = world.Splatmap;
            foreach (int tile in tiles)
            {
                int slice = tile;
                AsyncGPUReadback.Request(splatmap.Strata, 0, 0, grid.SplatSamples, 0, grid.SplatSamples, slice, 1, request =>
                {
                    if (request.hasError || world == null || world.Splatmap != splatmap) return;
                    world.UpdateStrataLeaves(slice, request.GetData<ushort>());
                });
            }
            SceneView.RepaintAll();
        }

        // The world's grass density array, if it has grass prototypes.
        public static StrataSplatEvaluator.GrassTarget GrassTarget(StrataWorld world) =>
            world.Grass != null && world.GrassPrototypes.Count > 0
                ? new StrataSplatEvaluator.GrassTarget { Prototypes = world.GrassPrototypes, Output = world.Grass.Texture }
                : null;

        public static List<int> AllTiles(StrataGrid grid)
        {
            List<int> tiles = new();
            for (int i = 0; i < grid.TileCount; i++) tiles.Add(i);
            return tiles;
        }

        // Tiles whose samples (shared edges included) overlap an area of world samples.
        private static List<int> TilesTouching(StrataGrid grid, RectInt area)
        {
            List<int> tiles = new();
            for (int tz = 0; tz < grid.TilesZ; tz++)
            for (int tx = 0; tx < grid.TilesX; tx++)
            {
                RectInt overlap = StrataEvaluator.Intersect(new RectInt(tx * grid.Resolution, tz * grid.Resolution, grid.Samples, grid.Samples), area);
                if (overlap.width > 0 && overlap.height > 0) tiles.Add(grid.TileIndex(tx, tz));
            }
            return tiles;
        }

        // Records the current feature states and returns the area covering everything that changed.
        private static RectInt Diff(WorldState state, StrataGrid grid, List<StrataEvaluator.Node> nodes, out bool splatAll, out RectInt caveDirty,
                                    out RectInt formationDirty)
        {
            RectInt dirty = default;
            caveDirty = default;
            formationDirty = default;
            splatAll = false;
            s_seen.Clear();
            int order = 0;
            foreach (StrataFeature feature in StrataEvaluator.Flatten(nodes))
            {
                s_seen.Add(feature);
                FeatureState now = new()
                {
                    Version = feature.Version,
                    Matrix = feature.transform.localToWorldMatrix,
                    Order = order++,
                    Area = StrataEvaluator.FeatureRect(grid, feature),
                    Heights = feature.AffectsHeights
                };
                bool added = !state.Features.TryGetValue(feature, out FeatureState before);
                bool changed = added || before.Version != now.Version || before.Matrix != now.Matrix || before.Order != now.Order || !before.Area.Equals(now.Area);
                // a cave changes only the ground around it, once its tiles' caves are evaluated again
                if (feature is StrataCaveFeature)
                {
                    if (changed) caveDirty = StrataEvaluator.Union(added ? caveDirty : StrataEvaluator.Union(caveDirty, before.Area), now.Area);
                    // a tunnel's mouth carve shapes the heights too
                    if (changed && (now.Heights || before.Heights)) dirty = StrataEvaluator.Union(added ? dirty : StrataEvaluator.Union(dirty, before.Area), now.Area);
                }
                else if (feature is CaveFormations)
                {
                    if (changed) formationDirty = StrataEvaluator.Union(added ? formationDirty : StrataEvaluator.Union(formationDirty, before.Area), now.Area);
                }
                else if (changed && !feature.AffectsHeights) splatAll = true;
                else if (added) dirty = StrataEvaluator.Union(dirty, now.Area);
                else if (changed) dirty = StrataEvaluator.Union(StrataEvaluator.Union(dirty, before.Area), now.Area);
                state.Features[feature] = now;
            }

            s_removed.Clear();
            foreach (KeyValuePair<StrataFeature, FeatureState> pair in state.Features)
                if (pair.Key == null || !s_seen.Contains(pair.Key))
                {
                    s_removed.Add(pair.Key);
                    if (pair.Key is CaveFormations)
                    {
                        formationDirty = StrataEvaluator.Union(formationDirty, pair.Value.Area);
                        continue;
                    }
                    if (pair.Key is StrataCaveFeature)
                    {
                        caveDirty = StrataEvaluator.Union(caveDirty, pair.Value.Area);
                        if (pair.Value.Heights) dirty = StrataEvaluator.Union(dirty, pair.Value.Area);
                        continue;
                    }
                    dirty = StrataEvaluator.Union(dirty, pair.Value.Area);
                    // a removed feature may have painted anywhere (or been the Auto Splat), so repaint everything
                    splatAll = true;
                }
            foreach (StrataFeature feature in s_removed) state.Features.Remove(feature);
            return dirty;
        }

        private static RectInt Evaluate(StrataWorld world, WorldState state, List<StrataEvaluator.Node> nodes, RectInt dirty)
        {
            Stopwatch watch = Stopwatch.StartNew();
            StrataHeightfield heightfield = world.Heightfield;
            StrataGrid grid = heightfield.Grid;
            RectInt commit = StrataEvaluator.Expand(grid, nodes, dirty, out RectInt region);
            RenderTexture heights = Evaluator.Evaluate(grid, nodes, region);
            List<int> tiles = Evaluator.WriteTiles(grid, heights, region, commit, heightfield.Texture);
            RenderTexture.ReleaseTemporary(heights);
            LastMilliseconds = watch.Elapsed.TotalMilliseconds;
            LastArea = commit;

            foreach (int tile in tiles)
            {
                int slice = tile;
                state.PendingReadbacks++;
                AsyncGPUReadback.Request(heightfield.Texture, 0, 0, grid.Samples, 0, grid.Samples, slice, 1, request =>
                {
                    state.PendingReadbacks--;
                    if (request.hasError || world == null || world.Heightfield != heightfield) return;
                    CopyTile(heightfield, slice, request.GetData<ushort>());
                    world.Trees?.RebuildTile(slice);
                    world.NotifyHeightsChanged();
                });
            }
            SceneView.RepaintAll();
            return commit;
        }

        public static void CopyTile(StrataHeightfield heightfield, int tile, NativeArray<ushort> data)
        {
            heightfield.Readers.Complete();
            StrataGrid grid = heightfield.Grid;
            NativeArray<ushort>.Copy(data, 0, heightfield.Heights, tile * grid.SamplesPerTile, grid.SamplesPerTile);
            NativeArray<int> tiles = new(1, Allocator.TempJob);
            tiles[0] = tile;
            heightfield.UpdateBounds(tiles);
            tiles.Dispose();
        }
    }
}
