using Unity.Collections;
using Unity.Mathematics;

namespace Strata.Editor
{
    // The solid the cave features make, as a signed distance field for Burst jobs: positive in air, negative in rock. It
    // starts as the heightfield's ground (height above the ground, scaled by the slope so it reads as a distance) and
    // applies each cave feature in hierarchy order, a carve as a smooth maximum with the inverted shape and an add as a
    // smooth minimum with the shape, so features fillet into the ground and each other. A feature's own distance is the
    // minimum over its primitives (tapered elliptical tubes and ellipsoids) plus world-anchored value noise. Everything
    // is evaluated at world positions computed the same way wherever they are shared, so neighbouring bricks and tiles
    // get bit-identical values and the meshes meet exactly. Shapes with biome layer options pick their wall and floor
    // layers for the two strongest biomes (underground, or surface for rock an add feature makes) and the weight between
    // them, which the cave shader blends.
    public struct StrataCaveField
    {
        public const float Far = 1e6f;

        public StrataHeights Heights;
        [ReadOnly] public NativeArray<StrataCaveShape> Shapes;
        [ReadOnly] public NativeArray<StrataCavePrimitive> Primitives;
        [ReadOnly] public NativeArray<StrataCaveLayerOption> LayerOptions;
        public StrataBiomeMap.View Biomes;
        // how far beyond a shape something still needs its distance (the mesh's rim around features), in metres
        public float Reach;

        // The shapes (and their primitives) that can matter inside a box: x shape, y first and z count in Indices.
        public struct Scope
        {
            public NativeList<int3> Shapes;
            public NativeList<int> Indices;

            public Scope(Allocator allocator)
            {
                Shapes = new NativeList<int3>(8, allocator);
                Indices = new NativeList<int>(64, allocator);
            }

            public bool IsEmpty => Shapes.Length == 0;

            public void Dispose()
            {
                Shapes.Dispose();
                Indices.Dispose();
            }
        }

        // The two strongest biomes at a point, from the biome map's four nearest texels blended bilinearly so the weight
        // changes smoothly between them. a is the lower biome index and b the higher, so neighbouring points in one blend
        // agree on which is which; weight is b's share (0, with b = a, where only one biome is present).
        public void BiomePair(float3 p, bool underground, out int a, out int b, out float weight)
        {
            a = b = StrataBiomeMap.None;
            weight = 0f;
            if (!Biomes.IsCreated) return;
            StrataGrid grid = Biomes.Grid;
            float spacing = grid.TileSize / grid.SplatResolution;
            float2 f = (p.xz - grid.Origin) / spacing;
            float2 cell = math.floor(f), t = f - cell;
            int4 ids = -1;
            float4 weights = 0f;
            for (int k = 0; k < 4; k++)
            {
                int2 corner = new(k & 1, k >> 1);
                float share = (corner.x == 1 ? t.x : 1f - t.x) * (corner.y == 1 ? t.y : 1f - t.y);
                uint texel = Biomes.Sample(grid.Origin + (cell + corner) * spacing, underground);
                float first = ((texel >> 16) & 0xff) / 255f;
                Accumulate(ref ids, ref weights, (int)(texel & 0xff), share * first);
                Accumulate(ref ids, ref weights, (int)((texel >> 8) & 0xff), share * (1f - first));
            }
            int top = 0, second = -1;
            for (int k = 1; k < 4; k++)
            {
                if (weights[k] > weights[top])
                {
                    second = top;
                    top = k;
                }
                else if (weights[k] > 0f && (second < 0 || weights[k] > weights[second])) second = k;
            }
            a = b = ids[top] < 0 ? StrataBiomeMap.None : ids[top];
            if (second < 0) return;
            int other = ids[second];
            float otherShare = weights[second] / (weights[top] + weights[second]);
            if (other < a)
            {
                b = a;
                a = other;
                weight = 1f - otherShare;
            }
            else
            {
                b = other;
                weight = otherShare;
            }
        }

        private static void Accumulate(ref int4 ids, ref float4 weights, int id, float weight)
        {
            if (weight <= 0f) return;
            for (int k = 0; k < 4; k++)
            {
                if (ids[k] == id)
                {
                    weights[k] += weight;
                    return;
                }
                if (ids[k] < 0)
                {
                    ids[k] = id;
                    weights[k] = weight;
                    return;
                }
            }
        }

