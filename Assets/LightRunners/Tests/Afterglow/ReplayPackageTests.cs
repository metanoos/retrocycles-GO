using System;
using System.Collections.Generic;
using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Afterglow;

namespace LightRunners.Afterglow.Tests
{
    /// <summary>
    /// Decision A/U/T: the <see cref="ReplayPackage"/> + <see cref="ReplayPackageSink"/>
    /// unit tests. Verifies event timestamp preservation, Freeze() rejection of further
    /// captures (decision A — package becomes immutable art), and final-snapshot population
    /// of <see cref="ReplayPackage.Trails"/> via the registered provider.
    /// </summary>
    public class ReplayPackageTests
    {
        private const double MeterLat = 1.0 / 111194.93; // ≈ 1 m of latitude in degrees

        private static GeoPoint MetersToGeo(double eastMeters, double northMeters, double alt = 0.0)
            => new GeoPoint(northMeters * MeterLat, eastMeters * MeterLat, alt);

        // ─────────────────────────────────────────────────────────────────────
        // ReplayPackage — capture ordering & Freeze rejection
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void LumenEvents_PreserveCaptureOrderAndTimestamps()
        {
            var pkg = new ReplayPackage("m1", DateTime.UtcNow, default);
            pkg.AddLumen(new LumenEvent("p1", MetersToGeo(0, 0), 1.0));
            pkg.AddLumen(new LumenEvent("p2", MetersToGeo(10, 0), 2.5));
            pkg.AddLumen(new LumenEvent("p1", MetersToGeo(0, 10), 5.0));

            Assert.AreEqual(3, pkg.Lumens.Count);
            Assert.AreEqual("p1", pkg.Lumens[0].PlayerId);
            Assert.AreEqual(1.0, pkg.Lumens[0].TimeSeconds, 1e-9);
            Assert.AreEqual("p2", pkg.Lumens[1].PlayerId);
            Assert.AreEqual(2.5, pkg.Lumens[1].TimeSeconds, 1e-9);
            Assert.AreEqual("p1", pkg.Lumens[2].PlayerId);
            Assert.AreEqual(5.0, pkg.Lumens[2].TimeSeconds, 1e-9);
        }

        [Test]
        public void CrashEvents_PreserveCaptureOrderAndTimestamps()
        {
            var pkg = new ReplayPackage();
            pkg.AddCrash(new CrashEvent("p1", MetersToGeo(0, 0), 3.0, CrashTier.Leader, 2));
            pkg.AddCrash(new CrashEvent("p2", MetersToGeo(5, 5), 7.0, CrashTier.NonLeader, 1));

            Assert.AreEqual(2, pkg.Crashes.Count);
            Assert.AreEqual(CrashTier.Leader, pkg.Crashes[0].Tier);
            Assert.AreEqual(2, pkg.Crashes[0].LumensDropped);
            Assert.AreEqual(CrashTier.NonLeader, pkg.Crashes[1].Tier);
            Assert.AreEqual(1, pkg.Crashes[1].LumensDropped);
            Assert.Less(pkg.Crashes[0].TimeSeconds, pkg.Crashes[1].TimeSeconds,
                "crash timestamp ordering must survive capture (decision U ordering)");
        }

        [Test]
        public void Freeze_RejectsFurtherCaptures_AndIsIdempotent()
        {
            var pkg = new ReplayPackage();
            pkg.Freeze();
            Assert.IsTrue(pkg.IsFrozen);

            // Idempotent — second Freeze does not throw.
            Assert.DoesNotThrow(() => pkg.Freeze());

            // Decision A: post-freeze captures throw via the package's internal API.
            Assert.Throws<InvalidOperationException>(
                () => pkg.AddLumen(new LumenEvent("p1", default, 1.0)),
                "AddLumen must reject a frozen package");
            Assert.Throws<InvalidOperationException>(
                () => pkg.AddCrash(new CrashEvent("p1", default, 1.0, CrashTier.NonLeader, 0)),
                "AddCrash must reject a frozen package");
            Assert.Throws<InvalidOperationException>(
                () => pkg.AddTrail(new TrailCapture("p1", Array.Empty<double>(), 0)),
                "AddTrail must reject a frozen package");
            Assert.Throws<InvalidOperationException>(
                () => pkg.SetFinishOrder(new List<string> { "p1" }),
                "SetFinishOrder must reject a frozen package");
        }

