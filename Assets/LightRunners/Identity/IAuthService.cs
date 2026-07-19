using System;
using LightRunners.Core;

namespace LightRunners.Identity
{
    /// <summary>
    /// Authentication seam (spec §12.1). The interface is kept abstract so a future
    /// self-sovereign auth flow can slot in without touching call sites.
    /// </summary>
    public interface IAuthService
    {
        PlayerIdentity CurrentIdentity { get; }
        string CurrentUserId { get; }
        bool IsAuthenticated { get; }

        void SignInAnonymously(Action onSuccess, Action<string> onError);
        bool TryRestoreSession();
        void Logout();

        event Action OnAuthenticated;
        event Action OnLogout;
    }
}
