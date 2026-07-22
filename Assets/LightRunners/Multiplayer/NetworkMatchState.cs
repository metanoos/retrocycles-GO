#if FUSION_WEAVER
using UnityEngine;
using Fusion;
using LightRunners.Core;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// HOST-AUTHORITATIVE match-wide state (decisions Q and T).
    ///
    /// DIVERGENCE FROM SPEC §8.1: under Shared Mode there is no single owner of
    /// "the match"; under Host Mode (decision Q) the host owns this NetworkObject
    /// and every networked property on it. <see cref="FrozenTailRadius"/> is the
    /// decision-T property — it freezes at Countdown so all peers render tails at
    /// the same width for the entire match.
    ///
    /// FROZEN-TAIL-RADIUS PROPAGATION CONTRACT (decision T — for Track D / Track A):
    ///
    ///   Signal source  : Track D's MatchManager calls
    ///                     <c>ServiceLocator.Get&lt;ITailAuthority&gt;().FreezeAtCountdown()</c>
    ///                     when the match enters Countdown. The host-side
    ///                     <c>ITailAuthority</c> impl (Track A) freezes its local
    ///                     value AND mirrors it onto this object via
    ///                     <see cref="HostSetFrozenTailRadius"/>. NetworkMatchState
    ///                     itself does NOT subscribe to a GameEvents signal — the
    ///                     contract is "host code calls HostSetFrozenTailRadius",
    ///                     which keeps the freeze atomic and avoids double-freeze.
    ///
    ///   Host → Client  : On the host, <see cref="FrozenTailRadius"/> is set; the
    ///                     <see cref="OnFrozenTailRadiusChanged"/> callback fires
    ///                     only on CLIENTS (Fusion does not re-fire OnChanged on
    ///                     the writer). Clients read the host value and propagate
    ///                     it into their local ITailAuthority-equivalent via
    ///                     <see cref="ApplyFrozenRadiusToLocalTailAuthority"/>.
    ///
    ///   Why poll-not-subscribe: the alternative was for NetworkMatchState to
    ///                     subscribe to a new GameEvents signal. That would require
    ///                     Track D's MatchManager to ALSO raise the signal, which
    ///                     duplicates the freeze call. The chosen contract is
    ///                     "Track D's MatchManager calls FreezeAtCountdown() on the
    ///                     locator-resolved ITailAuthority; the HOST-SIDE
    ///                     ITailAuthority impl is responsible for setting the
    ///                     networked prop". That keeps the freeze call site unique
    ///                     and authoritative, and lets the host-side ITailAuthority
    ///                     be the single owner of "did we already freeze?".
    ///
    ///   Coordination    : because NetworkMatchState cannot reference the Trail
    ///                     assembly's TailAuthority, the propagation into the
    ///                     client's local ITailAuthority is best-effort: on each
    ///                     OnChanged we call into whatever object is registered as
    ///                     ITailAuthority on the locator, by interface only. If
    ///                     none is registered (e.g. pre-Track-A), the change is
    ///                     silently dropped — which is correct, because in that
    ///                     case tails are not yet using the frozen value.
    /// </summary>
    public class NetworkMatchState : NetworkBehaviour
    {
        /// <summary>
        /// Sentinel returned before the host has frozen the radius (decision T).
        /// Negative so it can never collide with a real (positive) radius.
        /// </summary>
        public const float UnfrozenSentinel = -1f;

        /// <summary>
        /// Host-authoritative frozen tail radius (m). Host sets it via
        /// <see cref="HostSetFrozenTailRadius"/> when FreezeAtCountdown fires;
        /// clients read it for trail rendering width. Negative (sentinel) until
        /// frozen; positive afterwards.
        /// </summary>
        [Networked(OnChanged = nameof(OnFrozenTailRadiusChanged))]
        public float FrozenTailRadius { get; set; } = UnfrozenSentinel;

        /// <summary>Hash of every integer-derived collision/clearance field.</summary>
        [Networked]
        public uint FrozenConfigHash { get; set; }

        /// <summary>Serialized explicitly so old or modified room state cannot hide a different player radius.</summary>
        [Networked]
        public int FrozenPlayerHeadRadiusCm { get; set; }

        /// <summary>True on this peer once the host has frozen the radius.</summary>
        public bool HasFrozenTailRadius => FrozenTailRadius > 0f;

        // ─────────────────────────────────────────────────────────────────────
        // Host-side setter (decision T)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Host-only: publish the frozen tail radius to all clients. Called by the
        /// host-side <c>ITailAuthority</c> implementation when
        /// <c>FreezeAtCountdown()</c> fires (Track D owns that call site). No-op
        /// on clients and no-op once frozen (the host's local authority is the
        /// single owner of "did we already freeze?").
        /// </summary>
        public void HostSetFrozenTailRadius(float radius)
        {
            if (!Object.HasStateAuthority) return;
            if (!FrozenMatchConfig.TryCreateFromMeters(radius, out var config, out string error))
            {
                Debug.LogWarning($"[NetworkMatchState] Ignoring invalid frozen config: {error}");
                return;
            }
            if (HasFrozenTailRadius) return; // frozen values are immutable for the match
            FrozenPlayerHeadRadiusCm = FrozenMatchConfig.PlayerHeadRadiusCm;
            FrozenConfigHash = config.Hash;
            FrozenTailRadius = config.TailRadiusMeters;
        }

        /// <summary>
        /// Host-only: reset between matches (decision T). Mirrors
        /// <see cref="ITailAuthority.Unfreeze"/>.
        /// </summary>
        public void HostResetFrozenTailRadius()
        {
            if (!Object.HasStateAuthority) return;
            FrozenTailRadius = UnfrozenSentinel;
            FrozenPlayerHeadRadiusCm = 0;
            FrozenConfigHash = 0u;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Client-side OnChanged (decision T)
        //
        // Fusion only fires OnChanged on peers that did NOT write the value, so
        // this is the client path. The host has already frozen its local
        // ITailAuthority before calling HostSetFrozenTailRadius.
        // ─────────────────────────────────────────────────────────────────────
        public static void OnFrozenTailRadiusChanged(Changed<NetworkMatchState> changed)
        {
            changed.Behaviour.ApplyFrozenRadiusToLocalTailAuthority();
        }

        /// <summary>
        /// Propagate the host's frozen value into this peer's local
        /// <see cref="ITailAuthority"/> (resolved via locator). Best-effort: if
        /// no ITailAuthority is registered (pre-Track-A wiring) the value is
        /// dropped, which is correct because in that case no tail consumer is
        /// using the frozen radius yet.
        ///
        /// The client rederives all collision/clearance fields from integer centimetres and
        /// rejects the room state if the fixed player radius contract or hash does not match.
        /// </summary>
        private void ApplyFrozenRadiusToLocalTailAuthority()
        {
            if (!HasFrozenTailRadius) return;
            if (ServiceLocator.TryGet<ITailAuthority>(out var tail) && tail != null)
            {
                int tailRadiusCm = Mathf.RoundToInt(FrozenTailRadius * 100f);
                if (FrozenPlayerHeadRadiusCm != FrozenMatchConfig.PlayerHeadRadiusCm)
                {
                    Debug.LogError(
                        $"[NetworkMatchState] Rejected host player radius {FrozenPlayerHeadRadiusCm} cm; " +
                        $"first playable requires {FrozenMatchConfig.PlayerHeadRadiusCm} cm.");
                    return;
                }
                if (!tail.TryApplyNetworkedFreeze(tailRadiusCm, FrozenConfigHash, out string error))
                    Debug.LogError($"[NetworkMatchState] Rejected host frozen config: {error}");
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[NetworkMatchState] Client received frozen tail radius = {FrozenTailRadius} m, config {FrozenConfigHash:X8} (decision T).");
#endif
        }

        public override void Spawned()
        {
            // Late joiners may spawn after the value was frozen and therefore cannot rely on a
            // future OnChanged callback. Validate the initial replicated snapshot immediately.
            if (!Object.HasStateAuthority && HasFrozenTailRadius)
                ApplyFrozenRadiusToLocalTailAuthority();
        }
    }
}
#endif
