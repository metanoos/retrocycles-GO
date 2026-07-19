using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Owns one world-space <see cref="NeonTrailRenderer"/> per live trail (local + remote).
    /// The renderers are world-space, so they're visible to both the top-down map camera and
    /// the AR camera without duplication. Registers each with the <see cref="TrailLODManager"/>.
    /// </summary>
    public class TrailRenderingManager : MonoBehaviour
    {
        [SerializeField] private TrailLODManager lodManager;

        private readonly Dictionary<string, NeonTrailRenderer> _renderers = new Dictionary<string, NeonTrailRenderer>();

        private void Start()
        {
            if (lodManager == null) lodManager = FindAnyObjectByType<TrailLODManager>();
            if (TrailManager.HasInstance)
            {
                TrailManager.Instance.OnLocalPointAdded += OnLocalPointAdded;
                TrailManager.Instance.OnRemoteTrailUpdated += OnRemoteTrailUpdated;
                TrailManager.Instance.OnRemoteTrailRemoved += RemoveRenderer;
                TrailManager.Instance.OnTrailCrashed += OnLocalTrailEnded;
            }
        }

        private void OnDestroy()
        {
            if (TrailManager.HasInstance)
            {
                TrailManager.Instance.OnLocalPointAdded -= OnLocalPointAdded;
                TrailManager.Instance.OnRemoteTrailUpdated -= OnRemoteTrailUpdated;
                TrailManager.Instance.OnRemoteTrailRemoved -= RemoveRenderer;
                TrailManager.Instance.OnTrailCrashed -= OnLocalTrailEnded;
            }
        }

        private void OnLocalPointAdded(TrailPoint _)
        {
            var trail = TrailManager.HasInstance ? TrailManager.Instance.LocalTrail : null;
            if (trail != null) EnsureRenderer(trail, isLocal: true);
        }

        private void OnRemoteTrailUpdated(string playerId)
        {
            if (!TrailManager.HasInstance) return;
            if (TrailManager.Instance.AllTrails.TryGetValue(playerId, out var trail) && trail != null)
                EnsureRenderer(trail, isLocal: false);
        }

        private void OnLocalTrailEnded(bool crashed)
        {
            var trail = TrailManager.HasInstance ? TrailManager.Instance.LocalTrail : null;
            if (trail != null) RemoveRenderer(trail.OwnerId);
        }

        private void EnsureRenderer(TrailData trail, bool isLocal)
        {
            if (_renderers.TryGetValue(trail.OwnerId, out var existing) && existing != null)
                return; // NeonTrailRenderer.Sync runs in its own LateUpdate

            var go = new GameObject($"Trail_{trail.OwnerId}", typeof(LineRenderer), typeof(NeonTrailRenderer));
            go.transform.SetParent(transform, false);
            var r = go.GetComponent<NeonTrailRenderer>();
            r.Initialize(trail, isLocal);
            _renderers[trail.OwnerId] = r;
            lodManager?.Register(r);
        }

        private void RemoveRenderer(string playerId)
        {
            if (_renderers.TryGetValue(playerId, out var r) && r != null)
            {
                lodManager?.Unregister(r);
                Destroy(r.gameObject);
            }
            _renderers.Remove(playerId);
        }
    }
}
