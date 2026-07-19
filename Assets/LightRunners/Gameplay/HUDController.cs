using UnityEngine;
using UnityEngine.UI;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Binds the HUD to game state (spec §2.2, formats pinned in §25). The HUD is hidden
    /// except in <see cref="GameState.Running"/>, where it shows speed / altitude / elapsed
    /// time / distance / live runner count and the End Run button. Start Run is a sibling of
    /// the HUD panel, visible at Lobby. Updates ~4×/s, not per-frame.
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        [Header("HUD Panel + fields")]
        [SerializeField] private GameObject hudPanel;
        [SerializeField] private TMP_TextAdaptor speedText;
        [SerializeField] private TMP_TextAdaptor altitudeText;
        [SerializeField] private TMP_TextAdaptor timeText;
        [SerializeField] private TMP_TextAdaptor distanceText;
        [SerializeField] private TMP_TextAdaptor playersText;

        [Header("Offline badge (spec §8.1) — shown when a connect attempt failed/dropped")]
        [SerializeField] private GameObject offlineBadge;

        [Header("Buttons (visibility derived from state)")]
        [SerializeField] private GameObject startRunButton;     // Lobby only
        [SerializeField] private Button endRunButton;           // Running only
        [SerializeField] private Button viewToggleButton;       // Lobby + Running

        [Header("Beacon")]
        [SerializeField] private Button beaconFormButton;       // Lobby + Running

        private float _updateTimer;

        private void OnEnable()
        {
            if (GameManager.HasInstance)
                GameManager.Instance.OnStateChanged += HandleStateChanged;
        }

        private void OnDisable()
        {
            if (GameManager.HasInstance)
                GameManager.Instance.OnStateChanged -= HandleStateChanged;
        }

        private void Start()
        {
            if (GameManager.HasInstance) HandleStateChanged(GameState.Initializing, GameManager.Instance.State);
            WireButtons();

            // TEMP DIAGNOSTIC: confirm button visibility at startup.
            if (startRunButton != null)
            {
                Debug.Log($"[HUDController] startRunButton: activeSelf={startRunButton.activeSelf} activeInHierarchy={startRunButton.activeInHierarchy} parent={startRunButton.transform.parent?.name}");
            }
        }

        private void WireButtons()
        {
            if (startRunButton != null)
            {
                var btn = startRunButton.GetComponentInChildren<Button>();
                if (btn != null) btn.onClick.AddListener(() => GameManager.Instance?.RequestStartRun());
            }
            if (endRunButton != null) endRunButton.onClick.AddListener(() => GameManager.Instance?.EndRun());
            if (viewToggleButton != null) viewToggleButton.onClick.AddListener(() => GameManager.Instance?.ToggleViewMode());
            if (beaconFormButton != null) beaconFormButton.onClick.AddListener(() => GameManager.Instance?.CycleBeaconForm());
        }

        private void HandleStateChanged(GameState prev, GameState next)
        {
            // The `next` arg can be stale when called from Start() in a race with GameManager —
            // re-read the live state to be safe.
            if (GameManager.HasInstance) next = GameManager.Instance.State;

            bool running = next == GameState.Running;
            bool lobby = next == GameState.Lobby;

            if (hudPanel != null) hudPanel.SetActive(running);
            if (startRunButton != null) startRunButton.SetActive(lobby);
            if (endRunButton != null) endRunButton.gameObject.SetActive(running);
            if (viewToggleButton != null) viewToggleButton.gameObject.SetActive(running || lobby);
            if (beaconFormButton != null) beaconFormButton.gameObject.SetActive(running || lobby);
            if (offlineBadge != null && !running) offlineBadge.SetActive(false);
        }

        private void Update()
        {
            _updateTimer -= Time.deltaTime;
            if (_updateTimer > 0f) return;
            _updateTimer = 0.25f;
            Refresh();
        }

        private void Refresh()
        {
            if (GameManager.Instance == null) return;
            if (GameManager.Instance.State != GameState.Running) return;
            if (!TrailManager.HasInstance || TrailManager.Instance.LocalTrail == null) return;

            var tm = TrailManager.Instance;
            double distance = tm.LocalTrail.TotalLength;
            double elapsed = tm.RunElapsedSeconds;
            double speed = CurrentSpeed(tm.LocalTrail);
            double altitude = LocationProvider.HasInstance ? LocationProvider.Instance.CurrentPosition.altitude : 0.0;
            int players = tm.LivePlayerCount;

            speedText?.SetText($"{speed:F2} m/s");
            altitudeText?.SetText($"{altitude:F0} m");
            timeText?.SetText(FormatTime(elapsed));
            distanceText?.SetText(FormatDistance(distance));
            playersText?.SetText(players.ToString());
            if (beaconFormButton != null)
            {
                var label = beaconFormButton.GetComponentInChildren<TMP_TextAdaptor>();
                label?.SetText(GameManager.Instance.CurrentForm.ToString());
            }

            // Offline badge (spec §8.1): only after an attempted connection failed/dropped —
            // never during pure-solo phases where no attempt was made.
            if (offlineBadge != null)
                offlineBadge.SetActive(GameManager.Instance.OnlineRace == false);
        }

        /// <summary>
        /// Current smoothed speed over the last 3 accepted fixes (spec §25) — not the run
        /// average. Discontinuity pairs contribute nothing.
        /// </summary>
        private static double CurrentSpeed(TrailData trail)
        {
            var pts = trail.Points;
            int n = pts.Count;
            if (n < 2) return 0.0;
            int first = Mathf.Max(0, n - 3);
            double dist = 0.0, dt = 0.0;
            for (int i = first + 1; i < n; i++)
            {
                if (pts[i].isSegmentStart) { dist = 0.0; dt = 0.0; continue; }
                dist += pts[i - 1].position.HorizontalDistanceTo(pts[i].position);
                dt += pts[i].timestamp - pts[i - 1].timestamp;
            }
            return dt > 0.0 ? dist / dt : 0.0;
        }

        /// <summary>Metres below 1 km, else km with two decimals (spec §25).</summary>
        private static string FormatDistance(double meters)
            => meters < 1000.0 ? $"{meters:F0} m" : $"{meters / 1000.0:F2} km";

        private static string FormatTime(double seconds)
        {
            int m = (int)(seconds / 60.0);
            int s = (int)(seconds % 60.0);
            return $"{m:00}:{s:00}";
        }
    }
}
