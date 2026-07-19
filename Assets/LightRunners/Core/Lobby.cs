using System;

namespace LightRunners.Core
{
    /// <summary>
    /// One friend-match lobby as seen by clients (spec §8.5). Mirrors the
    /// <c>lobby_rooms</c> row (§12.5).
    /// </summary>
    [Serializable]
    public class LobbyInfo
    {
        /// <summary>The 6-char share code (uppercase, from the no-lookalike alphabet).</summary>
        public string code;

        /// <summary>The Photon room name (<c>party_{CODE}</c>) both host and joiners connect to.</summary>
        public string roomName;

        public string hostId;

        public PlayerIdentity[] members;

        /// <summary><c>open</c> | <c>racing</c> | <c>closed</c> (spec §12.5).</summary>
        public string status;

        /// <summary>Set when the host started the race — the joiners' start signal (spec §8.5).</summary>
        public DateTime? startedAt;

        public DateTime expiresAt;

        public bool IsOpen => status == "open";
        public bool IsRacing => status == "racing";
    }

    /// <summary>
    /// Friend-match seam (spec §8.5). Implemented by <c>SupabaseLobbyService</c> (Backend)
    /// and registered on the <see cref="ServiceLocator"/> by <c>SupabaseManager</c>, so
    /// Gameplay drives lobbies without referencing Backend directly (mirrors IAuthService).
    /// All calls are async over UnityWebRequest; errors surface via <c>onError</c> with the
    /// RPC's error token (<c>lobby_full</c> / <c>lobby_expired</c> / <c>lobby_closed</c> /
    /// <c>rate_limited</c> / <c>not_found</c>) or a transport message.
    /// </summary>
    public interface ILobbyService
    {
        /// <summary>The lobby this client is currently in, or null.</summary>
        LobbyInfo ActiveLobby { get; }

        /// <summary>Convenience: <c>ActiveLobby?.roomName</c> — read by GameManager.StartRun (spec §8.5).</summary>
        string ActiveRoomName { get; }

        /// <summary>True while this client is the host of <see cref="ActiveLobby"/>.</summary>
        bool IsHost { get; }

        void CreateLobby(Action<LobbyInfo> onSuccess, Action<string> onError);
        void JoinLobby(string code, Action<LobbyInfo> onSuccess, Action<string> onError);
        void LeaveLobby(Action onDone);

        /// <summary>Fetch current lobby state by code — the §8.5 start-signal poll.</summary>
        void GetLobby(string code, Action<LobbyInfo> onSuccess, Action<string> onError);

        /// <summary>Host-only: set status='racing' so pollers auto-start (spec §8.5).</summary>
        void StartLobbyRace(Action onSuccess, Action<string> onError);
    }
}
