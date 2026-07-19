using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;

namespace LightRunners.UI
{
    /// <summary>
    /// FPS-style off-screen player indicator (decision I). Renders a screen-edge arrow for each
    /// remote runner whose world position is outside the camera frustum, showing identity,
    /// horizontal distance, and an above/below chevron when the runner is on a different
    /// altitude band. The current leader is highlighted (see <see cref="LeaderCrown"/> for the
    /// dedicated crown marker; this class adds a leader tint to the arrow itself).
    ///
    /// Design notes:
    ///   • Like <see cref="TacticalRadar"/>, this UI assembly references Core + Beacon + Trail +
    ///     Location. Player positions are read from <see cref="TrailManager.AllTrails"/>; the
    ///     leader comes from <see cref="ILumenScoreboard"/> via the locator (Track A) or the
    ///     <see cref="GameEvents.LeaderChanged"/> bus.
    ///   • Indicators are pooled per-player-id; arrows reset position each frame and deactivate
    ///     when the player is on-screen.
    /// </summary>
    public class OffScreenIndicator : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private Camera worldCamera;
        [SerializeField] private RectTransform indicatorLayer;     // Typically a full-screen overlay
        [SerializeField] private RectTransform arrowTemplate;       // Pooled per player (hidden by default)

        [Header("Layout")]
        [Tooltip("Inset from the screen edge (pixels) where arrows sit.")]
        [SerializeField] private float screenEdgeInset = 40f;

        [Header("Colours")]
        [SerializeField] private Color defaultArrowColor = new Color(1f, 1f, 1f, 0.85f);
        [SerializeField] private Color leaderArrowColor = new Color(1f, 0.85f, 0.2f, 1f);

        private string _currentLeaderId = string.Empty;
        private readonly Dictionary<string, RectTransform> _arrows = new Dictionary<string, RectTransform>();
        private readonly List<string> _stale = new List<string>();

        private void OnEnable()
        {
            GameEvents.LeaderChanged += OnLeaderChanged;
            if (worldCamera == null) worldCamera = Camera.main;
        }

        private void OnDisable()
        {
            GameEvents.LeaderChanged -= OnLeaderChanged;
        }

        private void OnLeaderChanged(string newLeaderId)
        {
            _currentLeaderId = newLeaderId ?? string.Empty;
        }

