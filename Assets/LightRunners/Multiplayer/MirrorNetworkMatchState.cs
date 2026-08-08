using UnityEngine;
using Mirror;
using LightRunners.Core;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// Mirror-based HOST-AUTHORITATIVE match-wide state (decisions Q and T).
    /// Free replacement for the Fusion NetworkMatchState.
    ///
    /// The host owns this object and every networked property on it.
    /// <see cref="FrozenTailRadius"/> is the decision-T property — it freezes at
    /// Countdown so all peers render tails at the same width for the entire match.
    ///
    /// FROZEN-TAIL-RADIUS PROPAGATION CONTRACT (decision T):
    ///   Host code calls <see cref="HostSetFrozenTailRadius"/> → SyncVar replicates to
    ///   clients → the <see cref="OnFrozenTailRadiusChanged"/> hook fires on clients →
    ///   each client propagates the value into its local ITailAuthority.
    /// </summary>
    public class MirrorNetworkMatchState : NetworkBehaviour
    {
        /// <summary>
        /// Sentinel returned before the host has frozen the radius (decision T).
        /// Negative so it can never collide with a real (positive) radius.
        /// </summary>
        public const float UnfrozenSentinel = -1f;

        [SyncVar(hook = nameof(OnFrozenTailRadiusChangedHook))]
        public float FrozenTailRadius = UnfrozenSentinel;

        [SyncVar] public uint FrozenConfigHash;
        [SyncVar] public int FrozenPlayerHeadRadiusCm;

        /// <summary>True on this peer once the host has frozen the radius.</summary>
        public bool HasFrozenTailRadius => FrozenTailRadius > 0f;

        // ─────────────────────────────────────────────────────────────────────
        // Host-side setter (decision T)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Host-only: publish the frozen tail radius to all clients. Called by the
        /// host-side ITailAuthority implementation when FreezeAtCountdown() fires.
        /// No-op on clients and no-op once frozen.
        /// </summary>
        public void HostSetFrozenTailRadius(float radius)
        {
            if (!isServer) return;
            if (!FrozenMatchConfig.TryCreateFromMeters(radius, out var config, out string error))
            {
                Debug.LogWarning($"[MirrorNetworkMatchState] Ignoring invalid frozen config: {error}");
                return;
            }
            if (HasFrozenTailRadius) return;
            FrozenPlayerHeadRadiusCm = FrozenMatchConfig.PlayerHeadRadiusCm;
            FrozenConfigHash = config.Hash;
            FrozenTailRadius = config.TailRadiusMeters;
        }

        /// <summary>Host-only: reset between matches (decision T).</summary>
        public void HostResetFrozenTailRadius()
        {
            if (!isServer) return;
            FrozenTailRadius = UnfrozenSentinel;
            FrozenPlayerHeadRadiusCm = 0;
            FrozenConfigHash = 0u;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Client-side SyncVar hook (decision T)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Mirror SyncVar hook — fires on clients when the host changes the value.</summary>
        private void OnFrozenTailRadiusChangedHook(float _, float newValue)
        {
            ApplyFrozenRadiusToLocalTailAuthority();
        }

        /// <summary>
        /// Propagate the host's frozen value into this peer's local ITailAuthority
        /// (resolved via locator). Best-effort: if no ITailAuthority is registered
        /// the value is dropped.
        /// </summary>
        private void ApplyFrozenRadiusToLocalTailAuthority()
        {
            if (ServiceLocator.TryGet<ITailAuthority>(out var tail) && tail != null)
            {
                if (FrozenTailRadius == UnfrozenSentinel)
                {
                    tail.Unfreeze();
                    return;
                }

                int tailRadiusCm = Mathf.RoundToInt(FrozenTailRadius * 100f);
                if (FrozenPlayerHeadRadiusCm != FrozenMatchConfig.PlayerHeadRadiusCm)
                {
                    Debug.LogError(
                        $"[MirrorNetworkMatchState] Rejected host player radius {FrozenPlayerHeadRadiusCm} cm; " +
                        $"first playable requires {FrozenMatchConfig.PlayerHeadRadiusCm} cm.");
                    return;
                }
                if (!tail.TryApplyNetworkedFreeze(tailRadiusCm, FrozenConfigHash, out string error))
                    Debug.LogError($"[MirrorNetworkMatchState] Rejected host frozen config: {error}");
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Late joiners need the initial snapshot applied.
            if (!isServer)
                ApplyFrozenRadiusToLocalTailAuthority();
        }
    }
}
