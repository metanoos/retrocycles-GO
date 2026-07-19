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
    /// </summary>
    public class TrailCollisionDetector : MonoBehaviour
    {
        /// <summary>Raised with the id of the player whose trail was hit.</summary>
        public event System.Action<string> OnCollisionDetected;

        [Tooltip("If set, this detector is the no-Fusion fallback (spec §8.4). It runs only while no local-authority NetworkPlayer exists.")]
        [SerializeField] private bool isFallback = false;

        private readonly List<TrailSegment> _candidates = new List<TrailSegment>(64);
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
        /// Run the check for one movement step. Spec §7.3.
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
            _candidates.Clear();
            mgr.GetTrailSegmentsNear(
                center: playerPos,
                radius: cfg.collisionCheckRadius,
                excludePlayerId: null, // we want self included (older self-segments crash you)
                skipRecent: cfg.selfCollisionSkipPoints,
                results: _candidates);

            _isChecking = true;
            try
            {
                // Convert player movement segment to world (XZ).
                CoordinateConverter.EnsureReference(playerPos);
                Vector3 wA = CoordinateConverter.GeoToWorld(prevPos);
                Vector3 wB = CoordinateConverter.GeoToWorld(playerPos);
                Vector2 pA = new Vector2(wA.x, wA.z);
                Vector2 pB = new Vector2(wB.x, wB.z);

                float thr = cfg.collisionThreshold;
                float thr2 = thr * 2f;

                foreach (var seg in _candidates)
                {
                    Vector3 sAw = CoordinateConverter.GeoToWorld(seg.Start);
                    Vector3 sBw = CoordinateConverter.GeoToWorld(seg.End);
                    Vector2 sA = new Vector2(sAw.x, sAw.z);
                    Vector2 sB = new Vector2(sBw.x, sBw.z);

                    // 2D segment intersection on XZ plane (spec §7.3).
                    bool intersect = SegmentsIntersect2D(pA, pB, sA, sB);
                    // Distance gate (catches near-misses / coincident points).
                    double d1 = PointToSegmentDistance(pA, sA, sB);
                    double d2 = PointToSegmentDistance(pB, sA, sB);
                    bool near = (d1 < thr) || (d2 < thr) || intersect;

                    if (!near) continue;

                    // Height gate: trails live on a band; ignore segments too far above/below.
                    float minSegY = Mathf.Min(sAw.y, sBw.y);
                    float maxSegY = Mathf.Max(sAw.y, sBw.y);
                    float playerY = Mathf.Min(wA.y, wB.y);
                    float dy = Mathf.Max(0f, minSegY - playerY, playerY - maxSegY);
                    if (dy > thr2) continue;

                    // Local player crossing their own older trail is also a crash — that's the
                    // skip direction invariant (pitfall #1). Don't filter by ownerId == localPlayerId.
                    OnCollisionDetected?.Invoke(seg.OwnerId);
                    return; // one crash is enough
                }
            }
            finally
            {
                _isChecking = false;
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
