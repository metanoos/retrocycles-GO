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

        // Intentionally not providing an Unsubscribe-all: subscribers must manage their own
        // lifetimes to avoid silent leaks across scene loads.
    }
}
