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
    /// Shared-mode Fusion launcher (spec §8.1). The room name is the single matchmaking
    /// primitive — zone rooms and friend (party_) rooms both come in as a string; there is
    /// exactly one Connect path (pitfall #13).
    ///
    /// Edge cases handled per §8.1: room-full retries with a numeric suffix up to
    /// roomJoinRetryLimit, then solo; OnShutdown mid-run raises ConnectionStateChanged(false)
    /// and the run continues solo. Region: pin FixedRegion in PhotonAppSettings (pitfall #20).
    /// </summary>
    public class FusionLauncher : Singleton<FusionLauncher>, INetworkRunnerCallbacks
    {
        private NetworkRunner _runner;
        private string _localPlayerId;
        private Action<bool> _onConnectComplete;
        private bool _spawnedLocal;

        public bool IsConnected => _runner != null && _runner.IsRunning;
        public string CurrentRoomName { get; private set; }

        // ─────────────────────────────────────────────────────────────────────
        // Connect / Disconnect
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Start shared-mode matchmaking into <paramref name="roomName"/>. Fires
        /// <paramref name="onComplete"/> exactly once with the outcome; also raises
        /// <see cref="GameEvents.RaiseConnectionStateChanged"/>.
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
                    GameEvents.RaiseConnectionStateChanged(true);
                    CompleteConnect(true);
                    return;
                }

                // Room-full overflow (spec §8.1): zone_x_y → zone_x_y_2 → zone_x_y_3 …
                name = $"{roomName}_{attempt + 2}";
            }

            GameEvents.RaiseConnectionStateChanged(false);
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
                    GameMode = GameMode.Shared,
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

        public async void Disconnect()
        {
            CurrentRoomName = null;
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

        // ─────────────────────────────────────────────────────────────────────
        // INetworkRunnerCallbacks
        // ─────────────────────────────────────────────────────────────────────
        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            // Spawn the local avatar only for ourselves (spec §8.1).
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
            GameEvents.RaiseConnectionStateChanged(false);
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
