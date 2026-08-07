using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Afterglow
{
    // ─── Track F: Afterglow View Controller ──────────────────────────────────
    // Decision U: two interchangeable views of ONE replay package. Switching preserves
    // the user's selection and focus across Overview ↔ Walk-Inside. Decision S: Walk-Inside
    // is a stub for the ground-only milestone.

    /// <summary>
    /// Active Afterglow view. Decision U.
    /// </summary>
    public enum AfterglowView
    {
        /// <summary>No package shown yet.</summary>
        None,
        /// <summary>Top-down orthographic overview (ground milestone, decision S).</summary>
        Overview,
        /// <summary>Full-screen AR walk-inside (stubbed — unlocks after aerial milestone).</summary>
        WalkInside,
    }

    /// <summary>
    /// Owns the Afterglow Overview camera and the Walk-Inside stub (decisions A, U, S).
    /// Switching between <see cref="AfterglowView.Overview"/> and
    /// <see cref="AfterglowView.WalkInside"/> is a no-op swap that PRESERVES
    /// <see cref="SelectedPlayerIds"/> and <see cref="FocusedPlayerId"/> (decision U) —
    /// the same package is read by both, so the user's selection and focus carry across.
    ///
    /// Cross-fade style mirrors <see cref="LightRunners.AR.ARViewManager"/>: enable/disable
    /// the relevant GameObject stack. For the milestone we use a CUT (no fade animation);
    /// Track G can add a fade later by replacing <see cref="ApplyView"/> with a coroutine.
    /// </summary>
    public class AfterglowViewController : MonoBehaviour
    {
        [Header("Views (wired by the scene generator)")]
        [Tooltip("GameObject carrying the OverviewCameraController. Toggled on/off.")]
        [SerializeField] private GameObject overviewView;

        [Tooltip("GameObject carrying the (future) Walk-Inside camera. Toggled on/off. May be null at milestone.")]
        [SerializeField] private GameObject walkInsideView;

        // Decision U — selection/focus preserved across switches.
        private readonly HashSet<string> _selectedPlayerIds = new HashSet<string>();
        private string _focusedPlayerId = string.Empty;

        private AfterglowView _currentView = AfterglowView.None;
        private ReplayPackage _currentPackage;

        /// <summary>The currently-shown package, or null if none.</summary>
        public ReplayPackage CurrentPackage => _currentPackage;

        /// <summary>The active Afterglow view (decision U).</summary>
        public AfterglowView CurrentView => _currentView;

        /// <summary>
        /// Selected player ids. PRESERVED across Overview↔Walk-Inside switches (decision U).
        /// Returned as a copy so external code can't mutate the internal set.
        /// </summary>
        public IReadOnlyCollection<string> SelectedPlayerIds
        {
            get
            {
                // Snapshot for stable iteration semantics; callers can LINQ over it.
                var arr = new string[_selectedPlayerIds.Count];
                _selectedPlayerIds.CopyTo(arr);
                return arr;
            }
        }

        /// <summary>
        /// Focused player id (empty if none). PRESERVED across switches (decision U).
        /// </summary>
        public string FocusedPlayerId => _focusedPlayerId ?? string.Empty;

        /// <summary>True when at least one view has been shown for a package.</summary>
        public bool IsAnyViewActive => _currentView != AfterglowView.None;

        /// <summary>Backing stub used when switching to Walk-Inside at milestone (decision S).</summary>
        public WalkInsideStub WalkInsideStub { get; set; } = new WalkInsideStub();

        /// <summary>
        /// Resolve the scene-authored controller, including an inactive one, or build the
        /// zero-art Overview stack at runtime. Older checked-in scenes predate Afterglow, and
        /// generated scenes historically disabled the controller's whole root, so expiry must
        /// not depend on active-only scene lookup.
        /// </summary>
        public static AfterglowViewController EnsureRuntimeInstance()
        {
            var controller = FindAnyObjectByType<AfterglowViewController>(FindObjectsInactive.Include);
            if (controller == null)
            {
                var root = new GameObject("Afterglow");
                controller = root.AddComponent<AfterglowViewController>();
            }

            if (!controller.gameObject.activeSelf)
                controller.gameObject.SetActive(true);
            controller.EnsureOverviewView();
            controller.ApplyView();
            return controller;
        }

        /// <summary>
        /// Hide and clear the current replay if a runtime controller exists. Does not create a
        /// stack. Continue/Run Again and defensive new-run entry use this lifecycle boundary so
        /// the Overview camera cannot leak into Lobby or the next match.
        /// </summary>
        public static void ResetRuntimeInstance()
        {
            var controller = FindAnyObjectByType<AfterglowViewController>(FindObjectsInactive.Include);
            if (controller != null) controller.Reset();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Selection / focus (decision U — preserved across switches)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Add or remove a player from the multi-select. Decision U: selection is shared by
        /// Overview and Walk-Inside, so this survives a switch.
        /// </summary>
        public void SelectPlayer(string playerId, bool selected)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            if (selected) _selectedPlayerIds.Add(playerId);
            else _selectedPlayerIds.Remove(playerId);
        }

        /// <summary>
        /// Clear the multi-select.
        /// </summary>
        public void ClearSelection() => _selectedPlayerIds.Clear();

        /// <summary>
        /// Set the single focused player. Empty string clears focus. Decision U: focus is
        /// shared by Overview and Walk-Inside, so this survives a switch.
        /// </summary>
        public void FocusPlayer(string playerId)
        {
            _focusedPlayerId = playerId ?? string.Empty;
        }

        // ─────────────────────────────────────────────────────────────────────
        // View switching (decision U)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Show the Overview for <paramref name="package"/>. If a package was already
        /// showing, the selection/focus carry over (decision U). Idempotent if the same view
        /// is requested; safe to call with null (logs and ignores).
        /// </summary>
        public void ShowOverview(ReplayPackage package)
        {
            if (package == null)
            {
                Debug.LogWarning("[AfterglowViewController] ShowOverview(null) — ignored.");
                return;
            }
            _currentPackage = package;
            SwitchTo(AfterglowView.Overview);
        }

        /// <summary>
        /// Show the Walk-Inside for <paramref name="package"/>. Decision S: at the ground
        /// milestone this delegates to <see cref="WalkInsideStub"/>, which logs the locked
        /// message and renders nothing. Selection/focus are still recorded so they carry
        /// into the real Walk-Inside when it unlocks.
        /// </summary>
        public void ShowWalkInside(ReplayPackage package)
        {
            if (package == null)
            {
                Debug.LogWarning("[AfterglowViewController] ShowWalkInside(null) — ignored.");
                return;
            }
            _currentPackage = package;
            SwitchTo(AfterglowView.WalkInside);
        }

        /// <summary>
        /// Hide whatever is showing. Does NOT clear selection/focus (a subsequent Show*
        /// restores them over the new package).
        /// </summary>
        public void Hide()
        {
            SwitchTo(AfterglowView.None);
        }

        /// <summary>
        /// Reset selection, focus, and the active view. Used on match-leave.
        /// </summary>
        public void Reset()
        {
            _selectedPlayerIds.Clear();
            _focusedPlayerId = string.Empty;
            _currentPackage = null;
            SwitchTo(AfterglowView.None);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────────────

        private void EnsureOverviewView()
        {
            if (overviewView != null) return;

            overviewView = new GameObject("OverviewCamera");
            overviewView.transform.SetParent(transform, false);
            var camera = overviewView.AddComponent<Camera>();
            camera.orthographic = true;
            camera.enabled = false;
            overviewView.AddComponent<OverviewCameraController>();
            overviewView.SetActive(false);
        }

        private void SwitchTo(AfterglowView next)
        {
            if (next == _currentView && _currentView != AfterglowView.None) return;
            _currentView = next;
            ApplyView();
        }

        private void ApplyView()
        {
            // Mirrors ARViewManager.SetStackEnabled: enable the relevant GameObject stack.
            // Milestone uses a cut; a future fade would wrap these toggles in a coroutine.
            bool showOverview = _currentView == AfterglowView.Overview;
            bool showWalkInside = _currentView == AfterglowView.WalkInside;

            if (overviewView != null)
            {
                overviewView.SetActive(showOverview);
                var ctrl = overviewView.GetComponent<OverviewCameraController>();
                if (ctrl != null)
                {
                    if (showOverview && _currentPackage != null) ctrl.Show(_currentPackage);
                    else if (!showOverview) ctrl.Hide();
                }
            }

            if (walkInsideView != null)
            {
                walkInsideView.SetActive(showWalkInside);
            }

            if (showWalkInside)
            {
                // Decision S — log + no-op at milestone.
                WalkInsideStub?.Show(_currentPackage);
            }
        }
    }
}
