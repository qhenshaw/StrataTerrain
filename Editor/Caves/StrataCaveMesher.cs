using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Strata.Editor
{
    // The Burst jobs that turn a StrataCaveField into what the bake stores. BrickJob meshes one brick (32 cells a side,
    // at its level's cell size) with surface nets: it evaluates the field on the brick's corners and a one-cell margin,
    // emits a quad for every corner-to-corner edge it owns (edges whose lower corner is inside the brick) that crosses the
    // surface near a feature, and places each quad's cell vertex at the mean of its cell's edge crossings. Every brick
    // computes shared cells from the same global lattice positions, so neighbouring bricks (and tiles) of a level agree
    // on them exactly and the mesh is watertight without stitching, and vertices come out in cell order, so a bake is
    // repeatable byte for byte. Vertices on untouched ground are pulled just under the terrain, and carve vertices are
    // kept below it, so the band of cave mesh around every entrance sits under the terrain's clipped edge and hides the
    // seam. Coarse bricks also get skirts: their open edges (where a brick of another level may meet them) extruded into
    // the rock and drawn from both sides, which covers the cracks between levels. HoleJob works out a tile's terrain
    // presence, and InfluenceJob one feature's weight on the ground (for its paint and tree effect).
    public static class StrataCaveMesher
    {
        private const int Cells = StrataCaveData.BrickCells;
        // corners -1..32 and cells -1..31 on each axis
        private const int Corners = Cells + 2;
        private const int CellSide = Cells + 1;
        // cells per block when finding where the surface is
        private const int Block = 4;

        [BurstCompile]
        public struct BrickJob : IJob
        {
            public StrataCaveField Field;
            public int3 Coord;
            public int Level;
            // this level's cell size
            public float Voxel;
            public float3 Lattice;
            // how far from a feature its surface is still meshed, in metres
            public float Reach;
            // how far under the terrain ground and carve vertices are kept, in metres
            public float SnapDepth;
            // over what distance from the ground surface walls blend into the ground's layers, in metres
            public float BlendDepth;
            public NativeList<StrataCaveData.Vertex> Vertices;
            public NativeList<ushort> Indices;
            // 1 if any vertex is near the ground's surface, then the area of triangles that belong to features
            public NativeArray<float> Stats;

            private float3 Corner(int3 local) => Lattice + (float3)(Coord * Cells + local) * Voxel;
            private static int CornerIndex(int3 local) => ((local.z + 1) * Corners + local.y + 1) * Corners + local.x + 1;
            private static int CellIndex(int3 local) => ((local.z + 1) * CellSide + local.y + 1) * CellSide + local.x + 1;

            public void Execute()
            {
                StrataCaveField.Scope scope = new(Allocator.Temp);
                Field.Gather(Corner(-1), Corner(Cells + 1), ref scope);
                if (scope.IsEmpty)
                {
                    scope.Dispose();
                    return;
                }

                // the ground's height and slope factor per column of corners
                NativeArray<float> heights = new(Corners * Corners, Allocator.Temp);
                NativeArray<float> scales = new(Corners * Corners, Allocator.Temp);
                for (int z = 0; z < Corners; z++)
                for (int x = 0; x < Corners; x++)
                {
                    float3 p = Corner(new int3(x - 1, 0, z - 1));
                    scales[z * Corners + x] = Field.Ground(p.xz, out float height, out float2 _);
                    heights[z * Corners + x] = height;
                }

                // the field on the corners of blocks of cells first, and in full only inside blocks that can matter. A block
                // whose corners all lie on one side, farther from the surface than the block is across (twice over, for
                // noise), holds no surface and takes their side. A block whose corners are all far from every shape (by
                // the block's size and a cell neighbourhood, for noise) only has the ground there that matters, which is
                // exactly the field wherever it is near the surface. Either way the values that place vertices are the
                // field's own, the same in every brick sharing them, so meshes still meet exactly.
                NativeArray<float> values = new(Corners * Corners * Corners, Allocator.Temp);
                NativeArray<float> nearest = new(values.Length, Allocator.Temp);
                NativeArray<bool> keep = new(values.Length, Allocator.Temp);
                NativeArray<bool> done = new(values.Length, Allocator.Temp);
                float diagonal = Block * Voxel * 1.7320508f;
                float far = diagonal * 2f;
                float clear = Reach + diagonal * 1.5f + Voxel * 6f;
                bool solid = false, air = false;
                for (int bz = 0; bz < Corners - 1; bz += Block)
                for (int by = 0; by < Corners - 1; by += Block)
                for (int bx = 0; bx < Corners - 1; bx += Block)
                {
                    int3 lo = new(bx, by, bz), hi = math.min(lo + Block, Corners - 1);
                    bool settled = true, isolated = true, sign = false;
                    for (int c = 0; c < 8; c++)
                    {
                        int3 corner = math.select(lo, hi, new bool3((c & 1) != 0, (c & 2) != 0, (c & 4) != 0));
                        float value = Sample(corner, scope, heights, scales, values, nearest, keep, done);
                        settled &= math.abs(value) > far && (c == 0 || value < 0f == sign);
                        isolated &= nearest[CornerIndex(corner - 1)] > clear;
                        sign = value < 0f;
                    }
                    for (int z = lo.z; z <= hi.z; z++)
                    for (int y = lo.y; y <= hi.y; y++)
                    for (int x = lo.x; x <= hi.x; x++)
                    {
                        int3 corner = new(x, y, z);
                        int index = CornerIndex(corner - 1);
                        if (!done[index])
                        {
                            if (settled) values[index] = sign ? -far : far;
                            else if (isolated)
                            {
                                int column = z * Corners + x;
                                values[index] = (Corner(corner - 1).y - heights[column]) * scales[column];
                            }
                            else Sample(corner, scope, heights, scales, values, nearest, keep, done);
                        }
                        solid |= values[index] < 0f;
                        air |= values[index] >= 0f;
                    }
                }
                done.Dispose();
                nearest.Dispose();

                if (solid && air) Mesh(scope, values, keep);
                values.Dispose();
                keep.Dispose();
                heights.Dispose();
                scales.Dispose();
                scope.Dispose();
            }

            // The field at a corner (0-based, so one less is its brick-local cell), evaluated once.
            private float Sample(int3 corner, in StrataCaveField.Scope scope, NativeArray<float> heights, NativeArray<float> scales,
                                 NativeArray<float> values, NativeArray<float> nearest, NativeArray<bool> keep, NativeArray<bool> done)
            {
                int3 local = corner - 1;
                int index = CornerIndex(local);
                if (done[index]) return values[index];
                float3 p = Corner(local);
                int column = corner.z * Corners + corner.x;
                float ground = (p.y - heights[column]) * scales[column];
                float value = Field.Evaluate(p, ground, scope, out int _, out float closest);
                values[index] = value;
                nearest[index] = closest;
                keep[index] = closest < Reach;
                done[index] = true;
                return value;
            }

            private void Mesh(in StrataCaveField.Scope scope, NativeArray<float> values, NativeArray<bool> keep)
            {
                // -1 unused, -2 used, then the vertex index
                NativeArray<int> cells = new(CellSide * CellSide * CellSide, Allocator.Temp);
                for (int i = 0; i < cells.Length; i++) cells[i] = -1;
                NativeList<int4> quads = new(1024, Allocator.Temp);
                NativeList<bool> flips = new(1024, Allocator.Temp);
                for (int z = 0; z < Cells; z++)
                for (int y = 0; y < Cells; y++)
                for (int x = 0; x < Cells; x++)
                {
                    int3 local = new(x, y, z);
                    int corner = CornerIndex(local);
                    bool inside = values[corner] < 0f;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int3 step = Unit(axis);
                        int other = CornerIndex(local + step);
                        if (inside == values[other] < 0f || !keep[corner] && !keep[other]) continue;
                        // the four cells around the edge, counter-clockwise about it seen from its far end
                        int3 u = Unit((axis + 1) % 3), v = Unit((axis + 2) % 3);
                        int4 quad = new(CellIndex(local - u - v), CellIndex(local - v), CellIndex(local), CellIndex(local - u));
                        for (int c = 0; c < 4; c++) cells[quad[c]] = -2;
                        quads.Add(quad);
                        // the quad faces the edge's axis when the edge goes from rock into air
                        flips.Add(!inside);
                    }
                }

                int count = 0;
                for (int i = 0; i < cells.Length; i++)
                    if (cells[i] == -2) cells[i] = count++;
                if (count > 0)
                {
                    NativeList<float3> positions = new(count, Allocator.Temp);
                    NativeList<float3> normals = new(count, Allocator.Temp);
                    NativeList<uint> surfaces = new(count, Allocator.Temp);
                    NativeList<bool> features = new(count, Allocator.Temp);
                    bool exposed = false;
                    for (int i = 0; i < cells.Length; i++)
                    {
                        if (cells[i] < 0) continue;
                        int3 local = new(i % CellSide - 1, i / CellSide % CellSide - 1, i / (CellSide * CellSide) - 1);
                        Finish(scope, CellVertex(local, values), out float3 position, out float3 normal, out uint surface, out bool feature, out bool near);
                        positions.Add(position);
                        normals.Add(normal);
                        surfaces.Add(surface);
                        features.Add(feature);
                        exposed |= near;
                    }

                    NativeList<int> indices = new(quads.Length * 6, Allocator.Temp);
                    float area = 0f;
                    for (int q = 0; q < quads.Length; q++)
                    {
                        int4 quad = quads[q];
                        int a = cells[quad.x], b = cells[quad.y], c = cells[quad.z], d = cells[quad.w];
                        if (flips[q])
                        {
                            int swap = b;
                            b = d;
                            d = swap;
                        }
                        indices.Add(a); indices.Add(b); indices.Add(c);
                        indices.Add(a); indices.Add(c); indices.Add(d);
                        if (features[a] && features[b] && features[c] && features[d])
                            area += (math.length(math.cross(positions[b] - positions[a], positions[c] - positions[a])) +
                                     math.length(math.cross(positions[c] - positions[a], positions[d] - positions[a]))) * 0.5f;
                    }
                    if (Level > 0) AddSkirts(positions, normals, surfaces, indices);

                    if (positions.Length <= 65535)
                    {
                        float3 origin = Corner(int3.zero);
                        Vertices.Capacity = positions.Length;
                        for (int i = 0; i < positions.Length; i++)
                            Vertices.Add(StrataCaveData.Pack(positions[i] - origin, normals[i], surfaces[i], Voxel));
                        Indices.Capacity = indices.Length;
                        for (int i = 0; i < indices.Length; i++) Indices.Add((ushort)indices[i]);
                        Stats[0] = exposed ? 1f : 0f;
                        Stats[1] = area;
                    }
                    positions.Dispose();
                    normals.Dispose();
                    surfaces.Dispose();
                    features.Dispose();
                    indices.Dispose();
                }
                cells.Dispose();
                quads.Dispose();
                flips.Dispose();
            }

            // Extrudes every open edge (used by one triangle) a cell into the rock, as a quad drawn from both sides.
            private void AddSkirts(NativeList<float3> positions, NativeList<float3> normals, NativeList<uint> surfaces, NativeList<int> indices)
            {
                int triangles = indices.Length;
                NativeHashMap<long, int> uses = new(triangles, Allocator.Temp);
                for (int t = 0; t < triangles; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    long key = EdgeKey(indices[t + e], indices[t + (e + 1) % 3]);
                    uses.TryGetValue(key, out int n);
                    uses[key] = n + 1;
                }
                NativeHashMap<int, int> lowered = new(256, Allocator.Temp);
                for (int t = 0; t < triangles; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = indices[t + e], b = indices[t + (e + 1) % 3];
                    if (uses[EdgeKey(a, b)] != 1) continue;
                    int la = Lower(a, positions, normals, surfaces, lowered), lb = Lower(b, positions, normals, surfaces, lowered);
                    indices.Add(a); indices.Add(lb); indices.Add(b);
                    indices.Add(a); indices.Add(la); indices.Add(lb);
                    indices.Add(a); indices.Add(b); indices.Add(lb);
                    indices.Add(a); indices.Add(lb); indices.Add(la);
                }
                uses.Dispose();
                lowered.Dispose();
            }

            private int Lower(int vertex, NativeList<float3> positions, NativeList<float3> normals, NativeList<uint> surfaces, NativeHashMap<int, int> lowered)
            {
                if (lowered.TryGetValue(vertex, out int existing)) return existing;
                int index = positions.Length;
                positions.Add(positions[vertex] - normals[vertex] * Voxel);
                normals.Add(normals[vertex]);
                surfaces.Add(surfaces[vertex]);
                lowered[vertex] = index;
                return index;
            }

            private static long EdgeKey(int a, int b) => (long)math.min(a, b) << 32 | (uint)math.max(a, b);

            private static int3 Unit(int axis) => axis == 0 ? new int3(1, 0, 0) : axis == 1 ? new int3(0, 1, 0) : new int3(0, 0, 1);

            // The mean of the surface crossings on a cell's twelve edges.
            private float3 CellVertex(int3 cell, NativeArray<float> values)
            {
                float3 sum = 0f;
                int crossings = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    int3 step = Unit(axis), u = Unit((axis + 1) % 3), v = Unit((axis + 2) % 3);
                    for (int k = 0; k < 4; k++)
                    {
                        int3 a = cell + u * (k & 1) + v * (k >> 1);
                        float fa = values[CornerIndex(a)], fb = values[CornerIndex(a + step)];
                        if (fa < 0f == fb < 0f) continue;
                        float t = fa / (fa - fb);
                        sum += Corner(a) + (float3)step * (t * Voxel);
                        crossings++;
                    }
                }
                return crossings > 0 ? sum / crossings : Corner(cell) + Voxel * 0.5f;
            }

            // A vertex's final position, normal and surface; feature if a feature (not the ground) shapes it, near if it is
            // close enough to the ground's surface to be seen from outside.
            private void Finish(in StrataCaveField.Scope scope, float3 p, out float3 position, out float3 normal, out uint surface, out bool feature, out bool near)
            {
                float scale = Field.Ground(p.xz, out float height, out float2 gradient);
                float ground = (p.y - height) * scale;
                float value = Field.Evaluate(p, ground, scope, out int dominant, out float _);
                float blend;
                if (math.abs(value - ground) < 0.05f * Voxel)
                {
                    // untouched ground in the rim around a feature: just under the terrain, which it continues
                    p.y = height - SnapDepth;
                    normal = math.normalize(new float3(-gradient.x, 1f, -gradient.y));
                    dominant = -1;
                    blend = 1f;
                }
                else
                {
                    normal = Gradient(scope, p);
                    if (dominant >= 0 && Field.Shapes[dominant].Operation == StrataCaveOperation.Carve) p.y = math.min(p.y, height - SnapDepth);
                    blend = math.saturate(1f - math.abs(height - p.y) / BlendDepth);
                }

                int wall = -1, floor = -1;
                float floorWeight = 0f;
                if (dominant >= 0)
                {
                    StrataCaveShape shape = Field.Shapes[dominant];
                    wall = shape.WallLayer;
                    floor = shape.FloorLayer;
                    if (shape.WallOptions + shape.FloorOptions > 0)
                    {
                        int biome = Field.UndergroundBiome(p);
                        wall = Field.PickLayer(shape.FirstOption, shape.WallOptions, biome, shape.WallLayer);
                        floor = Field.PickLayer(shape.FirstOption + shape.WallOptions, shape.FloorOptions, biome, shape.ExplicitFloor ? shape.FloorLayer : wall);
                    }
                    floorWeight = math.saturate((normal.y - (shape.FloorCos - 0.1f)) / 0.2f);
                }
                position = p;
                surface = StrataCaveData.PackSurface(wall, floor, floorWeight, blend);
                feature = dominant >= 0;
                near = blend > 0f;
            }

            private float3 Gradient(in StrataCaveField.Scope scope, float3 p)
            {
                float e = Voxel * 0.25f;
                float3 gradient = new(Value(scope, p + new float3(e, 0f, 0f)) - Value(scope, p - new float3(e, 0f, 0f)),
                                      Value(scope, p + new float3(0f, e, 0f)) - Value(scope, p - new float3(0f, e, 0f)),
                                      Value(scope, p + new float3(0f, 0f, e)) - Value(scope, p - new float3(0f, 0f, e)));
                return math.lengthsq(gradient) > 1e-12f ? math.normalize(gradient) : new float3(0f, 1f, 0f);
            }

            private float Value(in StrataCaveField.Scope scope, float3 p) => Field.Evaluate(p, Field.GroundDistance(p), scope, out int _, out float _);
        }

        // A tile's terrain presence over a rectangle of its samples, a row per index (the rest of the tile is left as it
        // is): the field just under the ground, so where a carve has made air there the terrain is a hole.
        [BurstCompile]
        public struct HoleJob : IJobParallelFor
        {
            public StrataCaveField Field;
            public int2 Tile;
            // tile-local sample rectangle: x, z, width, height
            public int4 Rect;
            [NativeDisableParallelForRestriction] public NativeArray<byte> Presence;

            public void Execute(int row)
            {
                StrataGrid grid = Field.Heights.Grid;
                int z = Rect.y + row;
                int2 first = Tile * grid.Resolution + new int2(Rect.x, z);
                float2 min = grid.SamplePosition(first), max = grid.SamplePosition(first + new int2(Rect.z, 0));
                StrataCaveField.Scope scope = new(Allocator.Temp);
                Field.Gather(new float3(min.x, grid.MinHeight, min.y), new float3(max.x, grid.MaxHeight, max.y), ref scope);
                float depth = grid.Spacing * 0.5f;
                for (int x = Rect.x; x < Rect.x + Rect.z; x++)
                {
                    if (scope.IsEmpty)
                    {
                        Presence[z * grid.Samples + x] = StrataHoles.Solid;
                        continue;
                    }
                    int2 sample = Tile * grid.Resolution + new int2(x, z);
                    float2 xz = grid.SamplePosition(sample);
                    float3 p = new(xz.x, Field.Heights.At(sample) - depth, xz.y);
                    float value = Field.Evaluate(p, Field.GroundDistance(p), scope, out int _, out float _);
                    Presence[z * grid.Samples + x] = (byte)math.round(math.saturate(0.5f - value / (2f * grid.Spacing)) * 255f);
                }
                scope.Dispose();
            }
        }

        // One feature's weight on the ground over a rectangle of world samples, a row per index: 1 inside its shape at
        // the ground (or, for an add, anywhere in the column above), fading out over Reach beyond it.
        [BurstCompile]
        public struct InfluenceJob : IJobParallelFor
        {
            public StrataCaveField Field;
            public int Shape;
            public float Reach;
            // world sample rectangle: x, z, width, height
            public int4 Rect;
            [NativeDisableParallelForRestriction] public NativeArray<float> Influence;

            public void Execute(int z)
            {
                StrataGrid grid = Field.Heights.Grid;
                StrataCaveShape shape = Field.Shapes[Shape];
                // only the primitives whose box (grown by what can still give weight) the row's samples reach
                float pad = Reach + shape.NoiseAmplitude;
                float2 rowMin = grid.SamplePosition(Rect.xy + new int2(0, z)), rowMax = grid.SamplePosition(Rect.xy + new int2(Rect.z - 1, z));
                NativeList<int> near = new(16, Allocator.Temp);
                for (int i = shape.First; i < shape.First + shape.Count; i++)
                {
                    StrataCavePrimitive primitive = Field.Primitives[i];
                    if (primitive.Max.x + pad >= rowMin.x && primitive.Min.x - pad <= rowMax.x &&
                        primitive.Max.z + pad >= rowMin.y && primitive.Min.z - pad <= rowMax.y) near.Add(i);
                }
                for (int x = 0; x < Rect.z; x++)
                {
                    float weight = 0f;
                    if (near.Length > 0)
                    {
                        int2 sample = Rect.xy + new int2(x, z);
                        float2 xz = grid.SamplePosition(sample);
                        float height = Field.Heights.At(sample);
                        float d = Distance(shape, near, new float3(xz.x, height, xz.y), pad);
                        if (shape.Operation == StrataCaveOperation.Add)
                            for (int k = 1; k <= 8 && height < shape.Max.y; k++)
                                d = math.min(d, Distance(shape, near, new float3(xz.x, math.lerp(height, shape.Max.y, k / 8f), xz.y), pad));
                        weight = math.saturate(1f - d / Reach);
                    }
                    Influence[z * Rect.z + x] = weight;
                }
                near.Dispose();
            }

            // The shape's distance at p over the given primitives, Far where none is within pad.
            private float Distance(StrataCaveShape shape, NativeList<int> primitives, float3 p, float pad)
            {
                float d = StrataCaveField.Far;
                for (int i = 0; i < primitives.Length; i++)
                {
                    StrataCavePrimitive primitive = Field.Primitives[primitives[i]];
                    if (math.any(p < primitive.Min - pad) || math.any(p > primitive.Max + pad)) continue;
                    d = math.min(d, StrataCaveField.PrimitiveDistance(primitive, p));
                }
                if (d >= StrataCaveField.Far) return d;
                return shape.NoiseAmplitude > 0f ? d + shape.NoiseAmplitude * StrataCaveField.Noise(p * shape.NoiseFrequency, shape.NoiseOctaves, shape.Seed) : d;
            }
        }
    }
}
