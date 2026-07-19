using System;

namespace LightRunners.Core
{
    /// <summary>
    /// Static event bus for cross-assembly notifications that would otherwise create a
    /// dependency cycle (spec §3.2). <see cref="PlayerCrashed"/> lets Multiplayer raise a
    /// crash without referencing Gameplay (which already references Multiplayer).
    ///
    /// Use this bus for any cross-assembly notification that would otherwise create a cycle.
    /// </summary>
    public static class GameEvents
    {
        /// <summary>Raised by either the Fusion path or the fallback collision detector when the local player crashes.</summary>
        public static event Action<string> PlayerCrashed;

        /// <summary>Raised by <c>GameManager</c> when the active state machine transitions (spec §2.3).</summary>
        public static event Action<GameState, GameState> GameStateChanged;

        /// <summary>Raised by <c>GameManager</c> when the active view mode changes (spec §4.3).</summary>
        public static event Action<ViewMode> ViewModeChanged;

        /// <summary>
        /// Raised by the Multiplayer assembly when the Photon connection comes up or drops
        /// (spec §8.1). <c>true</c> = connected to a room; <c>false</c> = solo/offline race.
        /// Lets Gameplay/UI react without Multiplayer referencing them (would be circular).
        /// </summary>
        public static event Action<bool> ConnectionStateChanged;

        /// <summary>
        /// Raised by the Backend assembly when the player's level arrives from the server
        /// (sign-in or record_run). Beacon re-derives unlocks from it (spec §12.5) without a
        /// Backend→Beacon reference.
        /// </summary>
        public static event Action<int> PlayerLevelChanged;

        // ─── Lightfield Match events (active decisions 2026-07-18) ───────────
        // Raised by MatchManager (Gameplay) and its subsystems. Mirrors the
        // existing bus pattern: lets Multiplayer/AR/UI/Backend react to match
        // transitions without referencing Gameplay (which would be a cycle).

        /// <summary>Match sub-state transition (decision P). (previous, next).</summary>
        public static event Action<MatchState, MatchState> MatchStateChanged;

        /// <summary>Lumen tally changed (decision E). (playerId, newTotal).</summary>
        public static event Action<string, int> LumensChanged;

        /// <summary>Leader changed (decisions F, I). newLeaderId is empty string on tie/null.</summary>
        public static event Action<string> LeaderChanged;

        /// <summary>A runner collected a Lumen Gate (decisions C, E, G). (gateId.Value, collectorPlayerId).</summary>
        public static event Action<int, string> GateCollected;

        /// <summary>A Gate spawned (decision G/L/M). (gateId.Value, lat, lon, alt, placement).</summary>
        public static event Action<int, double, double, double, GatePlacement> GateSpawned;

        /// <summary>A Gate despawned (collected/expired). (gateId.Value).</summary>
        public static event Action<int> GateDespawned;

        /// <summary>A runner crossed the Lightfield boundary (decision K). playerId.</summary>
        public static event Action<string> BoundaryViolated;

        /// <summary>The live clock reached zero (decision O). Most Lumens wins.</summary>
        public static event Action MatchExpired;

        public static void RaisePlayerLevelChanged(int level)
            => PlayerLevelChanged?.Invoke(level);

        public static void RaisePlayerCrashed(string causedByPlayerId)
            => PlayerCrashed?.Invoke(causedByPlayerId);

        public static void RaiseGameStateChanged(GameState previous, GameState next)
            => GameStateChanged?.Invoke(previous, next);

        public static void RaiseViewModeChanged(ViewMode mode)
            => ViewModeChanged?.Invoke(mode);

        public static void RaiseConnectionStateChanged(bool online)
            => ConnectionStateChanged?.Invoke(online);

        // ─── Lightfield Match event raisers ──────────────────────────────────

        public static void RaiseMatchStateChanged(MatchState previous, MatchState next)
            => MatchStateChanged?.Invoke(previous, next);

        public static void RaiseLumensChanged(string playerId, int newTotal)
            => LumensChanged?.Invoke(playerId, newTotal);

        public static void RaiseLeaderChanged(string leaderId)
            => LeaderChanged?.Invoke(leaderId);

        public static void RaiseGateCollected(int gateIdValue, string collectorPlayerId)
            => GateCollected?.Invoke(gateIdValue, collectorPlayerId);

        public static void RaiseGateSpawned(int gateIdValue, double lat, double lon, double alt, GatePlacement placement)
            => GateSpawned?.Invoke(gateIdValue, lat, lon, alt, placement);

        public static void RaiseGateDespawned(int gateIdValue)
            => GateDespawned?.Invoke(gateIdValue);

        public static void RaiseBoundaryViolated(string playerId)
            => BoundaryViolated?.Invoke(playerId);

        public static void RaiseMatchExpired()
            => MatchExpired?.Invoke();

        // Intentionally not providing an Unsubscribe-all: subscribers must manage their own
        // lifetimes to avoid silent leaks across scene loads.
    }
}
