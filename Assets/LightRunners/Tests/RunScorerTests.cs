using NUnit.Framework;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Tests
{
    /// <summary>Spec §26: per-axis table tests against the §7.4 rules.</summary>
    public class RunScorerTests
    {
        private const double MeterLat = 1.0 / 111194.93;

        /// <summary>Straight-north trail with a given total length.</summary>
        private static TrailData Straight(double meters, double spacing = 10.0)
        {
            var t = new TrailData("p1", BeaconFormType.Hoverboard, Color.cyan);
            int points = (int)(meters / spacing) + 1;
            for (int i = 0; i < points; i++)
                t.AddPoint(new TrailPoint(new GeoPoint(i * spacing * MeterLat, 0), i, i));
            return t;
        }

        /// <summary>Trail turning a constant number of degrees per 10 m segment.</summary>
        private static TrailData Curved(double degPerSegment, int segments = 30)
        {
            var t = new TrailData("p1", BeaconFormType.Hoverboard, Color.cyan);
            double heading = 0, lat = 0, lon = 0;
            const double step = 10.0;
            t.AddPoint(new TrailPoint(new GeoPoint(0, 0), 0, 0));
            for (int i = 1; i <= segments; i++)
            {
                heading += degPerSegment;
                double rad = heading * System.Math.PI / 180.0;
                lat += System.Math.Cos(rad) * step * MeterLat;
                lon += System.Math.Sin(rad) * step * MeterLat; // equator: same scale
                t.AddPoint(new TrailPoint(new GeoPoint(lat, lon), i, i));
            }
            return t;
        }

        // ── Guards ──────────────────────────────────────────────────────────
        [Test]
        public void NullTrail_ScoresZero() =>
            Assert.AreEqual(0, RunScorer.Calculate(null, 60, 0).total);

        [Test]
        public void TooFewPoints_ScoresZero()
        {
            var t = new TrailData("p1", BeaconFormType.Hoverboard, Color.cyan);
            t.AddPoint(new TrailPoint(new GeoPoint(0, 0), 0, 0));
            Assert.AreEqual(0, RunScorer.Calculate(t, 60, 0).total);
        }

        [Test]
        public void ZeroDuration_ScoresZero() =>
            Assert.AreEqual(0, RunScorer.Calculate(Straight(100), 0, 0).total);

        // ── Distance (max 40 at 5 km) ───────────────────────────────────────
        [Test]
        public void Distance_ZeroMidCap()
        {
            // ~0 m
            Assert.LessOrEqual(RunScorer.Calculate(Straight(10), 10, 0).distance, 1);
            // 2.5 km → ~20
            Assert.AreEqual(20, RunScorer.Calculate(Straight(2500, 10), 1000, 0).distance, 1);
            // 6 km → capped at 40 (also exercises the accumulator path)
            Assert.AreEqual(40, RunScorer.Calculate(Straight(6000, 10), 2000, 0).distance);
        }

        // ── Speed (sweet spot 2–5 m/s) ──────────────────────────────────────
        [TestCase(0.4, 0)]     // below the ramp
        [TestCase(1.25, 10)]   // mid-ramp
        [TestCase(3.0, 20)]    // sweet spot
        [TestCase(10.0, 10)]   // mid-decay
        [TestCase(16.0, 0)]    // past the decay
        public void Speed_Table(double avgSpeed, int expected)
        {
            var trail = Straight(1000, 10);
            double distance = trail.TotalLength;
            double duration = distance / avgSpeed;
            Assert.AreEqual(expected, RunScorer.Calculate(trail, duration, 0).speed, 1);
        }

        // ── Beauty ──────────────────────────────────────────────────────────
        [Test]
        public void Beauty_StraightLine_IsNearZero() =>
            Assert.LessOrEqual(RunScorer.Calculate(Straight(1000), 300, 0).beauty, 2);

        [Test]
        public void Beauty_GentleCurves_BeatTightSpirals()
        {
            int gentle = RunScorer.Calculate(Curved(30), 300, 0).beauty;  // at the 30° sweet spot
            int spiral = RunScorer.Calculate(Curved(90), 300, 0).beauty;  // penalized past 60°
            Assert.Greater(gentle, spiral, ">60° average turning must score below the 30° sweet spot");
            Assert.Greater(gentle, 15, "30°/segment should approach the 21-point curve share");
        }

        // ── Proximity (min(n,5)·2) ──────────────────────────────────────────
        [TestCase(0, 0)]
        [TestCase(3, 6)]
        [TestCase(5, 10)]
        [TestCase(8, 10)]
        public void Proximity_Table(int nearby, int expected) =>
            Assert.AreEqual(expected, RunScorer.Calculate(Straight(1000), 300, nearby).proximity);

        // ── Total ───────────────────────────────────────────────────────────
        [Test]
        public void Total_IsSumOfAxes()
        {
            var s = RunScorer.Calculate(Straight(2500, 10), 1000, 3);
            Assert.AreEqual(s.distance + s.speed + s.beauty + s.proximity, s.total);
            Assert.AreEqual(100, s.Max);
        }
    }
}
