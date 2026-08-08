using System;
using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Identity;
using LightRunners.Trail;
using LightRunners.Afterglow;
using LightRunners.Backend;
using LightRunners.Lightfield;
using LightRunners.Location;

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
    ///    the frozen-config validator fires on entry to Countdown.
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
        [Tooltip("Minimum horizontal distance (metres) the runner must move away from the crash site before collision rearms.")]
        [SerializeField] private float respawnBackOffsetMeters = 5f;
        [Tooltip("Brief invulnerability (s) after a respawn so the trailing-into-own-tail case can't instantly re-crash.")]
        [SerializeField] private float respawnGraceSeconds = 2f;

        // ─── IMatchSession surface ──────────────────────────────────────────
        public string MatchId => _replaySink?.Package?.MatchId ?? string.Empty;
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
                    // A failed/timed-out/disconnected transport means this process is running
                    // the documented solo fallback. It must remain authoritative locally even
                    // when a dormant FusionLauncher still owns the locator slot.
                    if (!transport.IsConnected) return true;

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
        private readonly Dictionary<string, float> _respawnReadyAt = new Dictionary<string, float>();
        private readonly List<string> _readyRespawns = new List<string>();
        private GeoPoint _localRespawnSite;
        private bool _hasLocalRespawnSite;
        private Guid _backendMatchId;
        private bool _persistenceSubmitted;

        // Scoreboard / tail authority are constructed here (Track A impls land in integration).
        private LumenScoreboard _scoreboard;
        private TailAuthority _tailAuthority;
        private SnakeTailModel _snakeTailModel;
        // Track B gate director + lightfield volume — constructed in Awake (Round-2 fix R2-F1).
        private LightfieldVolume _lightfieldVolume;
        private GateSpawner _gateDirector;
        // Track F replay sink — constructed in Awake (Round-1 review fix).
        private ReplayPackageSink _replaySink;

        /// <summary>Match-relative time in seconds (0 outside a live match).</summary>
        private double MatchClockSeconds()
        {
            if (_matchStartEpochSeconds < 0) return 0.0;
            return Time.timeAsDouble - _matchStartEpochSeconds;
        }

        /// <summary>
        /// Resolve a referee-token validator Func for the GateSpawner (Round-2 fix R2-F8). Returns
        /// a delegate that calls <c>RefereeTokenValidator.Validate(token, matchId, secret)</c> if
        /// the Multiplayer assembly + type is reachable; falls back to a non-empty check otherwise.
        /// Reflective so this call site doesn't depend on FUSION_WEAVER at compile time.
        /// </summary>
        private static Func<string, bool> ResolveRefereeTokenValidator()
        {
            try
            {
                var t = System.Type.GetType("LightRunners.Multiplayer.RefereeTokenValidator, LightRunners.Multiplayer");
                if (t == null) return null; // GateSpawner substitutes its default non-empty check.
                var validate = t.GetMethod("Validate", new[] { typeof(string), typeof(string), typeof(string) });
                if (validate == null) return null;
                // The validator needs (token, matchId, secret). MatchId is known once a match
                // starts; secret comes from the host. For the milestone we resolve both lazily on
            // each call: matchId from the registered IMatchSession if available, secret from a
                // host-side config field. If secret is unset, fail-closed (return false) so a
                // misconfigured host can't mint tokens.
                return token =>
                {
                    if (string.IsNullOrEmpty(token)) return false;
                    string matchId = ServiceLocator.Get<IMatchSession>()?.MatchId;
                    string secret = GameConfig.Active.refereeTokenSecret;
                    if (string.IsNullOrEmpty(secret)) return false; // fail-closed
                    return (bool)validate.Invoke(null, new object[] { token, matchId ?? string.Empty, secret });
                };
            }
            catch { return null; }
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

            // Construct & register the real Track B impls (Round-2 review fix R2-F1: previously
            // MatchManager.Awake registered only LumenScoreboard/TailAuthority/IMatchSession/
            // IMatchReplaySink — but NOT IGateDirector or ILightfieldVolume. The Null* stubs from
            // PlatformServiceRegistry remained live, so ConfigureForPlayers and CheckPlayer were
            // no-ops. The entire gate/Lumen loop was dead in production despite the Round-1 fix
            // claims. Construct both here and set the Lightfield origin from the local GPS.
            _lightfieldVolume = new LightfieldVolume();
            _lightfieldVolume.BoundaryViolated += OnBoundaryViolated;
            if (LocationProvider.HasInstance)
                _lightfieldVolume.SetOrigin(LocationProvider.Instance.CurrentPosition);
            ServiceLocator.Register<ILightfieldVolume>(_lightfieldVolume);
            // Round-2 fix R2-F8: inject a referee-token validator into the GateSpawner so
            // PlaceBonusGate validates the token regardless of caller (Round-1 fix added the
            // ctor param but the production construction site used the default non-empty check).
            // The validator resolves the host-issued secret from a RefereeTokenValidator
            // (Multiplayer) — looked up reflectively so Gameplay doesn't take a hard Multiplayer
            // type dependency at this call site (the asmdef already references Multiplayer for
            // the gated Fusion blocks; reflective keeps the validator optional at runtime).
            _gateDirector = new GateSpawner(_lightfieldVolume, refereeTokenValidator: ResolveRefereeTokenValidator());
            ServiceLocator.Register<IGateDirector>(_gateDirector);

            // Construct & register the real Track F replay sink (Round-1 review fix F1: this was
            // never constructed, so the locator kept NullMatchReplaySink and Afterglow was always
            // empty). Gameplay references Afterglow directly now (asmdef updated) so we can drop
            // the prior reflection-based lookup.
            ResetReplaySink();

            // Keep the generated scene reproducible, but do not require regeneration for a
            // normal checkout: mount the two runtime presenters when they are absent.
            if (FindAnyObjectByType<LumenGateVisualizer>() == null)
                gameObject.AddComponent<LumenGateVisualizer>();
            if (FindAnyObjectByType<StolenLumenPickupSpawner>() == null)
                gameObject.AddComponent<StolenLumenPickupSpawner>();

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

        protected override void OnDestroy()
        {
            GameEvents.ConnectionStateChanged -= OnBusConnectionStateChanged;
            GameEvents.GateCollected -= OnBusGateCollected;
            if (_lightfieldVolume != null)
                _lightfieldVolume.BoundaryViolated -= OnBoundaryViolated;
            try { _gateDirector?.Dispose(); } catch { /* idempotent */ }
            try { _replaySink?.UnbindFromEventBus(); } catch { /* idempotent */ }
            RestoreNullServicesIfOwned();
            base.OnDestroy();
        }

        private void RestoreNullServicesIfOwned()
        {
            if (ReferenceEquals(ServiceLocator.Get<IMatchSession>(), this))
                ServiceLocator.Register<IMatchSession>(new NullMatchSession());
            if (ReferenceEquals(ServiceLocator.Get<ILumenScoreboard>(), _scoreboard))
                ServiceLocator.Register<ILumenScoreboard>(new NullLumenScoreboard());
            if (ReferenceEquals(ServiceLocator.Get<ITailAuthority>(), _tailAuthority))
                ServiceLocator.Register<ITailAuthority>(new NullTailAuthority());
            if (ReferenceEquals(ServiceLocator.Get<IGateDirector>(), _gateDirector))
                ServiceLocator.Register<IGateDirector>(new NullGateDirector());
            if (ReferenceEquals(ServiceLocator.Get<ILightfieldVolume>(), _lightfieldVolume))
                ServiceLocator.Register<ILightfieldVolume>(new NullLightfieldVolume());
            if (ReferenceEquals(ServiceLocator.Get<IMatchReplaySink>(), _replaySink))
                ServiceLocator.Register<IMatchReplaySink>(new NullMatchReplaySink());
        }

        private void ResetReplaySink()
        {
            try { _replaySink?.UnbindFromEventBus(); } catch { /* idempotent */ }
            _replaySink = new ReplayPackageSink();
            _replaySink.BindToEventBus();
            ServiceLocator.Register<IMatchReplaySink>(_replaySink);
        }

        private void Update()
        {
            CompleteReadyRespawns();
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

            // Reject an illegal room config before leaving Idle, so the host can correct the
            // setting and retry instead of becoming stranded in Warmup.
            if (!FrozenMatchConfig.TryCreateFromMeters(
                    GameConfig.Active.tailRadius,
                    out _,
                    out string configError))
            {
                Debug.LogError($"[MatchManager] Cannot begin match: {configError}");
                return;
            }

            // Resolve local player + reset state.
            _localPlayerId = ResolveLocalPlayerId();
            _knownPlayers.Clear();
            _respawnReadyAt.Clear();
            _lightfieldVolume?.Clear();
            RegisterPlayer(_localPlayerId);
            if (_scoreboard != null) _scoreboard.Reset();
            if (_tailAuthority != null) _tailAuthority.Unfreeze();
            if (_lightfieldVolume != null && LocationProvider.HasInstance)
                _lightfieldVolume.SetOrigin(LocationProvider.Instance.CurrentPosition);
            ResetReplaySink();
            _hasLocalRespawnSite = false;
            PrepareBackendMatchPersistence();

            TransitionTo(MatchState.Warmup);

            // Auto-advance into Countdown immediately for the milestone. A real Warmup screen
            // would wait on a host "ready" signal; the milestone ships the simplest viable flow.
            TransitionTo(MatchState.Countdown);
        }

        public void RegisterPlayer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            bool added = _knownPlayers.Add(playerId);
            if (added && State == MatchState.Live && IsHostAuthority)
                ConfigureGatesForLive();
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

            // Freeze and validate before mutating the FSM. An illegal host selection must not
            // create a room whose clients disagree about collision geometry.
            if (next == MatchState.Countdown && !TryFreezeTailAtCountdown(out string freezeError))
            {
                Debug.LogError($"[MatchManager] Cannot enter Countdown: {freezeError}");
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
                    // The config was frozen and validated before the state mutation above.
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
                    SubmitBackendMatchResultsOrDefer();
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

        /// <summary>Decision T — validate and freeze the full collision contract.</summary>
        private bool TryFreezeTailAtCountdown(out string error)
        {
            bool frozen;
            if (_tailAuthority != null)
                frozen = _tailAuthority.TryFreezeAtCountdown(out error);
            else if (ServiceLocator.TryGet<ITailAuthority>(out var auth) && auth != null)
                frozen = auth.TryFreezeAtCountdown(out error);
            else
            {
                error = "No tail authority is registered.";
                return false;
            }

            if (frozen && IsHostAuthority)
                PublishFrozenConfigToNetwork();
            return frozen;
        }

        private void PublishFrozenConfigToNetwork()
        {
            // Mirror: resolve the MirrorNetworkMatchState from the scene and publish
            // the frozen tail radius. Uses reflection so Gameplay doesn't take a
            // hard dependency on the Multiplayer assembly.
            var networkState = FindAnyObjectByType(
                System.Type.GetType("LightRunners.Multiplayer.MirrorNetworkMatchState, LightRunners.Multiplayer"));
            if (networkState != null)
            {
                var method = networkState.GetType().GetMethod("HostSetFrozenTailRadius");
                if (method != null)
                {
                    float radius = (_tailAuthority?.FrozenConfig ?? FrozenMatchConfig.Default).TailRadiusMeters;
                    method.Invoke(networkState, new object[] { radius });
                }
            }
        }

        /// <summary>Decision M — configure the gate pool for the live player count.</summary>
        private void ConfigureGatesForLive()
        {
            int players = _knownPlayers.Count;
            if (TrailManager.HasInstance)
            {
                foreach (string playerId in TrailManager.Instance.AllTrails.Keys)
                    if (!string.IsNullOrEmpty(playerId)) _knownPlayers.Add(playerId);
                players = Mathf.Max(players, TrailManager.Instance.LivePlayerCount);
            }
            players = Mathf.Max(1, players);
            float gatesPerPlayer = GameConfig.Active.gatesPerPlayer;
            if (ServiceLocator.TryGet<IGateDirector>(out var director) && director != null)
                director.ConfigureForPlayers(players, gatesPerPlayer);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Backend match persistence
        // ─────────────────────────────────────────────────────────────────────

        private void PrepareBackendMatchPersistence()
        {
            _backendMatchId = Guid.Empty;
            _persistenceSubmitted = false;

            if (_replaySink == null) return;
            if (!Guid.TryParse(_replaySink.Package.MatchId, out Guid desiredMatchId))
            {
                Debug.LogError($"[MatchManager] Replay match id is not a UUID: {_replaySink.Package.MatchId}");
                return;
            }
            // Keep the replay UUID as the eventual backend identity, but do not create an open
            // server row mid-match. Expiry submits one durable transaction that creates, writes,
            // and closes the match; an app kill before expiry therefore leaves no orphan row.
            _backendMatchId = desiredMatchId;
        }

        private void SubmitBackendMatchResultsOrDefer()
        {
            if (_persistenceSubmitted || !IsHostAuthority || !PlayerRepository.HasInstance)
                return;
            if (_backendMatchId == Guid.Empty) return;

            _persistenceSubmitted = true;
            var finishOrder = ComputeFinishOrder();
            var results = new List<MatchResultWrite>(finishOrder.Count);
            int previousLumens = int.MinValue;
            int currentRank = 0;
            for (int i = 0; i < finishOrder.Count; i++)
            {
                string playerId = finishOrder[i];
                int lumens = _scoreboard?.GetLumens(playerId) ?? 0;
                if (i == 0 || lumens != previousLumens) currentRank = i + 1;
                previousLumens = lumens;
                string role = playerId == _localPlayerId ? "host" : "runner";
                results.Add(new MatchResultWrite(playerId, lumens, currentRank, role));
            }

            // A tied maximum has no single winner. LumenScoreboard exposes empty in that case;
            // do not promote the deterministic replay tiebreaker into an authoritative winner.
            string winnerPlayerId = _scoreboard?.LeaderPlayerId ?? string.Empty;
            int durationSeconds = Math.Max(0, (int)Math.Round(MatchClockSeconds()));
            string roomId = GameManager.HasInstance
                ? GameManager.Instance.ResolveRoomName()
                : string.Empty;
            PlayerRepository.Instance.FinalizeMatchWithResults(
                _backendMatchId,
                roomId,
                _localPlayerId,
                results,
                winnerPlayerId,
                durationSeconds,
                onSuccess: null,
                onError: error => Debug.LogWarning($"[MatchManager] finalize_match failed: {error}"));
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
                // Early host forfeit still closes the replay/backend row. The FSM may skip the
                // public Scoring state, but finalization semantics remain exactly-once.
                FinalizeReplayPackage();
                SubmitBackendMatchResultsOrDefer();
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
            if (State != MatchState.Live || !IsHostAuthority) return;
            if (string.IsNullOrEmpty(playerId)) return;
            // A runner remains crashed until its grace window completes. Ignore duplicate
            // detector/RPC signals during that window so one physical collision cannot apply
            // multiple penalties or emit multiple replay records.
            if (_respawnReadyAt.ContainsKey(playerId)) return;
            RegisterPlayer(playerId);

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

            // Record the one authoritative crash after its penalty metadata is known.
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

            // Begin respawn. GameManager marks a trail discontinuity immediately; collision
            // stays disabled until the grace time and physical clearance checks both pass.
            float readyAt = Time.time + respawnGraceSeconds;
            _respawnReadyAt[playerId] = readyAt;
            if (playerId == _localPlayerId)
            {
                _localRespawnSite = at;
                _hasLocalRespawnSite = true;
            }
            try { RespawnRequested?.Invoke(playerId, at); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private void CompleteReadyRespawns()
        {
            if (_respawnReadyAt.Count == 0) return;
            _readyRespawns.Clear();
            foreach (var entry in _respawnReadyAt)
            {
                if (Time.time < entry.Value) continue;
                if (entry.Key == _localPlayerId && !HasLocalRunnerReachedRespawnClearance())
                    continue;
                _readyRespawns.Add(entry.Key);
            }

            foreach (string playerId in _readyRespawns)
            {
                _respawnReadyAt.Remove(playerId);
                if (playerId == _localPlayerId) _hasLocalRespawnSite = false;
                GameEvents.RaisePlayerRespawned(playerId);
            }
        }

        private bool HasLocalRunnerReachedRespawnClearance()
        {
            if (!_hasLocalRespawnSite || !LocationProvider.HasInstance) return true;
            FrozenMatchConfig frozen = _tailAuthority?.FrozenConfig ?? FrozenMatchConfig.Default;
            float requiredMeters = Mathf.Max(
                respawnBackOffsetMeters,
                frozen.HeadToTrailCollisionMeters + 1f);
            return HasReachedRespawnClearance(
                _localRespawnSite,
                LocationProvider.Instance.CurrentPosition,
                requiredMeters);
        }

        internal static bool HasReachedRespawnClearance(GeoPoint crashSite, GeoPoint current, float requiredMeters)
            => current.HorizontalDistanceTo(crashSite) >= Math.Max(0f, requiredMeters);

        /// <summary>
        /// True from the crash until both the grace time and safe horizontal clearance have
        /// been reached. Remaining still at the collision site cannot trigger a chain crash.
        /// </summary>
        public bool IsLocalRunnerInvulnerable
            => !string.IsNullOrEmpty(_localPlayerId) && _respawnReadyAt.ContainsKey(_localPlayerId);

        // ─────────────────────────────────────────────────────────────────────
        // GameEvents subscribers
        // ─────────────────────────────────────────────────────────────────────

        private void OnBusConnectionStateChanged(bool online)
        {
            // Track C fires this when the Fusion room comes up / drops. We learn the local
            // player id and host status here; the FSM itself doesn't transition on connection.
            if (online) _localPlayerId = ResolveLocalPlayerId();
        }

        private void OnBoundaryViolated(string playerId)
        {
            if (State == MatchState.Live && IsHostAuthority)
                GameEvents.RaiseBoundaryViolated(playerId);
        }

        private void OnBusGateCollected(int gateIdValue, string collectorPlayerId, GeoPoint at)
        {
            // Round-2 fix R2-F7: guard on Live state. Without this, a stray physics
            // OnTriggerEnter in the window between ExpireMatch and the gate GameObjects being
            // destroyed would still mutate the authoritative tally — breaking Decision O's
            // "most Lumens wins on expiry" invariant.
            if (State != MatchState.Live || !IsHostAuthority) return;

            // Round-1 review fix F3/R2-F1: the prior implementation only grew the roster and
            // forwarded to the replay sink — it NEVER called scoreboard.Award, so the Lumen
            // tally stayed at zero forever in any runtime path that didn't go through a Fusion
            // host RPC. The doc-comment claiming "Track A's scoreboard awards on every
            // GateCollected" was false. Award +1 Lumen here, on every collection, offline or
            // online. The host-side NetworkPlayer validates network requests and raises this
            // event; it does not mutate the scoreboard itself, keeping this as the sole award
            // site.
            if (string.IsNullOrEmpty(collectorPlayerId)) return;
            RegisterPlayer(collectorPlayerId);

            if (_scoreboard != null)
                _scoreboard.Award(collectorPlayerId);
            else
                ServiceLocator.Get<ILumenScoreboard>()?.Award(collectorPlayerId);

            // The collection position travels with the event. Looking it up here is too late:
            // GateSpawner removes the collected gate before later bus subscribers run.
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

            // Exact frozen collision contract (decision T) and finish order are plain fields.
            FrozenMatchConfig frozen = _tailAuthority?.FrozenConfig ?? FrozenMatchConfig.Default;
            _replaySink.FrozenTailRadius = frozen.TailRadiusMeters;
            _replaySink.FrozenPlayerHeadRadiusCm = FrozenMatchConfig.PlayerHeadRadiusCm;
            _replaySink.FrozenConfigHash = frozen.Hash;
            if (_lightfieldVolume != null)
                _replaySink.Package.SetOrigin(_lightfieldVolume.Origin);
            var order = ComputeFinishOrder();
            if (order != null) _replaySink.FinishOrder = order;
            // Freeze before MatchExpired observers run so UI/Afterglow always see a complete,
            // immutable package regardless of static-event subscription order.
            _replaySink.Freeze();
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
            if (TrailManager.HasInstance)
                foreach (string playerId in TrailManager.Instance.AllTrails.Keys)
                    if (!string.IsNullOrEmpty(playerId)) known.Add(playerId);
            var result = new List<string>();
            foreach ((string pid, int _) in _scoreboard.OrderedStandings)
                if (known.Contains(pid)) result.Add(pid);
            // Append any known players not on the scoreboard (zero Lumens, never awarded) in a
            // deterministic order so the FinishOrder length matches _knownPlayers.
            foreach (var pid in result) known.Remove(pid);
            var zeroScorePlayers = new List<string>(known);
            zeroScorePlayers.Sort(StringComparer.Ordinal);
            result.AddRange(zeroScorePlayers);
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

            if (state == MatchState.Countdown) TryFreezeTailAtCountdown(out _);
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
        internal void TestFreezeTail() => TryFreezeTailAtCountdown(out _);
    }
}
