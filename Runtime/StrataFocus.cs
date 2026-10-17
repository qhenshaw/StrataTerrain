using System.Collections.Generic;
using UnityEngine;

namespace Strata
{
    // Marks a transform that every Strata world should stream collision around, without any code: put it on players,
    // physics props or test objects. Worlds read the enabled foci each frame, so enable order doesn't matter.
    [DisallowMultipleComponent]
    public class StrataFocus : MonoBehaviour
    {
        private static readonly List<Transform> s_active = new();

        public static IReadOnlyList<Transform> Active => s_active;

        private void OnEnable() => s_active.Add(transform);

        private void OnDisable() => s_active.Remove(transform);
    }
}
