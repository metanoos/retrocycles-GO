#if FUSION_WEAVER
using UnityEngine;
using Fusion;
using LightRunners.Core;
using LightRunners.Location;
using LightRunners.Trail;
using LightRunners.Beacon;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// The networked avatar (spec §8.3). Local authority reads GPS, drives the networked
    /// position/heading, and runs the collision check; proxies mirror the networked state
    /// into their beacon visual. Crash raises <see cref="GameEvents.RaisePlayerCrashed"/> —
    /// NEVER a direct GameManager call (would be a circular assembly ref).
    /// </summary>
    public class NetworkPlayer : NetworkBehaviour
    {
        [Networked] public NetworkString<_64> PlayerId { get; set; }
        [Networked] public int BeaconForm { get; set; }
        [Networked] public NetworkBool IsCrashed { get; set; }
        [Networked] public float PositionX { get; set; }
        [Networked] public float PositionY { get; set; }
        [Networked] public float PositionZ { get; set; }
        [Networked] public float Heading { get; set; }

        private BeaconController _beacon;
        private TrailCollisionDetector _detector;
        private GeoPoint _lastPos;
        private bool _haveLast;
        private bool _crashHandled;

        public bool IsLocalAuthority => Object != null && Object.HasStateAuthority;

        /// <summary>Set by FusionLauncher right after Spawn (local authority only).</summary>
        public void StampLocalIdentity(string playerId)
        {
            if (!IsLocalAuthority) return;
            PlayerId = playerId;
            var form = BeaconFormManager.HasInstance
                ? BeaconFormManager.Instance.SelectedForm
                : BeaconFormType.Hoverboard;
            BeaconForm = (int)form;
        }

        public override void Spawned()
        {
            // Beacon visual on a child GO (spec §8.3).
            var beaconGo = new GameObject("Beacon");
            beaconGo.transform.SetParent(transform, false);
            _beacon = beaconGo.AddComponent<BeaconController>();
            beaconGo.AddComponent<BeaconEffects>();
            ApplyForm();

            if (IsLocalAuthority)
            {
                // Collision detector wired to OnCrash (spec §8.3). The §8.4 fallback detector
                // also exists on GameManager; the crash pipeline's double-fire guard makes
                // the overlap safe.
                _detector = gameObject.AddComponent<TrailCollisionDetector>();
                _detector.OnCollisionDetected += OnCrash;
                if (LocationProvider.HasInstance)
                    _detector.BeginRun(LocationProvider.Instance.CurrentPosition);

                // Idempotent — GameManager.StartRun already ran (pitfall #2).
                if (TrailManager.HasInstance)
                {
                    var form = (BeaconFormType)BeaconForm;
                    var color = BeaconFormManager.HasInstance
                        ? BeaconFormManager.Instance.GetTrailColor(form)
                        : Color.cyan;
                    TrailManager.Instance.StartRun(PlayerId.ToString(), form, color);
                }
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (_detector != null) _detector.OnCollisionDetected -= OnCrash;
            if (!IsLocalAuthority && TrailManager.HasInstance)
                TrailManager.Instance.RemoveRemoteTrail(PlayerId.ToString());
        }

        public override void FixedUpdateNetwork()
        {
            if (IsLocalAuthority)
                SyncLocalPosition();
            else
                MirrorRemoteState();
        }

        private void SyncLocalPosition()
        {
            if (!LocationProvider.HasInstance) return;
            GeoPoint pos = LocationProvider.Instance.CurrentPosition;
            CoordinateConverter.EnsureReference(pos);
            Vector3 w = CoordinateConverter.GeoToWorld(pos);

            PositionX = w.x;
            PositionY = w.y;
            PositionZ = w.z;
            if (_haveLast && _lastPos != pos)
                Heading = (float)CoordinateConverter.Bearing(_lastPos, pos);

            _beacon?.UpdatePosition(w, Heading);

            if (!IsCrashed && _detector != null && _haveLast)
                _detector.CheckCollision(pos, _lastPos, PlayerId.ToString());

            _lastPos = pos;
            _haveLast = true;
        }

        private void MirrorRemoteState()
        {
            ApplyForm();
            var w = new Vector3(PositionX, PositionY, PositionZ);
            _beacon?.UpdatePosition(w, Heading);

            if (IsCrashed && !_crashHandled)
            {
                _crashHandled = true;
                _beacon?.PlayCrashEffect();
            }
        }

        private int _appliedForm = -1;

        private void ApplyForm()
        {
            if (_appliedForm == BeaconForm || _beacon == null) return;
            _appliedForm = BeaconForm;
            var form = (BeaconFormType)BeaconForm;
            var color = BeaconFormManager.HasInstance
                ? BeaconFormManager.Instance.GetTrailColor(form)
                : Color.cyan;
            _beacon.SetForm(form);
            _beacon.SetTrailColor(color);
            if (!IsLocalAuthority && TrailManager.HasInstance)
                TrailManager.Instance.SetRemoteTrailStyle(PlayerId.ToString(), form, color);
        }

        /// <summary>Crash entry (spec §8.3): flag, FX, end trail, raise the bus event.</summary>
        private void OnCrash(string causedByPlayerId)
        {
            if (IsCrashed) return;
            IsCrashed = true;
            _beacon?.PlayCrashEffect();
            GameEvents.RaisePlayerCrashed(causedByPlayerId);
        }
    }
}
#endif
