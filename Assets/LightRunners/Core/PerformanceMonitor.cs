using UnityEngine;

namespace LightRunners.Core
{
    /// <summary>
    /// Lightweight adaptive-quality monitor. Sets a target frame rate and exposes a
    /// particle-reduction flag for low-fps situations. Spec §4.4 (referenced by BeaconEffects)
    /// and §14.2 (sits as a top-level Game-scene object).
    /// </summary>
    public class PerformanceMonitor : Singleton<PerformanceMonitor>
    {
        [SerializeField] private int targetFrameRate = 60;
        [SerializeField, Range(20, 60)] private int lowFpsThreshold = 30;
        [SerializeField, Range(0.5f, 5f)] private float sampleInterval = 1f;

        /// <summary>True when sustained frame rate is below threshold; consumers should cut particle counts.</summary>
        public bool ReduceParticles { get; private set; }

        private float _sampleTimer;
        private float _smoothedFps;

        protected virtual void Start()
        {
            Application.targetFrameRate = targetFrameRate;
            QualitySettings.vSyncCount = 0;
        }

        private void Update()
        {
            _sampleTimer -= Time.unscaledDeltaTime;
            if (_sampleTimer > 0f) return;

            float instFps = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            _smoothedFps = _smoothedFps <= 0f ? instFps : Mathf.Lerp(_smoothedFps, instFps, 0.3f);
            ReduceParticles = _smoothedFps < lowFpsThreshold;
            _sampleTimer = sampleInterval;
        }
    }
}
