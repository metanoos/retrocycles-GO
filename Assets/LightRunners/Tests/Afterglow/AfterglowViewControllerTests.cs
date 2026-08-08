using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using LightRunners.Core;
using LightRunners.Afterglow;

namespace LightRunners.Afterglow.Tests
{
    /// <summary>
    /// Decision U: <see cref="AfterglowViewController"/> must preserve
    /// <see cref="AfterglowViewController.SelectedPlayerIds"/> and
    /// <see cref="AfterglowViewController.FocusedPlayerId"/> across Overview↔Walk-Inside
    /// switches. Uses a stub <see cref="ReplayPackage"/>; no scene is required because the
    /// selection/focus state is pure-C# (the MonoBehaviour bodies that touch Camera/GameObject
    /// are guarded with null views).
    /// </summary>
    public class AfterglowViewControllerTests
    {
        private GameObject _host;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("AfterglowVC_Host", typeof(AfterglowViewController));
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }

        private static ReplayPackage StubPackage()
        {
            var pkg = new ReplayPackage("m-stub", DateTime.UtcNow, default);
            pkg.SetOrigin(new GeoPoint(37.7749, -122.4194, 5.0));
            pkg.SetFrozenTailRadius(0.5f);
            // A single 3-point trail so ShowOverview has something to frame.
            pkg.AddTrail(new TrailCapture(
                "p1",
                new double[] { 37.7749, -122.4194, 5.0,
                               37.7750, -122.4194, 5.0,
                               37.7751, -122.4194, 5.0 },
                3));
            pkg.AddLumen(new LumenEvent("p1", new GeoPoint(37.7750, -122.4194, 5.0), 1.0));
            pkg.Freeze();
            return pkg;
        }

