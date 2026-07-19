using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// Builds a glowing line for a trail, incrementally appending only points newer than the
    /// last rendered **sequence number** (spec §7.5; sequence-keyed per pitfall #19 so pruning
    /// can't desync the renderer). World-space, ground-offset on Y.
    ///
    /// Discontinuities (spec §20): a point flagged <c>isSegmentStart</c> freezes the current
    /// polyline into a child "strip" LineRenderer and starts a fresh one, so pause/dropout
    /// gaps are never drawn as walls.
    ///
    /// Uses <c>LightRunners/NeonTrailEnhanced</c> if present (phase 11), else cascades to
    /// URP/Lit then Standard so the project runs before custom shaders exist.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class NeonTrailRenderer : MonoBehaviour
    {
        [SerializeField] private Color trailColor = Color.cyan;
        [SerializeField] private float width = 0.5f;
        [SerializeField] private float groundOffset = 0.3f;
        [SerializeField] private float emissionBoost = 2.0f;

        private LineRenderer _line;
        private Material _material;
        private TrailData _data;
        private int _lastRenderedSequence = -1;
        private readonly List<LineRenderer> _strips = new List<LineRenderer>();

        // Owner tag — set so the LOD manager can decide to keep / cull us.
        public string OwnerId { get; private set; }
        public bool IsLocal { get; private set; }

        public void Initialize(TrailData data, bool isLocal)
        {
            _data = data;
            IsLocal = isLocal;
            OwnerId = data?.OwnerId;
            trailColor = data?.TrailColor ?? trailColor;
            ApplyMaterialAndColor();
            RebuildNow();
        }

        public void SetTrailColor(Color c)
        {
            trailColor = c;
            if (_material != null)
            {
                _material.SetColor("_BaseColor", c);
                _material.SetColor("_EmissionColor", c * emissionBoost);
            }
            if (_line != null) _line.startColor = _line.endColor = c;
            foreach (var s in _strips)
                if (s != null) s.startColor = s.endColor = c;
        }

        private void Awake()
        {
            _line = GetComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.positionCount = 0;
            _line.widthMultiplier = width;
            _line.numCornerVertices = 4;
            _line.numCapVertices = 2;
            _line.alignment = LineAlignment.View;
            ApplyMaterialAndColor();
        }

        private void ApplyMaterialAndColor()
        {
            if (_material == null)
            {
                Shader s = Shader.Find("LightRunners/NeonTrailEnhanced");
                if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
                if (s == null) s = Shader.Find("Standard");
                if (s != null) _material = new Material(s);
                if (_material != null) _material.name = $"NeonTrail_{OwnerId ?? "anon"}";
            }
            if (_material != null)
            {
                _material.SetColor("_BaseColor", trailColor);
                _material.SetColor("_EmissionColor", trailColor * emissionBoost);
                _material.SetFloat("_Width", width);
                if (_line != null) _line.material = _material;
            }
            if (_line != null)
            {
                _line.startColor = _line.endColor = trailColor;
                _line.startWidth = _line.endWidth = width;
            }
        }

        /// <summary>Sync the LineRenderer with whatever new points the trail has.</summary>
        public void Sync()
        {
            if (_data == null || _line == null) return;

            // Trail restarted (new run reuses this renderer) — the cursor went backwards.
            if (_data.HighestAppliedSequence < _lastRenderedSequence)
            {
                RebuildNow();
                return;
            }
            if (_data.HighestAppliedSequence == _lastRenderedSequence) return;

            // New points are a contiguous run at the tail of the list. Walk back to the first
            // point newer than what we've rendered (new points per tick are few).
            var pts = _data.Points;
            int firstNew = pts.Count;
            while (firstNew > 0 && pts[firstNew - 1].ownerSequenceIndex > _lastRenderedSequence)
                firstNew--;

            for (int i = firstNew; i < pts.Count; i++)
                AppendPoint(pts[i]);
            _lastRenderedSequence = _data.HighestAppliedSequence;

            // Bounded memory: pruning drops points from the data but this line keeps its old
            // positions. Once we exceed the cap comfortably, rebuild from surviving data.
            int cap = GameConfig.Active.maxTrailPoints;
            if (_line.positionCount > cap + cap / 4) RebuildNow();
        }

        private void AppendPoint(TrailPoint p)
        {
            if (p.isSegmentStart && _line.positionCount > 0)
                FreezeStrip();

            CoordinateConverter.EnsureReference(p.position);
            Vector3 w = CoordinateConverter.GeoToWorld(p.position);
            w.y += groundOffset;
            int n = _line.positionCount;
            _line.positionCount = n + 1;
            _line.SetPosition(n, w);
        }

        /// <summary>Move the current polyline into a frozen child strip and start a fresh one (spec §20).</summary>
        private void FreezeStrip()
        {
            var go = new GameObject($"Strip_{_strips.Count}");
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = _line.widthMultiplier;
            lr.numCornerVertices = _line.numCornerVertices;
            lr.numCapVertices = _line.numCapVertices;
            lr.alignment = _line.alignment;
            lr.material = _line.material;
            lr.startColor = _line.startColor;
            lr.endColor = _line.endColor;
            lr.startWidth = _line.startWidth;
            lr.endWidth = _line.endWidth;

            var positions = new Vector3[_line.positionCount];
            _line.GetPositions(positions);
            lr.positionCount = positions.Length;
            lr.SetPositions(positions);
            _strips.Add(lr);

            _line.positionCount = 0;
        }

        public void RebuildNow()
        {
            foreach (var s in _strips)
                if (s != null) Destroy(s.gameObject);
            _strips.Clear();

            _lastRenderedSequence = -1;
            if (_line != null) _line.positionCount = 0;
            if (_data == null) return;

            foreach (var p in _data.Points)
                AppendPoint(p);
            _lastRenderedSequence = _data.HighestAppliedSequence;
        }

        private void LateUpdate() => Sync();

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
