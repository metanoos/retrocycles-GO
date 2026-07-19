using LightRunners.Core;

namespace LightRunners.Location
{
    /// <summary>
    /// Editor/Standalone fallback (spec §6.2): a 1-D Kalman filter on raw GPS altitude.
    /// Used whenever a better sensor isn't available.
    /// </summary>
    public sealed class FallbackGPSAltitudeService : IAltitudeService
    {
        private double _x;            // estimated altitude
        private double _p = 1.0;      // estimate uncertainty
        private bool _initialized;

        // Tunables: process noise (q) and measurement noise (r). GPS altitude noise ~5 m RMS.
        private const double Q = 0.1; // m^2 — slow drift assumed
        private const double R = 25.0; // m^2 — 5 m RMS squared

        public bool IsAvailable => true;
        public bool Calibrated => _initialized;

        public void Initialize() { }

        public void OnGPSUpdate(double gpsAltitude)
        {
            // First measurement seeds the filter rather than waiting for convergence.
            if (!_initialized)
            {
                _x = gpsAltitude;
                _initialized = true;
                return;
            }
            // Predict
            _p += Q;
            // Update
            double k = _p / (_p + R);
            _x += k * (gpsAltitude - _x);
            _p = (1.0 - k) * _p;
        }

        public double GetAltitude(double gpsAltitude)
        {
            OnGPSUpdate(gpsAltitude);
            return _x;
        }

        public void Dispose() { }
    }
}