        // The first of count options from first whose biomes hold the biome, else the fallback.
        public int PickLayer(int first, int count, int biome, int fallback)
        {
            for (int i = first; i < first + count; i++)
            {
                StrataCaveLayerOption option = LayerOptions[i];
                if (StrataBiomeFilter.MaskAllows(option.MaskLow, option.MaskHigh, biome)) return option.Layer;
            }
            return fallback;
        }

        public float Pad(StrataCaveShape shape) => shape.Smoothness + shape.NoiseAmplitude + Reach;

        public void Gather(float3 min, float3 max, ref Scope scope)
        {
            scope.Shapes.Clear();
            scope.Indices.Clear();
            for (int s = 0; s < Shapes.Length; s++)
            {
                StrataCaveShape shape = Shapes[s];
                float pad = Pad(shape);
                if (!Overlaps(shape.Min, shape.Max, min, max, Reach)) continue;
                int first = scope.Indices.Length;
                for (int p = shape.First; p < shape.First + shape.Count; p++)
                    if (Overlaps(Primitives[p].Min, Primitives[p].Max, min, max, pad)) scope.Indices.Add(p);
                if (scope.Indices.Length > first) scope.Shapes.Add(new int3(s, first, scope.Indices.Length - first));
            }
        }

        private static bool Overlaps(float3 aMin, float3 aMax, float3 bMin, float3 bMax, float pad) =>
            math.all(aMin - pad <= bMax) && math.all(aMax + pad >= bMin);

        // The ground under a position: its height, and the slope factor that turns height above it into distance.
        public float Ground(float2 xz, out float height, out float2 gradient)
        {
            float s = Heights.Grid.Spacing;
            height = Heights.Sample(xz);
            gradient = new float2(Heights.Sample(xz + new float2(s, 0f)) - Heights.Sample(xz - new float2(s, 0f)),
                                  Heights.Sample(xz + new float2(0f, s)) - Heights.Sample(xz - new float2(0f, s))) / (2f * s);
            return math.rsqrt(1f + math.lengthsq(gradient));
        }

        public float GroundDistance(float3 p)
        {
            float scale = Ground(p.xz, out float height, out float2 _);
            return (p.y - height) * scale;
        }

        // The field at p, given its ground distance; also the shape whose surface is nearest (-1 for none) and the
        // smallest shape distance (negative inside a shape).
        public float Evaluate(float3 p, float ground, in Scope scope, out int dominant, out float nearest)
        {
            float f = ground;
            dominant = -1;
            nearest = Far;
            float best = Far;
            for (int i = 0; i < scope.Shapes.Length; i++)
            {
                int3 entry = scope.Shapes[i];
                StrataCaveShape shape = Shapes[entry.x];
                float d = ShapeDistance(shape, p, scope.Indices, entry.y, entry.z);
                if (d >= Far) continue;
                nearest = math.min(nearest, d);
                if (math.abs(d) < best)
                {
                    best = math.abs(d);
                    dominant = entry.x;
                }
                f = shape.Operation == StrataCaveOperation.Carve ? SmoothMax(f, -d, shape.Smoothness) : SmoothMin(f, d, shape.Smoothness);
            }
            return f;
        }

        // One shape's distance at p (with noise), Far if p is beyond all of its primitives' reach.
        public float ShapeDistance(StrataCaveShape shape, float3 p, NativeList<int> indices, int first, int count)
        {
            float pad = Pad(shape);
            float d = Far;
            for (int i = first; i < first + count; i++)
            {
                StrataCavePrimitive primitive = Primitives[indices[i]];
                if (math.any(p < primitive.Min - pad) || math.any(p > primitive.Max + pad)) continue;
                d = math.min(d, PrimitiveDistance(primitive, p));
            }
            if (d >= Far) return Far;
            // noise can't bring a shape this far out back within reach of the surface or its fillet, so it is left off
            if (shape.NoiseAmplitude > 0f && d < shape.NoiseAmplitude + shape.Smoothness + Reach)
                d += shape.NoiseAmplitude * Noise(p * shape.NoiseFrequency, shape.NoiseOctaves, shape.Seed);
            return d;
        }

