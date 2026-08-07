using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using LightRunners.Core;
using LightRunners.Trail;
using LightRunners.Backend;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// The single end-of-match panel, used for both crash-expired and voluntary-end (spec §2.2,
    /// §12.4, pitfall #9 — v1 didn't put it in any scene). Decision E/F: this panel now shows
    /// the integer Lumen tally, the player's finish rank, and the leader's name — replacing the
    /// deprecated 4-axis (distance/speed/beauty/proximity) float score from <c>RunScorer</c>.
    ///
    /// ─── TRACK D CHANGES (Lightfield match migration, 2026-07-18) ──────────
    ///   • Compile-break fix: <see cref="PlayerRepository.RecordRun"/> now takes the Lumen tally
    ///     (<c>int lumens</c>) instead of the deprecated <c>ScoreBreakdown</c>. This call site
    ///     resolves <see cref="ILumenScoreboard"/> from the locator and passes the local player's
    ///     final tally (Track E's new signature).
    ///   • UI change: removed the four 4-axis score columns; the panel now shows Lumens, finish
    ///     rank, and the leader's display name (decisions E/F/O).
    /// </summary>
    public class RunSummaryUI : Singleton<RunSummaryUI>
    {
        [Header("Panel")]
        [SerializeField] private GameObject summaryPanel;

        [Header("Lightfield Match — Lumens / rank / leader (decisions E, F, O)")]
        [SerializeField] private TMP_TextAdaptor summaryCrashText;
        [SerializeField] private TMP_TextAdaptor lumensText;
        [SerializeField] private TMP_TextAdaptor rankText;
        [SerializeField] private TMP_TextAdaptor leaderText;
        [SerializeField] private TMP_TextAdaptor distanceText;
        [SerializeField] private TMP_TextAdaptor timeText;

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

        /// <summary>
        /// Show the post-match summary. <paramref name="otherPlayersNearby"/> is retained for
        /// API stability but unused (the 4-axis proximity score is gone — decision E).
        /// </summary>
        public void ShowSummary(TrailData trail, double durationSeconds, int otherPlayersNearby, bool crashed, string causedByPlayerId)
        {
            if (trail == null) { Hide(); return; }

            // Decision E/F: pull the Lumen tally + finish rank + leader from the scoreboard.
            // MatchManager registers the real LumenScoreboard (Track A) on Awake; if it isn't
            // present (e.g. an editor scene without the match core), fall back to 0 / unranked.
            string localPlayerId = trail.OwnerId;
            int lumens = 0;
            int rank = 0;
            string leaderId = string.Empty;
            if (ServiceLocator.TryGet<ILumenScoreboard>(out var scoreboard) && scoreboard != null)
            {
                lumens = scoreboard.GetLumens(localPlayerId);
                leaderId = scoreboard.LeaderPlayerId ?? string.Empty;
                rank = ComputeRank(scoreboard, localPlayerId);
            }

            double distance = trail.TotalLength;

            if (summaryPanel != null) summaryPanel.SetActive(true);

            summaryCrashText?.SetText(CrashCauseText(trail, crashed, causedByPlayerId));
            lumensText?.SetText(lumens.ToString());
            rankText?.SetText(rank > 0 ? Ordinal(rank) : "—");
            leaderText?.SetText(LeaderDisplayName(leaderId, localPlayerId));
            distanceText?.SetText($"{distance:F0} m");
            timeText?.SetText(FormatTime(durationSeconds));

            // Haptics (spec §24): a crash buzzes, nothing else does.
            if (crashed)
            {
#if UNITY_IOS || UNITY_ANDROID
                Handheld.Vibrate();
#endif
            }

            // Persist the run (spec §12.4). Track E's new RecordRun signature takes the integer
            // Lumen tally instead of the deprecated ScoreBreakdown. Queue-and-retry on failure
            // is inside the repo (§21).
            if (PlayerRepository.HasInstance)
                PlayerRepository.Instance.RecordRun(trail, durationSeconds, lumens, crashed);
        }

        /// <summary>
        /// Compute the local player's 1-based finish rank (1 = most Lumens). Ties get the same
        /// rank (standard competition ranking). Returns 0 if the player is unknown to the
        /// scoreboard.
        /// </summary>
        private static int ComputeRank(ILumenScoreboard scoreboard, string localPlayerId)
        {
            if (scoreboard == null || string.IsNullOrEmpty(localPlayerId)) return 0;
            // Use the OrderedStandings roster (Phase 0.5 widening) for true finish order.
            // Standard competition ranking: players tied on Lumens share a rank equal to
            // 1 + the count of strictly-higher scorers.
            int? myLumens = null;
            foreach ((string pid, int lumens) in scoreboard.OrderedStandings)
            {
                if (pid == localPlayerId) { myLumens = lumens; break; }
            }
            if (!myLumens.HasValue) return 0; // player not on the board
            int rank = 1;
            foreach ((string pid, int lumens) in scoreboard.OrderedStandings)
            {
                if (pid == localPlayerId) break;
                if (lumens > myLumens.Value) rank++;
            }
            return rank;
        }

        private static string LeaderDisplayName(string leaderId, string localPlayerId)
        {
            if (string.IsNullOrEmpty(leaderId)) return "Tied";
            if (leaderId == localPlayerId) return "You";
            return StringUtils.RunnerDisplayName(leaderId);
        }

        private static string Ordinal(int n)
        {
            int m = n % 100;
            if (m >= 11 && m <= 13) return n + "th";
            switch (n % 10)
            {
                case 1: return n + "st";
                case 2: return n + "nd";
                case 3: return n + "rd";
                default: return n + "th";
            }
        }

        /// <summary>Crash cause phrasing pinned in §25.</summary>
        private static string CrashCauseText(TrailData trail, bool crashed, string causedByPlayerId)
        {
            if (!crashed) return "Match complete";
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
            LightRunners.Afterglow.AfterglowViewController.ResetRuntimeInstance();
            if (GameManager.HasInstance)
            {
                GameManager.Instance.SetState(GameState.Lobby);
                GameManager.Instance.StartRun();
            }
        }

        private void OnContinue()
        {
            Hide();
            LightRunners.Afterglow.AfterglowViewController.ResetRuntimeInstance();
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
