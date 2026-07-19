using System;
using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// Singleton owning all live trails. Spec §7.2.
    ///
    /// Invariants baked in (these are the v1 lessons — spec §17):
    ///  • <see cref="StartRun"/> is idempotent for the same owner+form+color so the dual
    ///    GameManager/NetworkPlayer callers don't wipe each other's opening segment (#2).
    ///  • <see cref="UpdateRemoteTrail"/> is an append-only merge keyed on the per-trail
    ///    sequence cursor, so out-of-order batches are idempotent AND the merge survives
    ///    pruning (#4, #19).
    ///  • <see cref="GetTrailSegmentsNear"/> skips the *newest* N segments of the local
    ///    player's trail (grace period for just-laid segments that share an endpoint with
    ///    the movement segment) **but keeps checking older self-segments** so looping over
    ///    your own trail crashes you (#1). Discontinuity pairs (spec §20) are never
    ///    returned as segments.
    /// </summary>
    public class TrailManager : Singleton<TrailManager>
    {
        /// <summary>A GPS silence longer than this marks the next local point as a segment start (spec §20).</summary>
        private const double DiscontinuityGapSeconds = 10.0;

        private readonly Dictionary<string, TrailData> _trails = new Dictionary<string, TrailData>();
        private TrailData _localTrail;

        // Run bookkeeping
        private double _runStartTimestamp = -1.0;
        private int _localNextSequenceIndex;
        private bool _forceDiscontinuity;

        public TrailData LocalTrail => _localTrail;
        public IReadOnlyDictionary<string, TrailData> AllTrails => _trails;

        /// <summary>Elapsed run time in seconds, or 0 if not running.</summary>
        public double RunElapsedSeconds
            => _runStartTimestamp < 0 ? 0 : Time.timeAsDouble - _runStartTimestamp;

        /// <summary>Raised when the local trail gains a point. Map and AR subscribe.</summary>
        public event Action<TrailPoint> OnLocalPointAdded;
        /// <summary>Raised when a remote trail gains points.</summary>
        public event Action<string> OnRemoteTrailUpdated;
        /// <summary>Raised when a remote trail is removed (player left).</summary>
        public event Action<string> OnRemoteTrailRemoved;
        /// <summary>Raised when the local trail ends — <c>crashed</c> distinguishes a crash from a voluntary end.</summary>
        public event Action<bool> OnTrailCrashed;

        // ─────────────────────────────────────────────────────────────────────
        // Run lifecycle
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Begin (or continue) a run for the local player. Idempotent for the same
        /// owner+form+color — two callers (GameManager.StartRun and local NetworkPlayer.Spawned)
        /// can both hit this; the late call must not wipe the opening segment (spec §7.2, pitfall #2).
        /// </summary>
        public void StartRun(string playerId, BeaconFormType form, Color color)
        {
            if (_localTrail != null && _localTrail.IsSameRun(playerId, form, color))
            {
                // Same run already in progress — keep existing points.
                return;
            }

            _localTrail = new TrailData(playerId, form, color);
            _trails[playerId] = _localTrail;
            _localNextSequenceIndex = 0;
            _runStartTimestamp = Time.timeAsDouble;
            _forceDiscontinuity = false;

            if (Location.LocationProvider.HasInstance)
            {
                var pos = Location.LocationProvider.Instance.CurrentPosition;
                if (pos.latitude != 0.0 || pos.longitude != 0.0)
                    AppendLocalPoint(pos);
            }
        }

        /// <summary>Called by <c>LocationProvider.OnPositionUpdated</c>. Appends a point to the local trail.</summary>
        public void OnLocationUpdate(GeoPoint geo)
        {
            if (_localTrail == null) return;
            AppendLocalPoint(geo);
        }

        /// <summary>
        /// Mark the *next* local point as a segment start (spec §20) — called by GameManager on
        /// resume from <c>Paused</c> so the pause gap never becomes a phantom wall.
        /// </summary>
        public void MarkDiscontinuity() => _forceDiscontinuity = true;

        /// <summary>
        /// Shift the run clock back by <paramref name="seconds"/> so time spent backgrounded
        /// still counts toward run duration (spec §20 — <c>Time.timeAsDouble</c> freezes while
        /// the app is paused).
        /// </summary>
        public void AdjustRunStart(double seconds)
        {
            if (_runStartTimestamp >= 0) _runStartTimestamp -= Math.Max(0.0, seconds);
        }

        private void AppendLocalPoint(GeoPoint geo)
        {
            double now = Time.timeAsDouble;

            bool segmentStart = _forceDiscontinuity;
            if (!segmentStart && _localTrail.PointCount > 0
                && now - _localTrail.LastPoint.timestamp > DiscontinuityGapSeconds)
            {
                segmentStart = true; // GPS dropout — don't bridge the gap (spec §20)
            }
            _forceDiscontinuity = false;

            var point = new TrailPoint(geo, now, _localNextSequenceIndex++, segmentStart);
            _localTrail.AddPoint(point);

            GameConfig cfg = GameConfig.Active;
            _localTrail.PruneTo(cfg.maxTrailPoints);

            OnLocalPointAdded?.Invoke(point);
        }

        /// <summary>
        /// Append-only merge of a remote batch by sequence cursor (spec §7.2 / §8.2). Each
        /// trail's <see cref="TrailData.HighestAppliedSequence"/> gates the append, so
        /// overlapping or out-of-order batches are idempotent and pruning can't corrupt the
        /// merge (pitfall #19). Remote trails are pruned to the same cap as the local one.
        /// </summary>
        public void UpdateRemoteTrail(string playerId, TrailSnapshot snapshot)
        {
            if (snapshot == null) return;

            if (!_trails.TryGetValue(playerId, out var trail) || trail == null)
            {
                // First sight of this remote: invent a placeholder form/color (the networked
                // form field will arrive via NetworkPlayer; the map just needs a colour).
                trail = new TrailData(playerId, BeaconFormType.Sphere, BeaconFormData.Magenta);
                _trails[playerId] = trail;
            }

            int before = trail.HighestAppliedSequence;
            trail.AddPoints(snapshot.Decode());
            if (trail.HighestAppliedSequence != before)
            {
                trail.PruneTo(GameConfig.Active.maxTrailPoints);
                OnRemoteTrailUpdated?.Invoke(playerId);
            }
        }

        /// <summary>Update a remote trail's visual identity once the networked form arrives (spec §8.3).</summary>
        public void SetRemoteTrailStyle(string playerId, BeaconFormType form, Color color)
        {
            if (_trails.TryGetValue(playerId, out var trail) && trail != null && trail != _localTrail)
            {
                // TrailData identity fields are set at construction; recreate metadata by
                // transferring points only if the style actually changed.
                if (trail.BeaconForm == form && trail.TrailColor == color) return;
                var replacement = new TrailData(playerId, form, color);
                replacement.AddPoints(trail.Points);
                _trails[playerId] = replacement;
                OnRemoteTrailUpdated?.Invoke(playerId);
            }
        }

        public void RemoveRemoteTrail(string playerId)
        {
            if (_trails.Remove(playerId) && playerId != (_localTrail?.OwnerId))
                OnRemoteTrailRemoved?.Invoke(playerId);
        }

        /// <summary>End the run. Fires <see cref="OnTrailCrashed"/>; clears the local trail (remote trails stay).</summary>
        public void EndRun(bool crashed)
        {
            if (_localTrail == null) return;
            OnTrailCrashed?.Invoke(crashed);

            string ownerId = _localTrail.OwnerId;
            _trails.Remove(ownerId);
            _localTrail = null;
            _runStartTimestamp = -1.0;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Spatial queries
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fill <paramref name="results"/> with all trail segments within <paramref name="radius"/>
        /// metres of <paramref name="center"/>, across every trail. For the *local* player's
        /// trail, the newest <paramref name="skipRecent"/> segments are skipped (grace period:
        /// the segments just laid share endpoints with the current movement segment and would
        /// cause instant crash-on-turn) **but older self-segments are still returned** so looping
        /// over your own trail crashes you. Spec §7.2, pitfall #1. Discontinuity pairs are
        /// never returned (spec §20).
        /// </summary>
        public void GetTrailSegmentsNear(
            GeoPoint center,
            double radius,
            string excludePlayerId,
            int skipRecent,
            List<TrailSegment> results)
        {
            if (results == null) return;
            double r2 = radius * radius;

            foreach (var kvp in _trails)
            {
                string ownerId = kvp.Key;
                var trail = kvp.Value;
                var points = trail.Points;
                if (points.Count < 2) continue;

                // For the local player: how many trailing segments to skip (grace period).
                // We compute the index of the first segment to *include*; segments at indices
                // ≥ (count-1 - skipRecent) are skipped. This skips the newest segments only.
                bool isLocal = _localTrail != null && ownerId == _localTrail.OwnerId;
                int segCount = points.Count - 1;
                int skip = isLocal ? Math.Max(0, skipRecent) : 0;
                int firstIncludedSeg = segCount - skip; // exclusive lower bound, segments [0 .. firstIncludedSeg)
                // We iterate i in [0, segCount); include only if i < firstIncludedSeg.

                for (int i = 0; i < segCount; i++)
                {
                    if (isLocal && i >= firstIncludedSeg) continue; // newest N skipped (grace)
                    if (ownerId == excludePlayerId && !isLocal) continue; // excluded remote owner
                    if (points[i + 1].isSegmentStart) continue; // discontinuity — not a wall (spec §20)

                    GeoPoint a = points[i].position;
                    GeoPoint b = points[i + 1].position;

                    // Coarse distance gate: midpoint Haversine to center.
                    GeoPoint mid = new GeoPoint(
                        (a.latitude + b.latitude) * 0.5,
                        (a.longitude + b.longitude) * 0.5,
                        (a.altitude + b.altitude) * 0.5);
                    double dMid = center.HorizontalDistanceTo(mid);
                    if (dMid > radius + SegLen(a, b)) continue;
                    if (dMid * dMid > r2 && dMid > radius) continue;

                    results.Add(new TrailSegment(a, b, ownerId));
                }
            }
        }

        private static double SegLen(GeoPoint a, GeoPoint b) => a.HorizontalDistanceTo(b);

        /// <summary>Count of distinct players with a live trail (including the local player).</summary>
        public int LivePlayerCount => _trails.Count;

        /// <summary>
        /// Number of remote players whose live position (their trail's newest point) is within
        /// <paramref name="radius"/> metres. Sampled periodically during the run by GameManager
        /// with <c>proximityRadius</c> for the §7.4 proximity axis (pitfall #17 — never sample
        /// this once at end-of-run).
        /// </summary>
        public int CountPlayersNear(GeoPoint center, double radius, string excludePlayerId)
        {
            int n = 0;
            foreach (var kvp in _trails)
            {
                if (kvp.Key == excludePlayerId) continue;
                if (kvp.Value.Points.Count == 0) continue;
                var last = kvp.Value.LastPoint.position;
                if (center.HorizontalDistanceTo(last) <= radius) n++;
            }
            return n;
        }
    }
}
