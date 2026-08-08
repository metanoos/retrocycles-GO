using System;
using System.Collections.Generic;
using NUnit.Framework;
using LightRunners.Core;
using LightRunners.Lightfield;

namespace LightRunners.Lightfield.Tests
{
    /// <summary>
    /// Decision M (gate density), decision R (referee bonus). Verifies that
    /// <see cref="GateSpawner.ConfigureForPlayers"/> spawns the formula-correct count, that
    /// collect-one-respawns-one preserves the active count (density gates are replaced), and
    /// that <see cref="GateSpawner.PlaceBonusGate"/> adds above the density cap and is NOT
    /// replaced on collection.
    ///
    /// Uses a fake volume (so the test never touches <c>GameConfig.Active</c>) and a deterministic
    /// sampler (fixed-point queue) so counts are exact.
    /// </summary>
    public class GateSpawnerTests
    {
        private sealed class FakeVolume : ILightfieldVolume
        {
            public GeoPoint Origin { get; set; } = new GeoPoint(37.0, -122.0, 0);
            public event Action<string> BoundaryViolated { add { } remove { } }
            public bool IsInside(GeoPoint point) => true; // every sample is in-bounds
            public void CheckPlayer(string playerId, GeoPoint point) { }
            public void ForgetPlayer(string playerId) { }
            public void Clear() { }
        }

        /// <summary>
        /// Returns queued points in order, then the origin forever. Lets the test assert exact
        /// spawn positions if needed; here we just want determinism.
        /// </summary>
        private sealed class QueuedSampler : IGatePositionSampler
        {
            private readonly Queue<GeoPoint> _queue = new Queue<GeoPoint>();
            public void Enqueue(GeoPoint p) => _queue.Enqueue(p);
            public GeoPoint SampleInside(ILightfieldVolume volume)
                => _queue.Count > 0 ? _queue.Dequeue() : volume.Origin;
        }

        private FakeVolume _volume;
        private QueuedSampler _sampler;
        private GateSpawner _spawner;

        [SetUp]
        public void SetUp()
        {
            _volume = new FakeVolume();
            _sampler = new QueuedSampler();
            _spawner = new GateSpawner(_volume, _sampler);
        }

        [TearDown]
        public void TearDown()
        {
            _spawner?.Dispose();
            GameEvents.ClearSubscribersForTests();
        }

        // ── ConfigureForPlayers spawns the formula-correct count ────────────
        [Test]
        public void ConfigureForPlayers_DefaultHalf_SpawnsExpectedCount()
        {
            _spawner.ConfigureForPlayers(playerCount: 5, gatesPerPlayer: 0.5f);
            // ceil(5 × 0.5) = ceil(2.5) = 3
            Assert.AreEqual(3, _spawner.ActiveGateCount);
        }

        [Test]
        public void ConfigureForPlayers_TwoPlayers_SpawnsOne()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            // ceil(2 × 0.5) = 1
            Assert.AreEqual(1, _spawner.ActiveGateCount);
        }

        [Test]
        public void ConfigureForPlayers_FiresSpawnedPerGate()
        {
            int spawned = 0;
            _spawner.GateSpawned += (id, p, pl) => spawned++;
            _spawner.ConfigureForPlayers(playerCount: 7, gatesPerPlayer: 0.5f);
            // ceil(7 × 0.5) = 4
            Assert.AreEqual(4, spawned);
        }

        [Test]
        public void ConfigureForPlayers_Twice_ReplacesPool()
        {
            _spawner.ConfigureForPlayers(playerCount: 10, gatesPerPlayer: 0.5f);
            int firstCount = _spawner.ActiveGateCount;
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            Assert.Less(_spawner.ActiveGateCount, firstCount);
            Assert.AreEqual(1, _spawner.ActiveGateCount);
        }

        // ── Collect-one-respawns-one preserves density ─────────────────────
        [Test]
        public void CollectGate_OnDensityGate_RespawnsOneAndPreservesCount()
        {
            _spawner.ConfigureForPlayers(playerCount: 4, gatesPerPlayer: 0.5f);
            int initial = _spawner.ActiveGateCount;
            int spawnedAfter = 0;
            _spawner.GateSpawned += (id, p, pl) => spawnedAfter++;

            // Take any active id; collect it.
            var firstGate = FirstActiveGateId(_spawner);
            _spawner.CollectGate(firstGate, "p2");

            Assert.AreEqual(initial, _spawner.ActiveGateCount, "density count must be preserved after a collect");
            Assert.AreEqual(1, spawnedAfter, "exactly one new gate must spawn to replace the collected one");
        }

