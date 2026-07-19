#if FUSION_WEAVER
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Fusion;
using Fusion.Sockets;
using LightRunners.Core;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// HOST-MODE Fusion launcher — DECISION Q (active decision 2026-07-18).
    ///
    /// DIVERGENCE FROM SPEC §8.1: the spec describes GameMode.Shared. The active
    /// decisions migrate the room to <see cref="GameMode.Host"/> so the room
    /// creator (HOST) holds State Authority on the match NetworkObject and acts as
    /// the single authoritative tally (Lumens, crash penalties, frozen tail radius,
    /// Gate validation). Clients send validated RPCs to the host instead of
    /// mutating shared state themselves. Track G will update SPEC §8.1.
    ///
    /// AUTHORITY FLOW (decision Q):
    ///   • First runner into the room (the room creator) joins as HOST — Fusion
    ///     grants it State Authority on the match object. <see cref="IsHost"/>
    ///     flips true once the runner is running AND the local peer is the host.
    ///   • Subsequent peers join as clients. They are "local" for input/visual
    ///     purposes only — they do NOT hold State Authority on the match object.
    ///   • <see cref="NetworkPlayer.IsLocalAuthority"/> therefore means "this is
    ///     the local player's avatar" (still useful for wiring the GPS read loop),
    ///     NOT "this peer owns match state". <see cref="NetworkPlayer.IsHostAuthority"/>
    ///     is the host-side check; <see cref="NetworkPlayer.HasInputAuthorityOnly"/>
    ///     is the client-side check.
    ///
    /// IMatchTransport (decision Q): GameManager (Track D) resolves
    /// <see cref="IMatchTransport"/> from the <see cref="ServiceLocator"/> and calls
    /// <see cref="ConnectMatch"/> / <see cref="Disconnect"/>. Phase 0 registers a
    /// <see cref="NullMatchTransport"/> by default; this real impl OVERWRITES that
    /// slot via <see cref="ServiceLocator.Register{IMatchTransport}(this)"/> when
    /// the runner comes up, and unregisters on shutdown.
    ///
    /// Edge cases preserved from §8.1: room-full retries with a numeric suffix up
    /// to <c>roomJoinRetryLimit</c>, then solo; OnShutdown mid-run raises
    /// <see cref="GameEvents.RaiseConnectionStateChanged"/>(false) and the run
    /// continues solo. Region: pin FixedRegion in PhotonAppSettings (pitfall #20).
    /// </summary>
    public class FusionLauncher : Singleton<FusionLauncher>, INetworkRunnerCallbacks, IMatchTransport
    {
        private NetworkRunner _runner;
        private string _localPlayerId;
        private Action<bool> _onConnectComplete;
        private bool _spawnedLocal;
        private bool _registeredAsTransport;

        public bool IsConnected => _runner != null && _runner.IsRunning;
        public string CurrentRoomName { get; private set; }

        /// <summary>
        /// True once this peer is the room's HOST (State Authority over the match
        /// object). The host owns the authoritative Lumen tally, applies crash
        /// penalties, freezes the tail radius at countdown, and validates
        /// Gate-collect / referee RPCs.
        /// </summary>
        public bool IsHost => IsConnected && _runner != null && _runner.IsServer;

        // ─────────────────────────────────────────────────────────────────────
        // IMatchTransport surface (decision Q)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised exactly once per connect/disconnect transition. Mirrors
        /// <see cref="GameEvents.ConnectionStateChanged"/> for consumers that want
        /// a typed dependency on <see cref="IMatchTransport"/> instead of the bus.
        /// </summary>
        public event Action<bool> ConnectionChanged;

        /// <summary>
        /// Connect (Host Mode) into <paramref name="roomId"/> as the local player
        /// <paramref name="localPlayerId"/>. Replaces the legacy
        /// <c>Connect(roomName, localPlayerId, onComplete)</c> as the entry point
        /// for Track D's <c>MatchManager</c>. The room-full-retry logic is preserved;
        /// completion is surfaced via <see cref="ConnectionChanged"/> and the
        /// <see cref="GameEvents.ConnectionStateChanged"/> bus. (No async callback
        /// parameter on the interface — callers observe the bus.)
        /// </summary>
        public void ConnectMatch(string roomId, string localPlayerId)
        {
            Connect(roomId, localPlayerId, onComplete: null);
        }

        /// <summary>
        /// Original connect path kept for Track D's transitional call sites and for
        /// the existing GameManager coroutine (which still passes a local callback
        /// inside its connect-timeout window). <paramref name="onComplete"/> is
        /// optional; pass null to use the locator/bus surface only.
        /// </summary>
        public async void Connect(string roomName, string localPlayerId, Action<bool> onComplete)
        {
            _localPlayerId = localPlayerId;
            _onConnectComplete = onComplete;
            _spawnedLocal = false;

            GameConfig cfg = GameConfig.Active;
            int retries = Mathf.Max(0, cfg.roomJoinRetryLimit);
            string name = roomName;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                bool ok = await TryStart(name, cfg);
                if (ok)
                {
                    CurrentRoomName = name;
                    RegisterAsTransport();
                    RaiseConnected(true);
                    CompleteConnect(true);
                    return;
                }

                // Room-full overflow (spec §8.1): zone_x_y → zone_x_y_2 → zone_x_y_3 …
                name = $"{roomName}_{attempt + 2}";
            }

            RaiseConnected(false);
            CompleteConnect(false);
        }

        private async Task<bool> TryStart(string roomName, GameConfig cfg)
        {
            try
            {
                if (_runner != null) await _runner.Shutdown();

                var go = new GameObject("NetworkRunner");
                go.transform.SetParent(transform, false);
                _runner = go.AddComponent<NetworkRunner>();
                _runner.ProvideInput = false; // GPS drives movement, not input polling
                _runner.AddCallbacks(this);

                var result = await _runner.StartGame(new StartGameArgs
                {
                    // DECISION Q: Host Mode (was GameMode.Shared). The first peer to
                    // create the room becomes the authoritative host; later peers are
                    // clients that send RPCs to it.
                    GameMode = GameMode.Host,
                    SessionName = roomName,
                    PlayerCount = cfg.maxPlayersPerRoom,
                    // AppId/app-version live in PhotonAppSettings.asset (spec §8.1).
                });

                if (!result.Ok)
                    Debug.LogWarning($"[FusionLauncher] StartGame '{roomName}' failed: {result.ShutdownReason}");
                return result.Ok;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FusionLauncher] Connect '{roomName}' threw: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Shutdown the runner. Overwrites <see cref="IMatchTransport.Disconnect"/>
        /// — Track D's MatchManager calls this through the locator.
        /// </summary>
        public async void Disconnect()
        {
            CurrentRoomName = null;
            UnregisterAsTransport();
            if (_runner != null)
            {
                var r = _runner;
                _runner = null;
                try { await r.Shutdown(); }
                catch (Exception e) { Debug.LogWarning($"[FusionLauncher] Shutdown threw: {e.Message}"); }
            }
        }

        private void CompleteConnect(bool ok)
        {
            var cb = _onConnectComplete;
            _onConnectComplete = null;
            cb?.Invoke(ok);
        }

        private void RaiseConnected(bool online)
        {
            // Two surfaces, one source of truth: the typed IMatchTransport event
            // AND the cross-assembly bus (lets Gameplay/UI react without a ref).
            try { ConnectionChanged?.Invoke(online); }
            catch (Exception e) { Debug.LogWarning($"[FusionLauncher] ConnectionChanged listener threw: {e.Message}"); }
            GameEvents.RaiseConnectionStateChanged(online);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ServiceLocator registration (decision Q)
        //
        // Phase 0's PlatformServiceRegistry registered a NullMatchTransport on the
        // locator. The real Host-Mode transport must OVERWRITE that slot the moment
        // a runner comes up — Register replaces; TryRegister would wrongly keep
        // the null. Unregistered symmetrically on shutdown so a later NullMatch
        // (e.g. after a return to Login) takes over cleanly.
        // ─────────────────────────────────────────────────────────────────────
        private void RegisterAsTransport()
        {
            if (_registeredAsTransport) return;
            ServiceLocator.Register<IMatchTransport>(this);
            _registeredAsTransport = true;
        }

        private void UnregisterAsTransport()
        {
            if (!_registeredAsTransport) return;
            // Only remove if it's still us — defensive against a stale runner path.
            if (ServiceLocator.TryGet<IMatchTransport>(out var current) && ReferenceEquals(current, this))
                ServiceLocator.Unregister<IMatchTransport>();
            _registeredAsTransport = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // INetworkRunnerCallbacks
        // ─────────────────────────────────────────────────────────────────────
        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            // Spawn the local avatar only for ourselves (spec §8.3, decision Q).
            // Under Host Mode the host spawns with State Authority; clients spawn
            // their own avatar with Input Authority only. Match-level state lives
            // on a separate NetworkMatchState object owned by the host.
            if (player != runner.LocalPlayer || _spawnedLocal) return;
            _spawnedLocal = true;

            var prefab = Resources.Load<GameObject>("Player/NetworkPlayer");
            if (prefab == null)
            {
                Debug.LogError("[FusionLauncher] Resources/Player/NetworkPlayer.prefab missing — run Light-Runners/Setup/NetworkPlayer Prefab.");
                return;
            }

            var obj = runner.Spawn(prefab, Vector3.zero, Quaternion.identity, player);
            var np = obj != null ? obj.GetComponent<NetworkPlayer>() : null;
            if (np != null) np.StampLocalIdentity(_localPlayerId);
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            // The NetworkPlayer despawn callback removes the trail; nothing else to do here.
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            if (runner != _runner && _runner != null) return; // stale runner from a retry
            _runner = null;
            CurrentRoomName = null;
            UnregisterAsTransport();
            RaiseConnected(false);
            CompleteConnect(false);
        }

        // Unused callbacks (interface completeness).
        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
    }
}
#endif
