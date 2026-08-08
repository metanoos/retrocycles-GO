using UnityEngine;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Beacon;
using LightRunners.Lightfield;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Drives the local player's world-space beacon when no Fusion NetworkPlayer exists
    /// (solo / offline race). With Fusion connected, NetworkPlayer owns the local beacon and
    /// this driver stands down (it checks GameManager.OnlineRace). Keeps phase-5 beacons
    /// visible in plain editor playmode.
    /// </summary>
    public class LocalBeaconDriver : MonoBehaviour
    {
        private BeaconController _beacon;
        private float _heading;
        private Vector3 _lastWorld;
        private bool _haveLast;

        private void OnEnable()
        {
            if (GameManager.HasInstance)
                GameManager.Instance.OnStateChanged += OnStateChanged;
        }

        private void OnDisable()
        {
            if (GameManager.HasInstance)
                GameManager.Instance.OnStateChanged -= OnStateChanged;
        }

        private void OnStateChanged(GameState prev, GameState next)
        {
            if (next == GameState.Running) SpawnIfNeeded();
            else if (next == GameState.Lobby || next == GameState.Crashed) Despawn();
        }

        private void SpawnIfNeeded()
        {
            // Fusion owns the avatar when a room connection is live.
            if (GameManager.HasInstance && GameManager.Instance.OnlineRace == true) { Despawn(); return; }
            if (_beacon != null) return;

            var go = new GameObject("LocalBeacon");
            go.transform.SetParent(transform, false);
            _beacon = go.AddComponent<BeaconController>();
            go.AddComponent<BeaconEffects>();

            // Round-1 review fix R2-F7: wire LocalRunnerIdentity + "Runner" tag + a trigger
            // collider so LumenGate / StolenLumenPickup OnTriggerEnter can resolve the collector.
            // Without this, every gate collection credits player id "unknown" and corrupts the
            // leaderboard, Afterglow finish order, and Decision-I crown. A small SphereCollider
            // stands in for the runner's "body" for trigger purposes (the AR camera itself has no
            // physics body). For the Fusion path (NetworkPlayer owns the avatar), the equivalent
            // wiring is a documented follow-up on first Fusion SDK import.
            var identity = go.AddComponent<LocalRunnerIdentity>();
            if (GameManager.HasInstance && !string.IsNullOrEmpty(GameManager.Instance.LocalPlayerId))
                identity.SetPlayerId(GameManager.Instance.LocalPlayerId);
            var bodyCollider = go.AddComponent<SphereCollider>();
            bodyCollider.isTrigger = true;
            bodyCollider.radius = FrozenMatchConfig.Default.PlayerHeadRadiusMeters;
            var body = go.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var form = GameManager.HasInstance ? GameManager.Instance.CurrentForm : BeaconFormType.Hoverboard;
            _beacon.SetForm(form);
            _beacon.SetTrailColor(BeaconFormManager.HasInstance
                ? BeaconFormManager.Instance.GetTrailColor(form)
                : Color.cyan);
            _haveLast = false;
        }

        private void Despawn()
        {
            if (_beacon != null)
            {
                Destroy(_beacon.gameObject);
                _beacon = null;
            }
        }

        private void Update()
        {
            if (_beacon == null || !LocationProvider.HasInstance) return;
            if (GameManager.HasInstance && GameManager.Instance.OnlineRace == true) { Despawn(); return; }

            var geo = LocationProvider.Instance.CurrentPosition;
            CoordinateConverter.EnsureReference(geo);
            Vector3 w = CoordinateConverter.GeoToWorld(geo);
            if (_haveLast && (w - _lastWorld).sqrMagnitude > 0.0001f)
                _heading = Mathf.Atan2(w.x - _lastWorld.x, w.z - _lastWorld.z) * Mathf.Rad2Deg;
            _lastWorld = w;
            _haveLast = true;

            _beacon.UpdatePosition(w, _heading);
        }
    }
}
