#if UNITY_XR_ARFOUNDATION
using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.AR
{
    /// <summary>
    /// One persisted trail projected into AR space (spec §11.3): a LineRenderer with the neon
    /// material, built from a <see cref="TrailSnapshot"/>'s world points.
    /// <see cref="UpdateVisibility"/> culls by nearest-point distance and fades alpha
    /// quadratically with distance.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class ARTrailObject : MonoBehaviour
    {
        private LineRenderer _line;
        private Material _material;
        private Color _baseColor = Color.cyan;
        private readonly List<Vector3> _worldPoints = new List<Vector3>();

        private void Awake()
        {
            _line = GetComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.widthMultiplier = GameConfig.Active.trailWidth;
            _line.numCapVertices = 2;
        }

        public void Build(TrailSnapshot snapshot, Color color, float groundOffset)
        {
            _baseColor = color;
            _worldPoints.Clear();

            var pts = snapshot.Decode();
            foreach (var p in pts)
            {
                Vector3 w = CoordinateConverter.GeoToWorld(p.position);
                w.y += groundOffset;
                _worldPoints.Add(w);
            }

            _line.positionCount = _worldPoints.Count;
            for (int i = 0; i < _worldPoints.Count; i++)
                _line.SetPosition(i, _worldPoints[i]);

            Shader s = Shader.Find("LightRunners/NeonTrailEnhanced");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("Standard");
            _material = new Material(s);
            _material.SetColor("_BaseColor", color);
            if (_material.HasProperty("_EmissionColor"))
                _material.SetColor("_EmissionColor", color * 2f);
            _line.material = _material;
            _line.startColor = _line.endColor = color;
        }

        /// <summary>Cull by nearest-point distance; fade alpha quadratically (spec §11.3).</summary>
        public void UpdateVisibility(Vector3 cameraPos, float maxDistance)
        {
            if (_worldPoints.Count == 0) { _line.enabled = false; return; }

            float nearestSq = float.MaxValue;
            foreach (var w in _worldPoints)
            {
                float d = (w - cameraPos).sqrMagnitude;
                if (d < nearestSq) nearestSq = d;
            }

            float nearest = Mathf.Sqrt(nearestSq);
            if (nearest > maxDistance) { _line.enabled = false; return; }

            _line.enabled = true;
            float t = Mathf.Clamp01(1f - nearest / maxDistance);
            float alpha = t * t; // quadratic fade
            var c = _baseColor; c.a = alpha;
            _line.startColor = _line.endColor = c;
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
#endif
