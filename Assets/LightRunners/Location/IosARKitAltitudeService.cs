#if UNITY_IOS && !UNITY_EDITOR
using System;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Location
{
    /// <summary>
    /// iOS altitude (spec §6.2): EMA-smoothed GPS altitude, plus ARKit relative-height
    /// corrections applied while an AR session is active. The AR correction arrives via
    /// <see cref="SetARHeightCorrection"/> (pushed by ARViewManager's plane baseline, §11.2)
    /// so this class needs no AR Foundation types of its own.
    /// </summary>
    public sealed class IosARKitAltitudeService : IAltitudeService
    {
        private const double EmaAlpha = 0.15;

        private double _ema;
        private bool _initialized;
        private double _arCorrection;
        private bool _arCorrectionActive;

        public bool IsAvailable => true;
        public bool Calibrated => _initialized;

        public void Initialize() { }

        public void OnGPSUpdate(double gpsAltitude)
        {
            if (!_initialized)
            {
                _ema = gpsAltitude;
                _initialized = true;
                return;
            }
            _ema += EmaAlpha * (gpsAltitude - _ema);
        }

        public double GetAltitude(double gpsAltitude)
        {
            OnGPSUpdate(gpsAltitude);
            return _arCorrectionActive ? _ema + _arCorrection : _ema;
        }

        /// <summary>ARKit relative-height correction while a session runs; NaN disables it.</summary>
        public void SetARHeightCorrection(double correction)
        {
            _arCorrectionActive = !double.IsNaN(correction);
            if (_arCorrectionActive) _arCorrection = correction;
        }

        public void Dispose() { }
    }
}
#endif
