using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Backend
{
    /// <summary>
    /// Trail persistence (spec §12.3). Lives as a component on the GameManager GO (spec §3.1).
    /// <see cref="BeginRun"/> creates the <c>trails</c> row and starts the auto-save loop
    /// (every trailSaveInterval, POST unsaved points in batches of 100);
    /// <see cref="SaveFullTrailOnCrash"/> flushes everything immediately;
    /// <see cref="FinalizeTrail"/> PATCHes totals/crash/end-geo/ended_at.
    /// Geo points are serialized as PostGIS <c>SRID=4326;POINT(lon lat alt)</c>.
    ///
    /// Failure policy (spec §21): auto-save batches are lossy (dropped after retries);
    /// finalize payloads queue to <see cref="PendingOpsQueue"/> so the run summary survives
    /// offline runs.
    /// </summary>
    public class TrailRepository : MonoBehaviour
    {
        private const int PointBatchSize = 100;

        private string _currentTrailId;
        private int _lastSavedSequence = -1;
        private TrailData _trail;
        private Coroutine _autoSave;

        private static SupabaseManager Supabase => SupabaseManager.HasInstance ? SupabaseManager.Instance : null;

        [Serializable]
        private class TrailRow
        {
            public string id;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Run lifecycle
        // ─────────────────────────────────────────────────────────────────────
        public void BeginRun(TrailData trail, string roomId)
        {
            var sb = Supabase;
            _trail = trail;
            _lastSavedSequence = -1;
            _currentTrailId = null;

            if (sb == null || !sb.IsConfigured || trail == null) return;

            var start = trail.PointCount > 0 ? trail.FirstPoint.position : default;
            string json = "{"
                + $"\"player_id\":\"{trail.OwnerId}\","
                + $"\"room_id\":{JsonString(roomId)},"
                + $"\"beacon_form\":{(int)trail.BeaconForm},"
                + $"\"color_rgb\":{JsonString(ColorUtility.ToHtmlStringRGB(trail.TrailColor))},"
                + $"\"start_geo\":{JsonString(EwktPoint(start))}"
                + "}";

            sb.Post("trails", json,
                onSuccess: resp =>
                {
                    var rows = JsonArray.FromJson<TrailRow>(resp);
                    if (rows.Length > 0) _currentTrailId = rows[0].id;
                    if (_autoSave != null) StopCoroutine(_autoSave);
                    _autoSave = StartCoroutine(CoAutoSave());
                },
                onError: err => Debug.LogWarning($"[TrailRepository] trails insert failed: {err}"),
                returnRepresentation: true);
        }

        private IEnumerator CoAutoSave()
        {
            var wait = new WaitForSecondsRealtime(GameConfig.Active.trailSaveInterval);
            while (_trail != null && _currentTrailId != null)
            {
                yield return wait;
                SaveUnsavedPoints();
            }
        }

        public void SaveFullTrailOnCrash(TrailData trail)
        {
            _trail = trail;
            SaveUnsavedPoints();
        }

        public void FinalizeTrail(TrailData trail, double durationSeconds, bool crashed, string crashCause)
        {
            if (_autoSave != null) { StopCoroutine(_autoSave); _autoSave = null; }
            var sb = Supabase;
            if (sb == null || !sb.IsConfigured || trail == null) { _trail = null; return; }

            var end = trail.PointCount > 0 ? trail.LastPoint.position : default;
            string json = "{"
                + $"\"total_distance\":{trail.TotalLength.ToInvariant()},"
                + $"\"point_count\":{trail.HighestAppliedSequence + 1},"
                + $"\"crashed\":{(crashed ? "true" : "false")},"
                + $"\"crash_cause\":{JsonString(crashCause)},"
                + $"\"end_geo\":{JsonString(EwktPoint(end))},"
                + $"\"ended_at\":\"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\""
                + "}";

            if (_currentTrailId != null)
            {
                sb.Patch($"trails?id=eq.{_currentTrailId}", json,
                    onSuccess: _ => { },
                    onError: err => Debug.LogWarning($"[TrailRepository] finalize failed: {err}"));
            }

            _trail = null;
            _currentTrailId = null;
            _lastSavedSequence = -1;
        }

        private void SaveUnsavedPoints()
        {
            var sb = Supabase;
            if (sb == null || !sb.IsConfigured || _trail == null || _currentTrailId == null) return;

            var pts = _trail.Points;
            var batch = new List<TrailPoint>(PointBatchSize);
            foreach (var p in pts)
            {
                if (p.ownerSequenceIndex <= _lastSavedSequence) continue;
                batch.Add(p);
                if (batch.Count >= PointBatchSize) { PostBatch(sb, batch); batch.Clear(); }
            }
            if (batch.Count > 0) PostBatch(sb, batch);
        }

        private void PostBatch(SupabaseManager sb, List<TrailPoint> batch)
        {
            var json = new StringBuilder("[");
            for (int i = 0; i < batch.Count; i++)
            {
                var p = batch[i];
                if (i > 0) json.Append(',');
                json.Append('{')
                    .Append($"\"trail_id\":\"{_currentTrailId}\",")
                    .Append($"\"lat\":{p.position.latitude.ToInvariant()},")
                    .Append($"\"lon\":{p.position.longitude.ToInvariant()},")
                    .Append($"\"alt\":{p.position.altitude.ToInvariant()},")
                    .Append($"\"sequence_index\":{p.ownerSequenceIndex}")
                    .Append('}');
            }
            json.Append(']');

            int highest = batch[batch.Count - 1].ownerSequenceIndex;
            sb.Post("trail_points", json.ToString(),
                onSuccess: _ => { if (highest > _lastSavedSequence) _lastSavedSequence = highest; },
                onError: err => Debug.LogWarning($"[TrailRepository] point batch dropped ({batch.Count} pts): {err}"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Nearby trails (AR feed, spec §12.3 / §11.2). Serves only the last 24 h (spec §23).
        // ─────────────────────────────────────────────────────────────────────
        [Serializable]
        private class NearbyPointRow
        {
            public string trail_id;
            public string color_rgb;
            public double lat;
            public double lon;
            public double alt;
            public int sequence_index;
        }

        public void LoadNearbyTrails(GeoPoint center, float radiusMeters, int maxTrails, Action<List<TrailSnapshot>, List<Color>> onLoaded)
        {
            var sb = Supabase;
            if (sb == null || !sb.IsConfigured) { onLoaded?.Invoke(new List<TrailSnapshot>(), new List<Color>()); return; }

            string json = "{"
                + $"\"center_lat\":{center.latitude.ToInvariant()},"
                + $"\"center_lon\":{center.longitude.ToInvariant()},"
                + $"\"radius_m\":{radiusMeters.ToInvariant()},"
                + $"\"max_trails\":{maxTrails}"
                + "}";

            sb.Rpc("get_nearby_trails", json,
                onSuccess: resp =>
                {
                    var rows = JsonArray.FromJson<NearbyPointRow>(resp);
                    var byTrail = new Dictionary<string, List<NearbyPointRow>>();
                    foreach (var r in rows)
                    {
                        if (!byTrail.TryGetValue(r.trail_id, out var list))
                            byTrail[r.trail_id] = list = new List<NearbyPointRow>();
                        list.Add(r);
                    }

                    var snapshots = new List<TrailSnapshot>(byTrail.Count);
                    var colors = new List<Color>(byTrail.Count);
                    foreach (var kvp in byTrail)
                    {
                        var list = kvp.Value;
                        list.Sort((a, b) => a.sequence_index.CompareTo(b.sequence_index));
                        var pts = new List<TrailPoint>(list.Count);
                        foreach (var r in list)
                            pts.Add(new TrailPoint(new GeoPoint(r.lat, r.lon, r.alt), 0, r.sequence_index));
                        snapshots.Add(TrailSnapshot.Encode(kvp.Key, pts, pts.Count > 0 ? pts[0].ownerSequenceIndex : 0, pts.Count));

                        Color c = Color.cyan;
                        if (list.Count > 0 && !string.IsNullOrEmpty(list[0].color_rgb))
                            ColorUtility.TryParseHtmlString("#" + list[0].color_rgb, out c);
                        colors.Add(c);
                    }
                    onLoaded?.Invoke(snapshots, colors);
                },
                onError: err =>
                {
                    Debug.LogWarning($"[TrailRepository] get_nearby_trails failed: {err}");
                    onLoaded?.Invoke(new List<TrailSnapshot>(), new List<Color>());
                });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────
        private static string EwktPoint(GeoPoint p)
            => StringUtils.FormatInvariant("SRID=4326;POINT({0} {1} {2})", p.longitude, p.latitude, p.altitude);

        private static string JsonString(string s)
            => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
