using System;
using System.Collections.Generic;
using LightRunners.Core;
using UnityEngine;

namespace LightRunners.Trail
{
    /// <summary>
    /// One trail (the local player's or one remote's): an ordered list of points plus owner
    /// metadata. Plain class, not a MonoBehaviour — <see cref="TrailManager"/> owns the
    /// collection. Spec §7.1.
    ///
    /// Two invariants from the 2026-07-04 gap review:
    ///  • <see cref="TotalLength"/> is a running accumulator that <see cref="PruneTo"/> never
    ///    reduces — recomputing from the pruned list undercounts exactly the 5 km+ runs that
    ///    hit the distance-score cap (pitfall #18).
    ///  • Merging keys on <see cref="HighestAppliedSequence"/>, a sequence cursor, never on
    ///    <see cref="PointCount"/> — list length diverges from sequence numbers the moment
    ///    pruning drops a point (pitfall #19).
    /// </summary>
    public class TrailData
    {
        public string OwnerId { get; private set; }
        public BeaconFormType BeaconForm { get; private set; }
        public Color TrailColor { get; private set; }

        private readonly List<TrailPoint> _points = new List<TrailPoint>();
        public IReadOnlyList<TrailPoint> Points => _points;
        public int PointCount => _points.Count;

        private double _totalLength;
        private int _highestAppliedSequence = -1;

        /// <summary>Highest ownerSequenceIndex ever applied to this trail (−1 when empty). The merge cursor (spec §7.2).</summary>
        public int HighestAppliedSequence => _highestAppliedSequence;

        public TrailData(string ownerId, BeaconFormType form, Color color)
        {
            OwnerId = ownerId;
            BeaconForm = form;
            TrailColor = color;
        }

        /// <summary>
        /// Begin a run. Always wipes existing points — callers that need idempotency for the
        /// same run (e.g. the dual GameManager/NetworkPlayer start) must check
        /// <see cref="IsSameRun"/> first (see <see cref="TrailManager.StartRun"/>).
        /// </summary>
        public void BeginRun(string ownerId, BeaconFormType form, Color color)
        {
            OwnerId = ownerId;
            BeaconForm = form;
            TrailColor = color;
            _points.Clear();
            _totalLength = 0.0;
            _highestAppliedSequence = -1;
        }

        /// <summary>True iff the caller would describe the same run already in progress.</summary>
        public bool IsSameRun(string ownerId, BeaconFormType form, Color color)
            => OwnerId == ownerId && BeaconForm == form && TrailColor == color && _points.Count > 0;

        /// <summary>
        /// Append-only by sequence cursor: points at or below <see cref="HighestAppliedSequence"/>
        /// are dropped, which makes overlapping/out-of-order batches idempotent (spec §7.2 / §8.2)
        /// and stays correct after pruning. Advances the distance accumulator unless the point
        /// starts a new segment (discontinuity, spec §20).
        /// </summary>
        public void AddPoint(TrailPoint point)
        {
            if (point.ownerSequenceIndex <= _highestAppliedSequence) return;

            if (_points.Count > 0 && !point.isSegmentStart)
                _totalLength += _points[_points.Count - 1].position.HorizontalDistanceTo(point.position);

            _points.Add(point);
            _highestAppliedSequence = point.ownerSequenceIndex;
        }

        /// <summary>Append a batch of points (each individually cursor-gated).</summary>
        public void AddPoints(IEnumerable<TrailPoint> points)
        {
            if (points == null) return;
            foreach (var p in points) AddPoint(p);
        }

        public void Clear()
        {
            _points.Clear();
            _totalLength = 0.0;
            _highestAppliedSequence = -1;
        }

        /// <summary>
        /// Drop the oldest points until at most <paramref name="max"/> remain (spec §7.1).
        /// Does NOT touch <see cref="TotalLength"/> or the merge cursor (pitfalls #18/#19).
        /// </summary>
        public void PruneTo(int max)
        {
            if (max <= 0) { _points.Clear(); return; }
            int excess = _points.Count - max;
            if (excess > 0) _points.RemoveRange(0, excess);
        }

        /// <summary>
        /// Cumulative horizontal distance of the whole run in metres — a running accumulator,
        /// unaffected by pruning (spec §7.1, pitfall #18). Discontinuity pairs (spec §20)
        /// contribute nothing.
        /// </summary>
        public double TotalLength => _totalLength;

        /// <summary>Serialize points with sequence ≥ <paramref name="fromSequence"/> into a packed snapshot. Spec §7.1 / §8.2.</summary>
        public TrailSnapshot TakeSnapshot(int fromSequence, int maxPoints)
            => TrailSnapshot.Encode(OwnerId, _points, fromSequence, maxPoints);

        public TrailPoint LastPoint => _points.Count > 0 ? _points[_points.Count - 1] : default;
        public TrailPoint FirstPoint => _points.Count > 0 ? _points[0] : default;
    }
}
