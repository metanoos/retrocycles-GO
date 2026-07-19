using System.Collections.Generic;
using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Gameplay;
using LightRunners.Trail;

namespace LightRunners.Gameplay.Tests
{
    /// <summary>
    /// Leaderboard integration tests (decisions E, F, I). Verifies that awarding a Lumen via the
    /// registered <see cref="ILumenScoreboard"/> (Track A's <see cref="LumenScoreboard"/>) raises
    /// <see cref="GameEvents.LumensChanged"/> + <see cref="GameEvents.LeaderChanged"/>, and that
    /// the leader actually flips as expected. Also covers crash-penalty application via the
    /// scoreboard (decision F) so the integration track's wiring is verified end-to-end.
    /// </summary>
    [TestFixture]
    public class LeaderboardIntegrationTests
    {
        private readonly List<(string, int)> _lumensEvents = new List<(string, int)>();
        private readonly List<string> _leaderEvents = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _lumensEvents.Clear();
            _leaderEvents.Clear();
            GameEvents.LumensChanged += OnLumens;
            GameEvents.LeaderChanged += OnLeader;
        }

        [TearDown]
        public void TearDown()
        {
            GameEvents.LumensChanged -= OnLumens;
            GameEvents.LeaderChanged -= OnLeader;
            ServiceLocator.Clear();
        }

        private void OnLumens(string playerId, int newTotal) => _lumensEvents.Add((playerId, newTotal));
        private void OnLeader(string leaderId) => _leaderEvents.Add(leaderId);

        [Test]
        public void Award_IncrementsLumens_FiresLumensChanged()
        {
            var sb = new LumenScoreboard();
            int a1 = sb.Award("p1");
            int a2 = sb.Award("p1");

            Assert.AreEqual(1, a1, "First award → 1");
            Assert.AreEqual(2, a2, "Second award → 2");
            Assert.AreEqual(2, sb.GetLumens("p1"));

            CollectionAssert.Contains(_lumensEvents, ("p1", 1));
            CollectionAssert.Contains(_lumensEvents, ("p1", 2));
        }

        [Test]
        public void Award_CrossingLeader_FiresLeaderChanged()
        {
            var sb = new LumenScoreboard();
            sb.Award("p1");        // p1 leads alone (1 vs 0)

            // p2 ties, then overtakes.
            sb.Award("p2");        // tie at 1 → no leader
            Assert.AreEqual(string.Empty, sb.LeaderPlayerId, "Tied at max → no leader");

            sb.Award("p2");        // p2 alone at 2 → leader
            Assert.AreEqual("p2", sb.LeaderPlayerId);

            CollectionAssert.Contains(_leaderEvents, "p1");  // p1 first took the lead
            CollectionAssert.Contains(_leaderEvents, "");    // tie → empty
            CollectionAssert.Contains(_leaderEvents, "p2");  // p2 took it back
        }

        [Test]
        public void Award_RegisteredOnLocator_RaisesGameEvents_DecisionE()
        {
            // MatchManager registers the scoreboard on Awake; mirror that here.
            var sb = new LumenScoreboard();
            ServiceLocator.Register<ILumenScoreboard>(sb);

            // The integration track's contract: any code path that resolves ILumenScoreboard and
            // calls Award must propagate to GameEvents (so UI/AR/Multiplayer react).
            Assert.IsTrue(ServiceLocator.TryGet<ILumenScoreboard>(out var resolved));
            resolved.Award("p1");

            CollectionAssert.Contains(_lumensEvents, ("p1", 1));
            Assert.AreEqual("p1", resolved.LeaderPlayerId);
        }

        [Test]
        public void CrashPenalty_LeaderDropsMoreThanNonLeader_DecisionF()
        {
            var sb = new LumenScoreboard();
            sb.Award("leader");   // leader has 1
            sb.Award("other");    // other has 1 (tied — no leader)
            sb.Award("leader");   // leader now has 2 → unique leader

            Assert.AreEqual("leader", sb.LeaderPlayerId);
            Assert.AreEqual(CrashTier.Leader, sb.GetCrashTier("leader"));
            Assert.AreEqual(CrashTier.NonLeader, sb.GetCrashTier("other"));

            int heldLeader = sb.GetLumens("leader");    // 2
            int heldOther = sb.GetLumens("other");       // 1

            int droppedLeader = sb.ApplyCrashPenalty("leader");
            int droppedOther = sb.ApplyCrashPenalty("other");

            // Default config: leader loses 2, non-leader loses 1 (both capped by held score).
            Assert.AreEqual(System.Math.Min(heldLeader, GameConfig.Active.crashLumenLossLeader), droppedLeader,
                "Leader should drop min(held, crashLumenLossLeader) Lumens (decision F)");
            Assert.AreEqual(System.Math.Min(heldOther, GameConfig.Active.crashLumenLossNonLeader), droppedOther,
                "Non-leader should drop min(held, crashLumenLossNonLeader) Lumens (decision F)");
            Assert.GreaterOrEqual(droppedLeader, droppedOther,
                "Leader should drop at least as many Lumens as a non-leader (decision F)");
            Assert.GreaterOrEqual(sb.GetLumens("leader"), 0, "Tally must never go negative");
            Assert.GreaterOrEqual(sb.GetLumens("other"), 0, "Tally must never go negative");
        }

        [Test]
        public void CrashPenalty_DropsLumens_CappedByHeldScore_NeverNegative()
        {
            var sb = new LumenScoreboard();
            // Player with 0 Lumens crashes — drops nothing, never goes negative.
            int dropped = sb.ApplyCrashPenalty("penniless");
            Assert.AreEqual(0, dropped);
            Assert.AreEqual(0, sb.GetLumens("penniless"));
        }
    }
}
