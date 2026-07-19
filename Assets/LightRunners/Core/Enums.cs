namespace LightRunners.Core
{
    /// <summary>
    /// The eight selectable beacon forms (player avatars). Spec §2.4 / §4.3.
    /// </summary>
    public enum BeaconFormType
    {
        Hoverboard = 0,
        Sphere = 1,
        Drone = 2,
        AbstractShape = 3,
        FloatingCube = 4,
        Motorcycle = 5,
        Phoenix = 6,
        Waveform = 7,
    }

    /// <summary>
    /// Top-level run/lobby state machine owned by <c>GameManager</c>. Spec §2.3.
    /// </summary>
    public enum GameState
    {
        Initializing,
        Login,
        Lobby,
        /// <summary>Friend-match pre-run roster (spec §2.3 / §8.5).</summary>
        PartyLobby,
        /// <summary>The async connect window (spec §2.3): Photon Connect in flight, or its timeout.</summary>
        Starting,
        Running,
        Crashed,
        /// <summary>App backgrounded mid-run (spec §20). Recording suspended; auto-ends after grace.</summary>
        Paused,
    }

    /// <summary>Map vs AR camera. Spec §4.3.</summary>
    public enum ViewMode
    {
        Map,
        AR,
    }

    // ─── Lightfield Match (active decisions, 2026-07-18) ───────────────────────
    // The Lightfield match core is a sub-FSM owned by MatchManager (Gameplay),
    // layered ON TOP of the existing GameState (which still drives Login/Lobby/etc).
    // Match is null while outside a match; transitions fire MatchStateChanged.

    /// <summary>
    /// Sub-state of an active Lightfield match. Owned by <c>MatchManager</c> (Gameplay),
    /// surfaced through <c>IMatchSession.State</c>. Decision O (timed match), decisions
    /// B/C/E/F (Snake tail + Lumen scoring + crash penalties).
    /// </summary>
    public enum MatchState
    {
        /// <summary>No match active; <c>IMatchSession</c> is registered but idle.</summary>
        Idle,
        /// <summary>Players assembled, host configuring, not yet counting down.</summary>
        Warmup,
        /// <summary>Pre-live countdown; tail radius freezes here (decision T).</summary>
        Countdown,
        /// <summary>Live play: clock running, Gate/Lumen/collision rules active.</summary>
        Live,
        /// <summary>Clock expired, computing final ranks before Afterglow (decision O).</summary>
        Scoring,
        /// <summary>Match finalized; Afterglow replay available; awaiting replay/leave.</summary>
        Expired,
    }

    /// <summary>
    /// How a Lumen Gate is anchored in the Lightfield. Decision L: ground placement
    /// half-buries the sphere (hemisphere visible); aerial placement exposes the full orb.
    /// Ground-only milestone (decision S) uses <see cref="Ground"/> only.
    /// </summary>
    public enum GatePlacement
    {
        Ground = 0,
        /// <summary>Stub for the aerial milestone (decision S); unlocks after altitude alignment.</summary>
        Aerial = 1,
    }

    /// <summary>
    /// Participant role inside a match. Decisions J (drivers excluded, passengers may
    /// compete), Q (host = State Authority), R (referee = validated command client).
    /// </summary>
    public enum PlayerRole
    {
        None = 0,
        Runner = 1,
        Host = 2,
        /// <summary>Optional validated command client (decision R); Gate-Director v2 deferred.</summary>
        Referee = 3,
    }

    /// <summary>
    /// Crash-penalty tier (decision F). Non-leader drops 1 Lumen, leader drops 2, both
    /// capped by held score. Dropped Lumens become stealable pickups at the crash site.
    /// </summary>
    public enum CrashTier
    {
        NonLeader = 0,
        Leader = 1,
    }
}
