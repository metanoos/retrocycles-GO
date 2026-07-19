using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;
using LightRunners.Beacon;

namespace LightRunners.Map
{
    /// <summary>
    /// Corner RawImage minimap, expandable on tap (3×) — spec §10.2. Implements
    /// <see cref="IMapProvider"/> and self-registers in Awake. Subscribes to
    /// <c>LocationProvider.OnPositionUpdated</c> (recenters) and
    /// <c>TrailManager.OnLocalPointAdded</c> / <c>OnRemoteTrailUpdated</c> (draws trail
    /// polylines in the owner's beacon color). Redraws all overlays on recenter.
    /// </summary>
    public class OSMMinimapView : MonoBehaviour, IMapProvider
    {
        [Header("UI (wired by the scene generator)")]
        [SerializeField] private RawImage baseImage;     // composite tiles
        [SerializeField] private RawImage overlayImage;  // player dot + trails
        [SerializeField] private Button expandButton;

        private OSMTileProvider _tiles;
        private MapTileRenderer _renderer;

        private double _centerLat, _centerLon;
        private int _zoom;
        private bool _expanded;
        private Vector2 _collapsedSize;

        private Color _playerColor = Color.cyan;
        private bool _playerVisible;
        private GeoPoint _playerPos;

        // Overlay redraw is batched: mark dirty, redraw at most once per frame in LateUpdate.
        private bool _overlayDirty;

        public bool IsVisible => gameObject.activeInHierarchy;
        public bool IsInitialized { get; private set; }

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────
        private void Awake()
        {
            ServiceLocator.TryRegister<IMapProvider>(this);
            _tiles = GetComponent<OSMTileProvider>();
            if (_tiles == null) _tiles = gameObject.AddComponent<OSMTileProvider>();
            _renderer = new MapTileRenderer();

            if (baseImage != null) baseImage.texture = _renderer.Composite;
            if (overlayImage != null) overlayImage.texture = _renderer.Overlay;
            if (expandButton != null) expandButton.onClick.AddListener(ToggleExpand);
        }

        private void Start()
        {
            GameConfig cfg = GameConfig.Active;
            double lat = cfg.defaultLatitude, lon = cfg.defaultLongitude;
            if (LocationProvider.HasInstance)
            {
                var p = LocationProvider.Instance.CurrentPosition;
                if (p.latitude != 0.0 || p.longitude != 0.0) { lat = p.latitude; lon = p.longitude; }
            }
            Initialize(lat, lon, cfg.osmDefaultZoom);

            if (LocationProvider.HasInstance)
                LocationProvider.Instance.OnPositionUpdated += OnPositionUpdated;
            if (TrailManager.HasInstance)
            {
                TrailManager.Instance.OnLocalPointAdded += OnLocalPointAdded;
                TrailManager.Instance.OnRemoteTrailUpdated += OnRemoteTrailUpdated;
                TrailManager.Instance.OnRemoteTrailRemoved += OnRemoteTrailRemoved;
            }

            var rt = baseImage != null ? baseImage.rectTransform.parent as RectTransform : null;
            if (rt != null) _collapsedSize = rt.sizeDelta;
        }

        private void OnDestroy()
        {
            if (LocationProvider.HasInstance)
                LocationProvider.Instance.OnPositionUpdated -= OnPositionUpdated;
            if (TrailManager.HasInstance)
            {
                TrailManager.Instance.OnLocalPointAdded -= OnLocalPointAdded;
                TrailManager.Instance.OnRemoteTrailUpdated -= OnRemoteTrailUpdated;
                TrailManager.Instance.OnRemoteTrailRemoved -= OnRemoteTrailRemoved;
            }
        }

        private void LateUpdate()
        {
            if (!_overlayDirty || !IsInitialized) return;
            _overlayDirty = false;
            RedrawOverlay();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Zoom on scroll (mouse wheel / trackpad) — only when the pointer is over the map,
        // so scrolling elsewhere (HUD, future panels) doesn't hijack the gesture.
        // ─────────────────────────────────────────────────────────────────────
        private float _zoomAccumulator;
        private float _lastScrollLog = -1f;

        private void Update()
        {
            if (!IsInitialized) return;

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Approximately(scroll, 0f)) scroll = GetInputSystemScroll();

            // TEMP DIAGNOSTIC: log non-zero scroll so we can confirm input reaches Update.
            if (!Mathf.Approximately(scroll, 0f) && Time.unscaledTime - _lastScrollLog > 0.5f)
            {
                _lastScrollLog = Time.unscaledTime;
                Debug.Log($"[OSMMinimapView] scroll delta={scroll} overMap={IsPointerOverMap()} zoom={_zoom}");
            }

            if (Mathf.Approximately(scroll, 0f)) return;
            if (!IsPointerOverMap()) return;

            // Trackpads fire many small deltas; accumulate to a full integer step before applying.
            _zoomAccumulator += scroll;
            if (Mathf.Abs(_zoomAccumulator) < 1f) return;
            int steps = (int)Mathf.Sign(_zoomAccumulator);
            _zoomAccumulator -= steps;
            SetZoom(_zoom + steps);
        }

        private bool IsPointerOverMap()
        {
            var rt = baseImage != null ? baseImage.rectTransform : (transform as RectTransform);
            if (rt == null) return false;
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : Camera.main;
            return RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam);
        }

