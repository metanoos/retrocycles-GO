using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using LightRunners.Core;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// Mirror-based HOST-MODE launcher — free open-source replacement for FusionLauncher.
    /// DECISION Q: host-authoritative match.
    ///
    /// IMatchTransport (decision Q): GameManager (Track D) resolves
    /// <see cref="IMatchTransport"/> from the <see cref="ServiceLocator"/> and calls
    /// <see cref="ConnectMatch"/> / <see cref="Disconnect"/>.
    ///
    /// Mirror uses a host model natively: NetworkManager.StartHost() makes the local
    /// peer both server and client, exactly matching Fusion's GameMode.Host. There is
    /// no CCU limit, no subscription, and no external server required.
    /// </summary>
    public class MirrorLauncher : NetworkManager, IMatchTransport
    {
        private string _localPlayerId;
        private bool _registeredAsTransport;

        public bool IsConnected => NetworkServer.active || NetworkClient.active;
        public string CurrentRoomName { get; private set; }

        /// <summary>
        /// True once this peer is the room's HOST (server active). The host owns the
        /// authoritative Lumen tally, applies crash penalties, freezes the tail radius
        /// at countdown, and validates Gate-collect / referee requests (decision Q).
        /// </summary>
        public bool IsHost => NetworkServer.active && NetworkClient.active;

        public event Action<bool> ConnectionChanged;

        // ─────────────────────────────────────────────────────────────────────
        // Singleton accessor (Mirror already provides NetworkManager.singleton,
        // but this gives a typed handle for the game layer).
        // ─────────────────────────────────────────────────────────────────────

        public new static MirrorLauncher singleton => NetworkManager.singleton as MirrorLauncher;

        public override void Awake()
        {
            base.Awake();
            RegisterAsTransport();
        }

        public override void OnDestroy()
        {
            UnregisterAsTransport();
            base.OnDestroy();
        }

        // ─────────────────────────────────────────────────────────────────────
        // IMatchTransport surface (decision Q)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Connect (Host Mode) into <paramref name="roomId"/> as the local player
        /// <paramref name="localPlayerId"/>. Track D's MatchManager calls this through
        /// the locator.
        /// </summary>
        public void ConnectMatch(string roomId, string localPlayerId)
        {
            RegisterAsTransport();
            _localPlayerId = localPlayerId;
            CurrentRoomName = roomId;
            StartHost();
        }

        /// <summary>
        /// Shutdown the host/client. Track D's MatchManager calls this through the locator.
        /// Maps IMatchTransport.Disconnect to Mirror's StopHost/StopClient.
        /// </summary>
        public void Disconnect()
        {
            if (NetworkServer.active) StopHost();
            else StopClient();
        }

        public override void OnStopClient()
        {
            CurrentRoomName = null;
            RaiseConnected(false);
        }

        public override void OnStopServer()
        {
            CurrentRoomName = null;
            RaiseConnected(false);
        }

        public override void OnStartClient()
        {
            RaiseConnected(true);
        }

        public override void OnStartServer()
        {
            RaiseConnected(true);
        }

        private void RaiseConnected(bool online)
        {
            try { ConnectionChanged?.Invoke(online); }
            catch (Exception e) { Debug.LogWarning($"[MirrorLauncher] ConnectionChanged listener threw: {e.Message}"); }
            GameEvents.RaiseConnectionStateChanged(online);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Player spawn — Mirror calls this on the server when a client connects.
        // We spawn the avatar prefab and stamp the local identity.
        // ─────────────────────────────────────────────────────────────────────

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            Transform startPos = GetStartPosition();
            GameObject player = startPos != null
                ? Instantiate(playerPrefab, startPos.position, startPos.rotation)
                : Instantiate(playerPrefab);

            // Mirror convention: assign the object to the connection via
                // NetworkServer.AddPlayerForConnection.
            NetworkServer.AddPlayerForConnection(conn, player);

            var np = player.GetComponent<MirrorNetworkPlayer>();
            if (np != null)
            {
                bool isLocalHostPlayer = conn == NetworkServer.localConnection;
                np.StampLocalIdentity(_localPlayerId ?? conn.connectionId.ToString(), isLocalHostPlayer);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ServiceLocator registration (decision Q)
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
            if (ServiceLocator.TryGet<IMatchTransport>(out var current) && ReferenceEquals(current, this))
                ServiceLocator.Unregister<IMatchTransport>();
            _registeredAsTransport = false;
        }
    }
}
