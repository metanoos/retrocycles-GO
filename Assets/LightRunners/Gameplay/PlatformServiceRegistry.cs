using UnityEngine;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Identity;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Awakens first in the Game scene and registers the cross-cutting platform services
    /// onto the <see cref="ServiceLocator"/>. <c>DontDestroyOnLoad</c> so they survive back to
    /// Login; idempotent — if services are already registered (carried over from a prior Game
    /// load), it skips (spec §3.1 / §14.2).
    /// </summary>
    [DefaultExecutionOrder(-100)] // Awake before GameManager
    public class PlatformServiceRegistry : MonoBehaviour
    {
        private static bool _alreadyCreated;

        private void Awake()
        {
            // Single-instance guard: an older registry may have been DontDestroyOnLoad-ed
            // from a previous scene load. Yield to it.
            if (_alreadyCreated)
            {
                Destroy(gameObject);
                return;
            }
            _alreadyCreated = true;
            DontDestroyOnLoad(gameObject);

            RegisterAuth();
            RegisterAltitude();
            RegisterNullMatchServices();
        }

        private void RegisterAuth()
        {
            if (ServiceLocator.IsRegistered<IAuthService>()) return;

            // Real Supabase auth when configured; the no-network stub otherwise (spec §12.1,
            // §21 "Play offline"). Same choice in editor and on device so the backend can be
            // exercised in playmode.
            IAuthService auth = AuthServiceFactory.Create();
            ServiceLocator.Register(auth);

            // Try silent restore so a returning user is already authenticated.
            auth.TryRestoreSession();
        }

        private void RegisterAltitude()
        {
            // Altitude is chosen per-platform by the factory; expose the chosen instance via
            // the locator so non-Location code (e.g. AR) can read calibrated altitude.
            if (ServiceLocator.IsRegistered<IAltitudeService>())
            {
                return;
            }

            var provider = LocationProvider.Instance;
            if (provider != null && provider.AltitudeService != null)
            {
                ServiceLocator.Register(provider.AltitudeService);
            }
        }

        /// <summary>
        /// Register Null* Lightfield match services so editor-only playmode compiles and
        /// runs end-to-end before the real impls land on their parallel tracks:
        ///   - LumenScoreboard        → LightRunners.Trail       (Track A)
        ///   - GateSpawner / Volume   → LightRunners.Lightfield  (Track B)
        ///   - FusionLauncher         → LightRunners.Multiplayer (Track C, FUSION_WEAVER)
        ///   - MatchManager           → LightRunners.Gameplay    (Track D)
        ///   - ReplayPackage sink     → LightRunners.Afterglow   (Track F)
        /// Each real impl overwrites its slot via ServiceLocator.Register when it comes up
        /// (Register replaces; TryRegister would wrongly keep the null).
        /// </summary>
        private void RegisterNullMatchServices()
        {
            if (!ServiceLocator.IsRegistered<IMatchSession>())
                ServiceLocator.Register<IMatchSession>(new NullMatchSession());
            if (!ServiceLocator.IsRegistered<ILumenScoreboard>())
                ServiceLocator.Register<ILumenScoreboard>(new NullLumenScoreboard());
            if (!ServiceLocator.IsRegistered<IGateDirector>())
                ServiceLocator.Register<IGateDirector>(new NullGateDirector());
            if (!ServiceLocator.IsRegistered<ILightfieldVolume>())
                ServiceLocator.Register<ILightfieldVolume>(new NullLightfieldVolume());
            if (!ServiceLocator.IsRegistered<IMatchTransport>())
                ServiceLocator.Register<IMatchTransport>(new NullMatchTransport());
            if (!ServiceLocator.IsRegistered<IMatchReplaySink>())
                ServiceLocator.Register<IMatchReplaySink>(new NullMatchReplaySink());
            if (!ServiceLocator.IsRegistered<ITailAuthority>())
                ServiceLocator.Register<ITailAuthority>(new NullTailAuthority());
        }

        private void OnDestroy()
        {
            if (!_alreadyCreated) return;
            _alreadyCreated = false;
        }
    }
}
