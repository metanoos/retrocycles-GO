using System;
using UnityEngine;

namespace LightRunners.Core
{
    /// <summary>
    /// Single ScriptableObject that drives every tunable. Spec §4.4. Loaded from
    /// <c>Resources/GameConfig.asset</c>; the editor bootstrap (see Editor/LightRunnersProjectBootstrap)
    /// creates a default asset if none exists. Secrets (Supabase url/anon key) are blank
    /// in the committed asset and filled per-environment (spec §17.10).
    /// </summary>
    [CreateAssetMenu(fileName = "GameConfig", menuName = "Light Runners/Game Config", order = 0)]
    public class GameConfig : ScriptableObject
    {
        [Header("Location (§6)")]
        [Min(0.01f)] public float trailPointMinDistance = 1.0f;     // m
        [Min(0.05f)] public float gpsSampleInterval = 0.5f;         // s
        [Range(2f, 200f)] public float gpsAccuracyThreshold = 20f;  // m
        [Range(0f, 1f)] public float barometerWeight = 0.7f;

        [Header("Trail (§7)")]
        [Min(16)] public int maxTrailPoints = 5000;
        [Min(0.05f)] public float trailWidth = 0.5f;
        [Min(0f)] public float trailGracePeriod = 2f;       // s — newest-N self-collision skip
        [Min(0f)] public float trailGroundOffset = 0.3f;    // m

        [Header("Collision (§7.3)")]
        [Min(0.5f)] public float collisionCheckRadius = 5f;        // m
        [Min(0.1f)] public float collisionThreshold = 1.5f;        // m
        [Range(2, 64)] public int selfCollisionSkipPoints = 10;

        [Header("Networking (§8) — FUSION_WEAVER")]
        public string fusionAppVersion = "1.0";
        [Range(2, 64)] public int maxPlayersPerRoom = 20;
        [Range(10, 60)] public int networkTickRate = 30;
        [Range(1, 16)] public int trailSyncBatchSize = 10;
        [Min(1f)] public float connectTimeoutSeconds = 8f;  // §2.3 Starting window
        [Range(0, 8)] public int roomJoinRetryLimit = 3;    // §8.1 room-full overflow

        [Header("Friend Match (§8.5)")]
        [Range(4, 12)] public int lobbyCodeLength = 6;
        [Range(2, 32)] public int lobbyMaxMembers = 8;
        public string lobbyCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        [Min(60)] public int lobbyIdleTimeoutSeconds = 1800;
        [Min(0.01f)] public float lobbyRegionCell = 0.1f;
        [Min(1f)] public float lobbyPollIntervalSeconds = 5f;

        [Header("Backend (§12) — secrets per-environment")]
        public string supabaseUrl = "";
        [TextArea] public string supabaseAnonKey = "";
        [Min(1f)] public float trailSaveInterval = 5f;     // s

        [Header("Scoring (§7.4)")]
        [Min(1f)] public float proximitySampleInterval = 10f; // s
        [Min(5f)] public float proximityRadius = 100f;        // m

        [Header("Lifecycle (§20)")]
        [Min(5f)] public float backgroundGraceSeconds = 60f;

        [Header("Map / OSM (§10)")]
        public string osmTileUserAgent = "LightRunners/1.0";
        [Range(64, 1024)] public int osmMinimapSize = 200;
        [Range(8, 22)] public int osmDefaultZoom = 16;
        [Range(1, 8)] public int osmMaxConcurrentRequests = 2;
        [Min(0.1f)] public float osmTileRequestInterval = 1f;
        [Range(8, 512)] public int osmTileCacheSize = 64;
        public double defaultLatitude = 37.7749;
        public double defaultLongitude = -122.4194;

        [Header("AR (§11) — UNITY_XR_ARFOUNDATION")]
        [Min(5f)] public float arTrailRenderDistance = 50f;
        [Range(1, 200)] public int arMaxNearbyTrails = 50;

        [Header("Beacon (§9)")]
        [Min(0.05f)] public float beaconBaseScale = 1f;
        [Min(0f)] public float beaconBobAmplitude = 0.1f;
        [Min(0f)] public float beaconBobFrequency = 2f;
        [Min(0f)] public float beaconGlowIntensity = 2f;
        [Min(0f)] public float beaconRotationSpeed = 45f;

        /// <summary>Cached lazy load of <c>Resources/GameConfig.asset</c>.</summary>
        private static GameConfig _cached;

        /// <summary>
        /// Load the active config. The editor bootstrap guarantees the asset exists; if it
        /// somehow doesn't, we fall back to a transient in-memory instance with default values
        /// rather than null-flooding every call site.
        /// </summary>
        public static GameConfig Active
        {
            get
            {
                if (_cached != null) return _cached;
                _cached = Resources.Load<GameConfig>("GameConfig");
                if (_cached == null)
                {
                    Debug.LogWarning("[GameConfig] Resources/GameConfig.asset not found — using in-memory defaults. Run 'Light-Runners > Setup > Ensure GameConfig Asset'.");
                    _cached = CreateInstance<GameConfig>();
                    _cached.name = "GameConfig (in-memory)";
                }
                return _cached;
            }
        }

        /// <summary>Editor-only: clear the cache so a freshly-created asset is picked up.</summary>
        public static void ClearCache()
        {
            _cached = null;
        }
    }
}
