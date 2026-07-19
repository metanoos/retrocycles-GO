using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;
using LightRunners.Identity;
using LightRunners.Backend;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Owns the run state machine and orchestrates every runtime system (spec §2.3, §16).
    /// HUD visibility, button enablement, and the crash pipeline all derive from state
    /// transitions and the <see cref="GameEvents"/> bus.
    ///
    /// Keeps a fallback <see cref="TrailCollisionDetector"/> (spec §8.4) so a run is crashable
    /// even when Fusion fails to connect. On collision it raises the same
    /// <see cref="GameEvents.RaisePlayerCrashed"/> so the crash pipeline is identical
    /// regardless of network state.
    ///
    /// ─── TRACK D CHANGES (Lightfield match migration, 2026-07-18) ──────────
    /// The match lifecycle has been delegated to <see cref="MatchManager"/> (decision P).
    /// Specifically:
    ///   • <see cref="StartRun"/> additionally calls <see cref="MatchManager.BeginMatch"/> so
    ///     the match sub-FSM (Idle→Warmup→Countdown→Live) runs in lockstep with this app-level
    ///     Running state. The match lives ON TOP of <see cref="GameState"/>, not in place of it.
    ///   • Direct <c>FusionLauncher.Connect/Disconnect</c> calls are replaced by
    ///     <c>IMatchTransport</c> resolved from the <see cref="ServiceLocator"/> (decision Q).
    ///     Track C's real <c>FusionLauncher</c> overwrites the locator slot when the runner
    ///     comes up; in editor-only mode the <c>NullMatchTransport</c> is used (no-op).
    ///   • Crash is no longer terminal (decision F): <see cref="OnPlayerCrashed"/> delegates to
    ///     <see cref="MatchManager.HandlePlayerCrash"/> (penalty + respawn) instead of
    ///     <see cref="FinalizeRun"/>. <see cref="FinalizeRun"/> is retained for voluntary
    ///     end-of-match (<see cref="EndRun"/>) and app-lifecycle grace expiry.
    ///   • <see cref="OnPositionUpdate"/> consults <see cref="SnakeTailModel"/> from
    ///     <see cref="MatchManager"/> after each local-trail append (Track A's snake-tail rule,
    ///     decision B).
    /// </summary>
    public class GameManager : Singleton<GameManager>
    {
        [Header("Run Bookkeeping")]
        [SerializeField] private BeaconFormType startForm = BeaconFormType.Hoverboard;

        [Header("Fallback Collision (spec §8.4)")]
        [SerializeField] private TrailCollisionDetector fallbackDetector;

        [Header("Refs")]
        [SerializeField] private CrashSequence crashSequence;

        // State
        public GameState State { get; private set; } = GameState.Initializing;
        public ViewMode ViewMode { get; private set; } = ViewMode.Map;
        public BeaconFormType CurrentForm { get; private set; } = BeaconFormType.Hoverboard;
        public string LocalPlayerId { get; private set; }

        /// <summary>
        /// null = no connection attempt this run; true = in a Photon room; false = attempt
        /// failed or dropped (solo "offline race", spec §8.1). HUD badge reads this.
        /// </summary>
        public bool? OnlineRace { get; private set; }

        public event Action<GameState, GameState> OnStateChanged;
        public event Action<ViewMode> OnViewModeChanged;

        // Crash pipeline double-fire guard (spec §16).
        private bool _crashPipelineFired;

        // Last movement (for fallback collision check).
        private GeoPoint _lastPos;
        private bool _haveLastPos;
        private double _runStartTimestamp = -1.0;

        // Proximity scoring (spec §7.4, pitfall #17): sampled during the run, peak kept.
        private int _peakNearby;
        private float _proximityTimer;

        // TEMP DIAGNOSTIC: throttle for the trail-recording logs above.
        private float _lastStateLog = -1f;

        // App lifecycle (spec §20).
        private DateTime _pausedAtUtc;

        // Trail persistence (spec §3.1: TrailRepository is a component on this GO).
        private TrailRepository _trailRepository;

        private Coroutine _connectRoutine;

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────
        protected override void Awake()
        {
            base.Awake();
            // Spec §3.1: TrailRepository lives on the GameManager GO and is locator-registered.
            _trailRepository = GetComponent<TrailRepository>();
            if (_trailRepository == null) _trailRepository = gameObject.AddComponent<TrailRepository>();
            ServiceLocator.TryRegister(_trailRepository);

            if (fallbackDetector == null)
            {
                fallbackDetector = gameObject.AddComponent<TrailCollisionDetector>();
                fallbackDetector.SetFallback();
            }
        }

        protected virtual void OnEnable()
        {
            GameEvents.PlayerCrashed += OnPlayerCrashed;
            GameEvents.ConnectionStateChanged += OnConnectionStateChanged;
            if (fallbackDetector != null) fallbackDetector.OnCollisionDetected += OnFallbackCollision;
            if (MatchManager.HasInstance)
                MatchManager.Instance.RespawnRequested += OnRespawnRequested;
        }

        protected virtual void OnDisable()
        {
            GameEvents.PlayerCrashed -= OnPlayerCrashed;
            GameEvents.ConnectionStateChanged -= OnConnectionStateChanged;
            if (fallbackDetector != null) fallbackDetector.OnCollisionDetected -= OnFallbackCollision;
            if (MatchManager.HasInstance)
                MatchManager.Instance.RespawnRequested -= OnRespawnRequested;
        }

        protected virtual void Start()
        {
            // Decide initial state by scene. If an IAuthService is registered, start at Lobby
            // (Game scene); otherwise Login (Login scene — handled by LoginUI).
            if (ServiceLocator.TryGet<IAuthService>(out var auth) && auth != null && auth.IsAuthenticated)
            {
                LocalPlayerId = auth.CurrentUserId;
                SetState(GameState.Lobby);
            }
            else
            {
                SetState(GameState.Login);
            }

            // Wire location updates once LocationProvider is up.
            if (LocationProvider.HasInstance)
                LocationProvider.Instance.OnPositionUpdated += OnPositionUpdate;
        }

        protected override void OnDestroy()
        {
            if (LocationProvider.HasInstance)
                LocationProvider.Instance.OnPositionUpdated -= OnPositionUpdate;
            base.OnDestroy();
        }

        private void Update()
        {
            if (State != GameState.Running) return;

            // Proximity axis input (spec §7.4): every proximitySampleInterval, count runners
            // within proximityRadius and keep the peak. Never sampled at end-of-run only.
            _proximityTimer -= Time.deltaTime;
            if (_proximityTimer <= 0f)
            {
                GameConfig cfg = GameConfig.Active;
                _proximityTimer = cfg.proximitySampleInterval;
                if (TrailManager.HasInstance && LocationProvider.HasInstance)
                {
                    int n = TrailManager.Instance.CountPlayersNear(
                        LocationProvider.Instance.CurrentPosition, cfg.proximityRadius, LocalPlayerId);
                    if (n > _peakNearby) _peakNearby = n;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // App lifecycle (spec §20): backgrounding mid-run pauses; grace then auto-end.
        // ─────────────────────────────────────────────────────────────────────
        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                if (State != GameState.Running) return;
                _pausedAtUtc = DateTime.UtcNow;
                SetState(GameState.Paused);
            }
            else
            {
                if (State != GameState.Paused) return;
                double away = (DateTime.UtcNow - _pausedAtUtc).TotalSeconds;
                if (away <= GameConfig.Active.backgroundGraceSeconds)
                {
                    // Resume: never bridge the gap with a segment; keep wall-clock duration.
                    if (TrailManager.HasInstance)
                    {
                        TrailManager.Instance.MarkDiscontinuity();
                        TrailManager.Instance.AdjustRunStart(away);
                    }
                    _haveLastPos = false; // next fix starts a fresh movement segment
                    SetState(GameState.Running);
                }
                else
                {
                    FinalizeRun(crashed: false, causedBy: null);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // State machine
        // ─────────────────────────────────────────────────────────────────────
        public void SetState(GameState next)
        {
            if (State == next) return;
            var prev = State;
            State = next;
            Debug.Log($"[GameManager] State {prev} → {next}");

            // Wake lock (spec §20): a GPS game dies with the screen.
            if (next == GameState.Running)
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
            else if (next != GameState.Paused)
                Screen.sleepTimeout = SleepTimeout.SystemSetting;

            OnStateChanged?.Invoke(prev, next);
            GameEvents.RaiseGameStateChanged(prev, next);

            switch (next)
            {
                case GameState.Lobby:
                    _crashPipelineFired = false;
                    break;
                case GameState.Running:
                    _crashPipelineFired = false;
                    break;
            }
        }

        public void SetViewMode(ViewMode mode)
        {
            if (ViewMode == mode) return;
            ViewMode = mode;
            OnViewModeChanged?.Invoke(mode);
            GameEvents.RaiseViewModeChanged(mode);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public actions (HUD calls these)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Start-run entry point for UI. Gates on the one-time safety disclaimer (spec §23)
        /// before delegating to <see cref="StartRun"/>.
        /// </summary>
        public void RequestStartRun()
        {
            if (!SafetyDisclaimerUI.Acknowledged && SafetyDisclaimerUI.HasInstance)
            {
                SafetyDisclaimerUI.Instance.Show(onAcknowledged: StartRun);
                return;
            }
            StartRun();
        }

        /// <summary>
        /// Begin a run. Enters <see cref="GameState.Starting"/> (the async connect window,
        /// spec §2.3); the run proceeds — online or solo — within connectTimeoutSeconds.
        /// Idempotent for the same form (delegates to TrailManager.StartRun).
        /// </summary>
        public void StartRun()
        {
            if (State != GameState.Lobby && State != GameState.PartyLobby && State != GameState.Starting)
                return;

            LocalPlayerId = ResolveLocalPlayerId();
            CurrentForm = ResolveStartForm();
            Color color = ResolveTrailColor(CurrentForm);

            SetState(GameState.Starting);
            OnlineRace = null;
            _peakNearby = 0;
            _proximityTimer = 0f;

            if (LocationProvider.HasInstance)
            {
                var pos = LocationProvider.Instance.CurrentPosition;
                // Reference lifetime = one run (spec §5.1): every run re-origins world space.
                CoordinateConverter.SetReference(pos.latitude, pos.longitude);
            }

            if (TrailManager.HasInstance)
            {
                TrailManager.Instance.StartRun(LocalPlayerId, CurrentForm, color);
                Debug.Log($"[GameManager] TrailManager.StartRun called: playerId={LocalPlayerId} form={CurrentForm} color={color} → localTrail?={TrailManager.Instance.LocalTrail != null} allTrails={TrailManager.Instance.AllTrails.Count}");
            }
            else
            {
                Debug.LogWarning("[GameManager] TrailManager.HasInstance is FALSE at StartRun — no trail will be recorded!");
            }

            _runStartTimestamp = Time.timeAsDouble;
            _haveLastPos = false;
            if (fallbackDetector != null)
            {
                fallbackDetector.BeginRun(LocationProvider.HasInstance
                    ? LocationProvider.Instance.CurrentPosition
                    : default);
            }

            // Decision Q: connect through the locator-resolved IMatchTransport instead of
            // reaching into the Multiplayer assembly directly. The concrete FusionLauncher
            // (Track C) overwrites the locator slot when the runner comes up; in editor-only
            // mode the NullMatchTransport is a no-op and we proceed solo immediately.
            if (ServiceLocator.TryGet<IMatchTransport>(out var transport) && transport != null)
            {
                if (_connectRoutine != null) StopCoroutine(_connectRoutine);
                _connectRoutine = StartCoroutine(CoConnectThenRun(transport));
                return;
            }
            BeginRunning();
        }

        /// <summary>
        /// Async connect window: race ConnectMatch against connectTimeoutSeconds, then run
        /// either way (spec §8.1 — never block Start Run on the network). Observes
        /// <see cref="GameEvents.ConnectionStateChanged"/> for completion rather than a
        /// transport-specific callback.
        /// </summary>
        private IEnumerator CoConnectThenRun(IMatchTransport transport)
        {
            string room = ResolveRoomName();
            bool done = false;
            Action<bool> onConn = _ => done = true;
            GameEvents.ConnectionStateChanged += onConn;

            try
            {
                transport.ConnectMatch(room, LocalPlayerId);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameManager] IMatchTransport.ConnectMatch threw: {e.Message}");
                done = true;
            }

            float deadline = Time.realtimeSinceStartup + GameConfig.Active.connectTimeoutSeconds;
            while (!done && Time.realtimeSinceStartup < deadline)
                yield return null;

            GameEvents.ConnectionStateChanged -= onConn;

            // OnlineRace is set by the ConnectionStateChanged event; on timeout it stays null
            // and the run proceeds solo with the §8.4 fallback detector.
            if (!done) Debug.LogWarning("[GameManager] Connect timed out — starting solo (offline race).");
            _connectRoutine = null;
            BeginRunning();
        }

        private void BeginRunning()
        {
            if (State != GameState.Starting) return; // user may have bailed

            if (_trailRepository != null && TrailManager.HasInstance && TrailManager.Instance.LocalTrail != null)
                _trailRepository.BeginRun(TrailManager.Instance.LocalTrail, ResolveRoomName());

            SetState(GameState.Running);

            // Decision P: start the match sub-FSM. MatchManager owns the Lightfield match
            // lifecycle (Warmup/Countdown/Live/Scoring/Expired) on top of this GameState.
            if (MatchManager.HasInstance)
                MatchManager.Instance.BeginMatch();
        }

        /// <summary>
        /// The single matchmaking primitive (spec §8.1): a friend-match room name when a lobby
        /// is active, else the geographic zone room from the current fix.
        /// </summary>
        public string ResolveRoomName()
        {
            if (ServiceLocator.TryGet<ILobbyService>(out var lobby) && lobby != null
                && !string.IsNullOrEmpty(lobby.ActiveRoomName))
                return lobby.ActiveRoomName;

            GeoPoint pos = LocationProvider.HasInstance ? LocationProvider.Instance.CurrentPosition : default;
            return ZoneRoomName(pos);
        }

        /// <summary>Anonymous room-name scheme: <c>zone_{floor(lat·10)/10}_{floor(lon·10)/10}</c> (spec §8.1).</summary>
        public static string ZoneRoomName(GeoPoint pos)
        {
            double latCell = Math.Floor(pos.latitude * 10.0) / 10.0;
            double lonCell = Math.Floor(pos.longitude * 10.0) / 10.0;
            return StringUtils.FormatInvariant("zone_{0:0.0}_{1:0.0}", latCell, lonCell);
        }

        /// <summary>
        /// Voluntarily end the run (End Run button, visible only in Running). In match mode
        /// (decision P), this delegates to <see cref="MatchManager.EndMatch"/> which drives
        /// the match through Scoring→Expired and shows the Afterglow / summary. For a non-match
        /// solo run (no MatchManager) we fall back to <see cref="FinalizeRun"/>.
        /// </summary>
        public void EndRun()
        {
            if (State != GameState.Running) return;

            if (MatchManager.HasInstance && MatchManager.Instance.State != MatchState.Idle
                && MatchManager.Instance.State != MatchState.Expired)
            {
                MatchManager.Instance.EndMatch();
                FinalizeRun(crashed: false, causedBy: null);
                return;
            }
            FinalizeRun(crashed: false, causedBy: null);
        }

        /// <summary>Toggle map / AR (the ViewToggle button).</summary>
        public void ToggleViewMode()
        {
            SetViewMode(ViewMode == ViewMode.Map ? ViewMode.AR : ViewMode.Map);
        }

        /// <summary>Cycle to the next *unlocked* beacon form (BeaconFormButton).</summary>
        public void CycleBeaconForm()
        {
            int n = Enum.GetValues(typeof(BeaconFormType)).Length;
            var next = CurrentForm;
            for (int i = 0; i < n; i++)
            {
                next = (BeaconFormType)(((int)next + 1) % n);
                if (IsFormUnlocked(next)) break;
            }
            CurrentForm = next;
            startForm = next;
            if (Beacon.BeaconFormManager.HasInstance)
                Beacon.BeaconFormManager.Instance.SelectForm(next);
        }

        private BeaconFormType ResolveStartForm()
        {
            if (Beacon.BeaconFormManager.HasInstance)
                return Beacon.BeaconFormManager.Instance.SelectedForm;
            return startForm;
        }

        private static bool IsFormUnlocked(BeaconFormType form)
        {
            if (Beacon.BeaconFormManager.HasInstance)
                return Beacon.BeaconFormManager.Instance.IsFormUnlocked(form);
            foreach (var d in BeaconFormData.Defaults)
                if (d.formType == form) return d.unlocked;
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Per-tick: trail recording + fallback collision
        // ─────────────────────────────────────────────────────────────────────
        private void OnPositionUpdate(GeoPoint pos)
        {
            if (State != GameState.Running)
            {
                // TEMP DIAGNOSTIC: see why trail isn't recording — once per second.
                if (Time.unscaledTime - _lastStateLog > 1f)
                {
                    _lastStateLog = Time.unscaledTime;
                    Debug.Log($"[GameManager] OnPositionUpdate dropping — State={State} (need Running)");
                }
                return;
            }

            // Append to local trail.
            if (TrailManager.HasInstance)
            {
                TrailManager.Instance.OnLocationUpdate(pos);

                // Decision B (snake-tail energy budget): after appending, consult the
                // MatchManager's SnakeTailModel and prune the local trail to its cap when
                // exceeded. TrailData.PruneTo preserves the TotalLength accumulator (pitfall #18),
                // so distance scoring stays correct even as the tail dissolves.
                if (MatchManager.HasInstance && MatchManager.Instance.SnakeTailModel != null)
                {
                    var lt2 = TrailManager.Instance.LocalTrail;
                    var model = MatchManager.Instance.SnakeTailModel;
                    if (lt2 != null && model.ShouldPruneOldest(lt2.PointCount))
                        lt2.PruneTo(model.MaxSegments);
                }

                // TEMP DIAGNOSTIC: once per second, confirm trail grew.
                if (Time.unscaledTime - _lastStateLog > 1f)
                {
                    _lastStateLog = Time.unscaledTime;
                    var lt = TrailManager.Instance.LocalTrail;
                    Debug.Log($"[GameManager] OnPositionUpdate running — localTrail?={lt != null} points={lt?.PointCount ?? -1} allTrails={TrailManager.Instance.AllTrails.Count}");
                }
            }

            // Fallback collision: runs whenever no local-authority NetworkPlayer detector is
            // active (spec §8.4). With Fusion connected, NetworkPlayer runs its own detector
            // too and the double-fire guard below makes the overlap safe.
            if (fallbackDetector != null)
            {
                GeoPoint prev = _haveLastPos ? _lastPos : pos;
                fallbackDetector.CheckCollision(pos, prev, LocalPlayerId);
            }

            _lastPos = pos;
            _haveLastPos = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Crash pipeline (spec §16)
        // ─────────────────────────────────────────────────────────────────────
        private void OnFallbackCollision(string causedByPlayerId)
        {
            // Same bus as the Fusion path → identical pipeline.
            GameEvents.RaisePlayerCrashed(causedByPlayerId);
        }

        private void OnConnectionStateChanged(bool online)
        {
            OnlineRace = online;
            if (!online && State == GameState.Running)
            {
                // Mid-run disconnect (spec §8.1): the run continues solo; remote trails freeze
                // as static walls; the fallback detector keeps the run crashable.
                Debug.LogWarning("[GameManager] Connection dropped mid-run — continuing solo.");
            }
        }

        private void OnPlayerCrashed(string causedByPlayerId)
        {
            // Double-fire guard: both Fusion and the fallback can fire (spec §16).
            if (State != GameState.Running) return;
            if (_crashPipelineFired) return;
            _crashPipelineFired = true;

            // Decision F: crash is no longer terminal in match mode. Delegate the penalty +
            // respawn to MatchManager, which applies the Lumen penalty, records full metadata
            // to the replay sink (closes Track F's crash-metadata gap), and fires
            // RespawnRequested. We reset the guard so a subsequent crash (after respawn grace)
            // is handled again.
            if (MatchManager.HasInstance && MatchManager.Instance.State == MatchState.Live)
            {
                GeoPoint at = LocationProvider.HasInstance
                    ? LocationProvider.Instance.CurrentPosition
                    : default;
                MatchManager.Instance.HandlePlayerCrash(
                    !string.IsNullOrEmpty(causedByPlayerId) ? causedByPlayerId : LocalPlayerId, at);
                _crashPipelineFired = false; // allow the next crash event through (post-respawn)
                return;
            }

            // Non-match (solo) path: crash still terminates the run.
            FinalizeRun(crashed: true, causedBy: causedByPlayerId);
        }

        /// <summary>
        /// Respawn hook (decision F). MatchManager fires this after applying the Lumen penalty +
        /// recording the crash metadata. We:
        ///   • mark a trail discontinuity so the post-crash movement doesn't bridge a phantom wall
        ///     across the crash site (spec §20),
        ///   • reset the fallback collision detector at the respawn point so the in-flight
        ///     movement segment can't instantly re-crash into the same trail,
        ///   • reset the crash-pipeline guard so the next crash event is handled normally,
        ///   • (TODO) teleport the avatar to the respawn offset — left to the avatar controller
        ///     in the integration phase; for the milestone the runner simply resumes from the
        ///     current GPS position.
        /// </summary>
        private void OnRespawnRequested(string playerId, GeoPoint crashSite)
        {
            if (TrailManager.HasInstance)
                TrailManager.Instance.MarkDiscontinuity();

            if (fallbackDetector != null)
                fallbackDetector.BeginRun(crashSite);

            _crashPipelineFired = false;
            _haveLastPos = false;

            Debug.Log($"[GameManager] Runner respawned after crash (playerId={playerId}, crash={crashSite}).");
        }

        private void FinalizeRun(bool crashed, string causedBy)
        {
            // Spec §16 invariants:
            //  • Save the local trail BEFORE EndRun nulls it.
            //  • CrashSequence + its OnDestroy restore timeScale.
            if (TrailManager.HasInstance)
            {
                TrailManager tm = TrailManager.Instance;
                double duration = tm.RunElapsedSeconds;

                if (_trailRepository != null && tm.LocalTrail != null)
                {
                    if (crashed) _trailRepository.SaveFullTrailOnCrash(tm.LocalTrail);
                    _trailRepository.FinalizeTrail(tm.LocalTrail, duration, crashed, causedBy);
                }

                if (crashed && crashSequence != null && tm.LocalTrail != null)
                    crashSequence.Play(tm.LocalTrail.TrailColor);

                // Show summary (the single end-of-run screen for both crash and voluntary end).
                if (RunSummaryUI.HasInstance)
                    RunSummaryUI.Instance.ShowSummary(tm.LocalTrail, duration, _peakNearby, crashed, causedBy);

                tm.EndRun(crashed);
            }

            // Tear down fallback collision for this run.
            if (fallbackDetector != null) fallbackDetector.EndRun();

            // Decision Q: disconnect through the locator-resolved IMatchTransport. The concrete
            // FusionLauncher (Track C) registers itself as the transport when it comes up; in
            // editor-only mode the NullMatchTransport is a no-op.
            if (_connectRoutine != null) { StopCoroutine(_connectRoutine); _connectRoutine = null; }
            if (ServiceLocator.TryGet<IMatchTransport>(out var transport) && transport != null)
                transport.Disconnect();
            OnlineRace = null;

            SetState(crashed ? GameState.Crashed : GameState.Lobby);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────
        private string ResolveLocalPlayerId()
        {
            if (!string.IsNullOrEmpty(LocalPlayerId)) return LocalPlayerId;
            if (ServiceLocator.TryGet<IAuthService>(out var auth) && auth != null && auth.IsAuthenticated)
                return auth.CurrentUserId;
            return "local-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static Color ResolveTrailColor(BeaconFormType form)
        {
            if (Beacon.BeaconFormManager.HasInstance)
                return Beacon.BeaconFormManager.Instance.GetTrailColor(form);
            foreach (var d in BeaconFormData.Defaults)
                if (d.formType == form) return d.trailColor;
            return Color.cyan;
        }
    }
}
