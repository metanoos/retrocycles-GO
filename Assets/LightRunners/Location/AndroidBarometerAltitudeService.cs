#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Location
{
    /// <summary>
    /// Android barometer altitude (spec §6.2): pressure sensor via JNI (SensorManager,
    /// TYPE_PRESSURE = 6), barometric formula h = 44330·(1 − (p/1013.25)^0.1903), calibrated
    /// against the first GPS altitude, fused by <c>barometerWeight</c>. Falls back to raw GPS
    /// when no sensor is present.
    /// </summary>
    public sealed class AndroidBarometerAltitudeService : IAltitudeService
    {
        private const int TypePressure = 6; // Sensor.TYPE_PRESSURE
        private const double SeaLevelHpa = 1013.25;

        private AndroidJavaObject _sensorManager;
        private AndroidJavaObject _pressureSensor;
        private PressureListener _listener;

        private double _lastPressureHpa = -1;
        private double _calibrationOffset;   // gpsAlt − baroAlt at first fix
        private bool _calibrated;
        private double _lastGpsAlt;

        public bool IsAvailable { get; private set; }
        public bool Calibrated => _calibrated;

        public void Initialize()
        {
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    _sensorManager = activity.Call<AndroidJavaObject>("getSystemService", "sensor");
                    _pressureSensor = _sensorManager?.Call<AndroidJavaObject>("getDefaultSensor", TypePressure);
                }

                if (_pressureSensor == null)
                {
                    Debug.LogWarning("[AndroidBarometer] no pressure sensor — falling back to raw GPS altitude.");
                    IsAvailable = false;
                    return;
                }

                _listener = new PressureListener(hpa => _lastPressureHpa = hpa);
                // SENSOR_DELAY_NORMAL = 3
                _sensorManager.Call<bool>("registerListener", _listener, _pressureSensor, 3);
                IsAvailable = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AndroidBarometer] init failed ({e.Message}) — falling back to GPS.");
                IsAvailable = false;
            }
        }

        public void OnGPSUpdate(double gpsAltitude)
        {
            _lastGpsAlt = gpsAltitude;
            if (!IsAvailable || _lastPressureHpa <= 0) return;

            if (!_calibrated)
            {
                _calibrationOffset = gpsAltitude - BarometricAltitude(_lastPressureHpa);
                _calibrated = true;
            }
        }

        public double GetAltitude(double gpsAltitude)
        {
            _lastGpsAlt = gpsAltitude;
            if (!IsAvailable || _lastPressureHpa <= 0) return gpsAltitude;
            if (!_calibrated) OnGPSUpdate(gpsAltitude);

            double baroAlt = BarometricAltitude(_lastPressureHpa) + _calibrationOffset;
            double w = GameConfig.Active.barometerWeight;
            return w * baroAlt + (1.0 - w) * gpsAltitude;
        }

        private static double BarometricAltitude(double pressureHpa)
            => 44330.0 * (1.0 - Math.Pow(pressureHpa / SeaLevelHpa, 0.1903));

        public void Dispose()
        {
            try
            {
                if (_sensorManager != null && _listener != null)
                    _sensorManager.Call("unregisterListener", _listener);
            }
            catch (Exception) { /* teardown best-effort */ }
            _pressureSensor?.Dispose();
            _sensorManager?.Dispose();
        }

        /// <summary>JNI proxy for android.hardware.SensorEventListener.</summary>
        private sealed class PressureListener : AndroidJavaProxy
        {
            private readonly Action<double> _onPressure;

            public PressureListener(Action<double> onPressure)
                : base("android.hardware.SensorEventListener")
            {
                _onPressure = onPressure;
            }

            // SensorEvent.values[0] = pressure in hPa.
            public void onSensorChanged(AndroidJavaObject sensorEvent)
            {
                try
                {
                    var values = sensorEvent.Get<float[]>("values");
                    if (values != null && values.Length > 0) _onPressure(values[0]);
                }
                catch (Exception) { /* drop malformed events */ }
            }

            public void onAccuracyChanged(AndroidJavaObject sensor, int accuracy) { }
        }
    }
}
#endif
