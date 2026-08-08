using System;
using System.Reflection;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Location
{
    /// <summary>
    /// Owns the device GPS and emits <see cref="OnPositionUpdated"/> — the single source of
    /// truth for "where the player is." Trail recording, the map, and AR all subscribe.
    /// Spec §6.1.
    ///
    /// On device: throttles sampling, rejects stale / low-accuracy fixes, feeds GPS altitude
    /// into the platform altitude service and emits a fused point.
    /// In editor: no GPS hardware exists, so it runs the **simulated-walk mode** (WASD/arrows
    /// move, Q/E turn, Shift sprints) which drives <see cref="OnPositionUpdated"/> exactly
    /// like real GPS. This simulator is mandatory for testing the whole loop without a device.
    /// </summary>
    public class LocationProvider : Singleton<LocationProvider>
    {
        /// <summary>Raised whenever a new (already-filtered) position is available.</summary>
        public event Action<GeoPoint> OnPositionUpdated;

        public bool GPSActive { get; private set; }
        public bool IsInitialized { get; private set; }
        public GeoPoint CurrentPosition { get; private set; }

        public IAltitudeService AltitudeService { get; private set; }

        [Header("Editor Sim")]
        [SerializeField] private double simStartLat = 37.7749;
        [SerializeField] private double simStartLon = -122.4194;
        [SerializeField] private double simStartAlt = 5.0;
        [SerializeField] private float simWalkSpeed = 1.4f;   // m/s, brisk walk
        [SerializeField] private float simSprintMultiplier = 3f;
        [SerializeField] private float simTurnSpeed = 90f;    // deg/s

        private float _sampleTimer;
        private double _lastEmitTimestampSeconds = -1.0;

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────
        protected virtual void Start()
        {
            AltitudeService = AltitudeServiceFactory.Create();
            AltitudeService?.Initialize();
            Initialize();
        }

        protected override void OnDestroy()
        {
            AltitudeService?.Dispose();
#if !UNITY_EDITOR
            if (GPSActive) Input.location.Stop();
#endif
            base.OnDestroy();
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            GameConfig cfg = GameConfig.Active;

#if UNITY_EDITOR
            StartEditorSim(cfg);
#else
            StartDeviceGPS(cfg);
#endif
            IsInitialized = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Editor simulated-walk mode
        // ─────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR
        private double _simLat, _simLon, _simAlt;
        private float _simHeading; // degrees, 0=N CW

        private void StartEditorSim(GameConfig cfg)
        {
            _simLat = simStartLat;
            _simLon = simStartLon;
            _simAlt = simStartAlt;
            _simHeading = 0f;

            // Seed the world reference + a starting position so subscribers that read
            // CurrentPosition immediately have something sensible.
            var start = new GeoPoint(_simLat, _simLon, _simAlt);
            CurrentPosition = start;
            CoordinateConverter.EnsureReference(start);
            GPSActive = true;
            Debug.Log("[LocationProvider] Editor simulated-walk mode active. WASD/arrows move, Q/E turn, Shift sprints.");
        }

        private void UpdateEditorSim(float dt, GameConfig cfg)
        {
            // Editor simulated-walk input. We read both the legacy Input class AND the new
            // InputSystem's Keyboard.current, because in "Both" backend mode the legacy poll
            // silently returns zero when an EventSystem/InputModule is competing for the focus
            // — observed dead with com.unity.inputsystem 1.19 + activeInputHandler=2.
            bool q = IsKeyDown(KeyCode.Q), e = IsKeyDown(KeyCode.E);
            bool w = IsKeyDown(KeyCode.W) || IsKeyDown(KeyCode.UpArrow);
            bool s = IsKeyDown(KeyCode.S) || IsKeyDown(KeyCode.DownArrow);
            bool a = IsKeyDown(KeyCode.A) || IsKeyDown(KeyCode.LeftArrow);
            bool d = IsKeyDown(KeyCode.D) || IsKeyDown(KeyCode.RightArrow);
            bool shift = IsKeyDown(KeyCode.LeftShift) || IsKeyDown(KeyCode.RightShift);

            // Heading (Q/E turn).
            if (q) _simHeading -= simTurnSpeed * dt;
            if (e) _simHeading += simTurnSpeed * dt;

            // Movement vector in local metres (north = +Z, east = +X). Bearing 0 = N, CW.
            float forward = 0f, strafe = 0f;
            if (w) forward += 1f;
            if (s) forward -= 1f;
            if (a) strafe -= 1f;
            if (d) strafe += 1f;

            float speed = simWalkSpeed * (shift ? simSprintMultiplier : 1f);

            // TEMP DIAGNOSTIC: confirms whether input is reaching the sim and how far each tick moves.
            Debug.Log($"[LocationProvider] sim tick: fwd={forward} strfe={strafe} heading={_simHeading} speed={speed} dt={dt} step={speed*dt}m");

            if (forward != 0f || strafe != 0f)
            {
                float headingRad = _simHeading * Mathf.Deg2Rad;
                float sin = Mathf.Sin(headingRad);
                float cos = Mathf.Cos(headingRad);
                // Forward (north) vector decomposed into (east, north) components.
                float east = (forward * sin + strafe * cos) * speed * dt;
                float north = (forward * cos - strafe * sin) * speed * dt;

                // Convert metre delta back to lat/lon delta using equirectangular scaling.
                double metersPerDegLat = Math.PI * CoordinateConverter.EarthRadiusMeters / 180.0;
                double metersPerDegLon = metersPerDegLat * Math.Cos(_simLat * Math.PI / 180.0);
                _simLat += north / metersPerDegLat;
                _simLon += east / metersPerDegLon;
            }

            var pt = new GeoPoint(_simLat, _simLon, _simAlt);
            EmitPoint(pt, cfg);
        }

        /// <summary>
        /// Editor-only key check that survives the Input System "Both" backend mode, where
        /// legacy <c>Input.GetKey</c> can silently return false when an EventSystem competes
        /// for keyboard focus. Falls back through reflection to <c>Keyboard.current</c> if the
        /// InputSystem assembly is present, so a missing asmdef reference can't break the build.
        /// </summary>
        private static bool IsKeyDown(KeyCode key)
        {
            if (Input.GetKey(key)) return true;
            return IsInputSystemKeyDown(key);
        }

        private static readonly Type _keyboardType =
            Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");

        private static bool IsInputSystemKeyDown(KeyCode key)
        {
            if (_keyboardType == null) return false;
            try
            {
                var currentProp = _keyboardType.GetProperty("current",
                    BindingFlags.Public | BindingFlags.Static);
                object keyboard = currentProp?.GetValue(null);
                if (keyboard == null) return false;

                // Indexer this[Key] — convert KeyCode → InputSystem Key by name.
                var keyType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
                if (keyType == null) return false;
                if (!Enum.IsDefined(keyType, key.ToString())) return false;
                object inputKey = Enum.Parse(keyType, key.ToString());

                var indexer = _keyboardType.GetProperty("Item", new[] { keyType });
                object keyControl = indexer?.GetValue(keyboard, new[] { inputKey });
                if (keyControl == null) return false;
                var isPressedProp = keyControl.GetType().GetProperty("isPressed");
                return isPressedProp != null && (bool)isPressedProp.GetValue(keyControl)!;
            }
            catch { return false; }
        }
#endif

        // ─────────────────────────────────────────────────────────────────────
        // Device GPS
        // ─────────────────────────────────────────────────────────────────────
#if !UNITY_EDITOR
        private void StartDeviceGPS(GameConfig cfg)
        {
            GPSActive = false;

            if (!Input.location.isEnabledByUser)
            {
                Debug.LogWarning("[LocationProvider] GPS is disabled by the user. Location updates will not fire.");
                return;
            }

#if UNITY_ANDROID
            // Fine-location permission must be requested before Start(). On Android we ask here.
            if (!HasAndroidFineLocationPermission())
                RequestAndroidFineLocationPermission();
#endif

            // 1 m accuracy, 0 m update distance — we throttle and filter ourselves (spec §6.1).
            Input.location.Start(1f, 0f);

            int maxWaitMs = 20;
            while (Input.location.status == LocationServiceStatus.Initializing && maxWaitMs > 0)
            {
                maxWaitMs--;
                System.Threading.Thread.Sleep(500); // best-effort init wait on the main thread
            }

            if (Input.location.status != LocationServiceStatus.Running)
            {
                Debug.LogWarning($"[LocationProvider] GPS failed to start (status={Input.location.status}).");
                return;
            }

            Input.compass.enabled = true;
            GPSActive = true;
        }

#if UNITY_ANDROID
        private bool HasAndroidFineLocationPermission()
        {
            try
            {
                using (var jc = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = jc.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    int granted = activity.Call<int>("checkSelfPermission", "android.permission.ACCESS_FINE_LOCATION");
                    return granted == 0; // PackageManager.PERMISSION_GRANTED
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LocationProvider] permission check failed ({e.Message}); assuming not granted.");
                return false;
            }
        }

        private void RequestAndroidFineLocationPermission()
        {
            try
            {
                using (var jc = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = jc.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    activity.Call("requestPermissions", new[] { "android.permission.ACCESS_FINE_LOCATION" }, 1001);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LocationProvider] permission request failed: {e.Message}");
            }
        }
#endif

        private void UpdateDeviceGPS(float dt, GameConfig cfg)
        {
            if (!GPSActive || Input.location.status != LocationServiceStatus.Running)
                return;

            LocationInfo data = Input.location.lastData;

            // Reject samples whose timestamp didn't advance.
            double ts = data.timestamp;
            if (ts <= _lastEmitTimestampSeconds) return;

            // Reject samples worse than the configured accuracy threshold.
            if (data.horizontalAccuracy > cfg.gpsAccuracyThreshold && cfg.gpsAccuracyThreshold > 0f) return;

            double fusedAlt = AltitudeService?.GetAltitude(data.altitude) ?? data.altitude;
            var pt = new GeoPoint(data.latitude, data.longitude, fusedAlt);
            EmitPoint(pt, cfg, ts);
        }
#endif

        // ─────────────────────────────────────────────────────────────────────
        // Update / emit
        // ─────────────────────────────────────────────────────────────────────
        private void Update()
        {
            if (!IsInitialized) return;

            GameConfig cfg = GameConfig.Active;
            float sampleInterval = GPSPowerManager.HasInstance ? GPSPowerManager.Instance.CurrentSampleInterval : cfg.gpsSampleInterval;
            _sampleTimer -= Time.deltaTime;
            if (_sampleTimer > 0f) return;
            _sampleTimer = sampleInterval;

#if UNITY_EDITOR
            // Sim advances by the full sample interval per tick (we only poll input here, not
            // every frame), so the simulated walk reaches the configured simWalkSpeed. Passing
            // Time.deltaTime would make the sim ~30x too slow at the default 0.5s sample.
            UpdateEditorSim(sampleInterval, cfg);
#else
            UpdateDeviceGPS(Time.deltaTime, cfg);
#endif
        }

        /// <summary>
        /// Apply the move-threshold filter (spec §6.1: "only emit if moved ≥ trailPointMinDistance
        /// from the last") and dispatch.
        /// </summary>
        private void EmitPoint(GeoPoint pt, GameConfig cfg, double deviceTimestampSeconds = -1)
        {
            // Editor path uses wall-clock seconds; device uses the GPS fix timestamp.
            if (deviceTimestampSeconds < 0)
            {
                deviceTimestampSeconds = Time.timeAsDouble;
            }
            else
            {
                _lastEmitTimestampSeconds = deviceTimestampSeconds;
            }

            // Move-threshold filter (except for the very first emit, which seeds CurrentPosition).
            if (CurrentPosition.latitude != 0.0 || CurrentPosition.longitude != 0.0)
            {
                double moved = CurrentPosition.HorizontalDistanceTo(pt);
                if (moved < cfg.trailPointMinDistance) return;
            }

            // Feed the altitude service with the raw reading (device path already did; on editor
            // we just route the sim alt through it so the filter stays consistent).
            AltitudeService?.OnGPSUpdate(pt.altitude);

            CurrentPosition = pt;
            OnPositionUpdated?.Invoke(pt);
        }

        /// <summary>Editor-only helper: hard-reset the simulated position (used by the scene menu).</summary>
        public void ResetEditorSim(double lat, double lon, double alt)
        {
#if UNITY_EDITOR
            _simLat = lat; _simLon = lon; _simAlt = alt;
            CoordinateConverter.SetReference(lat, lon);
            var pt = new GeoPoint(lat, lon, alt);
            CurrentPosition = pt;
            OnPositionUpdated?.Invoke(pt);
#endif
        }
    }
}
