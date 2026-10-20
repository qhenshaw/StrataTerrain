using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Strata.Editor
{
    // Turns a world's features into heights on the GPU. It evaluates a "region" of world samples from scratch (zero,
    // then every feature in hierarchy order, each dispatched over just the part of the region it touches), so the live
    // preview and the bake share one code path: the preview evaluates the area an edit dirtied, grown so that any filter
    // overlapping it runs over its whole footprint, plus an apron so slope masks near the edge read correct
    // neighbours; the bake evaluates the whole world. WriteTiles then stores a region's heights into the heightfield's
    // tile texture array.
    public sealed class StrataEvaluator : IDisposable
    {
        private const string HeightShaderPath = "Packages/com.qhenshaw.terrain/Editor/Compute/StrataHeight.compute";
        private const string FilterShaderPath = "Packages/com.qhenshaw.terrain/Editor/Compute/StrataFilters.compute";
        private const int CurveSamples = 64;
        // a time step that keeps the pipe model stable at any cell size the grid allows
        private const float PipeTimeStep = 0.05f;

        public sealed class Node
        {
            public StrataFeature Feature;
            public readonly List<Node> Children = new();
        }

        private readonly ComputeShader _height;
        private readonly ComputeShader _filters;
        private readonly ComputeBuffer _curve = new(CurveSamples, 4);
        private readonly ComputeBuffer _profile = new(CurveSamples, 4);
        private readonly float[] _curveData = new float[CurveSamples];
        private ComputeBuffer _splinePositions;
        private ComputeBuffer _splineShapes;
        private readonly RenderTexture _dummy;
        private readonly RenderTexture _dummyDelta;
        private readonly List<Vector4> _positions = new();
        private readonly List<Vector4> _shapes = new();
        private readonly Dictionary<FilterFeature, (RenderTexture texture, RectInt area)> _deltas = new();

        public StrataEvaluator()
        {
            _height = AssetDatabase.LoadAssetAtPath<ComputeShader>(HeightShaderPath);
            _filters = AssetDatabase.LoadAssetAtPath<ComputeShader>(FilterShaderPath);
            _dummy = NewTexture(1, 1, RenderTextureFormat.RFloat);
            _dummyDelta = NewTexture(1, 1, RenderTextureFormat.RFloat);
        }

        // The last height change of a painting filter (world samples covered), for its Deposition or Erosion paint.
        public bool TryGetDelta(FilterFeature filter, out RenderTexture delta, out RectInt area)
        {
            bool found = _deltas.TryGetValue(filter, out (RenderTexture texture, RectInt area) entry);
            delta = entry.texture;
            area = entry.area;
            return found;
        }

        private void ReleaseDelta(FilterFeature filter)
        {
            if (!_deltas.Remove(filter, out (RenderTexture texture, RectInt area) entry)) return;
            entry.texture.Release();
            UnityEngine.Object.DestroyImmediate(entry.texture);
        }

        public void Dispose()
        {
            foreach ((RenderTexture texture, RectInt _) in _deltas.Values)
            {
                texture.Release();
                UnityEngine.Object.DestroyImmediate(texture);
            }
            _deltas.Clear();
            _curve.Release();
            _profile.Release();
            _splinePositions?.Release();
            _splineShapes?.Release();
            _dummy.Release();
            UnityEngine.Object.DestroyImmediate(_dummy);
            _dummyDelta.Release();
            UnityEngine.Object.DestroyImmediate(_dummyDelta);
        }

        // The world's features as a tree, in hierarchy order. Objects without a feature are passed through (folders);
        // a group's children become its own list. Groups get their bounds from their children here.
        public static List<Node> Collect(StrataWorld world)
        {
            List<Node> nodes = new();
            CollectChildren(world.transform, nodes);
            return nodes;
        }

        private static void CollectChildren(Transform parent, List<Node> nodes)
        {
            foreach (Transform child in parent)
            {
                if (!child.gameObject.activeInHierarchy) continue;
                StrataFeature feature = child.GetComponent<StrataFeature>();
                if (feature == null || !feature.enabled)
                {
                    if (feature == null) CollectChildren(child, nodes);
                    continue;
                }
                Node node = new() { Feature = feature };
                nodes.Add(node);
                if (feature is StampGroup group)
                {
                    CollectChildren(child, node.Children);
                    bool unbounded = false;
                    Rect bounds = default;
                    bool any = false;
                    foreach (Node c in node.Children)
                    {
                        unbounded |= c.Feature.Unbounded;
                        bounds = any ? Union(bounds, c.Feature.Bounds) : c.Feature.Bounds;
                        any = true;
                    }
                    group.ChildrenUnbounded = unbounded;
                    group.ChildBounds = bounds;
                }
            }
        }

        public static IEnumerable<StrataFeature> Flatten(List<Node> nodes)
        {
            foreach (Node node in nodes)
            {
                yield return node.Feature;
                foreach (StrataFeature child in Flatten(node.Children)) yield return child;
            }
        }

        public static RectInt WorldRect(StrataGrid grid) => new(0, 0, grid.WorldSamplesX, grid.WorldSamplesZ);

        // The world samples a world-space rectangle covers, grown by a margin in metres, clamped to the world.
        public static RectInt SampleRect(StrataGrid grid, Rect rect, float margin = 0f)
        {
            int2 min = (int2)math.floor(((float2)rect.min - margin - grid.Origin) / grid.Spacing);
            int2 max = (int2)math.ceil(((float2)rect.max + margin - grid.Origin) / grid.Spacing) + 1;
            return Intersect(new RectInt(min.x, min.y, max.x - min.x, max.y - min.y), WorldRect(grid));
        }

        public static RectInt FeatureRect(StrataGrid grid, StrataFeature feature, float margin = 0f) =>
            feature.Unbounded ? WorldRect(grid) : SampleRect(grid, feature.Bounds, margin);

        // Grows a dirty area until it holds every filter it touches (footprint and apron), and returns the commit area
        // (whose heights change) and the evaluation area (the commit area plus the mask apron).
        public static RectInt Expand(StrataGrid grid, List<Node> nodes, RectInt dirty, out RectInt evaluate)
        {
            int apron = 2;
            List<StrataFeature> filters = new();
            foreach (StrataFeature feature in Flatten(nodes))
            {
                if (feature.Mask.UseSlope) apron++;
                if (feature is FilterFeature) filters.Add(feature);
            }
            apron = math.min(apron, 32);

            RectInt world = WorldRect(grid);
            RectInt commit = Intersect(dirty, world);
            bool grew = true;
            while (grew)
            {
                grew = false;
                RectInt reach = Grow(commit, apron, world);
                foreach (StrataFeature filter in filters)
                {
                    RectInt area = FeatureRect(grid, filter, filter.Apron);
                    if (!Overlaps(area, reach) || Contains(commit, area)) continue;
                    commit = Union(commit, area);
                    grew = true;
                }
            }
            evaluate = Grow(commit, apron, world);
            return commit;
        }

        // Evaluates every feature over a region of world samples into a new RFloat texture of world heights; release it
        // with RenderTexture.ReleaseTemporary.
        public RenderTexture Evaluate(StrataGrid grid, List<Node> nodes, RectInt region)
        {
            RenderTexture heights = Temporary(region.width, region.height, RenderTextureFormat.RFloat);
            Fill(heights, region, new RectInt(0, 0, region.width, region.height), 0f);
            EvaluateInto(grid, nodes, heights, region);
            return heights;
        }

        // Writes the commit area of a region's heights into the tile texture array; returns the tiles it touched.
        public List<int> WriteTiles(StrataGrid grid, RenderTexture heights, RectInt region, RectInt commit, RenderTexture tiles)
        {
            List<int> touched = new();
            int kernel = _height.FindKernel("ToTiles");
            for (int tz = 0; tz < grid.TilesZ; tz++)
            for (int tx = 0; tx < grid.TilesX; tx++)
            {
                RectInt tileRect = new(tx * grid.Resolution, tz * grid.Resolution, grid.Samples, grid.Samples);
                RectInt clip = Intersect(tileRect, commit);
                if (clip.width <= 0 || clip.height <= 0) continue;
                int slice = grid.TileIndex(tx, tz);
                touched.Add(slice);
                _height.SetInts("_Region", region.x, region.y, region.width, region.height);
                _height.SetInts("_Clip", clip.x - tileRect.x, clip.y - tileRect.y, clip.width, clip.height);
                _height.SetInts("_Tile", slice, tileRect.x, tileRect.y, 0);
                _height.SetVector("_HeightRange", new Vector4(grid.MinHeight, grid.HeightRange));
                _height.SetTexture(kernel, "_Source", heights);
                _height.SetTexture(kernel, "_Tiles", tiles);
                Dispatch(_height, kernel, clip.width, clip.height);
            }
            return touched;
        }

        private void EvaluateInto(StrataGrid grid, List<Node> nodes, RenderTexture heights, RectInt region)
        {
            foreach (Node node in nodes)
            {
                StrataFeature feature = node.Feature;
                RectInt clip = Intersect(FeatureRect(grid, feature), region);
                if (clip.width <= 0 || clip.height <= 0) continue;
                clip.position -= region.position;

                RenderTexture before = feature.Mask.UseSlope || feature is RockStrata ? Copy(heights) : null;
                switch (feature)
                {
                    case TextureStamp stamp: RunTextureStamp(grid, stamp, heights, before, region, clip); break;
                    case NoiseStamp noise: RunNoiseStamp(grid, noise, heights, before, region, clip); break;
                    case SplineFeature spline: RunSpline(grid, spline, heights, before, region, clip); break;
                    case FilterFeature filter: RunFilter(grid, filter, heights, before, region, clip); break;
                    case StampGroup group: RunGroup(grid, group, node.Children, heights, before, region, clip); break;
                    case RockStrata strata: RunRockStrata(grid, strata, heights, before, region, clip); break;
                }
                if (before != null) RenderTexture.ReleaseTemporary(before);
            }
        }

        private void RunTextureStamp(StrataGrid grid, TextureStamp stamp, RenderTexture heights, RenderTexture before, RectInt region, RectInt clip)
        {
            if (stamp.Texture == null) return;
            int kernel = _height.FindKernel("TextureStamp");
            SetCommon(_height, kernel, grid, stamp, heights, before, region, clip);
            SetFootprint(_height, stamp.transform, stamp.EdgeFalloff, stamp.FalloffCurve);
            _height.SetTexture(kernel, "_StampTexture", stamp.Texture);
            _height.SetFloat("_StampHeight", stamp.Height);
            Dispatch(_height, kernel, clip.width, clip.height);
        }

        private void RunNoiseStamp(StrataGrid grid, NoiseStamp noise, RenderTexture heights, RenderTexture before, RectInt region, RectInt clip)
        {
            int kernel = _height.FindKernel("NoiseStamp");
            SetCommon(_height, kernel, grid, noise, heights, before, region, clip);
            SetFootprint(_height, noise.transform, noise.EdgeFalloff, noise.FalloffCurve);
            _height.SetVector("_Noise", new Vector4((int)noise.Noise, 1f / noise.Scale, noise.Octaves, noise.Seed & 0xffff));
            _height.SetVector("_NoiseShape", new Vector4(noise.Lacunarity, noise.Gain, noise.Height, noise.Unbounded ? 1f : 0f));
            Dispatch(_height, kernel, clip.width, clip.height);
        }

        private void RunSpline(StrataGrid grid, SplineFeature spline, RenderTexture heights, RenderTexture before, RectInt region, RectInt clip)
        {
            int count = UploadSpline(spline);
            if (count < 2) return;

            int kernel = _height.FindKernel("SplineFeature");
            SetCommon(_height, kernel, grid, spline, heights, before, region, clip);
            SetCurve(_curve, spline.FalloffCurve);
            SetCurve(_profile, spline.CrossSection);
            _height.SetBuffer(kernel, "_SplinePositions", _splinePositions);
            _height.SetBuffer(kernel, "_SplineShapes", _splineShapes);
            _height.SetInt("_SplineCount", count);
            _height.SetVector("_SplineParams", new Vector4((int)spline.Mode, spline.Depth));
            Dispatch(_height, kernel, clip.width, clip.height);
        }

        public ComputeBuffer SplinePositions => _splinePositions;
        public ComputeBuffer SplineShapes => _splineShapes;

        // Uploads a spline feature's sampled points to SplinePositions and SplineShapes; returns how many.
        public int UploadSpline(SplineFeature spline)
        {
            IReadOnlyList<SplineFeature.Point> points = spline.Points;
            if (points.Count < 2) return points.Count;
            _positions.Clear();
            _shapes.Clear();
            foreach (SplineFeature.Point point in points)
            {
                _positions.Add(point.Position);
                _shapes.Add(point.Shape);
            }
            if (_splinePositions == null || _splinePositions.count < points.Count)
            {
                _splinePositions?.Release();
                _splineShapes?.Release();
                _splinePositions = new ComputeBuffer(math.ceilpow2(points.Count), 16);
                _splineShapes = new ComputeBuffer(math.ceilpow2(points.Count), 16);
            }
            _splinePositions.SetData(_positions);
            _splineShapes.SetData(_shapes);
            return points.Count;
        }

        private void RunRockStrata(StrataGrid grid, RockStrata strata, RenderTexture heights, RenderTexture before, RectInt region, RectInt clip)
        {
            int kernel = _height.FindKernel("RockStrataTerrace");
            SetCommon(_height, kernel, grid, strata, heights, before, region, clip);
            SetFootprint(_height, strata.transform, strata.EdgeFalloff, strata.FalloffCurve);
            SetBand(_height, strata.Definition);
            _height.SetVector("_StrataSlope", new Vector4(strata.SlopeStart, strata.SlopeFull, 0f, strata.Unbounded ? 1f : 0f));
            Dispatch(_height, kernel, clip.width, clip.height);
        }

        private readonly Vector4[] _band = new Vector4[StrataBandDefinition.Float4s];

        public void SetBand(ComputeShader shader, StrataBandDefinition definition)
        {
            definition.Pack(_band, 0);
            shader.SetVector("_Band0", _band[0]);
            shader.SetVector("_Band1", _band[1]);
            shader.SetVector("_Band2", _band[2]);
        }

        private void RunGroup(StrataGrid grid, StampGroup group, List<Node> children, RenderTexture heights, RenderTexture before, RectInt region, RectInt clip)
        {
            RenderTexture layer = group.Source == StrataGroupSource.Current ? Copy(heights) : Temporary(region.width, region.height, RenderTextureFormat.RFloat);
            if (group.Source == StrataGroupSource.Empty) Fill(layer, region, new RectInt(0, 0, region.width, region.height), 0f);
            EvaluateInto(grid, children, layer, region);

            int kernel = _height.FindKernel("BlendLayer");
            SetCommon(_height, kernel, grid, group, heights, before, region, clip);
            _height.SetTexture(kernel, "_Layer", layer);
            Dispatch(_height, kernel, clip.width, clip.height);
            RenderTexture.ReleaseTemporary(layer);
        }

        private void RunFilter(StrataGrid grid, FilterFeature filter, RenderTexture heights, RenderTexture before, RectInt region, RectInt clip)
        {
            RectInt area = Intersect(FeatureRect(grid, filter, filter.Apron), region);
            area.position -= region.position;
            RenderTexture a = Temporary(area.width, area.height, RenderTextureFormat.RFloat);
            RenderTexture b = Temporary(area.width, area.height, RenderTextureFormat.RFloat);
            Graphics.CopyTexture(heights, 0, 0, area.x, area.y, area.width, area.height, a, 0, 0, 0, 0);
            _filters.SetInts("_Area", area.x, area.y, area.width, area.height);

            RenderTexture result = a;
            switch (filter.Filter)
            {
                case StrataFilter.Smooth:
                {
                    float sigma = math.max(0.5f, filter.Radius / grid.Spacing);
                    _filters.SetVector("_Blur", new Vector4(sigma, math.ceil(sigma * 3f)));
                    Pass(_filters.FindKernel("BlurX"), a, b, area);
                    Pass(_filters.FindKernel("BlurZ"), b, a, area);
                    result = a;
                    break;
                }
                case StrataFilter.Terrace:
                    _filters.SetVector("_Terrace", new Vector4(filter.StepHeight, filter.Sharpness));
                    Pass(_filters.FindKernel("Terrace"), a, b, area);
                    result = b;
                    break;
                case StrataFilter.Thermal:
                    result = RunThermal(grid, filter, a, b, area);
                    break;
                case StrataFilter.Hydraulic:
                    result = RunHydraulic(grid, filter, a, b, area);
                    break;
            }

            // a filter that paints where it deposited or eroded keeps its height change for the splat pass
            RenderTexture delta = _dummyDelta;
            if (filter.Paints && filter.Paint.Source != StrataPaintSource.Influence)
            {
                ReleaseDelta(filter);
                delta = NewTexture(clip.width, clip.height, RenderTextureFormat.RFloat);
                _deltas[filter] = (delta, new RectInt(region.x + clip.x, region.y + clip.y, clip.width, clip.height));
            }

            int apply = _filters.FindKernel("Apply");
            SetCommon(_filters, apply, grid, filter, heights, before, region, clip);
            SetFootprint(_filters, filter.transform, filter.EdgeFalloff, filter.FalloffCurve);
            _filters.SetFloat("_Unbounded", filter.Unbounded ? 1f : 0f);
            _filters.SetTexture(apply, "_In", result);
            _filters.SetTexture(apply, "_Delta", delta);
            Dispatch(_filters, apply, clip.width, clip.height);
            RenderTexture.ReleaseTemporary(a);
            RenderTexture.ReleaseTemporary(b);
        }

        private RenderTexture RunThermal(StrataGrid grid, FilterFeature filter, RenderTexture a, RenderTexture b, RectInt area)
        {
            RenderTexture transfer = Temporary(area.width, area.height, RenderTextureFormat.RGFloat);
            _filters.SetVector("_Thermal", new Vector4(math.tan(math.radians(filter.TalusAngle)) * grid.Spacing, 0.5f));
            int outKernel = _filters.FindKernel("ThermalOut");
            int inKernel = _filters.FindKernel("ThermalIn");
            for (int i = 0; i < filter.Iterations; i++)
            {
                _filters.SetTexture(outKernel, "_In", a);
                _filters.SetTexture(outKernel, "_Transfer", transfer);
                Dispatch(_filters, outKernel, area.width, area.height);
                _filters.SetTexture(inKernel, "_In", a);
                _filters.SetTexture(inKernel, "_TransferIn", transfer);
                _filters.SetTexture(inKernel, "_Out", b);
                Dispatch(_filters, inKernel, area.width, area.height);
                (a, b) = (b, a);
            }
            RenderTexture.ReleaseTemporary(transfer);
            return a;
        }

        private RenderTexture RunHydraulic(StrataGrid grid, FilterFeature filter, RenderTexture terrain, RenderTexture terrainNext, RectInt area)
        {
            int w = area.width, h = area.height;
            RenderTexture water = Temporary(w, h, RenderTextureFormat.RFloat), waterNext = Temporary(w, h, RenderTextureFormat.RFloat);
            RenderTexture sediment = Temporary(w, h, RenderTextureFormat.RFloat), sedimentNext = Temporary(w, h, RenderTextureFormat.RFloat);
            RenderTexture flux = Temporary(w, h, RenderTextureFormat.ARGBFloat), fluxNext = Temporary(w, h, RenderTextureFormat.ARGBFloat);
            RenderTexture velocity = Temporary(w, h, RenderTextureFormat.RGFloat);
            Clear(water, filter.Rain);
            Clear(sediment, 0f);
            Clear(flux, 0f);
            Clear(velocity, 0f);

            _filters.SetVector("_Pipe", new Vector4(PipeTimeStep, 9.81f, grid.Spacing, filter.Rain));
            _filters.SetVector("_PipeErosion", new Vector4(filter.Capacity, filter.ErosionRate, filter.DepositionRate, filter.Evaporation));
            int fluxKernel = _filters.FindKernel("PipeFlux");
            int waterKernel = _filters.FindKernel("PipeWater");
            int erodeKernel = _filters.FindKernel("PipeErode");
            int transportKernel = _filters.FindKernel("PipeTransport");
            int thermalOut = _filters.FindKernel("ThermalOut");
            int thermalIn = _filters.FindKernel("ThermalIn");
            RenderTexture transfer = Temporary(w, h, RenderTextureFormat.RGFloat);
            _filters.SetVector("_Thermal", new Vector4(math.tan(math.radians(filter.TalusAngle)) * grid.Spacing, 0.5f));
            for (int i = 0; i < filter.Iterations; i++)
            {
                _filters.SetTexture(fluxKernel, "_Terrain", terrain);
                _filters.SetTexture(fluxKernel, "_Water", water);
                _filters.SetTexture(fluxKernel, "_FluxIn", flux);
                _filters.SetTexture(fluxKernel, "_Flux", fluxNext);
                Dispatch(_filters, fluxKernel, w, h);

                _filters.SetTexture(waterKernel, "_FluxIn", fluxNext);
                _filters.SetTexture(waterKernel, "_Water", water);
                _filters.SetTexture(waterKernel, "_WaterOut", waterNext);
                _filters.SetTexture(waterKernel, "_Velocity", velocity);
                Dispatch(_filters, waterKernel, w, h);

                _filters.SetTexture(erodeKernel, "_Terrain", terrain);
                _filters.SetTexture(erodeKernel, "_Water", waterNext);
                _filters.SetTexture(erodeKernel, "_Sediment", sediment);
                _filters.SetTexture(erodeKernel, "_VelocityIn", velocity);
                _filters.SetTexture(erodeKernel, "_TerrainOut", terrainNext);
                _filters.SetTexture(erodeKernel, "_SedimentOut", sedimentNext);
                Dispatch(_filters, erodeKernel, w, h);

                _filters.SetTexture(transportKernel, "_Sediment", sedimentNext);
                _filters.SetTexture(transportKernel, "_VelocityIn", velocity);
                _filters.SetTexture(transportKernel, "_Water", waterNext);
                _filters.SetTexture(transportKernel, "_SedimentOut", sediment);
                _filters.SetTexture(transportKernel, "_WaterOut", water);
                Dispatch(_filters, transportKernel, w, h);

                (terrain, terrainNext) = (terrainNext, terrain);
                (flux, fluxNext) = (fluxNext, flux);

                // loose material steeper than the talus angle slides down, which stops deposits growing into fins
                _filters.SetTexture(thermalOut, "_In", terrain);
                _filters.SetTexture(thermalOut, "_Transfer", transfer);
                Dispatch(_filters, thermalOut, w, h);
                _filters.SetTexture(thermalIn, "_In", terrain);
                _filters.SetTexture(thermalIn, "_TransferIn", transfer);
                _filters.SetTexture(thermalIn, "_Out", terrainNext);
                Dispatch(_filters, thermalIn, w, h);
                (terrain, terrainNext) = (terrainNext, terrain);
            }

            RenderTexture.ReleaseTemporary(transfer);
            RenderTexture.ReleaseTemporary(water);
            RenderTexture.ReleaseTemporary(waterNext);
            RenderTexture.ReleaseTemporary(sediment);
            RenderTexture.ReleaseTemporary(sedimentNext);
            RenderTexture.ReleaseTemporary(flux);
            RenderTexture.ReleaseTemporary(fluxNext);
            RenderTexture.ReleaseTemporary(velocity);
            return terrain;
        }

        private void Pass(int kernel, RenderTexture input, RenderTexture output, RectInt area)
        {
            _filters.SetTexture(kernel, "_In", input);
            _filters.SetTexture(kernel, "_Out", output);
            Dispatch(_filters, kernel, area.width, area.height);
        }

        private void SetCommon(ComputeShader shader, int kernel, StrataGrid grid, StrataFeature feature, RenderTexture heights, RenderTexture before, RectInt region, RectInt clip)
        {
            shader.SetInts("_Region", region.x, region.y, region.width, region.height);
            shader.SetInts("_Clip", clip.x, clip.y, clip.width, clip.height);
            shader.SetVector("_Grid", new Vector4(grid.Origin.x, grid.Origin.y, grid.Spacing));
            shader.SetInt("_Blend", (int)feature.Blend);
            shader.SetFloat("_Strength", feature.Strength);
            StrataMask mask = feature.Mask;
            shader.SetVector("_MaskHeight", new Vector4(mask.UseHeight ? 1f : 0f, mask.MinHeight, mask.MaxHeight, mask.HeightFalloff));
            shader.SetVector("_MaskSlope", new Vector4(mask.UseSlope ? 1f : 0f, mask.MinSlope, mask.MaxSlope, mask.SlopeFalloff));
            shader.SetTexture(kernel, "_Heights", heights);
            shader.SetTexture(kernel, "_Before", before != null ? before : _dummy);
            shader.SetBuffer(kernel, "_Curve", _curve);
            shader.SetBuffer(kernel, "_Profile", _profile);
        }

        private void SetFootprint(ComputeShader shader, Transform transform, float edgeFalloff, AnimationCurve curve)
        {
            float yaw = math.radians(transform.eulerAngles.y);
            Vector3 scale = transform.lossyScale;
            shader.SetVector("_Footprint", new Vector4(transform.position.x, transform.position.z, math.cos(yaw), math.sin(yaw)));
            shader.SetVector("_FootprintSize", new Vector4(1f / math.max(math.abs(scale.x), 1e-3f), 1f / math.max(math.abs(scale.z), 1e-3f),
                                                           edgeFalloff, transform.position.y));
            SetCurve(_curve, curve);
        }

        private void SetCurve(ComputeBuffer buffer, AnimationCurve curve)
        {
            for (int i = 0; i < CurveSamples; i++)
                _curveData[i] = curve != null && curve.length > 0 ? curve.Evaluate(i / (CurveSamples - 1f)) : 1f;
            buffer.SetData(_curveData);
        }

        private void Fill(RenderTexture target, RectInt region, RectInt clip, float value)
        {
            int kernel = _height.FindKernel("Fill");
            _height.SetInts("_Region", region.x, region.y, region.width, region.height);
            _height.SetInts("_Clip", clip.x, clip.y, clip.width, clip.height);
            _height.SetFloat("_FillValue", value);
            _height.SetTexture(kernel, "_Heights", target);
            Dispatch(_height, kernel, clip.width, clip.height);
        }

        private static void Clear(RenderTexture target, float value)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(false, true, new Color(value, value, value, value));
            RenderTexture.active = active;
        }

        private static RenderTexture Copy(RenderTexture source)
        {
            RenderTexture copy = Temporary(source.width, source.height, source.format);
            Graphics.CopyTexture(source, copy);
            return copy;
        }

        private static void Dispatch(ComputeShader shader, int kernel, int width, int height) =>
            shader.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);

        private static RenderTexture Temporary(int width, int height, RenderTextureFormat format)
        {
            RenderTexture texture = RenderTexture.GetTemporary(new RenderTextureDescriptor(width, height, format, 0) { enableRandomWrite = true });
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private static RenderTexture NewTexture(int width, int height, RenderTextureFormat format)
        {
            RenderTexture texture = new(width, height, 0, format) { enableRandomWrite = true, hideFlags = HideFlags.HideAndDontSave };
            texture.Create();
            return texture;
        }

        public static RectInt Intersect(RectInt a, RectInt b)
        {
            int xMin = math.max(a.xMin, b.xMin), yMin = math.max(a.yMin, b.yMin);
            int xMax = math.min(a.xMax, b.xMax), yMax = math.min(a.yMax, b.yMax);
            return new RectInt(xMin, yMin, math.max(0, xMax - xMin), math.max(0, yMax - yMin));
        }

        public static RectInt Union(RectInt a, RectInt b)
        {
            if (a.width <= 0 || a.height <= 0) return b;
            if (b.width <= 0 || b.height <= 0) return a;
            int xMin = math.min(a.xMin, b.xMin), yMin = math.min(a.yMin, b.yMin);
            return new RectInt(xMin, yMin, math.max(a.xMax, b.xMax) - xMin, math.max(a.yMax, b.yMax) - yMin);
        }

        private static Rect Union(Rect a, Rect b) => Rect.MinMaxRect(math.min(a.xMin, b.xMin), math.min(a.yMin, b.yMin), math.max(a.xMax, b.xMax), math.max(a.yMax, b.yMax));
        private static RectInt Grow(RectInt rect, int by, RectInt world) => Intersect(new RectInt(rect.x - by, rect.y - by, rect.width + by * 2, rect.height + by * 2), world);
        private static bool Overlaps(RectInt a, RectInt b) => a.xMin < b.xMax && b.xMin < a.xMax && a.yMin < b.yMax && b.yMin < a.yMax;
        private static bool Contains(RectInt outer, RectInt inner) => inner.xMin >= outer.xMin && inner.yMin >= outer.yMin && inner.xMax <= outer.xMax && inner.yMax <= outer.yMax;
    }
}