        // InputSystem fallback for scroll — same "Both" backend issue as keyboard: legacy
        // Input.mouseScrollDelta can return zero. Reflection avoids an asmdef reference.
        private static readonly PropertyInfo _mouseCurrentProp =
            Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem")?
                .GetProperty("current", BindingFlags.Public | BindingFlags.Static);

        private static float GetInputSystemScroll()
        {
            try
            {
                object mouse = _mouseCurrentProp?.GetValue(null);
                if (mouse == null) return 0f;
                var scrollProp = mouse.GetType().GetProperty("scroll");
                object scrollValue = scrollProp?.GetValue(mouse);
                if (scrollValue == null) return 0f;
                var yProp = scrollValue.GetType().GetProperty("y");
                return yProp != null ? (float)(double)yProp.GetValue(scrollValue)! : 0f;
            }
            catch { return 0f; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // IMapProvider
        // ─────────────────────────────────────────────────────────────────────
        public void Initialize(double latitude, double longitude, int zoom)
        {
            _centerLat = latitude;
            _centerLon = longitude;
            _zoom = zoom;
            _renderer.SetCenter(latitude, longitude, zoom);
            FetchGrid();
            IsInitialized = true;
            _overlayDirty = true;
        }

        public void UpdateCenter(double latitude, double longitude)
        {
            _centerLat = latitude;
            _centerLon = longitude;
            if (_renderer.SetCenter(latitude, longitude, _zoom))
                FetchGrid();          // crossed into a new center tile
            _overlayDirty = true;     // player dot moved regardless
        }

        public void SetZoom(int zoom)
        {
            _zoom = Mathf.Clamp(zoom, 3, 19);
            if (_renderer.SetCenter(_centerLat, _centerLon, _zoom))
                FetchGrid();
            _overlayDirty = true;
        }

        public void ShowPlayerBeacon(Color color)
        {
            _playerColor = color;
            _playerVisible = true;
            _overlayDirty = true;
        }

        public void UpdatePlayerBeacon(GeoPoint position)
        {
            _playerPos = position;
            _playerVisible = true;
            _overlayDirty = true;
        }

        public void DrawTrailOverlay(string playerId, IReadOnlyList<TrailPoint> points, Color color)
        {
            // Trails are re-read from TrailManager on every redraw; a direct call just dirties.
            _overlayDirty = true;
        }

        public void RemoveTrailOverlay(string playerId) => _overlayDirty = true;

        public void Show() => gameObject.SetActive(true);
        public void Hide() => gameObject.SetActive(false);

        // ─────────────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────────────
        private void FetchGrid()
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    _renderer.GetTileCoords(dx, dy, out int x, out int y);
                    int cdx = dx, cdy = dy;
                    int wantZoom = _renderer.Zoom, wantX = _renderer.CenterTileX, wantY = _renderer.CenterTileY;
                    _tiles.GetTile(_renderer.Zoom, x, y, tex =>
                    {
                        // Drop stale responses if the grid moved while the fetch was in flight.
                        if (_renderer.Zoom != wantZoom || _renderer.CenterTileX != wantX || _renderer.CenterTileY != wantY) return;
                        _renderer.BlitTile(cdx, cdy, tex);
                    });
                }
        }

        private void RedrawOverlay()
        {
            _renderer.ClearOverlay();

            int trailCount = 0, totalPoints = 0;
            if (TrailManager.HasInstance)
            {
                foreach (var kvp in TrailManager.Instance.AllTrails)
                {
                    var trail = kvp.Value;
                    if (trail == null || trail.PointCount < 2) continue;
                    _renderer.DrawPolyline(trail.Points, trail.TrailColor);
                    trailCount++;
                    totalPoints += trail.PointCount;
                }
            }

            // TEMP DIAGNOSTIC: confirms whether trail data reaches the overlay renderer.
            Debug.Log($"[OSMMinimapView] RedrawOverlay: trails={trailCount} points={totalPoints} playerVisible={_playerVisible} center=({_centerLat:F6},{_centerLon:F6})");

            if (_playerVisible)
            {
                var p = _renderer.GeoToPixel(_playerPos.latitude, _playerPos.longitude);
                _renderer.DrawPlayerDot(p, _playerColor);
            }

            _renderer.ApplyOverlay();
        }

        private void OnPositionUpdated(GeoPoint pos)
        {
            _playerPos = pos;
            _playerVisible = true;
            if (BeaconFormManager.HasInstance)
                _playerColor = BeaconFormManager.Instance.GetTrailColor(BeaconFormManager.Instance.SelectedForm);
            UpdateCenter(pos.latitude, pos.longitude);
        }

        private void OnLocalPointAdded(TrailPoint p) => _overlayDirty = true;
        private void OnRemoteTrailUpdated(string playerId) => _overlayDirty = true;
        private void OnRemoteTrailRemoved(string playerId) => _overlayDirty = true;

        private void ToggleExpand()
        {
            _expanded = !_expanded;
            var rt = transform as RectTransform;
            if (rt == null) return;
            if (_collapsedSize == Vector2.zero) _collapsedSize = rt.sizeDelta;
            rt.sizeDelta = _expanded ? _collapsedSize * 3f : _collapsedSize;
        }
    }
}
