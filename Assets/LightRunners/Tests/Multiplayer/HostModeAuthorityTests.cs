#if FUSION_WEAVER
using System;
using System.Linq;
using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Multiplayer;

namespace LightRunners.Multiplayer.Tests
{
    /// <summary>
    /// Host-Mode authority smoke tests (decision Q). Gated on FUSION_WEAVER
    /// because the types under test (FusionLauncher, NetworkPlayer) only compile
    /// when the Fusion SDK is present. Without the SDK this file compiles empty
    /// and is a no-op; the test runner skips it.
    ///
    /// These are PLACEHOLDERS for the full host-authority suite that lands when
    /// Fusion is imported into the project. They exist to:
    ///   • assert that the public types implement the contracts they claim to
    ///     (so a refactor that breaks the IMatchTransport surface fails CI),
    ///   • document the host-authority invariants that the full suite will
    ///     exercise once the runner can be stood up in a test fixture.
    ///
    /// DIVERGENCE FROM SPEC §8.1: under Shared Mode these invariants did not
    /// exist; they are new under Host Mode (decision Q).
    /// </summary>
    [TestFixture]
    public class HostModeAuthorityTests
    {
        /// <summary>
        /// Reflection smoke check: <see cref="FusionLauncher"/> implements
        /// <see cref="IMatchTransport"/>. This is the contract Track D's
        /// MatchManager relies on (locator-resolved IMatchTransport). If a
        /// refactor drops the interface, this test fails immediately.
        /// </summary>
        [Test]
        public void FusionLauncher_Implements_IMatchTransport()
        {
            Type launcherType = typeof(FusionLauncher);
            Type transportInterface = typeof(IMatchTransport);

            Assert.IsTrue(transportInterface.IsAssignableFrom(launcherType),
                $"{launcherType.FullName} must implement {transportInterface.FullName} so Track D can resolve it from the ServiceLocator.");
        }

        /// <summary>
        /// Reflection smoke check: <see cref="FusionLauncher"/> declares the
        /// Host-Mode authority flag <see cref="FusionLauncher.IsHost"/>. The host
        /// peer owns the authoritative Lumen tally, applies crash penalties, and
        /// validates Gate-collect / referee RPCs (decision Q).
        /// </summary>
        [Test]
        public void FusionLauncher_Declares_IsHost_Property()
        {
            Type launcherType = typeof(FusionLauncher);
            var prop = launcherType.GetProperty("IsHost");
            Assert.IsNotNull(prop, "FusionLauncher must expose IsHost for host-authority branching (decision Q).");
            Assert.AreEqual(typeof(bool), prop.PropertyType);
        }

        /// <summary>
        /// Reflection smoke check: <see cref="NetworkPlayer"/> exposes the
        /// Host-Mode authority split (<see cref="NetworkPlayer.IsLocalAuthority"/>,
        /// <see cref="NetworkPlayer.IsHostAuthority"/>,
        /// <see cref="NetworkPlayer.HasInputAuthorityOnly"/>). Under Shared Mode
        /// only IsLocalAuthority existed; under Host Mode (decision Q) the
        /// semantics split and Track D depends on the three properties.
        /// </summary>
        [Test]
        public void NetworkPlayer_Declares_HostModeAuthority_Properties()
        {
            Type np = typeof(NetworkPlayer);
            Assert.IsNotNull(np.GetProperty("IsLocalAuthority"),
                "IsLocalAuthority: true on each peer for its own avatar.");
            Assert.IsNotNull(np.GetProperty("IsHostAuthority"),
                "IsHostAuthority: true only on the host peer (decision Q).");
            Assert.IsNotNull(np.GetProperty("HasInputAuthorityOnly"),
                "HasInputAuthorityOnly: true on clients for their own avatar.");
        }
    }
}
#else
// ─────────────────────────────────────────────────────────────────────────────
// PLACEHOLDER — this file is FUSION_WEAVER-gated. Without the Fusion SDK the
// host-authority types (FusionLauncher, NetworkPlayer) do not compile, so the
// test file compiles empty and the runner skips it. The validation logic that
// CAN run without Fusion lives in RefereeTokenValidatorTests.cs (pure C#).
// When the SDK is imported, the block above activates and the smoke checks
// execute.
// ─────────────────────────────────────────────────────────────────────────────
#endif
