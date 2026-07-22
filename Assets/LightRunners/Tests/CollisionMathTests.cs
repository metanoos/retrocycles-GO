using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using LightRunners.Trail;

namespace LightRunners.Tests
{
    /// <summary>
    /// Spec §26: 2D segment-intersection truth table (incl. collinear, shared-endpoint,
    /// near-miss at threshold ± ε) and point-to-segment distance.
    /// </summary>
    public class CollisionMathTests
    {
        private static bool Intersect(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy)
            => TrailCollisionDetector.SegmentsIntersect2D(
                new Vector2(ax, ay), new Vector2(bx, by), new Vector2(cx, cy), new Vector2(dx, dy));

        [Test]
        public void Crossing_Intersects() =>
            Assert.IsTrue(Intersect(0, 0, 10, 10, 0, 10, 10, 0));

        [Test]
        public void Parallel_DoesNot() =>
            Assert.IsFalse(Intersect(0, 0, 10, 0, 0, 1, 10, 1));

        [Test]
        public void Disjoint_DoesNot() =>
            Assert.IsFalse(Intersect(0, 0, 1, 1, 5, 5, 6, 6));

        [Test]
        public void SharedEndpoint_Intersects() =>
            Assert.IsTrue(Intersect(0, 0, 5, 5, 5, 5, 10, 0), "touching at an endpoint counts");

        [Test]
        public void Collinear_Overlapping_Intersects() =>
            Assert.IsTrue(Intersect(0, 0, 10, 0, 5, 0, 15, 0));

        [Test]
        public void Collinear_Disjoint_DoesNot() =>
            Assert.IsFalse(Intersect(0, 0, 4, 0, 5, 0, 10, 0));

        [Test]
        public void TJunction_Intersects() =>
            Assert.IsTrue(Intersect(0, 0, 10, 0, 5, -5, 5, 0), "endpoint landing on the segment counts");

        // ── PointToSegmentDistance ──────────────────────────────────────────
        [Test]
        public void Distance_PerpendicularFoot() =>
            Assert.AreEqual(3.0, TrailCollisionDetector.PointToSegmentDistance(
                new Vector2(5, 3), new Vector2(0, 0), new Vector2(10, 0)), 1e-4);

        [Test]
        public void Distance_BeyondEndpoint_UsesEndpoint() =>
            Assert.AreEqual(5.0, TrailCollisionDetector.PointToSegmentDistance(
                new Vector2(13, 4), new Vector2(0, 0), new Vector2(10, 0)), 1e-4);

        [Test]
        public void Distance_DegenerateSegment_IsPointDistance() =>
            Assert.AreEqual(5.0, TrailCollisionDetector.PointToSegmentDistance(
                new Vector2(3, 4), new Vector2(0, 0), new Vector2(0, 0)), 1e-4);

        [Test]
        public void NearMiss_AtThresholdBoundary()
        {
            // Movement passes 1.51 m from a wall with a 1.5 m threshold: distance math must
            // report > 1.5 so the detector's near-gate stays false.
            double d = TrailCollisionDetector.PointToSegmentDistance(
                new Vector2(5, 1.51f), new Vector2(0, 0), new Vector2(10, 0));
            Assert.Greater(d, 1.5);

            double dHit = TrailCollisionDetector.PointToSegmentDistance(
                new Vector2(5, 1.49f), new Vector2(0, 0), new Vector2(10, 0));
            Assert.Less(dHit, 1.5);
        }

        [Test]
        public void SegmentDistance_Crossing3D_IsZero()
        {
            double distance = TrailCollisionDetector.SegmentToSegmentDistance(
                new Vector3(-5f, 0f, 0f), new Vector3(5f, 0f, 0f),
                new Vector3(0f, -5f, 0f), new Vector3(0f, 5f, 0f));
            Assert.AreEqual(0.0, distance, 1e-5);
        }

        [Test]
        public void SegmentDistance_ParallelAtAltitude_UsesTrue3DDistance()
        {
            double distance = TrailCollisionDetector.SegmentToSegmentDistance(
                new Vector3(0f, 5f, 0f), new Vector3(10f, 5f, 0f),
                new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f));
            Assert.AreEqual(5.0, distance, 1e-5);
        }

        [Test]
        public void SegmentDistance_DegenerateSegments_UsePointDistance()
        {
            double distance = TrailCollisionDetector.SegmentToSegmentDistance(
                new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f),
                new Vector3(0f, 3f, 4f), new Vector3(0f, 3f, 4f));
            Assert.AreEqual(5.0, distance, 1e-5);
        }

        [Test]
        public void CapsuleContact_ExactCombinedRadius_CountsAsCollision()
        {
            var chain = new List<CapsuleChainTail.Capsule>
            {
                new CapsuleChainTail.Capsule(
                    new Vector3(0f, 0f, 0f),
                    new Vector3(10f, 0f, 0f),
                    radius: 2f,
                    ownerId: "tail")
            };

            Assert.IsTrue(CapsuleChainTail.OverlapsAny(
                new Vector3(5f, 4f, 0f),
                headRadius: 2f,
                chain,
                out var hit));
            Assert.AreEqual("tail", hit.OwnerId);
        }
    }
}
