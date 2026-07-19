using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;

namespace LightRunners.UI
{
    /// <summary>
    /// Small corner radar (decision H — AR-primary + radar). Renders gate blips and nearby
    /// runner blips in a circular radius around the local player. The widget EXPANDS while the
    /// player is stopped (no movement for &gt; <see cref="expandAfterStoppedSeconds"/>) and
    /// CONTRACTS when the player moves again. SEPARATE from <c>OSMMinimapView</c> — does not
    /// touch the Map assembly at all.
    ///
    /// Design notes:
    ///   • UI assembly references Core + Beacon + Trail + Location. Trail gives
    ///     <see cref="TrailManager.AllTrails"/> for runner positions; Location gives
    ///     <see cref="LocationProvider"/> for the local fix; Core gives <see cref="GameEvents"/>
    ///     + <see cref="GeoPoint"/> + <see cref="CoordinateConverter"/>. We deliberately do NOT
    ///     reference LightRunners.Lightfield — gate positions are captured from
    ///     <see cref="GameEvents.GateSpawned"/> / <see cref="GameEvents.GateDespawned"/> so the
    ///     radar stays decoupled from Track B.
    ///   • Visuals are built procedurally (no prefab) so the scene runs with zero art.
    /// </summary>
    public class TacticalRadar : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private RectTransform radarRoot;          // The container that scales
        [SerializeField] private RectTransform blipLayer;          // Parent for runtime blips
        [SerializeField] private Image ringImage;                  // Optional decorative ring

        [Header("Sizing (decision H)")]
        [Tooltip("Scale (relative to radarRoot's base) when the player is moving.")]
        [SerializeField] private float contractedScale = 0.6f;
        [Tooltip("Scale (relative to radarRoot's base) when the player has been stopped.")]
        [SerializeField] private float expandedScale = 1.4f;
        [Tooltip("Stop duration (s) before the radar expands.")]
        [SerializeField] private float expandAfterStoppedSeconds = 1.5f;
        [Tooltip("Lerp speed for the expand/contract animation.")]
        [SerializeField] private float sizeLerpSpeed = 4f;

        [Header("Range")]
        [Tooltip("World-metre radius the radar covers.")]
        [SerializeField] private float radarRangeMeters = 100f;

        [Header("Blip visuals")]
        [SerializeField] private Color gateBlipColor = new Color(0.2f, 1f, 1f, 0.9f);
        [SerializeField] private Color runnerBlipColor = new Color(1f, 0.4f, 0.6f, 0.9f);
        [SerializeField] private Color localBlipColor = new Color(1f, 1f, 0.2f, 1f);
        [SerializeField] private int blipSizePixels = 8;

        // ─── State ────────────────────────────────────────────────────────
        private float _currentScale = 0.6f;
        private float _stoppedTimer;
        private bool _isExpanded;
        private GeoPoint _lastPlayerPos;
        private bool _haveLastPlayerPos;

        // Gate blips captured from the bus (decision G/L — positions only, no Lightfield ref).
        private readonly Dictionary<int, GeoPoint> _gates = new Dictionary<int, GeoPoint>();

        // Blip pool keyed by id (gate ids are positive; runner ids are hashed strings).
        private readonly Dictionary<int, GameObject> _gateBlips = new Dictionary<int, GameObject>();
        private readonly Dictionary<string, GameObject> _runnerBlips = new Dictionary<string, GameObject>();
        private GameObject _localBlip;

        private void OnEnable()
        {
            GameEvents.GateSpawned += OnGateSpawned;
            GameEvents.GateDespawned += OnGateDespawned;
            if (LocationProvider.HasInstance)
                LocationProvider.Instance.OnPositionUpdated += OnPositionUpdated;
        }

        private void OnDisable()
        {
            GameEvents.GateSpawned -= OnGateSpawned;
            GameEvents.GateDespawned -= OnGateDespawned;
            if (LocationProvider.HasInstance)
                LocationProvider.Instance.OnPositionUpdated -= OnPositionUpdated;
        }

