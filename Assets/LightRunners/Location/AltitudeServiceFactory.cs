using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Location
{
    /// <summary>
    /// The single place game code branches on <c>Application.platform</c> (spec §6.2).
    /// Android → barometer (phase 11), iOS → ARKit (phase 11), everything else →
    /// <see cref="FallbackGPSAltitudeService"/>. Platform-specific services are gated with
    /// #if so this file compiles without them; the fallback is always available.
    /// </summary>
    public static class AltitudeServiceFactory
    {
        public static IAltitudeService Create()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return CreateAndroidBarometer();
#elif UNITY_IOS && !UNITY_EDITOR
            return CreateIosARKit();
#else
            return new FallbackGPSAltitudeService();
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static IAltitudeService CreateAndroidBarometer()
        {
            try
            {
                return new AndroidBarometerAltitudeService();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[AltitudeServiceFactory] Android barometer unavailable, falling back to GPS Kalman: {e.Message}");
                return new FallbackGPSAltitudeService();
            }
        }
#endif

#if UNITY_IOS && !UNITY_EDITOR
        private static IAltitudeService CreateIosARKit()
        {
            try
            {
                return new IosARKitAltitudeService();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[AltitudeServiceFactory] iOS ARKit altitude unavailable, falling back to GPS Kalman: {e.Message}");
                return new FallbackGPSAltitudeService();
            }
        }
#endif
    }
}
