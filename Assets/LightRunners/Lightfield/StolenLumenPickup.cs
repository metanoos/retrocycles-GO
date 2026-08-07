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
    ///     range. Unlike a presentation Gate, this pickup owns its one-shot accepted state and
    ///     therefore publishes the accepted collection directly; no GateDirector despawn exists.
    ///   • Track D: this MonoBehaviour lives in <c>LightRunners.Lightfield</c>; instantiate it
    ///     from a Resources prefab or via <see cref="CreateInstance"/> at the crash site. The
    ///     runner collider must expose <see cref="IRunnerIdentity"/> (see LumenGate).
    ///
    /// Dropped pickups are driven by the authoritative <see cref="StolenLumenRecord"/> queue;
    /// score reconciliation events never create presentation objects.
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
            if (other.GetComponentInParent<IRunnerIdentity>() == null) return;

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
            // Re-use the canonical accepted-award path. This pickup owns the one-shot
            // _collected guard; the synthetic id is above every real Gate range.
            GameEvents.RaiseGateCollected(syntheticId, collector, _dropSite);
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
    /// by <c>MatchManager.HandlePlayerCrash</c>. Decision F, decision S.
    ///
    /// Round-2 fix R2-F5: the LumensChanged heuristic subscription was DELETED — it caused
    /// double-spawn (LumenScoreboard.ApplyCrashPenalty both enqueues a record AND fires
    /// RaiseLumensChanged, so the heuristic fired in addition to DrainAndSpawn). The queue-drain
    /// path is strictly more correct (it carries the crash GeoPoint; the heuristic used a
    /// last-known position that was never wired). UpdatePlayerPosition is removed for the same
    /// reason — its only caller was the heuristic path.
    /// </summary>
    public sealed class StolenLumenPickupSpawner : MonoBehaviour, IStolenLumenSpawner
    {
        [Tooltip("Enable drain-and-spawn. Disable to drop stolen Lumens silently (debug only).")]
        [SerializeField] private bool _enabled = true;

        private void OnEnable()
        {
            // Register self as the IStolenLumenSpawner (overwrites NullStolenLumenSpawner).
            // Round-1 review fix: nothing previously registered the real spawner.
            ServiceLocator.Register<IStolenLumenSpawner>(this);
        }
        private void OnDisable()
        {
            // Only restore the null fallback if we still own the slot; a replacement instance
            // may already have registered during a scene transition.
            if (ReferenceEquals(ServiceLocator.Get<IStolenLumenSpawner>(), this))
                ServiceLocator.Register<IStolenLumenSpawner>(new NullStolenLumenSpawner());
        }

        /// <summary>
        /// Drain the authoritative dropped-Lumen queue on the registered <c>ILumenScoreboard</c>
        /// and spawn a <see cref="StolenLumenPickup"/> for each record. Called by
        /// <c>MatchManager.HandlePlayerCrash</c>. Each record carries the crash GeoPoint + the
        /// number of Lumens dropped. One +1 pickup is created for each dropped Lumen so the
        /// amount removed from the victim can be fully recovered by other runners.
        /// </summary>
        public void DrainAndSpawn()
        {
            if (!_enabled) return;
            // Resolve through the Core contract so Lightfield stays independent of Trail.
            var scoreboard = ServiceLocator.Get<ILumenScoreboard>();
            if (scoreboard == null) return;
            while (scoreboard.TryDequeueStolenLumen(out var record))
            {
                if (!record.IsValid) continue;
                for (int i = 0; i < record.LumensDropped; i++)
                    StolenLumenPickup.CreateInstance(record.At, record.PlayerId);
            }
        }
    }
}