        [Test]
        public void Freeze_FillsMissingEndTime()
        {
            var pkg = new ReplayPackage();
            Assert.AreEqual(default(DateTime), pkg.MatchEndTimeUtc);
            pkg.Freeze();
            Assert.AreNotEqual(default(DateTime), pkg.MatchEndTimeUtc,
                "Freeze must default MatchEndTimeUtc to now if unset");
        }

        [Test]
        public void AddTrail_LatestWinsPerPlayer()
        {
            var pkg = new ReplayPackage();
            pkg.AddTrail(new TrailCapture("p1", new double[] { 0, 0, 0 }, 1));
            pkg.AddTrail(new TrailCapture("p1", new double[] { 0, 0, 0, 1, 0, 0 }, 2));

            Assert.AreEqual(1, pkg.Trails.Count, "second snapshot must replace, not append");
            Assert.AreEqual(2, pkg.Trails[0].PointCount);
        }

        [Test]
        public void MatchId_DefaultsToGuid_WhenEmpty()
        {
            var pkg = new ReplayPackage();
            Assert.IsFalse(string.IsNullOrEmpty(pkg.MatchId));
            var pkg2 = new ReplayPackage("", default, default);
            Assert.IsFalse(string.IsNullOrEmpty(pkg2.MatchId), "empty MatchId must fall back to a GUID");
        }

        [Test]
        public void TrailCapture_RoundTripsToGeoPoints()
        {
            double[] coords = {
                37.7749, -122.4194, 5.0,
                37.7750, -122.4194, 5.5,
                37.7751, -122.4194, 6.0,
            };
            var cap = new TrailCapture("p1", coords, 3);
            var geo = cap.ToGeoPoints();
            Assert.AreEqual(3, geo.Count);
            Assert.AreEqual(37.7749, geo[0].latitude, 1e-9);
            Assert.AreEqual(-122.4194, geo[0].longitude, 1e-9);
            Assert.AreEqual(6.0, geo[2].altitude, 1e-9);
        }

        // ─────────────────────────────────────────────────────────────────────
        // TrailSnapshotPoints → TrailCapture defensive copy
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void TrailCapture_FromSnapshot_DefensiveCopy()
        {
            double[] src = { 1, 2, 3, 4, 5, 6 };
            var points = new TrailSnapshotPoints(src, 2);
            var cap = TrailCapture.FromSnapshot("p1", in points);

            // Mutate the source after capture — the captured copy must be unaffected.
            src[0] = 999;
            Assert.AreEqual(1, cap.Coords[0], 1e-9, "FromSnapshot must take a defensive copy");
            Assert.AreEqual(2, cap.PointCount);
        }

