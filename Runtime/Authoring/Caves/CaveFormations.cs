using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // One kind of formation in a Cave Formations feature: its prototype, how many grow per 100 square metres of the
    // surface it grows on, and the underground biomes it grows in.
    [Serializable]
    public struct StrataFormationEntry
    {
        public StrataFormationPrototype Prototype;
        [Tooltip("Formations per 100 square metres of its surface (floor, ceiling or wall).")]
        [Min(0f)] public float Density;
        [Tooltip("The underground biomes it grows in.")]
        public StrataBiomeFilter Biomes;
    }

    // Scatters cave formations (stalactites, stalagmites, crystals) over the cave surfaces inside this object's box
    // (scale = size in metres, turned with it): each entry's prototype grows on its kind of surface at its density, at
    // spots hashed from the surface itself, so a bake places them the same way every time. Only cave walls count, not
    // the ground near a cave's mouth. The bake stores them with the tiles; they are drawn instanced and, where the
    // prototype says, collide.
    [AddComponentMenu("Strata/Cave Formations")]
    public class CaveFormations : StrataFeature
    {
        [SerializeField] private List<StrataFormationEntry> _entries = new();
        [Tooltip("Changes where formations grow.")]
        [SerializeField] private int _seed = 1;

        public IReadOnlyList<StrataFormationEntry> Entries => _entries;
        public int Seed => _seed;
        public override bool AffectsHeights => false;

        // The box's corners' footprint on the ground.
        public override Rect Bounds
        {
            get
            {
                Bounds box = Box;
                return new Rect(box.min.x, box.min.z, box.size.x, box.size.z);
            }
        }

        // The world-space box around the turned footprint.
        public Bounds Box
        {
            get
            {
                float3x3 axes = new(transform.rotation);
                float3 half = math.abs((float3)transform.lossyScale) * 0.5f;
                float3 extent = math.abs(axes.c0) * half.x + math.abs(axes.c1) * half.y + math.abs(axes.c2) * half.z;
                return new Bounds(transform.position, extent * 2f);
            }
        }

        // World to the box's unit cube (-0.5..0.5 inside).
        public Matrix4x4 WorldToBox => Matrix4x4.TRS(transform.position, transform.rotation, Vector3.Max(transform.lossyScale, Vector3.one * 1e-3f)).inverse;

        private void Reset() => transform.localScale = new Vector3(80f, 40f, 80f);

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.7f, 0.5f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
