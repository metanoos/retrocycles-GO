using UnityEngine;
using UnityEngine.UI;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;

namespace LightRunners.UI
{
    /// <summary>
    /// Visually crowns the current leader (decision I). Subscribes to
    /// <see cref="GameEvents.LeaderChanged"/> and tracks the leader's avatar by following their
    /// trail's last-known world position (resolved through <see cref="CoordinateConverter"/>).
    /// The crown sits a fixed height above the leader's head; it deactivates when there's no
    /// leader (tied / zero players) or when the leader is the local player (you don't need to be
    /// told you're winning — the HUD already shows it).
    ///
    /// Design notes: like the other UI widgets, this assembly references Core + Beacon + Trail +
    /// Location. The crown is a procedural Image (no prefab required); a richer version would
    /// project the leader's screen-space position from the world camera — for the milestone we
    /// attach to a screen-space overlay and reposition each frame from the leader's geo position.
    /// </summary>
    public class LeaderCrown : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private Camera worldCamera;
        [SerializeField] private RectTransform overlay;            // Screen-space overlay parent

        [Header("Positioning")]
        [Tooltip("World-space offset (m) above the leader's last trail point where the crown floats.")]
        [SerializeField] private float crownHeightMeters = 2.5f;
        [Tooltip("Pixel offset (down from the projected world point) so the crown sits clear of the avatar.")]
        [SerializeField] private float pixelOffsetY = -32f;

        [Header("Visuals")]
        [SerializeField] private Color crownColor = new Color(1f, 0.85f, 0.2f, 1f);
        [SerializeField] private int crownSizePixels = 32;

        private string _leaderId = string.Empty;
        private GameObject _crown;
        private RectTransform _crownRt;

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
            _leaderId = newLeaderId ?? string.Empty;
            if (string.IsNullOrEmpty(_leaderId) && _crown != null) _crown.SetActive(false);
        }

        private void LateUpdate()
        {
            if (overlay == null || worldCamera == null) return;
            if (string.IsNullOrEmpty(_leaderId)) { HideCrown(); return; }
            if (!TrailManager.HasInstance) { HideCrown(); return; }

            // Don't crown the local player (the HUD already conveys "you're leading").
            string localId = TrailManager.Instance.LocalTrail?.OwnerId;
            if (_leaderId == localId) { HideCrown(); return; }

            if (!TrailManager.Instance.AllTrails.TryGetValue(_leaderId, out var trail)
                || trail == null || trail.PointCount == 0)
            {
                HideCrown();
                return;
            }

            GeoPoint geo = trail.LastPoint.position;
            Vector3 world = CoordinateConverter.GeoToWorld(geo);
            world.y += crownHeightMeters;

            Vector3 screen = worldCamera.WorldToScreenPoint(world);
            if (screen.z <= 0f) { HideCrown(); return; }   // behind camera

            EnsureCrown();
            _crown.SetActive(true);

            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                overlay, new Vector2(screen.x, screen.y), null, out local);
            _crownRt.anchoredPosition = new Vector2(local.x, local.y + pixelOffsetY);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────────────

        private void EnsureCrown()
        {
            if (_crown != null) return;
            _crown = new GameObject("LeaderCrown", typeof(RectTransform), typeof(Image));
            _crown.transform.SetParent(overlay, false);
            _crownRt = _crown.GetComponent<RectTransform>();
            _crownRt.sizeDelta = new Vector2(crownSizePixels, crownSizePixels);
            _crownRt.anchorMin = _crownRt.anchorMax = new Vector2(0.5f, 0.5f);
            _crownRt.pivot = new Vector2(0.5f, 0.5f);
            var img = _crown.GetComponent<Image>();
            img.color = crownColor;
            img.raycastTarget = false;
            _crown.SetActive(false);
        }

        private void HideCrown()
        {
            if (_crown != null) _crown.SetActive(false);
        }
    }
}
