using System.Collections.Generic;
using NUnit.Framework;
using LightRunners.Core;

namespace LightRunners.Tests
{
    /// <summary>
    /// Spec §26: encode/decode round-trip &lt; 5 cm at longitude −122 (guards pitfall #16 —
    /// absolute lat/lon in a float would be ~1 m off), sequence-keyed slicing, empty trails.
    /// </summary>
    public class TrailSnapshotTests
    {
        private const double MeterLat = 1.0 / 111194.93;

        private static List<TrailPoint> Walk(int count, int firstSeq = 0)
        {
            var pts = new List<TrailPoint>(count);
            for (int i = 0; i < count; i++)
            {
                // 1 m strides north from a real-world SF coordinate (worst-case float lon).
                var geo = new GeoPoint(37.7749 + i * MeterLat, -122.4194, 5.0 + i * 0.1);
                pts.Add(new TrailPoint(geo, i * 0.5, firstSeq + i));
            }
            return pts;
        }

        [Test]
        public void RoundTrip_At_SF_Longitude_IsSubFiveCentimeters()
        {
            var pts = Walk(16);
            var snap = TrailSnapshot.Encode("p1", pts, 0, 16);
            var back = snap.Decode();

            Assert.AreEqual(16, back.Count);
            for (int i = 0; i < 16; i++)
            {
                double err = pts[i].position.HorizontalDistanceTo(back[i].position);
                Assert.Less(err, 0.05, $"point {i} error {err * 100:F2} cm (pitfall #16)");
                Assert.AreEqual(pts[i].position.altitude, back[i].position.altitude, 0.01);
                Assert.AreEqual(pts[i].ownerSequenceIndex, back[i].ownerSequenceIndex);
            }
        }

        [Test]
        public void Encode_SlicesBySequence_NotListPosition()
        {
            // Simulate a pruned list: sequences start at 100.
            var pts = Walk(10, firstSeq: 100);
            var snap = TrailSnapshot.Encode("p1", pts, 105, 16);

            Assert.AreEqual(105, snap.startIndex);
            Assert.AreEqual(5, snap.Count, "points 105..109");
            var back = snap.Decode();
            Assert.AreEqual(105, back[0].ownerSequenceIndex);
            Assert.AreEqual(109, back[4].ownerSequenceIndex);
        }

        [Test]
        public void Encode_RespectsMaxPoints()
        {
            var snap = TrailSnapshot.Encode("p1", Walk(100), 0, 16);
            Assert.AreEqual(16, snap.Count);
        }

        [Test]
        public void Encode_EmptyAndExhaustedInputs()
        {
            Assert.AreEqual(0, TrailSnapshot.Encode("p1", null, 0, 16).Count);
            Assert.AreEqual(0, TrailSnapshot.Encode("p1", new List<TrailPoint>(), 0, 16).Count);
            Assert.AreEqual(0, TrailSnapshot.Encode("p1", Walk(5), 5, 16).Count, "fromSequence past the end");
        }

        [Test]
        public void TimeOffsets_PreserveDeltas()
        {
            var pts = Walk(4);
            var back = TrailSnapshot.Encode("p1", pts, 0, 4).Decode();
            // Absolute timestamps are local-clock (spec §4.2); only deltas must survive.
            Assert.AreEqual(0.5, back[1].timestamp - back[0].timestamp, 1e-3);
            Assert.AreEqual(1.0, back[2].timestamp - back[0].timestamp, 1e-3);
        }
    }
}
