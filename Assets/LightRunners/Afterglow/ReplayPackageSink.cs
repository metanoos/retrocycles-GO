using System;
using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Afterglow
{
    // ─── Track F: Afterglow Replay Sink ──────────────────────────────────────
    // Implements IMatchReplaySink (Core) on top of a ReplayPackage. Subscribes to
    // GameEvents to capture Lumen/crash/expiry as a parallel observer. Decision U:
    // the resulting package is the one artifact the Overview and Walk-Inside both read.

    /// <summary>
    /// Per-player trail-snapshot provider that the sink queries on <c>MatchExpired</c> to
    /// finalize <see cref="ReplayPackage.Trails"/>. Track D wires the real provider; for
    /// the milestone the sink requires this be set via <see cref="ReplayPackageSink.TrailSnapshotProvider"/>
    /// and warns (does not throw) if it is null at expiry.
    ///
    /// Contract for Track D:
    ///   • Receives a playerId string.
    ///   • Returns the player's final <see cref="TrailSnapshotPoints"/> (flattened
    ///     lat/lon/alt doubles + point count). Return a default
    ///     <c>TrailSnapshotPoints(empty, 0)</c> for an unknown / empty trail.
    ///   • MUST be safe to call from the main thread (sink calls it on MatchExpired).
    ///   • MUST be idempotent at expiry (the sink calls it once per known player).
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public delegate TrailSnapshotPoints TrailSnapshotProviderDelegate(string playerId);

    /// <summary>
    /// Iterates over all live players at expiry and returns their snapshot. Tracks that
    /// already captured via <see cref="IMatchReplaySink.RecordTrailSnapshot"/> are still
    /// re-snapshotted by this enumerator — the latest wins. Returning an empty enumerator
    /// is valid (e.g. when the match host already pushed snapshots through the sink).
    /// </summary>
    public delegate IEnumerable<string> LivePlayerEnumeratorDelegate();

    /// <summary>
    /// <see cref="IMatchReplaySink"/> implementation that accumulates a
    /// <see cref="ReplayPackage"/> throughout a match (decisions A, U, T) and finalizes it
    /// on <c>GameEvents.MatchExpired</c>.
    ///
    /// Capture sources (decision documented per source):
    ///   • Lumens: <see cref="GameEvents.LumensChanged"/> AND
    ///     <see cref="GameEvents.GateCollected"/>. The latter carries gateId but NOT the
    ///     collection point; we wait for the next LumensChanged for the same player to
    ///     backfill, but also accept a direct <see cref="IMatchReplaySink.RecordLumen"/>
    ///     call (Track D's authoritative path).
    ///   • Crashes: <see cref="IMatchReplaySink.RecordCrash"/> is the proper contract and
    ///     Track D calls it directly with full metadata. As a SAFETY NET we ALSO subscribe
    ///     to <see cref="GameEvents.PlayerCrashed"/> — which carries ONLY a player id — to
    ///     ensure no crash is lost if Track D's path is skipped. The bus fallback emits a
    ///     partial crash (Tier = NonLeader, LumensDropped = 0, At = default) and is
    ///     superseded by any subsequent full RecordCrash for the same player at the same
    ///     timestamp. GAP NOTE for Track A: until a richer crash-event API exists, bus-only
    ///     crashes cannot recover their tier/Lumens-dropped.
    ///   • Trail snapshots: <see cref="IMatchReplaySink.RecordTrailSnapshot"/> accepts
    ///     incremental snapshots during the match; at expiry, the sink also queries the
    ///     registered <see cref="TrailSnapshotProvider"/> for each known player so the
    ///     final-shape trail is captured even if Track D only used the event-bus path.
    ///
    /// Ground-only milestone (decision S): no Walk-Inside — but the package is identical.
    /// </summary>
    public sealed class ReplayPackageSink : IMatchReplaySink
    {
        // Re-entrancy / double-finalize guard: MatchExpired can fire from multiple paths
        // (UI, host authoritative, late bus subscribers).
        private bool _finalized;

        private readonly ReplayPackage _package;

        /// <summary>
        /// Per-player trail snapshot provider. Track D wires the real provider. If null at
        /// expiry the sink logs a warning and finalizes with whatever snapshots were
        /// captured via <see cref="RecordTrailSnapshot"/> during the match.
        /// </summary>
        public TrailSnapshotProviderDelegate TrailSnapshotProvider;

        /// <summary>
        /// Enumerator of all live player ids at expiry. Track D wires this from
        /// <c>MatchManager</c>'s roster. If null, the sink finalizes with whatever snapshots
        /// arrived during the match (no per-player re-query).
        /// </summary>
        public LivePlayerEnumeratorDelegate LivePlayerEnumerator;

        /// <summary>
        /// Optional rank order (1st first). Track D supplies this at or before expiry from
        /// the <c>ILumenScoreboard</c>; the sink writes it into <see cref="ReplayPackage.FinishOrder"/>
        /// on <see cref="Freeze"/>. May be null.
        /// </summary>
        public IReadOnlyList<string> FinishOrder;

        /// <summary>
        /// Optional authoritative tail radius (decision T). Track D wires this from
        /// <c>ITailAuthority</c>; the sink writes it into <see cref="ReplayPackage.FrozenTailRadius"/>
        /// before freezing. If unset the package default (2.0 m) is used.
        /// </summary>
        public float? FrozenTailRadius;

        /// <summary>Fixed player radius copied from the exact host-frozen config.</summary>
        public int? FrozenPlayerHeadRadiusCm;

        /// <summary>Hash copied from the exact host-frozen config.</summary>
        public uint? FrozenConfigHash;

        /// <summary>The package this sink is filling. Read-only view; capture via this sink.</summary>
        public ReplayPackage Package => _package;

        /// <summary>True after <see cref="Freeze"/> has run; further captures are no-ops.</summary>
        public bool IsFinalized => _finalized;

        /// <summary>
        /// Construct a sink for a new package. The match id defaults to a fresh GUID in the
        /// package constructor; pass an explicit package for tests.
        /// </summary>
        public ReplayPackageSink() : this(new ReplayPackage())
        {
        }

        /// <summary>Construct a sink over an existing package (used by tests).</summary>
        public ReplayPackageSink(ReplayPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            if (_package.MatchStartTimeUtc == default)
                _package.MatchStartTimeUtc = DateTime.UtcNow;
        }

        // ─── IMatchReplaySink (authoritative contract — Track D calls these) ─────

        /// <summary>
        /// Record one Lumen collection at <paramref name="at"/> / <paramref name="matchTimeSeconds"/>.
        /// </summary>
        public void RecordLumen(string playerId, GeoPoint at, double matchTimeSeconds)
        {
            if (_finalized) return; // sink ignores late captures silently (vs. package throws)
            if (string.IsNullOrEmpty(playerId)) return;
            _package.AddLumen(new LumenEvent(playerId, at, matchTimeSeconds));
        }

        /// <summary>
        /// Record one crash with full metadata (authoritative — Track D calls this from
        /// <c>ILumenScoreboard.ApplyCrashPenalty</c>). The legacy event bus can't supply
        /// tier/dropped; that fallback lives in <see cref="OnPlayerCrashed"/>.
        /// </summary>
        public void RecordCrash(string playerId, GeoPoint at, double matchTimeSeconds, CrashTier tier, int lumensDropped)
        {
            if (_finalized) return;
            if (string.IsNullOrEmpty(playerId)) return;
            _package.AddCrash(new CrashEvent(playerId, at, matchTimeSeconds, tier, lumensDropped));
        }

        /// <summary>
        /// Record one incremental trail snapshot. Latest wins per player (replaces in
        /// <see cref="ReplayPackage.AddTrail"/>).
        /// </summary>
        public void RecordTrailSnapshot(string playerId, in TrailSnapshotPoints points, double matchTimeSeconds)
        {
            if (_finalized) return;
            if (string.IsNullOrEmpty(playerId)) return;
            _package.AddTrail(TrailCapture.FromSnapshot(playerId, in points));
        }

        // ─── Finalization ───────────────────────────────────────────────────

        /// <summary>
        /// Snapshot all live trails via <see cref="TrailSnapshotProvider"/> (if set),
        /// apply finish order / tail radius / end-time, then freeze the package. Idempotent.
        /// Safe to call from <c>GameEvents.MatchExpired</c>. Track D should call this (or
        /// the sink should subscribe — see <see cref="BindToEventBus"/>).
        /// </summary>
        public void Freeze()
        {
            if (_finalized) return;
            _finalized = true;

            // Per-player final snapshots. The provider may be null in milestone/test setups.
            if (TrailSnapshotProvider != null && LivePlayerEnumerator != null)
            {
                foreach (var playerId in LivePlayerEnumerator())
                {
                    if (string.IsNullOrEmpty(playerId)) continue;
                    var snap = TrailSnapshotProvider(playerId);
                    if (snap.PointCount > 0)
                        _package.AddTrail(TrailCapture.FromSnapshot(playerId, in snap));
                }
            }
            else if (TrailSnapshotProvider == null && LivePlayerEnumerator != null)
            {
                Debug.LogWarning(
                    "[ReplayPackageSink] LivePlayerEnumerator set but TrailSnapshotProvider is null — " +
                    "final trail shapes will be missing for players not captured via RecordTrailSnapshot. " +
                    "Track D must wire TrailSnapshotProvider (decision U).");
            }

            if (FinishOrder != null)
                _package.SetFinishOrder(new List<string>(FinishOrder));

            if (FrozenTailRadius.HasValue
                && FrozenPlayerHeadRadiusCm.HasValue
                && FrozenConfigHash.HasValue)
            {
                int tailRadiusCm = (int)Math.Round(
                    FrozenTailRadius.Value * 100.0,
                    MidpointRounding.AwayFromZero);
                if (FrozenMatchConfig.TryRestore(
                        tailRadiusCm,
                        FrozenPlayerHeadRadiusCm.Value,
                        FrozenConfigHash.Value,
                        out var config,
                        out string error))
                    _package.SetFrozenMatchConfig(config);
                else
                    Debug.LogError($"[ReplayPackageSink] Rejected frozen match config: {error}");
            }
            else if (FrozenTailRadius.HasValue)
            {
                // Backward compatibility for older capture callers. New match finalization always
                // supplies all three fields and therefore takes the validated branch above.
                _package.SetFrozenTailRadius(FrozenTailRadius.Value);
            }

            _package.SetMatchEndTime(DateTime.UtcNow);
            _package.Freeze();
        }

        // ─── Event-bus binding (parallel observer of GameEvents) ─────────────
        //
        // Subscribe on scene enter, unsubscribe on scene exit (mirrors ARViewManager's
        // enter/exit pattern). The sink does NOT auto-subscribe in its constructor — that
        // would leak across scene loads (GameEvents explicitly does not auto-clear).

        /// <summary>
        /// Subscribe to the GameEvents bus for Lumen/crash/expiry capture. Idempotent;
        /// pair with <see cref="UnbindFromEventBus"/> on scene exit.
        /// </summary>
        public void BindToEventBus()
        {
            GameEvents.LumensChanged += OnLumensChanged;
            GameEvents.GateCollected += OnGateCollected;
            GameEvents.MatchExpired += OnMatchExpired;
            GameEvents.PlayerCrashed += OnPlayerCrashed;
        }

        /// <summary>Unsubscribe from the GameEvents bus.</summary>
        public void UnbindFromEventBus()
        {
            GameEvents.LumensChanged -= OnLumensChanged;
            GameEvents.GateCollected -= OnGateCollected;
            GameEvents.MatchExpired -= OnMatchExpired;
            GameEvents.PlayerCrashed -= OnPlayerCrashed;
        }

        private void OnLumensChanged(string playerId, int newTotal)
        {
            // We don't have the lumen-collect point from this event (the tally fires from
            // multiple sources). We record a lumen event only when we have a position,
            // which arrives via RecordLumen (Track D). No-op here.
        }

        private void OnGateCollected(int gateIdValue, string collectorPlayerId)
        {
            // As with LumensChanged: no point available. The authoritative RecordLumen
            // call from Track D's gate-collect handler is the canonical capture path.
        }

        private void OnPlayerCrashed(string playerId)
        {
            // GAP NOTE (Track A): GameEvents.PlayerCrashed carries ONLY a player id. We
            // emit a partial crash record as a safety net; Track D's authoritative
            // RecordCrash call (with tier + lumensDropped) supersedes it if it arrives
            // later in the same frame. Until Track A exposes a richer crash event, bus-
            // only crashes can't recover their tier or dropped-Lumen count.
            if (_finalized || string.IsNullOrEmpty(playerId)) return;
            _package.AddCrash(new CrashEvent(
                playerId,
                default,
                CurrentMatchTimeSeconds(),
                CrashTier.NonLeader,
                lumensDropped: 0));
        }

        private void OnMatchExpired()
        {
            Freeze();
        }

        /// <summary>
        /// Best-effort match-relative time used when only the event bus fires (no
        /// authoritative Record* call). Falls back to wall-clock seconds since match start.
        /// </summary>
        private double CurrentMatchTimeSeconds()
        {
            if (_package.MatchStartTimeUtc == default) return 0.0;
            return (DateTime.UtcNow - _package.MatchStartTimeUtc).TotalSeconds;
        }
    }
}
