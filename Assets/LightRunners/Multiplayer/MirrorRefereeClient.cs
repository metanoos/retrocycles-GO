using UnityEngine;
using Mirror;
using LightRunners.Core;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// Mirror-based REFEREE connection (decision R) — a VALIDATED COMMAND CLIENT.
    /// Free replacement for the Fusion RefereeClient.
    ///
    /// The referee may issue Gate-Director commands (place bonus gates etc.) but does
    /// NOT own match state; the host remains authoritative (decision Q). The referee
    /// presents a host-issued token; the host validates it via
    /// <see cref="RefereeTokenValidator.Validate"/> before forwarding any command to
    /// the locator-resolved <see cref="IGateDirector"/>.
    /// </summary>
    public class MirrorRefereeClient : NetworkBehaviour
    {
        [SyncVar] public string MatchId = string.Empty;

        private string _hostIssuedSecret;

        /// <summary>
        /// Host-only: stamp the referee credential for this match. Called by the
        /// host when it accepts a referee connection.
        /// </summary>
        public void HostConfigureReferee(string matchId, string hostIssuedSecret)
        {
            if (!isServer) return;
            MatchId = matchId ?? string.Empty;
            _hostIssuedSecret = hostIssuedSecret ?? string.Empty;
        }

        /// <summary>
        /// Referee → Host Command: request placement of a bonus gate (decision R).
        /// Validates refereeToken via RefereeTokenValidator (pure C#, unit-tested),
        /// and on success forwards to the locator-resolved IGateDirector.PlaceBonusGate.
        /// </summary>
        [Command]
        public void CmdPlaceBonusGate(double lat, double lon, double alt, int placement, string refereeToken)
        {
            if (!isServer) return;
            if (string.IsNullOrEmpty(refereeToken))
            {
                Debug.LogWarning("[MirrorRefereeClient] Rejecting PlaceBonusGate: empty token.");
                return;
            }

            string matchId = MatchId;
            if (!RefereeTokenValidator.Validate(refereeToken, matchId, _hostIssuedSecret))
            {
                Debug.LogWarning($"[MirrorRefereeClient] Rejecting PlaceBonusGate: token failed validation for match {matchId}.");
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
                Debug.LogWarning("[MirrorRefereeClient] No IGateDirector registered — PlaceBonusGate dropped.");
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Referee is a passive observer until it issues a command. The host
            // will call HostConfigureReferee on connect.
        }
    }
}
