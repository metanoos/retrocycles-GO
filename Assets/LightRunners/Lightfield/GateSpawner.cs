using System;
using System.Collections.Generic;
using LightRunners.Core;
using UnityEngine;

namespace LightRunners.Lightfield
{
    /// <summary>
    /// Density formula for the active Lumen Gate pool. Decision M:
    /// <c>activeGateCount = max(1, ceil(playerCount × gatesPerPlayer))</c>. Default ratio 0.5.
    /// Pure-C# so the formula is unit-tested in isolation (see GateDensityTests).
    /// </summary>
    public static class GateDensity
    {
        /// <summary>
        /// Active gate count for the given player count and ratio. Decision M.
        /// Always at least 1 (a match with players must have a gate). Negative player counts
        /// are clamped to 0 before the formula. NaN/negative ratios produce 1 (the floor).
        /// </summary>
        public static int ActiveGateCount(int playerCount, float gatesPerPlayer)
        {
            if (playerCount <= 0) return 1;
            if (float.IsNaN(gatesPerPlayer) || gatesPerPlayer <= 0f) return 1;

            double raw = Math.Ceiling(playerCount * (double)gatesPerPlayer);
            int count = (int)raw;
            return Math.Max(1, count);
        }
    }

    /// <summary>
    /// Authoritative record of a single Gate spawn (density-pool or bonus). Held in the
    /// <see cref="GateSpawner"/>'s active dictionary so collection/respawn can address it.
    /// Decisions G/L/M/R.
    /// </summary>
    public sealed class LumenGateState
    {
        public GateId Id { get; }
        public GeoPoint Position { get; }
        public GatePlacement Placement { get; }
        /// <summary>
        /// True for referee-placed bonus gates (decision R). Bonus gates do NOT count toward
        /// <see cref="GateSpawner.ActiveGateCount"/> and are not replaced on collection.
        /// </summary>
        public bool IsBonus { get; }

        public LumenGateState(GateId id, GeoPoint position, GatePlacement placement, bool isBonus)
        {
            Id = id;
            Position = position;
            Placement = placement;
            IsBonus = isBonus;
        }

        public override string ToString() => $"{Id} @ {Position} ({Placement}{(IsBonus ? ", bonus" : "")})";
    }

    /// <summary>
    /// Generates a candidate spawn position inside a Lightfield volume. Pulled out as an
    /// interface so unit tests can inject a fixed sequence (the spawner's respawn loop is
    /// otherwise non-deterministic). Decision L (ground placement half-buries; milestone is
    /// always ground).
    /// </summary>
    public interface IGatePositionSampler
    {
        /// <summary>Return a point inside <paramref name="volume"/> at the volume's ground altitude.</summary>
        GeoPoint SampleInside(ILightfieldVolume volume);
    }

    /// <summary>
    /// Default uniform-area sampler: picks a random point inside the disc of radius
    /// <c>lightfieldBaseRadiusMeters</c> around the volume origin. Uses sqrt-uniform radius so
    /// the density is uniform (not clustered at the centre). Spec §4.1 (small-angle projection).
    ///
    /// The milestone always returns ground altitude; aerial bands are deferred (decision S,
    /// decision L). Aerial milestone TODO: pick altitude from active-player bands.
    /// </summary>
    public sealed class DefaultGatePositionSampler : IGatePositionSampler
    {
        private readonly System.Random _rng;
        private readonly float _radiusMarginFraction;

        /// <param name="rng">Inject for tests; defaults to a fresh <see cref="System.Random"/>.</param>
        /// <param name="radiusMarginFraction">
        /// Sample at this fraction of the configured base radius so Haversine-vs-equirectangular
        /// error at the rim cannot place a "valid" sample slightly outside. Default 0.9.
        /// </param>
        public DefaultGatePositionSampler(System.Random rng = null, float radiusMarginFraction = 0.9f)
        {
            _rng = rng ?? new System.Random();
            _radiusMarginFraction = Mathf.Clamp01(radiusMarginFraction);
        }

        public GeoPoint SampleInside(ILightfieldVolume volume)
        {
            if (volume == null) return default;

            GameConfig cfg = GameConfig.Active;
            float maxRadius = cfg.lightfieldBaseRadiusMeters * _radiusMarginFraction;
            GeoPoint origin = volume.Origin;

            // sqrt-uniform so area density is constant over the disc.
            double r = Math.Sqrt(_rng.NextDouble()) * maxRadius;
            double theta = _rng.NextDouble() * 2.0 * Math.PI;

            // Equirectangular projection back to lat/lon (consistent with GeoPoint.Haversine).
            const double EarthR = GeoPoint.EarthRadiusMeters;
            double metersPerDegLat = Math.PI * EarthR / 180.0;
            double metersPerDegLon = metersPerDegLat * Math.Cos(origin.latitude * Math.PI / 180.0);

            double dNorth = r * Math.Cos(theta);
            double dEast = r * Math.Sin(theta);

            return new GeoPoint(
                origin.latitude + dNorth / metersPerDegLat,
                origin.longitude + dEast / metersPerDegLon,
                origin.altitude);  // ground-only milestone; aerial bands deferred (decision S/L).
        }
    }

