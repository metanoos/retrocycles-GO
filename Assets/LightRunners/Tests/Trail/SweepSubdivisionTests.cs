using System.Collections.Generic;
using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Trail.Tests
{
    /// <summary>
    /// Active decision N (no speed limit + sweep subdivision). Verifies
    /// <see cref="TrailCollisionDetector.SubdivideSweep"/>: short sweeps stay single, long sweeps
    /// split into ≤ maxStep sub-segments, total length is preserved, and the maxStep is respected.
    /// Mirrors the existing test convention (NUnit <c>[Test]</c>/<c>[TestCase]</c>, tolerance
    /// asserts).
    ///
    /// Why this matters (decision N): there is no movement speed cap. A teleport or fast vehicle
    /// move between frames is a long "sweep" that would otherwise jump PAST a trail without being
    /// tested. Subdivision breaks the sweep into ≤ maxStep pieces so every metre is actually
    /// tested against candidate trails.
    /// </summary>
    public class SweepSubdivisionTests
    {
        private const double MeterLat = 1.0 / 111194.93; // ≈ 1 m of latitude in degrees
        // ~1 m of longitude at 37° N (cos(37°) ≈ 0.7986).
        private const double MeterLon = 1.0 / (111194.93 * 0.7986);

        private static GeoPoint North(double meters)
            => new GeoPoint(meters * MeterLat, 0.0, 0.0);

        private static GeoPoint North(GeoPoint from, double meters)
            => new GeoPoint(from.latitude + meters * MeterLat, from.longitude, from.altitude);

        private static List<(GeoPoint, GeoPoint)> Subdivide(GeoPoint prev, GeoPoint cur, float maxStep)
        {
            var list = new List<(GeoPoint, GeoPoint)>();
            foreach (var sub in TrailCollisionDetector.SubdivideSweep(prev, cur, maxStep))
                list.Add(sub);
            return list;
        }

        // ── Short sweep = 1 segment ─────────────────────────────────────────

        [Test]
        public void ShortSweep_ReturnsSingleSegment()
        {
            var prev = North(0);
            var cur = North(1.5); // 1.5 m
            var subs = Subdivide(prev, cur, maxStep: 2f);

            Assert.AreEqual(1, subs.Count, "sweep under the step cap → unchanged");
            Assert.AreEqual(prev, subs[0].Item1);
            Assert.AreEqual(cur, subs[0].Item2);
        }

        [Test]
        public void CoincidentPoints_ReturnsSingleSegment()
        {
            var p = North(0);
            var subs = Subdivide(p, p, maxStep: 2f);

            Assert.AreEqual(1, subs.Count);
            Assert.AreEqual(p, subs[0].Item1);
            Assert.AreEqual(p, subs[0].Item2);
        }

        // ── Long sweep subdivides, total length preserved ───────────────────

        [Test]
        public void LongSweep_SubdividesIntoStepBoundedSegments()
        {
            var prev = North(0);
            var cur = North(10); // 10 m
            var subs = Subdivide(prev, cur, maxStep: 2f);

            // ceil(10 / 2) = 5 sub-segments
            Assert.AreEqual(5, subs.Count);

            // Each sub-segment ≤ maxStep (within Haversine round-trip tolerance).
            foreach (var (a, b) in subs)
            {
                double len = a.HorizontalDistanceTo(b);
                Assert.LessOrEqual(len, 2.0 + 1e-3, "every sub-segment must respect the maxStep cap");
            }
        }

        [Test]
        public void LongSweep_NonEvenSplit_LastSegmentIsRemainder()
        {
            var prev = North(0);
            var cur = North(10); // 10 m, step 3 → ceil(10/3) = 4 sub-segments
            var subs = Subdivide(prev, cur, maxStep: 3f);

            Assert.AreEqual(4, subs.Count);
            // The first three should be ≈ 2.5 m (10/4), the last also 2.5 — uniform split.
            foreach (var (a, b) in subs)
            {
                double len = a.HorizontalDistanceTo(b);
                Assert.AreEqual(2.5, len, 1e-2, "even split: each sub-segment ≈ total/steps");
            }
        }

        /// <summary>
        /// Decision N load-bearing invariant: the sum of sub-segment lengths ≈ the original sweep
        /// length (within one sub-segment's worth of equirectangular round-trip error). If this
        /// ever breaks, a long teleport would silently lose metres of collision testing.
        /// </summary>
        [Test]
        public void TotalLength_PreservedWithinOneStepTolerance()
        {
            var prev = North(0);
            var cur = North(100); // 100 m
            double original = prev.HorizontalDistanceTo(cur);

            var subs = Subdivide(prev, cur, maxStep: 2f);

            double sum = 0;
            foreach (var (a, b) in subs) sum += a.HorizontalDistanceTo(b);

            double err = System.Math.Abs(sum - original);
            Assert.Less(err, 2.0,
                "subdivided total length must ≈ original within one step's tolerance; "
                + $"got sum={sum:F3} vs original={original:F3}");
        }

        // ── Chain continuity: sub[i].End == sub[i+1].Start ──────────────────

        [Test]
        public void SubdividedChain_IsContinuous()
        {
            var prev = North(0);
            var cur = North(7);
            var subs = Subdivide(prev, cur, maxStep: 2f);

            Assert.AreEqual(prev, subs[0].Item1, "chain starts at prev");
            Assert.AreEqual(cur, subs[subs.Count - 1].Item2, "chain ends at cur");
            for (int i = 0; i < subs.Count - 1; i++)
                Assert.AreEqual(subs[i].Item2, subs[i + 1].Item1,
                    $"chain discontinuous at sub[{i}].End → sub[{i + 1}].Start");
        }

        // ── 2D (longitude) sweep also works ────────────────────────────────

        [Test]
        public void Sweep_AlongLongitude_SubdividesCorrectly()
        {
            var prev = new GeoPoint(37.0, 0.0);
            var cur = new GeoPoint(37.0, 10 * MeterLon); // ~10 m east
            var subs = Subdivide(prev, cur, maxStep: 2f);

            double original = prev.HorizontalDistanceTo(cur);
            Assert.AreEqual((int)System.Math.Ceiling(original / 2.0), subs.Count,
                "approximate longitude conversion may land just above 10 m; count follows measured distance");

            double sum = 0;
            foreach (var (a, b) in subs) sum += a.HorizontalDistanceTo(b);
            Assert.Less(System.Math.Abs(sum - original), 2.0, "longitude sweep length preserved");
        }

        // ── Edge cases ──────────────────────────────────────────────────────

        [Test]
        public void MaxStep_NonPositive_ReturnsSingleSegment()
        {
            var prev = North(0);
            var cur = North(50);
            var subs0 = Subdivide(prev, cur, maxStep: 0f);
            var subsNeg = Subdivide(prev, cur, maxStep: -1f);

            Assert.AreEqual(1, subs0.Count, "maxStep=0 → fall back to single segment");
            Assert.AreEqual(1, subsNeg.Count, "negative maxStep → single segment");
        }

        [TestCase(2f, Description = "exactly at the step")]
        [TestCase(1.99f, Description = "one mm over the step forces a split")]
        public void StepBoundary_Honored(float step)
        {
            var prev = North(0);
            var cur = North(2); // 2 m
            var subs = Subdivide(prev, cur, maxStep: step);

            if (step >= 2f)
            {
                Assert.AreEqual(1, subs.Count, "sweep ≤ step → single segment");
            }
            else
            {
                Assert.AreEqual(2, subs.Count, "sweep > step → split");
                foreach (var (a, b) in subs)
                {
                    double len = a.HorizontalDistanceTo(b);
                    Assert.LessOrEqual(len, step + 1e-2);
                }
            }
        }
    }
}
