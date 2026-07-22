using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using LightRunners.Core;
using LightRunners.Gameplay;
using LightRunners.Trail;

namespace LightRunners.Gameplay.Tests
{
    /// <summary>
    /// Match FSM transition tests for <see cref="MatchManager"/> (decisions P, O, T).
    /// Verifies Idle→Warmup→Countdown→Live→Scoring→Expired transitions, that the tail authority
    /// freezes on entry to Countdown (decision T), that clock expiry fires MatchExpired
    /// (decision O), and that invalid transitions are rejected.
    /// </summary>
    [TestFixture]
    public class MatchFsmTests
    {
        private GameObject _host;
        private MatchManager _match;
        private TailAuthority _tail;
        private readonly List<MatchState> _transitions = new List<MatchState>();
        private readonly List<(MatchState, MatchState)> _busTransitions = new List<(MatchState, MatchState)>();
        private bool _expiredFired;
        private bool _tailWasFrozenOnBus;

        [SetUp]
        public void SetUp()
        {
            ServiceLocator.Clear();
            _host = new GameObject("MatchManagerHost");
            _match = _host.AddComponent<MatchManager>();
            // EditMode does not automatically invoke MonoBehaviour.Awake. Exercise the same
            // initialization path the Game scene uses before asserting locator registration.
            if (!ServiceLocator.IsRegistered<ITailAuthority>())
            {
                var awake = typeof(MatchManager).GetMethod(
                    "Awake",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.IsNotNull(awake);
                awake.Invoke(_match, null);
            }
            // MatchManager.Awake registers ILumenScoreboard + ITailAuthority; read the tail back.
            Assert.IsTrue(ServiceLocator.TryGet<ITailAuthority>(out var tail), "TailAuthority should be registered by MatchManager.Awake");
            _tail = tail as TailAuthority;
            Assert.IsNotNull(_tail, "MatchManager should register the concrete TailAuthority");
            _tail.Unfreeze();

            _transitions.Clear();
            _busTransitions.Clear();
            _expiredFired = false;
            _tailWasFrozenOnBus = false;

            _match.StateChanged += (prev, next) => _transitions.Add(next);
            GameEvents.MatchStateChanged += OnBusTransition;
            GameEvents.MatchExpired += OnExpired;
        }

        [TearDown]
        public void TearDown()
        {
            GameEvents.MatchStateChanged -= OnBusTransition;
            GameEvents.MatchExpired -= OnExpired;
            if (_host != null) Object.DestroyImmediate(_host);
            ServiceLocator.Clear();
        }

        private void OnBusTransition(MatchState prev, MatchState next) => _busTransitions.Add((prev, next));
        private void OnExpired() => _expiredFired = true;

        [Test]
        public void BeginMatch_IdleAdvancesThroughWarmupToCountdown()
        {
            Assert.AreEqual(MatchState.Idle, _match.State);

            _match.BeginMatch();

            // BeginMatch should land in Countdown (Warmup→Countdown auto-advance for the milestone).
            Assert.AreEqual(MatchState.Countdown, _match.State, "BeginMatch should land in Countdown");
            CollectionAssert.Contains(_transitions, MatchState.Warmup);
            CollectionAssert.Contains(_transitions, MatchState.Countdown);
            // The bus mirrors the typed event.
            Assert.GreaterOrEqual(_busTransitions.Count, 2, "Bus should have received Warmup + Countdown");
        }

        [Test]
        public void Countdown_FreezesTailAuthority_DecisionT()
        {
            // Drive to Countdown explicitly so the freeze is unambiguous.
            _match.TestSetState(MatchState.Warmup);
            Assert.IsFalse(_tail.IsFrozen, "Tail must be unfrozen in Warmup");

            _match.TestFreezeTail();   // mirrors what TransitionTo(Countdown) does
            Assert.IsTrue(_tail.IsFrozen, "Tail must freeze at Countdown (decision T)");
            Assert.Greater(_tail.FrozenTailRadius, 0f);
        }

        [Test]
        public void BeginMatch_CountdownFreezesTailAuthority()
        {
            Assert.IsFalse(_tail.IsFrozen);
            _match.BeginMatch();
            Assert.AreEqual(MatchState.Countdown, _match.State);
            Assert.IsTrue(_tail.IsFrozen, "Tail should freeze as part of entering Countdown");
        }

        [Test]
        public void BeginMatch_InvalidTailConfig_StaysInWarmup()
        {
            float original = GameConfig.Active.tailRadius;
            try
            {
                GameConfig.Active.tailRadius = 1.75f;
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Cannot begin match:.*Tail radius"));

                _match.BeginMatch();

                Assert.AreEqual(MatchState.Idle, _match.State, "invalid config remains retryable");
                Assert.IsFalse(_tail.IsFrozen);
            }
            finally
            {
                GameConfig.Active.tailRadius = original;
            }
        }

        [Test]
        public void Live_GoesToExpired_OnClockExpiry_FiresMatchExpired_DecisionO()
        {
            _match.TestBeginMatchAtLive();
            Assert.AreEqual(MatchState.Live, _match.State);
            Assert.Greater(_match.TimeRemaining, 0f);

            _match.TestExpireMatch();

            Assert.AreEqual(MatchState.Expired, _match.State, "Expiry should land in Expired (decision O)");
            Assert.IsTrue(_expiredFired, "GameEvents.MatchExpired should fire on expiry");
            Assert.LessOrEqual(_match.TimeRemaining, 0f);
        }

        [Test]
        public void Validator_RejectsInvalidTransitions()
        {
            // Use reflection to invoke the private ValidateTransition for explicit table coverage.
            // The validator is the source of truth for the FSM layering.
            Assert.IsTrue(InvokeValidator(MatchState.Idle, MatchState.Warmup),      "Idle→Warmup valid");
            Assert.IsTrue(InvokeValidator(MatchState.Warmup, MatchState.Countdown), "Warmup→Countdown valid");
            Assert.IsTrue(InvokeValidator(MatchState.Countdown, MatchState.Live),   "Countdown→Live valid");
            Assert.IsTrue(InvokeValidator(MatchState.Live, MatchState.Scoring),     "Live→Scoring valid");
            Assert.IsTrue(InvokeValidator(MatchState.Scoring, MatchState.Expired),  "Scoring→Expired valid");

            Assert.IsFalse(InvokeValidator(MatchState.Idle, MatchState.Live),       "Idle→Live invalid (must go via Warmup/Countdown)");
            Assert.IsFalse(InvokeValidator(MatchState.Live, MatchState.Warmup),     "Live→Warmup invalid (no backward to Warmup)");
            Assert.IsFalse(InvokeValidator(MatchState.Warmup, MatchState.Live),     "Warmup→Live invalid (must freeze tail first)");
            Assert.IsFalse(InvokeValidator(MatchState.Live, MatchState.Live),       "Same-state invalid");
        }

        [Test]
        public void EndMatch_VoluntaryEnd_DrivesToExpired()
        {
            _match.TestBeginMatchAtLive();
            _match.EndMatch();
            Assert.AreEqual(MatchState.Expired, _match.State, "EndMatch should drive to Expired");
            Assert.IsTrue(_expiredFired, "EndMatch should fire MatchExpired");
        }

        [Test]
        public void HandleCrash_OutsideLive_IsNoOp()
        {
            // Pre-Live (e.g. Countdown): a crash event must not apply penalties.
            _match.BeginMatch();
            Assert.AreEqual(MatchState.Countdown, _match.State);
            int before = _match.Scoreboard.GetLumens("p1");
            _match.HandlePlayerCrash("p1", default);
            int after = _match.Scoreboard.GetLumens("p1");
            Assert.AreEqual(before, after, "Crash outside Live should be a no-op on the tally");
        }

        private static bool InvokeValidator(MatchState from, MatchState to)
        {
            var method = typeof(MatchManager).GetMethod("ValidateTransition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "ValidateTransition must exist on MatchManager");
            return (bool)method.Invoke(null, new object[] { from, to });
        }
    }
}
