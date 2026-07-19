using System.Collections;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Cross-fades between Map and AR over <c>transitionDuration</c> (0.6 s, smoothstep) —
    /// spec §11.4. Subscribes to <see cref="GameManager.OnViewModeChanged"/>.
    ///
    /// In phase 4 there is no AR assembly yet, so the AR side is a no-op stub: it toggles a
    /// CanvasGroup and (if present) a second camera. Phase 9 wires <see cref="IARViewController"/>
    /// via the service locator so this code doesn't reference AR Foundation directly.
    /// </summary>
    public class ViewTransitionManager : MonoBehaviour
    {
        [SerializeField] private float transitionDuration = 0.6f;
        [SerializeField] private CanvasGroup mapCanvasGroup;
        [SerializeField] private CanvasGroup arCanvasGroup;
        [SerializeField] private Camera mainCamera;
        [SerializeField] private Camera arCamera;

        private Coroutine _routine;

        private void OnEnable()
        {
            if (GameManager.HasInstance)
                GameManager.Instance.OnViewModeChanged += HandleViewModeChanged;
            GameEvents.ViewModeChanged += HandleViewModeBus;
        }

        private void OnDisable()
        {
            if (GameManager.HasInstance)
                GameManager.Instance.OnViewModeChanged -= HandleViewModeChanged;
            GameEvents.ViewModeChanged -= HandleViewModeBus;
        }

        private void HandleViewModeChanged(ViewMode mode) => StartTransition(mode);
        private void HandleViewModeBus(ViewMode mode) { /* already handled by direct subscription */ }

        private void StartTransition(ViewMode mode)
        {
            if (_routine != null) StopCoroutine(_routine);
            _routine = StartCoroutine(CoTransition(mode));
        }

        private IEnumerator CoTransition(ViewMode mode)
        {
            float targetMapAlpha = mode == ViewMode.Map ? 1f : 0f;
            float targetArAlpha = mode == ViewMode.AR ? 1f : 0f;

            float startMap = mapCanvasGroup != null ? mapCanvasGroup.alpha : 0f;
            float startAr = arCanvasGroup != null ? arCanvasGroup.alpha : 0f;

            // Enable/disable cameras immediately.
            if (mainCamera != null) mainCamera.depth = mode == ViewMode.Map ? 0 : -1;
            if (arCamera != null)
            {
                arCamera.gameObject.SetActive(mode == ViewMode.AR);
                if (mode == ViewMode.AR) arCamera.depth = 0;
            }

            // Drive AR Foundation lifecycle via the seam (phase 9). Resolved reflectively so
            // this compiles without the AR assembly.
            object arController = ServiceLocator.GetByInterfaceName("LightRunners.AR.IARViewController");
            if (arController != null)
            {
                if (mode == ViewMode.AR) InvokeNoArg(arController, "EnterAR");
                else InvokeNoArg(arController, "ExitAR");
            }

            float t = 0f;
            while (t < transitionDuration)
            {
                t += Time.unscaledDeltaTime;
                float k = Smoothstep(0f, 1f, t / transitionDuration);
                if (mapCanvasGroup != null) mapCanvasGroup.alpha = Mathf.Lerp(startMap, targetMapAlpha, k);
                if (arCanvasGroup != null) arCanvasGroup.alpha = Mathf.Lerp(startAr, targetArAlpha, k);
                yield return null;
            }
            if (mapCanvasGroup != null) mapCanvasGroup.alpha = targetMapAlpha;
            if (arCanvasGroup != null) arCanvasGroup.alpha = targetArAlpha;
            _routine = null;
        }

        private static float Smoothstep(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        private static void InvokeNoArg(object target, string method)
        {
            try { target?.GetType().GetMethod(method)?.Invoke(target, null); }
            catch (System.Exception e) { Debug.LogWarning($"[ViewTransitionManager] {method} failed: {e.Message}"); }
        }
    }
}
