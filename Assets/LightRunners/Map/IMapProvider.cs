using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Map
{
    /// <summary>
    /// The map seam (spec §10.1). Implemented by <see cref="OSMMinimapView"/>, which
    /// self-registers on the ServiceLocator in Awake.
    /// </summary>
    public interface IMapProvider
    {
        void Initialize(double latitude, double longitude, int zoom);
        void UpdateCenter(double latitude, double longitude);
        void SetZoom(int zoom);
        void ShowPlayerBeacon(Color color);
        void UpdatePlayerBeacon(GeoPoint position);
        void DrawTrailOverlay(string playerId, IReadOnlyList<TrailPoint> points, Color color);
        void RemoveTrailOverlay(string playerId);
        void Show();
        void Hide();
        bool IsVisible { get; }
        bool IsInitialized { get; }
    }
}
