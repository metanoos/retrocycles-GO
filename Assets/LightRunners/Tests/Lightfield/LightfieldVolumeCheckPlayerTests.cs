using System.Collections.Generic;
using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Lightfield;

namespace LightRunners.Lightfield.Tests
{
    /// <summary>
    /// Round-1 review fix R1-F12: <see cref="LightfieldVolume.CheckPlayer"/> transition logic
    /// previously had ZERO test coverage — <see cref="LightfieldBoundaryTests"/> only exercised
    /// the pure-C# geometry helpers. The untested behavior is specifically the per-player
    /// inside/outside STATE tracking that fires <see cref="ILightfieldVolume.BoundaryViolated"/>
    /// at most once per crossing (idempotency). Off-by-one bugs in transition logic are exactly
    /// where this kind of state machine breaks, so these tests pin the invariant.
    ///
    /// Decision K (Lightfield boundary). Origin is (0,0,0); 1° lat ≈ 111 194.93 m so
    /// degree offsets map to known metre distances. Tests derive their outside/above points
    /// from the active <see cref="GameConfig"/> values used by <see cref="LightfieldVolume"/>.
    /// </summary>
    [TestFixture]
    public class LightfieldVolumeCheckPlayerTests
    {
        private const double MetersPerDegLat = 111_194.92664455873;

        private static double OutsideRadiusMeters
            => GameConfig.Active.lightfieldBaseRadiusMeters + 10d;

        private static double AboveCeilingMeters
            => GameConfig.Active.lightfieldDomeCeilingMeters + 1d;

        private static GeoPoint At(double metersNorth, double metersEast, double alt = 0d)
            => new GeoPoint(metersNorth / MetersPerDegLat, metersEast / MetersPerDegLat, alt);

        private static List<string> CaptureViolations(out LightfieldVolume vol)
        {
            vol = new LightfieldVolume();
            vol.SetOrigin(new GeoPoint(0, 0, 0));
            var fires = new List<string>();
            vol.BoundaryViolated += pid => fires.Add(pid);
            return fires;
        }

        [Test]
        public void CheckPlayer_WalkInsideThenOutside_FiresOnce()
        {
            var fires = CaptureViolations(out var vol);
            // Player starts inside at origin; walks outside beyond the configured disc.
            vol.CheckPlayer("p1", At(0, 0, 0));
            Assert.IsEmpty(fires, "should not fire while inside");
            vol.CheckPlayer("p1", At(OutsideRadiusMeters, 0, 0));
            Assert.AreEqual(new[] { "p1" }, fires, "exactly one fire on inside→outside");
        }

        [Test]
        public void CheckPlayer_OutsideInOut_FiresOnceAfterReentry()
        {
            var fires = CaptureViolations(out var vol);
            vol.CheckPlayer("p1", At(OutsideRadiusMeters, 0, 0)); // starts outside — no transition
            vol.CheckPlayer("p1", At(0, 0, 0));     // re-enter (no fire)
            vol.CheckPlayer("p1", At(OutsideRadiusMeters, 0, 0)); // exit again
            // Per the documented contract: starts-outside does NOT fire (no inside→outside
            // transition), but the subsequent inside→outside transition does.
            Assert.AreEqual(new[] { "p1" }, fires, "starts-outside no-fire + one inside→outside = 1 total");
        }

        [Test]
        public void CheckPlayer_StartsOutside_NeverEntered_NoFire()
        {
            var fires = CaptureViolations(out var vol);
            for (int i = 0; i < 10; i++)
                vol.CheckPlayer("p1", At(OutsideRadiusMeters, 0, 0)); // outside every tick
            Assert.IsEmpty(fires, "outside→outside must NOT fire (no transition)");
        }

        [Test]
        public void CheckPlayer_StandingOutside_DoesNotSpam()
        {
            // The dedup invariant: a player standing outside for N ticks fires at most ONCE
            // (transition into outside). This was the untested hazard — without dedup, every
            // tick would re-fire and spam the HUD.
            var fires = CaptureViolations(out var vol);
            vol.CheckPlayer("p1", At(0, 0, 0)); // start inside
            for (int i = 0; i < 10; i++)
                vol.CheckPlayer("p1", At(OutsideRadiusMeters, 0, 0)); // standing outside
            Assert.AreEqual(1, fires.Count, "10 ticks outside must not spam: exactly 1 fire");
        }

        [Test]
        public void CheckPlayer_MultiplePlayers_Independent()
        {
            var fires = CaptureViolations(out var vol);
            vol.CheckPlayer("p1", At(0, 0, 0));
            vol.CheckPlayer("p2", At(0, 0, 0));
            vol.CheckPlayer("p1", At(OutsideRadiusMeters, 0, 0)); // p1 exits
            vol.CheckPlayer("p2", At(0, 0, 0));  // p2 still inside — no fire
            Assert.AreEqual(new[] { "p1" }, fires, "per-player state is independent");
        }

        [Test]
        public void CheckPlayer_AltitudeCeiling_FiresOnUpwardExit()
        {
            var fires = CaptureViolations(out var vol);
            vol.CheckPlayer("p1", At(0, 0, 0)); // inside at ground
            vol.CheckPlayer("p1", At(0, 0, AboveCeilingMeters));
            Assert.AreEqual(new[] { "p1" }, fires, "ceiling crossing fires BoundaryViolated");
        }
    }
}
