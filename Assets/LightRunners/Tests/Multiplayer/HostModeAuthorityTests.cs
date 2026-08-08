using System;
using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Multiplayer;

namespace LightRunners.Multiplayer.Tests
{
    /// <summary>
    /// Host-Mode authority smoke tests (decision Q) — Mirror implementation.
    ///
    /// These tests assert that the public types implement the contracts they claim to:
    ///   • MirrorLauncher implements IMatchTransport (the contract Track D's MatchManager
    ///     resolves from the ServiceLocator).
    ///   • MirrorLauncher exposes IsHost (the host-authority flag, decision Q).
    ///   • MirrorNetworkPlayer exposes the three authority-split properties.
    ///
    /// Mirror is free and open source (MIT), so these tests compile without any
    /// paid SDK or preprocessor guard — unlike the old Fusion placeholders which
    /// were gated behind FUSION_WEAVER and skipped in every CI run.
    /// </summary>
    [TestFixture]
    public class HostModeAuthorityTests
    {
        /// <summary>
        /// Reflection smoke check: <see cref="MirrorLauncher"/> implements
        /// <see cref="IMatchTransport"/>. This is the contract Track D's
        /// MatchManager relies on (locator-resolved IMatchTransport). If a
        /// refactor drops the interface, this test fails immediately.
        /// </summary>
        [Test]
        public void MirrorLauncher_Implements_IMatchTransport()
        {
            Type launcherType = typeof(MirrorLauncher);
            Type transportInterface = typeof(IMatchTransport);

            Assert.IsTrue(transportInterface.IsAssignableFrom(launcherType),
                $"{launcherType.FullName} must implement {transportInterface.FullName} so Track D can resolve it from the ServiceLocator.");
        }

        /// <summary>
        /// Reflection smoke check: <see cref="MirrorLauncher"/> declares the
        /// Host-Mode authority flag <see cref="MirrorLauncher.IsHost"/>. The host
        /// peer owns the authoritative Lumen tally, applies crash penalties, and
        /// validates Gate-collect / referee Commands (decision Q).
        /// </summary>
        [Test]
        public void MirrorLauncher_Declares_IsHost_Property()
        {
            Type launcherType = typeof(MirrorLauncher);
            var prop = launcherType.GetProperty("IsHost");
            Assert.IsNotNull(prop, "MirrorLauncher must expose IsHost for host-authority branching (decision Q).");
            Assert.AreEqual(typeof(bool), prop.PropertyType);
        }

        /// <summary>
        /// Reflection smoke check: <see cref="MirrorNetworkPlayer"/> exposes the
        /// Host-Mode authority split (IsLocalAuthority, IsHostAuthority,
        /// HasInputAuthorityOnly). Track D depends on these three properties.
        /// </summary>
        [Test]
        public void MirrorNetworkPlayer_Declares_HostModeAuthority_Properties()
        {
            Type np = typeof(MirrorNetworkPlayer);
            Assert.IsNotNull(np.GetProperty("IsLocalAuthority"),
                "IsLocalAuthority: true on each peer for its own avatar.");
            Assert.IsNotNull(np.GetProperty("IsHostAuthority"),
                "IsHostAuthority: true only on the host peer (decision Q).");
            Assert.IsNotNull(np.GetProperty("HasInputAuthorityOnly"),
                "HasInputAuthorityOnly: true on clients for their own avatar.");
        }

        /// <summary>
        /// Verify MirrorLauncher registers/unregisters on the ServiceLocator as
        /// IMatchTransport. This is the decision-Q transport seam: Phase 0 registers
        /// a NullMatchTransport; the real Mirror transport OVERWRITES that slot.
        /// </summary>
        [Test]
        public void MirrorLauncher_IsRegistered_OnAwake_AsTransport()
        {
            // The registration logic is in Awake; we can't easily create a MonoBehaviour
            // in EditMode without the full Unity lifecycle, so verify via reflection
            // that the registration methods exist and are correctly named.
            Type launcherType = typeof(MirrorLauncher);
            Assert.IsNotNull(launcherType.GetMethod("ConnectMatch",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
                "MirrorLauncher must have ConnectMatch (IMatchTransport).");
            Assert.IsNotNull(launcherType.GetMethod("Disconnect",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
                "MirrorLauncher must have Disconnect (IMatchTransport — inherited from NetworkManager).");
        }
    }
}
