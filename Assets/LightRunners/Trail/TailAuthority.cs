using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// Tail-radius authority (active decision T). The host-selected tail radius is AUTHORITATIVE,
    /// FROZEN AT COUNTDOWN, PRESERVED IN AFTERGLOW, and ALL head-to-trail collision + safety
    /// clearances DERIVE FROM IT.
    ///
    /// Why this exists: in v1, <c>collisionThreshold</c> and <c>trailWidth</c> were decoupled
    /// tunables — a host could set a wide visual ribbon with a narrow collision radius and the
    /// runner would clip visually through their own tail without crashing, or vice-versa. Decision
    /// T makes the tail radius the tunable source of truth while the player collision radius is
    /// fixed at 2 m. Collision and clearance distances are integer-centimetre derivatives exposed
    /// through <see cref="FrozenConfig"/>; no consumer approximates them independently.
    ///
    /// Lifecycle:
    ///  • Pre-countdown: <see cref="FrozenTailRadius"/> returns <see cref="GameConfig.tailRadius"/>
    ///    live (so tuning in the editor is reflected immediately).
    ///  • On entry to <see cref="MatchState.Countdown"/>: host calls <see cref="FreezeAtCountdown"/>,
    ///    which snapshots the current config value. After that, config changes do NOT propagate —
    ///    the frozen value is preserved across the whole match and into Afterglow so the replay
    ///    shows the same tail the runners actually raced against.
    ///  • On match end: <see cref="Unfreeze"/> resets so the next match can re-freeze fresh.
    ///
    /// Pure C# — Track D's MatchManager constructs and registers this on the
    /// <see cref="ServiceLocator"/>, overwriting the <see cref="NullTailAuthority"/> installed
    /// by PlatformServiceRegistry. <see cref="TrailCollisionDetector.CheckCollision"/> resolves
    /// <c>ITailAuthority</c> from the locator and falls back to
    /// <see cref="FrozenMatchConfig.Default"/> if no authority is registered.
    /// </summary>
    public sealed class TailAuthority : ITailAuthority
    {
        private FrozenMatchConfig? _frozenConfig;

        /// <summary>
        /// True once <see cref="FreezeAtCountdown"/> has been called; reset by <see cref="Unfreeze"/>.
        /// </summary>
        public bool IsFrozen => _frozenConfig.HasValue;

        /// <summary>
        /// The authoritative tail radius (m). Returns the frozen snapshot once
        /// <see cref="FreezeAtCountdown"/> has been called; otherwise the live config value
        /// (<see cref="GameConfig.tailRadius"/>). Never negative — clamped to a small floor.
        /// </summary>
        public float FrozenTailRadius
        {
            get
            {
                if (_frozenConfig.HasValue) return _frozenConfig.Value.TailRadiusMeters;
                return GameConfig.Active.tailRadius;
            }
        }

        /// <summary>
        /// Validated frozen contract. Before countdown, a legal live selection is reflected
        /// immediately; an invalid inspector value yields the safe default, while
        /// <see cref="TryFreezeAtCountdown"/> reports and rejects it.
        /// </summary>
        public FrozenMatchConfig FrozenConfig
        {
            get
            {
                if (_frozenConfig.HasValue) return _frozenConfig.Value;
                return FrozenMatchConfig.TryCreateFromMeters(
                    GameConfig.Active.tailRadius,
                    out var live,
                    out _)
                    ? live
                    : FrozenMatchConfig.Default;
            }
        }

        /// <summary>
        /// Host-only: snapshot <see cref="GameConfig.tailRadius"/> at its current value and freeze
        /// it for the rest of the match. No-op once frozen (the first freeze wins; a re-freeze
        /// mid-match would silently change the rules, which decision T forbids).
        /// </summary>
        public void FreezeAtCountdown()
        {
            TryFreezeAtCountdown(out _);
        }

        public bool TryFreezeAtCountdown(out string error)
        {
            if (_frozenConfig.HasValue)
            {
                error = string.Empty;
                return true;
            }

            if (!FrozenMatchConfig.TryCreateFromMeters(
                    GameConfig.Active.tailRadius,
                    out var config,
                    out error))
                return false;

            _frozenConfig = config;
            return true;
        }

        public bool TryApplyNetworkedFreeze(int tailRadiusCm, uint configHash, out string error)
        {
            if (!FrozenMatchConfig.TryRestore(
                    tailRadiusCm,
                    FrozenMatchConfig.PlayerHeadRadiusCm,
                    configHash,
                    out var received,
                    out error))
                return false;

            if (_frozenConfig.HasValue)
            {
                if (_frozenConfig.Value == received)
                {
                    error = string.Empty;
                    return true;
                }

                error = "Tail authority is already frozen with a different match config.";
                return false;
            }

            _frozenConfig = received;
            return true;
        }

        /// <summary>
        /// Reset to unfrozen (call between matches so the next countdown re-freezes fresh).
        /// </summary>
        public void Unfreeze()
        {
            _frozenConfig = null;
        }
    }
}
