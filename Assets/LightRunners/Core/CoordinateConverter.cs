using System;
using UnityEngine;

namespace LightRunners.Core
{
    /// <summary>
    /// Two coordinate spaces coexist (spec §5):
    ///   • Geo = WGS84 lat/lon/alt.
    ///   • World = local Unity metres relative to a lazily-set reference point:
    ///             X = east, Y = up (altitude), Z = north.
    ///
    /// This is the local-metre space used by trails, AR, and collision — the *only* place
    /// that should treat geo as planar near a reference point. The minimap uses a separate
    /// mercator pixel space (spec §5.2) and must NOT be unified with this.
    ///
    /// Stateful: <see cref="EnsureReference"/> makes the first call self-initializing.
    /// </summary>
    public static class CoordinateConverter
    {
        /// <summary>WGS84 semi-major axis in metres. Spec §5.1.</summary>
        public const double EarthRadiusMeters = 6_378_137.0;

        private const double Deg2Rad = Math.PI / 180.0;
        private const double Rad2Deg = 180.0 / Math.PI;

        private static bool _hasReference;
        private static double _refLat;
        private static double _refLon;
        private static double _refLatRad;
        private static double _metersPerDegLat;
        private static double _metersPerDegLon;

        public static bool HasReference => _hasReference;
        public static double ReferenceLatitude => _refLat;
        public static double ReferenceLongitude => _refLon;

        /// <summary>Reset to no reference. Call at the start of each run (spec §5.1: ref = first fix).</summary>
        public static void Reset()
        {
            _hasReference = false;
        }

        /// <summary>Set the reference origin explicitly. Required before any geo→world conversion.</summary>
        public static void SetReference(double latitude, double longitude)
        {
            _refLat = latitude;
            _refLon = longitude;
            _refLatRad = latitude * Deg2Rad;
            // Equirectangular approximation scaled by cos(refLat) for longitude (spec §5.1).
            _metersPerDegLat = Math.PI * EarthRadiusMeters / 180.0;
            _metersPerDegLon = _metersPerDegLat * Math.Cos(_refLatRad);
            _hasReference = true;
        }

        /// <summary>If no reference is set, take <paramref name="point"/> as the origin. Idempotent.</summary>
        public static void EnsureReference(GeoPoint point)
        {
            if (!_hasReference) SetReference(point.latitude, point.longitude);
        }

        /// <summary>
        /// Geo → local Unity metres. Returns a Vector3 with X = east, Y = altitude, Z = north.
        /// The reference must already be set (call <see cref="EnsureReference"/> first if unsure).
        /// </summary>
        public static Vector3 GeoToWorld(GeoPoint geo)
        {
            if (!_hasReference) SetReference(geo.latitude, geo.longitude);
            double x = (geo.longitude - _refLon) * _metersPerDegLon;   // east
            double z = (geo.latitude - _refLat) * _metersPerDegLat;    // north
            double y = geo.altitude;                                    // up
            return new Vector3((float)x, (float)y, (float)z);
        }

        /// <summary>Local Unity metres → Geo (the inverse of <see cref="GeoToWorld"/>).</summary>
        public static GeoPoint WorldToGeo(Vector3 world)
        {
            if (!_hasReference)
                return new GeoPoint(0, 0, world.y);

            double lon = _refLon + (world.x / _metersPerDegLon);
            double lat = _refLat + (world.z / _metersPerDegLat);
            return new GeoPoint(lat, lon, world.y);
        }

        /// <summary>Compass bearing in degrees, 0 = north, clockwise. Spec §5.1.</summary>
        public static double Bearing(GeoPoint from, GeoPoint to)
        {
            // Bearing on a sphere using local equirectangular projection is consistent with
            // how we build World space (east/north), so compute from the delta in metres.
            if (!_hasReference) EnsureReference(from);
            double dx = (to.longitude - from.longitude) * _metersPerDegLon; // east
            double dz = (to.latitude - from.latitude) * _metersPerDegLat;   // north
            double b = Math.Atan2(dx, dz) * Rad2Deg; // 0 = +Z (north), CCW-positive atan2 → CW-positive when X is east
            return (b + 360.0) % 360.0;
        }
    }
}
