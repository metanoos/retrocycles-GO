using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightRunners.Core
{
    /// <summary>
    /// Wire format for both network trail sync (spec §8.2) and backend batch saves.
    ///
    /// **Precision (pitfall #16):** absolute lat/lon must never live in a 32-bit float — one
    /// float ulp at longitude −122° is ~0.7–0.9 m, the same order as the collision threshold.
    /// So each snapshot carries a per-batch **double** origin (<see cref="originLat"/> /
    /// <see cref="originLon"/>) and packs points as float *offsets from that origin*, scaled
    /// by 1e5 (1 unit ≈ 1.1 m latitude; float resolution at batch spans is sub-millimeter):
    /// <c>[dLat·1e5, dLon·1e5, alt, time_offset]</c> per point. <c>time_offset</c> is seconds
    /// relative to the batch's first point (timestamps are local-clock, spec §4.2).
    /// </summary>
    [Serializable]
    public class TrailSnapshot
    {
        /// <summary>Degrees → packed offset units. 1e-5 deg ≈ 1.1 m latitude.</summary>
        public const double OffsetScale = 1e5;

        /// <summary>Owner (player) id of this trail.</summary>
        public string ownerId;

        /// <summary>Sequence number of the first packed point within the owner's run. For ordering/idempotency.</summary>
        public int startIndex;

        /// <summary>Per-batch double origin: latitude of the first packed point.</summary>
        public double originLat;

        /// <summary>Per-batch double origin: longitude of the first packed point.</summary>
        public double originLon;

        /// <summary>Packed point data: [dLat0·1e5, dLon0·1e5, alt0, t0, dLat1·1e5, ...]. Always length = count * 4.</summary>
        public float[] points;

        /// <summary>Number of points encoded in <see cref="points"/> (== points.Length / 4).</summary>
        public int Count => points == null ? 0 : points.Length / 4;

        public TrailSnapshot() { }

        public TrailSnapshot(string ownerId, int startIndex, double originLat, double originLon, float[] points)
        {
            this.ownerId = ownerId;
            this.startIndex = startIndex;
            this.originLat = originLat;
            this.originLon = originLon;
            this.points = points;
        }

        /// <summary>
        /// Encode a slice of <paramref name="trail"/> starting at the point whose
        /// <c>ownerSequenceIndex</c> == <paramref name="fromSequence"/>. Sequence-keyed, not
        /// list-position-keyed, so it stays correct after pruning (pitfall #19). Points with
        /// lower sequence numbers (already pruned or already sent) are skipped.
        /// </summary>
        public static TrailSnapshot Encode(
            string ownerId,
            IReadOnlyList<TrailPoint> trail,
            int fromSequence,
            int maxPoints)
        {
            if (trail == null || trail.Count == 0 || maxPoints <= 0)
                return new TrailSnapshot(ownerId, fromSequence, 0, 0, Array.Empty<float>());

            // Locate the first list position with sequence >= fromSequence. Sequences are
            // ascending, so binary search; for the common contiguous case this resolves in
            // O(log n) without assuming seq == list index.
            int lo = 0, hi = trail.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (trail[mid].ownerSequenceIndex < fromSequence) lo = mid + 1;
                else hi = mid;
            }
            int first = lo;
            int count = Math.Min(maxPoints, trail.Count - first);
            if (count <= 0)
                return new TrailSnapshot(ownerId, fromSequence, 0, 0, Array.Empty<float>());

            double oLat = trail[first].position.latitude;
            double oLon = trail[first].position.longitude;
            double firstTimestamp = trail[first].timestamp;

            float[] data = new float[count * 4];
            for (int i = 0; i < count; i++)
            {
                TrailPoint p = trail[first + i];
                int o = i * 4;
                data[o + 0] = (float)((p.position.latitude - oLat) * OffsetScale);
                data[o + 1] = (float)((p.position.longitude - oLon) * OffsetScale);
                data[o + 2] = (float)p.position.altitude;
                data[o + 3] = (float)(p.timestamp - firstTimestamp); // time_offset
            }
            return new TrailSnapshot(ownerId, trail[first].ownerSequenceIndex, oLat, oLon, data);
        }

        /// <summary>Decode packed point data back into a list of <see cref="TrailPoint"/>.</summary>
        public List<TrailPoint> Decode()
        {
            var result = new List<TrailPoint>(Count);
            if (points == null || points.Length < 4) return result;

            for (int i = 0; i < Count; i++)
            {
                int o = i * 4;
                var geo = new GeoPoint(
                    originLat + points[o + 0] / OffsetScale,
                    originLon + points[o + 1] / OffsetScale,
                    points[o + 2]);
                double ts = points[o + 3]; // relative to batch start; only deltas are meaningful
                result.Add(new TrailPoint(geo, ts, startIndex + i));
            }
            return result;
        }
    }
}
