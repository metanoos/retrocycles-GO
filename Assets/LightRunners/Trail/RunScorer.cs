using System;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// One axis of the run score (spec §7.4). Returned by <see cref="RunScorer.Calculate"/>.
    /// </summary>
    [Serializable]
    public struct ScoreBreakdown
    {
        public int distance;   // /40
        public int speed;      // /20
        public int beauty;     // /30
        public int proximity;  // /10
        public int total;      // rounded sum

        public int Max => 40 + 20 + 30 + 10;
    }

    /// <summary>
    /// Static scorer. Spec §7.4.
    /// </summary>
    public static class RunScorer
    {
        // Distance scale: 5000 m → 40 points.
        private const double DistanceMaxMeters = 5000.0;
        private const int DistanceMax = 40;

        // Speed sweet spot 2–5 m/s. Below 0.5 → 0, ramp 0.5–2, flat 2–5, decay 5–15, 0 above 15.
        private const int SpeedMax = 20;

        // Beauty: 0.7·curve + 0.3·altitude-variation. Curves max at 30° avg heading change,
        // penalized above 60°.
        private const int BeautyMax = 30;

        // Proximity: min(otherPlayers, 5) · 2, max 10.
        private const int ProximityMax = 10;

        /// <summary>
        /// Calculate the run score. <paramref name="otherPlayersNearby"/> is the count of
        /// distinct live runners within the collision radius.
        /// </summary>
        public static ScoreBreakdown Calculate(TrailData trail, double durationSeconds, int otherPlayersNearby)
        {
            var s = new ScoreBreakdown();

            if (trail == null || trail.PointCount < 2 || durationSeconds <= 0.0)
            {
                s.total = 0;
                return s;
            }

            // ── Distance ────────────────────────────────────────────────
            double distMeters = trail.TotalLength;
            s.distance = (int)Math.Round(Math.Clamp(distMeters / DistanceMaxMeters, 0.0, 1.0) * DistanceMax);

            // ── Speed ───────────────────────────────────────────────────
            double avgSpeed = distMeters / durationSeconds; // m/s
            s.speed = (int)Math.Round(SpeedScore(avgSpeed) * SpeedMax);

            // ── Beauty ──────────────────────────────────────────────────
            double curve, altVar;
            BeautyMetrics(trail, out curve, out altVar);
            double curveClamped = Math.Clamp(curve / 30.0, 0.0, 1.0);
            // Penalize excessive turning (>60° avg heading change) so tight spirals don't farm.
            if (curve > 60.0) curveClamped *= Math.Clamp(1.0 - (curve - 60.0) / 60.0, 0.0, 1.0);
            double altNorm = Math.Clamp(altVar / 50.0, 0.0, 1.0); // 50 m of altitude variance ≈ full
            s.beauty = (int)Math.Round((0.7 * curveClamped + 0.3 * altNorm) * BeautyMax);

            // ── Proximity ───────────────────────────────────────────────
            s.proximity = (int)Math.Round(Math.Min(otherPlayersNearby, 5) / 5.0 * ProximityMax);

            s.total = s.distance + s.speed + s.beauty + s.proximity;
            return s;
        }

        /// <summary>Speed sweet-spot mapping in [0,1]. Spec §7.4.</summary>
        private static double SpeedScore(double v)
        {
            if (v <= 0.5) return 0.0;
            if (v < 2.0) return (v - 0.5) / (2.0 - 0.5);    // ramp 0.5→2
            if (v <= 5.0) return 1.0;                        // flat 2→5
            if (v < 15.0) return 1.0 - (v - 5.0) / (15.0 - 5.0); // decay 5→15
            return 0.0;
        }

        /// <summary>
        /// Compute beauty sub-metrics. <paramref name="curve"/> = average absolute heading
        /// change per segment (degrees). <paramref name="altVar"/> = max altitude range (m).
        /// </summary>
        private static void BeautyMetrics(TrailData trail, out double curve, out double altVar)
        {
            curve = 0.0;
            altVar = 0.0;
            var pts = trail.Points;
            if (pts.Count < 3) return;

            double minAlt = double.MaxValue, maxAlt = double.MinValue;
            double prevHeading = double.NaN;
            double sumDelta = 0.0;
            int deltaCount = 0;

            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i].position;
                minAlt = Math.Min(minAlt, p.altitude);
                maxAlt = Math.Max(maxAlt, p.altitude);

                if (i > 0)
                {
                    double h = CoordinateConverter.Bearing(pts[i - 1].position, p);
                    if (!double.IsNaN(prevHeading))
                    {
                        double d = Math.Abs(NormalizeHeadingDelta(h - prevHeading));
                        sumDelta += d;
                        deltaCount++;
                    }
                    prevHeading = h;
                }
            }

            altVar = (maxAlt - minAlt);
            curve = deltaCount > 0 ? sumDelta / deltaCount : 0.0;
        }

        private static double NormalizeHeadingDelta(double delta)
        {
            // Wrap to [-180, 180].
            while (delta > 180.0) delta -= 360.0;
            while (delta < -180.0) delta += 360.0;
            return delta;
        }
    }
}