        [Test]
        public void CollectGate_FiresCollectedEvent()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            GateId? seen = null;
            string collector = null;
            _spawner.GateCollected += (id, c) => { seen = id; collector = c; };

            var firstGate = FirstActiveGateId(_spawner);
            _spawner.CollectGate(firstGate, "p3");

            Assert.AreEqual(firstGate, seen);
            Assert.AreEqual("p3", collector);
        }

        [Test]
        public void CollectGate_FiresDespawnedThenSpawned()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            var order = new List<string>();
            _spawner.GateDespawned += id => order.Add("despawned");
            _spawner.GateSpawned += (id, p, pl) => order.Add("spawned");

            var firstGate = FirstActiveGateId(_spawner);
            _spawner.CollectGate(firstGate, "p2");

            Assert.AreEqual(new[] { "despawned", "spawned" }, order.ToArray());
        }

        [Test]
        public void CollectGate_UnknownId_NoOp()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            int before = _spawner.ActiveGateCount;
            _spawner.CollectGate(new GateId(999_999), "p2"); // not in pool
            Assert.AreEqual(before, _spawner.ActiveGateCount);
        }

        // ── PlaceBonusGate adds above the density cap (decision R) ──────────
        [Test]
        public void PlaceBonusGate_AddsAboveDensityCap()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            int density = _spawner.ActiveGateCount;
            Assert.AreEqual(0, _spawner.ActiveBonusGateCount, "no bonus gates before PlaceBonusGate");

            _spawner.PlaceBonusGate(new GeoPoint(37.001, -122.001, 0), GatePlacement.Ground, "ref-token-1");

            // Round-2 fix R2-F8: ActiveGateCount is DENSITY-ONLY per the IGateDirector contract
            // (Round-1 fix R1-F10). A bonus gate must NOT inflate ActiveGateCount; it must show
            // up in ActiveBonusGateCount. The prior test asserted the OLD combined-count behavior
            // and would have passed against a regression that re-combined them.
            Assert.AreEqual(density, _spawner.ActiveGateCount, "bonus gate must NOT inflate density count");
            Assert.AreEqual(1, _spawner.ActiveBonusGateCount, "bonus gate must show in ActiveBonusGateCount");
        }

        [Test]
        public void PlaceBonusGate_EmptyToken_Rejected()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            int before = _spawner.ActiveGateCount;

            _spawner.PlaceBonusGate(new GeoPoint(37.001, -122.001, 0), GatePlacement.Ground, "");

            Assert.AreEqual(before, _spawner.ActiveGateCount, "empty referee token must be rejected (Track C will validate the real token)");
        }

        [Test]
        public void PlaceBonusGate_NullToken_Rejected()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            int before = _spawner.ActiveGateCount;

            _spawner.PlaceBonusGate(new GeoPoint(37.001, -122.001, 0), GatePlacement.Ground, null);

            Assert.AreEqual(before, _spawner.ActiveGateCount);
        }

        [Test]
        public void PlaceBonusGate_NotReplacedOnCollect()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            int density = _spawner.ActiveGateCount;
            _spawner.PlaceBonusGate(new GeoPoint(37.001, -122.001, 0), GatePlacement.Ground, "ref-token-1");
            Assert.AreEqual(1, _spawner.ActiveBonusGateCount, "bonus gate active");

            // Find the bonus gate (its id is >= BonusGateIdBase).
            GateId bonus = default;
            bool found = false;
            foreach (var s in _spawner.ActiveGates)
            {
                if (s.IsBonus) { bonus = s.Id; found = true; break; }
            }
            Assert.IsTrue(found, "bonus gate must be tracked as active");
            _spawner.CollectGate(bonus, "p2");

            // Round-2 fix R2-F8: bonus gate is one-shot (no respawn), so ActiveBonusGateCount
            // drops to 0; density count is UNCHANGED (the bonus collect doesn't trigger respawn).
            Assert.AreEqual(0, _spawner.ActiveBonusGateCount, "bonus gate is one-shot — gone after collect");
            Assert.AreEqual(density, _spawner.ActiveGateCount, "density count unchanged by bonus collect");
        }

        /// <summary>
        /// Round-2 fix R2-F8: pin the density-respawn behavior separately from the bonus one-shot.
        /// A density-gate collect must preserve ActiveGateCount (respawn) and leave
        /// ActiveBonusGateCount untouched.
        /// </summary>
        [Test]
        public void DensityGate_RespawnsOnCollect_BonusUntouched()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            int density = _spawner.ActiveGateCount; // = max(1, ceil(2*0.5)) = 1
            _spawner.PlaceBonusGate(new GeoPoint(37.001, -122.001, 0), GatePlacement.Ground, "ref-token-1");
            Assert.AreEqual(1, _spawner.ActiveBonusGateCount);

            // Collect a DENSITY gate (first non-bonus).
            GateId densityGate = default;
            foreach (var s in _spawner.ActiveGates)
            {
                if (!s.IsBonus) { densityGate = s.Id; break; }
            }
            _spawner.CollectGate(densityGate, "p1");

            Assert.AreEqual(density, _spawner.ActiveGateCount, "density gate respawns — count preserved");
            Assert.AreEqual(1, _spawner.ActiveBonusGateCount, "bonus unaffected by density collect");
        }

        // ── Bonus vs density id ranges don't collide ───────────────────────
        [Test]
        public void BonusIds_AreAboveDensityIds()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            _spawner.PlaceBonusGate(new GeoPoint(0, 0, 0), GatePlacement.Ground, "tok");

            int maxDensity = -1, minBonus = int.MaxValue;
            foreach (var s in _spawner.ActiveGates)
            {
                if (s.IsBonus) minBonus = Math.Min(minBonus, s.Id.Value);
                else maxDensity = Math.Max(maxDensity, s.Id.Value);
            }
            Assert.Greater(minBonus, maxDensity, "bonus ids must be partitioned above density ids");
            Assert.GreaterOrEqual(minBonus, GateSpawner.BonusGateIdBase);
        }

        // ── Accepted collection is the only static score/replay signal ─────
        [Test]
        public void TryCollectGate_Accepted_RaisesStaticBusOnce()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            int accepted = 0;
            GateId observed = default;
            GameEvents.GateCollected += (id, collector, at) =>
            {
                accepted++;
                observed = new GateId(id);
            };

            var first = FirstActiveGateId(_spawner);
            Assert.IsTrue(_spawner.TryCollectGate(first, "p-collector"));

            Assert.AreEqual(1, accepted);
            Assert.AreEqual(first, observed);
        }

        [Test]
        public void TryCollectGate_StaleId_DoesNotRaiseAcceptedBus()
        {
            _spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
            int accepted = 0;
            GameEvents.GateCollected += (id, collector, at) => accepted++;

            var first = FirstActiveGateId(_spawner);
            Assert.IsTrue(_spawner.TryCollectGate(first, "p-collector"));
            Assert.IsFalse(_spawner.TryCollectGate(first, "p-collector"),
                "the destroyed visual's stale id must be rejected");

            Assert.AreEqual(1, accepted, "only the first authoritative consumption is accepted");
        }

        // ── Sampler fallback when out-of-bounds ────────────────────────────
        [Test]
        public void Sampler_OutOfBoundsPoints_FallsBackToOrigin()
        {
            // Volume that rejects every sample (so the spawner exhausts its retry budget and
            // falls back to the volume origin).
            var rejectingVolume = new AlwaysOutsideVolume();
            var rejecting = new FixedPointSampler();
            var spawner = new GateSpawner(rejectingVolume, rejecting);
            try
            {
                spawner.ConfigureForPlayers(playerCount: 2, gatesPerPlayer: 0.5f);
                // Still spawns (count is preserved) using the fallback to the origin.
                Assert.AreEqual(1, spawner.ActiveGateCount);
            }
            finally { spawner.Dispose(); }
        }

        private sealed class AlwaysOutsideVolume : ILightfieldVolume
        {
            public GeoPoint Origin { get; set; } = new GeoPoint(37.0, -122.0, 0);
            public event Action<string> BoundaryViolated { add { } remove { } }
            public bool IsInside(GeoPoint point) => false; // sampler never succeeds
            public void CheckPlayer(string playerId, GeoPoint point) { }
            public void ForgetPlayer(string playerId) { }
            public void Clear() { }
        }

        private sealed class FixedPointSampler : IGatePositionSampler
        {
            public GeoPoint SampleInside(ILightfieldVolume volume) => new GeoPoint(99, 99, 9999);
        }

        // ── Helpers ────────────────────────────────────────────────────────
        private static GateId FirstActiveGateId(GateSpawner spawner)
        {
            foreach (var s in spawner.ActiveGates) return s.Id;
            Assert.Fail("no active gates");
            return default;
        }
    }
}
