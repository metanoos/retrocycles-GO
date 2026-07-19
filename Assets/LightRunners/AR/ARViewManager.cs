#if UNITY_XR_ARFOUNDATION
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;
using LightRunners.Backend;

namespace LightRunners.AR
{
    /// <summary>
    /// AR Foundation 6 lifecycle owner (spec §11.2). Implements <see cref="IARViewController"/>
    /// and self-registers on the ServiceLocator so ViewTransitionManager can drive it without
    /// an AR Foundation reference. Uses <see cref="XROrigin"/> (Unity.XR.CoreUtils) — never
    /// the deprecated ARSessionOrigin (pitfall #6).
    ///
    /// EnterAR enables the session/origin/plane manager; the first detected plane locks the
    /// altitude baseline; persisted nearby trails come from <see cref="TrailRepository"/>
    /// (last 24 h only, spec §23) and project as <see cref="ARTrailObject"/>s.
    /// </summary>
    public class ARViewManager : Singleton<ARViewManager>, IARViewController
    {
        [Header("AR Foundation 6 (wired by the scene generator)")]
        [SerializeField] private ARSession arSession;
        [SerializeField] private XROrigin xrOrigin;
        [SerializeField] private ARPlaneManager planeManager;

        [Header("Parents")]
        [SerializeField] private Transform arTrailParent;
        [SerializeField] private Transform arBeaconParent;

        private readonly List<ARTrailObject> _trailObjects = new List<ARTrailObject>();
        private readonly Dictionary<string, GameObject> _beacons = new Dictionary<string, GameObject>();
        private bool _arActive;
        private bool _altitudeBaselineLocked;
        private float _heightOffset;
        private float _visibilityTimer;

        private bool ARSupportedPlatform =>
#if UNITY_ANDROID || UNITY_IOS
            true;
#else
            false;
#endif

        public bool IsARAvailable => ARSupportedPlatform && arSession != null;
        public bool IsARActive => _arActive;

        protected override void Awake()
        {
            base.Awake();
        }

        private void Start()
        {
            ServiceLocator.TryRegister<IARViewController>(this);
            SetStackEnabled(false);
        }

        // ─────────────────────────────────────────────────────────────────────
        // IARViewController
        // ─────────────────────────────────────────────────────────────────────
        public void EnterAR()
        {
            if (!IsARAvailable)
            {
                Debug.LogWarning("[ARViewManager] EnterAR on an unsupported platform — ignoring.");
                return;
            }
            _arActive = true;
            _altitudeBaselineLocked = false;
            SetStackEnabled(true);

            if (planeManager != null)
                planeManager.trackablesChanged.AddListener(OnPlanesChanged);

            if (LocationProvider.HasInstance)
                LoadNearbyTrails(LocationProvider.Instance.CurrentPosition);
        }

        public void ExitAR()
        {
            _arActive = false;
            if (planeManager != null)
                planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
            SetStackEnabled(false);
            ClearTrailObjects();
        }

        public void LoadNearbyTrails(GeoPoint center)
        {
            var repo = ServiceLocator.Get<TrailRepository>();
            if (repo == null) return;

            GameConfig cfg = GameConfig.Active;
            repo.LoadNearbyTrails(center, cfg.arTrailRenderDistance, cfg.arMaxNearbyTrails,
                (snapshots, colors) =>
                {
                    if (!_arActive) return;
                    ClearTrailObjects();
                    for (int i = 0; i < snapshots.Count; i++)
                        ProjectTrailIntoAR(snapshots[i], i < colors.Count ? colors[i] : Color.cyan);
                });
        }

        public void UpdateARHeightOffset(float offset) => _heightOffset = offset;

        // ─────────────────────────────────────────────────────────────────────
        // Projection
        // ─────────────────────────────────────────────────────────────────────
        private void ProjectTrailIntoAR(TrailSnapshot snapshot, Color color)
        {
            var go = new GameObject($"ARTrail_{snapshot.ownerId}", typeof(LineRenderer), typeof(ARTrailObject));
            go.transform.SetParent(arTrailParent != null ? arTrailParent : transform, false);
            var obj = go.GetComponent<ARTrailObject>();
            obj.Build(snapshot, color, GameConfig.Active.trailGroundOffset + _heightOffset);
            _trailObjects.Add(obj);
        }

        public void ShowBeaconInAR(string playerId, Color color)
        {
            if (_beacons.ContainsKey(playerId)) return;
            var go = new GameObject($"ARBeacon_{playerId}");
            go.transform.SetParent(arBeaconParent != null ? arBeaconParent : transform, false);
            var bc = go.AddComponent<Beacon.BeaconController>();
            bc.SetForm(BeaconFormType.Sphere);
            bc.SetTrailColor(color);
            _beacons[playerId] = go;
        }

        public void UpdateBeaconPosition(string playerId, GeoPoint position, float heading)
        {
            if (!_beacons.TryGetValue(playerId, out var go) || go == null) return;
            Vector3 w = CoordinateConverter.GeoToWorld(position);
            w.y += GameConfig.Active.trailGroundOffset + _heightOffset;
            go.GetComponent<Beacon.BeaconController>()?.UpdatePosition(w, heading);
        }

        public void RemoveBeacon(string playerId)
        {
            if (_beacons.TryGetValue(playerId, out var go) && go != null) Destroy(go);
            _beacons.Remove(playerId);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────────────
        private void SetStackEnabled(bool on)
        {
            if (arSession != null) arSession.enabled = on;
            if (xrOrigin != null) xrOrigin.gameObject.SetActive(on);
            if (planeManager != null) planeManager.enabled = on;
        }

        private void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            // First detected plane locks the initial altitude baseline (spec §11.2): the
            // plane's world Y anchors "ground" so trails sit on the real floor, not at raw
            // GPS altitude.
            if (_altitudeBaselineLocked || args.added == null || args.added.Count == 0) return;
            _altitudeBaselineLocked = true;
            float planeY = args.added[0].transform.position.y;
            float gpsY = LocationProvider.HasInstance
                ? (float)LocationProvider.Instance.CurrentPosition.altitude
                : 0f;
            UpdateARHeightOffset(planeY - gpsY);
        }

        private void Update()
        {
            if (!_arActive || _trailObjects.Count == 0) return;
            _visibilityTimer -= Time.deltaTime;
            if (_visibilityTimer > 0f) return;
            _visibilityTimer = 0.25f;

            Camera cam = xrOrigin != null ? xrOrigin.Camera : Camera.main;
            if (cam == null) return;
            float maxDist = GameConfig.Active.arTrailRenderDistance;
            foreach (var t in _trailObjects)
                if (t != null) t.UpdateVisibility(cam.transform.position, maxDist);
        }

        private void ClearTrailObjects()
        {
            foreach (var t in _trailObjects)
                if (t != null) Destroy(t.gameObject);
            _trailObjects.Clear();
        }
    }
}
#endif
