using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Lightfield
{
    /// <summary>
    /// Contract a runner-tagged GameObject exposes so a <see cref="LumenGate"/> (or
    /// <see cref="StolenLumenPickup"/>) can resolve the colliding runner's player id. Implement
    /// on the runner's avatar root OR a sibling component (e.g. the <c>BeaconController</c>'s
    /// parent, or a thin <c>RunnerTag</c> MonoBehaviour). Track D wires this on the local +
    /// networked player prefabs; the milestone ships with the interface and a TODO so a missing
    /// implementation is logged rather than crashing. Decisions G, L.
    /// </summary>
    public interface IRunnerIdentity
    {
        /// <summary>Auth.uid of the runner that owns this collider (or empty if unknown).</summary>
        string PlayerId { get; }
    }

    /// <summary>
    /// A glowing Lumen Gate anchored to the ground (decision G): a hemisphere visual (the lower
    /// half of a sphere buried at <c>transform.position.y</c>) plus a single spherical trigger
    /// volume of radius <c>GameConfig.Active.gateCollectionRadius</c> (decision L). A runner
    /// entering the trigger fires <see cref="GameEvents.RaiseGateCollected"/> exactly once; the
    /// authoritative <see cref="GateSpawner"/> (host) reacts via its bus hook and respawns a
    /// replacement elsewhere (decision M).
    ///
    /// Ground-only milestone (decision S): always a hemisphere. Aerial (full orb) is stubbed —
    /// <see cref="Initialize"/> accepts <see cref="GatePlacement.Aerial"/> but logs a warning
    /// and renders as ground. Aerial milestone TODO: render a full orb at altitude.
    ///
    /// Visual style mirrors <see cref="BeaconController"/>: emissive material, point light,
    /// glow particles — all built in code so the scene runs with zero art.
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    public class LumenGate : MonoBehaviour
    {
        /// <summary>Tag used by runner-tagged colliders. Decision G — collection filter.</summary>
        public const string RunnerTag = "Runner";

        private GateId? _gateId;
        private GatePlacement _placement;
        private bool _collected;

        private GameObject _visualRoot;
        private Light _light;
        private ParticleSystem _glow;
        private SphereCollider _trigger;

        /// <summary>The opaque id assigned by <see cref="GateSpawner"/>. Null until initialized.</summary>
        public GateId? GateId => _gateId;

        /// <summary>True once this gate has fired a collection event (prevents double-collect).</summary>
        public bool IsCollected => _collected;

        private void Awake()
        {
            _trigger = GetComponent<SphereCollider>();
            if (_trigger == null) _trigger = gameObject.AddComponent<SphereCollider>();
            _trigger.isTrigger = true;
            _trigger.radius = GameConfig.Active.gateCollectionRadius;

            EnsureRig();
        }

        private void EnsureRig()
        {
            float radius = GameConfig.Active.gateCollectionRadius;

            if (_visualRoot == null)
            {
                _visualRoot = new GameObject("Hemisphere");
                _visualRoot.transform.SetParent(transform, false);
            }

            // Emissive hemisphere: a sphere whose lower half is below the ground. Half-buried =
            // hemisphere visible above the floor (decision L). Local Y = 0 sits at the anchor.
            if (_visualRoot.transform.childCount == 0)
            {
                var orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                // Strip physics from the visual; the trigger on the parent is the authoritative collider.
                var orbCol = orb.GetComponent<Collider>();
                if (orbCol != null) Destroy(orbCol);
                orb.transform.SetParent(_visualRoot.transform, false);
                orb.transform.localScale = Vector3.one * radius * 2f;
                orb.transform.localPosition = Vector3.zero;
                orb.GetComponent<MeshRenderer>().material = MakeGlowMaterial(Color.cyan);
            }

            if (_light == null)
            {
                var lightGo = new GameObject("GateLight");
                lightGo.transform.SetParent(transform, false);
                lightGo.transform.localPosition = Vector3.up * (radius * 0.5f);
                _light = lightGo.AddComponent<Light>();
                _light.type = LightType.Point;
                _light.color = Color.cyan;
                _light.range = radius * 4f;
                _light.intensity = GameConfig.Active.beaconGlowIntensity;
            }

            if (_glow == null)
            {
                var psGo = new GameObject("GateParticles");
                psGo.transform.SetParent(transform, false);
                _glow = psGo.AddComponent<ParticleSystem>();
                var main = _glow.main;
                main.startSize = radius * 0.15f;
                main.startLifetime = 1.2f;
                main.startSpeed = 0.3f;
                main.maxParticles = 48;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var em = _glow.emission;
                em.rateOverTime = 10f;
                var shape = _glow.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = radius * 0.5f;
            }
        }

        /// <summary>
        /// Place this gate at <paramref name="at"/> (geo) and tag it with <paramref name="id"/>.
        /// Resolves the world position via <see cref="CoordinateConverter.GeoToWorld"/> — the
        /// <c>LightRunners.Location</c> (and <c>Core</c>) reference covers the seam the same way
        /// <c>BeaconController</c>'s callers feed world positions to <c>UpdatePosition</c>.
        /// Decision G/L.
        /// </summary>
        public void Initialize(GateId id, GeoPoint at, GatePlacement placement)
        {
            _gateId = id;
            _placement = placement;
            _collected = false;

            if (placement == GatePlacement.Aerial)
            {
                // Decision S/L stub: aerial deferred for the milestone. Log + force ground.
                Debug.LogWarning($"[LumenGate] Aerial placement requested for {id} but aerial milestone is deferred (decision S/L); rendering as ground hemisphere.");
                _placement = GatePlacement.Ground;
            }

            CoordinateConverter.EnsureReference(at);
            Vector3 world = CoordinateConverter.GeoToWorld(at);
            // Anchor at ground level: the half-buried sphere exposes a hemisphere above y=0.
            transform.position = world;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_collected || !_gateId.HasValue) return;
            if (other == null) return;

            // Cheap tag filter first; fall back to component probe if Track D hasn't tagged yet.
            if (!other.CompareTag(RunnerTag) && other.GetComponentInParent<IRunnerIdentity>() == null)
                return;

            string collector = ResolveCollectorPlayerId(other);
            if (string.IsNullOrEmpty(collector))
            {
                // TODO(track-D): wire IRunnerIdentity on the runner prefab + tag the collider
                // "Runner". Until then we collect with an unknown id so the match still flows.
                Debug.LogWarning($"[LumenGate] Triggered by {other.name} but no {nameof(IRunnerIdentity)} found; collecting as \"unknown\".");
                collector = "unknown";
            }

            _collected = true;
            GameEvents.RaiseGateCollected(_gateId.Value.Value, collector);
        }

        private static string ResolveCollectorPlayerId(Collider other)
        {
            // Prefer an explicit IRunnerIdentity up the hierarchy (Track D contract).
            var identity = other.GetComponentInParent<IRunnerIdentity>();
            if (identity != null && !string.IsNullOrEmpty(identity.PlayerId))
                return identity.PlayerId;

            return string.Empty;
        }

        private static Material MakeGlowMaterial(Color c)
        {
            // Same fallback ladder as BeaconController: project shader → URP/Lit → Standard.
            Shader s = Shader.Find("LightRunners/BeaconGlow");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("Standard");
            var m = new Material(s) { name = "GateGlow_runtime" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * GameConfig.Active.beaconGlowIntensity);
            }
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.6f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.7f);
            return m;
        }
    }
}
