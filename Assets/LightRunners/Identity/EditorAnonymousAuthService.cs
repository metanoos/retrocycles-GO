using System;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Identity
{
    /// <summary>
    /// No-network anonymous auth used in editor / before Supabase is wired (phase 4 stop-gap).
    /// Generates a stable random UUID, persists it in <c>PlayerPrefs</c> so a returning user
    /// keeps the same ephemeral identity (spec §12.1 behaviour, minus the network).
    /// Replaced by <c>SupabaseAuthService</c> in phase 7.
    /// </summary>
    public sealed class EditorAnonymousAuthService : IAuthService
    {
        private const string PrefKey = "sb_refresh_token"; // same key SupabaseAuthService will use
        private PlayerIdentity _identity;

        public PlayerIdentity CurrentIdentity => _identity;
        public string CurrentUserId => _identity.userId;
        public bool IsAuthenticated => _identity.IsValid;

        public event Action OnAuthenticated;
        public event Action OnLogout;

        public void SignInAnonymously(Action onSuccess, Action<string> onError)
        {
            // Restore if present, else mint a new one.
            if (!TryRestoreSession())
            {
                string guid = Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(PrefKey, guid);
                PlayerPrefs.Save();
                _identity = new PlayerIdentity(guid, StringUtils.RunnerDisplayName(guid));
            }

            try
            {
                OnAuthenticated?.Invoke();
                onSuccess?.Invoke();
            }
            catch (Exception e)
            {
                onError?.Invoke(e.Message);
            }
        }

        public bool TryRestoreSession()
        {
            if (PlayerPrefs.HasKey(PrefKey))
            {
                string guid = PlayerPrefs.GetString(PrefKey);
                if (!string.IsNullOrEmpty(guid))
                {
                    _identity = new PlayerIdentity(guid, StringUtils.RunnerDisplayName(guid));
                    return true;
                }
            }
            return false;
        }

        public void Logout()
        {
            PlayerPrefs.DeleteKey(PrefKey);
            PlayerPrefs.Save();
            _identity = default;
            OnLogout?.Invoke();
        }
    }
}
