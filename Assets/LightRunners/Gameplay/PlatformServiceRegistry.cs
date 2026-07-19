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

        private void OnDestroy()
        {
            if (!_alreadyCreated) return;
            _alreadyCreated = false;
        }
    }
}
