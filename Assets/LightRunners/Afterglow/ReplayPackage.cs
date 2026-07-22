using System;
using System.Collections.Generic;
using LightRunners.Core;

namespace LightRunners.Afterglow
{
    // ─── Track F: Afterglow Replay Package ───────────────────────────────────
    // Decision A: completed trails persist as neon world art — Afterglow is the
    // "art" view of one finished match. This is the data model that view reads.
    // Decision T: the frozen tail radius is captured here so the Overview renders
    // the same capsule-chain widths the live match used.
    // Decision U: Overview (ground milestone, decision S) and Walk-Inside share
    // ONE replay package.

    /// <summary>
    /// One player's final trail, captured into a <see cref="ReplayPackage"/> at match
    /// expiry. Mirrors <see cref="TrailSnapshotPoints"/> (Core): flattened
    /// [lat0,lon0,alt0, lat1,lon1,alt1, ...] doubles, with a separate point count.
    /// Storing the snapshot as plain doubles keeps Afterglow free of the Trail
    /// assembly dependency (decision F1 — asmdef references Core only).
    /// </summary>
    [Serializable]
    public sealed class TrailCapture
    {
        /// <summary>Owner player id (matches the match-room player id).</summary>
        public string PlayerId;

        /// <summary>Flattened lat/lon/alt doubles, length = <see cref="PointCount"/> × 3.</summary>
        public double[] Coords;

        /// <summary>Number of points encoded in <see cref="Coords"/>.</summary>
        public int PointCount;

        public TrailCapture() { }

        public TrailCapture(string playerId, double[] coords, int pointCount)
        {
            PlayerId = playerId;
            Coords = coords;
            PointCount = pointCount;
        }

        /// <summary>
        /// Copy from a Core <see cref="TrailSnapshotPoints"/> at finalization time
        /// (the value type Track D hands us via <see cref="IMatchReplaySink.RecordTrailSnapshot"/>).
        /// Takes a defensive copy so the package is self-contained and immutable-by-convention.
        /// </summary>
        public static TrailCapture FromSnapshot(string playerId, in TrailSnapshotPoints points)
        {
            int count = points.PointCount;
            double[] coords;
            if (points.Coords == null || count <= 0)
            {
                coords = Array.Empty<double>();
                count = 0;
            }
            else
            {
                coords = new double[count * 3];
                Array.Copy(points.Coords, 0, coords, 0, Math.Min(coords.Length, points.Coords.Length));
            }
            return new TrailCapture(playerId, coords, count);
        }

        /// <summary>
        /// Decode the flattened <see cref="Coords"/> back into GeoPoints for rendering.
        /// Tolerates a Coords array whose length is not an exact multiple of 3 (truncates).
        /// </summary>
        public List<GeoPoint> ToGeoPoints()
        {
            var list = new List<GeoPoint>(PointCount);
            if (Coords == null) return list;
            int n = Math.Min(PointCount, Coords.Length / 3);
            for (int i = 0; i < n; i++)
            {
                int o = i * 3;
                list.Add(new GeoPoint(Coords[o], Coords[o + 1], Coords[o + 2]));
            }
            return list;
        }
    }

    /// <summary>
    /// One Lumen-collect event in a <see cref="ReplayPackage"/>. Decision E (Lumen tally).
    /// The Overview renders these as small glowing markers at <see cref="At"/>.
    /// </summary>
    [Serializable]
    public sealed class LumenEvent
    {
        public string PlayerId;
        public GeoPoint At;
        public double TimeSeconds;

        public LumenEvent() { }

        public LumenEvent(string playerId, GeoPoint at, double timeSeconds)
        {
            PlayerId = playerId;
            At = at;
            TimeSeconds = timeSeconds;
        }
    }

    /// <summary>
    /// One crash event in a <see cref="ReplayPackage"/>. Decision F (crash penalty tiers).
    /// See the IMatchReplaySink crash-metadata gap note on
    /// <see cref="ReplayPackageSink"/>: full <see cref="Tier"/> and
    /// <see cref="LumensDropped"/> require the proper sink contract (Track D calls it);
    /// the legacy <c>GameEvents.PlayerCrashed</c> bus carries only the player id.
    /// </summary>
    [Serializable]
    public sealed class CrashEvent
    {
        public string PlayerId;
        public GeoPoint At;
        public double TimeSeconds;
        public CrashTier Tier;
        public int LumensDropped;

        public CrashEvent() { }

        public CrashEvent(string playerId, GeoPoint at, double timeSeconds, CrashTier tier, int lumensDropped)
        {
            PlayerId = playerId;
            At = at;
            TimeSeconds = timeSeconds;
            Tier = tier;
            LumensDropped = lumensDropped;
        }
    }

    /// <summary>
    /// Serializable record of one finished match for the Afterglow view (decisions A, U, T).
    ///
    /// Captured incrementally by <see cref="ReplayPackageSink"/> throughout the match and
    /// finalized with <see cref="Freeze"/> on <c>GameEvents.MatchExpired</c>. After freeze,
    /// no further captures are accepted: any mutation throws <see cref="InvalidOperationException"/>.
    ///
    /// Identity decisions (documented for future authors):
    ///   • <see cref="MatchId"/> is a string (networked, human-shareable, matches the
    ///     playerId convention). Track D supplies it; default is a fresh GUID string.
    ///   • <see cref="MatchStartTimeUtc"/> / <see cref="MatchEndTimeUtc"/> are UTC DateTime
    ///     for display and persistence; per-event <c>TimeSeconds</c> on LumenEvent/CrashEvent/
    ///     TrailCapture is the match-relative double the sink receives (clock derived).
    /// </summary>
    [Serializable]
    public sealed class ReplayPackage
    {
        /// <summary>Stable identifier for this match. Default is a GUID string.</summary>
        public string MatchId;

