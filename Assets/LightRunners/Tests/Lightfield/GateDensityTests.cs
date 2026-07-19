using NUnit.Framework;
using LightRunners.Lightfield;

namespace LightRunners.Lightfield.Tests
{
    /// <summary>
    /// Decision M: <c>activeGateCount = max(1, ceil(playerCount × gatesPerPlayer))</c>. Table
    /// test across the spec ratios (0.5 default; 0.1, 1.0, 2.0 boundary ratios) and across
    /// 1/2/3/5/7/8 players, plus edge cases (zero players, negative players, NaN/negative ratio,
    /// ceil-tie at integer products).
    /// </summary>
    public class GateDensityTests
    {
        // ── Default ratio 0.5 (decision M default) ───────────────────────────
        [TestCase(1, 0.5f, ExpectedResult = 1)]  // ceil(0.5) = 1
        [TestCase(2, 0.5f, ExpectedResult = 1)]  // ceil(1.0) = 1
        [TestCase(3, 0.5f, ExpectedResult = 2)]  // ceil(1.5) = 2
        [TestCase(5, 0.5f, ExpectedResult = 3)]  // ceil(2.5) = 3
        [TestCase(7, 0.5f, ExpectedResult = 4)]  // ceil(3.5) = 4
        [TestCase(8, 0.5f, ExpectedResult = 4)]  // ceil(4.0) = 4
        public int DefaultRatio_Half(int players, float ratio)
            => GateDensity.ActiveGateCount(players, ratio);

        // ── Sparse ratio 0.1 ─────────────────────────────────────────────────
        [TestCase(1, 0.1f, ExpectedResult = 1)]  // ceil(0.1) = 1, floored at 1
        [TestCase(5, 0.1f, ExpectedResult = 1)]  // ceil(0.5) = 1
        [TestCase(7, 0.1f, ExpectedResult = 1)]  // ceil(0.7) = 1
        [TestCase(11, 0.1f, ExpectedResult = 2)] // ceil(1.1) = 2
        public int SparseRatio_OneTenth(int players, float ratio)
            => GateDensity.ActiveGateCount(players, ratio);

        // ── 1:1 ratio ────────────────────────────────────────────────────────
        [TestCase(1, 1.0f, ExpectedResult = 1)]
        [TestCase(2, 1.0f, ExpectedResult = 2)]
        [TestCase(3, 1.0f, ExpectedResult = 3)]
        [TestCase(7, 1.0f, ExpectedResult = 7)]
        [TestCase(8, 1.0f, ExpectedResult = 8)]
        public int OneToOneRatio(int players, float ratio)
            => GateDensity.ActiveGateCount(players, ratio);

        // ── 2:1 ratio (dense) ────────────────────────────────────────────────
        [TestCase(1, 2.0f, ExpectedResult = 2)]  // ceil(2.0) = 2
        [TestCase(3, 2.0f, ExpectedResult = 6)]  // ceil(6.0) = 6
        [TestCase(5, 2.0f, ExpectedResult = 10)] // ceil(10.0) = 10
        public int TwoToOneRatio(int players, float ratio)
            => GateDensity.ActiveGateCount(players, ratio);

        // ── Edge cases (floor of 1, garbage input) ───────────────────────────
        [Test]
        public void ZeroPlayers_AlwaysAtLeastOneGate() =>
            Assert.AreEqual(1, GateDensity.ActiveGateCount(0, 0.5f));

        [Test]
        public void NegativePlayers_AlwaysAtLeastOneGate() =>
            Assert.AreEqual(1, GateDensity.ActiveGateCount(-5, 0.5f));

        [Test]
        public void NegativeRatio_FloorsToOneGate() =>
            Assert.AreEqual(1, GateDensity.ActiveGateCount(10, -1.0f));

        [Test]
        public void NanRatio_FloorsToOneGate() =>
            Assert.AreEqual(1, GateDensity.ActiveGateCount(10, float.NaN));

        [Test]
        public void ZeroRatio_FloorsToOneGate() =>
            Assert.AreEqual(1, GateDensity.ActiveGateCount(10, 0f));

        // ── Explicit ceil edge cases called out in the spec ─────────────────
        [Test]
        public void OnePlayerHalf_FlooredToOne() =>
            Assert.AreEqual(1, GateDensity.ActiveGateCount(1, 0.5f));

        [Test]
        public void ThreePlayersHalf_CeilsToTwo() =>
            Assert.AreEqual(2, GateDensity.ActiveGateCount(3, 0.5f));

        [Test]
        public void FivePlayersHalf_CeilsToThree() =>
            Assert.AreEqual(3, GateDensity.ActiveGateCount(5, 0.5f));
    }
}
