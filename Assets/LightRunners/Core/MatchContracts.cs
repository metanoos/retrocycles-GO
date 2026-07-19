using System;

namespace LightRunners.Core
{
    // ─── Lightfield Match Contracts ─────────────────────────────────────────
    // The seven interfaces below are the seams the new Lightfield match core
    // exposes to Gameplay (and to parallel implementation tracks). They live in
    // Core so any assembly can consume them without a cycle. Mirrors the existing
    // ILobbyService / IAuthService / IMapProvider / IAltitudeService pattern
    // (interface in Core or its owning subsystem, registered on ServiceLocator,
    // resolved by Gameplay at runtime).
    //
    // Null* implementations live in NullMatchServices.cs; they keep editor-only
    // playmode compiling and running without Fusion/Backend wired up.

    /// <summary>
    /// Opaque identifier for a single Lumen Gate spawn. Decisions G/L/M. Equality
    /// is by value so gates can be keyed in dictionaries across host/client.
    /// </summary>
    public readonly struct GateId : IEquatable<GateId>
    {
        public readonly int Value;
        public GateId(int value) { Value = value; }
        public bool Equals(GateId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is GateId g && Equals(g);
        public override int GetHashCode() => Value;
        public static bool operator ==(GateId a, GateId b) => a.Value == b.Value;
        public static bool operator !=(GateId a, GateId b) => a.Value != b.Value;
        public override string ToString() => $"Gate#{Value}";
    }

    /// <summary>
    /// Match FSM front (decision P — new Lightfield match core). Implemented by
    /// MatchManager (Gameplay) on top of the existing GameState. The match is a
    /// strictly layered sub-FSM: <see cref="MatchState.Idle"/> whenever no match
    /// is active, otherwise one of Warmup/Countdown/Live/Scoring/Expired.
    /// </summary>
    public interface IMatchSession
    {
        MatchState State { get; }
        /// <summary>Seconds remaining on the live clock; meaningless outside Live/Scoring.</summary>
        float TimeRemaining { get; }
        /// <summary>True if this client is the authoritative match host (decision Q).</summary>
        bool IsHostAuthority { get; }
        /// <summary> Fires on any MatchState transition (also re-raised on GameEvents.MatchStateChanged).</summary>
        event Action<MatchState, MatchState> StateChanged;

        void BeginMatch();
        void EndMatch();
    }

    /// <summary>
    /// Authoritative Lumen tally for a match (decisions E, F, I). Decisions E/F
    /// replace the deprecated RunScorer float-score with an integer Lumen count
    /// accrued live during the match.
    /// </summary>
    public interface ILumenScoreboard
    {
        int GetLumens(string playerId);
        /// <summary>Current leader's player id, or null/empty if no players or tied at zero.</summary>
        string LeaderPlayerId { get; }
        event Action<string, int> LumensChanged;       // (playerId, newTotal)
        event Action<string> LeaderChanged;            // (newLeaderId | null as empty string)

        /// <summary>Award one Lumen (a Gate touch). Returns the player's new total.</summary>
        int Award(string playerId);

        /// <summary>
        /// Apply crash penalty (decision F). Tier is host-authoritative: leader loses
        /// <c>crashLumenLossLeader</c>, anyone else loses <c>crashLumenLossNonLeader</c>;
        /// both capped by held score. Returns the actual amount dropped (>= 0).
        /// </summary>
        int ApplyCrashPenalty(string playerId);

        /// <summary>Tier for crash-penalty purposes (decision F).</summary>
        CrashTier GetCrashTier(string playerId);
    }

    /// <summary>
    /// Authoritative Gate spawn/collect lifecycle (decisions G, L, M, R). Implemented
    /// host-side; clients observe via the events. <see cref="PlayerRole.Referee"/>
    /// may call <see cref="PlaceBonusGate"/> when decision R is implemented.
    /// </summary>
    public interface IGateDirector
    {
        int ActiveGateCount { get; }
        event Action<GateId, GeoPoint, GatePlacement> GateSpawned;
        event Action<GateId> GateDespawned;
        event Action<GateId, string> GateCollected;    // (gateId, collectorPlayerId)

        /// <summary>Initialize the gate pool to max(1, ceil(playerCount × gatesPerPlayer)). Decision M.</summary>
        void ConfigureForPlayers(int playerCount, float gatesPerPlayer);

        /// <summary>
        /// Decision R — referee-only. Validates the caller's role/token host-side;
        /// no-op if the caller isn't an authorized referee. v2 (full Gate-Director
        /// UI) is deferred per decision S.
        /// </summary>
        void PlaceBonusGate(GeoPoint at, GatePlacement placement, string refereeToken);
    }

    /// <summary>
    /// The Lightfield play volume (decision K). Ground-only milestone (decision S)
    /// models this as a circular disc with a hard altitude ceiling; the aerial
    /// milestone replaces <see cref="IsInside"/> with true hemispherical dome math.
    /// </summary>
    public interface ILightfieldVolume
    {
        /// <summary>Match origin in geo coordinates; the disc/dome is centered here.</summary>
        GeoPoint Origin { get; }
        bool IsInside(GeoPoint point);
        /// <summary>Raised host-side when a runner crosses the boundary; idempotent per crossing.</summary>
        event Action<string> BoundaryViolated;        // playerId
    }

    /// <summary>
    /// Networking transport seam (decision Q). Replaces GameManager's prior direct
    /// FusionLauncher coupling with a locator-resolved interface so the Multiplayer
    /// assembly can be reworked (Shared→Host Mode) without touching Gameplay.
    /// </summary>
    public interface IMatchTransport
    {
        bool IsConnected { get; }
        event Action<bool> ConnectionChanged;         // true = connected to match room

        void ConnectMatch(string roomId, string localPlayerId);
        void Disconnect();
    }

    /// <summary>
    /// Observer that records match events into a replay package for Afterglow
    /// (decision U). Implementations are write-only; the Afterglow view reads the
    /// captured package back. Ground-only milestone ships Overview only (decision S).
    /// </summary>
    public interface IMatchReplaySink
    {
        void RecordLumen(string playerId, GeoPoint at, double matchTimeSeconds);
        void RecordCrash(string playerId, GeoPoint at, double matchTimeSeconds, CrashTier tier, int lumensDropped);
        void RecordTrailSnapshot(string playerId, in TrailSnapshotPoints points, double matchTimeSeconds);
    }

    /// <summary>
    /// Lightweight view over a player's final trail for replay capture. Avoids a
    /// hard dependency from Core on the Trail assembly by passing the snapshot as
    /// a primitive array; Trail-side code fills it from <c>TrailData</c>.
    /// </summary>
    public readonly struct TrailSnapshotPoints
    {
        /// <summary>Flattened [lat0,lon0,alt0, lat1,lon1,alt1, ...].</summary>
        public readonly double[] Coords;
        public readonly int PointCount;
        public TrailSnapshotPoints(double[] coords, int pointCount) { Coords = coords; PointCount = pointCount; }
    }

    /// <summary>
    /// Tail-radius authority (decision T). The host-selected tail radius is
    /// authoritative, frozen at countdown, preserved in Afterglow, and all
    /// head-to-trail collision and safety clearances derive from it.
    /// </summary>
    public interface ITailAuthority
    {
        /// <summary>Selected tail radius (m). Returns the host value once frozen, else the config default.</summary>
        float FrozenTailRadius { get; }
        bool IsFrozen { get; }
        /// <summary>Host-only: freeze the radius at its current value. No-op once frozen.</summary>
        void FreezeAtCountdown();
        /// <summary>Reset to unfrozen (called between matches).</summary>
        void Unfreeze();
    }
}