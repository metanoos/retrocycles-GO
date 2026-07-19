using System;

namespace LightRunners.Location
{
    /// <summary>
    /// Altitude fusion seam (spec §6.2). GPS altitude is noisy (~5 m RMS); each platform
    /// blends it with a better source (barometer on Android, ARKit on iOS, a 1-D Kalman
    /// filter everywhere else). Implementations are chosen by <see cref="AltitudeServiceFactory"/>.
    /// </summary>
    public interface IAltitudeService : IDisposable
    {
        void Initialize();
        /// <summary>Feed the latest raw GPS altitude each fix.</summary>
        void OnGPSUpdate(double gpsAltitude);
        /// <summary>Get the current fused altitude, given the latest raw GPS reading.</summary>
        double GetAltitude(double gpsAltitude);
        bool IsAvailable { get; }
        bool Calibrated { get; }
    }
}
