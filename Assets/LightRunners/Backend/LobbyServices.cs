using System;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Backend
{
    /// <summary>
    /// Null-op <see cref="ILobbyService"/> registered when Supabase isn't configured
    /// (spec §3.1) — friend match simply reports unavailable, everything else runs.
    /// </summary>
    public sealed class NullLobbyService : ILobbyService
    {
        public LobbyInfo ActiveLobby => null;
        public string ActiveRoomName => null;
        public bool IsHost => false;

        public void CreateLobby(Action<LobbyInfo> onSuccess, Action<string> onError)
            => onError?.Invoke("offline");

        public void JoinLobby(string code, Action<LobbyInfo> onSuccess, Action<string> onError)
            => onError?.Invoke("offline");

        public void LeaveLobby(Action onDone) => onDone?.Invoke();

        public void GetLobby(string code, Action<LobbyInfo> onSuccess, Action<string> onError)
            => onError?.Invoke("offline");

        public void StartLobbyRace(Action onSuccess, Action<string> onError)
            => onError?.Invoke("offline");
    }

    /// <summary>
    /// Supabase-backed friend match (spec §8.5 / §12.5): 4 RPCs + the start-signal poll.
    /// The RPC error token (lobby_full / lobby_expired / lobby_closed / rate_limited /
    /// not_found) is extracted from the PostgREST error body and surfaced verbatim so the UI
    /// can show the specific reason.
    /// </summary>
    public sealed class SupabaseLobbyService : ILobbyService
    {
        private readonly SupabaseManager _supabase;
        private LobbyInfo _active;

        public SupabaseLobbyService(SupabaseManager supabase)
        {
            _supabase = supabase;
        }

        public LobbyInfo ActiveLobby => _active;
        public string ActiveRoomName => _active?.roomName;
        public bool IsHost => _active != null && _active.hostId == _supabase.UserId;

        // ─────────────────────────────────────────────────────────────────────
        // DTOs (PostgREST rows)
        // ─────────────────────────────────────────────────────────────────────
        [Serializable]
        private class LobbyRow
        {
            public string code;
            public string room_name;
            public string host_id;
            public string status;
            public string started_at;
            public string expires_at;
            public MemberRow[] members;
        }

        [Serializable]
        private class MemberRow
        {
            public string user_id;
            public string display_name;
        }

        private static LobbyInfo ToInfo(LobbyRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.code)) return null;
            var members = Array.Empty<PlayerIdentity>();
            if (row.members != null)
            {
                members = new PlayerIdentity[row.members.Length];
                for (int i = 0; i < row.members.Length; i++)
                    members[i] = new PlayerIdentity(row.members[i].user_id, row.members[i].display_name);
            }
            DateTime.TryParse(row.expires_at, out var expires);
            DateTime? started = null;
            if (!string.IsNullOrEmpty(row.started_at) && DateTime.TryParse(row.started_at, out var s))
                started = s;
            return new LobbyInfo
            {
                code = row.code,
                roomName = row.room_name,
                hostId = row.host_id,
                members = members,
                status = row.status,
                startedAt = started,
                expiresAt = expires,
            };
        }

        /// <summary>Pull the raise-exception token out of a PostgREST error body, if present.</summary>
        private static string ErrorToken(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unknown";
            foreach (var token in new[] { "lobby_full", "lobby_expired", "lobby_closed", "rate_limited", "not_host", "not_found" })
                if (raw.Contains(token)) return token;
            return raw;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ILobbyService
        // ─────────────────────────────────────────────────────────────────────
        public void CreateLobby(Action<LobbyInfo> onSuccess, Action<string> onError)
        {
            GeoPoint pos = Location.LocationProvider.HasInstance
                ? Location.LocationProvider.Instance.CurrentPosition
                : default;
            float cell = GameConfig.Active.lobbyRegionCell;
            string region = StringUtils.FormatInvariant("{0:0.0}_{1:0.0}",
                Math.Floor(pos.latitude / cell) * cell, Math.Floor(pos.longitude / cell) * cell);

            _supabase.RpcWithRetry("create_lobby", $"{{\"region\":\"{region}\"}}",
                onSuccess: resp =>
                {
                    var rows = JsonArray.FromJson<LobbyRow>(resp);
                    var info = rows.Length > 0 ? ToInfo(rows[0]) : null;
                    if (info == null) { onError?.Invoke("bad create_lobby response"); return; }
                    _active = info;
                    onSuccess?.Invoke(info);
                },
                onError: err => onError?.Invoke(ErrorToken(err)));
        }

        public void JoinLobby(string code, Action<LobbyInfo> onSuccess, Action<string> onError)
        {
            string clean = (code ?? "").Trim().ToUpperInvariant();
            _supabase.Rpc("join_lobby", $"{{\"lobby_code\":\"{clean}\"}}",
                onSuccess: resp =>
                {
                    var rows = JsonArray.FromJson<LobbyRow>(resp);
                    var info = rows.Length > 0 ? ToInfo(rows[0]) : null;
                    if (info == null) { onError?.Invoke("not_found"); return; }
                    _active = info;
                    onSuccess?.Invoke(info);
                },
                onError: err => onError?.Invoke(ErrorToken(err)));
        }

        public void LeaveLobby(Action onDone)
        {
            _active = null;
            _supabase.Rpc("leave_lobby", "{}",
                onSuccess: _ => onDone?.Invoke(),
                onError: _ => onDone?.Invoke()); // best-effort: server sweep covers stragglers
        }

        public void GetLobby(string code, Action<LobbyInfo> onSuccess, Action<string> onError)
        {
            string clean = (code ?? "").Trim().ToUpperInvariant();
            _supabase.Rpc("get_lobby", $"{{\"lobby_code\":\"{clean}\"}}",
                onSuccess: resp =>
                {
                    var rows = JsonArray.FromJson<LobbyRow>(resp);
                    var info = rows.Length > 0 ? ToInfo(rows[0]) : null;
                    if (info == null) { onError?.Invoke("not_found"); return; }
                    if (_active != null && _active.code == info.code) _active = info;
                    onSuccess?.Invoke(info);
                },
                onError: err => onError?.Invoke(ErrorToken(err)));
        }

        public void StartLobbyRace(Action onSuccess, Action<string> onError)
        {
            _supabase.RpcWithRetry("start_lobby_race", "{}",
                onSuccess: _ =>
                {
                    if (_active != null) { _active.status = "racing"; _active.startedAt = DateTime.UtcNow; }
                    onSuccess?.Invoke();
                },
                onError: err => onError?.Invoke(ErrorToken(err)));
        }

        /// <summary>Called by LobbyUIController when the player abandons the party flow.</summary>
        public void ClearActive() => _active = null;
    }
}
