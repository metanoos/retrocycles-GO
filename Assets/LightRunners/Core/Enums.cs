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
}
