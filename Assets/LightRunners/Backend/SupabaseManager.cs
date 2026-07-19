using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using LightRunners.Core;

namespace LightRunners.Backend
{
    /// <summary>
    /// Thin REST/RPC client over UnityWebRequest (spec §12.2). Base URL + anon key come from
    /// <see cref="GameConfig"/>; auth header is <c>Bearer &lt;access_token or anon key&gt;</c>.
    /// Registers <see cref="ILobbyService"/> in Awake — a real service when configured, a
    /// null-op stub when the Supabase URL is blank (spec §3.1) so Gameplay never null-checks.
    ///
    /// Write retry policy (spec §21): 2 retries with 2 s backoff, constants not config.
    /// </summary>
    public class SupabaseManager : Singleton<SupabaseManager>
    {
        private const int WriteRetries = 2;
        private const float RetryBackoffSeconds = 2f;

        public bool IsConfigured
        {
            get
            {
                var cfg = GameConfig.Active;
                return !string.IsNullOrEmpty(cfg.supabaseUrl) && !string.IsNullOrEmpty(cfg.supabaseAnonKey);
            }
        }

        /// <summary>Current session access token (JWT); null before sign-in.</summary>
        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public string UserId { get; private set; }

        private string BaseUrl => GameConfig.Active.supabaseUrl.TrimEnd('/');
        private string AnonKey => GameConfig.Active.supabaseAnonKey;

        /// <summary>
        /// Find-or-create the manager. Needed because auth happens in the Login scene, which
        /// doesn't ship a SupabaseManager object (spec §14.1) — the service still needs a
        /// coroutine runner there.
        /// </summary>
        public static SupabaseManager Ensure()
        {
            if (HasInstance) return Instance;
            var existing = FindAnyObjectByType<SupabaseManager>();
            if (existing != null) return existing;
            var go = new GameObject("SupabaseManager");
            DontDestroyOnLoad(go);
            return go.AddComponent<SupabaseManager>();
        }