    /// <summary>
    /// Authoritative Gate spawn/collect lifecycle. Implements <see cref="IGateDirector"/>
    /// (decisions G, L, M, R). Host-side plain C# — visual instantiation is the consumer's job
    /// (subscribe to <see cref="GateSpawned"/>/<see cref="GateDespawned"/> and create the
    /// <see cref="LumenGate"/> GameObjects). Collection is accepted atomically through
    /// <see cref="TryCollectGate"/>; only that accepted path raises the static collection event
    /// consumed by scoring and replay.
    /// </summary>
    public sealed class GateSpawner : IGateDirector
    {
        /// <summary>
        /// Bonus gates are minted above this value so their ids never collide with density-pool
        /// ids. Density ids start at 1; bonus ids start at <c>BonusGateIdBase</c>. A separate
        /// reserved range for <c>StolenLumenPickup</c> synthetic ids lives above this.
        /// </summary>
        public const int BonusGateIdBase = 1_000_000;

        private readonly ILightfieldVolume _volume;
        private readonly IGatePositionSampler _sampler;
        private readonly Dictionary<GateId, LumenGateState> _active = new Dictionary<GateId, LumenGateState>();
        private int _nextDensityId = 1;
        private int _nextBonusId = BonusGateIdBase;
        private int _densityCount;
        private int _bonusCount;

        /// <summary>
        /// Density-pool (baseline) gate count only. Round-1 review fix R1-F10/R2: previously
        /// returned <c>_active.Count</c> which included bonus gates, breaking
        /// <c>ValidateGateCollectHost</c>'s id-range bound. Bonus gates are tracked separately
        /// via <see cref="ActiveBonusGateCount"/>.
        /// </summary>
        public int ActiveGateCount => _densityCount;

        /// <summary>Referee-placed bonus gates currently active (decision R).</summary>
        public int ActiveBonusGateCount => _bonusCount;

        public event Action<GateId, GeoPoint, GatePlacement> GateSpawned;
        public event Action<GateId> GateDespawned;
        public event Action<GateId, string> GateCollected;

        /// <summary>
        /// Look up an active gate's position (Round-1 fix R1-F15/R2-F8: host-side validation and
        /// replay sink both need gate positions). Returns false for unknown/collected ids.
        /// </summary>
        public bool TryGetGatePosition(GateId id, out GeoPoint position)
        {
            if (_active.TryGetValue(id, out var state)) { position = state.Position; return true; }
            position = default;
            return false;
        }

        /// <param name="volume">The play volume gates must spawn inside.</param>
        /// <param name="sampler">Spawn position source; null → <see cref="DefaultGatePositionSampler"/>.</param>
        /// <param name="refereeTokenValidator">
        /// Optional token validator for <see cref="PlaceBonusGate"/> (decision R). Round-1 review
        /// fix R1-F16/R2-F12: previously <see cref="PlaceBonusGate"/> only checked the token was
        /// non-empty, and the real <c>RefereeTokenValidator</c> (in Multiplayer) was only invoked
        /// by <c>RefereeClient</c> — any host-side caller could bypass it by calling
        /// PlaceBonusGate directly. The validator is now injected at construction so the check is
        /// unavoidable regardless of caller. Track C's <c>RefereeClient</c> registers the real
        /// validator when it constructs/registers the GateSpawner; the default is the legacy
        /// non-empty check for tests and scenes that don't run a referee.
        /// </param>
        public GateSpawner(ILightfieldVolume volume, IGatePositionSampler sampler = null,
            Func<string, bool> refereeTokenValidator = null)
        {
            _volume = volume ?? throw new ArgumentNullException(nameof(volume));
            _sampler = sampler ?? new DefaultGatePositionSampler();
            _refereeTokenValidator = refereeTokenValidator ?? DefaultTokenCheck;
        }

        private readonly Func<string, bool> _refereeTokenValidator;
        private static bool DefaultTokenCheck(string token) => !string.IsNullOrEmpty(token);


        /// <summary>
        /// Decision M: set the active pool size to
        /// <c>max(1, ceil(playerCount × gatesPerPlayer))</c> and spawn that many Gates at random
        /// valid points inside the volume. Clears any prior pool first. Fires
        /// <see cref="GateSpawned"/> once per spawned gate.
        /// </summary>
        public void ConfigureForPlayers(int playerCount, float gatesPerPlayer)
        {
            // Clear existing pool (fire despawn so visuals can clean up).
            if (_active.Count > 0)
            {
                foreach (var id in new List<GateId>(_active.Keys))
                    DespawnGate(id);
            }

            int target = GateDensity.ActiveGateCount(playerCount, gatesPerPlayer);
            for (int i = 0; i < target; i++)
                SpawnDensityGate();
        }

