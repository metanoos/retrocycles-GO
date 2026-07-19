using System.Collections;
using UnityEngine;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Plays the crash sequence (spec §7.6): slow-mo + a coloured full-screen flash, then
    /// restores <c>Time.timeScale</c>. Always resets <c>timeScale</c> in <see cref="OnDestroy"/>
    /// so a paused editor can never leave the game stuck in slow motion.
    ///
    /// When the <c>LightRunners/ScreenCrashFlash</c> shader (spec §13) is present, the flash
    /// drives its uniforms (_FlashIntensity, _Distortion, _VignetteIntensity, _FlashColor)
    /// on a material applied to the overlay Image; otherwise it animates plain Image alpha.
    /// A serialized <see cref="crashClip"/> slot is the game's one audio hook (spec §24) —
    /// silent when empty.
    /// </summary>
    public class CrashSequence : MonoBehaviour
    {
        [SerializeField] private float slowMotionScale = 0.2f;
        [SerializeField] private float slowMotionDuration = 0.8f;
        [SerializeField] private float flashDuration = 0.3f;
        [SerializeField] private UnityEngine.UI.Image flashOverlay;
        [SerializeField] private AudioClip crashClip;

        private Coroutine _routine;
        private Material _flashMaterial;
        private bool _triedShader;

        public bool IsPlaying => _routine != null;

        public void Play(Color flashColor)
        {
            if (_routine != null) StopCoroutine(_routine);
            _routine = StartCoroutine(CoPlay(flashColor));

            if (crashClip != null)
                AudioSource.PlayClipAtPoint(crashClip, Camera.main != null ? Camera.main.transform.position : Vector3.zero);
        }

        private Material ResolveFlashMaterial()
        {
            if (_triedShader) return _flashMaterial;
            _triedShader = true;
            Shader s = Shader.Find("LightRunners/ScreenCrashFlash");
            if (s != null)
            {
                _flashMaterial = new Material(s) { name = "ScreenCrashFlash_runtime" };
                if (flashOverlay != null) flashOverlay.material = _flashMaterial;
            }
            return _flashMaterial;
        }

        private IEnumerator CoPlay(Color flashColor)
        {
            float originalScale = Time.timeScale;
            Time.timeScale = slowMotionScale;

            var mat = ResolveFlashMaterial();

            if (flashOverlay != null)
            {
                flashOverlay.gameObject.SetActive(true);

                if (mat != null)
                {
                    // Shader path (spec §7.6): intensity 1 → 0 over flashDuration.
                    flashOverlay.color = Color.white;
                    mat.SetColor("_FlashColor", flashColor);
                    mat.SetFloat("_Distortion", 0.03f);
                    mat.SetFloat("_VignetteIntensity", 0.9f);

                    float t = 0f;
                    while (t < flashDuration)
                    {
                        t += Time.unscaledDeltaTime;
                        mat.SetFloat("_FlashIntensity", Mathf.Clamp01(1f - t / flashDuration));
                        yield return null;
                    }
                    mat.SetFloat("_FlashIntensity", 0f);
                }
                else
                {
                    // Fallback: plain Image alpha fade.
                    flashOverlay.color = flashColor;
                    float t = 0f;
                    while (t < flashDuration)
                    {
                        t += Time.unscaledDeltaTime;
                        float a = Mathf.Clamp01(1f - (t / flashDuration));
                        var c = flashOverlay.color;
                        c.a = a;
                        flashOverlay.color = c;
                        yield return null;
                    }
                }

                flashOverlay.gameObject.SetActive(false);
            }

            // Hold slow-mo for the remainder of the duration.
            float hold = Mathf.Max(0f, slowMotionDuration - flashDuration);
            if (hold > 0f) yield return new WaitForSecondsRealtime(hold);

            Time.timeScale = originalScale;
            _routine = null;
        }

        private void OnDestroy()
        {
            // Invariant (spec §16): timeScale must always return to 1.
            Time.timeScale = 1f;
            if (_flashMaterial != null) Destroy(_flashMaterial);
        }
    }
}