        private void LateUpdate()
        {
            if (worldCamera == null || indicatorLayer == null) return;
            if (!TrailManager.HasInstance) return;

            // First mark all arrows stale; we'll re-activate the ones we use.
            _stale.Clear();
            foreach (var kvp in _arrows) _stale.Add(kvp.Key);

            string localId = TrailManager.Instance.LocalTrail?.OwnerId;
            Vector3 localWorldPos = LocationProvider.HasInstance
                ? CoordinateConverter.GeoToWorld(LocationProvider.Instance.CurrentPosition)
                : (TrailManager.HasInstance && TrailManager.Instance.LocalTrail != null && TrailManager.Instance.LocalTrail.PointCount > 0
                    ? CoordinateConverter.GeoToWorld(TrailManager.Instance.LocalTrail.LastPoint.position)
                    : Vector3.zero);

            foreach (var kvp in TrailManager.Instance.AllTrails)
            {
                string pid = kvp.Key;
                var trail = kvp.Value;
                if (trail == null || trail.PointCount == 0) continue;
                if (pid == localId) continue;

                GeoPoint geo = trail.LastPoint.position;
                Vector3 world = CoordinateConverter.GeoToWorld(geo);

                Vector3 screenPos = worldCamera.WorldToScreenPoint(world);
                bool onScreen = screenPos.z > 0f
                                && screenPos.x >= 0f && screenPos.x <= Screen.width
                                && screenPos.y >= 0f && screenPos.y <= Screen.height;

                var arrow = EnsureArrow(pid);
                if (onScreen)
                {
                    arrow.gameObject.SetActive(false);
                }
                else
                {
                    arrow.gameObject.SetActive(true);
                    PositionAtScreenEdge(arrow, screenPos);
                    UpdateLabel(arrow, pid, localWorldPos, world);
                    UpdateLeaderTint(arrow, pid);
                }

                _stale.Remove(pid);
            }

            // Deactivate any arrows we didn't touch this frame (player left / lost trail).
            foreach (var pid in _stale)
            {
                if (_arrows.TryGetValue(pid, out var arrow)) arrow.gameObject.SetActive(false);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────────────

        private RectTransform EnsureArrow(string playerId)
        {
            if (_arrows.TryGetValue(playerId, out var existing)) return existing;

            RectTransform rt;
            if (arrowTemplate != null)
            {
                var go = Instantiate(arrowTemplate, indicatorLayer, false);
                rt = go.GetComponent<RectTransform>();
                go.gameObject.SetActive(false);
            }
            else
            {
                // Procedural arrow: a coloured diamond + label.
                var go = new GameObject($"Arrow_{playerId}", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(indicatorLayer, false);
                rt = go.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(32f, 32f);
                var img = go.GetComponent<Image>();
                img.color = defaultArrowColor;
                img.raycastTarget = false;

                var labelGo = new GameObject("Label", typeof(Text));
                labelGo.transform.SetParent(rt, false);
                var labelRt = labelGo.GetComponent<RectTransform>();
                labelRt.anchorMin = labelRt.anchorMax = new Vector2(0.5f, 0f);
                labelRt.pivot = new Vector2(0.5f, 1f);
                labelRt.anchoredPosition = new Vector2(0f, -4f);
                labelRt.sizeDelta = new Vector2(120f, 28f);
                var txt = labelGo.GetComponent<Text>();
                txt.alignment = TextAnchor.UpperCenter;
                txt.fontSize = 14;
                txt.color = Color.white;
                txt.raycastTarget = false;
                txt.text = playerId;
            }
            _arrows[playerId] = rt;
            return rt;
        }

        private void PositionAtScreenEdge(RectTransform arrow, Vector3 screenPos)
        {
            // Treat a behind-camera point as a flipped on-screen point so the arrow points
            // toward where the player would emerge.
            if (screenPos.z < 0f)
            {
                screenPos.x = Screen.width - screenPos.x;
                screenPos.y = Screen.height - screenPos.y;
            }

            // Normalise to [-1, 1] centred on screen centre.
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float dx = (screenPos.x - cx) / cx;
            float dy = (screenPos.y - cy) / cy;

            // Clamp the direction to the screen-edge rectangle (inset).
            float halfW = (Screen.width * 0.5f) - screenEdgeInset;
            float halfH = (Screen.height * 0.5f) - screenEdgeInset;

            float absX = Mathf.Abs(dx);
            float absY = Mathf.Abs(dy);
            float scale = Mathf.Min(halfW / Mathf.Max(0.0001f, absX), halfH / Mathf.Max(0.0001f, absY));

            float sx = cx + dx * scale * cx;
            float sy = cy + dy * scale * cy;

            // Convert back to overlay-space anchored pixels (overlay assumed to match screen).
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                indicatorLayer, new Vector2(sx, sy), null, out local);
            arrow.anchoredPosition = local;
        }

        private static void UpdateLabel(RectTransform arrow, string playerId, Vector3 localWorld, Vector3 remoteWorld)
        {
            var txt = arrow.GetComponentInChildren<Text>();
            if (txt == null) return;

            double horizontal = HorizontalDistance(localWorld, remoteWorld);
            string aboveBelow = VerticalDirection(localWorld, remoteWorld);
            string name = StringUtils.RunnerDisplayName(playerId);
            string dist = horizontal < 1000.0 ? $"{horizontal:F0} m" : $"{horizontal / 1000.0:F2} km";
            txt.text = $"{name}\n{dist}{(string.IsNullOrEmpty(aboveBelow) ? "" : "  " + aboveBelow)}";
        }

        private void UpdateLeaderTint(RectTransform arrow, string playerId)
        {
            var img = arrow.GetComponent<Image>();
            if (img == null) return;
            img.color = playerId == _currentLeaderId ? leaderArrowColor : defaultArrowColor;
        }

        private static double HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static string VerticalDirection(Vector3 local, Vector3 remote)
        {
            float dy = remote.y - local.y;
            if (dy > 1.5f) return "▲";
            if (dy < -1.5f) return "▼";
            return string.Empty;
        }
    }
}
