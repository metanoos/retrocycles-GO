using System;
using System.Collections.Generic;
using LightRunners.Core;

namespace LightRunners.Lightfield
{
    /// <summary>
    /// Pure-C# geometry primitives for the Lightfield play volume. Spec §4.1 (geo), decision K
    /// (hemispherical play volume), decision S (ground-only milestone: model as circular disc
    /// radius <c>lightfieldBaseRadiusMeters</c> + hard altitude ceiling <c>lightfieldDomeCeilingMeters</c>).
    ///
    /// All math is stateless and unit-testable. <see cref="LightfieldVolume"/> is the stateful
    /// wrapper that implements <see cref="ILightfieldVolume"/>.
    /// </summary>
    public static class LightfieldGeometry
    {
        /// <summary>
        /// Small altitude tolerance (m). Players on the ground at the origin altitude, plus a
        /// little jitter for GPS/barometer noise, must still be "inside". Decision K.
        /// </summary>
        public const float GroundDipToleranceMeters = 1f;

        /// <summary>
        /// Horizontal-disc membership. True iff the great-circle distance from
        /// <paramref name="origin"/> to <paramref name="p"/> is at most <paramref name="radiusMeters"/>.
        /// Decision K ground-only milestone. Boundary is inclusive (on-edge counts as inside).
        /// </summary>
        public static bool IsInsideDisc(GeoPoint origin, GeoPoint p, float radiusMeters)
        {
            if (radiusMeters < 0f) return false;
            return origin.HorizontalDistanceTo(p) <= radiusMeters;
        }

        /// <summary>
        /// Ceiling membership. True iff <c>p.altitude - origin.altitude</c> is at most
        /// <paramref name="ceilingMeters"/> AND at least <c>-GroundDipToleranceMeters</c> (the
        /// latter is the small ground-dip tolerance so a runner on the floor stays "inside").
        /// Decision K ground-only milestone. Both bounds inclusive.
        /// </summary>
        public static bool IsBelowCeiling(GeoPoint origin, GeoPoint p, float ceilingMeters)
        {
            double relAlt = p.altitude - origin.altitude;
            return relAlt <= ceilingMeters && relAlt >= -GroundDipToleranceMeters;
        }

        /// <summary>
        /// Stub hemispherical-dome test for the aerial milestone (decision S). The ground-only
        /// milestone approximates "inside the dome" as <see cref="IsInsideDisc"/> AND
        /// <see cref="IsBelowCeiling"/>. Replace with true hemisphere math (radius tapers with
        /// altitude) when the aerial milestone lands. TODO(aerial-milestone): real dome.
        /// </summary>
        public static bool IsInsideDome(GeoPoint origin, GeoPoint p, float radiusMeters, float ceilingMeters)
            => IsInsideDisc(origin, p, radiusMeters) && IsBelowCeiling(origin, p, ceilingMeters);
    }

    /// <summary>
    /// Implements <see cref="ILightfieldVolume"/> (decision K). The match host owns the single
    /// instance: it sets the origin at match start, then per-tick calls <see cref="CheckPlayer"/>
    /// for each runner. Crossing either the disc boundary or the altitude ceiling raises
    /// <see cref="BoundaryViolated"/> — fired at most once per crossing per player so the bus
    /// is not spammed while a runner stays outside.
    ///
    /// Ground-only milestone (decision S): <see cref="IsInside(GeoPoint)"/> is the disc +
    /// ceiling approximation from <see cref="LightfieldGeometry"/>. True dome math deferred.
    /// </summary>
    public sealed class LightfieldVolume : ILightfieldVolume
    {
        private readonly Dictionary<string, bool> _playerInside = new Dictionary<string, bool>();

        /// <summary>
        /// Match origin in geo coordinates. Defaults to <see cref="GeoPoint.Zero"/> until the
        /// host calls <see cref="SetOrigin"/>. Decision K.
        /// </summary>
        public GeoPoint Origin { get; private set; }

        /// <summary>
        /// Per-player boundary-crossing notification. Fires when <see cref="CheckPlayer"/>
        /// observes a transition from inside to outside. Does NOT fire on the outside→inside
        /// recovery (recovery is not a violation). Decision K.
        /// </summary>
        public event Action<string> BoundaryViolated;

        /// <summary>Set/replace the origin (called by the host at match start). Decision K.</summary>
        public void SetOrigin(GeoPoint origin) => Origin = origin;

        /// <summary>
        /// Pure membership test against the volume at the current origin. Spec §4.1, decision K,
        /// decision S. See <see cref="LightfieldGeometry.IsInsideDome"/> for the milestone
        /// approximation.
        /// </summary>
        public bool IsInside(GeoPoint point)
        {
            GameConfig cfg = GameConfig.Active;
            return LightfieldGeometry.IsInsideDome(
                Origin,
                point,
                cfg.lightfieldBaseRadiusMeters,
                cfg.lightfieldDomeCeilingMeters);
        }

        /// <summary>
        /// Host-per-tick call: feed each runner's last-known position. The first observation of
        /// a runner is treated as the initial inside/outside state and does not itself raise
        /// (we only raise on a transition). Subsequent observations raise
        /// <see cref="BoundaryViolated"/> exactly once per inside→outside transition; the player
        /// must re-enter before another violation can fire. Decision K.
        /// </summary>
        public void CheckPlayer(string playerId, GeoPoint point)
        {
            if (string.IsNullOrEmpty(playerId)) return;

            bool nowInside = IsInside(point);
            bool wasInside = _playerInside.TryGetValue(playerId, out var prev) ? prev : nowInside;

            // Only fire on the inside→outside transition (idempotent per crossing).
            if (wasInside && !nowInside)
            {
                try
                {
                    BoundaryViolated?.Invoke(playerId);
                }
                catch (Exception ex)
                {
                    // A subscriber throwing must not corrupt the volume's per-tick loop or the
                    // recorded state of this player. Log and continue (matches the bus pattern).
                    UnityEngine.Debug.LogException(ex);
                }
            }

            _playerInside[playerId] = nowInside;
        }

        /// <summary>
        /// Forget a player's inside/outside state (called by the host on player leave / match
        /// end so a returning player's first observation is treated as fresh). Decision K.
        /// </summary>
        public void ForgetPlayer(string playerId)
        {
            if (!string.IsNullOrEmpty(playerId))
                _playerInside.Remove(playerId);
        }

        /// <summary>Reset to a clean state between matches (drops all per-player tracking).</summary>
        public void Clear() => _playerInside.Clear();
    }
}
