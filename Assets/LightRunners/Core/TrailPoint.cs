using System;

namespace LightRunners.Core
{
    /// <summary>
    /// One sampled point along a trail. <see cref="ownerSequenceIndex"/> is the point's
    /// **run-scoped sequence number** (0,1,2,… monotonic for the owner's whole run) — the key
    /// used to merge remote batches in arrival order (spec §4.2 / §7.2). It is NOT an index
    /// into the live points list once pruning has dropped old points (pitfall #19).
    ///
    /// <see cref="isSegmentStart"/> marks a discontinuity (spec §20): the pair
    /// (previous point → this point) does not represent continuous movement (app was paused,
    /// GPS dropped out). Renderers, collision, and the distance accumulator all treat that
    /// pair as a non-segment. Local-only — never sent on the wire (a remote's pauses simply
    /// stop their batches; their delivered points stay continuous by sequence).
    /// </summary>
    [Serializable]
    public struct TrailPoint : IEquatable<TrailPoint>
    {
        public GeoPoint position;
        public double timestamp;
        public int ownerSequenceIndex;
        public bool isSegmentStart;

        public TrailPoint(GeoPoint position, double timestamp, int ownerSequenceIndex, bool isSegmentStart = false)
        {
            this.position = position;
            this.timestamp = timestamp;
            this.ownerSequenceIndex = ownerSequenceIndex;
            this.isSegmentStart = isSegmentStart;
        }

        public bool Equals(TrailPoint other)
            => position.Equals(other.position)
               && timestamp.Equals(other.timestamp)
               && ownerSequenceIndex == other.ownerSequenceIndex
               && isSegmentStart == other.isSegmentStart;

        public override bool Equals(object obj) => obj is TrailPoint t && Equals(t);
        public override int GetHashCode() => (position, timestamp, ownerSequenceIndex, isSegmentStart).GetHashCode();
        public static bool operator ==(TrailPoint a, TrailPoint b) => a.Equals(b);
        public static bool operator !=(TrailPoint a, TrailPoint b) => !a.Equals(b);
    }
}
