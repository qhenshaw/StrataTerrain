using UnityEngine;
using UnityEngine.Rendering;

namespace Strata
{
    // Parallax occlusion mapping for the terrain and cave surfaces, switched for every Strata material at once: the
    // global STRATA_PARALLAX keyword (so turning it off costs nothing) and the march's step counts and fade distance.
    // A game sets it from its own graphics options; until something does, the first material bind applies the defaults
    // so the editor's views show it too.
    public static class StrataParallax
    {
        public const int DefaultMinSteps = 16, DefaultMaxSteps = 32;
        public const float DefaultDistance = 30f;

        private static readonly GlobalKeyword Keyword = GlobalKeyword.Create("STRATA_PARALLAX");
        private static readonly int ParamsId = Shader.PropertyToID("_StrataParallaxParams");
        private static bool s_set;

        public static bool Enabled { get; private set; }

        // Marches between minSteps (looking straight down) and maxSteps (at grazing angles), fading out by distance metres.
        public static void Set(int minSteps, int maxSteps, float distance)
        {
            s_set = true;
            Enabled = true;
            Shader.SetGlobalVector(ParamsId, new Vector4(Mathf.Max(1, minSteps), Mathf.Max(minSteps, maxSteps), Mathf.Max(distance, 0.01f), 0f));
            Shader.EnableKeyword(Keyword);
        }

        public static void Disable()
        {
            s_set = true;
            Enabled = false;
            Shader.DisableKeyword(Keyword);
        }

        public static void EnsureDefaults()
        {
            if (!s_set) Set(DefaultMinSteps, DefaultMaxSteps, DefaultDistance);
        }
    }
}
