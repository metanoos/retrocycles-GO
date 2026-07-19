using NUnit.Framework;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Tests
{
    /// <summary>
    /// Spec §26: accumulator survives pruning (pitfall #18), cursor-gated appends survive
    /// pruning (pitfall #19), IsSameRun truth table, discontinuity handling (spec §20).
    /// </summary>
    public class TrailDataTests
    {
        private const double MeterLat = 1.0 / 111194.93; // ≈ 1 m of latitude in degrees

        private static TrailPoint P(int seq, double northMeters, bool segmentStart = false)
            => new TrailPoint(new GeoPoint(northMeters * MeterLat, 0), seq * 0.5, seq, segmentStart);

        private static TrailData Straight(int points, double spacingMeters = 1.0)
        {
            var t = new TrailData("p1", BeaconFormType.Hoverboard, Color.cyan);
            for (int i = 0; i < points; i++)
                t.AddPoint(P(i, i * spacingMeters));
            return t;
        }

        [Test]
        public void TotalLength_AccumulatesHaversine()
        {
            var t = Straight(11, 10.0); // 10 segments × 10 m
            Assert.AreEqual(100.0, t.TotalLength, 0.1);
        }

        [Test]
        public void TotalLength_UnchangedByPrune()
        {
            var t = Straight(101, 1.0); // 100 m
            double before = t.TotalLength;
            t.PruneTo(10);
            Assert.AreEqual(10, t.PointCount);
            Assert.AreEqual(before, t.TotalLength, 1e-9, "PruneTo must never reduce the accumulator (pitfall #18)");
        }

        [Test]
        public void AddPoint_GatesOnSequenceCursor()
        {
            var t = Straight(5);
            int count = t.PointCount;
            t.AddPoint(P(2, 2.0)); // duplicate sequence — must drop
            t.AddPoint(P(4, 4.0)); // stale — must drop
            Assert.AreEqual(count, t.PointCount);
            Assert.AreEqual(4, t.HighestAppliedSequence);
        }

        [Test]
        public void Merge_SurvivesPruning()
        {
            var t = Straight(20);
            t.PruneTo(5); // list shrinks to 5, cursor stays 19
            Assert.AreEqual(19, t.HighestAppliedSequence);

            // A replayed old batch (seqs 10..14) must NOT append even though the list is short.
            for (int i = 10; i < 15; i++) t.AddPoint(P(i, i));
            Assert.AreEqual(5, t.PointCount, "stale batch leaked past the cursor (pitfall #19)");

            // Genuinely new points still append.
            t.AddPoint(P(20, 20));
            Assert.AreEqual(6, t.PointCount);
            Assert.AreEqual(20, t.HighestAppliedSequence);
        }

        [Test]
        public void OverlappingBatches_AreIdempotent()
        {
            var t = new TrailData("p1", BeaconFormType.Sphere, Color.magenta);
            var batch1 = new[] { P(0, 0), P(1, 1), P(2, 2) };
            var batch2 = new[] { P(1, 1), P(2, 2), P(3, 3) }; // overlaps batch1
            t.AddPoints(batch1);
            t.AddPoints(batch2);
            t.AddPoints(batch2); // replay
            Assert.AreEqual(4, t.PointCount);
            Assert.AreEqual(3.0, t.TotalLength, 0.01);
        }

        [Test]
        public void Discontinuity_ContributesNoDistance_AndNoSegment()
        {
            var t = new TrailData("p1", BeaconFormType.Drone, Color.green);
            t.AddPoint(P(0, 0));
            t.AddPoint(P(1, 10));
            t.AddPoint(P(2, 500, segmentStart: true)); // 490 m teleport gap (spec §20)
            t.AddPoint(P(3, 510));
            Assert.AreEqual(20.0, t.TotalLength, 0.1, "the gap pair must not count as distance");
        }

        [Test]
        public void IsSameRun_TruthTable()
        {
            var t = Straight(3);
            Assert.IsTrue(t.IsSameRun("p1", BeaconFormType.Hoverboard, Color.cyan));
            Assert.IsFalse(t.IsSameRun("p2", BeaconFormType.Hoverboard, Color.cyan), "different owner");
            Assert.IsFalse(t.IsSameRun("p1", BeaconFormType.Sphere, Color.cyan), "different form");
            Assert.IsFalse(t.IsSameRun("p1", BeaconFormType.Hoverboard, Color.red), "different color");

            var empty = new TrailData("p1", BeaconFormType.Hoverboard, Color.cyan);
            Assert.IsFalse(empty.IsSameRun("p1", BeaconFormType.Hoverboard, Color.cyan), "no points yet ⇒ not a run in progress");
        }

        [Test]
        public void BeginRun_ResetsEverything()
        {
            var t = Straight(10);
            t.BeginRun("p1", BeaconFormType.Hoverboard, Color.cyan);
            Assert.AreEqual(0, t.PointCount);
            Assert.AreEqual(0.0, t.TotalLength, 1e-9);
            Assert.AreEqual(-1, t.HighestAppliedSequence);
        }
    }
}
