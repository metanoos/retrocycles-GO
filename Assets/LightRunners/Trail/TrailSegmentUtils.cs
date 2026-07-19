using System;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// A start/end geo pair representing one line segment of a trail, with the id of the
    /// owning player. Returned by <see cref="TrailManager.GetTrailSegmentsNear"/> for the
    /// collision detector to test against.
    /// </summary>
    public readonly struct TrailSegment : IEquatable<TrailSegment>
    {
        public readonly GeoPoint Start;
        public readonly GeoPoint End;
        public readonly string OwnerId;

        public TrailSegment(GeoPoint start, GeoPoint end, string ownerId)
        {
            Start = start;
            End = end;
            OwnerId = ownerId;
        }

        public bool Equals(TrailSegment other)
            => Start.Equals(other.Start) && End.Equals(other.End) && OwnerId == other.OwnerId;
        public override bool Equals(object obj) => obj is TrailSegment s && Equals(s);
        public override int GetHashCode() => (Start, End, OwnerId).GetHashCode();
    }
}
