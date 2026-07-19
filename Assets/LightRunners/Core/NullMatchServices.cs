using System;

namespace LightRunners.Core
{
    // ─── Null / offline implementations of the Lightfield match contracts ────
    // Mirrors the NullLobbyService pattern (Backend/LobbyServices.cs): registered
    // by PlatformServiceRegistry when no live implementation is available, so
    // editor-only playmode compiles and runs end-to-end without Fusion/Backend.
    //
    // Each Null* implementation is a no-op that never fires events and returns
    // the most inert value available (0, false, empty string). Real impls live
    // in their owning tracks:
    //   - LumenScoreboard        → LightRunners.Trail       (Track A)
    //   - GateSpawner            → LightRunners.Lightfield  (Track B)
    //   - LightfieldVolume       → LightRunners.Lightfield  (Track B)
    //   - FusionLauncher         → LightRunners.Multiplayer (Track C, FUSION_WEAVER)
    //   - MatchManager           → LightRunners.Gameplay    (Track D)
    //   - ReplayPackage sink     → LightRunners.Afterglow   (Track F)

    /// <summary>No-op <see cref="IMatchSession"/>: never leaves <see cref="MatchState.Idle"/>.</summary>
    public sealed class NullMatchSession : IMatchSession
    {
        public MatchState State => MatchState.Idle;
        public float TimeRemaining => 0f;
        public bool IsHostAuthority => false;
        public event Action<MatchState, MatchState> StateChanged { add { } remove { } }
        public void BeginMatch() { }
        public void EndMatch() { }
    }

    /// <summary>No-op <see cref="ILumenScoreboard"/>: everyone has zero Lumens, no leader.</summary>
    public sealed class NullLumenScoreboard : ILumenScoreboard
    {
        public string LeaderPlayerId => string.Empty;
        public event Action<string, int> LumensChanged { add { } remove { } }
        public event Action<string> LeaderChanged { add { } remove { } }
        public int GetLumens(string playerId) => 0;
        public int Award(string playerId) => 0;
        public int ApplyCrashPenalty(string playerId) => 0;
        public CrashTier GetCrashTier(string playerId) => CrashTier.NonLeader;
    }

    /// <summary>No-op <see cref="IGateDirector"/>: spawns/collects nothing.</summary>
    public sealed class NullGateDirector : IGateDirector
    {
        public int ActiveGateCount => 0;
        public event Action<GateId, GeoPoint, GatePlacement> GateSpawned { add { } remove { } }
        public event Action<GateId> GateDespawned { add { } remove { } }
        public event Action<GateId, string> GateCollected { add { } remove { } }
        public void ConfigureForPlayers(int playerCount, float gatesPerPlayer) { }
        public void PlaceBonusGate(GeoPoint at, GatePlacement placement, string refereeToken) { }
    }

    /// <summary>
    /// No-op <see cref="ILightfieldVolume"/>: every point is "inside", no boundary
    /// violations. Origin is <see cref="GeoPoint.Zero"/>.
    /// </summary>
    public sealed class NullLightfieldVolume : ILightfieldVolume
    {
        public GeoPoint Origin => default;
        public event Action<string> BoundaryViolated { add { } remove { } }
        public bool IsInside(GeoPoint point) => true;
    }

    /// <summary>No-op <see cref="IMatchTransport"/>: never connects.</summary>
    public sealed class NullMatchTransport : IMatchTransport
    {
        public bool IsConnected => false;
        public event Action<bool> ConnectionChanged { add { } remove { } }
        public void ConnectMatch(string roomId, string localPlayerId) { }
        public void Disconnect() { }
    }

    /// <summary>No-op <see cref="IMatchReplaySink"/>: discards all recorded events.</summary>
    public sealed class NullMatchReplaySink : IMatchReplaySink
    {
        public void RecordLumen(string playerId, GeoPoint at, double matchTimeSeconds) { }
        public void RecordCrash(string playerId, GeoPoint at, double matchTimeSeconds, CrashTier tier, int lumensDropped) { }
        public void RecordTrailSnapshot(string playerId, in TrailSnapshotPoints points, double matchTimeSeconds) { }
    }

    /// <summary>
    /// No-op <see cref="ITailAuthority"/>: tail radius is the fallback 0.5m, never frozen.
    /// </summary>
    public sealed class NullTailAuthority : ITailAuthority
    {
        public float FrozenTailRadius => 0.5f;
        public bool IsFrozen => false;
        public void FreezeAtCountdown() { }
        public void Unfreeze() { }
    }
}