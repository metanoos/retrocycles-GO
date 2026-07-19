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

        [Header("Lightfield Match — active decisions 2026-07-18")]
        [Tooltip("Decimal gate-to-player density. activeGateCount = max(1, ceil(playerCount × this)). Decision M, default 0.5.")]
        [Range(0f, 4f)] public float gatesPerPlayer = 0.5f;
        [Tooltip("Lumen Gate collection trigger radius (m). Decision G — host-tunable.")]
        [Min(0.1f)] public float gateCollectionRadius = 2.0f;
        [Tooltip("Authoritative tail radius (m). Frozen at countdown (decision T); all head-to-trail collision derives from this.")]
        [Min(0.05f)] public float tailRadius = 0.5f;
        [Tooltip("Match clock (s). Host-tunable; default 6 minutes. Decision O.")]
        [Min(10f)] public float matchDurationSeconds = 360f;
        [Tooltip("Pre-live countdown (s). Tail radius freezes on entry to Countdown (decision T).")]
        [Min(1f)] public float matchCountdownSeconds = 3f;
        [Tooltip("Lumens dropped by a non-leader on crash. Decision F.")]
        [Min(0)] public int crashLumenLossNonLeader = 1;
        [Tooltip("Lumens dropped by the current leader on crash. Decision F.")]
        [Min(0)] public int crashLumenLossLeader = 2;
        [Tooltip("Lifetime of a stealable Lumen pickup dropped on crash (s). Decision F.")]
        [Min(1f)] public float stolenLumenPickupSeconds = 8f;
        [Tooltip("Grace after run start during which a runner's own trail cannot kill it (decision D). Extends trailGracePeriod.")]
        [Min(0f)] public float emergenceGraceSeconds = 2f;
        [Tooltip("Ground-disc radius of the Lightfield (m). Decision K — ground-only milestone; full dome deferred (decision S).")]
        [Min(5f)] public float lightfieldBaseRadiusMeters = 50f;
        [Tooltip("Hard altitude ceiling stub for the dome (m). Decision K — aerial milestone (decision S) replaces with true hemisphere.")]
        [Min(1f)] public float lightfieldDomeCeilingMeters = 6f;
        [Tooltip("Max single-segment length (m) for collision sweep subdivision. Decision N — long teleports/vehicle moves are tested segment-by-segment.")]
        [Min(0.5f)] public float sweepSubdivideMaxStepMeters = 2f;
        [Tooltip("Host-side HMAC secret for referee-token issuance (decision R). Blank by default — a host with no configured secret fails CLOSED on referee-token validation (PlaceBonusGate rejects) so a misconfigured host can't mint tokens. Fill per-environment; do NOT commit a real secret (spec pitfall #10).")]
        public string refereeTokenSecret = "";

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
