using System;
using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// Per-frame trail-vs-movement collision check (spec §7.3). Tests the player's last
    /// movement segment against every nearby trail segment via continuous 3D
    /// segment-to-segment distance. Fires
    /// <see cref="OnCollisionDetected"/> with the owning player's id.
    ///
    /// Reentrancy guard: a single check can't overlap itself (one frame's query finishes
    /// before another begins).
    ///
    /// ─── Lightfield migration (active decisions N + T, 2026-07-18) ────────────
    /// Decision T (tail geometry): the collision threshold is the host tail radius plus the
    /// locked 2 m player radius, both carried by <see cref="FrozenMatchConfig"/>. This fixes the
    /// v1 symmetric-radius approximation and falls back to the validated default contract when
    /// no authority is registered.
    ///
    /// Decision N (no speed limit + sweep subdivision): there is no movement speed cap. A long
    /// teleport or fast vehicle move is SUBDIVIDED into locked 4 m microsegments
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

        public bool IsFallback => isFallback;
        public bool IsEnabled { get; set; } = true;

        /// <summary>Mark this detector as the no-Fusion fallback (spec §8.4).</summary>
        public void SetFallback() => isFallback = true;

        public void BeginRun(GeoPoint startPos)
        {
            _isChecking = false;
            _candidates.Clear();
            _sweepBuffer.Clear();
            _candidateWorldBuffer.Clear();
        }

        public void EndRun()
        {
            _isChecking = false;
            _candidates.Clear();
            _sweepBuffer.Clear();
            _candidateWorldBuffer.Clear();
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
            //
            // Round-1 review fix R2-F9: emergenceGraceSeconds (decision D's Lightfield-specific
            // match-start grace) was declared in GameConfig but never read — hosts tuning it saw
            // no effect. Take the max of the two so either config knob works; emergenceGrace
            // extends trailGrace for matches without shortening it for solo runs.
            float graceSeconds = Mathf.Max(cfg.trailGracePeriod, cfg.emergenceGraceSeconds);
            if (mgr.LocalTrail != null && mgr.RunElapsedSeconds < graceSeconds) return;

            // Round-1 review fix R2-F10: respawn invulnerability (decision F). MatchManager sets a
            // short invulnerability window after a crash so the respawning runner can move off the
            // crash site without chain-dying against the same trail web. The window was set but
            // never consulted here. Resolve MatchManager reflectively (Trail doesn't reference
            // Gameplay) to avoid an asmdef cycle; null-safe.
            if (IsLocalRunnerInvulnerable()) return;

            // Decision T: exact capsule overlap threshold = tail radius + fixed head radius.
            float thr = ResolveHeadToTrailCollisionDistance();

            // Decision N: subdivide the sweep so a long teleport / fast vehicle move can't skip
            // over a trail between frames. Each sub-segment is tested independently below.
            CoordinateConverter.EnsureReference(playerPos);
            _sweepBuffer.Clear();
            foreach (var sub in SubdivideSweep(
                         prevPos,
                         playerPos,
                         FrozenMatchConfig.Default.CollisionMicrosegmentMeters))
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

                // Test each sub-segment of the sweep against every candidate. One hit is enough.
                foreach (var sub in _sweepBuffer)
                {
                    Vector3 wA = CoordinateConverter.GeoToWorld(sub.Item1);
                    Vector3 wB = CoordinateConverter.GeoToWorld(sub.Item2);

                    foreach (var cw in _candidateWorldBuffer)
                    {
                        // Continuous 3D capsule test: the swept head spine overlaps the tail
                        // spine when their shortest distance is at or inside the combined radii.
                        if (SegmentToSegmentDistance(wA, wB, cw.Item1, cw.Item2) > thr)
                            continue;

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
        /// Resolve the exact head-to-trail capsule overlap distance (decision T).
        /// </summary>
        public static float ResolveHeadToTrailCollisionDistance()
        {
            if (ServiceLocator.TryGet<ITailAuthority>(out var authority) && authority != null)
                return authority.FrozenConfig.HeadToTrailCollisionMeters;
            return FrozenMatchConfig.Default.HeadToTrailCollisionMeters;
        }

        /// <summary>
        /// Shortest Euclidean distance between two finite 3D segments. Handles point segments,
        /// parallel segments, crossings, and arbitrary altitude; this is the live swept-capsule
        /// geometric truth used by <see cref="CheckCollision"/>.
        /// </summary>
        public static double SegmentToSegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            const double epsilon = 1e-10;
            Vector3 u = q1 - p1;
            Vector3 v = q2 - p2;
            Vector3 w = p1 - p2;
            double a = Vector3.Dot(u, u);
            double b = Vector3.Dot(u, v);
            double c = Vector3.Dot(v, v);
            double d = Vector3.Dot(u, w);
            double e = Vector3.Dot(v, w);

            if (a <= epsilon && c <= epsilon)
                return Vector3.Distance(p1, p2);
            if (a <= epsilon)
            {
                double t = Math.Max(0.0, Math.Min(1.0, e / c));
                return Vector3.Distance(p1, p2 + v * (float)t);
            }
            if (c <= epsilon)
            {
                double s = Math.Max(0.0, Math.Min(1.0, -d / a));
                return Vector3.Distance(p1 + u * (float)s, p2);
            }

            double denominator = a * c - b * b;
            double sNumerator;
            double sDenominator = denominator;
            double tNumerator;
            double tDenominator = denominator;

            if (denominator <= epsilon)
            {
                sNumerator = 0.0;
                sDenominator = 1.0;
                tNumerator = e;
                tDenominator = c;
            }
            else
            {
                sNumerator = b * e - c * d;
                tNumerator = a * e - b * d;
                if (sNumerator < 0.0)
                {
                    sNumerator = 0.0;
                    tNumerator = e;
                    tDenominator = c;
                }
                else if (sNumerator > sDenominator)
                {
                    sNumerator = sDenominator;
                    tNumerator = e + b;
                    tDenominator = c;
                }
            }

            if (tNumerator < 0.0)
            {
                tNumerator = 0.0;
                if (-d < 0.0)
                    sNumerator = 0.0;
                else if (-d > a)
                    sNumerator = sDenominator;
                else
                {
                    sNumerator = -d;
                    sDenominator = a;
                }
            }
            else if (tNumerator > tDenominator)
            {
                tNumerator = tDenominator;
                if (-d + b < 0.0)
                    sNumerator = 0.0;
                else if (-d + b > a)
                    sNumerator = sDenominator;
                else
                {
                    sNumerator = -d + b;
                    sDenominator = a;
                }
            }

            double sc = Math.Abs(sNumerator) <= epsilon ? 0.0 : sNumerator / sDenominator;
            double tc = Math.Abs(tNumerator) <= epsilon ? 0.0 : tNumerator / tDenominator;
            Vector3 closestDelta = w + (float)sc * u - (float)tc * v;
            return closestDelta.magnitude;
        }

        /// <summary>
        /// Round-1 review fix R2-F10: consult MatchManager's respawn-inulnerability window so a
        /// respawning runner doesn't chain-die against the trail web that just killed them. Trail
        /// can't reference Gameplay (asmdef cycle), so resolve via reflective ServiceLocator lookup
        /// by interface name — null-safe (solo runs without MatchManager always return false).
        /// </summary>
        private static bool IsLocalRunnerInvulnerable()
        {
            try
            {
                object session = ServiceLocator.GetByInterfaceName("LightRunners.Core.IMatchSession");
                if (session == null) return false;
                // Reflectively read an "IsLocalRunnerInvulnerable" bool property if present.
                var prop = session.GetType().GetProperty("IsLocalRunnerInvulnerable");
                if (prop == null) return false;
                return prop.GetValue(session) is bool b && b;
            }
            catch
            {
                return false;
            }
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
