using System;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// Snake-tail energy-budget length rule (active decision B). The active trail has a finite
    /// ENERGY-DEPENDENT maximum length: <c>maxSegments = floor(energyBudget / segmentCost)</c>.
    /// The oldest segment dissolves as the runner advances past the cap. This replaces the
    /// fixed <c>maxTrailPoints</c> cap that previously bounded every trail equally regardless of
    /// how much "energy" (gate pickups / momentum / level) the runner had banked.
    ///
    /// This is a PURE C# helper — <c>TrailManager</c> (Track D) calls
    /// <see cref="ShouldPruneOldest"/> after each <c>AddPoint</c> and then calls
    /// <c>TrailData.PruneTo(MaxSegments)</c>. Track A deliberately does NOT modify
    /// <c>TrailManager.AppendLocalPoint</c> — that wiring belongs to Track D so the gameplay
    /// layer owns when/where energy is sourced.
    ///
    /// CRITICAL — the <c>TrailData.TotalLength</c> accumulator invariant (SPEC §7.1, pitfall #18):
    /// the accumulator is a RUNNING sum that <c>TrailData.PruneTo</c> must NEVER reduce. Only the
    /// live point list is shortened; the cumulative distance traveled stays intact so a 5 km+ run
    /// (which is exactly when pruning kicks in) still scores correctly. This helper does not touch
    /// the accumulator — it only computes the cap. <see cref="ShouldPruneOldest"/> is therefore
    /// safe to call before any <c>PruneTo</c> without corrupting distance bookkeeping.
    /// </summary>
    public sealed class SnakeTailModel
    {
        /// <summary>
        /// Default segment cost (m) used when <see cref="Configure"/> has not been called. Matches
        /// the v1 <c>trailPointMinDistance</c> floor so an unconfigured model behaves like the old
        /// fixed cap rather than crashing.
        /// </summary>
        public const float DefaultSegmentCostMeters = 1.0f;

        /// <summary>
        /// Default energy budget (m of tail) used when unconfigured. Picked so the default cap
        /// equals the legacy <c>maxTrailPoints</c> default (5000) at the default segment cost —
        /// an unconfigured model is a no-op migration, not a regression.
        /// </summary>
        public const float DefaultEnergyBudgetMeters = 5000.0f;

        /// <summary>Smallest sane cap so a tiny/zero budget doesn't drop the whole trail (1 segment min).</summary>
        public const int MinSegments = 1;

        private float _energyBudgetMeters = DefaultEnergyBudgetMeters;
        private float _segmentCostMeters = DefaultSegmentCostMeters;

        /// <summary>
        /// Configure the energy budget and per-segment cost. After this,
        /// <see cref="MaxSegments"/> = <c>clamp(floor(energyBudget / segmentCost), 1, int.MaxValue)</c>.
        /// A non-positive <paramref name="segmentCostMeters"/> falls back to
        /// <see cref="DefaultSegmentCostMeters"/> (avoids div-by-zero). A non-positive
        /// <paramref name="energyBudgetMeters"/> yields <see cref="MinSegments"/>.
        /// </summary>
        public void Configure(float energyBudgetMeters, float segmentCostMeters)
        {
            _energyBudgetMeters = energyBudgetMeters > 0f ? energyBudgetMeters : 0f;
            _segmentCostMeters = segmentCostMeters > 0f ? segmentCostMeters : DefaultSegmentCostMeters;
        }

        /// <summary>Currently configured energy budget (m). 0 if never configured.</summary>
        public float EnergyBudgetMeters => _energyBudgetMeters;

        /// <summary>Currently configured per-segment cost (m).</summary>
        public float SegmentCostMeters => _segmentCostMeters;

        /// <summary>
        /// The energy-derived maximum number of segments the active tail may hold. At least
        /// <see cref="MinSegments"/> so a near-zero budget doesn't erase the runner's trail.
        /// </summary>
        public int MaxSegments
        {
            get
            {
                if (_energyBudgetMeters <= 0f) return MinSegments;
                double raw = Math.Floor(_energyBudgetMeters / _segmentCostMeters);
                if (raw <= 0) return MinSegments;
                if (raw > int.MaxValue) return int.MaxValue;
                return (int)raw;
            }
        }

        /// <summary>
        /// True iff a trail with <paramref name="currentSegmentCount"/> live points has exceeded
        /// <see cref="MaxSegments"/> and should drop its oldest point(s). A "segment" here is one
        /// point slot (so a trail of N points has N-1 line segments but the cap is expressed in
        /// points, mirroring <c>TrailData.PruneTo(maxPoints)</c>). Callers then do:
        /// <code>
        /// if (model.ShouldPruneOldest(trail.PointCount))
        ///     trail.PruneTo(model.MaxSegments);
        /// </code>
        /// and trust <c>PruneTo</c> to preserve the <c>TotalLength</c> accumulator (pitfall #18).
        /// </summary>
        public bool ShouldPruneOldest(int currentSegmentCount)
        {
            if (currentSegmentCount <= 0) return false;
            return currentSegmentCount > MaxSegments;
        }

        /// <summary>
        /// How many points would be dropped by a <c>PruneTo(MaxSegments)</c> at the given count.
        /// Always &gt;= 0. Useful for debugging / instrumenting the dissolve rate.
        /// </summary>
        public int ExcessCount(int currentSegmentCount)
        {
            if (currentSegmentCount <= 0) return 0;
            int excess = currentSegmentCount - MaxSegments;
            return excess > 0 ? excess : 0;
        }
    }
}
