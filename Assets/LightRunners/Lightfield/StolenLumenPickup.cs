using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Lightfield
{
    /// <summary>
    /// A stealable Lumen pickup dropped where a runner crashed (decision F). For the ground-only
    /// milestone (decision S) we spawn it heuristically: subscribe to
    /// <see cref="GameEvents.LumensChanged"/> and, when a runner's total drops (negative delta),
    /// spawn a pickup at that runner's last-known position. Lifetime is
    /// <c>GameConfig.Active.stolenLumenPickupSeconds</c>; on collection by another runner we
    /// award via <see cref="GameEvents.RaiseGateCollected"/> with a synthetic gate id in a
    /// reserved range so the existing award pipeline (Track A's <c>ILumenScoreboard</c>) gives
    /// the collector +1 Lumen.
    ///
    /// ─── StolenLumenPickup contract decision (B5) ─────────────────────────────────
    /// Award path: <see cref="GameEvents.RaiseGateCollected"/> with a synthetic id in the range
    /// [<see cref="SyntheticGateIdBase"/>, <see cref="SyntheticGateIdBase"/> + N). Track A's
    /// scoreboard already awards +1 Lumen per GateCollected — so a StolenLumen pickup re-uses
    /// that path with no new event. Track A and Track D please note:
    ///   • Track A: any Lumen award triggered by GateCollected must accept ids in the synthetic
    ///     range without trying to despawn a real Gate (no such LumenGate exists). Recommended:
    ///     resolve <c>IGateDirector</c> via ServiceLocator and let it ignore unknown ids —
    ///     <see cref="GateSpawner"/> already no-ops on ids it doesn't own.
    ///   • Track D: this MonoBehaviour lives in <c>LightRunners.Lightfield</c>; instantiate it
    ///     from a Resources prefab or via <see cref="CreateInstance"/> at the crash site. The
    ///     runner collider must expose <see cref="IRunnerIdentity"/> (see LumenGate) and be
    ///     tagged <c>LumenGate.RunnerTag</c> for OnTriggerEnter to fire.
    ///
    /// ─── Heuristic caveat (decision S) ───────────────────────────────────────────
    /// Subscribing to <c>LumensChanged</c> deltas cannot tell apart "crash drop" from
    /// "referee deduction" or "scoreboard reconciliation". The cleaner contract is for Track A
    /// to expose the authoritative dropped-Lumen queue (see <see cref="StolenLumenRecord"/>) on
    /// its <c>ILumenScoreboard</c> implementation; that swap is a tracked follow-up, not a
    /// milestone blocker.
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    public class StolenLumenPickup : MonoBehaviour
    {
        /// <summary>
        /// Synthetic gate ids for stolen-Lumen pickups start here — well above
        /// <see cref="GateSpawner.BonusGateIdBase"/> so they cannot collide with density or
        /// bonus ids. Track A must accept (and ignore for despawn) ids in this range.
        /// </summary>
        public const int SyntheticGateIdBase = 2_000_000;

        /// <summary>How many Lumens this pickup grants to its collector (decision F: 1 per pickup).</summary>
        public const int LumensPerPickup = 1;

        private static int _nextSyntheticId = SyntheticGateIdBase;

        private GeoPoint _dropSite;
        private string _droppingPlayerId;
        private float _remainingLifetime;
        private bool _collected;
        private float _collectionRadius;

        /// <summary>
        /// Lazily-instantiated prefab-free factory. Creates a <see cref="GameObject"/> with a
        /// <see cref="StolenLumenPickup"/> + trigger sphere + glow at the world position for
        /// <paramref name="dropSite"/>. Caller (Track D's crash handler, or this class's
        /// heuristic subscriber) decides when to call this.
        /// </summary>
        public static StolenLumenPickup CreateInstance(GeoPoint dropSite, string droppingPlayerId)
        {
            var go = new GameObject($"StolenLumen_{droppingPlayerId}");
            var pickup = go.AddComponent<StolenLumenPickup>();
            pickup.Initialize(dropSite, droppingPlayerId);
            return pickup;
        }

        /// <summary>Configure drop site + owner. Called by <see cref="CreateInstance"/> or a prefab spawner.</summary>
        public void Initialize(GeoPoint dropSite, string droppingPlayerId)
        {
            _dropSite = dropSite;
            _droppingPlayerId = droppingPlayerId ?? string.Empty;
            _remainingLifetime = GameConfig.Active.stolenLumenPickupSeconds;
            _collectionRadius = Mathf.Max(0.5f, GameConfig.Active.gateCollectionRadius * 0.75f);

            CoordinateConverter.EnsureReference(dropSite);
            transform.position = CoordinateConverter.GeoToWorld(dropSite);

            EnsureRig();
        }

        private void EnsureRig()
        {
            var trigger = GetComponent<SphereCollider>();
            if (trigger == null) trigger = gameObject.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = _collectionRadius;

            // Tiny emissive orb visual. Zero-art, mirrors BeaconController/LumenGate.
            var orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var orbCol = orb.GetComponent<Collider>();
            if (orbCol != null) Destroy(orbCol);
            orb.transform.SetParent(transform, false);
            orb.transform.localScale = Vector3.one * _collectionRadius * 0.6f;
            orb.GetComponent<MeshRenderer>().material = MakeGlowMaterial(new Color(1f, 0.55f, 0.1f));
        }

        private void Update()
        {
            if (_collected) return;
            _remainingLifetime -= Time.deltaTime;
            if (_remainingLifetime <= 0f)
            {
                // Lifetime expired without a thief — despawn silently. Decision F.
                Destroy(gameObject);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_collected || other == null) return;
            if (!other.CompareTag(LumenGate.RunnerTag) && other.GetComponentInParent<IRunnerIdentity>() == null)
                return;

            string collector = other.GetComponentInParent<IRunnerIdentity>()?.PlayerId;
            if (string.IsNullOrEmpty(collector))
            {
                Debug.LogWarning($"[StolenLumenPickup] Triggered by {other.name} but no {nameof(IRunnerIdentity)}; ignoring (Track D: wire IRunnerIdentity on runner prefabs).");
                return;
            }

            // Don't let the dropping runner re-collect their own Lumens instantly.
            if (collector == _droppingPlayerId)
                return;

            _collected = true;
            int syntheticId = System.Threading.Interlocked.Increment(ref _nextSyntheticId) - 1;
            // Re-use the canonical award path: Track A's scoreboard awards +1 Lumen per
            // GateCollected. The synthetic id is above any real gate's range, so
            // GateSpawner.CollectGate safely no-ops.
            GameEvents.RaiseGateCollected(syntheticId, collector);
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            // Nothing to unsubscribe here — the static GameEvents subscription lifetime is
            // managed by the dedicated spawner component (StolenLumenPickupSpawner) so this
            // MonoBehaviour stays inert on its own.
        }

        private static Material MakeGlowMaterial(Color c)
        {
            Shader s = Shader.Find("LightRunners/BeaconGlow");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("Standard");
            var m = new Material(s) { name = "StolenLumenGlow_runtime" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * GameConfig.Active.beaconGlowIntensity);
            }
            return m;
        }
    }

    /// <summary>
    /// Optional host-side singleton that bridges <see cref="GameEvents.LumensChanged"/> (Track A's
    /// scoreboard) to <see cref="StolenLumenPickup"/> spawns. Attach once per match; enable in
    /// <see cref="MatchState.Live"/>.
    ///
    /// Round-1 review fix R1-F2/R2-F3: previously this spawner observed
    /// <c>GameEvents.LumensChanged</c> negative deltas as a heuristic, was never placed in any
    /// scene, and the authoritative <c>StolenLumenQueue</c> on <c>ILumenScoreboard</c> was never
    /// drained. It now implements <see cref="IStolenLumenSpawner"/> and drains the queue directly
    /// (which carries the crash GeoPoint + lifetime) when <see cref="DrainAndSpawn"/> is called
    /// by <c>MatchManager.HandlePlayerCrash</c>. The LumensChanged heuristic subscription is kept
    /// as a fallback for any drops that bypass the queue. Decision F, decision S.
    /// </summary>
    public sealed class StolenLumenPickupSpawner : MonoBehaviour, IStolenLumenSpawner
    {
        [Tooltip("Track each runner's last known position so we can spawn a pickup at the crash site for drops that arrive via the LumensChanged heuristic path.")]
        [SerializeField] private bool _enabled = true;

        private readonly System.Collections.Generic.Dictionary<string, GeoPoint> _lastPositions =
            new System.Collections.Generic.Dictionary<string, GeoPoint>();

        private void OnEnable()
        {
            GameEvents.LumensChanged += OnLumensChanged;
            // Register self as the IStolenLumenSpawner (overwrites NullStolenLumenSpawner).
            // Round-1 review fix: nothing previously registered the real spawner.
            ServiceLocator.Register<IStolenLumenSpawner>(this);
        }
        private void OnDisable()
        {
            GameEvents.LumensChanged -= OnLumensChanged;
            // Only unregister if we still own the slot (another instance may have registered).
            if (ServiceLocator.Get<IStolenLumenSpawner>() == this)
                ServiceLocator.Unregister<IStolenLumenSpawner>();
        }

        /// <summary>Feed the latest geo position for a runner (called by the position pipeline per tick).</summary>
        public void UpdatePlayerPosition(string playerId, GeoPoint at)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            _lastPositions[playerId] = at;
        }

        /// <summary>
        /// Drain the authoritative dropped-Lumen queue on the registered <c>ILumenScoreboard</c>
        /// and spawn a <see cref="StolenLumenPickup"/> for each record. Called by
        /// <c>MatchManager.HandlePlayerCrash</c>. Each record carries the crash GeoPoint + the
        /// number of Lumens dropped (a single crash may drop 1 or 2 — the queue is per-record,
        /// one record per crash, so we spawn one pickup per record). Round-1 review fix.
        /// </summary>
        public void DrainAndSpawn()
        {
            if (!_enabled) return;
            // Resolve the concrete LumenScoreboard from the locator (it owns the queue).
            var scoreboard = ServiceLocator.Get<ILumenScoreboard>() as LightRunners.Trail.LumenScoreboard;
            if (scoreboard == null) return;
            while (scoreboard.TryDequeueStolenLumen(out var record))
            {
                if (!record.IsValid) continue;
                StolenLumenPickup.CreateInstance(record.At, record.PlayerId);
            }
        }

        private void OnLumensChanged(string playerId, int newTotal)
        {
            if (!_enabled || string.IsNullOrEmpty(playerId)) return;
            if (!_lastPositions.TryGetValue(playerId, out var pos))
            {
                // Heuristic limitation: no last-known position yet → cannot place a pickup.
                // The authoritative path (DrainAndSpawn) reads the crash GeoPoint from the queue
                // and doesn't need this; this is a fallback for any drops that bypass the queue.
                return;
            }

            // Negative delta = Lumens dropped (crash penalty per decision F). newTotal is the
            // authoritative post-drop value; the prior total isn't on this event, so we trigger
            // a single pickup per negative-delta observation. Documented heuristic: this fires
            // once per LumensChanged with a decrease, which is the right granularity for crash
            // penalties (each penalty is a single integer drop).
            if (newTotal < GetCachedTotal(playerId))
            {
                StolenLumenPickup.CreateInstance(pos, playerId);
            }
            _totals[playerId] = newTotal;
        }

        private readonly System.Collections.Generic.Dictionary<string, int> _totals =
            new System.Collections.Generic.Dictionary<string, int>();
        private int GetCachedTotal(string playerId)
            => _totals.TryGetValue(playerId, out var t) ? t : 0;
    }
}
