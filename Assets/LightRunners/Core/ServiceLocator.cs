using System;
using System.Collections.Generic;

namespace LightRunners.Core
{
    /// <summary>
    /// Type-keyed dictionary that is the single seam through which cross-system dependencies
    /// resolve. Registrations happen in <c>Awake</c>/<c>Start</c> of owning objects; resolution
    /// happens later so initialization order doesn't have to be perfect. Spec §3.1.
    /// </summary>
    public static class ServiceLocator
    {
        private static readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();

        /// <summary>Register <paramref name="instance"/> as the implementation of <typeparamref name="T"/>.</summary>
        public static void Register<T>(T instance) where T : class
        {
            if (instance == null)
            {
                UnityEngine.Debug.LogWarning($"[ServiceLocator] Refusing to register null for {typeof(T).Name}.");
                return;
            }
            _services[typeof(T)] = instance;
        }

        /// <summary>Register only if nothing is already registered — keeps registration idempotent across scene loads (spec §3.1).</summary>
        public static bool TryRegister<T>(T instance) where T : class
        {
            if (instance == null || _services.ContainsKey(typeof(T))) return false;
            _services[typeof(T)] = instance;
            return true;
        }

        public static T Get<T>() where T : class
        {
            return _services.TryGetValue(typeof(T), out var obj) ? (T)obj : null;
        }

        public static bool TryGet<T>(out T service) where T : class
        {
            bool ok = _services.TryGetValue(typeof(T), out var obj);
            service = ok ? (T)obj : null;
            return ok;
        }

        public static bool IsRegistered<T>() where T : class => _services.ContainsKey(typeof(T));

        public static void Unregister<T>() where T : class => _services.Remove(typeof(T));
        public static void Clear() => _services.Clear();

        /// <summary>
        /// Reflective lookup by full interface type name — for the rare case where a caller
        /// can't reference the assembly that owns the interface (e.g. Gameplay wanting to talk
        /// to <c>LightRunners.AR.IARViewController</c> without depending on the AR assembly,
        /// which is gated behind <c>UNITY_XR_ARFOUNDATION</c>). Returns null if not registered.
        /// </summary>
        public static object GetByInterfaceName(string fullTypeName)
        {
            foreach (var kvp in _services)
            {
                if (kvp.Key.FullName == fullTypeName) return kvp.Value;
                foreach (var iface in kvp.Key.GetInterfaces())
                    if (iface.FullName == fullTypeName) return kvp.Value;
            }
            return null;
        }
    }
}
