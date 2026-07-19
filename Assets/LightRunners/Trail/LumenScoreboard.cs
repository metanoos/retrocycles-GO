using System;
using System.Collections.Generic;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// A Lumen dropped on crash and briefly stealable. Decision F: dropped Lumens
    /// become pickups at the crash site for <see cref="GameConfig.stolenLumenPickupSeconds"/>
    /// seconds; the gameplay layer (Track B's <c>StolenLumenPickup</c>) drains the queue and
    /// renders + collects them.
    ///
    /// Track A owns the authoritative queue (see <see cref="LumenScoreboard"/>). This struct's
    /// shape mirrors the consumer-side record declared in
    /// <c>LightRunners.Lightfield.StolenLumenRecord</c> (PlayerId / At / LumensDropped /
    /// MatchTimeSeconds) plus the consumer-readable expiry fields, so Track B can adapt with a
    /// trivial conversion (or, in the integration phase, both can be unified into Core).
    /// Cross-track coordination note: see the report at the end of Track A's branch.
    /// </summary>
    [Serializable]
    public readonly struct StolenLumenRecord : IEquatable<StolenLumenRecord>
    {
        /// <summary>The player whose crash dropped the Lumens.</summary>
        public readonly string PlayerId;
        /// <summary>Where the crash happened (pickup spawns here).</summary>
        public readonly GeoPoint At;
        /// <summary>Lumens dropped (capped by held score, tier-scaled per decision F). Always &gt; 0 for a real record.</summary>
        public readonly int LumensDropped;
        /// <summary>Match time of the crash, for ordering / expiry.</summary>
        public readonly double MatchTimeSeconds;
        /// <summary>Lifetime (s). Cached from <see cref="GameConfig.stolenLumenPickupSeconds"/> at drop time so a later config tweak doesn't change pickups already in flight.</summary>
        public readonly float LifetimeSeconds;

        public StolenLumenRecord(string playerId, GeoPoint at, int lumensDropped, double matchTimeSeconds, float lifetimeSeconds)
        {
            PlayerId = playerId;
            At = at;
            LumensDropped = lumensDropped;
            MatchTimeSeconds = matchTimeSeconds;
            LifetimeSeconds = lifetimeSeconds;
        }

        /// <summary>Expiry match time = <see cref="MatchTimeSeconds"/> + <see cref="LifetimeSeconds"/>.</summary>
        public double ExpiresAtSeconds => MatchTimeSeconds + LifetimeSeconds;

        /// <summary>True when populated with a real drop. Empty/null records are invalid.</summary>
        public bool IsValid => !string.IsNullOrEmpty(PlayerId) && LumensDropped > 0;

        public bool Equals(StolenLumenRecord other)
            => PlayerId == other.PlayerId
               && At.Equals(other.At)
               && LumensDropped == other.LumensDropped
               && MatchTimeSeconds.Equals(other.MatchTimeSeconds)
               && LifetimeSeconds.Equals(other.LifetimeSeconds);

        public override bool Equals(object obj) => obj is StolenLumenRecord r && Equals(r);
        public override int GetHashCode()
            => (PlayerId, At, LumensDropped, MatchTimeSeconds, LifetimeSeconds).GetHashCode();
        public static bool operator ==(StolenLumenRecord a, StolenLumenRecord b) => a.Equals(b);
        public static bool operator !=(StolenLumenRecord a, StolenLumenRecord b) => !a.Equals(b);

        public override string ToString()
            => $"Stolen[{PlayerId}] -{LumensDropped}L @ {At} t={MatchTimeSeconds:F1}s life={LifetimeSeconds:F1}s";
    }

    /// <summary>
    /// Authoritative integer Lumen tally for a match (decisions E, F, I). Implements
    /// <see cref="ILumenScoreboard"/>. Pure C# — Track D (Gameplay/MatchManager) constructs and
    /// registers it on the <see cref="ServiceLocator"/>, overwriting the
    /// <see cref="NullLumenScoreboard"/> installed by PlatformServiceRegistry.
    ///
    /// Invariants (SPEC §16 crash pipeline + decision F):
    ///  • One Lumen per Gate touch (decision E). Integer tally, never float.
    ///  • Crash penalty is tier-scaled (leader = <see cref="GameConfig.crashLumenLossLeader"/>,
    ///    non-leader = <see cref="GameConfig.crashLumenLossNonLeader"/>) and CAPPED by held
    ///    score so a player never goes negative.
    ///  • Leader is the player with the max Lumens. Ties → no leader (everyone NonLeader). Zero
    ///    players or all-zero scores → empty string.
    ///  • Every change raises <see cref="GameEvents.RaiseLumensChanged"/>; a leader change also
    ///    raises <see cref="GameEvents.RaiseLeaderChanged"/>.
    ///  • Dropped Lumens enqueue a <see cref="StolenLumenRecord"/> (decision F); the gameplay
    ///    layer drains <see cref="StolenLumenQueue"/> to render stealable pickups.
    ///
    /// This class is NOT thread-safe; it lives on the main thread inside the match host.
    /// </summary>
    public sealed class LumenScoreboard : ILumenScoreboard
    {
        private readonly Dictionary<string, int> _lumens = new Dictionary<string, int>();
        private readonly Queue<StolenLumenRecord> _stolenLumenQueue = new Queue<StolenLumenRecord>();
        private readonly Func<double> _matchClockSeconds;

        private string _leaderId = string.Empty;

        /// <summary>
        /// Construct a scoreboard. <paramref name="matchClockSeconds"/> supplies the live match
        /// time stamped on each <see cref="StolenLumenRecord"/>; null → 0.0 (use only in tests).
        /// Injecting the clock keeps this class pure-C# and unit-testable without Time.time.
        /// </summary>
        public LumenScoreboard(Func<double> matchClockSeconds = null)
        {
            _matchClockSeconds = matchClockSeconds;
        }

        // ─── ILumenScoreboard ───────────────────────────────────────────────

        /// <summary>Current leader's player id, or empty string if zero players or tied (incl. tied at zero).</summary>
        public string LeaderPlayerId => _leaderId;

        /// <summary>Raised on every Lumen change. (playerId, newTotal).</summary>
        public event Action<string, int> LumensChanged;

        /// <summary>Raised when the leader id changes (incl. to/from empty on tie). newLeaderId is "" for "no leader".</summary>
        public event Action<string> LeaderChanged;

        /// <summary>Lumens currently held by <paramref name="playerId"/> (0 if unknown).</summary>
        public int GetLumens(string playerId)
        {
            return _lumens.TryGetValue(playerId, out int v) ? v : 0;
        }

        /// <summary>
        /// Award one Lumen (decision E: one Lumen per Gate touch). Returns the player's new
        /// total. Fires <see cref="LumensChanged"/> and, if the leader changed,
        /// <see cref="LeaderChanged"/>. Also mirrors both onto <see cref="GameEvents"/> so
        /// non-Gameplay assemblies (UI/AR/Multiplayer) react without a cycle.
        /// </summary>
        public int Award(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return 0;

            _lumens.TryGetValue(playerId, out int current);
            int next = current + 1;
            _lumens[playerId] = next;

            RaiseLumensChanged(playerId, next);
            RecomputeLeader();
            return next;
        }

        /// <summary>
        /// Apply crash penalty (decision F). Tier is host-authoritative: the current leader loses
        /// <see cref="GameConfig.crashLumenLossLeader"/>, anyone else loses
        /// <see cref="GameConfig.crashLumenLossNonLeader"/>; the loss is capped by held score so
        /// the tally never goes negative. Each dropped Lumen becomes a stealable
        /// <see cref="StolenLumenRecord"/> at <paramref name="at"/> (decision F). Returns the
        /// actual amount dropped (&gt;= 0).
        /// </summary>
        /// <param name="playerId">The crashing player.</param>
        /// <param name="at">Crash site (the pickup spawns here). Defaults to <see cref="GeoPoint"/> zero if omitted.</param>
        public int ApplyCrashPenalty(string playerId, GeoPoint at = default)
        {
            if (string.IsNullOrEmpty(playerId)) return 0;

            CrashTier tier = GetCrashTier(playerId);
            GameConfig cfg = GameConfig.Active;
            int loss = tier == CrashTier.Leader
                ? Math.Max(0, cfg.crashLumenLossLeader)
                : Math.Max(0, cfg.crashLumenLossNonLeader);

            _lumens.TryGetValue(playerId, out int held);
            int actualDropped = Math.Min(loss, Math.Max(0, held));
            if (actualDropped <= 0)
            {
                // Nothing to drop — still recompute (a tied competitor may now lead).
                RecomputeLeader();
                return 0;
            }

            _lumens[playerId] = held - actualDropped;

            // Decision F: drop the lost Lumens as a stealable pickup record at the crash site.
            // Lifetime is snapshotted from config so a mid-flight config tweak doesn't change
            // pickups already on the ground.
            double matchTime = CurrentMatchTimeSeconds();
            var record = new StolenLumenRecord(
                playerId,
                at,
                actualDropped,
                matchTime,
                Math.Max(1f, cfg.stolenLumenPickupSeconds));
            _stolenLumenQueue.Enqueue(record);

            RaiseLumensChanged(playerId, _lumens[playerId]);
            RecomputeLeader();
            return actualDropped;
        }

        /// <summary>
        /// Crash-penalty tier for <paramref name="playerId"/> (decision F). The leader is the
        /// player with the strict maximum Lumen count. Ties (two or more players sharing the max)
        /// → no leader, so everyone is treated as NonLeader. A player with zero Lumens who happens
        /// to be the only entry is NOT the leader.
        /// </summary>
        public CrashTier GetCrashTier(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return CrashTier.NonLeader;

            // Leader is the unique strict max holder (and only if max > 0).
            string leader = ComputeLeaderId();
            if (string.IsNullOrEmpty(leader)) return CrashTier.NonLeader;
            return leader == playerId ? CrashTier.Leader : CrashTier.NonLeader;
        }

        /// <summary>
        /// Players and their Lumen totals ordered by Lumens descending. Ties are stable
        /// (insertion order via <c>OrderByDescending</c> which is stable in LINQ-to-Objects).
        /// Phase 0.5 widening — used by RunSummaryUI for finish-rank and Afterglow for
        /// FinishOrder. Players with zero Lumens are included so a full roster is visible.
        /// </summary>
        public IEnumerable<(string playerId, int lumens)> OrderedStandings
        {
            get
            {
                // Materialize once so repeated enumeration is safe and consistent.
                var snapshot = new List<(string playerId, int lumens)>(_lumens.Count);
                foreach (var kvp in _lumens)
                    snapshot.Add((kvp.Key, kvp.Value));
                snapshot.Sort((a, b) => b.lumens.CompareTo(a.lumens));
                return snapshot;
            }
        }

        // ─── Stolen-Lumen pickup queue (Track B consumer drains this) ───────

        /// <summary>Read-only peek at the undrained stolen-Lumen pickup queue.</summary>
        public IReadOnlyCollection<StolenLumenRecord> StolenLumenQueue => _stolenLumenQueue;

        /// <summary>Number of undrained stolen-Lumen records.</summary>
        public int StolenLumenCount => _stolenLumenQueue.Count;

        /// <summary>
        /// Dequeue and return one stolen-Lumen record, or a default (IsValid == false) if the
        /// queue is empty. Track B's consumer calls this in a loop to spawn pickups.
        /// </summary>
        public bool TryDequeueStolenLumen(out StolenLumenRecord record)
        {
            if (_stolenLumenQueue.Count == 0)
            {
                record = default;
                return false;
            }
            record = _stolenLumenQueue.Dequeue();
            return true;
        }

        /// <summary>Clear the stolen-Lumen queue (e.g. on match reset).</summary>
        public void ClearStolenLumens() => _stolenLumenQueue.Clear();

        // ─── Reset / teardown ───────────────────────────────────────────────

        /// <summary>Wipe the tally and the pickup queue (call between matches).</summary>
        public void Reset()
        {
            _lumens.Clear();
            _stolenLumenQueue.Clear();
            _leaderId = string.Empty;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────────────

        private double CurrentMatchTimeSeconds()
            => _matchClockSeconds?.Invoke() ?? 0.0;

        /// <summary>
        /// Recompute the leader from the live tally. Ties at the max → no leader (empty string).
        /// Empty dictionary or all-zero → no leader. Fires <see cref="LeaderChanged"/> +
        /// <see cref="GameEvents.RaiseLeaderChanged"/> only when the id actually changes.
        /// </summary>
        private void RecomputeLeader()
        {
            string newLeader = ComputeLeaderId();
            if (newLeader == _leaderId) return;
            _leaderId = newLeader;
            try { LeaderChanged?.Invoke(_leaderId); } catch { /* never let a subscriber kill the host */ }
            GameEvents.RaiseLeaderChanged(_leaderId);
        }

        /// <summary>
        /// Pure leader computation. Returns the unique player with the strict-max Lumens (max must
        /// be &gt; 0), or empty string if: no players, all zero, or a tie at the max.
        /// </summary>
        private string ComputeLeaderId()
        {
            if (_lumens.Count == 0) return string.Empty;

            string leader = string.Empty;
            int best = 0;
            bool tied = false;
            foreach (var kvp in _lumens)
            {
                int v = kvp.Value;
                if (v > best)
                {
                    best = v;
                    leader = kvp.Key;
                    tied = false;
                }
                else if (v == best && v > 0)
                {
                    // Two players sharing the strict max → tie.
                    tied = true;
                }
            }

            if (best <= 0) return string.Empty;       // all-zero → no leader
            if (tied) return string.Empty;            // tie at the max → no leader
            return leader;
        }

        private void RaiseLumensChanged(string playerId, int newTotal)
        {
            try { LumensChanged?.Invoke(playerId, newTotal); } catch { /* never let a subscriber kill the host */ }
            GameEvents.RaiseLumensChanged(playerId, newTotal);
        }
    }
}