        private AfterglowViewController NewController()
        {
            var ctrl = _host.GetComponent<AfterglowViewController>();
            // Replace the stub so we can assert it was invoked without polluting logs (it
            // still logs a warning per decision S — that's expected and correct).
            ctrl.WalkInsideStub = new WalkInsideStub();
            return ctrl;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Selection / focus preservation across view switches (decision U)
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Switch_Overview_WalkInside_Overview_PreservesSelectionAndFocus()
        {
            var ctrl = NewController();
            var pkg = StubPackage();

            // Set selection + focus while in Overview.
            ctrl.ShowOverview(pkg);
            ctrl.SelectPlayer("p1", true);
            ctrl.SelectPlayer("p2", true);
            ctrl.FocusPlayer("p2");

            CollectionAssert.AreEquivalent(new[] { "p1", "p2" }, ctrl.SelectedPlayerIds);
            Assert.AreEqual("p2", ctrl.FocusedPlayerId);
            Assert.AreEqual(AfterglowView.Overview, ctrl.CurrentView);

            // Switch to Walk-Inside (stubbed — no-op render, decision S) and back.
            ctrl.ShowWalkInside(pkg);
            Assert.AreEqual(AfterglowView.WalkInside, ctrl.CurrentView,
                "switch must update CurrentView");
            CollectionAssert.AreEquivalent(new[] { "p1", "p2" }, ctrl.SelectedPlayerIds,
                "selection must survive Overview→Walk-Inside (decision U)");
            Assert.AreEqual("p2", ctrl.FocusedPlayerId,
                "focus must survive Overview→Walk-Inside (decision U)");

            ctrl.ShowOverview(pkg);
            Assert.AreEqual(AfterglowView.Overview, ctrl.CurrentView);
            CollectionAssert.AreEquivalent(new[] { "p1", "p2" }, ctrl.SelectedPlayerIds,
                "selection must survive Walk-Inside→Overview (decision U)");
            Assert.AreEqual("p2", ctrl.FocusedPlayerId,
                "focus must survive Walk-Inside→Overview (decision U)");
        }

        [Test]
        public void EnsureRuntimeInstance_ActivatesInactiveRootAndBuildsOverview()
        {
            _host.SetActive(false);

            var ctrl = AfterglowViewController.EnsureRuntimeInstance();

            Assert.AreSame(_host.GetComponent<AfterglowViewController>(), ctrl);
            Assert.IsTrue(_host.activeSelf, "expiry must revive a generated inactive root");
            var overview = _host.GetComponentInChildren<OverviewCameraController>(true);
            Assert.IsNotNull(overview, "older scenes must receive the zero-art Overview stack");

            var empty = new ReplayPackage("runtime-overview", DateTime.UtcNow, default);
            empty.Freeze();
            ctrl.ShowOverview(empty);
            Assert.IsTrue(overview.gameObject.activeSelf);
            Assert.IsTrue(overview.IsShown);
        }

        [Test]
        public void SelectPlayer_TogglesMembership()
        {
            var ctrl = NewController();
            ctrl.ShowOverview(StubPackage());

            ctrl.SelectPlayer("p1", true);
            Assert.Contains("p1", new List<string>(ctrl.SelectedPlayerIds));

            ctrl.SelectPlayer("p1", false);
            CollectionAssert.DoesNotContain(ctrl.SelectedPlayerIds, "p1");
        }

        [Test]
        public void ClearSelection_LeavesFocusIntact()
        {
            var ctrl = NewController();
            ctrl.ShowOverview(StubPackage());
            ctrl.SelectPlayer("p1", true);
            ctrl.SelectPlayer("p2", true);
            ctrl.FocusPlayer("p1");

            ctrl.ClearSelection();

            Assert.AreEqual(0, ctrl.SelectedPlayerIds.Count);
            Assert.AreEqual("p1", ctrl.FocusedPlayerId,
                "ClearSelection must NOT clear focus (decision U — focus is separate state)");
        }

        [Test]
        public void Reset_Clears_All_State()
        {
            var ctrl = NewController();
            ctrl.ShowOverview(StubPackage());
            ctrl.SelectPlayer("p1", true);
            ctrl.FocusPlayer("p1");

            ctrl.Reset();

            Assert.AreEqual(AfterglowView.None, ctrl.CurrentView);
            Assert.AreEqual(0, ctrl.SelectedPlayerIds.Count);
            Assert.AreEqual(string.Empty, ctrl.FocusedPlayerId);
            Assert.IsNull(ctrl.CurrentPackage);
        }

        [Test]
        public void ShowOverview_WithNull_Packages_LogsAndIgnores()
        {
            var ctrl = NewController();
            LogAssert.Expect(LogType.Warning, "[AfterglowViewController] ShowOverview(null) — ignored.");
            ctrl.ShowOverview(null);
            Assert.AreEqual(AfterglowView.None, ctrl.CurrentView,
                "null package must not switch the view");
        }

        [Test]
        public void ShowWalkInside_WithNull_Packages_LogsAndIgnores()
        {
            var ctrl = NewController();
            LogAssert.Expect(LogType.Warning, "[AfterglowViewController] ShowWalkInside(null) — ignored.");
            ctrl.ShowWalkInside(null);
            Assert.AreEqual(AfterglowView.None, ctrl.CurrentView);
        }

        [Test]
        public void ShowWalkInside_LogsDecisionS_LockedMessage()
        {
            var ctrl = NewController();
            LogAssert.Expect(LogType.Warning,
                $"[WalkInsideStub] {WalkInsideStub.LockedMessage}");
            ctrl.ShowWalkInside(StubPackage());
            Assert.AreEqual(AfterglowView.WalkInside, ctrl.CurrentView);
        }

        [Test]
        public void Hide_LeavesSelectionAndFocusIntact()
        {
            var ctrl = NewController();
            ctrl.ShowOverview(StubPackage());
            ctrl.SelectPlayer("p1", true);
            ctrl.FocusPlayer("p1");

            ctrl.Hide();

            Assert.AreEqual(AfterglowView.None, ctrl.CurrentView);
            CollectionAssert.AreEquivalent(new[] { "p1" }, ctrl.SelectedPlayerIds,
                "Hide must NOT clear selection (decision U — restored on next Show)");
            Assert.AreEqual("p1", ctrl.FocusedPlayerId);
        }

        [Test]
        public void FocusPlayer_EmptyString_ClearsFocus()
        {
            var ctrl = NewController();
            ctrl.ShowOverview(StubPackage());
            ctrl.FocusPlayer("p2");
            Assert.AreEqual("p2", ctrl.FocusedPlayerId);

            ctrl.FocusPlayer(string.Empty);
            Assert.AreEqual(string.Empty, ctrl.FocusedPlayerId);
        }
    }
}
