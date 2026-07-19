using System;
using UnityEngine;
using UnityEngine.UI;
using LightRunners.Core;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// One-time physical-safety notice (spec §23): shown before the first-ever Start Run,
    /// acknowledged once, stored in PlayerPrefs. Covers both map and AR play.
    /// </summary>
    public class SafetyDisclaimerUI : Singleton<SafetyDisclaimerUI>
    {
        private const string PrefKey = "safety_ack";

        [SerializeField] private GameObject panel;
        [SerializeField] private Button acknowledgeButton;

        private Action _onAcknowledged;

        /// <summary>True once the player has acknowledged the notice (persisted).</summary>
        public static bool Acknowledged => PlayerPrefs.GetInt(PrefKey, 0) == 1;

        protected override void Awake()
        {
            base.Awake();
            if (panel != null) panel.SetActive(false);
        }

        private void OnEnable()
        {
            if (acknowledgeButton != null) acknowledgeButton.onClick.AddListener(OnAcknowledge);
        }

        private void OnDisable()
        {
            if (acknowledgeButton != null) acknowledgeButton.onClick.RemoveListener(OnAcknowledge);
        }

        public void Show(Action onAcknowledged)
        {
            _onAcknowledged = onAcknowledged;
            if (panel != null) panel.SetActive(true);
            else OnAcknowledge(); // no panel wired — never block the game on missing UI
        }

        private void OnAcknowledge()
        {
            PlayerPrefs.SetInt(PrefKey, 1);
            PlayerPrefs.Save();
            if (panel != null) panel.SetActive(false);
            var cb = _onAcknowledged;
            _onAcknowledged = null;
            cb?.Invoke();
        }
    }
}
