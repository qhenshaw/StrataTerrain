using System;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    public enum StrataCaveOperation
    {
        // hollows the ground out (tunnels, caverns)
        Carve,
        // adds rock (arches, overhangs)
        Add
    }

    // Noise added to a cave feature's distance, so walls aren't smooth tubes and blobs.
    [Serializable]
    public struct StrataCaveNoise
    {
        [Tooltip("How far walls move in and out, in metres.")]
        [Min(0f)] public float Amplitude;
        [Tooltip("Size of the largest bumps, in metres.")]
        [Min(0.5f)] public float Scale;
        [Range(1, 5)] public int Octaves;
        public int Seed;

        public static StrataCaveNoise Default => new() { Amplitude = 0.8f, Scale = 6f, Octaves = 3, Seed = 1 };
    }

    // One piece of a cave feature's shape in world space: a segment of a tube with an elliptical cross section whose
    // radius and flat floor taper from A to B, or an ellipsoid centred on A with half extents B, turned by Rotation.
    public struct StrataCavePrimitive
    {
        public const int Tube = 0;
        public const int Ellipsoid = 1;

        public int Type;
        public float3 A;
        public float3 B;
        // tube: radius at A and B
        public float2 Radius;
        // how far the floor is filled in, 0 (round) to 1 (to the centre line); tube at A and B, ellipsoid in x
        public float2 Floor;
        // tube: cross section height over width
        public float Aspect;
        public quaternion Rotation;
        // the shape's box, before noise and smoothing
        public float3 Min;
        public float3 Max;
    }

    // A cave feature as the field evaluator uses it: how it combines with what is above it, its noise, its primitives
    // (First, Count), its box grown by everything that can reach outside the primitives, and the palette layers its
    // walls and floors take (-1 to show the ground's own layers).
    public struct StrataCaveShape
    {
        public StrataCaveOperation Operation;
        public float Smoothness;
        public float NoiseAmplitude;
        public float NoiseFrequency;
        public int NoiseOctaves;
        public uint Seed;
        public int First;
        public int Count;
        public float3 Min;
        public float3 Max;
        public int WallLayer;
        public int FloorLayer;
        // cosine of the steepest slope still counted as floor
        public float FloorCos;
    }
}
