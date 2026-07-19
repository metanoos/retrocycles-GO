using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Trail.Tests
{
    /// <summary>
    /// Decision E (one Lumen per Gate touch, integer tally), decision F (crash penalty tiers,
    /// capped by held score, dropped Lumens become stealable pickups), decision I (leader feeds
    /// the live HUD/rank). Active decisions 2026-07-18; mirrors the existing test convention
    /// (NUnit <c>[Test]</c>/<c>[TestCase]</c>, <c>/// &lt;summary&gt;</c> citing spec, tolerance
    /// asserts).
    ///
    /// Config note: <see cref="LumenScoreboard"/> reads tunables from <see cref="GameConfig.Active"/>.
    /// In edit-mode tests that resolves to an in-memory default whose defaults match the spec
    /// (crashLumenLossNonLeader=1, crashLumenLossLeader=2, stolenLumenPickupSeconds=8). Tests that
    /// need to verify the cap-by-held invariant do so by setting the player's tally, not by
    /// mutating config; tests that need to verify a custom lifetime use
    /// <see cref="SetCachedConfig"/> to inject a temp instance.
    /// </summary>
    public class LumenScoreboardTests
    {
        [SetUp]
        public void SetUp() => ClearConfigCache();

        [TearDown]
        public void TearDown() => ClearConfigCache();

        // ── Decision E: Award increments, fires events ─────────────────────

        [Test]
        public void Award_IncrementsByOne()
        {
            var sb = new LumenScoreboard();
            Assert.AreEqual(1, sb.Award("p1"));
            Assert.AreEqual(2, sb.Award("p1"));
            Assert.AreEqual(3, sb.Award("p1"));
            Assert.AreEqual(3, sb.GetLumens("p1"));
        }

        [Test]
        public void Award_UnknownPlayer_StartsAtZeroThenIncrements()
        {
            var sb = new LumenScoreboard();
            Assert.AreEqual(0, sb.GetLumens("nobody"));
            Assert.AreEqual(1, sb.Award("nobody"));
        }

        [Test]
        public void Award_FiresLumensChangedWithNewTotal()
        {
            var sb = new LumenScoreboard();
            string seenPlayer = "unset";
            int seenTotal = -1;
            int fireCount = 0;
            sb.LumensChanged += (pid, total) =>
            {
                seenPlayer = pid;
                seenTotal = total;
                fireCount++;
            };

            sb.Award("p1");
            sb.Award("p1");

            Assert.AreEqual("p1", seenPlayer);
            Assert.AreEqual(2, seenTotal);
            Assert.AreEqual(2, fireCount);
        }

        [Test]
        public void Award_EmptyPlayerId_IsNoOp()
        {
            var sb = new LumenScoreboard();
            Assert.AreEqual(0, sb.Award(""));
            Assert.AreEqual(0, sb.Award(null));
            Assert.AreEqual(0, sb.GetLumens(""));
        }

        // ── Decision I: Leader detection ───────────────────────────────────

        [Test]
        public void Leader_SoloLeader_IsThatPlayer()
        {
            var sb = new LumenScoreboard();
            sb.Award("solo");
            Assert.AreEqual("solo", sb.LeaderPlayerId);
        }

        [Test]
        public void Leader_NoPlayers_IsEmpty()
        {
            var sb = new LumenScoreboard();
            Assert.AreEqual(string.Empty, sb.LeaderPlayerId);
        }

        [Test]
        public void Leader_AllZero_IsEmpty()
        {
            // A scoreboard never goes negative; but a freshly-constructed one with no Awards is
            // the "all zero" state. Award then crash-cap to zero to exercise the all-zero path.
            var sb = new LumenScoreboard();
            sb.Award("p1");
            sb.ApplyCrashPenalty("p1"); // drops 1 (non-leader default) → back to 0
            Assert.AreEqual(0, sb.GetLumens("p1"));
            Assert.AreEqual(string.Empty, sb.LeaderPlayerId, "all-zero must have no leader");
        }

        [Test]
        public void Leader_TieAtNonZero_IsEmpty()
        {
            var sb = new LumenScoreboard();
            sb.Award("p1");
            sb.Award("p2");
            Assert.AreEqual(string.Empty, sb.LeaderPlayerId, "tied at the max → no leader (decision F)");
        }

        [Test]
        public void Leader_TieResolvesToLeader_WhenOnePullsAhead()
        {
            var sb = new LumenScoreboard();
            sb.Award("p1");
            sb.Award("p2");
            sb.Award("p1"); // p1 pulls ahead 2 vs 1
            Assert.AreEqual("p1", sb.LeaderPlayerId);
        }

        [Test]
        public void Leader_LeadChange_FiresLeaderChangedEvent()
        {
            var sb = new LumenScoreboard();
            string lastFired = "(never)";
            int fires = 0;
            sb.LeaderChanged += newLeader =>
            {
                lastFired = newLeader;
                fires++;
            };

            sb.Award("p1"); // → leader p1 (change)
            sb.Award("p2"); // tie → "" (change)
            sb.Award("p2"); // → leader p2 (change)
            sb.Award("p2"); // still p2 (NO change)

            Assert.AreEqual("p2", lastFired);
            Assert.AreEqual(3, fires, "LeaderChanged fires only when the leader id actually changes");
        }

        // ── Decision F: Crash penalty tiers, capped by held, queue pickups ─

        [Test]
        public void CrashPenalty_NonLeader_DropsConfiguredAmount()
        {
            var sb = new LumenScoreboard();
            sb.Award("leader");
            sb.Award("leader"); // 2 — sole leader
            sb.Award("other"); // 1 — non-leader

            int dropped = sb.ApplyCrashPenalty("other");
            Assert.AreEqual(1, dropped, "non-leader default loss is 1");
            Assert.AreEqual(0, sb.GetLumens("other"));
            Assert.AreEqual(CrashTier.NonLeader, sb.GetCrashTier("other"));
        }

        [Test]
        public void CrashPenalty_Leader_DropsConfiguredAmount()
        {
            var sb = new LumenScoreboard();
            sb.Award("leader");
            sb.Award("leader");
            sb.Award("leader"); // 3 — sole leader
            sb.Award("other"); // 1

            Assert.AreEqual(CrashTier.Leader, sb.GetCrashTier("leader"));
            int dropped = sb.ApplyCrashPenalty("leader");
            Assert.AreEqual(2, dropped, "leader default loss is 2");
            Assert.AreEqual(1, sb.GetLumens("leader"));
        }

        [Test]
        public void CrashPenalty_Tie_NoLeader_BothTreatedAsNonLeader()
        {
            var sb = new LumenScoreboard();
            sb.Award("p1");
            sb.Award("p2"); // tie at 1 → no leader
            Assert.AreEqual(string.Empty, sb.LeaderPlayerId);

            Assert.AreEqual(CrashTier.NonLeader, sb.GetCrashTier("p1"), "tie → everyone NonLeader");
            Assert.AreEqual(CrashTier.NonLeader, sb.GetCrashTier("p2"));

            int dropped = sb.ApplyCrashPenalty("p1");
            Assert.AreEqual(1, dropped);
            Assert.AreEqual(0, sb.GetLumens("p1"));
        }

        [TestCase(0, Description = "zero held → drops nothing")]
        [TestCase(1, Description = "one held, leader loss 2 → capped at 1")]
        public void CrashPenalty_CappedByHeldScore(int heldBefore)
        {
            var sb = new LumenScoreboard();
            // Make p1 the sole leader so the leader loss (2) applies.
            for (int i = 0; i < heldBefore; i++) sb.Award("p1");
            sb.Award("other"); // a competitor so p1's lead is unambiguous when held>0

            int dropped = sb.ApplyCrashPenalty("p1");
            Assert.AreEqual(Math.Min(2, heldBefore), dropped, "loss must be min(configured, held)");
            Assert.AreEqual(Math.Max(0, heldBefore - 2), sb.GetLumens("p1"));
        }

        [Test]
        public void CrashPenalty_NeverGoesNegative()
        {
            var sb = new LumenScoreboard();
            sb.Award("p1"); // 1 held, sole leader
            int dropped1 = sb.ApplyCrashPenalty("p1");
            int dropped2 = sb.ApplyCrashPenalty("p1"); // 0 held now
            Assert.AreEqual(1, dropped1);
            Assert.AreEqual(0, dropped2, "capped at 0 — never negative");
            Assert.AreEqual(0, sb.GetLumens("p1"));
        }

        [Test]
        public void CrashPenalty_StolenPickupQueuePopulatedWithCorrectLifetime()
        {
            var cfg = SetCachedConfig(stolenLumenPickupSeconds: 12f);
            try
            {
                var sb = new LumenScoreboard(matchClockSeconds: () => 42.0);
                sb.Award("p1");
                sb.Award("p1"); // 2 — sole leader

                GeoPoint at = new GeoPoint(37.0, -122.0, 5.0);
                int dropped = sb.ApplyCrashPenalty("p1", at);

                Assert.AreEqual(2, dropped);
                Assert.AreEqual(1, sb.StolenLumenCount, "queue holds one record per crash");

                Assert.IsTrue(sb.TryDequeueStolenLumen(out var rec));
                Assert.AreEqual("p1", rec.PlayerId);
                Assert.AreEqual(2, rec.LumensDropped);
                Assert.AreEqual(at, rec.At);
                Assert.AreEqual(42.0, rec.MatchTimeSeconds, 1e-9, "match clock supplied by ctor");
                Assert.AreEqual(12f, rec.LifetimeSeconds, 1e-3f, "lifetime snapshotted from config at drop time");
                Assert.AreEqual(54.0, rec.ExpiresAtSeconds, 1e-9);
                Assert.IsTrue(rec.IsValid);

                Assert.IsFalse(sb.TryDequeueStolenLumen(out _), "queue drained");
            }
            finally { RestoreDefaultConfig(cfg); }
        }

        [Test]
        public void CrashPenalty_ZeroDrop_DoesNotEnqueue()
        {
            var sb = new LumenScoreboard();
            // No Lumens held → nothing to drop → no pickup.
            int dropped = sb.ApplyCrashPenalty("p1");
            Assert.AreEqual(0, dropped);
            Assert.AreEqual(0, sb.StolenLumenCount);
        }

        [Test]
        public void CrashPenalty_FiresLumensChangedWithNewTotal()
        {
            var sb = new LumenScoreboard();
            sb.Award("p1"); // 1 — sole leader
            int seenTotal = -1;
            sb.LumensChanged += (pid, total) => seenTotal = total;

            sb.ApplyCrashPenalty("p1");
            Assert.AreEqual(0, seenTotal);
        }

        [Test]
        public void Reset_ClearsTallyAndQueue()
        {
            var sb = new LumenScoreboard();
            sb.Award("p1");
            sb.Award("p1");
            sb.ApplyCrashPenalty("p1");

            sb.Reset();

            Assert.AreEqual(0, sb.GetLumens("p1"));
            Assert.AreEqual(0, sb.StolenLumenCount);
            Assert.AreEqual(string.Empty, sb.LeaderPlayerId);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Config injection helpers (GameConfig._cached is private; use reflection)
        // ─────────────────────────────────────────────────────────────────────

        private static void ClearConfigCache()
        {
            // Ensure Active rebuilds on next access (Resources or in-memory default).
            GameConfig.ClearCache();
        }

        /// <summary>
        /// Create an in-memory <see cref="GameConfig"/> with the given overrides, install it as the
        /// cached Active instance via reflection, and return the previously-cached instance so the
        /// caller can restore it. Used to test config-derived tunables without a Resources asset.
        /// </summary>
        private static GameConfig SetCachedConfig(
            int? crashLumenLossNonLeader = null,
            int? crashLumenLossLeader = null,
            float? stolenLumenPickupSeconds = null,
            float? tailRadius = null)
        {
            var fresh = ScriptableObject.CreateInstance<GameConfig>();
            fresh.name = "GameConfig (test)";
            if (crashLumenLossNonLeader.HasValue) fresh.crashLumenLossNonLeader = crashLumenLossNonLeader.Value;
            if (crashLumenLossLeader.HasValue) fresh.crashLumenLossLeader = crashLumenLossLeader.Value;
            if (stolenLumenPickupSeconds.HasValue) fresh.stolenLumenPickupSeconds = stolenLumenPickupSeconds.Value;
            if (tailRadius.HasValue) fresh.tailRadius = tailRadius.Value;
            return SwapCached(fresh);
        }

        private static GameConfig SwapCached(GameConfig newInstance)
        {
            var field = typeof(GameConfig).GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "_cached field missing — GameConfig changed shape");
            var previous = (GameConfig)field.GetValue(null);
            field.SetValue(null, newInstance);
            return previous;
        }

        private static void RestoreDefaultConfig(GameConfig previous)
        {
            var field = typeof(GameConfig).GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static);
            // Destroy any temp instance we created; leave a Resources-loaded one alone.
            var current = (GameConfig)field.GetValue(null);
            if (current != null && current.name == "GameConfig (test)")
                ScriptableObject.DestroyImmediate(current);
            field.SetValue(null, previous);
        }
    }
}
