using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// Three-band distance LOD for trail renderers (spec §7.5): full-res under 20 m, medium
    /// under 50 m, low under 100 m, culled beyond. Updates periodically, not per-frame.
    /// </summary>
    public class TrailLODManager : MonoBehaviour
    {
        [SerializeField] private float nearBand = 20f;
        [SerializeField] private float midBand = 50f;
        [SerializeField] private float farBand = 100f;
        [SerializeField, Range(0.1f, 5f)] private float updateInterval = 0.5f;

        private readonly List<NeonTrailRenderer> _renderers = new List<NeonTrailRenderer>();
        private float _timer;

        public void Register(NeonTrailRenderer r)
        {
            if (r != null && !_renderers.Contains(r)) _renderers.Add(r);
        }

        public void Unregister(NeonTrailRenderer r) => _renderers.Remove(r);

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = updateInterval;

            Vector3 cam = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                var line = r.GetComponent<LineRenderer>();
                if (line == null) continue;

                // Distance from camera to the line's mid point (or first point if empty).
                Vector3 sample = line.positionCount > 0 ? line.GetPosition(line.positionCount / 2) : r.transform.position;
                float d = Vector3.Distance(cam, sample);

                if (d > farBand)
                {
                    line.enabled = false;
                    continue;
                }
                line.enabled = true;
                if (d < nearBand) line.widthMultiplier = 1.0f;
                else if (d < midBand) line.widthMultiplier = 0.75f;
                else line.widthMultiplier = 0.5f;
            }
        }
    }
}
