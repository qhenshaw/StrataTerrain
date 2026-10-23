using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Strata
{
    // An ellipsoid the size of the object's scale, turned with it: Carve makes a cavern (with an optionally flattened
    // floor), Add a boulder of rock, which leaning out of a slope is an overhang.
    [AddComponentMenu("Strata/Cave Chamber")]
    public class CaveChamber : StrataCaveFeature
    {
        [Tooltip("How far the floor is filled in: 0 round, 1 up to the middle.")]
        [SerializeField, Range(0f, 1f)] private float _flatFloor = 0.25f;

        protected override void BuildPrimitives(List<StrataCavePrimitive> primitives)
        {
            float3 half = math.max(math.abs((float3)transform.lossyScale) * 0.5f, 0.25f);
            quaternion rotation = transform.rotation;
            // the turned ellipsoid fits in the box of its turned axes
            float3x3 axes = new(rotation);
            float3 extent = math.abs(axes.c0) * half.x + math.abs(axes.c1) * half.y + math.abs(axes.c2) * half.z;
            float3 centre = transform.position;
            primitives.Add(new StrataCavePrimitive
            {
                Type = StrataCavePrimitive.Ellipsoid,
                A = centre,
                B = half,
                Floor = new float2(_flatFloor, 0f),
                Aspect = 1f,
                Rotation = rotation,
                Min = centre - extent,
                Max = centre + extent
            });
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = GizmoColour;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
            Gizmos.DrawWireSphere(Vector3.zero, 0.5f);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
