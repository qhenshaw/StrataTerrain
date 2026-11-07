using UnityEngine;
using UnityEngine.Rendering;

namespace Strata
{
    // Cuts a dithered see-through hole in Strata trees between a camera and this transform (e.g. the player in a
    // top-down game), so canopies and trunks in front of it don't hide it. Shadows are untouched. The hole is a
    // cylinder of Radius around the camera-to-target line, ending Margin before the target, and thins out over
    // Softness at its edge. Only Strata/Tree draws (not impostors, which are far away) use it. One per scene: it sets
    // shader globals just before each camera renders, on for the chosen camera and off for every other (Scene view,
    // reflection probes...).
    [DisallowMultipleComponent]
    public class StrataOcclusionFade : MonoBehaviour
    {
        private static readonly int TargetId = Shader.PropertyToID("_StrataOcclusionTarget");
        private static readonly int ParamsId = Shader.PropertyToID("_StrataOcclusionParams");

        [Tooltip("Camera that sees through the trees. Empty uses the main camera.")]
        [SerializeField] private Camera targetCamera;

        [Tooltip("Point the hole aims at, as a world-space offset from this transform (e.g. up to the middle of the character).")]
        [SerializeField] private Vector3 targetOffset = new(0f, 1f, 0f);

        [Tooltip("Radius of the see-through hole around the camera-to-target line, in metres.")]
        [SerializeField, Min(0f)] private float radius = 3f;

        [Tooltip("Width of the dithered edge where the hole fades back to solid, in metres.")]
        [SerializeField, Min(0.01f)] private float softness = 1.5f;

        [Tooltip("Trees closer to the target than this (along the view line) stay solid, e.g. the trunk she stands by.")]
        [SerializeField, Min(0f)] private float margin = 1f;

        [Tooltip("How much of the hole's centre is cleared: 1 = fully see-through, 0.5 = half the pixels kept.")]
        [SerializeField, Range(0f, 1f)] private float strength = 1f;

        private void OnEnable() => RenderPipelineManager.beginCameraRendering += BeforeCamera;

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            Shader.SetGlobalVector(TargetId, Vector4.zero);
        }

        private void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            Camera view = targetCamera != null ? targetCamera : Camera.main;
            bool on = camera == view && isActiveAndEnabled;
            Vector3 target = transform.position + targetOffset;
            Shader.SetGlobalVector(TargetId, new Vector4(target.x, target.y, target.z, on ? 1f : 0f));
            Shader.SetGlobalVector(ParamsId, new Vector4(radius, softness, margin, strength));
        }
    }
}
