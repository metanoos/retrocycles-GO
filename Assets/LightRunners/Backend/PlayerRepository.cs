using System;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Backend
{
    /// <summary>
    /// Player row + run history persistence (spec §12.3 / §12.4). Rides on the SupabaseManager
    /// GO. <see cref="RecordRun"/> posts the <c>record_run</c> RPC with the full score
    /// breakdown; on failure after retries the payload queues to <see cref="PendingOpsQueue"/>
    /// (spec §21) — the summary panel never blocks on the network.
    ///
    /// After a successful record or fetch, the player's level is broadcast via
    /// <see cref="GameEvents"/> so the Beacon assembly can re-derive unlocks without a
    /// Backend→Beacon reference.
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

        /// <summary>Persist a finished run (spec §12.4). Fire-and-forget with offline queueing.</summary>
        public void RecordRun(TrailData trail, double durationSeconds, ScoreBreakdown score, bool crashed)
        {
            var sb = Supabase;
            if (trail == null) return;

            double distance = trail.TotalLength;
            double avgSpeed = durationSeconds > 0 ? distance / durationSeconds : 0;

            string json = "{"
                + $"\"distance_m\":{distance.ToInvariant()},"
                + $"\"duration_s\":{durationSeconds.ToInvariant()},"
                + $"\"avg_speed\":{avgSpeed.ToInvariant()},"
                + $"\"score_total\":{score.total},"
                + $"\"score_distance\":{score.distance},"
                + $"\"score_speed\":{score.speed},"
                + $"\"score_beauty\":{score.beauty},"
                + $"\"score_proximity\":{score.proximity},"
                + $"\"beacon_form\":{(int)trail.BeaconForm},"
                + $"\"crashed\":{(crashed ? "true" : "false")}"
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
    }
}
