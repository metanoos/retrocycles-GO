using NUnit.Framework;
using LightRunners.Core;

namespace LightRunners.Tests
{
    public class FrozenMatchConfigTests
    {
        [Test]
        public void Default_DerivesLockedCollisionAndClearanceValues()
        {
            FrozenMatchConfig config = FrozenMatchConfig.Default;

            Assert.AreEqual(200, config.TailRadiusCm);
            Assert.AreEqual(200, FrozenMatchConfig.PlayerHeadRadiusCm);
            Assert.AreEqual(400, config.HeadToTrailCollisionCm);
            Assert.AreEqual(400, config.HeadToHeadCollisionCm);
            Assert.AreEqual(1200, config.RespawnTrailClearanceCm);
            Assert.AreEqual(800, config.SpawnExitTrailClearanceCm);
            Assert.AreEqual(1000, config.ActiveHeadClearanceCm);
            Assert.AreEqual(400, FrozenMatchConfig.CollisionMicrosegmentCm);
            Assert.AreNotEqual(0u, config.Hash);
        }

        [TestCase(150)]
        [TestCase(200)]
        [TestCase(250)]
        [TestCase(300)]
        [TestCase(350)]
        [TestCase(400)]
        public void LegalTailRadii_CreateAndHashDeterministically(int tailRadiusCm)
        {
            Assert.IsTrue(FrozenMatchConfig.TryCreate(
                tailRadiusCm,
                FrozenMatchConfig.PlayerHeadRadiusCm,
                out var first,
                out string firstError), firstError);
            Assert.IsTrue(FrozenMatchConfig.TryCreate(
                tailRadiusCm,
                FrozenMatchConfig.PlayerHeadRadiusCm,
                out var second,
                out string secondError), secondError);

            Assert.AreEqual(first, second);
            Assert.AreEqual(first.Hash, second.Hash);
            Assert.AreEqual(tailRadiusCm + 200, first.HeadToTrailCollisionCm);
            Assert.AreEqual(400, first.HeadToHeadCollisionCm, "head-to-head is independent of tail thickness");
        }

        [TestCase(149)]
        [TestCase(175)]
        [TestCase(401)]
        public void IllegalTailRadius_IsRejected(int tailRadiusCm)
        {
            Assert.IsFalse(FrozenMatchConfig.TryCreate(tailRadiusCm, 200, out _, out string error));
            StringAssert.Contains("Tail radius", error);
        }

        [Test]
        public void NonDefaultPlayerRadius_IsRejected()
        {
            Assert.IsFalse(FrozenMatchConfig.TryCreate(200, 250, out _, out string error));
            StringAssert.Contains("locked at 200 cm", error);
        }

        [Test]
        public void Restore_RejectsTamperedHash()
        {
            FrozenMatchConfig original = FrozenMatchConfig.Default;
            Assert.IsFalse(FrozenMatchConfig.TryRestore(
                original.TailRadiusCm,
                FrozenMatchConfig.PlayerHeadRadiusCm,
                original.Hash ^ 1u,
                out _,
                out string error));
            StringAssert.Contains("hash mismatch", error.ToLowerInvariant());
        }
    }
}
