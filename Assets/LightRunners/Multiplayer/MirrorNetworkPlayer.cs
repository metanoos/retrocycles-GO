using UnityEngine;
using Mirror;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;
using LightRunners.Beacon;
using LightRunners.Lightfield;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// Mirror-based networked avatar — free replacement for the Fusion NetworkPlayer.
    /// Spec §8.3, ADAPTED for Host Mode (decision Q).
    ///
    /// AUTHORITY MODEL (Mirror + decision Q):
    ///   • The host runs as server+client (StartHost). It is authoritative.
    ///   • Clients connect to the host and send validated Commands for Gate-collect
    ///     and crash reporting.
    ///   • <see cref="isLocalPlayer"/> means "this is my own avatar" — used to wire
    ///     the GPS read loop.
    ///   • <see cref="IsHostAuthority"/> means "this peer is the host" — used for
    ///     authoritative scoreboard mutation.
    /// </summary>
    public class MirrorNetworkPlayer : NetworkBehaviour
    {
        [SyncVar] public string PlayerId = string.Empty;
        [SyncVar] public int BeaconForm;
        [SyncVar(hook = nameof(OnCrashedChanged))]
        public bool IsCrashed;
        // Position/heading SyncVars: client-authoritative (C2 fix). The local player
        // writes GPS position and Mirror replicates to the host + other clients.
        // syncDirection is set to ClientToServer in OnStartLocalPlayer (this Mirror
        // version doesn't support syncDirection via [SyncVar] attribute parameter).
        [SyncVar] public float PositionX;
        [SyncVar] public float PositionY;
        [SyncVar] public float PositionZ;
        [SyncVar] public float Heading;

        private BeaconController _beacon;
        private TrailCollisionDetector _detector;
        private GeoPoint _lastPos;
        private bool _haveLast;
        private bool _crashHandled;
        private LocalRunnerIdentity _runnerIdentity;
        private int _appliedForm = -1;

        /// <summary>TRUE on every peer for its own avatar (Mirror isLocalPlayer).</summary>
        public bool IsLocalAuthority => isLocalPlayer;

        /// <summary>TRUE only on the host peer (decision Q). The host owns authoritative state.</summary>
        public bool IsHostAuthority => isServer && isLocalPlayer;

        /// <summary>TRUE on a client peer for its own avatar.</summary>
        public bool HasInputAuthorityOnly => isLocalPlayer && !isServer;

        /// <summary>Set by MirrorLauncher right after spawn (host-side, host's own player only).</summary>
        public void StampLocalIdentity(string playerId, bool isHost)
        {
            // Only stamp on the owner's side. For the host, that's isLocalPlayer.
            if (!isLocalPlayer) return;
            PlayerId = playerId;
            _runnerIdentity?.SetPlayerId(playerId);
            var form = BeaconFormManager.HasInstance
                ? BeaconFormManager.Instance.SelectedForm
                : BeaconFormType.Hoverboard;
            BeaconForm = (int)form;
        }

        /// <summary>
        /// Client-side identity stamp: called from OnStartLocalPlayer on clients to
        /// send their own player ID to the host via a Command (M2 fix). The host's
        /// OnServerAddPlayer stamps the host's ID directly; clients need this path
        /// because _localPlayerId on the host is the HOST's id, not the client's.
        /// </summary>
        [Command]
        public void CmdSetPlayerId(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            PlayerId = playerId;
        }

        public override void OnStartLocalPlayer()
        {
            base.OnStartLocalPlayer();

            // C2 fix: set client-authoritative sync direction so GPS position writes
            // replicate to the host. Mirror's default is ServerToClient which silently
            // drops client writes. syncDirection is per-NetworkBehaviour, so we set it
            // on this component for the local player's avatar.
            syncDirection = SyncDirection.ClientToServer;

            // Also set on the trail sync component (same GO).
            var trailSync = GetComponent<MirrorNetworkTrailSync>();
            if (trailSync != null) trailSync.syncDirection = SyncDirection.ClientToServer;

            // M2 fix: clients send their own player ID to the host via Command.
            // The host's OnServerAddPlayer stamps the host's ID; clients must
            // self-report because the host doesn't know the client's identity.
            // Uses reflection to avoid a circular Multiplayer→Gameplay dependency.
            if (HasInputAuthorityOnly)
            {
                string myId = ResolveLocalPlayerIdReflective();
                if (!string.IsNullOrEmpty(myId))
                    CmdSetPlayerId(myId);
            }

            // Beacon visual on a child GO (spec §8.3).
            var beaconGo = new GameObject("Beacon");
            beaconGo.transform.SetParent(transform, false);
            _beacon = beaconGo.AddComponent<BeaconController>();
            beaconGo.AddComponent<BeaconEffects>();
            EnsureRunnerCollisionIdentity();
            ApplyForm();
            GameEvents.PlayerRespawned += OnPlayerRespawned;

            // Collision detector wired to OnCrash (spec §8.3).
            _detector = gameObject.AddComponent<TrailCollisionDetector>();
            _detector.OnCollisionDetected += OnCrash;
            if (LocationProvider.HasInstance)
                _detector.BeginRun(LocationProvider.Instance.CurrentPosition);

            // Idempotent — GameManager.StartRun already ran (pitfall #2).
            if (TrailManager.HasInstance)
            {
                var form = (BeaconFormType)BeaconForm;
                var color = BeaconFormManager.HasInstance
                    ? BeaconFormManager.Instance.GetTrailColor(form)
                    : Color.cyan;
                TrailManager.Instance.StartRun(PlayerId, form, color);
            }
        }

        public override void OnStopClient()
        {
            GameEvents.PlayerRespawned -= OnPlayerRespawned;
            if (_detector != null) _detector.OnCollisionDetected -= OnCrash;
            if (!isLocalPlayer && TrailManager.HasInstance && !string.IsNullOrEmpty(PlayerId))
                TrailManager.Instance.RemoveRemoteTrail(PlayerId);
        }

        private void Update()
        {
            if (isLocalPlayer)
                SyncLocalPosition();
            else
                MirrorRemoteState();
        }

        private void SyncLocalPosition()
        {
            if (!LocationProvider.HasInstance) return;
            GeoPoint pos = LocationProvider.Instance.CurrentPosition;
            CoordinateConverter.EnsureReference(pos);
            Vector3 w = CoordinateConverter.GeoToWorld(pos);

            // Authoritative on local player; server-side SyncVars replicate to clients.
            PositionX = w.x;
            PositionY = w.y;
            PositionZ = w.z;
            if (_haveLast && _lastPos != pos)
                Heading = (float)CoordinateConverter.Bearing(_lastPos, pos);

            _beacon?.UpdatePosition(w, Heading);

            if (!IsCrashed && _detector != null && _haveLast)
                _detector.CheckCollision(pos, _lastPos, PlayerId);

            _lastPos = pos;
            _haveLast = true;
        }

        private void MirrorRemoteState()
        {
            _runnerIdentity?.SetPlayerId(PlayerId);
            ApplyForm();
            var w = new Vector3(PositionX, PositionY, PositionZ);
            _beacon?.UpdatePosition(w, Heading);

            if (IsCrashed && !_crashHandled)
            {
                _crashHandled = true;
                _beacon?.PlayCrashEffect();
            }
            else if (!IsCrashed)
            {
                _crashHandled = false;
            }
        }

        private void EnsureRunnerCollisionIdentity()
        {
            _runnerIdentity = GetComponent<LocalRunnerIdentity>();
            if (_runnerIdentity == null) _runnerIdentity = gameObject.AddComponent<LocalRunnerIdentity>();
            _runnerIdentity.SetPlayerId(PlayerId);

            var bodyCollider = GetComponent<SphereCollider>();
            if (bodyCollider == null) bodyCollider = gameObject.AddComponent<SphereCollider>();
            bodyCollider.isTrigger = true;
            bodyCollider.radius = FrozenMatchConfig.Default.PlayerHeadRadiusMeters;

            var body = GetComponent<Rigidbody>();
            if (body == null) body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
        }

        private void ApplyForm()
        {
            if (_appliedForm == BeaconForm || _beacon == null) return;
            _appliedForm = BeaconForm;
            var form = (BeaconFormType)BeaconForm;
            var color = BeaconFormManager.HasInstance
                ? BeaconFormManager.Instance.GetTrailColor(form)
                : Color.cyan;
            _beacon.SetForm(form);
            _beacon.SetTrailColor(color);
            if (!isLocalPlayer && TrailManager.HasInstance)
                TrailManager.Instance.SetRemoteTrailStyle(PlayerId, form, color);
        }

        // ─────────────────────────────────────────────────────────────────────
        // CRASH PATH (spec §8.3 + decision Q host-authoritative scoring)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Crash entry. On the host, raises the bus event (single crash signal Gameplay
        /// consumes per spec §16). On a client, sends a Command to the host so it can
        /// apply the authoritative penalty and broadcast IsCrashed.
        /// </summary>
        private void OnCrash(string causedByPlayerId)
        {
            if (IsCrashed) return;
            IsCrashed = true;
            _beacon?.PlayCrashEffect();
            GeoPoint here = _haveLast ? _lastPos : default;

            if (HasInputAuthorityOnly)
            {
                // Client path: ask the host to broadcast the crash.
                CmdReportCrash(causedByPlayerId, here.latitude, here.longitude, here.altitude);
                return;
            }

            // Host (or solo): raise the bus — Gameplay's single crash listener calls
            // MatchManager.HandlePlayerCrash → scoreboard.ApplyCrashPenalty.
            GameEvents.RaisePlayerCrashed(PlayerId, causedByPlayerId, here);
        }

        /// <summary>Mirror SyncVar hook: fires on clients when IsCrashed changes.</summary>
        private void OnCrashedChanged(bool _, bool newValue)
        {
            if (newValue && !_crashHandled)
            {
                _crashHandled = true;
                _beacon?.PlayCrashEffect();
            }
            else if (!newValue)
            {
                _crashHandled = false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // CLIENT → HOST COMMANDS (decision Q)
        // Mirror [Command] = client-to-server RPC, executed on the host/server.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Client → Host: report that the local player has crashed. The host raises the
        /// bus event (single application path → HandlePlayerCrash → ApplyCrashPenalty).
        /// </summary>
        [Command]
        private void CmdReportCrash(string causedByPlayerId, double lat, double lon, double alt)
        {
            if (IsCrashed) return;
            string crashedPlayerId = PlayerId;
            var crashSite = new GeoPoint(lat, lon, alt);
            GameEvents.RaisePlayerCrashed(crashedPlayerId, causedByPlayerId, crashSite);
            IsCrashed = true;
        }

        /// <summary>
        /// Client → Host: request credit for collecting a Lumen Gate. The host validates
        /// (decision Q) and, if it passes, awards via the authoritative IGateDirector.
        /// </summary>
        [Command]
        private void CmdRequestGateCollect(int gateId)
        {
            AwardGateCollectHost(gateId);
        }

        /// <summary>Public seam for Track D's UI / input layer to call from a client.</summary>
        public void RequestGateCollect(int gateId)
        {
            if (!isLocalPlayer) return;
            CmdRequestGateCollect(gateId);
        }

        // ─────────────────────────────────────────────────────────────────────
        // HOST-AUTHORITATIVE LUMEN / GATE-COLLECT APPLY (decision Q)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Host-only: validate a Gate-collect request and, if it passes, award one
        /// Lumen via the authoritative <see cref="IGateDirector"/>.
        /// </summary>
        private void AwardGateCollectHost(int gateId)
        {
            if (!IsHostAuthority) return;
            string playerId = PlayerId;
            if (!ValidateGateCollectHost(playerId, gateId, out _)) return;
            if (ServiceLocator.TryGet<IGateDirector>(out var director) && director != null)
                director.TryCollectGate(new GateId(gateId), playerId);
        }

        /// <summary>
        /// Host-side validation for a Gate-collect request (decision Q, anti-cheat).
        /// Resolves the gate position and requires the player to be within
        /// gateCollectionRadius (with 2× tolerance for movement + GPS jitter).
        /// </summary>
        private bool ValidateGateCollectHost(string playerId, int gateId, out GeoPoint gatePosition)
        {
            gatePosition = default;
            if (string.IsNullOrEmpty(playerId)) return false;
            if (ServiceLocator.TryGet<IGateDirector>(out var director) && director != null)
            {
                if (!director.TryGetGatePosition(new GateId(gateId), out var gatePos)) return false;
                gatePosition = gatePos;

                GeoPoint playerPos = ResolveAuthoritativePlayerGeo();
                double dist = playerPos.HorizontalDistanceTo(gatePos);
                float radius = GameConfig.Active.gateCollectionRadius;
                if (dist > radius * 2.0) return false;
            }
            return true;
        }

        /// <summary>
        /// Resolve this avatar's authoritative geo position from the networked world-space
        /// fields (host-side validation).
        /// </summary>
        private GeoPoint ResolveAuthoritativePlayerGeo()
        {
            Vector3 w = new Vector3(PositionX, PositionY, PositionZ);
            if (!CoordinateConverter.HasReference && LocationProvider.HasInstance)
                CoordinateConverter.EnsureReference(LocationProvider.Instance.CurrentPosition);
            return CoordinateConverter.WorldToGeo(w);
        }

        private void OnPlayerRespawned(string playerId)
        {
            if (playerId != PlayerId) return;
            if (IsHostAuthority) IsCrashed = false;
            _crashHandled = false;
            _haveLast = false;
            if (_detector != null && LocationProvider.HasInstance)
                _detector.BeginRun(LocationProvider.Instance.CurrentPosition);
        }

        /// <summary>
        /// Reflectively resolve GameManager.Instance.LocalPlayerId to avoid a
        /// circular Multiplayer→Gameplay assembly dependency (M2 fix).
        /// </summary>
        private static string ResolveLocalPlayerIdReflective()
        {
            try
            {
                var gmType = System.Type.GetType(
                    "LightRunners.Gameplay.GameManager, LightRunners.Gameplay");
                if (gmType == null) return null;
                var hasInstanceProp = gmType.GetProperty("HasInstance");
                if (hasInstanceProp != null && !(bool)hasInstanceProp.GetValue(null))
                    return null;
                var instanceProp = gmType.GetProperty("Instance");
                if (instanceProp == null) return null;
                var instance = instanceProp.GetValue(null);
                if (instance == null) return null;
                var idProp = gmType.GetProperty("LocalPlayerId");
                return idProp?.GetValue(instance) as string;
            }
            catch { return null; }
        }
    }
}
