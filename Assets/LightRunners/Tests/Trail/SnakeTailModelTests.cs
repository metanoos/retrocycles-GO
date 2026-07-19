using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Trail.Tests
{
    /// <summary>
    /// Active decision B (Snake movement: energy-budget finite tail). Verifies the
    /// <see cref="SnakeTailModel"/> cap math and the prune-trigger predicate. Matches the existing
    /// test convention (NUnit <c>[Test]</c>, <c>/// &lt;summary&gt;</c> citing spec, helper
    /// builders, tolerance asserts).
    ///
    /// CRITICAL — pitfall #18 invariant: <see cref="TrailData.TotalLength"/> is a running
    /// accumulator that <see cref="TrailData.PruneTo"/> must NEVER reduce (SPEC §7.1). The test
    /// <see cref="ShouldPruneOldest_PreservesTotalLengthAccumulator"/> documents and verifies that
    /// pruning by the snake model's cap leaves the accumulator intact — so a 5 km+ run still
    /// scores correctly even as the snake tail dissolves its oldest points.
    /// </summary>
    public class SnakeTailModelTests
    {
        // ── MaxSegments cap math ───────────────────────────────────────────

        [Test]
        public void MaxSegments_DefaultsToLegacyCap()
        {
            var model = new SnakeTailModel();
            // Defaults: 5000 m budget / 1.0 m cost = 5000 segments — the v1 maxTrailPoints default.
            Assert.AreEqual(5000, model.MaxSegments);
        }

        [Test]
        public void Configure_FloorOfBudgetOverCost()
        {
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: 100f, segmentCostMeters: 3f);
            // floor(100 / 3) = 33
            Assert.AreEqual(33, model.MaxSegments);
        }

        [Test]
        public void Configure_ExactDivisor_NoOffByOne()
        {
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: 100f, segmentCostMeters: 5f);
            Assert.AreEqual(20, model.MaxSegments);
        }

        [Test]
        public void Configure_ZeroBudget_FloorsToMinSegments()
        {
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: 0f, segmentCostMeters: 1f);
            Assert.AreEqual(SnakeTailModel.MinSegments, model.MaxSegments, "tiny/zero budget keeps ≥1 segment");
        }

        [Test]
        public void Configure_NegativeBudget_FloorsToMinSegments()
        {
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: -10f, segmentCostMeters: 1f);
            Assert.AreEqual(SnakeTailModel.MinSegments, model.MaxSegments);
        }

        [Test]
        public void Configure_NonPositiveSegmentCost_FallsBackToDefault()
        {
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: 50f, segmentCostMeters: 0f); // div-by-zero guard
            // 50 / default(1.0) = 50
            Assert.AreEqual(50, model.MaxSegments);

            model.Configure(energyBudgetMeters: 50f, segmentCostMeters: -2f);
            Assert.AreEqual(50, model.MaxSegments);
        }

        // ── ShouldPruneOldest at/below/over budget ─────────────────────────

        [Test]
        public void ShouldPruneOldest_AtBudget_False()
        {
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: 10f, segmentCostMeters: 1f);
            Assert.IsFalse(model.ShouldPruneOldest(10), "exactly at cap — no prune");
        }

        [Test]
        public void ShouldPruneOldest_BelowBudget_False()
        {
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: 10f, segmentCostMeters: 1f);
            Assert.IsFalse(model.ShouldPruneOldest(5));
            Assert.IsFalse(model.ShouldPruneOldest(0));
        }

        [Test]
        public void ShouldPruneOldest_OverBudget_True()
        {
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: 10f, segmentCostMeters: 1f);
            Assert.IsTrue(model.ShouldPruneOldest(11), "one over cap → prune");
            Assert.IsTrue(model.ShouldPruneOldest(500));
        }

        [Test]
        public void ExcessCount_ReportsDropCount()
        {
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: 10f, segmentCostMeters: 1f);
            Assert.AreEqual(0, model.ExcessCount(10));
            Assert.AreEqual(1, model.ExcessCount(11));
            Assert.AreEqual(40, model.ExcessCount(50));
        }

        // ── Pitfall #18: accumulator invariant survives snake-style pruning ─

        /// <summary>
        /// Decision B + pitfall #18: when the snake model prunes the oldest points to stay within
        /// the energy budget, <see cref="TrailData.TotalLength"/> must NOT drop. The accumulator is
        /// the runner's true lifetime distance; only the visible tail is bounded by energy.
        /// </summary>
        [Test]
        public void ShouldPruneOldest_PreservesTotalLengthAccumulator()
        {
            const double MeterLat = 1.0 / 111194.93; // ≈ 1 m of latitude in degrees (matches TrailDataTests convention)

            var trail = new TrailData("snake", BeaconFormType.Hoverboard, UnityEngine.Color.cyan);
            var model = new SnakeTailModel();
            model.Configure(energyBudgetMeters: 10f, segmentCostMeters: 1f); // cap = 10 points

            // Lay 20 points × 1 m → 19 m of distance, well over the 10-point cap.
            for (int i = 0; i < 20; i++)
                trail.AddPoint(new TrailPoint(
                    new GeoPoint(i * MeterLat, 0), i * 0.5, i, isSegmentStart: false));

            double lengthBefore = trail.TotalLength;
            Assert.Greater(lengthBefore, 15.0, "sanity: distance accumulated");

            // Snake-model-driven prune: the moment we exceed the cap, drop oldest to the cap.
            if (model.ShouldPruneOldest(trail.PointCount))
                trail.PruneTo(model.MaxSegments);

            Assert.AreEqual(model.MaxSegments, trail.PointCount, "pruned down to the energy cap");
            Assert.AreEqual(lengthBefore, trail.TotalLength, 1e-9,
                "PruneTo must NEVER reduce the TotalLength accumulator (pitfall #18) — the snake's "
                + "visible tail is bounded by energy, but the runner's lifetime distance is not.");
        }
    }
}