        // One shape's distance at p over all of its primitives.
        public float ShapeDistance(StrataCaveShape shape, float3 p)
        {
            float d = Far;
            for (int i = shape.First; i < shape.First + shape.Count; i++) d = math.min(d, PrimitiveDistance(Primitives[i], p));
            if (d >= Far) return Far;
            if (shape.NoiseAmplitude > 0f) d += shape.NoiseAmplitude * Noise(p * shape.NoiseFrequency, shape.NoiseOctaves, shape.Seed);
            return d;
        }

        public static float PrimitiveDistance(StrataCavePrimitive primitive, float3 p)
        {
            if (primitive.Type == StrataCavePrimitive.Ellipsoid)
            {
                float3 radius = primitive.B;
                float3 local = math.mul(math.inverse(primitive.Rotation), p - primitive.A);
                float k0 = math.length(local / radius);
                float k1 = math.length(local / (radius * radius));
                float d = k1 > 1e-6f ? k0 * (k0 - 1f) / k1 : -math.cmin(radius);
                float floorY = primitive.A.y - radius.y * (1f - primitive.Floor.x);
                return math.max(d, floorY - p.y);
            }

            float3 axis = primitive.B - primitive.A;
            float lengthSq = math.max(math.lengthsq(axis), 1e-8f);
            float t = math.saturate(math.dot(p - primitive.A, axis) / lengthSq);
            float3 centre = primitive.A + axis * t;
            float r = math.lerp(primitive.Radius.x, primitive.Radius.y, t);
            float h = r * primitive.Aspect;
            float3 tangent = axis * math.rsqrt(lengthSq);
            float3 side = math.cross(new float3(0f, 1f, 0f), tangent);
            side = math.lengthsq(side) > 1e-6f ? math.normalize(side) : new float3(1f, 0f, 0f);
            float3 up = math.cross(tangent, side);
            float3 offset = p - centre;
            float3 q = new(math.dot(offset, side) / r, math.dot(offset, up) / h, math.dot(offset, tangent) / r);
            float tube = (math.length(q) - 1f) * math.min(r, h);
            float floorHeight = centre.y - h * (1f - math.lerp(primitive.Floor.x, primitive.Floor.y, t));
            return math.max(tube, floorHeight - p.y);
        }

        public static float SmoothMin(float a, float b, float k)
        {
            if (k <= 0f) return math.min(a, b);
            float h = math.max(k - math.abs(a - b), 0f) / k;
            return math.min(a, b) - h * h * k * 0.25f;
        }

        public static float SmoothMax(float a, float b, float k) => -SmoothMin(-a, -b, k);

        // Fractal value noise in -1..1.
        public static float Noise(float3 p, int octaves, uint seed)
        {
            float sum = 0f, amplitude = 0.5f, total = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += (ValueNoise(p, seed + (uint)o * 101u) * 2f - 1f) * amplitude;
                total += amplitude;
                amplitude *= 0.5f;
                p = p * 2.03f + 17.17f;
            }
            return total > 0f ? sum / total : 0f;
        }

        private static float ValueNoise(float3 p, uint seed)
        {
            float3 cell = math.floor(p);
            int3 i = (int3)cell;
            float3 f = p - cell;
            f = f * f * (3f - 2f * f);
            float x00 = math.lerp(Hash(i, seed), Hash(i + new int3(1, 0, 0), seed), f.x);
            float x10 = math.lerp(Hash(i + new int3(0, 1, 0), seed), Hash(i + new int3(1, 1, 0), seed), f.x);
            float x01 = math.lerp(Hash(i + new int3(0, 0, 1), seed), Hash(i + new int3(1, 0, 1), seed), f.x);
            float x11 = math.lerp(Hash(i + new int3(0, 1, 1), seed), Hash(i + new int3(1, 1, 1), seed), f.x);
            return math.lerp(math.lerp(x00, x10, f.y), math.lerp(x01, x11, f.y), f.z);
        }

        private static float Hash(int3 cell, uint seed) => (math.hash(new uint4((uint3)cell, seed)) & 0xffffffu) / 16777215f;
    }
}
