using System;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Backend;

namespace LightRunners.Identity
{
    /// <summary>
    /// Anonymous Supabase auth (spec §12.1). Persists the refresh token in PlayerPrefs
    /// (<c>sb_refresh_token</c>) so a returning user keeps the same ephemeral account.
    /// Display name = "Runner_" + first 6 chars of the UUID. On success, upserts the
    /// <c>players</c> row (the DB trigger covers it too — the upsert also pulls the level).
    /// </summary>
    public sealed class SupabaseAuthService : IAuthService
    {
        private const string PrefKey = "sb_refresh_token";

        private readonly SupabaseManager _supabase;
        private PlayerIdentity _identity;

        public SupabaseAuthService(SupabaseManager supabase)
        {
            _supabase = supabase;
        }

        public PlayerIdentity CurrentIdentity => _identity;
        public string CurrentUserId => _identity.userId;
        public bool IsAuthenticated => _identity.IsValid;

        public event Action OnAuthenticated;
        public event Action OnLogout;

        public void SignInAnonymously(Action onSuccess, Action<string> onError)
        {
            // Prefer restoring the existing ephemeral account.
            string stored = PlayerPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(stored))
            {
                _supabase.RestoreSession(stored,
                    onSuccess: (userId, refresh) => Complete(userId, refresh, onSuccess),
                    onError: _ =>
                    {
                        // Stale/revoked token — fall through to a fresh anonymous account.
                        SignUpFresh(onSuccess, onError);
                    });
                return;
            }
            SignUpFresh(onSuccess, onError);
        }

        private void SignUpFresh(Action onSuccess, Action<string> onError)
        {
            _supabase.SignInAnonymously(
                onSuccess: (userId, refresh) => Complete(userId, refresh, onSuccess),
                onError: err => onError?.Invoke(err));
        }

        private void Complete(string userId, string refreshToken, Action onSuccess)
        {
            if (!string.IsNullOrEmpty(refreshToken))
            {
                PlayerPrefs.SetString(PrefKey, refreshToken);
                PlayerPrefs.Save();
            }
            _identity = new PlayerIdentity(userId, StringUtils.RunnerDisplayName(userId));

            if (PlayerRepository.HasInstance)
                PlayerRepository.Instance.RegisterOrUpdatePlayer(_identity);

            OnAuthenticated?.Invoke();
            onSuccess?.Invoke();
        }

        /// <summary>
        /// True if a stored token exists — the actual refresh is async; identity becomes
        /// valid when <see cref="OnAuthenticated"/> fires (call sites already listen).
        /// </summary>
        public bool TryRestoreSession()
        {
            string stored = PlayerPrefs.GetString(PrefKey, "");
            if (string.IsNullOrEmpty(stored)) return false;
            _supabase.RestoreSession(stored,
                onSuccess: (userId, refresh) => Complete(userId, refresh, null),
                onError: _ => { /* Play press retries via SignInAnonymously */ });
            return _identity.IsValid; // silent restore reports current validity, not the future
        }

        public void Logout()
        {
            PlayerPrefs.DeleteKey(PrefKey);
            PlayerPrefs.Save();
            _supabase.SignOut();
            _identity = default;
            OnLogout?.Invoke();
        }
    }
}
