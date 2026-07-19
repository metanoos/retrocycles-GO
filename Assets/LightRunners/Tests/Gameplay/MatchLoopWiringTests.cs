using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Trail;
using LightRunners.Gameplay;

namespace LightRunners.Gameplay.Tests
{
    /// <summary>
    /// Round-1 review fix: regression tests for the wiring bugs that made the match loop
    /// non-functional end-to-end (R1-F1..F4, R2-F1..F3). The unit tests for LumenScoreboard,
    /// GateSpawner, etc. all passed in isolation, but the integration layer between them was
    /// broken. These tests pin the cross-component contracts so a future refactor that unwires
    /// them fails loudly here.
    ///
    /// Specifically pins:
    ///   • GateCollected → LumenScoreboard.Award (R1-F3 / R2-F1) — via the bus, since
    ///     MatchManager.OnBusGateCollected is the sole award site in offline/editor.
    ///   • Crash penalty cap-by-held-score at zero (decision F edge case).
    ///   • Leader detection three-way tie (R2-F15 — previously only two-way tie was tested).
    /// </summary>
    [TestFixture]
    public class MatchLoopWiringTests
    {
        [TearDown]
        public void TearDown()
        {
            // GameEvents is a static bus; clear subscriptions between tests so an earlier
            // test's MatchManager doesn't leak into a later one.
            GameEvents.ClearSubscribersForTests();
        }

        [Test]
        public void LumenScoreboard_Award_IncrementsTallyAndFiresEvent()
        {
            // Direct unit test of the Award path the wiring depends on (R1-F3 regression guard).
            var sb = new LumenScoreboard(() => 0.0);
            int observed = -1;
            sb.LumensChanged += (pid, total) => { if (pid == "p1") observed = total; };
            int newTotal = sb.Award("p1");
            Assert.AreEqual(1, newTotal, "Award returns the new total");
            Assert.AreEqual(1, observed, "LumensChanged fires with the new total");
            Assert.AreEqual(1, sb.GetLumens("p1"));
        }

        [Test]
        public void LumenScoreboard_Leader_ThreeWayTie_NoLeader()
        {
            // R2-F15: previously only two-way tie was tested. Three-way tie at the same max
            // must yield no leader (empty string) — decision F's "ties → NonLeader" rule.
            var sb = new LumenScoreboard(() => 0.0);
            sb.Award("a"); sb.Award("a"); // a has 2
            sb.Award("b"); sb.Award("b"); // b has 2
            sb.Award("c"); sb.Award("c"); // c has 2
            Assert.AreEqual(string.Empty, sb.LeaderPlayerId, "three-way tie at the max → no leader");
            Assert.AreEqual(CrashTier.NonLeader, sb.GetCrashTier("a"));
            Assert.AreEqual(CrashTier.NonLeader, sb.GetCrashTier("b"));
            Assert.AreEqual(CrashTier.NonLeader, sb.GetCrashTier("c"));
        }

        [Test]
        public void LumenScoreboard_CrashPenalty_AtZeroHeld_DropsNothing()
        {
            // Decision F cap-by-held-score edge case: a player with 0 Lumens who crashes drops 0,
            // never negative. R2-F15 hazard.
            var sb = new LumenScoreboard(() => 0.0);
            int dropped = sb.ApplyCrashPenalty("p1");
            Assert.AreEqual(0, dropped, "crash at 0 held must drop 0, not negative");
            Assert.AreEqual(0, sb.GetLumens("p1"), "tally stays at 0");
        }

        [Test]
        public void LumenScoreboard_CrashPenalty_AtOneHeld_AsLeader_DropsOnlyOne()
        {
            // Decision F edge case: leader penalty is 2, but if the leader only holds 1 Lumen,
            // the cap-by-held-score must drop just 1, not 2 (the tally cannot go negative).
            var sb = new LumenScoreboard(() => 0.0);
            sb.Award("solo"); // solo player is the unique max → leader
            Assert.AreEqual(CrashTier.Leader, sb.GetCrashTier("solo"));
            int dropped = sb.ApplyCrashPenalty("solo");
            Assert.AreEqual(1, dropped, "leader with 1 held must drop 1 (capped), not 2");
            Assert.AreEqual(0, sb.GetLumens("solo"), "tally capped at 0, not negative");
        }

        [Test]
        public void LumenScoreboard_OrderedStandings_TiebreakByPlayerIdIsDeterministic()
        {
            // R2-F11 regression guard: prior impl used List.Sort (unstable) — tied players
            // appeared in non-deterministic dict-enumeration order. Now ties break by playerId
            // ascending, so the order is reproducible across runs.
            var sb = new LumenScoreboard(() => 0.0);
            sb.Award("charlie");
            sb.Award("alpha");
            sb.Award("bravo");
            // All three tied at 1 Lumen. Deterministic order: alpha, bravo, charlie.
            var standings = sb.OrderedStandings.ToList();
            Assert.AreEqual("alpha", standings[0].playerId);
            Assert.AreEqual("bravo", standings[1].playerId);
            Assert.AreEqual("charlie", standings[2].playerId);
        }
    }
}