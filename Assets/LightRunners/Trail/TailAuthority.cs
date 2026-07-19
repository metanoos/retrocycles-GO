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
    /// T makes the tail radius the single source of truth: visuals, collisions, and safety
    /// clearances all derive from <see cref="FrozenTailRadius"/>. The collision detector reads
    /// <c>FrozenTailRadius × 2</c> as its near-gate threshold (a head touches a tail when their
    /// combined radii overlap; with a unit head radius, head-radius + tail-radius = tail-radius + 1,
    /// and we approximate as <c>2 × tail-radius</c> for the symmetric near-test).
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
    /// <see cref="GameConfig.collisionThreshold"/> if no authority is registered (e.g. an editor
    /// scene that hasn't bootstrapped the match core).
    /// </summary>
    public sealed class TailAuthority : ITailAuthority
    {
        private float? _frozenTailRadius;

        /// <summary>
        /// True once <see cref="FreezeAtCountdown"/> has been called; reset by <see cref="Unfreeze"/>.
        /// </summary>
        public bool IsFrozen => _frozenTailRadius.HasValue;

        /// <summary>
        /// The authoritative tail radius (m). Returns the frozen snapshot once
        /// <see cref="FreezeAtCountdown"/> has been called; otherwise the live config value
        /// (<see cref="GameConfig.tailRadius"/>). Never negative — clamped to a small floor.
        /// </summary>
        public float FrozenTailRadius
        {
            get
            {
                if (_frozenTailRadius.HasValue) return _frozenTailRadius.Value;
                float live = GameConfig.Active.tailRadius;
                return live < 0f ? 0f : live;
            }
        }

        /// <summary>
        /// Host-only: snapshot <see cref="GameConfig.tailRadius"/> at its current value and freeze
        /// it for the rest of the match. No-op once frozen (the first freeze wins; a re-freeze
        /// mid-match would silently change the rules, which decision T forbids).
        /// </summary>
        public void FreezeAtCountdown()
        {
            if (_frozenTailRadius.HasValue) return;
            float v = GameConfig.Active.tailRadius;
            _frozenTailRadius = v < 0f ? 0f : v;
        }

        /// <summary>
        /// Reset to unfrozen (call between matches so the next countdown re-freezes fresh).
        /// </summary>
        public void Unfreeze()
        {
            _frozenTailRadius = null;
        }
    }
}