        [Test]
        public void TrailCapture_FromSnapshot_EmptyInput()
        {
            var empty = new TrailSnapshotPoints(null, 0);
            var cap = TrailCapture.FromSnapshot("p1", in empty);
            Assert.AreEqual(0, cap.PointCount);
            Assert.IsNotNull(cap.Coords);
            Assert.AreEqual(0, cap.Coords.Length);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ReplayPackageSink — finalization populates Trails from provider
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Sink_FreezesPackage_WithProviderSnapshots()
        {
            var pkg = new ReplayPackage("m1", DateTime.UtcNow, default);
            pkg.SetOrigin(MetersToGeo(0, 0));
            var sink = new ReplayPackageSink(pkg);

            // Provider returns a 2-point trail for p1, empty for p2.
            sink.TrailSnapshotProvider = id => id == "p1"
                ? new TrailSnapshotPoints(new double[] { 0, 0, 0, 0, 1, 0 }, 2)
                : new TrailSnapshotPoints(null, 0);
            sink.LivePlayerEnumerator = () => new[] { "p1", "p2" };
            sink.FrozenTailRadius = 0.7f;
            sink.FinishOrder = new[] { "p1", "p2" };

            // Record a few events BEFORE finalization.
            sink.RecordLumen("p1", MetersToGeo(5, 5), 1.5);
            sink.RecordCrash("p2", MetersToGeo(0, 10), 3.0, CrashTier.NonLeader, 1);

            sink.Freeze();

            Assert.IsTrue(pkg.IsFrozen);
            Assert.IsTrue(sink.IsFinalized);
            Assert.AreEqual(0.7f, pkg.FrozenTailRadius, 1e-4f, "FrozenTailRadius (decision T) must propagate");
            Assert.AreEqual(new[] { "p1", "p2" }, pkg.FinishOrder);

            // The provider's snapshot for p1 must end up in Trails.
            Assert.AreEqual(1, pkg.Trails.Count, "only p1 had a non-empty snapshot");
            Assert.AreEqual("p1", pkg.Trails[0].PlayerId);
            Assert.AreEqual(2, pkg.Trails[0].PointCount);

            Assert.AreEqual(1, pkg.Lumens.Count);
            Assert.AreEqual(1, pkg.Crashes.Count);
            Assert.AreEqual(1, pkg.Crashes[0].LumensDropped);
        }

        [Test]
        public void FrozenMatchConfig_RoundTripsAndRejectsTampering()
        {
            var package = new ReplayPackage();
            Assert.IsTrue(FrozenMatchConfig.TryCreate(300, 200, out var frozen, out string createError), createError);

            package.SetFrozenMatchConfig(frozen);

            Assert.IsTrue(package.TryGetFrozenMatchConfig(out var restored, out string restoreError), restoreError);
            Assert.AreEqual(frozen, restored);
            package.FrozenPlayerHeadRadiusCm = 250;
            Assert.IsFalse(package.TryGetFrozenMatchConfig(out _, out string tamperError));
            StringAssert.Contains("locked at 200 cm", tamperError);
        }

        [Test]
        public void Sink_LateCapturesAfterFreeze_AreIgnored()
        {
            var pkg = new ReplayPackage();
            var sink = new ReplayPackageSink(pkg);
            sink.Freeze();

            int lumenBefore = pkg.Lumens.Count;
            int crashBefore = pkg.Crashes.Count;
            int trailBefore = pkg.Trails.Count;

            // These must be no-ops on the sink (do not throw — the package already threw
            // for direct captures; the sink soft-swallows late arrivals).
            Assert.DoesNotThrow(() => sink.RecordLumen("p1", default, 1.0));
            Assert.DoesNotThrow(() => sink.RecordCrash("p1", default, 1.0, CrashTier.NonLeader, 0));
            Assert.DoesNotThrow(() => sink.RecordTrailSnapshot("p1", new TrailSnapshotPoints(null, 0), 1.0));

            Assert.AreEqual(lumenBefore, pkg.Lumens.Count);
            Assert.AreEqual(crashBefore, pkg.Crashes.Count);
            Assert.AreEqual(trailBefore, pkg.Trails.Count);
        }

        [Test]
        public void Sink_FreezeIsIdempotent()
        {
            var pkg = new ReplayPackage();
            var sink = new ReplayPackageSink(pkg);
            sink.Freeze();
            sink.Freeze(); // second call must be a no-op, not throw

            Assert.IsTrue(sink.IsFinalized);
            Assert.IsTrue(pkg.IsFrozen);
        }

        [Test]
        public void Sink_WarnsWhenProviderNull_ButFinalizes()
        {
            var pkg = new ReplayPackage("m1", DateTime.UtcNow, default);
            var sink = new ReplayPackageSink(pkg);
            // LivePlayerEnumerator set but no provider — should warn but finalize.
            sink.LivePlayerEnumerator = () => new[] { "p1" };
            sink.TrailSnapshotProvider = null;

            Assert.DoesNotThrow(() => sink.Freeze());
            Assert.IsTrue(sink.IsFinalized);
            Assert.AreEqual(0, pkg.Trails.Count, "no provider ⇒ no trail shapes");
        }

        [Test]
        public void Sink_TypedCrashBus_UsesSingleAuthoritativeReplayRecord()
        {
            // PlayerCrashed carries collision identity and position, while MatchManager owns
            // penalty tier/drop calculation and calls RecordCrash with the complete record.
            // The replay observer must not capture the bus signal independently or the one
            // authoritative crash would appear twice.
            var pkg = new ReplayPackage("m1", DateTime.UtcNow, default);
            var sink = new ReplayPackageSink(pkg);
            var crashSite = MetersToGeo(5, 10, 2);
            PlayerCrashEvent observed = default;
            int busEvents = 0;
            Action<PlayerCrashEvent> observer = crash =>
            {
                observed = crash;
                busEvents++;
            };

            GameEvents.PlayerCrashed += observer;
            sink.BindToEventBus();
            try
            {
                GameEvents.RaisePlayerCrashed("p3", "p2", crashSite);

                Assert.AreEqual(1, busEvents, "typed crash event must fire exactly once");
                Assert.AreEqual("p3", observed.CrashedPlayerId);
                Assert.AreEqual("p2", observed.CausedByPlayerId);
                Assert.AreEqual(crashSite, observed.At);
                Assert.AreEqual(0, pkg.Crashes.Count,
                    "the bus signal alone must not create a partial replay duplicate");

                sink.RecordCrash("p3", crashSite, 4.25, CrashTier.Leader, 2);
            }
            finally
            {
                sink.UnbindFromEventBus();
                GameEvents.PlayerCrashed -= observer;
            }

            Assert.AreEqual(1, pkg.Crashes.Count, "one crash must produce one authoritative replay record");
            Assert.AreEqual("p3", pkg.Crashes[0].PlayerId);
            Assert.AreEqual(crashSite, pkg.Crashes[0].At);
            Assert.AreEqual(4.25, pkg.Crashes[0].TimeSeconds, 1e-9);
            Assert.AreEqual(CrashTier.Leader, pkg.Crashes[0].Tier);
            Assert.AreEqual(2, pkg.Crashes[0].LumensDropped);
        }

        [Test]
        public void Sink_EachBusSubscriberRespondsToMatchExpired()
        {
            // Each sink subscribes its own handlers; when MatchExpired fires, every bound
            // sink must freeze its own package. Verifies GameEvents dispatch + unbind.
            var pkg1 = new ReplayPackage("m1", DateTime.UtcNow, default);
            var pkg2 = new ReplayPackage("m2", DateTime.UtcNow, default);
            var sink1 = new ReplayPackageSink(pkg1);
            var sink2 = new ReplayPackageSink(pkg2);

            sink1.BindToEventBus();
            sink2.BindToEventBus();
            try
            {
                GameEvents.RaiseMatchExpired();
            }
            finally
            {
                sink1.UnbindFromEventBus();
                sink2.UnbindFromEventBus();
            }

            Assert.IsTrue(pkg1.IsFrozen, "sink1 should freeze on MatchExpired");
            Assert.IsTrue(pkg2.IsFrozen, "sink2 should freeze on MatchExpired");
        }

        [Test]
        public void Sink_BindIsIdempotent_AndSingleUnbindDetaches()
        {
            var pkg = new ReplayPackage("m-bind", DateTime.UtcNow, default);
            var sink = new ReplayPackageSink(pkg);

            sink.BindToEventBus();
            sink.BindToEventBus();
            sink.UnbindFromEventBus();
            GameEvents.RaiseMatchExpired();

            Assert.IsFalse(pkg.IsFrozen,
                "binding twice must not leave a hidden subscription after one unbind");
        }
    }
}
