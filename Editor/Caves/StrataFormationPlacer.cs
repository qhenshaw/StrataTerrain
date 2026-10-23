using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Strata.Editor
{
    // Places cave formations on the level 0 cave meshes. Each triangle of a cave's own surface (not the ground around its
    // mouths) is floor, ceiling or wall by its normal; for every Cave Formations box holding its centre, each entry for
    // that surface expects area times density formations there, the count and spots drawn from hashes of the triangle's
    // quantised centre, so every bake places the same formations whichever brick or tile the triangle is meshed in. An
    // entry limited to underground biomes expects that many times its biome filter there.
    public static class StrataFormationPlacer
    {
        public struct Box
        {
            public float4x4 WorldToBox;
            public uint Seed;
            public int First;
            public int Count;
        }

        public struct Entry
        {
            public int Prototype;
            // per square metre
            public float Density;
            public int Surface;
            public float Align;
            public float MinScale;
            public float MaxScale;
            // metres the base sinks at scale 1
            public float Sink;
            // the underground biomes it grows in (a 256-bit mask), unless Any
            public bool Filtered;
            public uint4 MaskLow;
            public uint4 MaskHigh;
        }

        public struct Placed
        {
            public float3 Position;
            public quaternion Rotation;
            public float Scale;
            public int Prototype;
        }

        [BurstCompile]
        public struct PlaceJob : IJob
        {
            [ReadOnly] public NativeArray<StrataCaveData.Vertex> Vertices;
            [ReadOnly] public NativeArray<ushort> Indices;
            public float3 Origin;
            public float Cell;
            [ReadOnly] public NativeArray<Box> Boxes;
            [ReadOnly] public NativeArray<Entry> Entries;
            public StrataBiomeMap.View Biomes;
            public NativeList<Placed> Output;

            public void Execute()
            {
                for (int t = 0; t + 2 < Indices.Length; t += 3)
                {
                    StrataCaveData.Vertex va = Vertices[Indices[t]], vb = Vertices[Indices[t + 1]], vc = Vertices[Indices[t + 2]];
                    // the cave's own surface: a wall layer set, and not blending into the ground
                    if (!Cave(va) || !Cave(vb) || !Cave(vc)) continue;
                    float3 a = Origin + StrataCaveData.Unpack(va, Cell), b = Origin + StrataCaveData.Unpack(vb, Cell), c = Origin + StrataCaveData.Unpack(vc, Cell);
                    float3 cross = math.cross(b - a, c - a);
                    float area = math.length(cross) * 0.5f;
                    if (area < 1e-4f) continue;
                    float3 normal = cross / (area * 2f);
                    int surface = normal.y > 0.6f ? (int)StrataFormationSurface.Floor : normal.y < -0.6f ? (int)StrataFormationSurface.Ceiling : (int)StrataFormationSurface.Wall;
                    float3 centre = (a + b + c) / 3f;
                    uint3 key = (uint3)(int3)math.round(centre * 100f);
                    for (int bi = 0; bi < Boxes.Length; bi++)
                    {
                        Box box = Boxes[bi];
                        float3 inBox = math.transform(box.WorldToBox, centre);
                        if (math.any(math.abs(inBox) > 0.5f)) continue;
                        for (int e = box.First; e < box.First + box.Count; e++)
                        {
                            Entry entry = Entries[e];
                            if (entry.Surface != surface || entry.Density <= 0f) continue;
                            uint seed = math.hash(new uint4(key, box.Seed ^ ((uint)e * 0x9e3779b9u)));
                            float expected = area * entry.Density;
                            if (entry.Filtered) expected *= Gate(entry, centre);
                            int count = (int)expected + (Unit(seed, 0) < expected - (int)expected ? 1 : 0);
                            for (int k = 0; k < count; k++)
                            {
                                float r1 = Unit(seed, 1 + k * 5), r2 = Unit(seed, 2 + k * 5);
                                if (r1 + r2 > 1f)
                                {
                                    r1 = 1f - r1;
                                    r2 = 1f - r2;
                                }
                                float3 p = a + (b - a) * r1 + (c - a) * r2;
                                float3 gravity = surface == (int)StrataFormationSurface.Floor ? new float3(0f, 1f, 0f) :
                                                 surface == (int)StrataFormationSurface.Ceiling ? new float3(0f, -1f, 0f) : normal;
                                float3 axis = math.normalizesafe(math.lerp(gravity, normal, entry.Align), gravity);
                                quaternion turn = math.mul(FromTo(new float3(0f, 1f, 0f), axis), quaternion.RotateY(Unit(seed, 3 + k * 5) * math.PI * 2f));
                                float scale = math.lerp(entry.MinScale, entry.MaxScale, Unit(seed, 4 + k * 5));
                                Output.Add(new Placed { Position = p - axis * entry.Sink * scale, Rotation = turn, Scale = scale, Prototype = entry.Prototype });
                            }
                        }
                    }
                }
            }

            // the entry's biome filter at a point, blended by the two strongest underground biomes there
            private float Gate(Entry entry, float3 p)
            {
                if (!Biomes.IsCreated) return 0f;
                uint texel = Biomes.Sample(p.xz, true);
                float w0 = ((texel >> 16) & 0xff) / 255f;
                float g0 = StrataBiomeFilter.MaskAllows(entry.MaskLow, entry.MaskHigh, (int)(texel & 0xff)) ? 1f : 0f;
                float g1 = StrataBiomeFilter.MaskAllows(entry.MaskLow, entry.MaskHigh, (int)((texel >> 8) & 0xff)) ? 1f : 0f;
                return g1 + (g0 - g1) * w0;
            }

            private static bool Cave(StrataCaveData.Vertex v) => (v.Surface & 0xff) != StrataCaveData.NoLayer && (v.Surface >> 24) < 128;

            private static float Unit(uint seed, int index) => (math.hash(new uint2(seed, (uint)index)) & 0xffffffu) / 16777216f;

            private static quaternion FromTo(float3 from, float3 to)
            {
                float d = math.dot(from, to);
                if (d < -0.9999f) return quaternion.AxisAngle(new float3(1f, 0f, 0f), math.PI);
                float3 c = math.cross(from, to);
                return math.normalize(new quaternion(c.x, c.y, c.z, 1f + d));
            }
        }

        // The formation prototypes the features use, in order of first use.
        public static List<StrataFormationPrototype> Prototypes(IEnumerable<CaveFormations> formations)
        {
            List<StrataFormationPrototype> prototypes = new();
            foreach (CaveFormations feature in formations)
                foreach (StrataFormationEntry entry in feature.Entries)
                    if (entry.Prototype != null && entry.Prototype.IsValid && !prototypes.Contains(entry.Prototype)) prototypes.Add(entry.Prototype);
            return prototypes;
        }

        // The features' boxes and entries for the job, against the given prototype list.
        public static void Inputs(IEnumerable<CaveFormations> formations, List<StrataFormationPrototype> prototypes, IReadOnlyList<StrataBiomeArea> biomes,
                                  out NativeArray<Box> boxes, out NativeArray<Entry> entries)
        {
            List<Box> boxList = new();
            List<Entry> entryList = new();
            foreach (CaveFormations feature in formations)
            {
                int first = entryList.Count;
                foreach (StrataFormationEntry entry in feature.Entries)
                {
                    int index = entry.Prototype != null ? prototypes.IndexOf(entry.Prototype) : -1;
                    if (index < 0) continue;
                    StrataFormationPrototype prototype = entry.Prototype;
                    entry.Biomes.Mask(biomes, out uint4 low, out uint4 high);
                    entryList.Add(new Entry
                    {
                        Prototype = index,
                        Density = entry.Density / 100f * feature.Strength,
                        Surface = (int)prototype.Surface,
                        Align = prototype.Surface == StrataFormationSurface.Wall ? 1f : prototype.Align,
                        MinScale = prototype.MinScale,
                        MaxScale = prototype.MaxScale,
                        Sink = prototype.Sink * prototype.Mesh.bounds.max.y,
                        Filtered = !entry.Biomes.IsAny,
                        MaskLow = low,
                        MaskHigh = high
                    });
                }
                if (entryList.Count > first)
                    boxList.Add(new Box { WorldToBox = feature.WorldToBox, Seed = (uint)feature.Seed, First = first, Count = entryList.Count - first });
            }
            boxes = new NativeArray<Box>(boxList.ToArray(), Allocator.TempJob);
            entries = new NativeArray<Entry>(entryList.ToArray(), Allocator.TempJob);
        }
    }
}
