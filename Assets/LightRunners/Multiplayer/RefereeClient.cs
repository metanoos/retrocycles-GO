#if FUSION_WEAVER
using UnityEngine;
using Fusion;
using LightRunners.Core;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// REFEREE connection (decision R) — a VALIDATED COMMAND CLIENT, not a State
    /// Authority. The referee may issue Gate-Director commands (place bonus gates
    /// etc.) but does NOT own match state; the host remains authoritative
    /// (decision Q). The referee presents a host-issued token; the host validates
    /// it via <see cref="RefereeTokenValidator.Validate"/> before forwarding any
    /// command to the locator-resolved <see cref="IGateDirector"/>.
    ///
    /// DIVERGENCE FROM SPEC §8.1: this role does not exist under Shared Mode. It
    /// is new under Host Mode (decision Q + R).
    ///
    /// v2 vs milestone: the full Gate-Director UI (decision R/S) is deferred to
    /// v2. For the milestone we ship the role + token validation and stub the
    /// Gate-Director RPCs so the contract is stable when the v2 UI lands.
    /// </summary>
    public class RefereeClient : NetworkBehaviour
    {
        /// <summary>
        /// Match id this referee is credentialed for. Set host-side at connect
        /// (the host stamps it when the referee's connection is accepted). The
        /// token validator compares the token's match id against this value.
        /// </summary>
        [Networked] public NetworkString<_64> MatchId { get; set; }

        /// <summary>
        /// Host-only secret used to validate tokens for this match. NEVER
        /// networked in cleartext — only the host holds it; clients present
        /// tokens derived from it.
        /// </summary>
        private string _hostIssuedSecret;

        /// <summary>
        /// Host-only: stamp the referee credential for this match. Called by the
        /// host when it accepts a referee connection (the match id and secret are
        /// host-derived; the referee receives only the token).
        /// </summary>
        public void HostConfigureReferee(string matchId, string hostIssuedSecret)
        {
            if (!Object.HasStateAuthority) return;
            MatchId = matchId ?? string.Empty;
            _hostIssuedSecret = hostIssuedSecret ?? string.Empty;
        }

        /// <summary>
        /// Referee → Host RPC: request placement of a bonus gate (decision R).
        /// Validates <paramref name="refereeToken"/> via
        /// <see cref="RefereeTokenValidator.Validate"/> (pure C#, unit-tested), and
        /// on success forwards to the locator-resolved
        /// <see cref="IGateDirector.PlaceBonusGate"/>. The full Gate-Director UI
        /// is deferred to v2 — for now this is the only command a referee may
        /// issue. Future commands (move gate, despawn, freeze runner) follow the
        /// same validate-then-forward pattern.
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RpcPlaceBonusGate(double lat, double lon, double alt, int placement, string refereeToken, RpcInfo info = default)
        {
            if (!Object.HasStateAuthority) return;
            if (string.IsNullOrEmpty(refereeToken))
            {
                Debug.LogWarning("[RefereeClient] Rejecting PlaceBonusGate: empty token.");
                return;
            }

            string matchId = MatchId.ToString();
            if (!RefereeTokenValidator.Validate(refereeToken, matchId, _hostIssuedSecret))
            {
                Debug.LogWarning($"[RefereeClient] Rejecting PlaceBonusGate: token failed validation for match {matchId}.");
                return;
            }

            if (ServiceLocator.TryGet<IGateDirector>(out var director) && director != null)
            {
                var at = new GeoPoint(lat, lon, alt);
                var gp = (GatePlacement)placement;
                director.PlaceBonusGate(at, gp, refereeToken);
            }
            else
            {
                Debug.LogWarning("[RefereeClient] No IGateDirector registered — PlaceBonusGate dropped.");
            }
        }

        public override void Spawned()
        {
            // Referee is a passive observer until it issues a command. The host
            // will call HostConfigureReferee on connect; clients (including the
            // referee peer) read MatchId via replication if they need it.
        }
    }
}
#endif
