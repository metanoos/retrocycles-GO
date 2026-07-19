using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using LightRunners.Core;
using LightRunners.Identity;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Login-scene controller. The <c>Play</c> button authenticates anonymously via the
    /// registered <see cref="IAuthService"/>, then loads the Game scene (spec §2.1, §14.1).
    /// </summary>
    public class LoginUI : MonoBehaviour
    {
        [SerializeField] private GameObject loginPanel;
        [SerializeField] private Button loginButton;
        [SerializeField] private TMP_TextAdaptor statusText;
        [SerializeField] private TMP_TextAdaptor infoText;

        [SerializeField] private string gameSceneName = "Game";

        private const string DefaultInfo = "Anonymous sign-in — tap Play to start";

        // Spec §21: after 3 consecutive sign-in failures, offer offline play (local identity,
        // no persistence, no multiplayer — everything else works).
        private int _failCount;
        private bool _offlineFallback;

        private void Start()
        {
            infoText?.SetText(DefaultInfo);
            statusText?.SetText("");
            if (loginButton != null) loginButton.onClick.AddListener(OnPlayClicked);

            // Try silent restore so a returning user gets straight in.
            if (ServiceLocator.TryGet<IAuthService>(out var auth) && auth != null && auth.TryRestoreSession())
            {
                statusText?.SetText($"Welcome back, {auth.CurrentIdentity.displayName}");
            }
        }

        private void OnDestroy()
        {
            if (loginButton != null) loginButton.onClick.RemoveListener(OnPlayClicked);
        }

        public void OnPlayClicked()
        {
            if (loginButton != null) loginButton.interactable = false;
            statusText?.SetText("Signing in…");

            if (_offlineFallback)
            {
                // Third strike (spec §21): swap in the local stub and get the player racing.
                var offline = new EditorAnonymousAuthService();
                ServiceLocator.Register<IAuthService>(offline);
                offline.SignInAnonymously(onSuccess: () => OnAuthenticated(), onError: err => OnError(err));
                return;
            }

            if (!ServiceLocator.TryGet<IAuthService>(out var auth) || auth == null)
            {
                // Login scene runs before PlatformServiceRegistry (which is in the Game scene),
                // so register the auth service here on demand — Supabase when configured,
                // the no-network stub otherwise (spec §12.1).
                auth = AuthServiceFactory.Create();
                ServiceLocator.Register(auth);
            }

            auth.OnAuthenticated -= OnAuthenticated;
            auth.OnAuthenticated += OnAuthenticated;
            auth.SignInAnonymously(
                onSuccess: () => OnAuthenticated(),
                onError: err => OnError(err));
        }

        private void OnAuthenticated()
        {
            var auth = ServiceLocator.Get<IAuthService>();
            statusText?.SetText($"Welcome, {auth?.CurrentIdentity.displayName ?? "Runner"}");
            LoadGameScene();
        }

        private void OnError(string error)
        {
            _failCount++;
            if (_failCount >= 3)
            {
                _offlineFallback = true;
                statusText?.SetText("Can't reach server — tap Play to race offline");
            }
            else
            {
                statusText?.SetText($"Sign-in failed: {error}");
            }
            if (loginButton != null) loginButton.interactable = true;
        }

        private void LoadGameScene()
        {
            // Loading the Game scene registers PlatformServiceRegistry, which DontDestroyOnLoads
            // the auth service across subsequent Game↔Login transitions.
            try
            {
                SceneManager.LoadScene(gameSceneName);
            }
            catch (System.Exception e)
            {
                statusText?.SetText($"Could not load '{gameSceneName}': {e.Message}");
                if (loginButton != null) loginButton.interactable = true;
            }
        }
    }
}
