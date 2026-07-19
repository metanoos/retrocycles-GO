using NUnit.Framework;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Tests
{
    /// <summary>Spec §26: geo↔world round-trip, cardinal bearings, reference reset.</summary>
    public class CoordinateConverterTests
    {
        [SetUp]
        public void SetUp() => CoordinateConverter.Reset();

        [Test]
        public void RoundTrip_Within5km_IsSubCentimeter()
        {
            CoordinateConverter.SetReference(37.7749, -122.4194);

            // ~3.5 km northeast of the reference.
            var geo = new GeoPoint(37.8049, -122.3894, 12.5);
            Vector3 world = CoordinateConverter.GeoToWorld(geo);
            GeoPoint back = CoordinateConverter.WorldToGeo(world);

            double err = geo.HorizontalDistanceTo(back);
            Assert.Less(err, 0.01, $"round-trip error {err * 100:F2} cm");
            Assert.AreEqual(geo.altitude, back.altitude, 1e-3);
        }

        [Test]
        public void GeoToWorld_AxesAreEastAndNorth()
        {
            CoordinateConverter.SetReference(37.0, -122.0);

            // Due north: +Z only.
            Vector3 north = CoordinateConverter.GeoToWorld(new GeoPoint(37.001, -122.0));
            Assert.Greater(north.z, 0f);
            Assert.AreEqual(0f, north.x, 0.01f);

            // Due east: +X only.
            Vector3 east = CoordinateConverter.GeoToWorld(new GeoPoint(37.0, -121.999));
            Assert.Greater(east.x, 0f);
            Assert.AreEqual(0f, east.z, 0.01f);
        }

        [Test]
        public void Bearing_CardinalDirections()
        {
            CoordinateConverter.SetReference(37.0, -122.0);
            var origin = new GeoPoint(37.0, -122.0);

            Assert.AreEqual(0.0, CoordinateConverter.Bearing(origin, new GeoPoint(37.001, -122.0)), 0.5);   // N
            Assert.AreEqual(90.0, CoordinateConverter.Bearing(origin, new GeoPoint(37.0, -121.999)), 0.5);  // E
            Assert.AreEqual(180.0, CoordinateConverter.Bearing(origin, new GeoPoint(36.999, -122.0)), 0.5); // S
            Assert.AreEqual(270.0, CoordinateConverter.Bearing(origin, new GeoPoint(37.0, -122.001)), 0.5); // W
        }

        [Test]
        public void SetReference_ReOrigins_BetweenRuns()
        {
            // Run 1 reference.
            CoordinateConverter.SetReference(37.0, -122.0);
            Vector3 w1 = CoordinateConverter.GeoToWorld(new GeoPoint(37.001, -122.0));
            Assert.Greater(w1.z, 100f);

            // Run 2 re-origin at that point (spec §5.1: reference lifetime = one run).
            CoordinateConverter.SetReference(37.001, -122.0);
            Vector3 w2 = CoordinateConverter.GeoToWorld(new GeoPoint(37.001, -122.0));
            Assert.AreEqual(0f, w2.z, 0.01f);
        }

        [Test]
        public void EnsureReference_IsIdempotent()
        {
            CoordinateConverter.EnsureReference(new GeoPoint(10.0, 20.0));
            CoordinateConverter.EnsureReference(new GeoPoint(50.0, 60.0)); // must not move the origin
            Assert.AreEqual(10.0, CoordinateConverter.ReferenceLatitude, 1e-9);
            Assert.AreEqual(20.0, CoordinateConverter.ReferenceLongitude, 1e-9);
        }
    }

    /// <summary>Spec §26: haversine sanity against known distances.</summary>
    public class GeoPointTests
    {
        [Test]
        public void Haversine_OneDegreeLatitude_At6371kmRadius()
        {
            // πR/180 with R = 6,371,000 → 111,194.93 m.
            var a = new GeoPoint(0, 0);
            var b = new GeoPoint(1, 0);
            Assert.AreEqual(111194.93, a.HorizontalDistanceTo(b), 1.0);
        }

        [Test]
        public void Haversine_ZeroDistance() =>
            Assert.AreEqual(0.0, new GeoPoint(37.5, -122.5).HorizontalDistanceTo(new GeoPoint(37.5, -122.5)), 1e-9);

        [Test]
        public void Haversine_AcrossAntimeridian()
        {
            // 0.2° of longitude at the equator, crossing ±180.
            var a = new GeoPoint(0, 179.9);
            var b = new GeoPoint(0, -179.9);
            Assert.AreEqual(111194.93 * 0.2, a.HorizontalDistanceTo(b), 5.0);
        }

        [Test]
        public void VerticalAnd3D_Distances()
        {
            var a = new GeoPoint(0, 0, 10);
            var b = new GeoPoint(0, 0, 50);
            Assert.AreEqual(40.0, a.VerticalDistanceTo(b), 1e-9);
            Assert.AreEqual(40.0, a.Distance3DTo(b), 1e-6);
        }
    }
}
