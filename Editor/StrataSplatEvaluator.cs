using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Strata.Editor
{
    // Paints a world's splats on the GPU, after its heights are final: tile by tile, it starts each texel at the
    // palette's first layer, then walks the features in hierarchy order, running the palette's rules at the Auto Splat
    // feature and painting each feature's layer where that feature acts, and writes the four strongest layers into the
    // world's splat tile arrays. The rules' moisture input comes from a coarse world-wide map (flow accumulation over
    // the heights, and distance to water below sea level or under River splines) rebuilt by UpdateMoisture. Each tile
    // also gets its rock strata map: the strongest Rock Strata over each texel and its strength. Given a tree context, it
    // then works out each forest's density over the tile (Tree Scatters and tree effects in hierarchy order) and starts
    // reading it back, with moisture, for StrataTreeEvaluator to place trees from.
    public sealed class StrataSplatEvaluator : IDisposable
    {
        private const string ShaderPath = "Packages/com.qhenshaw.terrain/Editor/Compute/StrataSplat.compute";
        private const int CurveSamples = 64;
        private const int RuleFloat4s = 6;
        private const int MaxFlowIterations = 768;

        private sealed class Moisture
        {
            public RenderTexture Map;
            public Vector4 Coarse;
            public Vector2Int Size;
        }

        private readonly ComputeShader _shader;
        private readonly ComputeBuffer _curve = new(CurveSamples, 4);
        private readonly ComputeBuffer _profile = new(CurveSamples, 4);
        private readonly ComputeBuffer _paintCurve = new(CurveSamples, 4);
        private readonly ComputeBuffer _dummyBuffer = new(4, 16);
        private ComputeBuffer _forestLayers;
        private ComputeBuffer _grassEntries;
        private RenderTexture _grassState;
        private RenderTexture _treeState;
        private RenderTexture _treeOut;
        // set for the evaluation in progress
        private StrataPalette _palette;
        private RenderTexture _moistureMap;
        private readonly float[] _curveData = new float[CurveSamples];
        private ComputeBuffer _rules;
        private ComputeBuffer _riverPoints;
        private RenderTexture _index;
        private RenderTexture _weight;
        private RenderTexture _rock;
        private readonly RenderTexture _dummyMoisture;
        private readonly RenderTexture _dummyDelta;
        private readonly Dictionary<StrataWorld, Moisture> _moisture = new();

        public StrataSplatEvaluator()
        {
            _shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            _dummyMoisture = NewTexture(1, 1, RenderTextureFormat.RFloat);
            _dummyDelta = NewTexture(2, 2, RenderTextureFormat.RFloat);
        }

        public void Dispose()
        {
            _curve.Release();
            _profile.Release();
            _paintCurve.Release();
            _dummyBuffer.Release();
            _rules?.Release();
            _riverPoints?.Release();
            _forestLayers?.Release();
            _grassEntries?.Release();
            Destroy(_grassState);
            Destroy(_treeState);
            Destroy(_treeOut);
            Destroy(_index);
            Destroy(_weight);
            Destroy(_rock);
            Destroy(_dummyMoisture);
            Destroy(_dummyDelta);
            foreach (Moisture moisture in _moisture.Values) Destroy(moisture.Map);
            _moisture.Clear();
        }

        // The world's Rock Strata features in hierarchy order; a strata map's definition index points into this list.
        public static List<RockStrata> RockStrataOf(List<StrataEvaluator.Node> nodes)
        {
            List<RockStrata> strata = new();
            foreach (StrataFeature feature in StrataEvaluator.Flatten(nodes))
                if (feature is RockStrata rock && strata.Count < 255) strata.Add(rock);
            return strata;
        }

        public static StrataBandDefinition[] Definitions(List<RockStrata> strata) => strata.ConvertAll(s => s.Definition).ToArray();

        public static AutoSplat FindAutoSplat(List<StrataEvaluator.Node> nodes)
        {
            foreach (StrataFeature feature in StrataEvaluator.Flatten(nodes))
                if (feature is AutoSplat auto) return auto;
            return null;
        }

        // Whether any rule reads moisture, so the moisture map must follow height changes everywhere.
        public static bool NeedsMoisture(StrataWorld world, List<StrataEvaluator.Node> nodes)
        {
            if (world.Settings.Palette == null || FindAutoSplat(nodes) == null) return false;
            foreach (StrataLayer layer in world.Settings.Palette.Layers)
                if (layer != null && layer.Rule.Enabled && layer.Rule.Moisture.Enabled) return true;
            return false;
        }

        // Rebuilds the world's moisture map from its current GPU heights.
        public void UpdateMoisture(StrataWorld world, List<StrataEvaluator.Node> nodes)
        {
            AutoSplat auto = FindAutoSplat(nodes);
            StrataGrid grid = world.Heightfield.Grid;
            StrataWorldSettings settings = world.Settings;
            float spacing = settings.MoistureSpacing;
            Vector2Int size = new((int)math.ceil(grid.Size.x / spacing) + 1, (int)math.ceil(grid.Size.y / spacing) + 1);
            if (!_moisture.TryGetValue(world, out Moisture moisture)) _moisture[world] = moisture = new Moisture();
            if (moisture.Map == null || moisture.Size != size)
            {
                Destroy(moisture.Map);
                moisture.Map = NewTexture(size.x, size.y, RenderTextureFormat.RFloat);
                moisture.Size = size;
            }
            moisture.Coarse = new Vector4(grid.Origin.x, grid.Origin.y, spacing, 0f);

            SetHeights(world);
            _shader.SetVector("_Coarse", moisture.Coarse);
            _shader.SetInts("_CoarseSize", size.x, size.y);
            float cellArea = spacing * spacing;
            float flowCells = math.max(2f, (auto != null ? auto.FlowForFullMoisture : 40000f) / cellArea);
            _shader.SetVector("_Water", new Vector4(settings.UseSeaLevel ? 1f : 0f, settings.SeaLevel, auto != null ? auto.WaterReach : 40f, math.log(flowCells)));

            RenderTexture heights = Temporary(size, RenderTextureFormat.RFloat);
            RenderTexture receivers = Temporary(size, RenderTextureFormat.RInt);
            RenderTexture flowA = Temporary(size, RenderTextureFormat.RFloat), flowB = Temporary(size, RenderTextureFormat.RFloat);
            RenderTexture seedA = Temporary(size, RenderTextureFormat.RGFloat), seedB = Temporary(size, RenderTextureFormat.RGFloat);

            int kernel = _shader.FindKernel("MoistureHeights");
            _shader.SetTexture(kernel, "_CoarseHeight", heights);
            Dispatch(kernel, size);

            kernel = _shader.FindKernel("FlowReceivers");
            _shader.SetTexture(kernel, "_CoarseHeightIn", heights);
            _shader.SetTexture(kernel, "_Receiver", receivers);
            Dispatch(kernel, size);

            Clear(flowA, 1f);
            kernel = _shader.FindKernel("FlowStep");
            int iterations = math.min(math.max(size.x, size.y), MaxFlowIterations);
            for (int i = 0; i < iterations; i++)
            {
                _shader.SetTexture(kernel, "_ReceiverIn", receivers);
                _shader.SetTexture(kernel, "_FlowIn", flowA);
                _shader.SetTexture(kernel, "_FlowOut", flowB);
                Dispatch(kernel, size);
                (flowA, flowB) = (flowB, flowA);
            }

            kernel = _shader.FindKernel("WaterSeed");
            _shader.SetTexture(kernel, "_CoarseHeightIn", heights);
            _shader.SetTexture(kernel, "_Seed", seedA);
            Dispatch(kernel, size);
            SeedRivers(nodes, seedA);

            kernel = _shader.FindKernel("JumpFlood");
            for (int step = math.ceilpow2(math.max(size.x, size.y)) / 2; step >= 1; step /= 2)
            {
                _shader.SetInt("_JumpStep", step);
                _shader.SetTexture(kernel, "_SeedIn", seedA);
                _shader.SetTexture(kernel, "_Seed", seedB);
                Dispatch(kernel, size);
                (seedA, seedB) = (seedB, seedA);
            }

            kernel = _shader.FindKernel("MoistureCombine");
            _shader.SetTexture(kernel, "_FlowIn", flowA);
            _shader.SetTexture(kernel, "_SeedIn", seedA);
            _shader.SetTexture(kernel, "_Moisture", moisture.Map);
            Dispatch(kernel, size);

            foreach (RenderTexture texture in new[] { heights, receivers, flowA, flowB, seedA, seedB }) RenderTexture.ReleaseTemporary(texture);
        }

        private void SeedRivers(List<StrataEvaluator.Node> nodes, RenderTexture seeds)
        {
            List<Vector4> points = new();
            foreach (StrataFeature feature in StrataEvaluator.Flatten(nodes))
                if (feature is SplineFeature { Mode: StrataSplineMode.River } river)
                    foreach (SplineFeature.Point point in river.Points)
                        points.Add(new Vector4(point.Position.x, point.Position.z, point.Shape.x * 0.5f, 0f));
            if (points.Count == 0) return;
            if (_riverPoints == null || _riverPoints.count < points.Count)
            {
                _riverPoints?.Release();
                _riverPoints = new ComputeBuffer(math.ceilpow2(points.Count), 16);
            }
            _riverPoints.SetData(points);
            int kernel = _shader.FindKernel("RiverSeed");
            _shader.SetBuffer(kernel, "_RiverPoints", _riverPoints);
            _shader.SetInt("_RiverCount", points.Count);
            _shader.SetTexture(kernel, "_Seed", seeds);
            _shader.Dispatch(kernel, (points.Count + 63) / 64, 1, 1);
        }

        // Paints the given tiles' splats into the world's splat arrays, with a tree context their forest densities, and
        // with a grass target their grass densities.
        public void Evaluate(StrataWorld world, List<StrataEvaluator.Node> nodes, IEnumerable<int> tiles, StrataEvaluator heights,
                             StrataTreeEvaluator.Context trees = null, GrassTarget grass = null)
        {
            StrataGrid grid = world.Heightfield.Grid;
            StrataPalette palette = world.Settings.Palette;
            int layerCount = palette != null ? palette.Layers.Count : 0;
            EnsureTargets(grid);
            UploadRules(palette);

            SetHeights(world);
            _moisture.TryGetValue(world, out Moisture moisture);
            _palette = palette;
            _moistureMap = moisture?.Map != null ? moisture.Map : _dummyMoisture;
            _shader.SetVector("_Coarse", moisture?.Coarse ?? new Vector4(grid.Origin.x, grid.Origin.y, 1e6f, 0f));
            _shader.SetInts("_CoarseSize", moisture?.Size.x ?? 1, moisture?.Size.y ?? 1);
            _shader.SetFloat("_SplatSpacing", grid.SplatSpacing);
            _shader.SetInt("_LayerCount", layerCount);
            _shader.SetFloat("_CurvatureRadius", math.max(grid.SplatSpacing * 2f, 4f));

            List<StrataFeature> features = new(StrataEvaluator.Flatten(nodes));
            List<RockStrata> strata = RockStrataOf(nodes);
            int init = _shader.FindKernel("SplatInit");
            int rockInit = _shader.FindKernel("RockInit");
            int rockMap = _shader.FindKernel("RockStrataMap");
            int rockWrite = _shader.FindKernel("RockWrite");
            int rules = _shader.FindKernel("SplatRules");
            int paint = _shader.FindKernel("PaintFeature");
            int write = _shader.FindKernel("SplatWrite");
            Vector2Int size = new(grid.SplatSamples, grid.SplatSamples);

            foreach (int tile in tiles)
            {
                int tx = tile % grid.TilesX, tz = tile / grid.TilesX;
                _shader.SetInts("_SplatTile", tile, tx, tz, grid.SplatResolution);
                Bind(init);
                Dispatch(init, size);

                float2 tileMin = grid.TileOrigin(tx, tz);
                Rect tileRect = new(tileMin, new Vector2(grid.TileSize, grid.TileSize));
                foreach (StrataFeature feature in features)
                {
                    if (feature is AutoSplat)
                    {
                        if (layerCount < 2) continue;
                        Bind(rules);
                        _shader.SetTexture(rules, "_MoistureIn", moisture?.Map != null ? moisture.Map : _dummyMoisture);
                        _shader.SetBuffer(rules, "_Rules", _rules);
                        Dispatch(rules, size);
                    }
                    else if (feature.Paints && palette != null && palette.IndexOf(feature.Paint.Layer) >= 0)
                    {
                        if (!feature.Unbounded && feature is not SplineFeature && !feature.Bounds.Overlaps(tileRect)) continue;
                        if (SetPaint(paint, feature, palette, heights)) Dispatch(paint, size);
                    }
                }

                Bind(write);
                _shader.SetTexture(write, "_OutIndices", world.Splatmap.Indices);
                _shader.SetTexture(write, "_OutWeights", world.Splatmap.Weights);
                Dispatch(write, size);

                _shader.SetTexture(rockInit, "_RockState", _rock);
                Dispatch(rockInit, size);
                for (int i = 0; i < strata.Count; i++)
                {
                    RockStrata rock = strata[i];
                    if (!rock.Unbounded && !rock.Bounds.Overlaps(tileRect)) continue;
                    SetMaskAndStrength(rock);
                    SetFootprint(rock.transform, rock.EdgeFalloff, rock.FalloffCurve);
                    _shader.SetVector("_StrataSlope", new Vector4(rock.SlopeStart, rock.SlopeFull, i, rock.Unbounded ? 1f : 0f));
                    _shader.SetTexture(rockMap, "_RockState", _rock);
                    _shader.SetBuffer(rockMap, "_Curve", _curve);
                    Dispatch(rockMap, size);
                }
                _shader.SetTexture(rockWrite, "_RockState", _rock);
                _shader.SetTexture(rockWrite, "_OutStrata", world.Splatmap.Strata);
                Dispatch(rockWrite, size);

                if (trees != null || grass?.Prototypes.Count > 0) EvaluateVegetation(trees, grass, tile, tileRect, features, heights, size);
            }
        }

        // A grass evaluation: the prototypes the world's grass density array holds (its slices per tile), and the array.
        public sealed class GrassTarget
        {
            public IReadOnlyList<StrataGrassPrototype> Prototypes;
            public RenderTexture Output;
        }

        // The tile's forest densities (then what the all-forest effects leave, then moisture) into _treeOut with a
        // readback of them added to the tree context, and its grass densities straight into the grass target.
        private void EvaluateVegetation(StrataTreeEvaluator.Context trees, GrassTarget grass, int tile, Rect tileRect, List<StrataFeature> features,
                                        StrataEvaluator heights, Vector2Int size)
        {
            int forests = trees?.Forests.Count ?? 0;
            int grassTypes = grass?.Prototypes.Count ?? 0;
            EnsureTreeTargets(size.x, forests);
            EnsureGrassTarget(size.x, grassTypes);
            int treeInit = _shader.FindKernel("TreeInit");
            int treeScatter = _shader.FindKernel("TreeScatter");
            int effect = _shader.FindKernel("TreeEffect");
            int treeWrite = _shader.FindKernel("TreeWrite");
            int grassInit = _shader.FindKernel("GrassInit");
            int grassScatter = _shader.FindKernel("GrassScatter");
            int grassWrite = _shader.FindKernel("GrassWrite");
            _shader.SetInt("_TreeSlices", forests);
            _shader.SetInt("_GrassSlices", grassTypes);
            _shader.SetTexture(treeInit, "_TreeState", _treeState);
            Dispatch(treeInit, size);
            _shader.SetTexture(grassInit, "_GrassState", _grassState);
            Dispatch(grassInit, size);

            foreach (StrataFeature feature in features)
            {
                if (!feature.Unbounded && feature is not SplineFeature && !feature.Bounds.Overlaps(tileRect)) continue;
                if (feature is TreeScatter treeFeature)
                {
                    int slice = trees != null ? trees.Forests.IndexOf(treeFeature.Forest) : -1;
                    if (slice < 0) continue;
                    SetScatter(treeScatter, feature, treeFeature.EdgeFalloff, treeFeature.FalloffCurve);
                    SetForest(treeScatter, treeFeature.Forest);
                    _shader.SetTexture(treeScatter, "_TreeState", _treeState);
                    _shader.SetInt("_TreeSlice", slice);
                    _shader.SetVector("_TreeEffect", new Vector4(1f, treeFeature.Replace ? 1f : 0f, 0f, 0f));
                    Dispatch(treeScatter, size);
                }
                else if (feature is GrassScatter grassFeature)
                {
                    if (grassTypes == 0 || grassFeature.Meadow == null || !SetMeadow(grassFeature.Meadow, grass.Prototypes)) continue;
                    SetScatter(grassScatter, feature, grassFeature.EdgeFalloff, grassFeature.FalloffCurve);
                    StrataMeadow meadow = grassFeature.Meadow;
                    SetRule(grassScatter, meadow.Density, meadow.Height, meadow.Slope, meadow.Moisture, meadow.Layers, meadow.LayerSensitivity,
                            meadow.NoiseScale, meadow.NoiseAmount, meadow.Seed);
                    _shader.SetBuffer(grassScatter, "_GrassEntries", _grassEntries);
                    _shader.SetTexture(grassScatter, "_GrassState", _grassState);
                    _shader.SetVector("_TreeEffect", new Vector4(1f, grassFeature.Replace ? 1f : 0f, 0f, 0f));
                    Dispatch(grassScatter, size);
                }
                else if (feature.Trees.Mode == StrataTreeMode.Density)
                {
                    int slice = feature.Trees.Forest != null ? trees?.Forests.IndexOf(feature.Trees.Forest) ?? -1 : -1;
                    if (feature.Trees.Forest != null && slice < 0) continue;
                    if (!SetInfluence(effect, feature, heights, new Vector4(0f, 0f, math.max(feature.Trees.WidthScale, 0f), 1f))) continue;
                    _shader.SetTexture(effect, "_TreeState", _treeState);
                    _shader.SetTexture(effect, "_GrassState", _grassState);
                    _shader.SetInt("_TreeSlice", slice);
                    _shader.SetVector("_TreeEffect", new Vector4(feature.Trees.Density, 0f, 0f, 0f));
                    Dispatch(effect, size);
                }
            }

            if (trees != null)
            {
                _shader.SetTexture(treeWrite, "_TreeState", _treeState);
                _shader.SetTexture(treeWrite, "_TreeOut", _treeOut);
                _shader.SetTexture(treeWrite, "_MoistureIn", _moistureMap);
                Dispatch(treeWrite, size);
                trees.Requests.Add((tile, AsyncGPUReadback.Request(_treeOut, 0, 0, size.x, 0, size.y, 0, forests + 2)));
            }
            if (grassTypes > 0)
            {
                _shader.SetTexture(grassWrite, "_GrassState", _grassState);
                _shader.SetTexture(grassWrite, "_GrassOut", grass.Output);
                Dispatch(grassWrite, size);
            }
        }

        // What a scatter kernel needs from its feature: the splats, mask, strength and footprint.
        private void SetScatter(int kernel, StrataFeature feature, float edgeFalloff, AnimationCurve falloffCurve)
        {
            Bind(kernel);
            SetMaskAndStrength(feature);
            SetFootprint(feature.transform, edgeFalloff, falloffCurve);
            _shader.SetBuffer(kernel, "_Curve", _curve);
            _shader.SetFloat("_Unbounded", feature.Unbounded ? 1f : 0f);
        }

        // The meadow's entries for GrassScatter (prototype slice, weight, ranges); false if none are in the target.
        private bool SetMeadow(StrataMeadow meadow, IReadOnlyList<StrataGrassPrototype> prototypes)
        {
            List<Vector4> entries = new();
            foreach (StrataMeadowEntry entry in meadow.Entries)
            {
                int slice = -1;
                for (int i = 0; i < prototypes.Count; i++)
                    if (prototypes[i] == entry?.Prototype) slice = i;
                if (slice < 0) continue;
                entries.Add(new Vector4(slice, entry.Weight, 0f, 0f));
                entries.Add(entry.Height.Packed);
                entries.Add(entry.Slope.Packed);
                entries.Add(entry.Moisture.Packed);
            }
            if (entries.Count == 0) return false;
            if (_grassEntries == null || _grassEntries.count < entries.Count)
            {
                _grassEntries?.Release();
                _grassEntries = new ComputeBuffer(math.ceilpow2(entries.Count), 16);
            }
            _grassEntries.SetData(entries);
            _shader.SetInt("_GrassEntryCount", entries.Count / 4);
            return true;
        }

        private void SetForest(int kernel, StrataForest forest) =>
            SetRule(kernel, forest.Density, forest.Height, forest.Slope, forest.Moisture, forest.Layers, forest.LayerSensitivity,
                    forest.NoiseScale, forest.NoiseAmount, forest.Seed);

        // The forest (or meadow) rule FeatureInfluence and the scatter kernels read, and the moisture it samples.
        private void SetRule(int kernel, float density, StrataRange height, StrataRange slope, StrataRange moisture, IReadOnlyList<StrataForestLayer> ruleLayers,
                             float sensitivity, float noiseScale, float noiseAmount, int seed)
        {
            _shader.SetVector("_Forest", new Vector4(density, 1f / math.max(noiseScale, 1f), noiseAmount, seed));
            _shader.SetVector("_ForestHeight", height.Packed);
            _shader.SetVector("_ForestSlope", slope.Packed);
            _shader.SetVector("_ForestMoisture", moisture.Packed);
            List<Vector4> layers = new();
            foreach (StrataForestLayer layer in ruleLayers)
            {
                int index = _palette != null ? _palette.IndexOf(layer.Layer) : -1;
                if (index >= 0) layers.Add(new Vector4(index, layer.Avoid ? 1f : 0f, 0f, 0f));
            }
            EnsureForestLayers(layers.Count);
            if (layers.Count > 0) _forestLayers.SetData(layers);
            _shader.SetVector("_ForestLayerInfo", new Vector4(layers.Count, sensitivity, 0f, 0f));
            _shader.SetBuffer(kernel, "_ForestLayers", _forestLayers);
            _shader.SetTexture(kernel, "_MoistureIn", _moistureMap);
        }

        private void EnsureGrassTarget(int size, int types)
        {
            if (_grassState != null && _grassState.width == size && _grassState.volumeDepth == System.Math.Max(1, types)) return;
            Destroy(_grassState);
            _grassState = NewArray(size, System.Math.Max(1, types), GraphicsFormat.R32_SFloat);
        }

        private void EnsureForestLayers(int count)
        {
            if (_forestLayers != null && _forestLayers.count >= count) return;
            _forestLayers?.Release();
            _forestLayers = new ComputeBuffer(math.max(8, math.ceilpow2(count)), 16);
        }

        private void EnsureTreeTargets(int size, int forests)
        {
            if (_treeOut != null && _treeOut.width == size && _treeOut.volumeDepth == forests + 2) return;
            Destroy(_treeState);
            Destroy(_treeOut);
            _treeState = NewArray(size, forests + 1, GraphicsFormat.R32_SFloat);
            _treeOut = NewArray(size, forests + 2, GraphicsFormat.R8_UNorm);
        }

        private static RenderTexture NewArray(int size, int slices, GraphicsFormat format)
        {
            RenderTexture texture = new(size, size, format, GraphicsFormat.None)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = slices,
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            return texture;
        }

        private bool SetPaint(int kernel, StrataFeature feature, StrataPalette palette, StrataEvaluator heights)
        {
            StrataPaint paint = feature.Paint;
            SetCurve(_paintCurve, paint.Curve, true);
            _shader.SetBuffer(kernel, "_PaintCurve", _paintCurve);
            Vector4 parameters = new(palette.IndexOf(paint.Layer), paint.Strength, math.max(paint.WidthScale, 0f), math.max(paint.Sensitivity, 0.01f));
            return SetInfluence(kernel, feature, heights, parameters);
        }

        // Binds what FeatureInfluence needs for a feature (its kind, footprint, spline or filter delta, mask and strength)
        // with _Paint as given; false if the feature has nothing to act with.
        private bool SetInfluence(int kernel, StrataFeature feature, StrataEvaluator heights, Vector4 paint)
        {
            StrataPaintSource source = paint.y > 0f ? feature.Paint.Source : StrataPaintSource.Influence;
            Bind(kernel);
            _shader.SetVector("_Paint", paint);
            SetMaskAndStrength(feature);
            _shader.SetBuffer(kernel, "_Curve", _curve);
            EnsureForestLayers(1);
            _shader.SetBuffer(kernel, "_ForestLayers", _forestLayers);
            _shader.SetTexture(kernel, "_MoistureIn", _moistureMap);
            _shader.SetBuffer(kernel, "_Profile", _profile);
            _shader.SetTexture(kernel, "_StampTexture", Texture2D.blackTexture);
            _shader.SetTexture(kernel, "_Delta", _dummyDelta);
            _shader.SetInts("_DeltaArea", 0, 0, 0, 0);
            _shader.SetBuffer(kernel, "_SplinePositions", heights.SplinePositions ?? _dummyBuffer);
            _shader.SetBuffer(kernel, "_SplineShapes", heights.SplineShapes ?? _dummyBuffer);
            _shader.SetInt("_SplineCount", 0);
            _shader.SetFloat("_Unbounded", feature.Unbounded ? 1f : 0f);

            switch (feature)
            {
                case TextureStamp stamp:
                    if (stamp.Texture == null) return false;
                    _shader.SetInt("_PaintKind", 0);
                    SetFootprint(stamp.transform, stamp.EdgeFalloff, stamp.FalloffCurve);
                    _shader.SetTexture(kernel, "_StampTexture", stamp.Texture);
                    return true;
                case NoiseStamp noise:
                    _shader.SetInt("_PaintKind", 1);
                    SetFootprint(noise.transform, noise.EdgeFalloff, noise.FalloffCurve);
                    return true;
                case SplineFeature spline:
                {
                    int count = heights.UploadSpline(spline);
                    if (count < 2) return false;
                    _shader.SetInt("_PaintKind", 2);
                    _shader.SetBuffer(kernel, "_SplinePositions", heights.SplinePositions);
                    _shader.SetBuffer(kernel, "_SplineShapes", heights.SplineShapes);
                    _shader.SetInt("_SplineCount", count);
                    SetCurve(_profile, spline.FalloffCurve, false);
                    return true;
                }
                case FilterFeature filter:
                {
                    SetFootprint(filter.transform, filter.EdgeFalloff, filter.FalloffCurve);
                    if (source == StrataPaintSource.Influence)
                    {
                        _shader.SetInt("_PaintKind", 3);
                        return true;
                    }
                    if (!heights.TryGetDelta(filter, out RenderTexture delta, out RectInt area)) return false;
                    _shader.SetInt("_PaintKind", source == StrataPaintSource.Deposition ? 4 : 5);
                    _shader.SetTexture(kernel, "_Delta", delta);
                    _shader.SetInts("_DeltaArea", area.x, area.y, area.width, area.height);
                    return true;
                }
                case RockStrata rock:
                    _shader.SetInt("_PaintKind", 6);
                    SetFootprint(rock.transform, rock.EdgeFalloff, rock.FalloffCurve);
                    _shader.SetVector("_StrataSlope", new Vector4(rock.SlopeStart, rock.SlopeFull, 0f, rock.Unbounded ? 1f : 0f));
                    return true;
                case TreeScatter scatter:
                    if (scatter.Forest == null) return false;
                    _shader.SetInt("_PaintKind", 7);
                    SetFootprint(scatter.transform, scatter.EdgeFalloff, scatter.FalloffCurve);
                    SetForest(kernel, scatter.Forest);
                    return true;
                default:
                    return false;
            }
        }

        private void SetMaskAndStrength(StrataFeature feature)
        {
            _shader.SetFloat("_Strength", feature.Strength);
            StrataMask mask = feature.Mask;
            _shader.SetVector("_MaskHeight", new Vector4(mask.UseHeight ? 1f : 0f, mask.MinHeight, mask.MaxHeight, mask.HeightFalloff));
            _shader.SetVector("_MaskSlope", new Vector4(mask.UseSlope ? 1f : 0f, mask.MinSlope, mask.MaxSlope, mask.SlopeFalloff));
        }

        private void SetFootprint(Transform transform, float edgeFalloff, AnimationCurve curve)
        {
            float yaw = math.radians(transform.eulerAngles.y);
            Vector3 scale = transform.lossyScale;
            _shader.SetVector("_Footprint", new Vector4(transform.position.x, transform.position.z, math.cos(yaw), math.sin(yaw)));
            _shader.SetVector("_FootprintSize", new Vector4(1f / math.max(math.abs(scale.x), 1e-3f), 1f / math.max(math.abs(scale.z), 1e-3f),
                                                            edgeFalloff, transform.position.y));
            SetCurve(_curve, curve, false);
        }

        private void UploadRules(StrataPalette palette)
        {
            int count = math.max(1, palette != null ? palette.Layers.Count : 0);
            Vector4[] data = new Vector4[count * RuleFloat4s];
            for (int i = 0; palette != null && i < palette.Layers.Count; i++)
            {
                StrataLayer layer = palette.Layers[i];
                if (layer == null) continue;
                StrataLayerRule rule = layer.Rule;
                data[i * RuleFloat4s] = new Vector4(rule.Enabled ? 1f : 0f, rule.Opacity, 1f / math.max(rule.NoiseScale, 0.1f), rule.NoiseAmount);
                data[i * RuleFloat4s + 1] = rule.Height.Packed;
                data[i * RuleFloat4s + 2] = rule.Slope.Packed;
                data[i * RuleFloat4s + 3] = rule.Curvature.Packed;
                data[i * RuleFloat4s + 4] = rule.Moisture.Packed;
            }
            if (_rules == null || _rules.count < data.Length)
            {
                _rules?.Release();
                _rules = new ComputeBuffer(math.ceilpow2(data.Length), 16);
            }
            _rules.SetData(data);
        }

        private void SetHeights(StrataWorld world)
        {
            StrataGrid grid = world.Heightfield.Grid;
            _shader.SetVector("_Grid", new Vector4(grid.Origin.x, grid.Origin.y, grid.Spacing));
            _shader.SetVector("_TileGrid", new Vector4(grid.TilesX, grid.TilesZ, grid.TileSize, grid.Resolution));
            _shader.SetVector("_HeightRange", new Vector4(grid.MinHeight, grid.HeightRange));
            foreach (string name in new[] { "MoistureHeights", "SplatRules", "PaintFeature", "RockStrataMap", "TreeScatter", "TreeEffect", "TreeWrite", "GrassScatter" })
                _shader.SetTexture(_shader.FindKernel(name), "_HeightTiles", world.Heightfield.Texture);
        }

        private void Bind(int kernel)
        {
            _shader.SetTexture(kernel, "_SplatIndex", _index);
            _shader.SetTexture(kernel, "_SplatWeight", _weight);
        }

        private void EnsureTargets(StrataGrid grid)
        {
            if (_index != null && _index.width == grid.SplatSamples) return;
            Destroy(_index);
            Destroy(_weight);
            Destroy(_rock);
            _index = NewTexture(grid.SplatSamples, grid.SplatSamples, RenderTextureFormat.ARGBFloat);
            _weight = NewTexture(grid.SplatSamples, grid.SplatSamples, RenderTextureFormat.ARGBFloat);
            _rock = NewTexture(grid.SplatSamples, grid.SplatSamples, RenderTextureFormat.RGFloat);
        }

        private void SetCurve(ComputeBuffer buffer, AnimationCurve curve, bool identityIfMissing)
        {
            for (int i = 0; i < CurveSamples; i++)
            {
                float t = i / (CurveSamples - 1f);
                _curveData[i] = curve != null && curve.length > 0 ? curve.Evaluate(t) : identityIfMissing ? t : 1f;
            }
            buffer.SetData(_curveData);
        }

        private void Dispatch(int kernel, Vector2Int size) => _shader.Dispatch(kernel, (size.x + 7) / 8, (size.y + 7) / 8, 1);

        private static void Clear(RenderTexture target, float value)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(false, true, new Color(value, value, value, value));
            RenderTexture.active = active;
        }

        private static RenderTexture Temporary(Vector2Int size, RenderTextureFormat format)
        {
            RenderTexture texture = RenderTexture.GetTemporary(new RenderTextureDescriptor(size.x, size.y, format, 0) { enableRandomWrite = true });
            texture.filterMode = FilterMode.Point;
            return texture;
        }

        private static RenderTexture NewTexture(int width, int height, RenderTextureFormat format)
        {
            RenderTexture texture = new(width, height, 0, format)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            return texture;
        }

        private static void Destroy(RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
