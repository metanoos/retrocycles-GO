using NUnit.Framework;
using UnityEngine;
using LightRunners.UI;

namespace LightRunners.Gameplay.Tests
{
    /// <summary>
    /// Expand-on-stop tests for <see cref="TacticalRadar"/> (decision H). The radar should
    /// EXPAND when the player has been stopped for longer than the configured threshold, and
    /// CONTRACT immediately when movement resumes.
    /// </summary>
    ///
    /// These tests exercise the radar's pure expand/contract logic via the internal test hooks
    /// (no Canvas required). The full position-driven path is integration-tested in playmode.
    [TestFixture]
    public class TacticalRadarExpandTests
    {
        private GameObject _host;
        private TacticalRadar _radar;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("TacticalRadarHost");
            _radar = _host.AddComponent<TacticalRadar>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
        }

        [Test]
        public void Radar_ContractedByDefault()
        {
            Assert.IsFalse(_radar.IsExpanded_Internal, "Radar should start contracted");
            Assert.AreEqual(0f, _radar.StoppedTimer_Internal, "Stopped timer should start at zero");
        }

        [Test]
        public void Radar_ExpandsWhenStoppedBeyondThreshold()
        {
            // Simulate 1s of stopped time (below the default 1.5s threshold).
            _radar.TestSimulateStopped(1f);
            Assert.IsFalse(_radar.IsExpanded_Internal, "Below threshold — should still be contracted");

            // Cross the threshold.
            _radar.TestSimulateStopped(1f);
            Assert.IsTrue(_radar.IsExpanded_Internal, "Past threshold — should be expanded");
        }

        [Test]
        public void Radar_ContractsOnMovement()
        {
            _radar.TestSimulateStopped(5f);
            Assert.IsTrue(_radar.IsExpanded_Internal, "Should be expanded after long stop");

            _radar.TestSimulateMoved();
            Assert.IsFalse(_radar.IsExpanded_Internal, "Movement should contract the radar");
            Assert.AreEqual(0f, _radar.StoppedTimer_Internal, "Stopped timer should reset on movement");
        }

        [Test]
        public void Radar_MovementDuringCountup_PreservesContractionUntilThreshold()
        {
            // Accumulate, move, accumulate again — only crossing the threshold after the move
            // should re-expand.
            _radar.TestSimulateStopped(1f);
            _radar.TestSimulateMoved();
            _radar.TestSimulateStopped(1f);
            Assert.IsFalse(_radar.IsExpanded_Internal, "Below threshold after a move — contracted");

            _radar.TestSimulateStopped(1f);
            Assert.IsTrue(_radar.IsExpanded_Internal, "Crossed threshold — expanded");
        }
    }
}
