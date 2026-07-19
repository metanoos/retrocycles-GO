using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Location;

namespace LightRunners.Lightfield
{
    /// <summary>
    /// Subscribes to <see cref="IGateDirector.GateSpawned"/>/<see cref="GateDespawned"/> and
    /// instantiates/destroys <c>Resources/Gates/LumenGate.prefab</c> at each gate's world
    /// position so runners can actually collide with them.
    ///
    /// Round-1 review fix R2-F2: previously the <c>GateSpawner</c> raised <c>GateSpawned</c>
    /// events and tracked pure-C# <c>LumenGateState</c> records, but NOTHING in any runtime
    /// path loaded the prefab or instantiated a <see cref="LumenGate"/>. Every gate-collection
    /// test passed (the C# logic is sound) but no <c>OnTriggerEnter</c> could ever fire in
    /// production — the entire gate-collection loop was dead code in any environment. This
    /// component closes that gap. Decisions G, L, M.
    ///
    /// Subscribes to the static <see cref="GameEvents.GateSpawned"/>/<see cref="GameEvents.GateDespawned"/>
    /// bus (so it works whether or not a concrete IGateDirector is registered) and resolves the
    /// world position via <see cref="CoordinateConverter"/> (Location assembly).
    /// </summary>
    public class LumenGateVisualizer : MonoBehaviour
    {
        private const string PrefabPath = "Gates/LumenGate";

        [Tooltip("Optional override; if unset, loads from Resources/" + PrefabPath + ".")]
        [SerializeField] private GameObject gatePrefabOverride;

        private readonly Dictionary<int, GameObject> _instances = new Dictionary<int, GameObject>();
        private GameObject _prefab;
        private bool _hooked;

        private void OnEnable()
        {
            _prefab = gatePrefabOverride != null
                ? gatePrefabOverride
                : Resources.Load<GameObject>(PrefabPath);
            if (_prefab == null)
            {
                Debug.LogWarning($"[LumenGateVisualizer] No gate prefab at Resources/{PrefabPath}; gates will not render. Run 'Light-Runners → Setup → Gate Prefabs'.");
                return;
            }
            if (_hooked) return;
            GameEvents.GateSpawned += OnGateSpawned;
            GameEvents.GateDespawned += OnGateDespawned;
            _hooked = true;
        }

        private void OnDisable()
        {
            if (!_hooked) return;
            GameEvents.GateSpawned -= OnGateSpawned;
            GameEvents.GateDespawned -= OnGateDespawned;
            _hooked = false;
            // Destroy any instances we created so we don't leak across scene loads / disable.
            foreach (var kvp in _instances)
            {
                if (kvp.Value != null) Destroy(kvp.Value);
            }
            _instances.Clear();
        }

        private void OnGateSpawned(int gateIdValue, double lat, double lon, double alt, GatePlacement placement)
        {
            if (_prefab == null) return;
            if (_instances.ContainsKey(gateIdValue)) return; // idempotent

            var geo = new GeoPoint(lat, lon, alt);
            Vector3 world = CoordinateConverter.GeoToWorld(geo);

            GameObject go = Instantiate(_prefab, world, Quaternion.identity);
            go.name = $"LumenGate_{gateIdValue}";
            var gate = go.GetComponent<LumenGate>();
            if (gate != null)
            {
                gate.Initialize(new GateId(gateIdValue), geo, placement);
            }
            else
            {
                Debug.LogWarning($"[LumenGateVisualizer] Prefab at Resources/{PrefabPath} has no LumenGate component.");
            }
            _instances[gateIdValue] = go;
        }

        private void OnGateDespawned(int gateIdValue)
        {
            if (_instances.TryGetValue(gateIdValue, out var go) && go != null)
            {
                Destroy(go);
            }
            _instances.Remove(gateIdValue);
        }
    }
}