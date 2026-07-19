using System;
using UnityEngine;

namespace LightRunners.Core
{
    /// <summary>
    /// A WGS84 geodetic position. The single source of truth for "where" a thing is.
    /// </summary>
    [Serializable]
    public struct GeoPoint : IEquatable<GeoPoint>
    {
        public double latitude;
        public double longitude;
        public double altitude;

        public GeoPoint(double latitude, double longitude, double altitude = 0.0)
        {
            this.latitude = latitude;
            this.longitude = longitude;
            this.altitude = altitude;
        }

        /// <summary>Mean Earth radius in metres (Haversine / equirectangular use the same value).</summary>
        public const double EarthRadiusMeters = 6_371_000.0;

        /// <summary>Great-circle horizontal distance to <paramref name="other"/>, ignoring altitude. Spec §4.1.</summary>
        public double HorizontalDistanceTo(GeoPoint other) => Haversine(this, other);

        /// <summary>Absolute altitude difference. Spec §4.1.</summary>
        public double VerticalDistanceTo(GeoPoint other) => Math.Abs(altitude - other.altitude);

        /// <summary>Pythagorean 3D distance using Haversine horizontal + alt delta.</summary>
        public double Distance3DTo(GeoPoint other)
        {
            double dh = HorizontalDistanceTo(other);
            double dv = other.altitude - altitude;
            return Math.Sqrt(dh * dh + dv * dv);
        }

        public bool Equals(GeoPoint other)
            => latitude.Equals(other.latitude) && longitude.Equals(other.longitude) && altitude.Equals(other.altitude);

        public override bool Equals(object obj) => obj is GeoPoint p && Equals(p);
        public override int GetHashCode() => (latitude, longitude, altitude).GetHashCode();
        public static bool operator ==(GeoPoint a, GeoPoint b) => a.Equals(b);
        public static bool operator !=(GeoPoint a, GeoPoint b) => !a.Equals(b);

        public override string ToString()
            => $"({latitude:F6}, {longitude:F6}, {altitude:F1}m)";

        /// <summary>
        /// Standard Haversine in metres. Spec §4.1.
        /// </summary>
        public static double Haversine(GeoPoint a, GeoPoint b)
        {
            const double R = EarthRadiusMeters;
            double lat1 = a.latitude * Math.PI / 180.0;
            double lat2 = b.latitude * Math.PI / 180.0;
            double dLat = (b.latitude - a.latitude) * Math.PI / 180.0;
            double dLon = (b.longitude - a.longitude) * Math.PI / 180.0;

            double sinDLat = Math.Sin(dLat * 0.5);
            double sinDLon = Math.Sin(dLon * 0.5);
            double h = sinDLat * sinDLat
                       + Math.Cos(lat1) * Math.Cos(lat2) * sinDLon * sinDLon;
            double c = 2.0 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1.0 - h));
            return R * c;
        }
    }
}