        /// <summary>
        /// Decision R — referee-only bonus gate. v2 (full Gate-Director UI) is deferred per
        /// decision S. Round-1 review fix R1-F16/R2-F12: the token is now validated via the
        /// injected <c>refereeTokenValidator</c> (constructor arg) rather than a local non-empty
        /// check, so the validation is unavoidable regardless of caller. Track C's
        /// <c>RefereeClient</c> injects the real <c>RefereeTokenValidator.Validate</c> when it
        /// constructs the GateSpawner; tests get the default non-empty check.
        ///
        /// Bonus gates do NOT count toward <see cref="ActiveGateCount"/> and are NOT replaced on
        /// collection (they're one-shot rewards).
        /// </summary>
        public void PlaceBonusGate(GeoPoint at, GatePlacement placement, string refereeToken)
        {
            if (!_refereeTokenValidator(refereeToken))
            {
                UnityEngine.Debug.LogWarning("[GateSpawner] PlaceBonusGate rejected: referee token failed validation.");
                return;
            }

            // Milestone: warn (do not spawn) for aerial requests — decision S/L defers aerial.
            if (placement == GatePlacement.Aerial)
            {
                UnityEngine.Debug.LogWarning("[GateSpawner] PlaceBonusGate: Aerial placement requested but aerial milestone is deferred (decision S/L); forcing Ground for this bonus gate.");
                placement = GatePlacement.Ground;
            }

            var id = new GateId(_nextBonusId++);
            var state = new LumenGateState(id, at, placement, isBonus: true);
            _active[id] = state;
            _bonusCount++;

            GameEvents.RaiseGateSpawned(id.Value, at.latitude, at.longitude, at.altitude, placement);
            try { GateSpawned?.Invoke(id, at, placement); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }

        /// <summary>
        /// Compatibility wrapper for older direct callers. New code should use
        /// <see cref="TryCollectGate"/> so it can observe rejection.
        /// </summary>
        public void CollectGate(GateId gateId, string collectorPlayerId)
            => TryCollectGate(gateId, collectorPlayerId);

        /// <summary>
        /// Canonical authoritative collection entry point. Removes an active gate, fires its
        /// instance lifecycle events, restores density, and only then publishes the accepted
        /// <see cref="GameEvents.GateCollected"/> notification. Unknown/stale ids never reach
        /// score or replay mutation.
        /// </summary>
        public bool TryCollectGate(GateId gateId, string collectorPlayerId)
        {
            if (string.IsNullOrEmpty(collectorPlayerId)) return false;
            if (!_active.TryGetValue(gateId, out var state))
                return false;

            bool wasBonus = state.IsBonus;
            DespawnGate(gateId);

            try { GateCollected?.Invoke(gateId, collectorPlayerId); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }

            // Decision M: density gates are replaced; bonus gates are one-shot.
            if (!wasBonus)
                SpawnDensityGate();

            // Score/replay observers see only an accepted, fully-settled director mutation.
            GameEvents.RaiseGateCollected(gateId.Value, collectorPlayerId, state.Position);
            return true;
        }

        /// <summary>Snapshot of currently-active gates (density + bonus). Read-only view.</summary>
        public IReadOnlyCollection<LumenGateState> ActiveGates => _active.Values;

        /// <summary>Tear down: clear the pool (no despawn events fired).</summary>
        public void Dispose()
        {
            _active.Clear();
            _densityCount = 0;
            _bonusCount = 0;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────────────

        private void SpawnDensityGate()
        {
            GeoPoint at = SampleValidPoint();
            var id = new GateId(_nextDensityId++);
            var state = new LumenGateState(id, at, GatePlacement.Ground, isBonus: false);
            _active[id] = state;
            _densityCount++;

            GameEvents.RaiseGateSpawned(id.Value, at.latitude, at.longitude, at.altitude, GatePlacement.Ground);
            try { GateSpawned?.Invoke(id, at, GatePlacement.Ground); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }

        private void DespawnGate(GateId id)
        {
            // Capture IsBonus BEFORE removing so we can maintain the split counters
            // (Round-1 review fix R1-F10).
            bool isBonus = _active.TryGetValue(id, out var s) && s.IsBonus;
            if (!_active.Remove(id)) return;
            if (isBonus) _bonusCount = Math.Max(0, _bonusCount - 1);
            else _densityCount = Math.Max(0, _densityCount - 1);
            GameEvents.RaiseGateDespawned(id.Value);
            try { GateDespawned?.Invoke(id); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }

        private GeoPoint SampleValidPoint()
        {
            // Try the sampler up to N times; if it keeps returning out-of-bounds points (a buggy
            // custom sampler), fall back to the origin so we never fail to spawn.
            const int maxAttempts = 8;
            for (int i = 0; i < maxAttempts; i++)
            {
                GeoPoint p = _sampler.SampleInside(_volume);
                if (_volume.IsInside(p)) return p;
            }
            UnityEngine.Debug.LogWarning("[GateSpawner] Sampler failed to return an in-bounds point after 8 tries; falling back to volume origin.");
            return _volume.Origin;
        }
    }

    // StolenLumenRecord lives in LightRunners.Core (Core/MatchContracts.cs) — shared by
    // Trail (authoritative queue owner) and Lightfield (consumer). Reconciled during
    // integration; Track B's forward-declaration duplicate removed.
}
