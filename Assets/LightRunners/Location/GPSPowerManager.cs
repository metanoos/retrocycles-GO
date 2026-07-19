using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Location
{
    /// <summary>
    /// Battery-aware GPS sampling interval (spec §6.3). Triples <see cref="baseInterval"/>
    /// below 15% battery (non-charging), doubles it while backgrounded, restores on recovery.
    /// Exposes <see cref="CurrentSampleInterval"/>, consumed by <see cref="LocationProvider"/>.
    /// </summary>
    public class GPSPowerManager : Singleton<GPSPowerManager>
    {
        [SerializeField, Range(0.05f, 5f)] private float baseInterval = 0.5f;
        [SerializeField, Range(0f, 1f)] private float lowBatteryThreshold = 0.15f;

        public float CurrentSampleInterval { get; private set; } = 0.5f;

        private bool _isBackgrounded;

        protected virtual void Start()
        {
            CurrentSampleInterval = baseInterval;
        }

        private void Update()
        {
            float mult = 1f;

#if UNITY_IOS || UNITY_ANDROID
            // Unity exposes battery level/status on device. In editor it returns -1 (unknown),
            // in which case we don't penalize sampling.
            float level = SystemInfo.batteryLevel;
            BatteryStatus status = SystemInfo.batteryStatus;
            if (level >= 0f && level <= lowBatteryThreshold && status != BatteryStatus.Charging)
                mult *= 3f;
#endif
            if (_isBackgrounded) mult *= 2f;

            CurrentSampleInterval = baseInterval * mult;
        }

        private void OnApplicationPause(bool paused) => _isBackgrounded = paused;
        private void OnApplicationFocus(bool focused) => _isBackgrounded = !focused;
    }
}
