using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Backend
{
    public readonly struct MatchResultWrite
    {
        public string PlayerId { get; }
        public int Lumens { get; }
        public int FinishRank { get; }
        public string Role { get; }

        public MatchResultWrite(string playerId, int lumens, int finishRank, string role)
        {
            PlayerId = playerId ?? string.Empty;
            Lumens = lumens;
            FinishRank = finishRank;
            Role = role ?? string.Empty;
        }
    }

    /// <summary>
    /// Player row + run history + Lightfield match persistence (spec §12.3 / §12.4;
    /// decisions E/O for the match tables). Rides on the SupabaseManager GO.
    /// <see cref="RecordRun"/> posts the <c>record_run</c> RPC with the player's
    /// Lumen tally (decision E — integer Lumens, NOT the deprecated float
    /// ScoreBreakdown); on failure after retries the payload queues to
    /// <see cref="PendingOpsQueue"/> (spec §21) — the summary panel never blocks
    /// on the network.
    ///
    /// <see cref="RecordMatchResult"/> / <see cref="FinalizeMatch"/> /
    /// <see cref="FinalizeMatchWithResults"/> drive the <c>matches</c> +
    /// <c>match_players</c> tables (decision O: timed match, most Lumens wins). They mirror the
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

        // Match lifecycle RPC error tokens (mirrors LobbyServices.ErrorToken).
        private static readonly string[] MatchErrorTokens =
            {
                "not_authenticated", "not_host", "not_found", "bad_role",
                "bad_lumens", "bad_finish_rank", "bad_results", "host_already_exists",
                "match_id_conflict"
            };

        private static SupabaseManager Supabase => SupabaseManager.HasInstance ? SupabaseManager.Instance : null;

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
        /// Host-only: upsert a player's authoritative match result.
        /// <paramref name="role"/> is the lowercase PlayerRole
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

        /// <summary>
        /// Host-only atomic match close. The server writes every final player row and marks the
        /// match ended in one PostgreSQL transaction, so a finalized match cannot expose a
        /// partial standings table.
        /// </summary>
        public void FinalizeMatchWithResults(
            Guid matchId,
            string roomId,
            string hostPlayerId,
            IReadOnlyList<MatchResultWrite> results,
            string winnerPlayerId,
            int durationSeconds,
            Action onSuccess,
            Action<string> onError)
        {
            var sb = Supabase;
            if (matchId == Guid.Empty) { onError?.Invoke("bad_match_id"); return; }
            if (string.IsNullOrEmpty(hostPlayerId)) { onError?.Invoke("bad_host_id"); return; }
            if (results == null || results.Count == 0) { onError?.Invoke("bad_results"); return; }

            var rows = new StringBuilder("[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) rows.Append(',');
                MatchResultWrite result = results[i];
                rows.Append('{')
                    .Append("\"player_id\":").Append(JsonString(result.PlayerId)).Append(',')
                    .Append("\"lumens\":").Append(result.Lumens).Append(',')
                    .Append("\"finish_rank\":").Append(result.FinishRank).Append(',')
                    .Append("\"role\":").Append(JsonString(result.Role))
                    .Append('}');
            }
            rows.Append(']');

            string json = "{"
                + $"\"p_match_id\":\"{matchId}\","
                + $"\"p_room_id\":{JsonString(roomId)},"
                + $"\"p_host_player_id\":{JsonString(hostPlayerId)},"
                + $"\"p_results\":{rows},"
                + $"\"p_winner_player_id\":{JsonString(winnerPlayerId)},"
                + $"\"p_duration_seconds\":{Math.Max(0, durationSeconds)}"
                + "}";

            // This is the only durable operation for a match: one PostgreSQL transaction creates
            // the row, writes standings, and closes it. Nothing is persisted mid-match, so an app
            // kill cannot leave an open match. Stable-ID/idempotent replay covers lost responses.
            PendingOpsQueue.EnqueueUnique("finalize_match_with_results", json);
            if (sb == null || !sb.IsConfigured)
            {
                onError?.Invoke("offline");
                return;
            }

            sb.RpcWithRetry("finalize_match_with_results", json,
                onSuccess: _ =>
                {
                    PendingOpsQueue.Remove("finalize_match_with_results", json);
                    PendingOpsQueue.Flush(sb);
                    onSuccess?.Invoke();
                },
                onError: err =>
                {
                    string token = MatchErrorToken(err);
                    if (IsPermanentMatchWriteError(token))
                        PendingOpsQueue.Remove("finalize_match_with_results", json);
                    onError?.Invoke(token);
                });
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

        private static bool IsPermanentMatchWriteError(string token)
        {
            switch (token)
            {
                case "not_host":
                case "bad_role":
                case "bad_lumens":
                case "bad_finish_rank":
                case "bad_results":
                case "host_already_exists":
                case "match_id_conflict":
                    return true;
                default:
                    // Authentication expiry and transport/HTTP failures can recover after the
                    // user restores a session or connectivity returns.
                    return false;
            }
        }

        private static string JsonString(string s)
            => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
