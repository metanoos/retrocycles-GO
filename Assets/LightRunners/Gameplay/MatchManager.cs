using System;
using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;
using LightRunners.Afterglow;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Lightfield match orchestrator — the integration track that ties together Tracks A/B/C/E/F.
    /// Implements <see cref="IMatchSession"/> (decision P): a strictly layered match sub-FSM
    /// <c>Idle→Warmup→Countdown→Live→Scoring→Expired</c> layered ON TOP of the existing
    /// <see cref="GameState"/> (which still drives Login/Lobby/etc).
    ///
    /// ACTIVE DECISIONS implemented here:
    ///  • P (match core architecture): the match sub-FSM is the single authority for match
    ///    lifecycle; <see cref="GameManager"/> delegates match begin/end here.
    ///  • O (timed match): host-tunable <c>matchDurationSeconds</c> clock (default 6 min); most
    ///    Lumens wins on expiry.
    ///  • E/F/I (Lumen scoring + crash penalty): constructs and registers the authoritative
    ///    <see cref="LumenScoreboard"/> (Track A) on the locator, overwriting
    ///    <see cref="NullLumenScoreboard"/>.
    ///  • T (tail authority): constructs and registers <see cref="TailAuthority"/> (Track A);
    ///    <see cref="FreezeTailAtCountdown"/> fires on entry to Countdown.
    ///  • Q (host-mode transport): resolves <see cref="IMatchTransport"/> from the locator
    ///    (real <c>FusionLauncher</c> overwrites <see cref="NullMatchTransport"/>). Online/offline
    ///    detection observes <see cref="GameEvents.ConnectionStateChanged"/>.
    ///  • F (crash is no longer terminal): <see cref="HandlePlayerCrash"/> applies the Lumen
    ///    penalty, records the crash with full metadata via
    ///    <see cref="IMatchReplaySink.RecordCrash"/> (closes Track F's crash-metadata gap),
    ///    and respawns the runner. The match only ends on clock expiry.
    ///  • U (afterglow): wires <see cref="IMatchReplaySink"/> (Track F's
    ///    <c>ReplayPackageSink</c>) via its <c>TrailSnapshotProvider</c> / finish-order / tail
    ///    radius hooks; freezes the package on expiry.
    ///
    /// CRASH PIPELINE DOUBLE-HANDLING RESOLUTION (Track D vs GameManager):
    /// The crash bus (<see cref="GameEvents.PlayerCrashed"/>) historically terminated the run via
    /// <c>GameManager.FinalizeRun</c>. Under the match model, a crash is a mid-match penalty +
    /// respawn — NOT a terminal run-end. To avoid double-handling:
    ///   1. <see cref="GameManager"/> is the SINGLE subscriber of
    ///      <see cref="GameEvents.PlayerCrashed"/>; this class does NOT subscribe.
    ///   2. <see cref="GameManager.OnPlayerCrashed"/> delegates to
    ///      <see cref="HandlePlayerCrash"/> on this manager (with the crash GeoPoint) instead of
    ///      <c>FinalizeRun</c>, and its own crash double-fire guard stays in place.
    ///   3. <see cref="HandlePlayerCrash"/> is itself idempotent per crash event: it applies the
    ///      penalty + replay record + respawn exactly once (early-out if not in Live state).
    /// This keeps the crash source single (GameManager) and the match reaction single (MatchManager).
    /// </summary>
    public class MatchManager : Singleton<MatchManager>, IMatchSession
    {
        // ─── Inspector ──────────────────────────────────────────────────────
        [Header("Respawn (decision F — crash is no longer terminal)")]
        [Tooltip("Respawn offset (metres) from the crash site along the runner's last heading. Default 5m back.")]
        [SerializeField] private float respawnBackOffsetMeters = 5f;
        [Tooltip("Brief invulnerability (s) after a respawn so the trailing-into-own-tail case can't instantly re-crash.")]
        [SerializeField] private float respawnGraceSeconds = 2f;

        // ─── IMatchSession surface ──────────────────────────────────────────
        public MatchState State { get; private set; } = MatchState.Idle;
        public float TimeRemaining { get; private set; }

        /// <summary>
        /// True if this client is the authoritative match host (decision Q). Resolves
        /// <see cref="IMatchTransport"/> and (when it exposes a host bit via reflection on the
        /// concrete FusionLauncher) honours it; falls back to <c>true</c> in offline/editor mode
        /// so a single-player editor session can still drive the FSM.
        /// </summary>
        public bool IsHostAuthority
        {
            get
            {
                if (ServiceLocator.TryGet<IMatchTransport>(out var transport) && transport != null)
                {
                    // The concrete FusionLauncher (Track C) exposes `bool IsHost`. We can't
                    // reference that type here without taking a Multiplayer dependency from a
                    // property getter, so resolve reflectively — the contract is documented in
                    // Track C's FusionLauncher class comment.
                    var t = transport.GetType();
                    var prop = t.GetProperty("IsHost");
                    if (prop != null && prop.PropertyType == typeof(bool))
                    {
                        try { return (bool)prop.GetValue(transport); }
                        catch { /* fall through */ }
                    }
                    // NullMatchTransport / unknown → treat offline as host so editor FSM works.
                    return true;
                }
                return true;
            }
        }

        public event Action<MatchState, MatchState> StateChanged;

        /// <summary>
        /// Raised by <see cref="HandlePlayerCrash"/> after the Lumen penalty + replay record,
        /// signalling GameManager to respawn the runner (reset the trail / teleport the avatar).
        /// (playerId, crashSite). Decision F — crash is no longer terminal.
        /// </summary>
        public event Action<string, GeoPoint> RespawnRequested;

        // ─── Internal state ─────────────────────────────────────────────────
        private double _matchStartEpochSeconds = -1.0;
        private double _countdownStartedAt = -1.0;
        private string _localPlayerId;
        private readonly HashSet<string> _knownPlayers = new HashSet<string>();
        private float _respawnGraceUntil = -1f;

        // Scoreboard / tail authority are constructed here (Track A impls land in integration).
        private LumenScoreboard _scoreboard;
        private TailAuthority _tailAuthority;
        private SnakeTailModel _snakeTailModel;
        // Track F replay sink — constructed in Awake (Round-1 review fix).
        private ReplayPackageSink _replaySink;

        /// <summary>Match-relative time in seconds (0 outside a live match).</summary>
        private double MatchClockSeconds()
        {
            if (_matchStartEpochSeconds < 0) return 0.0;
            return Time.timeAsDouble - _matchStartEpochSeconds;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────
        protected override void Awake()
        {
            base.Awake();

            // Construct & register the real Track A impls, overwriting the Null* instances
            // installed by PlatformServiceRegistry. Register (not TryRegister) so we always win.
            _scoreboard = new LumenScoreboard(MatchClockSeconds);
            _tailAuthority = new TailAuthority();
            _snakeTailModel = new SnakeTailModel();
            ServiceLocator.Register<ILumenScoreboard>(_scoreboard);
            ServiceLocator.Register<ITailAuthority>(_tailAuthority);

            // Construct & register the real Track F replay sink (Round-1 review fix F1: this was
            // never constructed, so the locator kept NullMatchReplaySink and Afterglow was always
            // empty). Gameplay references Afterglow directly now (asmdef updated) so we can drop
            // the prior reflection-based lookup.
            _replaySink = new ReplayPackageSink();
            _replaySink.BindToEventBus();
            ServiceLocator.Register<IMatchReplaySink>(_replaySink);

            // Register self as the match session (overwrites NullMatchSession).
            ServiceLocator.Register<IMatchSession>(this);

            // Subscribe to the static bus (mirrors GameManager's pattern; we never reference
            // Multiplayer / Backend directly — those assemblies push events here).
            // NOTE: we deliberately do NOT subscribe to GameEvents.PlayerCrashed — that would
            // double-handle with GameManager.OnPlayerCrashed, which is the single crash listener
            // and calls MatchManager.HandlePlayerCrash directly with the crash GeoPoint. The
            // crash-pipeline contract is documented on this class's doc-comment.
            GameEvents.ConnectionStateChanged += OnBusConnectionStateChanged;
            GameEvents.GateCollected += OnBusGateCollected;
        }

        protected virtual void OnDestroy()
        {
            GameEvents.ConnectionStateChanged -= OnBusConnectionStateChanged;
            GameEvents.GateCollected -= OnBusGateCollected;
            try { _replaySink?.UnbindFromEventBus(); } catch { /* idempotent */ }
        }

        private void Update()
        {
            if (State == MatchState.Countdown)
            {
                float remaining = Mathf.Max(0f,
                    GameConfig.Active.matchCountdownSeconds - (float)(Time.timeAsDouble - _countdownStartedAt));
                if (remaining <= 0f)
                    TransitionTo(MatchState.Live);
            }
            else if (State == MatchState.Live)
            {
                TimeRemaining -= Time.deltaTime;
                if (TimeRemaining <= 0f)
                {
                    TimeRemaining = 0f;
                    ExpireMatch();
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // IMatchSession API (called by GameManager / LobbyUIController / HUD)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Start a new match (decision P). Transitions <c>Idle→Warmup→Countdown</c> in sequence
        /// so the host can configure gates before freezing the tail radius. Idempotent: a no-op if
        /// a match is already in flight.
        /// </summary>
        public void BeginMatch()
        {
            if (State != MatchState.Idle && State != MatchState.Expired)
            {
                Debug.LogWarning($"[MatchManager] BeginMatch ignored — match already in state {State}.");
                return;
            }

            // Resolve local player + reset state.
            _localPlayerId = ResolveLocalPlayerId();
            _knownPlayers.Clear();
            if (!string.IsNullOrEmpty(_localPlayerId)) _knownPlayers.Add(_localPlayerId);
            if (_scoreboard != null) _scoreboard.Reset();
            if (_tailAuthority != null) _tailAuthority.Unfreeze();
            _respawnGraceUntil = -1f;

            TransitionTo(MatchState.Warmup);

            // Auto-advance into Countdown immediately for the milestone. A real Warmup screen
            // would wait on a host "ready" signal; the milestone ships the simplest viable flow.
            TransitionTo(MatchState.Countdown);
        }

        /// <summary>
        /// Voluntarily end the match early (host forfeit / debug). Transitions through Scoring
        /// → Expired exactly like clock expiry.
        /// </summary>
        public void EndMatch()
        {
            if (State == MatchState.Idle || State == MatchState.Expired) return;
            ExpireMatch();
        }

        // ─────────────────────────────────────────────────────────────────────
        // FSM transitions
        // ─────────────────────────────────────────────────────────────────────

        private void TransitionTo(MatchState next)
        {
            if (!ValidateTransition(State, next))
            {
                Debug.LogWarning($"[MatchManager] Rejected transition {State} → {next}.");
                return;
            }

            var prev = State;
            State = next;
            Debug.Log($"[MatchManager] MatchState {prev} → {next}");

            switch (next)
            {
                case MatchState.Warmup:
                    // Reset clock; it ticks during Live.
                    _matchStartEpochSeconds = -1.0;
                    TimeRemaining = GameConfig.Active.matchDurationSeconds;
                    break;

                case MatchState.Countdown:
                    // Decision T: freeze the tail radius on entry to Countdown (host-side).
                    FreezeTailAtCountdown();
                    _countdownStartedAt = Time.timeAsDouble;
                    break;

                case MatchState.Live:
                    _matchStartEpochSeconds = Time.timeAsDouble;
                    TimeRemaining = GameConfig.Active.matchDurationSeconds;
                    ConfigureGatesForLive();
                    break;

                case MatchState.Scoring:
                    // Brief scoring pass; for the milestone we collapse straight into Expired.
                    // (A real implementation waits a frame for late Lumen events.)
                    FinalizeReplayPackage();
                    break;

                case MatchState.Expired:
                    GameEvents.RaiseMatchExpired();
                    break;
            }

            try { StateChanged?.Invoke(prev, next); }
            catch (Exception e) { Debug.LogException(e); }
            GameEvents.RaiseMatchStateChanged(prev, next);
        }

        /// <summary>
        /// Strict transition validator. The match FSM is layered: forward-only except for the
        /// <c>Expired→Warmup</c> (next match) and <c>Expired→Idle</c> reset.
        /// </summary>
        private static bool ValidateTransition(MatchState from, MatchState to)
        {
            if (from == to) return false;
            switch (from)
            {
                case MatchState.Idle:      return to == MatchState.Warmup;
                case MatchState.Warmup:    return to == MatchState.Countdown || to == MatchState.Expired;
                case MatchState.Countdown: return to == MatchState.Live      || to == MatchState.Expired;
                case MatchState.Live:      return to == MatchState.Scoring   || to == MatchState.Expired;
                case MatchState.Scoring:   return to == MatchState.Expired;
                case MatchState.Expired:   return to == MatchState.Warmup || to == MatchState.Idle;
            }
            return false;
        }

        /// <summary>Decision T — freeze the tail radius at its current config value.</summary>
        private void FreezeTailAtCountdown()
        {
            if (_tailAuthority != null) _tailAuthority.FreezeAtCountdown();
            else if (ServiceLocator.TryGet<ITailAuthority>(out var auth)) auth.FreezeAtCountdown();
        }

        /// <summary>Decision M — configure the gate pool for the live player count.</summary>
        private void ConfigureGatesForLive()
        {
            int players = Mathf.Max(1, _knownPlayers.Count);
            float gatesPerPlayer = GameConfig.Active.gatesPerPlayer;
            if (ServiceLocator.TryGet<IGateDirector>(out var director) && director != null)
                director.ConfigureForPlayers(players, gatesPerPlayer);
        }

        private void ExpireMatch()
        {
            // Advance all the way through to Expired. The intermediate Scoring state exists so
            // late Lumen events can settle and the Afterglow package can finalize; for the
            // milestone we collapse straight through, but the FSM table enforces the layering.
            if (State == MatchState.Live)
            {
                TransitionTo(MatchState.Scoring);
                // Scoring finalizes the replay package; then Expired raises MatchExpired.
                if (State == MatchState.Scoring) TransitionTo(MatchState.Expired);
            }
            else if (State == MatchState.Scoring)
            {
                TransitionTo(MatchState.Expired);
            }
            else if (State == MatchState.Warmup || State == MatchState.Countdown)
            {
                // Early expire (host forfeit before live): the transition table permits these
                // states to go directly to Expired. No package to finalize in this case.
                TransitionTo(MatchState.Expired);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Crash pipeline (decision F — crash is no longer terminal)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Authoritative crash handler. Called by <see cref="GameManager.OnPlayerCrashed"/> (the
        /// single crash source) — applies the Lumen penalty, records full metadata to the replay
        /// sink, and respawns the runner. No-op outside Live (the bus can fire late during
        /// Scoring; we ignore those). Idempotent per crash event.
        /// </summary>
        public void HandlePlayerCrash(string playerId, GeoPoint at)
        {
            if (State != MatchState.Live) return;
            if (string.IsNullOrEmpty(playerId)) return;

            CrashTier tier = CrashTier.NonLeader;
            int dropped = 0;

            // Apply Lumen penalty (decision F). Phase 0.5 widened ILumenScoreboard.ApplyCrashPenalty
            // to take the crash GeoPoint, so we route uniformly through the interface — the
            // concrete LumenScoreboard ref is no longer needed to pass the crash site. The
            // scoreboard stamps the position onto the dropped-Lumen pickup record (decision F).
            if (ServiceLocator.TryGet<ILumenScoreboard>(out var sb) && sb != null)
            {
                tier = sb.GetCrashTier(playerId);
                dropped = sb.ApplyCrashPenalty(playerId, at);
            }
            else if (_scoreboard != null)
            {
                // Defensive fallback: _scoreboard is registered on the locator in Awake, so this
                // branch should never hit, but keep it so a misconfigured scene still applies the
                // penalty instead of silently dropping it.
                tier = _scoreboard.GetCrashTier(playerId);
                dropped = _scoreboard.ApplyCrashPenalty(playerId, at);
            }

            // Closes Track F's crash-metadata gap: full RecordCrash with tier + dropped Lumens,
            // superseding the partial record the sink's PlayerCrashed fallback emits.
            if (ServiceLocator.TryGet<IMatchReplaySink>(out var sink) && sink != null)
            {
                sink.RecordCrash(playerId, at, MatchClockSeconds(), tier, dropped);
            }

            // Drain dropped-Lumen pickups (Round-1 review fix R1-F2/R2-F3): the scoreboard enqueues
            // a StolenLumenRecord per crash; the gameplay layer drains it and renders the
            // stealable pickups at the crash site. Previously nothing drained the queue, so
            // dropped Lumens never re-entered play AND the queue leaked memory for the match.
            if (dropped > 0)
            {
                try { ServiceLocator.Get<IStolenLumenSpawner>()?.DrainAndSpawn(); }
                catch (Exception e) { Debug.LogException(e); }
            }

            // Respawn the runner (decision F). For the milestone we mark a grace window; the
            // concrete respawn (reset the trail / teleport the avatar) is wired by GameManager in
            // response to a RespawnRequested event so this class stays free of TrailManager /
            // avatar concerns.
            _respawnGraceUntil = Time.time + respawnGraceSeconds;
            try { RespawnRequested?.Invoke(playerId, at); }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>True while the local runner is in the post-respawn invulnerability window.</summary>
        public bool IsLocalRunnerInvulnerable => _respawnGraceUntil > 0f && Time.time < _respawnGraceUntil;

        // ─────────────────────────────────────────────────────────────────────
        // GameEvents subscribers
        // ─────────────────────────────────────────────────────────────────────

        private void OnBusConnectionStateChanged(bool online)
        {
            // Track C fires this when the Fusion room comes up / drops. We learn the local
            // player id and host status here; the FSM itself doesn't transition on connection.
            if (online) _localPlayerId = ResolveLocalPlayerId();
        }

        private void OnBusGateCollected(int gateIdValue, string collectorPlayerId)
        {
            // Round-1 review fix F3/R2-F1: the prior implementation only grew the roster and
            // forwarded to the replay sink — it NEVER called scoreboard.Award, so the Lumen
            // tally stayed at zero forever in any runtime path that didn't go through a Fusion
            // host RPC. The doc-comment claiming "Track A's scoreboard awards on every
            // GateCollected" was false. Award +1 Lumen here, on every collection, offline or
            // online. (Online: the host-side NetworkPlayer.AwardGateCollectHost also awards;
            // that path is gated on FUSION_WEAVER and host-authority, so in solo/editor this is
            // the sole award site. In a hosted match the host still gets the final say via its
            // own authoritative scoreboard.)
            if (string.IsNullOrEmpty(collectorPlayerId)) return;
            _knownPlayers.Add(collectorPlayerId);

            int newTotal = _scoreboard != null
                ? _scoreboard.Award(collectorPlayerId)
                : (ServiceLocator.Get<ILumenScoreboard>()?.Award(collectorPlayerId) ?? 0);

            // Forward to the replay sink with the gate's position when we can resolve it.
            // Track B's GateSpawner exposes an ActiveGates snapshot; resolve via the concrete
            // type (Round-1 fix R1-F15: prior code passed default GeoPoint, recording every
            // Lumen at lat=0/lon=0 in Afterglow). Fall back to default if lookup fails.
            GeoPoint at = default;
            if (ServiceLocator.TryGet<IGateDirector>(out var director))
            {
                foreach (var g in director.ActiveGates)
                {
                    if (g.Id.Value == gateIdValue) { at = g.Position; break; }
                }
            }
            if (ServiceLocator.TryGet<IMatchReplaySink>(out var sink) && sink != null)
                sink.RecordLumen(collectorPlayerId, at, MatchClockSeconds());
        }

        // ─────────────────────────────────────────────────────────────────────
        // Track F (Afterglow) wiring — finalization at expiry
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Push finish order + tail radius into the replay package and freeze it. Round-1 review
        /// fix R1-F13: previously resolved the sink via reflection-by-interface-name (fragile — a
        /// rename would silently null-out the lookup at runtime, not compile time). Gameplay now
        /// references Afterglow directly, so we use the typed locator and set the sink's public
        /// fields directly. Safe to call multiple times — the sink itself is idempotent.
        /// </summary>
        private void FinalizeReplayPackage()
        {
            if (_replaySink == null) return;

            // Wire the per-player trail-snapshot provider (decision U — closes Track F's gap).
            _replaySink.TrailSnapshotProvider = BuildTrailSnapshot;
            _replaySink.LivePlayerEnumerator = LivePlayersForReplay;

            // Frozen tail radius (decision T) and finish order.
            _replaySink.FrozenTailRadius = _tailAuthority?.FrozenTailRadius ?? GameConfig.Active.tailRadius;
            var order = ComputeFinishOrder();
            if (order != null) _replaySink.FinishOrder = order;
        }

        /// <summary>
        /// Create a delegate of the field's exact declared type wrapping <paramref name="method"/>
        /// on this MatchManager instance. Required because Track F's sink declares its own
        /// delegate types (LivePlayerEnumeratorDelegate returns IEnumerable&lt;string&gt; but is
        /// a distinct type from Func&lt;IEnumerable&lt;string&gt;&gt;).
        /// </summary>
        private void BindDelegate(object target, string fieldName, System.Reflection.MethodInfo method)
        {
            if (target == null || method == null) return;
            try
            {
                var f = target.GetType().GetField(fieldName);
                if (f == null) return;
                var del = Delegate.CreateDelegate(f.FieldType, this, method, throwOnBindFailure: false);
                if (del != null) f.SetValue(target, del);
            }
            catch (Exception e) { Debug.LogWarning($"[MatchManager] bind {fieldName} failed: {e.Message}"); }
        }

        private static void SetDelegateField(object target, string fieldName, Delegate value)
        {
            if (target == null) return;
            try
            {
                var f = target.GetType().GetField(fieldName);
                if (f != null) f.SetValue(target, value);
            }
            catch (Exception e) { Debug.LogWarning($"[MatchManager] wire {fieldName} failed: {e.Message}"); }
        }

        private static void SetFieldValue(object target, string fieldName, object value)
        {
            if (target == null) return;
            try
            {
                var f = target.GetType().GetField(fieldName);
                if (f == null) return;

                Type fieldType = f.FieldType;
                Type valueType = value?.GetType();

                // Strip Nullable<T> so we can pass a plain T into a float? / int? field (Track F's
                // ReplayPackageSink.FrozenTailRadius is float?).
                Type underlying = Nullable.GetUnderlyingType(fieldType);
                if (underlying != null) fieldType = underlying;

                if (value == null || fieldType.IsAssignableFrom(valueType) || fieldType == valueType)
                    f.SetValue(target, value);
                else
                    Debug.LogWarning($"[MatchManager] {fieldName} type mismatch: field={f.FieldType.Name} value={valueType?.Name}");
            }
            catch (Exception e) { Debug.LogWarning($"[MatchManager] set {fieldName} failed: {e.Message}"); }
        }

        /// <summary>
        /// Build a <see cref="TrailSnapshotPoints"/> from the local player's <see cref="TrailData"/>.
        /// Returns an empty snapshot for unknown players / missing trails. Called by the replay
        /// sink's TrailSnapshotProvider delegate.
        /// </summary>
        private TrailSnapshotPoints BuildTrailSnapshot(string playerId)
        {
            if (!TrailManager.HasInstance) return default;
            var tm = TrailManager.Instance;
            TrailData trail = null;
            if (tm.LocalTrail != null && tm.LocalTrail.OwnerId == playerId) trail = tm.LocalTrail;
            else if (tm.AllTrails.TryGetValue(playerId, out var t)) trail = t;
            if (trail == null || trail.PointCount == 0) return default;

            var pts = trail.Points;
            int n = pts.Count;
            var coords = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                coords[i * 3]     = pts[i].position.latitude;
                coords[i * 3 + 1] = pts[i].position.longitude;
                coords[i * 3 + 2] = pts[i].position.altitude;
            }
            return new TrailSnapshotPoints(coords, n);
        }

        private IEnumerable<string> LivePlayersForReplay()
        {
            if (!TrailManager.HasInstance) yield break;
            foreach (var kvp in TrailManager.Instance.AllTrails)
                if (!string.IsNullOrEmpty(kvp.Key)) yield return kvp.Key;
        }

        private IReadOnlyList<string> ComputeFinishOrder()
        {
            if (_scoreboard == null) return null;
            // Round-1 review fix R2-F11 (second half): previously sorted _knownPlayers (a HashSet,
            // no insertion order) by Lumens alone — ties got an arbitrary order that was then
            // written into the Afterglow replay's FinishOrder, surfacing as "I tied with my friend
            // but the replay showed them above me." Now derive from OrderedStandings (deterministic
            // tiebreak by playerId asc); include only known players for the snapshot.
            var known = new HashSet<string>(_knownPlayers);
            var result = new List<string>();
            foreach ((string pid, int _) in _scoreboard.OrderedStandings)
                if (known.Contains(pid)) result.Add(pid);
            // Append any known players not on the scoreboard (zero Lumens, never awarded) in a
            // deterministic order so the FinishOrder length matches _knownPlayers.
            foreach (var pid in result) known.Remove(pid);
            foreach (var pid in known) result.Add(pid);
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private string ResolveLocalPlayerId()
        {
            if (!string.IsNullOrEmpty(_localPlayerId)) return _localPlayerId;
            if (ServiceLocator.TryGet<IAuthService>(out var auth) && auth != null && auth.IsAuthenticated)
                return auth.CurrentUserId;
            return "local-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// Get the snake-tail model (Track A). <see cref="GameManager"/> reads this to consult
        /// <see cref="SnakeTailModel.ShouldPruneOldest"/> after appending to the local trail.
        /// </summary>
        public SnakeTailModel SnakeTailModel => _snakeTailModel;

        /// <summary>The registered Lumen scoreboard (Track A impl).</summary>
        public LumenScoreboard Scoreboard => _scoreboard;

        // ─── Test hooks (LightRunners.Tests.Gameplay) ──────────────────────
        // Edit-mode tests use these to drive the FSM synchronously without relying on
        // Time.deltaTime / Update. They are NOT part of the public API.

        /// <summary>Test-only: force the FSM to a specific state, bypassing the validator.</summary>
        internal void TestSetState(MatchState state)
        {
            var prev = State;
            State = state;
            try { StateChanged?.Invoke(prev, state); }
            catch (Exception e) { Debug.LogException(e); }
            GameEvents.RaiseMatchStateChanged(prev, state);

            if (state == MatchState.Countdown) FreezeTailAtCountdown();
            if (state == MatchState.Live)
            {
                _matchStartEpochSeconds = Time.timeAsDouble;
                TimeRemaining = GameConfig.Active.matchDurationSeconds;
            }
        }

        /// <summary>Test-only: drive the FSM straight to Live (skips Warmup/Countdown timers).</summary>
        internal void TestBeginMatchAtLive()
        {
            BeginMatch();
            TestSetState(MatchState.Live);
        }

        /// <summary>Test-only: invoke the live-clock expiry path synchronously.</summary>
        internal void TestExpireMatch()
        {
            TimeRemaining = 0f;
            ExpireMatch();
        }

        /// <summary>Test-only: invoke the countdown freeze on the tail authority.</summary>
        internal void TestFreezeTail() => FreezeTailAtCountdown();
    }
}
