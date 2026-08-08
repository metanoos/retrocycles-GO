using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Afterglow
{
    // ─── Track F: Afterglow Overview Camera ──────────────────────────────────
    // Top-down orthographic camera framing all captured trails (decision A — completed
    // trails as neon world art). Renders trails as LineRenderer strips at width =
    // FrozenTailRadius × 2 (decision T) and Lumen events as small glowing markers.

    /// <summary>
    /// Top-down orthographic overview of one finished match (decisions A, U, T).
    ///
    /// Frames all trail GeoPoints by computing their bounding box, re-anchoring
    /// <see cref="CoordinateConverter"/> to the package origin, and sizing the
    /// orthographic camera to fit (with a small margin). Trails are drawn as neon line
    /// strips; Lumen-collect events are drawn as small glowing markers.
    ///
    /// Camera controls (milestone): scroll = zoom (orthographic size), drag = pan.
    /// Both implemented; nothing stubbed.
    ///
    /// GAP NOTE for Track A: the milestone renders trails as a simple
    /// <see cref="LineRenderer"/> width-multiplier strip. Track A's capsule-chain tail
    /// rendering can be wired here later by replacing <see cref="BuildTrailLine"/> with a
    /// call into the Trail assembly's neon renderer (decision T preserves the radius so
    /// the swap is a 1:1 width match).
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class OverviewCameraController : MonoBehaviour
    {
        [Header("Framing")]
        [Tooltip("Fraction of the bounding box added as margin around the trails.")]
        [Min(0f)] public float frameMargin = 0.15f;

        [Tooltip("Minimum orthographic size (m) — prevents over-zoom on degenerate trails.")]
        [Min(1f)] public float minOrthoSize = 5f;

        [Tooltip("Maximum orthographic size (m) — clamp on how far out the camera frames.")]
        [Min(10f)] public float maxOrthoSize = 2000f;

        [Header("Visuals")]
        [Tooltip("Lumen marker diameter (m).")]
        [Min(0.05f)] public float lumenMarkerSize = 1.5f;

        [Tooltip("Trail neon color when a per-player color isn't supplied.")]
        public Color defaultTrailColor = new Color(0.2f, 1f, 1f, 1f);

        [Tooltip("Lumen marker color.")]
        public Color lumenMarkerColor = new Color(1f, 0.85f, 0.2f, 1f);

        [Header("Controls")]
        [Tooltip("Zoom speed multiplier on scroll.")]
        [Min(0.01f)] public float zoomSpeed = 0.15f;

        [Tooltip("Pan speed multiplier on drag (screen→world units).")]
        [Min(0.01f)] public float panSpeed = 0.01f;

        private Camera _camera;
        private Transform _container;
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<Material> _materials = new List<Material>();
        private Vector3 _panAnchorWorld;
        private Vector3 _panAnchorScreen;
        private bool _panning;

        public bool IsShown { get; private set; }

        private void Awake()
        {
            EnsureInitialized();
        }

        /// <summary>
        /// Lazily resolve the Camera and container. Awake is not guaranteed to fire in
        /// EditMode tests when AddComponent creates the controller, so Show/Hide must
        /// be safe to call before Unity has invoked Awake.
        /// </summary>
        private void EnsureInitialized()
        {
            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
                if (_camera != null)
                {
                    _camera.orthographic = true;
                    _camera.orthographicSize = minOrthoSize;
                }
            }

            if (_container == null)
            {
                _container = new GameObject("AfterglowOverview_Content").transform;
                _container.SetParent(transform, false);
            }
        }

        private void OnDestroy()
        {
            ClearSpawned();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Show / Hide
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Frame the package's trails and render them. Re-anchors
        /// <see cref="CoordinateConverter"/> to <see cref="ReplayPackage.Origin"/> so
        /// world-space math is centered on the match (decision U).
        /// </summary>
        public void Show(ReplayPackage package)
        {
            EnsureInitialized();
            ClearSpawned();
            if (package == null)
            {
                Debug.LogWarning("[OverviewCameraController] Show(null) — nothing to render.");
                return;
            }

            // Re-anchor CoordinateConverter to the package origin. The Overview camera is
            // positioned in Unity world space; we want world == "metres from match origin".
            CoordinateConverter.SetReference(package.Origin.latitude, package.Origin.longitude);

            float halfWidth = package.FrozenTailRadius; // line strip half-width = tail radius
            int colorIdx = 0;
            foreach (var trail in package.Trails)
            {
                var color = TrailColorFor(colorIdx++);
                BuildTrailLine(trail, color, halfWidth);
            }

            foreach (var lumen in package.Lumens)
                BuildLumenMarker(lumen, package.FrozenTailRadius);

            FrameAll(package);
            gameObject.SetActive(true);
            _camera.enabled = true;
            IsShown = true;
        }

        /// <summary>Hide the camera and clear spawned art.</summary>
        public void Hide()
        {
            IsShown = false;
            EnsureInitialized();
            if (_camera != null) _camera.enabled = false;
            gameObject.SetActive(false);
            ClearSpawned();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Framing
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Position the orthographic camera above the centroid of all trail points and size
        /// its orthographic frustum to fit the bounding box (X = east, Z = north) plus
        /// margin. Empty packages fall back to <see cref="minOrthoSize"/> at origin.
        /// </summary>
        public void FrameAll(ReplayPackage package)
        {
            Vector3 min = new Vector3(float.PositiveInfinity, 0f, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, 0f, float.NegativeInfinity);
            bool any = false;

            foreach (var trail in package.Trails)
            {
                if (trail == null) continue;
                foreach (var p in trail.ToGeoPoints())
                {
                    Vector3 w = CoordinateConverter.GeoToWorld(p);
                    if (w.x < min.x) min.x = w.x;
                    if (w.z < min.z) min.z = w.z;
                    if (w.x > max.x) max.x = w.x;
                    if (w.z > max.z) max.z = w.z;
                    any = true;
                }
            }

            // Also include Lumen markers so the frame doesn't crop a near-miss.
            foreach (var lumen in package.Lumens)
            {
                Vector3 w = CoordinateConverter.GeoToWorld(lumen.At);
                if (w.x < min.x) min.x = w.x;
                if (w.z < min.z) min.z = w.z;
                if (w.x > max.x) max.x = w.x;
                if (w.z > max.z) max.z = w.z;
                any = true;
            }

            if (!any)
            {
                transform.position = new Vector3(0f, 100f, 0f);
                _camera.orthographicSize = minOrthoSize;
                return;
            }

            Vector3 center = new Vector3((min.x + max.x) * 0.5f, 0f, (min.z + max.z) * 0.5f);
            Vector3 size = max - min;
            float margin = 1f + frameMargin;
            float spanX = Mathf.Max(size.x, 1f) * margin;
            float spanZ = Mathf.Max(size.z, 1f) * margin;

            // Orthographic size is half the VERTICAL frustum — account for aspect so the
            // wider axis (often longitude near a tall trail) is what fits.
            float aspect = _camera.aspect > 0.01f ? _camera.aspect : 1f;
            float sizeForZ = spanZ * 0.5f;
            float sizeForX = (spanX * 0.5f) / aspect;
            float ortho = Mathf.Max(sizeForZ, sizeForX);
            _camera.orthographicSize = Mathf.Clamp(ortho, minOrthoSize, maxOrthoSize);

            // Look straight down (camera forward = -Y) at the centroid.
            float height = 100f; // arbitrary; ortho size governs framing, not distance
            transform.position = new Vector3(center.x, height, center.z);
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Trail / marker construction
        // ─────────────────────────────────────────────────────────────────────

        private void BuildTrailLine(TrailCapture trail, Color color, float halfWidth)
        {
            var points = trail.ToGeoPoints();
            if (points.Count == 0) return;

            var go = new GameObject($"AfterglowTrail_{trail.PlayerId}", typeof(LineRenderer));
            go.transform.SetParent(_container, false);
            var line = go.GetComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 w = CoordinateConverter.GeoToWorld(points[i]);
                w.y += halfWidth * 0.5f; // lift slightly above ground
                line.SetPosition(i, w);
            }

            // Decision T: width = FrozenTailRadius × 2 (capsule-chain width match).
            float width = halfWidth * 2f;
            line.startWidth = width;
            line.endWidth = width;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;

            Material mat = NewNeonMaterial(color);
            line.material = mat;
            line.startColor = line.endColor = color;
            _materials.Add(mat);
            _spawned.Add(go);
        }

        private void BuildLumenMarker(LumenEvent lumen, float tailRadius)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"AfterglowLumen_{lumen.PlayerId}_{lumen.TimeSeconds:F1}";
            go.transform.SetParent(_container, false);
            // Strip the default capsule collider so we don't pollute the scene physics.
            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            Vector3 w = CoordinateConverter.GeoToWorld(lumen.At);
            w.y += tailRadius; // hover just above the trail
            go.transform.position = w;
            float diameter = Mathf.Max(lumenMarkerSize, tailRadius * 0.5f);
            go.transform.localScale = new Vector3(diameter, diameter, diameter);

            Material mat = NewNeonMaterial(lumenMarkerColor);
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = mat;
            _materials.Add(mat);
            _spawned.Add(go);
        }

        private Material NewNeonMaterial(Color color)
        {
            Shader s = Shader.Find("LightRunners/NeonTrailEnhanced");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("Standard");
            var mat = new Material(s);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", color * 2f);
            return mat;
        }

        private Color TrailColorFor(int index)
        {
            // Simple palette so multiple players read distinctly; default to defaultTrailColor.
            Color[] palette =
            {
                defaultTrailColor,
                new Color(1f, 0.3f, 0.6f, 1f),
                new Color(0.6f, 1f, 0.3f, 1f),
                new Color(0.9f, 0.5f, 1f, 1f),
                new Color(1f, 0.6f, 0.2f, 1f),
            };
            return palette[index % palette.Length];
        }

        private void ClearSpawned()
        {
            foreach (var go in _spawned)
                if (go != null) Destroy(go);
            _spawned.Clear();
            foreach (var mat in _materials)
                if (mat != null) Destroy(mat);
            _materials.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Camera controls (zoom / pan) — milestone, fully implemented
        // ─────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!IsShown) return;
            HandleZoom();
            HandlePan();
        }

        private void HandleZoom()
        {
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Approximately(scroll, 0f)) return;
            float factor = 1f - Mathf.Sign(scroll) * zoomSpeed;
            _camera.orthographicSize = Mathf.Clamp(
                _camera.orthographicSize * factor, minOrthoSize, maxOrthoSize);
        }

        private void HandlePan()
        {
            if (Input.GetMouseButtonDown(0))
            {
                _panning = true;
                _panAnchorScreen = Input.mousePosition;
                _panAnchorWorld = ScreenToWorldOnGround(Input.mousePosition);
            }
            else if (Input.GetMouseButtonUp(0))
            {
                _panning = false;
            }

            if (!_panning) return;
            Vector3 currentWorld = ScreenToWorldOnGround(Input.mousePosition);
            Vector3 delta = _panAnchorWorld - currentWorld;
            // Convert the world delta to a screen-space pan so it feels 1:1 regardless of
            // ortho size. Simple constant multiplier for the milestone.
            Vector3 move = new Vector3(delta.x, 0f, delta.z) * panSpeed * 100f;
            transform.position += move;
        }

        private Vector3 ScreenToWorldOnGround(Vector3 screen)
        {
            Ray ray = _camera.ScreenPointToRay(screen);
            // Intersect with the y = 0 plane (top-down). Handles orthographic rays cleanly.
            if (Mathf.Abs(ray.direction.y) < 1e-6f) return transform.position;
            float t = -ray.origin.y / ray.direction.y;
            return ray.origin + ray.direction * t;
        }
    }
}
