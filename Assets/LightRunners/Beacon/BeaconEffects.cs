using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Beacon
{
    /// <summary>
    /// Speed-reactive particle FX (spec §9.3): glow emission scales with speed; speed-lines
    /// appear above a threshold; trail-create pulse; a colored crash explosion. Respects
    /// <see cref="PerformanceMonitor.ReduceParticles"/>.
    /// </summary>
    [RequireComponent(typeof(BeaconController))]
    public class BeaconEffects : MonoBehaviour
    {
        [SerializeField] private float speedLineThreshold = 3.5f; // m/s
        [SerializeField] private float baseEmissionRate = 12f;
        [SerializeField] private float emissionPerMs = 6f;        // extra particles/s per m/s

        private BeaconController _controller;
        private ParticleSystem _speedLines;
        private Vector3 _lastPos;
        private bool _haveLast;
        private float _smoothedSpeed;

        private void Awake()
        {
            _controller = GetComponent<BeaconController>();
        }

        private void Start()
        {
            var go = new GameObject("SpeedLines");
            go.transform.SetParent(transform, false);
            _speedLines = go.AddComponent<ParticleSystem>();
            var main = _speedLines.main;
            main.startSize = 0.05f;
            main.startLifetime = 0.4f;
            main.startSpeed = -6f; // streak backwards
            main.maxParticles = 128;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = _speedLines.emission;
            emission.rateOverTime = 0f;
            var shape = _speedLines.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 8f;
            shape.radius = 0.3f;
        }

        private void Update()
        {
            // Estimate speed from world movement (works for local and remote beacons alike).
            Vector3 pos = transform.position;
            if (_haveLast && Time.deltaTime > 0f)
            {
                float inst = (pos - _lastPos).magnitude / Time.deltaTime;
                _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, inst, 0.2f);
            }
            _lastPos = pos;
            _haveLast = true;

            bool reduce = PerformanceMonitor.HasInstance && PerformanceMonitor.Instance.ReduceParticles;

            if (_speedLines != null)
            {
                var emission = _speedLines.emission;
                bool on = _smoothedSpeed >= speedLineThreshold && !reduce;
                emission.rateOverTime = on ? baseEmissionRate + _smoothedSpeed * emissionPerMs : 0f;
                var main = _speedLines.main;
                main.startColor = _controller.TrailColor;
            }
        }

        /// <summary>Small pulse when a trail point lands (wired by the local beacon driver).</summary>
        public void PlayTrailPulse()
        {
            if (PerformanceMonitor.HasInstance && PerformanceMonitor.Instance.ReduceParticles) return;
            _speedLines?.Emit(4);
        }

        /// <summary>Colored crash explosion (delegates to the controller's burst).</summary>
        public void PlayCrashExplosion() => _controller?.PlayCrashEffect();
    }
}
