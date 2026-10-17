using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace Strata.Editor
{
    // GameObject > Strata menu items: a world, and each kind of feature, created under the selected world (or the
    // selected feature's world) at the scene view's focus point.
    public static class StrataMenu
    {
        [MenuItem("GameObject/Strata/World", false, 10)]
        private static void CreateWorld(MenuCommand command)
        {
            GameObject go = new("Strata World");
            go.AddComponent<StrataWorld>();
            Place(go, command, null);
        }

        [MenuItem("GameObject/Strata/Texture Stamp", false, 21)]
        private static void CreateTextureStamp(MenuCommand command) => CreateFeature<TextureStamp>("Texture Stamp", command);

        [MenuItem("GameObject/Strata/Noise Stamp", false, 22)]
        private static void CreateNoiseStamp(MenuCommand command) => CreateFeature<NoiseStamp>("Noise Stamp", command);

        [MenuItem("GameObject/Strata/Spline Feature", false, 23)]
        private static void CreateSpline(MenuCommand command)
        {
            GameObject go = CreateFeature<SplineFeature>("Spline Feature", command);
            Spline spline = go.GetComponent<SplineContainer>().Spline;
            spline.Clear();
            spline.Add(new BezierKnot(new Vector3(-50f, 0f, 0f)), TangentMode.AutoSmooth);
            spline.Add(new BezierKnot(new Vector3(0f, 0f, 30f)), TangentMode.AutoSmooth);
            spline.Add(new BezierKnot(new Vector3(50f, 0f, 0f)), TangentMode.AutoSmooth);
        }

        [MenuItem("GameObject/Strata/Filter", false, 24)]
        private static void CreateFilter(MenuCommand command) => CreateFeature<FilterFeature>("Filter", command);

        [MenuItem("GameObject/Strata/Rock Strata", false, 24)]
        private static void CreateRockStrata(MenuCommand command) => CreateFeature<RockStrata>("Rock Strata", command);

        [MenuItem("GameObject/Strata/Stamp Group", false, 25)]
        private static void CreateGroup(MenuCommand command) => CreateFeature<StampGroup>("Stamp Group", command);

        // last, so it sees the finished heights and splats and features above it can't change it
        [MenuItem("GameObject/Strata/Tree Scatter", false, 27)]
        private static void CreateTreeScatter(MenuCommand command) => CreateFeature<TreeScatter>("Tree Scatter", command).transform.SetAsLastSibling();

        [MenuItem("GameObject/Strata/Grass Scatter", false, 28)]
        private static void CreateGrassScatter(MenuCommand command) => CreateFeature<GrassScatter>("Grass Scatter", command);

        // first, so every feature that paints paints over the rules
        [MenuItem("GameObject/Strata/Auto Splat", false, 26)]
        private static void CreateAutoSplat(MenuCommand command) => CreateFeature<AutoSplat>("Auto Splat", command).transform.SetAsFirstSibling();

        private static GameObject CreateFeature<T>(string name, MenuCommand command) where T : StrataFeature
        {
            GameObject go = new(name);
            go.AddComponent<T>();
            Transform parent = (command.context as GameObject)?.transform ?? Selection.activeTransform;
            if (parent != null && parent.GetComponentInParent<StrataWorld>() == null) parent = null;
            if (parent == null && StrataWorld.Active.Count > 0) parent = StrataWorld.Active[0].transform;
            Place(go, command, parent);
            return go;
        }

        private static void Place(GameObject go, MenuCommand command, Transform parent)
        {
            Vector3 scale = go.transform.localScale;
            if (parent != null) go.transform.SetParent(parent, false);
            if (SceneView.lastActiveSceneView != null && go.GetComponent<StrataWorld>() == null)
                go.transform.position = SceneView.lastActiveSceneView.pivot;
            go.transform.localScale = scale;
            Undo.RegisterCreatedObjectUndo(go, $"Create {go.name}");
            Selection.activeGameObject = go;
        }
    }
}
