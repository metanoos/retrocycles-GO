using NUnit.Framework;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Trail.Tests
{
    /// <summary>
    /// Active decision T (tail geometry). Verifies <see cref="TailAuthority"/>: the authoritative
    /// radius is frozen at countdown, the frozen value survives subsequent config changes, and
    /// Unfreeze resets it. Mirrors the existing test convention.
    ///
    /// Why this matters (decision T): in v1, <c>collisionThreshold</c> and <c>trailWidth</c> were
    /// decoupled, so a host could set a wide visual ribbon with a narrow collision radius and the
    /// runner would clip through their own tail. Decision T makes the tail radius the single source
    /// of truth, frozen at countdown so the Afterglow replay shows the same tail the runners raced.
    /// </summary>
    public class TailAuthorityTests
    {
        [SetUp]
        public void SetUp() => ClearConfigCache();

        [TearDown]
        public void TearDown() => ClearConfigCache();

        [Test]
        public void FrozenTailRadius_PreFreeze_ReturnsLiveConfigValue()
        {
            var cfg = SetCachedConfig(tailRadius: 2.0f);
            try
            {
                var auth = new TailAuthority();
                Assert.IsFalse(auth.IsFrozen);
                Assert.AreEqual(2.0f, auth.FrozenTailRadius, 1e-4f,
                    "pre-freeze: returns the live config value");
            }
            finally { RestoreConfig(cfg); }
        }

        [Test]
        public void FreezeAtCountdown_SnapshotsConfigValue()
        {
            var cfg = SetCachedConfig(tailRadius: 2.5f);
            try
            {
                var auth = new TailAuthority();
                Assert.IsTrue(auth.TryFreezeAtCountdown(out string error), error);
                Assert.IsTrue(auth.IsFrozen);
                Assert.AreEqual(2.5f, auth.FrozenTailRadius, 1e-4f,
                    "frozen value = config value at the moment of freezing");
            }
            finally { RestoreConfig(cfg); }
        }

        /// <summary>
        /// Decision T core invariant: after freeze, mutating the config tunable does NOT change
        /// the authoritative radius. This is what makes Afterglow replays show the same tail the
        /// runners actually raced against.
        /// </summary>
        [Test]
        public void FrozenTailRadius_PostFreeze_ConfigChangeDoesNotAlter()
        {
            var cfg = SetCachedConfig(tailRadius: 1.5f);
            try
            {
                var auth = new TailAuthority();
                auth.FreezeAtCountdown();
                Assert.AreEqual(1.5f, auth.FrozenTailRadius, 1e-4f);

                // Now mutate the config — the frozen authority must NOT pick up the change.
                MutateCachedConfig(tailRadius: 5.0f);

                Assert.IsTrue(auth.IsFrozen, "still frozen");
                Assert.AreEqual(1.5f, auth.FrozenTailRadius, 1e-4f,
                    "post-freeze config changes must not alter the authoritative radius");
            }
            finally { RestoreConfig(cfg); }
        }

        [Test]
        public void FreezeAtCountdown_Twice_FirstFreezeWins()
        {
            var cfg = SetCachedConfig(tailRadius: 1.5f);
            try
            {
                var auth = new TailAuthority();
                auth.FreezeAtCountdown();
                MutateCachedConfig(tailRadius: 3.0f);
                auth.FreezeAtCountdown(); // no-op — re-freeze mid-match is forbidden (decision T)

                Assert.AreEqual(1.5f, auth.FrozenTailRadius, 1e-4f,
                    "first freeze wins; a re-freeze can't change the rules mid-match");
            }
            finally { RestoreConfig(cfg); }
        }

        [Test]
        public void Unfreeze_ResetsToLiveConfigValue()
        {
            var cfg = SetCachedConfig(tailRadius: 2.5f);
            try
            {
                var auth = new TailAuthority();
                auth.FreezeAtCountdown();
                Assert.IsTrue(auth.IsFrozen);

                auth.Unfreeze();

                Assert.IsFalse(auth.IsFrozen, "unfrozen");
                Assert.AreEqual(2.5f, auth.FrozenTailRadius, 1e-4f,
                    "after unfreeze, returns to live config value again");
            }
            finally { RestoreConfig(cfg); }
        }

        [Test]
        public void Unfreeze_AllowsRefreezeWithNewConfig()
        {
            var cfg = SetCachedConfig(tailRadius: 1.5f);
            try
            {
                var auth = new TailAuthority();
                auth.FreezeAtCountdown();
                Assert.AreEqual(1.5f, auth.FrozenTailRadius, 1e-4f);

                auth.Unfreeze();
                MutateCachedConfig(tailRadius: 3.0f);
                auth.FreezeAtCountdown(); // a NEW match re-freezes fresh

                Assert.AreEqual(3.0f, auth.FrozenTailRadius, 1e-4f,
                    "between matches, Unfreeze lets the next countdown freeze a fresh value");
            }
            finally { RestoreConfig(cfg); }
        }

        [TestCase(1.0f)]
        [TestCase(1.75f)]
        [TestCase(4.5f)]
        public void TryFreezeAtCountdown_RejectsIllegalTailRadius(float illegalRadius)
        {
            var cfg = SetCachedConfig(illegalRadius);
            try
            {
                var auth = new TailAuthority();
                Assert.IsFalse(auth.TryFreezeAtCountdown(out string error));
                Assert.IsFalse(auth.IsFrozen);
                StringAssert.Contains("Tail radius", error);
            }
            finally { RestoreConfig(cfg); }
        }

        [Test]
        public void NetworkedFreeze_VerifiesHashAndAppliesHostValue()
        {
            Assert.IsTrue(FrozenMatchConfig.TryCreate(350, 200, out var hostConfig, out string createError), createError);
            var auth = new TailAuthority();

            Assert.IsTrue(auth.TryApplyNetworkedFreeze(350, hostConfig.Hash, out string applyError), applyError);
            Assert.IsTrue(auth.IsFrozen);
            Assert.AreEqual(3.5f, auth.FrozenTailRadius, 1e-4f);
            Assert.AreEqual(5.5f, auth.FrozenConfig.HeadToTrailCollisionMeters, 1e-4f);

            auth.Unfreeze();
            Assert.IsFalse(auth.TryApplyNetworkedFreeze(350, hostConfig.Hash + 1u, out string hashError));
            StringAssert.Contains("hash mismatch", hashError.ToLowerInvariant());
        }

        // ─────────────────────────────────────────────────────────────────────
        // Config injection helpers (mirror LumenScoreboardTests' pattern)
        // ─────────────────────────────────────────────────────────────────────

        private static void ClearConfigCache() => GameConfig.ClearCache();

        private const string TestAssetName = "GameConfig (snake-tail-test)";

        private static GameConfig SetCachedConfig(float tailRadius)
        {
            var fresh = ScriptableObject.CreateInstance<GameConfig>();
            fresh.name = TestAssetName;
            fresh.tailRadius = tailRadius;
            return SwapCached(fresh);
        }

        private static void MutateCachedConfig(float tailRadius)
        {
            var current = GameConfig.Active;
            current.tailRadius = tailRadius;
        }

        private static GameConfig SwapCached(GameConfig newInstance)
        {
            var field = typeof(GameConfig).GetField("_cached", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(field, "_cached field missing — GameConfig changed shape");
            var previous = (GameConfig)field.GetValue(null);
            field.SetValue(null, newInstance);
            return previous;
        }

        private static void RestoreConfig(GameConfig previous)
        {
            var field = typeof(GameConfig).GetField("_cached", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var current = (GameConfig)field.GetValue(null);
            if (current != null && current.name == TestAssetName)
                ScriptableObject.DestroyImmediate(current);
            field.SetValue(null, previous);
        }
    }
}
