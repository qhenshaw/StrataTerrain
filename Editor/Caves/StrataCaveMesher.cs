using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Strata.Editor
{
    // The Burst jobs that turn a StrataCaveField into what the bake stores. BrickJob meshes one brick (32 cells a side)
    // with surface nets: it evaluates the field on the brick's corners and a one-cell margin, emits a quad for every
    // corner-to-corner edge it owns (edges whose lower corner is inside the brick) that crosses the surface near a
    // feature, and places each quad's cell vertex at the mean of its cell's edge crossings. Every brick computes shared
    // cells from the same global lattice positions, so neighbouring bricks (and tiles) agree on them exactly and the mesh
    // is watertight without stitching, and vertices come out in cell order, so a bake is repeatable byte for byte.
    // Vertices on untouched ground are pulled just under the terrain, and carve vertices are kept below it, so the band
    // of cave mesh around every entrance sits under the terrain's clipped edge and hides the seam. HoleJob works out a
    // tile's terrain presence, and InfluenceJob one feature's weight on the ground (for its paint and tree effect).
    public static class StrataCaveMesher
    {
        private const int Cells = StrataCaveData.BrickCells;
        // corners -1..32 and cells -1..31 on each axis
        private const int Corners = Cells + 2;
        private const int CellSide = Cells + 1;

        [BurstCompile]
        public struct BrickJob : IJob
        {
            public StrataCaveField Field;
            public int3 Coord;
            public float Voxel;
            public float3 Lattice;
            // how far under the terrain ground and carve vertices are kept, in metres
            public float SnapDepth;
            // over what distance from the ground surface walls blend into the ground's layers, in metres
            public float BlendDepth;
            public NativeList<StrataCaveData.Vertex> Vertices;
            public NativeList<ushort> Indices;

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

                NativeArray<float> values = new(Corners * Corners * Corners, Allocator.Temp);
                NativeArray<bool> keep = new(values.Length, Allocator.Temp);
                bool solid = false, air = false;
                for (int z = 0; z < Corners; z++)
                for (int y = 0; y < Corners; y++)
                for (int x = 0; x < Corners; x++)
                {
                    int3 local = new(x - 1, y - 1, z - 1);
                    float3 p = Corner(local);
                    int column = z * Corners + x;
                    float ground = (p.y - heights[column]) * scales[column];
                    float value = Field.Evaluate(p, ground, scope, out int _, out float nearest);
                    int index = CornerIndex(local);
                    values[index] = value;
                    keep[index] = nearest < Field.Reach;
                    solid |= value < 0f;
                    air |= value >= 0f;
                }

                if (solid && air) Mesh(scope, values, keep);
                values.Dispose();
                keep.Dispose();
                heights.Dispose();
                scales.Dispose();
                scope.Dispose();
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
                if (count > 0 && count <= 65535)
                {
                    Vertices.Capacity = count;
                    float3 origin = Corner(int3.zero);
                    for (int i = 0; i < cells.Length; i++)
                    {
                        if (cells[i] < 0) continue;
                        int3 local = new(i % CellSide - 1, i / CellSide % CellSide - 1, i / (CellSide * CellSide) - 1);
                        Vertices.Add(Finish(scope, CellVertex(local, values), origin));
                    }
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
                        Indices.Add((ushort)a); Indices.Add((ushort)b); Indices.Add((ushort)c);
                        Indices.Add((ushort)a); Indices.Add((ushort)c); Indices.Add((ushort)d);
                    }
                }
                cells.Dispose();
                quads.Dispose();
                flips.Dispose();
            }

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

            private StrataCaveData.Vertex Finish(in StrataCaveField.Scope scope, float3 p, float3 origin)
            {
                float scale = Field.Ground(p.xz, out float height, out float2 gradient);
                float ground = (p.y - height) * scale;
                float value = Field.Evaluate(p, ground, scope, out int dominant, out float _);
                float3 normal;
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
                    floorWeight = math.saturate((normal.y - (shape.FloorCos - 0.1f)) / 0.2f);
                }
                return StrataCaveData.Pack(p - origin, normal, StrataCaveData.PackSurface(wall, floor, floorWeight, blend), Voxel);
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

        // A tile's terrain presence over a rectangle of its samples (the rest stays solid): the field just under the
        // ground, so where a carve has made air there the terrain is a hole.
        [BurstCompile]
        public struct HoleJob : IJob
        {
            public StrataCaveField Field;
            public int2 Tile;
            // tile-local sample rectangle: x, z, width, height
            public int4 Rect;
            public NativeArray<byte> Presence;
            public NativeArray<int> Holed;

            public void Execute()
            {
                StrataGrid grid = Field.Heights.Grid;
                for (int i = 0; i < Presence.Length; i++) Presence[i] = StrataHoles.Solid;
                int2 first = Tile * grid.Resolution + Rect.xy;
                float2 min = grid.SamplePosition(first), max = grid.SamplePosition(first + Rect.zw);
                StrataCaveField.Scope scope = new(Allocator.Temp);
                Field.Gather(new float3(min.x, grid.MinHeight, min.y), new float3(max.x, grid.MaxHeight, max.y), ref scope);
                float depth = grid.Spacing * 0.5f;
                int holed = 0;
                if (!scope.IsEmpty)
                    for (int z = Rect.y; z < Rect.y + Rect.w; z++)
                    for (int x = Rect.x; x < Rect.x + Rect.z; x++)
                    {
                        int2 sample = Tile * grid.Resolution + new int2(x, z);
                        float2 xz = grid.SamplePosition(sample);
                        float3 p = new(xz.x, Field.Heights.At(sample) - depth, xz.y);
                        float value = Field.Evaluate(p, Field.GroundDistance(p), scope, out int _, out float _);
                        byte presence = (byte)math.round(math.saturate(0.5f - value / (2f * grid.Spacing)) * 255f);
                        Presence[z * grid.Samples + x] = presence;
                        if (presence < 128) holed = 1;
                    }
                Holed[0] = holed;
                scope.Dispose();
            }
        }

        // One feature's weight on the ground over a rectangle of world samples: 1 inside its shape at the ground (or, for
        // an add, anywhere in the column above), fading out over Reach beyond it.
        [BurstCompile]
        public struct InfluenceJob : IJob
        {
            public StrataCaveField Field;
            public int Shape;
            public float Reach;
            // world sample rectangle: x, z, width, height
            public int4 Rect;
            public NativeArray<float> Influence;

            public void Execute()
            {
                StrataGrid grid = Field.Heights.Grid;
                StrataCaveShape shape = Field.Shapes[Shape];
                for (int z = 0; z < Rect.w; z++)
                for (int x = 0; x < Rect.z; x++)
                {
                    int2 sample = Rect.xy + new int2(x, z);
                    float2 xz = grid.SamplePosition(sample);
                    float height = Field.Heights.At(sample);
                    float d = Field.ShapeDistance(shape, new float3(xz.x, height, xz.y));
                    if (shape.Operation == StrataCaveOperation.Add)
                        for (int k = 1; k <= 8 && height < shape.Max.y; k++)
                            d = math.min(d, Field.ShapeDistance(shape, new float3(xz.x, math.lerp(height, shape.Max.y, k / 8f), xz.y)));
                    Influence[z * Rect.z + x] = math.saturate(1f - d / Reach);
                }
            }
        }
    }
}
