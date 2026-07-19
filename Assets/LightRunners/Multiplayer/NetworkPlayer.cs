#if FUSION_WEAVER
using UnityEngine;
using Fusion;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;
using LightRunners.Beacon;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// The networked avatar (spec §8.3, ADAPTED for Host Mode — decision Q).
    ///
    /// DIVERGENCE FROM SPEC §8.1: under Shared Mode, "local authority" ==
    /// "HasStateAuthority on this avatar". Under Host Mode (decision Q) the HOST
    /// holds State Authority on the match NetworkObject; clients hold only Input
    /// Authority on their own avatar. Authority terminology therefore splits:
    ///
    ///   • <see cref="IsLocalAuthority"/> — TRUE on every peer for its OWN avatar.
    ///     Used to wire the GPS read loop and the local trail recorder. Does NOT
    ///     imply match-state authority.
    ///   • <see cref="IsHostAuthority"/> — TRUE only on the host peer. The host
    ///     owns the authoritative <see cref="ILumenScoreboard"/>, applies crash
    ///     penalties, and validates Gate-collect requests.
    ///   • <see cref="HasInputAuthorityOnly"/> — TRUE on clients for their own
    ///     avatar (input authority, but not state authority over match state).
    ///
    /// CLIENT-TO-HOST VALIDATION FLOW (decision Q, anti-cheat):
    ///   • A client that wants to collect a Gate calls
    ///     <see cref="RpcRequestGateCollect"/> (an Rpc to the host).
    ///   • The HOST validates the request (does the Gate exist? is the player
    ///     within <c>gateCollectionRadius</c>?) and only then calls
    ///     <see cref="ILumenScoreboard.Award"/>. This gives one authoritative
    ///     tally and removes the client's ability to grant itself Lumens.
    ///   • Crashes follow the same pattern: a client reports its own crash via
    ///     <see cref="RpcReportCrash"/>; the host applies
    ///     <see cref="ILumenScoreboard.ApplyCrashPenalty"/> and re-broadcasts the
    ///     crashed flag so every peer renders the same FX.
    ///
    /// CRASH PATH (spec §8.3): the existing <see cref="GameEvents.RaisePlayerCrashed"/>
    /// raise is PRESERVED — it remains the single crash signal Gameplay consumes
    /// (no circular assembly ref). The host also applies the Lumen penalty before
    /// raising; clients merely mirror the networked IsCrashed flag for FX.
    /// </summary>
    public class NetworkPlayer : NetworkBehaviour
    {
        [Networked] public NetworkString<_64> PlayerId { get; set; }
        [Networked] public int BeaconForm { get; set; }
        [Networked] public NetworkBool IsCrashed { get; set; }
        [Networked] public float PositionX { get; set; }
        [Networked] public float PositionY { get; set; }
        [Networked] public float PositionZ { get; set; }
        [Networked] public float Heading { get; set; }

        private BeaconController _beacon;
        private TrailCollisionDetector _detector;
        private GeoPoint _lastPos;
        private bool _haveLast;
        private bool _crashHandled;

        /// <summary>
        /// TRUE on every peer for its own avatar (Host-Mode — decision Q).
        /// Used to wire the local GPS read loop / trail recorder. Does NOT imply
        /// the peer owns match state — that is <see cref="IsHostAuthority"/>.
        ///
        /// Under Host Mode this is "Input Authority on my avatar". The host peer
        /// also has State Authority over the match NetworkObject (separate obj).
        /// </summary>
        public bool IsLocalAuthority
            => Object != null && Object.HasInputAuthority && Runner != null
               && Object.InputAuthority == Runner.LocalPlayer;

        /// <summary>
        /// TRUE only on the host peer (decision Q). The host owns the
        /// authoritative Lumen tally, applies crash penalties, and validates
        /// Gate-collect / referee RPCs. Convenience accessor — equivalent to
        /// <c>Object.HasStateAuthority</c> on the host-owned match object, but
        /// exposed here so RPC handlers on this avatar can branch cleanly.
        /// </summary>
        public bool IsHostAuthority => Object != null && Object.HasStateAuthority;

        /// <summary>
        /// TRUE on a client peer for its own avatar (decision Q). The client has
        /// input authority (drives GPS) but NOT state authority over match state.
        /// </summary>
        public bool HasInputAuthorityOnly => IsLocalAuthority && !IsHostAuthority;

        /// <summary>Set by FusionLauncher right after Spawn (local authority only).</summary>
        public void StampLocalIdentity(string playerId)
        {
            if (!IsLocalAuthority) return;
            PlayerId = playerId;
            var form = BeaconFormManager.HasInstance
                ? BeaconFormManager.Instance.SelectedForm
                : BeaconFormType.Hoverboard;
            BeaconForm = (int)form;
        }

        public override void Spawned()
        {
            // Beacon visual on a child GO (spec §8.3).
            var beaconGo = new GameObject("Beacon");
            beaconGo.transform.SetParent(transform, false);
            _beacon = beaconGo.AddComponent<BeaconController>();
            beaconGo.AddComponent<BeaconEffects>();
            ApplyForm();

            if (IsLocalAuthority)
            {
                // Collision detector wired to OnCrash (spec §8.3). The §8.4 fallback detector
                // also exists on GameManager; the crash pipeline's double-fire guard makes
                // the overlap safe.
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
                    TrailManager.Instance.StartRun(PlayerId.ToString(), form, color);
                }
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (_detector != null) _detector.OnCollisionDetected -= OnCrash;
            if (!IsLocalAuthority && TrailManager.HasInstance)
                TrailManager.Instance.RemoveRemoteTrail(PlayerId.ToString());
        }

        public override void FixedUpdateNetwork()
        {
            if (IsLocalAuthority)
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

            PositionX = w.x;
            PositionY = w.y;
            PositionZ = w.z;
            if (_haveLast && _lastPos != pos)
                Heading = (float)CoordinateConverter.Bearing(_lastPos, pos);

            _beacon?.UpdatePosition(w, Heading);

            if (!IsCrashed && _detector != null && _haveLast)
                _detector.CheckCollision(pos, _lastPos, PlayerId.ToString());

            _lastPos = pos;
            _haveLast = true;
        }

        private void MirrorRemoteState()
        {
            ApplyForm();
            var w = new Vector3(PositionX, PositionY, PositionZ);
            _beacon?.UpdatePosition(w, Heading);

            if (IsCrashed && !_crashHandled)
            {
                _crashHandled = true;
                _beacon?.PlayCrashEffect();
            }
        }

        private int _appliedForm = -1;

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
            if (!IsLocalAuthority && TrailManager.HasInstance)
                TrailManager.Instance.SetRemoteTrailStyle(PlayerId.ToString(), form, color);
        }

        /// <summary>
        /// Crash entry (spec §8.3): flag, FX, end trail, raise the bus event.
        ///
        /// Under Host Mode (decision Q):
        ///   • On the HOST: this runs the local detector → sets IsCrashed → applies
        ///     <see cref="ILumenScoreboard.ApplyCrashPenalty"/> on the authoritative
        ///     scoreboard → raises <see cref="GameEvents.RaisePlayerCrashed"/>.
        ///   • On a CLIENT: the local detector fires here, but the Lumen penalty is
        ///     applied by the HOST. The client therefore also calls
        ///     <see cref="RpcReportCrash"/> so the host applies the authoritative
        ///     penalty and re-broadcasts IsCrashed. The bus event is still raised
        ///     locally for the local crash pipeline (FX, end-of-run summary).
        /// </summary>
        private void OnCrash(string causedByPlayerId)
        {
            if (IsCrashed) return;
            IsCrashed = true;
            _beacon?.PlayCrashEffect();

            // Round-1 review fix R1-F7/R2-F4: the host previously applied the Lumen penalty
            // directly here AND raised the bus event, which GameManager.OnPlayerCrashed →
            // MatchManager.HandlePlayerCrash re-applied — a 2× penalty on the host for one
            // crash. Single-source fix: the host no longer applies the penalty locally; it
            // raises the bus event (the single crash signal Gameplay consumes per spec §16),
            // and MatchManager.HandlePlayerCrash owns the authoritative ApplyCrashPenalty call
            // (with the crash GeoPoint, which the local direct path didn't have).
            if (!IsHostAuthority && HasInputAuthorityOnly)
            {
                // Client path: ask the host to broadcast the crash. The host's resulting bus
                // raise drives HandlePlayerCrash on the host's authoritative scoreboard.
                GeoPoint here = _haveLast ? _lastPos : GeoPoint.Zero;
                RpcReportCrash(PlayerId.ToString(), here.latitude, here.longitude, here.altitude);
                // Clients do NOT raise the bus locally (R1-F14/R2): doing so fires Gameplay's
                // crash pipeline against the client's non-authoritative scoreboard, causing
                // visible "score dropped then bounced back" flicker when host sync overwrites.
                // Clients observe the crash via the networked IsCrashed flag (mirrored below).
                return;
            }

            // Host (or solo editor with no Fusion): raise the bus — Gameplay's single crash
            // listener will call MatchManager.HandlePlayerCrash → scoreboard.ApplyCrashPenalty.
            GameEvents.RaisePlayerCrashed(causedByPlayerId);
        }

        // ─────────────────────────────────────────────────────────────────────
        // HOST-AUTHORITATIVE LUMEN / GATE-COLLECT APPLY (decision Q)
        // ─────────────────────────────────────────────────────────────────────
        // Note (Round-1 review fix R1-F7/R2-F4): the host no longer applies crash penalties
        // directly. All crash penalties route through GameEvents.RaisePlayerCrashed on the host
        // → GameManager.OnPlayerCrashed → MatchManager.HandlePlayerCrash → ApplyCrashPenalty.
        // Single application site, correct crash GeoPoint, single stolen-Lumen record.

        /// <summary>
        /// Host-only: validate a Gate-collect request and, if it passes, award one
        /// Lumen via the authoritative <see cref="ILumenScoreboard"/>. Validation
        /// lives in <see cref="ValidateGateCollectHost"/> so it can grow without
        /// touching the RPC surface.
        /// </summary>
        private void AwardGateCollectHost(string playerId, int gateId)
        {
            if (!IsHostAuthority) return;
            if (!ValidateGateCollectHost(playerId, gateId)) return;
            if (ServiceLocator.TryGet<ILumenScoreboard>(out var scoreboard) && scoreboard != null)
            {
                int newTotal = scoreboard.Award(playerId);
                GameEvents.RaiseGateCollected(gateId, playerId);
                GameEvents.RaiseLumensChanged(playerId, newTotal);
            }
        }

        /// <summary>
        /// Host-side validation for a Gate-collect request (decision Q, anti-cheat).
        ///
        /// Milestone validation: the gate exists in the active
        /// <see cref="IGateDirector"/> pool. The full milestone check (player within
        /// <c>gateCollectionRadius</c> of the gate's geo-anchor) is deferred until
        /// Track B lands the Gate geometry; for now we accept any gate the director
        /// knows about so the wiring can be smoke-tested.
        /// </summary>
        private bool ValidateGateCollectHost(string playerId, int gateId)
        {
            if (string.IsNullOrEmpty(playerId)) return false;
            // Round-1 review fix R1-F9/R2-F8: the prior check (gateId < 0 || gateId >= ActiveGateCount)
            // had two problems: (a) bonus gate ids start at BonusGateIdBase (1M) and were always
            // rejected, making referee-placed gates uncollectible; (b) there was no distance check,
            // so a cheating client could collect any gate from anywhere. Now we resolve the gate's
            // position via TryGetGatePosition (which handles density + bonus uniformly) and require
            // the player to be within gateCollectionRadius of it.
            if (ServiceLocator.TryGet<IGateDirector>(out var director) && director != null)
            {
                if (!director.TryGetGatePosition(new GateId(gateId), out var gatePos)) return false;

                // Distance check (decision Q host-authoritative anti-cheat). The host's
                // NetworkPlayer carries the authoritative avatar position via PositionX/Y/Z (in
                // world space; convert back to geo for the haversine compare). A small tolerance
                // accounts for the tick-rate gap between the player's last authoritative update
                // and the gate-collect RPC.
                GeoPoint playerPos = ResolveAuthoritativePlayerGeo();
                double dist = playerPos.HorizontalDistanceTo(gatePos);
                float radius = GameConfig.Active.gateCollectionRadius;
                // 2× tolerance: movement between ticks + GPS jitter (decision-N no-speed-limit
                // means a fast runner can cover meaningful distance between authoritative ticks).
                if (dist > radius * 2.0) return false;
            }
            return true;
        }

        /// <summary>
        /// Resolve this avatar's authoritative geo position from the networked world-space
        /// fields (host-side validation). Round-1 review fix: needed by the gate-distance check.
        /// </summary>
        private GeoPoint ResolveAuthoritativePlayerGeo()
        {
            Vector3 w = new Vector3(PositionX, PositionY, PositionZ);
            return LightRunners.Location.CoordinateConverter.WorldToGeo(w);
        }

        // ─────────────────────────────────────────────────────────────────────
        // CLIENT → HOST RPCs (decision Q)
        //
        // [Rpc] / [RpcInvoke] attributes are Fusion-specific. The
        // [Rpc(RpcSources.All, RpcTargets.StateAuthority)] form targets the host
        // (the State-Authority peer on this object) — exactly the validated
        // command path we want.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Client → Host: request credit for collecting a Lumen Gate. The host
        /// validates (decision Q) and, if it passes, awards via
        /// <see cref="AwardGateCollectHost"/>. Cannot be self-awarded by the
        /// client.
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RpcRequestGateCollect(int gateId, string playerId, RpcInfo info = default)
        {
            if (!IsHostAuthority) return;
            if (string.IsNullOrEmpty(playerId)) return;
            // Round-1 review fix R2-F8: tighten the source check. The prior "soft" check let a
            // client request a collect for ANY player id (it never compared info.Source to
            // Object.InputAuthority). Now the sender must be the input-authority peer for this
            // avatar — a client can only collect on its own behalf. (Full Token-verified player
            // id binding remains a v2 concern, but this closes the trivially-exploitable path.)
            if (info.Source != Object.InputAuthority) return;
            AwardGateCollectHost(playerId, gateId);
        }

        /// <summary>
        /// Client → Host: report that the local player has crashed. The host raises the crash
        /// bus event (Round-1 review fix: previously applied the penalty directly here, which
        /// (a) bypassed MatchManager.HandlePlayerCrash's GeoPoint stamping for the
        ///     stolen-Lumen pickup, and
        /// (b) created a second penalty application site that could double with the host's
        ///     own OnCrash bus raise.)
        /// Routing through the bus means there's exactly one penalty application site
        /// (HandlePlayerCrash), exactly one stolen-Lumen record, and exactly one replay-sink
        /// RecordCrash with full metadata. The client's crash GeoPoint is carried in the RPC
        /// so the host stamps the correct pickup spawn site.
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RpcReportCrash(string playerId, double lat, double lon, double alt, RpcInfo info = default)
        {
            if (!IsHostAuthority) return;
            if (string.IsNullOrEmpty(playerId)) return;
            // Source-check: the reporter must be the input-authority peer for this avatar
            // (Round-1 fix R2-F8: a client could otherwise spam crash reports for rivals).
            if (info.Source != Object.InputAuthority) return;
            // Delegate to the single crash-application path. The bus raise on the host drives
            // GameManager.OnPlayerCrashed → MatchManager.HandlePlayerCrash with the crash site.
            // We stash the supplied GeoPoint on a host-side field so HandlePlayerCrash can read
            // the authoritative position when it asks LocationProvider (the host's local
            // LocationProvider read would be the wrong player's position).
            _reportedCrashSite = new GeoPoint(lat, lon, alt);
            GameEvents.RaisePlayerCrashed(playerId);
            IsCrashed = true;
        }

        /// <summary>
        /// Crash GeoPoint reported via the most recent RpcReportCrash (host-side). Read by
        /// MatchManager.HandlePlayerCrash via <see cref="GetReportedCrashSite"/> so the penalty
        /// + stolen-Lumen pickup land at the correct spot for remote players.
        /// </summary>
        private GeoPoint? _reportedCrashSite;
        /// <summary>Host-side: the GeoPoint of the most recent RpcReportCrash, or null.</summary>
        public GeoPoint? GetReportedCrashSite() => _reportedCrashSite;

        /// <summary>Public seam for Track D's UI / input layer to call from a client.</summary>
        public void RequestGateCollect(int gateId)
        {
            if (!IsLocalAuthority) return;
            RpcRequestGateCollect(gateId, PlayerId.ToString());
        }
    }
}
#endif