        private void Start()
        {
            if (radarRoot == null)
            {
                // Self-host: build a minimal canvas hierarchy procedurally.
                BuildProceduralRig();
            }
            _currentScale = contractedScale;
            ApplyScale(_currentScale);
        }

        private void Update()
        {
            // Expand/contract animation.
            float target = _isExpanded ? expandedScale : contractedScale;
            if (!Mathf.Approximately(_currentScale, target))
            {
                _currentScale = Mathf.Lerp(_currentScale, target, Time.deltaTime * sizeLerpSpeed);
                ApplyScale(_currentScale);
            }

            RenderBlips();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Event handlers
        // ─────────────────────────────────────────────────────────────────────

        private void OnPositionUpdated(GeoPoint pos)
        {
            // Detect movement to drive expand/contract (decision H).
            if (_haveLastPlayerPos)
            {
                double moved = _lastPlayerPos.HorizontalDistanceTo(pos);
                // 1.5 m/s is a slow walk; below that counts as "stopped".
                bool moving = moved > 1.5;
                if (moving)
                {
                    _stoppedTimer = 0f;
                    _isExpanded = false;
                }
                else
                {
                    _stoppedTimer += Time.deltaTime;
                    if (_stoppedTimer >= expandAfterStoppedSeconds) _isExpanded = true;
                }
            }
            _lastPlayerPos = pos;
            _haveLastPlayerPos = true;
        }

        private void OnGateSpawned(int gateIdValue, double lat, double lon, double alt, GatePlacement placement)
        {
            _gates[gateIdValue] = new GeoPoint(lat, lon, alt);
        }

        private void OnGateDespawned(int gateIdValue)
        {
            _gates.Remove(gateIdValue);
            if (_gateBlips.TryGetValue(gateIdValue, out var go))
            {
                Destroy(go);
                _gateBlips.Remove(gateIdValue);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Rendering
        // ─────────────────────────────────────────────────────────────────────

        private void RenderBlips()
        {
            if (blipLayer == null) return;
            GeoPoint center = LocationProvider.HasInstance
                ? LocationProvider.Instance.CurrentPosition
                : _lastPlayerPos;
            if (!CoordinateConverter.HasReference) CoordinateConverter.EnsureReference(center);

            // Local player at centre.
            EnsureLocalBlip();
            PositionBlip(_localBlip, Vector2.zero);

            // Gate blips.
            var activeGateIds = new List<int>(_gates.Keys);
            foreach (var id in activeGateIds)
            {
                var blip = EnsureGateBlip(id);
                Vector2? uv = WorldToRadarUV(center, _gates[id]);
                if (uv.HasValue) PositionBlip(blip, uv.Value);
                else blip.SetActive(false);
            }

            // Runner blips (from TrailManager.AllTrails).
            if (TrailManager.HasInstance)
            {
                string localId = TrailManager.Instance.LocalTrail?.OwnerId;
                foreach (var kvp in TrailManager.Instance.AllTrails)
                {
                    if (kvp.Value == null || kvp.Value.PointCount == 0) continue;
                    if (kvp.Key == localId) continue;
                    var blip = EnsureRunnerBlip(kvp.Key);
                    Vector2? uv = WorldToRadarUV(center, kvp.Value.LastPoint.position);
                    if (uv.HasValue)
                    {
                        blip.SetActive(true);
                        PositionBlip(blip, uv.Value);
                    }
                    else blip.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Convert a world GeoPoint into a normalised radar UV in [-1, 1] on each axis. Returns
        /// null if the point is outside <see cref="radarRangeMeters"/> (no blip drawn).
        /// </summary>
        private Vector2? WorldToRadarUV(GeoPoint center, GeoPoint point)
        {
            if (!CoordinateConverter.HasReference) return null;
            Vector3 c = CoordinateConverter.GeoToWorld(center);
            Vector3 p = CoordinateConverter.GeoToWorld(point);
            Vector3 delta = p - c; // X = east, Z = north
            float range = Mathf.Max(1f, radarRangeMeters);
            if (delta.x * delta.x + delta.z * delta.z > range * range) return null;
            return new Vector2(delta.x / range, delta.z / range);
        }

        private void PositionBlip(GameObject blip, Vector2 uv)
        {
            if (blip == null || blipLayer == null) return;
            blip.SetActive(true);
            var rt = blip.GetComponent<RectTransform>();
            if (rt == null) return;

            // Map normalised UV to the blip layer's rect half-extent.
            Vector2 size = blipLayer.rect.size;
            rt.anchoredPosition = new Vector2(uv.x * size.x * 0.5f, uv.y * size.y * 0.5f);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Blip factory (pooled-style: ensure-once, reuse, deactivate on miss)
        // ─────────────────────────────────────────────────────────────────────

        private GameObject EnsureGateBlip(int id)
        {
            if (_gateBlips.TryGetValue(id, out var go)) return go;
            go = CreateBlip($"Gate_{id}", gateBlipColor);
            _gateBlips[id] = go;
            return go;
        }

        private GameObject EnsureRunnerBlip(string playerId)
        {
            if (_runnerBlips.TryGetValue(playerId, out var go)) return go;
            go = CreateBlip($"Runner_{playerId}", runnerBlipColor);
            _runnerBlips[playerId] = go;
            return go;
        }

        private void EnsureLocalBlip()
        {
            if (_localBlip != null) return;
            _localBlip = CreateBlip("Local", localBlipColor);
        }

        private GameObject CreateBlip(string name, Color color)
        {
            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(blipLayer, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(blipSizePixels, blipSizePixels);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            return go;
        }

        private void ApplyScale(float scale)
        {
            if (radarRoot != null) radarRoot.localScale = new Vector3(scale, scale, scale);
        }

        /// <summary>Build a minimal radar rig in code (ring + blip layer) when no prefab is wired.</summary>
        private void BuildProceduralRig()
        {
            var rootGo = new GameObject("TacticalRadar_Root", typeof(RectTransform));
            rootGo.transform.SetParent(transform, false);
            radarRoot = rootGo.GetComponent<RectTransform>();
            radarRoot.anchorMin = radarRoot.anchorMax = new Vector2(1f, 1f); // top-right corner
            radarRoot.pivot = new Vector2(1f, 1f);
            radarRoot.anchoredPosition = new Vector2(-16f, -16f);
            radarRoot.sizeDelta = new Vector2(140f, 140f);

            if (ringImage == null)
            {
                var ringGo = new GameObject("Ring", typeof(Image));
                ringGo.transform.SetParent(radarRoot, false);
                ringImage = ringGo.GetComponent<Image>();
                ringImage.color = new Color(0f, 0f, 0f, 0.35f);
                ringImage.raycastTarget = false;
                var ringRt = ringImage.rectTransform;
                ringRt.anchorMin = Vector2.zero;
                ringRt.anchorMax = Vector2.one;
                ringRt.offsetMin = ringRt.offsetMax = Vector2.zero;
            }

            var blipGo = new GameObject("BlipLayer", typeof(RectTransform));
            blipGo.transform.SetParent(radarRoot, false);
            blipLayer = blipGo.GetComponent<RectTransform>();
            blipLayer.anchorMin = Vector2.zero;
            blipLayer.anchorMax = Vector2.one;
            blipLayer.offsetMin = blipLayer.offsetMax = Vector2.zero;
        }

        // ─── Test hooks (decision H) ───────────────────────────────────────
        // The TacticalRadarExpandTests exercise the stopped→expand logic without a Canvas by
        // calling these helpers directly. They are safe no-ops in playmode.
        internal bool IsExpanded_Internal => _isExpanded;
        internal float StoppedTimer_Internal => _stoppedTimer;

        /// <summary>Test-only: simulate N seconds of stopped time in a single call.</summary>
        internal void TestSimulateStopped(float seconds)
        {
            _stoppedTimer += seconds;
            if (_stoppedTimer >= expandAfterStoppedSeconds) _isExpanded = true;
        }

        /// <summary>Test-only: simulate a movement update that contracts the radar.</summary>
        internal void TestSimulateMoved()
        {
            _stoppedTimer = 0f;
            _isExpanded = false;
        }
    }
}
