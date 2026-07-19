using System;
using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// Per-frame trail-vs-movement collision check (spec §7.3). Tests the player's last
    /// movement segment against every nearby trail segment via 2D XZ intersection, then
    /// confirms with point-to-segment distance and an altitude gate. Fires
    /// <see cref="OnCollisionDetected"/> with the owning player's id.
    ///
    /// Reentrancy guard: a single check can't overlap itself (one frame's query finishes
    /// before another begins).
    ///
    /// ─── Lightfield migration (active decisions N + T, 2026-07-18) ────────────
    /// Decision T (tail geometry): the collision threshold now DERIVES from the authoritative
    /// tail radius via <see cref="ITailAuthority.FrozenTailRadius"/> × 2 (resolved from the
    /// <see cref="ServiceLocator"/>). This fixes the v1 bug where <c>collisionThreshold</c> and
    /// <c>trailWidth</c> were decoupled — a host could set a wide visual ribbon with a narrow
    /// collision radius and the runner would clip through their own tail. Falls back to
    /// <see cref="GameConfig.collisionThreshold"/> when no authority is registered (e.g. an editor
    /// scene that hasn't bootstrapped the match core) so playmode still works.
    ///
    /// Decision N (no speed limit + sweep subdivision): there is no movement speed cap. A long
    /// teleport or fast vehicle move is SUBDIVIDED into ≤ <see cref="GameConfig.sweepSubdivideMaxStepMeters"/>
    /// sub-segments via <see cref="SubdivideSweep"/> and each is tested independently, so a long
    /// sweep can't jump PAST a trail between frames. The candidate query radius is expanded by
    /// half the sweep length (centred on the sweep midpoint) so trails the sweep passes over are
    /// actually returned by <c>GetTrailSegmentsNear</c>.
    ///
    /// Pitfall #1 (self-collision grace direction) is preserved unchanged: the newest N segments
    /// of the LOCAL player's trail are skipped (shared endpoints with the movement segment) but
    /// older self-segments are still tested so looping over your own trail crashes you.
    /// </summary>
    public class TrailCollisionDetector : MonoBehaviour
    {
        /// <summary>Raised with the id of the player whose trail was hit.</summary>
        public event System.Action<string> OnCollisionDetected;

        [Tooltip("If set, this detector is the no-Fusion fallback (spec §8.4). It runs only while no local-authority NetworkPlayer exists.")]
        [SerializeField] private bool isFallback = false;

        private readonly List<TrailSegment> _candidates = new List<TrailSegment>(64);
        // Decision N: sub-segments of the current sweep (prev→cur), filled per check.
        private readonly List<(GeoPoint, GeoPoint)> _sweepBuffer = new List<(GeoPoint, GeoPoint)>(8);
        // Decision N: candidate segments pre-converted to world space once per check (so each
        // sub-segment test doesn't re-convert them). (startWorld, endWorld, ownerId).
        private readonly List<(Vector3, Vector3, string)> _candidateWorldBuffer = new List<(Vector3, Vector3, string)>(64);
        private bool _isChecking;

        private double _runStartTimestamp = -1.0;
        private GeoPoint _prevPos;
        private bool _havePrev;

        public bool IsFallback => isFallback;
        public bool IsEnabled { get; set; } = true;

        /// <summary>Mark this detector as the no-Fusion fallback (spec §8.4).</summary>
        public void SetFallback() => isFallback = true;

        public void BeginRun(GeoPoint startPos)
        {
            _prevPos = startPos;
            _havePrev = true;
            _runStartTimestamp = Time.timeAsDouble;
        }

        public void EndRun()
        {
            _havePrev = false;
            _runStartTimestamp = -1.0;
        }

        /// <summary>
        /// Run the check for one movement step. Spec §7.3. Decisions N + T (Lightfield migration):
        /// the near-gate threshold derives from the authoritative tail radius when an
        /// <see cref="ITailAuthority"/> is registered, and the sweep (prevPos→playerPos) is
        /// subdivided via <see cref="SubdivideSweep"/> so a long teleport / vehicle move is tested
        /// segment-by-segment instead of jumping past a trail.
        /// </summary>
        public void CheckCollision(GeoPoint playerPos, GeoPoint prevPos, string localPlayerId)
        {
            if (!IsEnabled) return;
            if (_isChecking) return; // reentrancy guard
            if (!TrailManager.HasInstance) return;

            GameConfig cfg = GameConfig.Active;
            var mgr = TrailManager.Instance;

            // Run-start grace (spec §4.4): for trailGracePeriod seconds after Start Run, no
            // collision fires at all — GPS needs a beat to settle, and a noisy first fix can
            // otherwise land the player on top of a trail and kill the run at t=0.
            if (mgr.LocalTrail != null && mgr.RunElapsedSeconds < cfg.trailGracePeriod) return;

            // Decision T: derive the near-gate threshold from the authoritative tail radius.
            // Falls back to the legacy config threshold when no authority is registered so editor
            // playmode still works before the match core boots.
            float thr = ResolveCollisionThreshold();

            // Decision N: subdivide the sweep so a long teleport / fast vehicle move can't skip
            // over a trail between frames. Each sub-segment is tested independently below.
            CoordinateConverter.EnsureReference(playerPos);
            _sweepBuffer.Clear();
            foreach (var sub in SubdivideSweep(prevPos, playerPos, cfg.sweepSubdivideMaxStepMeters))
                _sweepBuffer.Add(sub);

            // Expand the candidate query radius to cover the whole sweep: query from the sweep
            // midpoint with radius = (half the sweep length + collisionCheckRadius + thr slack).
            // This is what makes a long sweep actually find the trails it passes over.
            GeoPoint sweepMid = new GeoPoint(
                (prevPos.latitude + playerPos.latitude) * 0.5,
                (prevPos.longitude + playerPos.longitude) * 0.5,
                (prevPos.altitude + playerPos.altitude) * 0.5);
            double halfSweep = prevPos.HorizontalDistanceTo(playerPos) * 0.5;
            double queryRadius = cfg.collisionCheckRadius + halfSweep + thr;

            _candidates.Clear();
            mgr.GetTrailSegmentsNear(
                center: sweepMid,
                radius: queryRadius,
                excludePlayerId: null, // we want self included (older self-segments crash you)
                skipRecent: cfg.selfCollisionSkipPoints,
                results: _candidates);

            _isChecking = true;
            try
            {
                // Pre-convert candidate segments to world space once (independent of sub-segment).
                _candidateWorldBuffer.Clear();
                foreach (var seg in _candidates)
                {
                    Vector3 sAw = CoordinateConverter.GeoToWorld(seg.Start);
                    Vector3 sBw = CoordinateConverter.GeoToWorld(seg.End);
                    _candidateWorldBuffer.Add((sAw, sBw, seg.OwnerId));
                }

                float thr2 = thr * 2f;

                // Test each sub-segment of the sweep against every candidate. One hit is enough.
                foreach (var sub in _sweepBuffer)
                {
                    Vector3 wA = CoordinateConverter.GeoToWorld(sub.Item1);
                    Vector3 wB = CoordinateConverter.GeoToWorld(sub.Item2);
                    Vector2 pA = new Vector2(wA.x, wA.z);
                    Vector2 pB = new Vector2(wB.x, wB.z);
                    float minPlayerY = Mathf.Min(wA.y, wB.y);

                    foreach (var cw in _candidateWorldBuffer)
                    {
                        Vector2 sA = new Vector2(cw.Item1.x, cw.Item1.z);
                        Vector2 sB = new Vector2(cw.Item2.x, cw.Item2.z);

                        // 2D segment intersection on XZ plane (spec §7.3).
                        bool intersect = SegmentsIntersect2D(pA, pB, sA, sB);
                        // Distance gate (catches near-misses / coincident points).
                        double d1 = PointToSegmentDistance(pA, sA, sB);
                        double d2 = PointToSegmentDistance(pB, sA, sB);
                        bool near = (d1 < thr) || (d2 < thr) || intersect;

                        if (!near) continue;

                        // Height gate: trails live on a band; ignore segments too far above/below.
                        float minSegY = Mathf.Min(cw.Item1.y, cw.Item2.y);
                        float maxSegY = Mathf.Max(cw.Item1.y, cw.Item2.y);
                        float dy = Mathf.Max(0f, minSegY - minPlayerY, minPlayerY - maxSegY);
                        if (dy > thr2) continue;

                        // Local player crossing their own older trail is also a crash — that's the
                        // skip direction invariant (pitfall #1). Don't filter by ownerId == localPlayerId.
                        OnCollisionDetected?.Invoke(cw.Item3);
                        return; // one crash is enough
                    }
                }
            }
            finally
            {
                _isChecking = false;
            }
        }

        /// <summary>
        /// Resolve the near-gate threshold (decision T). Uses the authoritative tail radius
        /// (<c>FrozenTailRadius × 2</c>) when an <see cref="ITailAuthority"/> is registered on the
        /// <see cref="ServiceLocator"/>; otherwise falls back to
        /// <see cref="GameConfig.collisionThreshold"/>. The ×2 covers the symmetric near-test
        /// (head radius + tail radius; we model the head as a point so head touches tail when
        /// their combined radii overlap, which for a unit-radius head is ≈ tail radius × 2).
        /// </summary>
        private static float ResolveCollisionThreshold()
        {
            if (ServiceLocator.TryGet<ITailAuthority>(out var authority) && authority != null)
            {
                float r = authority.FrozenTailRadius;
                if (r > 0f) return r * 2f;
            }
            return GameConfig.Active.collisionThreshold;
        }

        /// <summary>
        /// Subdivide a movement sweep (decision N) into sub-segments each no longer than
        /// <paramref name="maxStepMeters"/>. Returns pairs of <see cref="GeoPoint"/>s; the
        /// concatenation traces prev→cur. Yields the original (prev, cur) pair as a single result
        /// when the sweep is short or when <paramref name="maxStepMeters"/> is non-positive.
        ///
        /// Invariant (test-backed): the total horizontal length of the subdivided chain ≈ the
        /// original sweep length, within one sub-segment's worth of equirectangular round-trip
        /// error (sub-millimetre at city scale). This is the property that makes a long teleport
        /// safe — every metre of the sweep is actually tested.
        ///
        /// Pure static — no MonoBehaviour state touched — so it is unit-testable in isolation
        /// (see SweepSubdivisionTests).
        /// </summary>
        public static IEnumerable<(GeoPoint, GeoPoint)> SubdivideSweep(GeoPoint prev, GeoPoint cur, float maxStepMeters)
        {
            if (maxStepMeters <= 0f)
            {
                yield return (prev, cur);
                yield break;
            }

            double total = prev.HorizontalDistanceTo(cur);
            if (total <= maxStepMeters)
            {
                yield return (prev, cur);
                yield break;
            }

            // Number of sub-segments (each ≤ maxStepMeters). Use ceil so the last step is the
            // remainder, never over the cap. The interpolation is done in the equirectangular
            // local-metre space (consistent with CoordinateConverter's planar approximation), so
            // sub-segments are straight in World space — same as a single long segment would be.
            int steps = (int)Math.Ceiling(total / maxStepMeters);
            if (steps < 1) steps = 1;

            // Local planar basis around `prev` (equirectangular; matches CoordinateConverter).
            const double EarthR = GeoPoint.EarthRadiusMeters;
            double metersPerDegLat = Math.PI * EarthR / 180.0;
            double cosLat = Math.Cos(prev.latitude * Math.PI / 180.0);
            double metersPerDegLon = metersPerDegLat * cosLat;

            double dLatTotal = cur.latitude - prev.latitude;
            double dLonTotal = cur.longitude - prev.longitude;
            double dAltTotal = cur.altitude - prev.altitude;

            GeoPoint a = prev;
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                GeoPoint b = new GeoPoint(
                    prev.latitude + dLatTotal * t,
                    prev.longitude + dLonTotal * t,
                    prev.altitude + dAltTotal * t);
                yield return (a, b);
                a = b;
            }
        }

        /// <summary>Standard 2D segment-segment intersection test (CCW orientation).</summary>
        public static bool SegmentsIntersect2D(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
        {
            int o1 = Orient(a1, a2, b1);
            int o2 = Orient(a1, a2, b2);
            int o3 = Orient(b1, b2, a1);
            int o4 = Orient(b1, b2, a2);

            if (o1 != o2 && o3 != o4) return true;

            // Collinear-trivial cases (a point lies on the other segment).
            if (o1 == 0 && OnSegment(a1, b1, a2)) return true;
            if (o2 == 0 && OnSegment(a1, b2, a2)) return true;
            if (o3 == 0 && OnSegment(b1, a1, b2)) return true;
            if (o4 == 0 && OnSegment(b1, a2, b2)) return true;

            return false;
        }

        private static int Orient(Vector2 p, Vector2 q, Vector2 r)
        {
            // Cross product sign: >0 CCW, <0 CW, 0 collinear.
            double v = (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
            if (v > 0) return 1;
            if (v < 0) return -1;
            return 0;
        }

        private static bool OnSegment(Vector2 p, Vector2 q, Vector2 r)
        {
            // Assumes p,q,r collinear; true iff q is within the bounding box of p,r.
            return q.x <= Mathf.Max(p.x, r.x) && q.x >= Mathf.Min(p.x, r.x)
                && q.y <= Mathf.Max(p.y, r.y) && q.y >= Mathf.Min(p.y, r.y);
        }

        /// <summary>Euclidean distance from point p to segment [a,b].</summary>
        public static double PointToSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 1e-8f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            Vector2 proj = a + ab * t;
            return Vector2.Distance(p, proj);
        }
    }
}
