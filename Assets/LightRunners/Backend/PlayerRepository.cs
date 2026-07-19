using System;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Backend
{
    /// <summary>
    /// Player row + run history + Lightfield match persistence (spec §12.3 / §12.4;
    /// decisions E/O for the match tables). Rides on the SupabaseManager GO.
    /// <see cref="RecordRun"/> posts the <c>record_run</c> RPC with the player's
    /// Lumen tally (decision E — integer Lumens, NOT the deprecated float
    /// ScoreBreakdown); on failure after retries the payload queues to
    /// <see cref="PendingOpsQueue"/> (spec §21) — the summary panel never blocks
    /// on the network.
    ///
    /// <see cref="CreateMatch"/> / <see cref="RecordMatchResult"/> /
    /// <see cref="FinalizeMatch"/> drive the <c>matches</c> + <c>match_players</c>
    /// tables (decision O: timed match, most Lumens wins). They mirror the
    /// lobby-RPC pattern in <see cref="LobbyServices"/>: <c>RpcWithRetry</c> +
    /// PostgREST error-token extraction surfaced verbatim to the caller.
    ///
    /// After a successful record or fetch, the player's level is broadcast via
    /// <see cref="GameEvents"/> so the Beacon assembly can re-derive unlocks
    /// without a Backend→Beacon reference.
    /// </summary>
    public class PlayerRepository : Singleton<PlayerRepository>
    {
        [Serializable]
        private class PlayerRow
        {
            public string id;
            public string display_name;
            public int level;
            public double total_distance;
            public int total_runs;
        }

        // PostgREST returns scalar RPC results as a bare JSON value, e.g.
        // create_match() returns the match UUID as `"abc-def"` (a quoted string).
        // JsonUtility can't parse a bare scalar, so we unwrap the quotes manually.
        // Match lifecycle RPC error tokens (mirrors LobbyServices.ErrorToken).
        private static readonly string[] MatchErrorTokens =
            { "not_authenticated", "not_host", "not_found", "bad_role" };

        private static SupabaseManager Supabase => SupabaseManager.HasInstance ? SupabaseManager.Instance : null;

        private void Start()
        {
            // Connectivity may have returned since the last session — flush queued writes (§21).
            PendingOpsQueue.Flush(Supabase);
        }

        public void RegisterOrUpdatePlayer(PlayerIdentity identity)
        {
            var sb = Supabase;
            if (sb == null || !sb.IsConfigured || !identity.IsValid) return;

            string json = "{"
                + $"\"id\":\"{identity.userId}\","
                + $"\"display_name\":\"{identity.displayName}\""
                + "}";
            sb.Upsert("players?on_conflict=id", json,
                onSuccess: _ => GetPlayer(identity.userId, null),
                onError: err => Debug.LogWarning($"[PlayerRepository] upsert failed: {err}"));
        }

        public void GetPlayer(string userId, Action<int> onLevel)
        {
            var sb = Supabase;
            if (sb == null || !sb.IsConfigured || string.IsNullOrEmpty(userId)) return;

            sb.Get($"players?id=eq.{userId}&select=id,display_name,level,total_distance,total_runs",
                onSuccess: resp =>
                {
                    var rows = JsonArray.FromJson<PlayerRow>(resp);
                    if (rows.Length == 0) return;
                    int level = rows[0].level;
                    onLevel?.Invoke(level);
                    GameEvents.RaisePlayerLevelChanged(level);
                },
                onError: err => Debug.LogWarning($"[PlayerRepository] get failed: {err}"));
        }

        /// <summary>
        /// Persist a finished run (spec §12.4). Fire-and-forget with offline
        /// queueing. <paramref name="lumens"/> is the player's Lumen tally for
        /// this run (decision E); the deprecated float ScoreBreakdown has been
        /// removed entirely.
        /// </summary>
        public void RecordRun(TrailData trail, double durationSeconds, int lumens, bool crashed)
        {
            var sb = Supabase;
            if (trail == null) return;

            double distance = trail.TotalLength;
            double avgSpeed = durationSeconds > 0 ? distance / durationSeconds : 0;

            string json = "{"
                + $"\"p_distance_m\":{distance.ToInvariant()},"
                + $"\"p_duration_s\":{durationSeconds.ToInvariant()},"
                + $"\"p_avg_speed\":{avgSpeed.ToInvariant()},"
                + $"\"p_lumens\":{lumens},"
                + $"\"p_beacon_form\":{(int)trail.BeaconForm},"
                + $"\"p_crashed\":{(crashed ? "true" : "false")}"
                + "}";

            if (sb == null || !sb.IsConfigured)
            {
                PendingOpsQueue.Enqueue("record_run", json);
                return;
            }

            sb.RpcWithRetry("record_run", json,
                onSuccess: _ =>
                {
                    // Level may have changed — refresh + rebroadcast, and drain any backlog.
                    if (!string.IsNullOrEmpty(sb.UserId)) GetPlayer(sb.UserId, null);
                    PendingOpsQueue.Flush(sb);
                },
                onError: err =>
                {
                    Debug.LogWarning($"[PlayerRepository] record_run queued after failure: {err}");
                    PendingOpsQueue.Enqueue("record_run", json);
                });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lightfield match lifecycle (decision O — matches / match_players tables).
        // Mirror the LobbyServices RPC style: RpcWithRetry + error-token extraction.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Host-only. Inserts a <c>matches</c> row + a host <c>match_players</c> row
        /// and returns the new match id. Call from <c>IMatchSession.BeginMatch</c>.
        /// </summary>
        public void CreateMatch(string roomId, string hostPlayerId, Action<Guid> onSuccess, Action<string> onError)
        {
            var sb = Supabase;
            if (sb == null || !sb.IsConfigured) { onError?.Invoke("offline"); return; }
            if (string.IsNullOrEmpty(hostPlayerId)) { onError?.Invoke("bad_host_id"); return; }

            string json = "{"
                + $"\"p_room_id\":{JsonString(roomId)},"
                + $"\"p_host_player_id\":{JsonString(hostPlayerId)}"
                + "}";

            sb.RpcWithRetry("create_match", json,
                onSuccess: resp =>
                {
                    Guid matchId = ParseUuidScalar(resp);
                    if (matchId == Guid.Empty) { onError?.Invoke("bad create_match response"); return; }
                    onSuccess?.Invoke(matchId);
                },
                onError: err => onError?.Invoke(MatchErrorToken(err)));
        }

        /// <summary>
        /// Upsert a player's match result. Host writes any player's row; a player
        /// may write their own. <paramref name="role"/> is the lowercase PlayerRole
        /// token (<c>runner</c>/<c>host</c>/<c>referee</c>).
        /// </summary>
        public void RecordMatchResult(Guid matchId, string playerId, int lumens, int finishRank, string role)
        {
            var sb = Supabase;
            if (sb == null || !sb.IsConfigured || matchId == Guid.Empty || string.IsNullOrEmpty(playerId)) return;

            string json = "{"
                + $"\"p_match_id\":\"{matchId}\","
                + $"\"p_player_id\":{JsonString(playerId)},"
                + $"\"p_lumens\":{lumens},"
                + $"\"p_finish_rank\":{finishRank},"
                + $"\"p_role\":{JsonString(role)}"
                + "}";

            sb.RpcWithRetry("record_match_result", json,
                onSuccess: _ => { },
                onError: err => Debug.LogWarning($"[PlayerRepository] record_match_result failed: {MatchErrorToken(err)}"));
        }

        /// <summary>
        /// Host-only. Sets <c>ended_at</c> / <c>winner_player_id</c> /
        /// <c>duration_seconds</c> on the match row. Call from
        /// <c>IMatchSession.EndMatch</c> (decision O).
        /// </summary>
        public void FinalizeMatch(Guid matchId, string winnerPlayerId, int durationSeconds, Action onSuccess, Action<string> onError)
        {
            var sb = Supabase;
            if (sb == null || !sb.IsConfigured) { onError?.Invoke("offline"); return; }
            if (matchId == Guid.Empty) { onError?.Invoke("bad_match_id"); return; }

            string json = "{"
                + $"\"p_match_id\":\"{matchId}\","
                + $"\"p_winner_player_id\":{JsonString(winnerPlayerId)},"
                + $"\"p_duration_seconds\":{durationSeconds}"
                + "}";

            sb.RpcWithRetry("finalize_match", json,
                onSuccess: _ => onSuccess?.Invoke(),
                onError: err => onError?.Invoke(MatchErrorToken(err)));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Pull the raise-exception token out of a PostgREST error body, if present
        /// (mirrors <see cref="LobbyServices"/> / SupabaseLobbyService.ErrorToken).
        /// </summary>
        private static string MatchErrorToken(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unknown";
            foreach (var token in MatchErrorTokens)
                if (raw.Contains(token)) return token;
            return raw;
        }

        /// <summary>
        /// Parse a scalar UUID returned by an RPC. PostgREST returns scalar RPC
        /// results as a bare quoted JSON string (<c>"abc-def"</c>) or, for some
        /// configurations, a JSON object. Handle the quoted-string shape; fall
        /// back to Guid.TryParse on the raw text. Returns Guid.Empty on failure.
        /// </summary>
        private static Guid ParseUuidScalar(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return Guid.Empty;
            string trimmed = resp.Trim();
            if (trimmed.StartsWith("\"") && trimmed.EndsWith("\"") && trimmed.Length >= 2)
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            return Guid.TryParse(trimmed, out var g) ? g : Guid.Empty;
        }

        private static string JsonString(string s)
            => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
