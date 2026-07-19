using UnityEngine;

namespace LightRunners.Core
{
    /// <summary>
    /// Peeking singleton base — <see cref="Instance"/> returns the live instance or null and
    /// **never creates one** (spec §3.1). This avoids hiding missing-wiring bugs behind
    /// auto-spawned objects and keeps the call sites honest.
    /// </summary>
    public abstract class Singleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;
        private static bool _appIsQuitting;

        public static T Instance
        {
            get
            {
                if (_appIsQuitting) return _instance;
                if (_instance == null) _instance = FindAnyObjectByType<T>(); // never creates
                return _instance;
            }
        }

        public static bool HasInstance => _instance != null && !_appIsQuitting;

        protected virtual void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this as T;
        }

        protected virtual void OnApplicationQuit() => _appIsQuitting = true;
        protected virtual void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