        protected override void Awake()
        {
            base.Awake();

            // ILobbyService registration (spec §3.1 / §8.5): real when configured, null-op otherwise.
            if (!ServiceLocator.IsRegistered<ILobbyService>())
            {
                if (IsConfigured) ServiceLocator.Register<ILobbyService>(new SupabaseLobbyService(this));
                else ServiceLocator.Register<ILobbyService>(new NullLobbyService());
            }

            // PlayerRepository rides on the same GO (RunSummaryUI reaches it via HasInstance).
            if (GetComponent<PlayerRepository>() == null)
                gameObject.AddComponent<PlayerRepository>();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Auth (spec §12.2)
        // ─────────────────────────────────────────────────────────────────────
        [Serializable]
        private class AuthResponse
        {
            public string access_token;
            public string refresh_token;
            public AuthUser user;
        }

        [Serializable]
        private class AuthUser
        {
            public string id;
        }

        public void SignInAnonymously(Action<string, string> onSuccess, Action<string> onError)
        {
            // POST /auth/v1/signup with an empty JSON body = anonymous sign-in (spec §12.2;
            // requires "anonymous sign-ins" enabled on the project).
            StartCoroutine(CoAuth($"{BaseUrl}/auth/v1/signup", "{}", onSuccess, onError));
        }

        public void RestoreSession(string refreshToken, Action<string, string> onSuccess, Action<string> onError)
        {
            string body = "{\"refresh_token\":\"" + refreshToken + "\"}";
            StartCoroutine(CoAuth($"{BaseUrl}/auth/v1/token?grant_type=refresh_token", body, onSuccess, onError));
        }

        public void SignOut()
        {
            AccessToken = null;
            RefreshToken = null;
            UserId = null;
        }

        private IEnumerator CoAuth(string url, string body, Action<string, string> onSuccess, Action<string> onError)
        {
            if (!IsConfigured) { onError?.Invoke("Supabase not configured"); yield break; }

            using (var www = MakeJsonRequest(url, "POST", body, authed: false))
            {
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success)
                {
                    onError?.Invoke(HttpError(www));
                    yield break;
                }

                AuthResponse resp = null;
                try { resp = JsonUtility.FromJson<AuthResponse>(www.downloadHandler.text); }
                catch (Exception e) { onError?.Invoke("auth parse error: " + e.Message); yield break; }

                if (resp == null || string.IsNullOrEmpty(resp.access_token) || resp.user == null)
                {
                    onError?.Invoke("auth response missing token");
                    yield break;
                }

                AccessToken = resp.access_token;
                RefreshToken = resp.refresh_token;
                UserId = resp.user.id;
                onSuccess?.Invoke(resp.user.id, resp.refresh_token);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Generic REST (spec §12.2)
        // ─────────────────────────────────────────────────────────────────────
        public void Get(string pathAndQuery, Action<string> onSuccess, Action<string> onError)
            => StartCoroutine(CoRequest($"{BaseUrl}/rest/v1/{pathAndQuery}", "GET", null, null, onSuccess, onError, retries: 0));

        public void Post(string path, string json, Action<string> onSuccess, Action<string> onError, bool returnRepresentation = false)
            => StartCoroutine(CoRequest($"{BaseUrl}/rest/v1/{path}", "POST", json,
                returnRepresentation ? "return=representation" : "return=minimal", onSuccess, onError, WriteRetries));

        public void Upsert(string path, string json, Action<string> onSuccess, Action<string> onError)
            => StartCoroutine(CoRequest($"{BaseUrl}/rest/v1/{path}", "POST", json,
                "resolution=merge-duplicates,return=minimal", onSuccess, onError, WriteRetries));

        public void Patch(string pathAndQuery, string json, Action<string> onSuccess, Action<string> onError)
            => StartCoroutine(CoRequest($"{BaseUrl}/rest/v1/{pathAndQuery}", "PATCH", json, "return=minimal", onSuccess, onError, WriteRetries));

        /// <summary>POST /rest/v1/rpc/{fn} (spec §12.2).</summary>
        public void Rpc(string fn, string json, Action<string> onSuccess, Action<string> onError, int retries = 0)
            => StartCoroutine(CoRequest($"{BaseUrl}/rest/v1/rpc/{fn}", "POST", json, null, onSuccess, onError, retries));

        /// <summary>Rpc with the standard write-retry policy (spec §21).</summary>
        public void RpcWithRetry(string fn, string json, Action<string> onSuccess, Action<string> onError)
            => Rpc(fn, json, onSuccess, onError, WriteRetries);

        private IEnumerator CoRequest(
            string url, string method, string json, string preferHeader,
            Action<string> onSuccess, Action<string> onError, int retries)
        {
            if (!IsConfigured) { onError?.Invoke("Supabase not configured"); yield break; }

            for (int attempt = 0; ; attempt++)
            {
                using (var www = MakeJsonRequest(url, method, json, authed: true))
                {
                    if (!string.IsNullOrEmpty(preferHeader)) www.SetRequestHeader("Prefer", preferHeader);
                    yield return www.SendWebRequest();

                    if (www.result == UnityWebRequest.Result.Success)
                    {
                        onSuccess?.Invoke(www.downloadHandler.text);
                        yield break;
                    }

                    if (attempt >= retries)
                    {
                        onError?.Invoke(HttpError(www));
                        yield break;
                    }
                }
                yield return new WaitForSecondsRealtime(RetryBackoffSeconds);
            }
        }

        private UnityWebRequest MakeJsonRequest(string url, string method, string json, bool authed)
        {
            var www = new UnityWebRequest(url, method);
            if (json != null)
                www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("apikey", AnonKey);
            string bearer = authed && !string.IsNullOrEmpty(AccessToken) ? AccessToken : AnonKey;
            www.SetRequestHeader("Authorization", "Bearer " + bearer);
            www.timeout = 15;
            return www;
        }

        private static string HttpError(UnityWebRequest www)
            => $"{www.responseCode} {www.error} {Truncate(www.downloadHandler?.text, 200)}";

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));
    }

    /// <summary>
    /// PostgREST returns bare JSON arrays; JsonUtility can't parse a top-level array. Wrap it.
    /// </summary>
    public static class JsonArray
    {
        [Serializable]
        private class Wrapper<T>
        {
            public T[] items;
        }

        public static T[] FromJson<T>(string json)
        {
            if (string.IsNullOrEmpty(json)) return Array.Empty<T>();
            string trimmed = json.TrimStart();
            if (!trimmed.StartsWith("[")) json = "[" + json + "]";
            var w = JsonUtility.FromJson<Wrapper<T>>("{\"items\":" + json + "}");
            return w?.items ?? Array.Empty<T>();
        }
    }
}