        /// <summary>Wall-clock UTC match start. Set when the sink begins capturing.</summary>
        public DateTime MatchStartTimeUtc;

        /// <summary>Wall-clock UTC match end. Set when the sink freezes.</summary>
        public DateTime MatchEndTimeUtc;

        /// <summary>
        /// The Lightfield origin used for the match. The Overview camera re-anchors to this
        /// point so the replay renders consistently wherever the player replays it (decision U).
        /// </summary>
        public GeoPoint Origin;

        /// <summary>
        /// Authoritative tail radius frozen at countdown (decision T). Width used by the
        /// Overview's neon line strips = <c>FrozenTailRadius × 2</c>. Default 2.0 m until
        /// the sink receives the host value.
        /// </summary>
        public float FrozenTailRadius = FrozenMatchConfig.Default.TailRadiusMeters;

        /// <summary>Locked player collision radius persisted for restore/migration validation.</summary>
        public int FrozenPlayerHeadRadiusCm = FrozenMatchConfig.PlayerHeadRadiusCm;

        /// <summary>Stable hash of the exact frozen collision and clearance contract.</summary>
        public uint FrozenConfigHash = FrozenMatchConfig.Default.Hash;

        /// <summary>One final trail per player, captured at match expiry.</summary>
        public List<TrailCapture> Trails = new List<TrailCapture>();

        /// <summary>Lumen-collect events in capture (insertion) order. Timestamps preserved.</summary>
        public List<LumenEvent> Lumens = new List<LumenEvent>();

        /// <summary>Crash events in capture (insertion) order. Timestamps preserved.</summary>
        public List<CrashEvent> Crashes = new List<CrashEvent>();

        /// <summary>Finish order: index 0 = 1st place (most Lumens). Length = player count.</summary>
        public List<string> FinishOrder = new List<string>();

        /// <summary>True after <see cref="Freeze"/>; captures from the sink are rejected.</summary>
        public bool IsFrozen { get; private set; }

        /// <summary>Construct an empty, mutable package. MatchId defaults to a GUID string.</summary>
        public ReplayPackage()
            : this(Guid.NewGuid().ToString("N"), default, default)
        {
        }

        /// <summary>Construct with explicit identity. Used by tests and Track D wiring.</summary>
        public ReplayPackage(string matchId, DateTime matchStartTimeUtc, DateTime matchEndTimeUtc)
        {
            MatchId = string.IsNullOrEmpty(matchId) ? Guid.NewGuid().ToString("N") : matchId;
            MatchStartTimeUtc = matchStartTimeUtc;
            MatchEndTimeUtc = matchEndTimeUtc;
        }

        // ─── Capture entry points (called by ReplayPackageSink, tests, Track D) ──
        // All gated on !IsFrozen; Freeze() flips IsFrozen and any further call throws.

        public void AddLumen(LumenEvent ev)
        {
            if (IsFrozen) throw Ex();
            Lumens.Add(ev);
        }

        public void AddCrash(CrashEvent ev)
        {
            if (IsFrozen) throw Ex();
            Crashes.Add(ev);
        }

        public void AddTrail(TrailCapture capture)
        {
            if (IsFrozen) throw Ex();
            // Replace any existing trail for the same player (final snapshot wins).
            for (int i = 0; i < Trails.Count; i++)
            {
                if (Trails[i].PlayerId == capture.PlayerId)
                {
                    Trails[i] = capture;
                    return;
                }
            }
            Trails.Add(capture);
        }

        public void SetFinishOrder(List<string> order)
        {
            if (IsFrozen) throw Ex();
            FinishOrder.Clear();
            if (order != null) FinishOrder.AddRange(order);
        }

        public void SetOrigin(GeoPoint origin)
        {
            if (IsFrozen) throw Ex();
            Origin = origin;
        }

        public void SetFrozenTailRadius(float radius)
        {
            if (IsFrozen) throw Ex();
            FrozenTailRadius = radius > 0f ? radius : FrozenTailRadius;
        }

        public void SetFrozenMatchConfig(FrozenMatchConfig config)
        {
            if (IsFrozen) throw Ex();
            FrozenTailRadius = config.TailRadiusMeters;
            FrozenPlayerHeadRadiusCm = FrozenMatchConfig.PlayerHeadRadiusCm;
            FrozenConfigHash = config.Hash;
        }

        /// <summary>Validate that a loaded replay still matches its recorded frozen rules.</summary>
        public bool TryGetFrozenMatchConfig(out FrozenMatchConfig config, out string error)
        {
            int tailRadiusCm = (int)Math.Round(FrozenTailRadius * 100.0, MidpointRounding.AwayFromZero);
            return FrozenMatchConfig.TryRestore(
                tailRadiusCm,
                FrozenPlayerHeadRadiusCm,
                FrozenConfigHash,
                out config,
                out error);
        }

        public void SetMatchEndTime(DateTime endUtc)
        {
            if (IsFrozen) throw Ex();
            MatchEndTimeUtc = endUtc;
        }

        /// <summary>
        /// Finalize the package: no more captures accepted. Idempotent (subsequent calls
        /// are a no-op). Decision A — the package becomes the immutable "art" artifact.
        /// </summary>
        public void Freeze()
        {
            if (IsFrozen) return;
            if (MatchEndTimeUtc == default) MatchEndTimeUtc = DateTime.UtcNow;
            IsFrozen = true;
        }

        private InvalidOperationException Ex()
            => new InvalidOperationException(
                $"ReplayPackage {MatchId} is frozen; further captures are rejected (decision A).");
    }
}
