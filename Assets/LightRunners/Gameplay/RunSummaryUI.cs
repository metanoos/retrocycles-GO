using UnityEngine;
using UnityEngine.UI;
using LightRunners.Core;
using LightRunners.Trail;
using LightRunners.Backend;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// The single end-of-run panel, used for both crash and voluntary end (spec §2.2, §12.4,
    /// pitfall #9 — v1 didn't put it in any scene). Computes the score via <see cref="RunScorer"/>,
    /// populates the panel (formats pinned in §25), and POSTs the <c>record_run</c> RPC.
    /// </summary>
    public class RunSummaryUI : Singleton<RunSummaryUI>
    {
        [Header("Panel")]
        [SerializeField] private GameObject summaryPanel;

        [Header("Text fields")]
        [SerializeField] private TMP_TextAdaptor summaryCrashText;
        [SerializeField] private TMP_TextAdaptor totalScoreText;
        [SerializeField] private TMP_TextAdaptor distanceText;
        [SerializeField] private TMP_TextAdaptor timeText;
        [SerializeField] private TMP_TextAdaptor avgSpeedText;
        [SerializeField] private TMP_TextAdaptor distanceScoreText;
        [SerializeField] private TMP_TextAdaptor speedScoreText;
        [SerializeField] private TMP_TextAdaptor beautyScoreText;
        [SerializeField] private TMP_TextAdaptor proximityScoreText;

        [Header("Buttons")]
        [SerializeField] private Button runAgainButton;
        [SerializeField] private Button continueButton;

        protected override void Awake()
        {
            base.Awake();
            Hide();
        }

        private void OnEnable()
        {
            if (runAgainButton != null) runAgainButton.onClick.AddListener(OnRunAgain);
            if (continueButton != null) continueButton.onClick.AddListener(OnContinue);
        }

        private void OnDisable()
        {
            if (runAgainButton != null) runAgainButton.onClick.RemoveListener(OnRunAgain);
            if (continueButton != null) continueButton.onClick.RemoveListener(OnContinue);
        }

        public void ShowSummary(TrailData trail, double durationSeconds, int otherPlayersNearby, bool crashed, string causedByPlayerId)
        {
            if (trail == null) { Hide(); return; }

            // TODO(lumen-scoreboard): Track D — replace RunScorer/ScoreBreakdown with
            // ILumenScoreboard (Lumens, rank, leader) per active decisions E/F/O.
            ScoreBreakdown score = RunScorer.Calculate(trail, durationSeconds, otherPlayersNearby);

            double distance = trail.TotalLength;
            double avgSpeed = durationSeconds > 0 ? distance / durationSeconds : 0.0;

            if (summaryPanel != null) summaryPanel.SetActive(true);

            summaryCrashText?.SetText(CrashCauseText(trail, crashed, causedByPlayerId));

            totalScoreText?.SetText(score.total.ToString());
            distanceText?.SetText($"{distance:F0} m");
            timeText?.SetText(FormatTime(durationSeconds));
            avgSpeedText?.SetText($"{avgSpeed:F2} m/s");
            distanceScoreText?.SetText($"{score.distance} / 40");
            speedScoreText?.SetText($"{score.speed} / 20");
            beautyScoreText?.SetText($"{score.beauty} / 30");
            proximityScoreText?.SetText($"{score.proximity} / 10");

            // Haptics (spec §24): a crash buzzes, nothing else does.
            if (crashed)
            {
#if UNITY_IOS || UNITY_ANDROID
                Handheld.Vibrate();
#endif
            }

            // Persist the run (spec §12.4). Queue-and-retry on failure is inside the repo (§21).
            if (PlayerRepository.HasInstance)
                PlayerRepository.Instance.RecordRun(trail, durationSeconds, score, crashed);
        }

        /// <summary>Crash cause phrasing pinned in §25.</summary>
        private static string CrashCauseText(TrailData trail, bool crashed, string causedByPlayerId)
        {
            if (!crashed) return "Run complete";
            if (string.IsNullOrEmpty(causedByPlayerId) || causedByPlayerId == trail.OwnerId)
                return "You crossed your own trail";
            return $"You hit {StringUtils.RunnerDisplayName(causedByPlayerId)}'s trail";
        }

        public void Hide()
        {
            if (summaryPanel != null) summaryPanel.SetActive(false);
        }

        private void OnRunAgain()
        {
            Hide();
            if (GameManager.HasInstance)
            {
                GameManager.Instance.SetState(GameState.Lobby);
                GameManager.Instance.StartRun();
            }
        }

        private void OnContinue()
        {
            Hide();
            if (GameManager.HasInstance) GameManager.Instance.SetState(GameState.Lobby);
        }

        private static string FormatTime(double seconds)
        {
            int m = (int)(seconds / 60.0);
            int s = (int)(seconds % 60.0);
            return $"{m:00}:{s:00}";
        }
    }
}
