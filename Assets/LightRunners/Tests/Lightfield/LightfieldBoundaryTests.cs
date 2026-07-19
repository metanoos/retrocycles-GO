using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Lightfield;

namespace LightRunners.Lightfield.Tests
{
    /// <summary>
    /// Decision K (Lightfield boundary), decision S (ground-only milestone: disc + hard
    /// ceiling). Tests the pure-C# helpers in <see cref="LightfieldGeometry"/>: inside/outside/
    /// on-edge disc membership, ceiling violation, ground-dip tolerance. Origin is (0,0,0) so
    /// lat/lon offsets in degrees map to known metre distances (1° lat ≈ 111 194.93 m).
    /// </summary>
    public class LightfieldBoundaryTests
    {
        private const float Radius = 50f;
        private const float Ceiling = 6f;
        private const double MetersPerDegLat = 111_194.92664455873; // π * 6,371,000 / 180

        private static GeoPoint At(double metersNorth, double metersEast, double altAboveOrigin)
            => new GeoPoint(metersNorth / MetersPerDegLat, metersEast / MetersPerDegLat, altAboveOrigin);

        // ── Disc membership (decision K ground-only milestone) ───────────────
        [Test]
        public void Disc_OriginInside() =>
            Assert.IsTrue(LightfieldGeometry.IsInsideDisc(new GeoPoint(0, 0, 0), new GeoPoint(0, 0, 0), Radius));

        [Test]
        public void Disc_PointWellInside() =>
            Assert.IsTrue(LightfieldGeometry.IsInsideDisc(new GeoPoint(0, 0, 0), At(20, 0, 0), Radius));

        [Test]
        public void Disc_PointOutside() =>
            Assert.IsFalse(LightfieldGeometry.IsInsideDisc(new GeoPoint(0, 0, 0), At(80, 0, 0), Radius));

        [Test]
        public void Disc_OnEdge_Inclusive() =>
            // Exactly at the radius: Haversine returns ~50m; inclusive ⇒ inside.
            Assert.IsTrue(LightfieldGeometry.IsInsideDisc(new GeoPoint(0, 0, 0), At(50, 0, 0), Radius));

        [Test]
        public void Disc_JustInsideEdge() =>
            Assert.IsTrue(LightfieldGeometry.IsInsideDisc(new GeoPoint(0, 0, 0), At(49.5, 0, 0), Radius));

        [Test]
        public void Disc_JustOutsideEdge() =>
            Assert.IsFalse(LightfieldGeometry.IsInsideDisc(new GeoPoint(0, 0, 0), At(50.5, 0, 0), Radius));

        [Test]
        public void Disc_DiagonalOutside() =>
            // 40 north + 40 east ≈ 56.6m diagonally → outside the 50m disc.
            Assert.IsFalse(LightfieldGeometry.IsInsideDisc(new GeoPoint(0, 0, 0), At(40, 40, 0), Radius));

        [Test]
        public void Disc_DiagonalInside() =>
            // 30 north + 30 east ≈ 42.4m diagonally → inside.
            Assert.IsTrue(LightfieldGeometry.IsInsideDisc(new GeoPoint(0, 0, 0), At(30, 30, 0), Radius));

        [Test]
        public void Disc_NegativeRadius_AlwaysOutside() =>
            Assert.IsFalse(LightfieldGeometry.IsInsideDisc(new GeoPoint(0, 0, 0), new GeoPoint(0, 0, 0), -1f));

        // ── Ceiling membership (decision K + ground-dip tolerance) ───────────
        [Test]
        public void Ceiling_AtOriginAlt_Inside() =>
            Assert.IsTrue(LightfieldGeometry.IsBelowCeiling(new GeoPoint(0, 0, 0), new GeoPoint(0, 0, 0), Ceiling));

        [Test]
        public void Ceiling_AtCeiling_Inclusive() =>
            Assert.IsTrue(LightfieldGeometry.IsBelowCeiling(new GeoPoint(0, 0, 0), At(0, 0, 6), Ceiling));

        [Test]
        public void Ceiling_AboveCeiling_Outside() =>
            Assert.IsFalse(LightfieldGeometry.IsBelowCeiling(new GeoPoint(0, 0, 0), At(0, 0, 7), Ceiling));

        [Test]
        public void Ceiling_GroundDipWithinTolerance_Inside() =>
            // Tolerance is 1m; a 0.9m dip stays inside.
            Assert.IsTrue(LightfieldGeometry.IsBelowCeiling(new GeoPoint(0, 0, 0), At(0, 0, -0.9), Ceiling));

        [Test]
        public void Ceiling_GroundDipBeyondTolerance_Outside() =>
            Assert.IsFalse(LightfieldGeometry.IsBelowCeiling(new GeoPoint(0, 0, 0), At(0, 0, -1.5), Ceiling));

        [Test]
        public void Ceiling_AtNegativeToleranceBoundary_Inclusive() =>
            // Exactly -1m (the tolerance boundary) is inside (>= -tolerance).
            Assert.IsTrue(LightfieldGeometry.IsBelowCeiling(new GeoPoint(0, 0, 0), At(0, 0, -1.0), Ceiling));

        // ── Dome stub (decision S; replaces with true hemisphere at aerial milestone) ─
        [Test]
        public void Dome_InsideBoth_Inside() =>
            Assert.IsTrue(LightfieldGeometry.IsInsideDome(new GeoPoint(0, 0, 0), At(10, 10, 3), Radius, Ceiling));

        [Test]
        public void Dome_OutsideDisc_OutsideEvenIfAltitudeOk() =>
            Assert.IsFalse(LightfieldGeometry.IsInsideDome(new GeoPoint(0, 0, 0), At(80, 0, 3), Radius, Ceiling));

        [Test]
        public void Dome_AboveCeiling_OutsideEvenIfDiscOk() =>
            Assert.IsFalse(LightfieldGeometry.IsInsideDome(new GeoPoint(0, 0, 0), At(10, 0, 10), Radius, Ceiling));
    }
}
