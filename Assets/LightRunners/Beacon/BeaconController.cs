using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Beacon
{
    /// <summary>
    /// The player avatar visual (spec §9.2): model root, glow particles, a light, and a Unity
    /// trail renderer. <see cref="SetForm"/> loads <c>Resources/Beacons/&lt;prefabName&gt;</c>
    /// or, if the prefab is missing/empty, builds a primitive fallback mesh in code — each of
    /// the 8 forms has a distinct primitive composition, so the game runs with zero art AND
    /// forms stay shape-distinct for colorblind players (spec §24).
    /// </summary>
    public class BeaconController : MonoBehaviour
    {
        private GameObject _modelRoot;
        private Light _light;
        private ParticleSystem _glowParticles;
        private TrailRenderer _unityTrail;

        private BeaconFormType _form = BeaconFormType.Hoverboard;
        private Color _color = Color.cyan;
        private float _bobPhase;
        private float _spinDegrees;

        public BeaconFormType Form => _form;
        public Color TrailColor => _color;

        private void Awake()
        {
            EnsureRig();
        }

        private void EnsureRig()
        {
            if (_modelRoot == null)
            {
                _modelRoot = new GameObject("Model");
                _modelRoot.transform.SetParent(transform, false);
            }

            if (_light == null)
            {
                var lightGo = new GameObject("GlowLight");
                lightGo.transform.SetParent(transform, false);
                lightGo.transform.localPosition = Vector3.up * 0.5f;
                _light = lightGo.AddComponent<Light>();
                _light.type = LightType.Point;
                _light.range = 6f;
                _light.intensity = GameConfig.Active.beaconGlowIntensity;
            }

            if (_glowParticles == null)
            {
                var psGo = new GameObject("GlowParticles");
                psGo.transform.SetParent(transform, false);
                _glowParticles = psGo.AddComponent<ParticleSystem>();
                var main = _glowParticles.main;
                main.startSize = 0.15f;
                main.startLifetime = 1.2f;
                main.startSpeed = 0.4f;
                main.maxParticles = 64;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var emission = _glowParticles.emission;
                emission.rateOverTime = 12f;
                var shape = _glowParticles.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.4f;
            }

            if (_unityTrail == null)
            {
                var trailGo = new GameObject("MotionTrail");
                trailGo.transform.SetParent(transform, false);
                _unityTrail = trailGo.AddComponent<TrailRenderer>();
                _unityTrail.time = 0.8f;
                _unityTrail.startWidth = 0.35f;
                _unityTrail.endWidth = 0.02f;
                _unityTrail.material = MakeGlowMaterial(_color);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Form
        // ─────────────────────────────────────────────────────────────────────
        public void SetForm(BeaconFormType form)
        {
            EnsureRig();
            _form = form;

            // Clear the previous model.
            for (int i = _modelRoot.transform.childCount - 1; i >= 0; i--)
                Destroy(_modelRoot.transform.GetChild(i).gameObject);

            string prefabName = BeaconFormManager.HasInstance
                ? BeaconFormManager.Instance.GetPrefabName(form)
                : form.ToString();

            var prefab = Resources.Load<GameObject>($"Beacons/{prefabName}");
            bool prefabHasMesh = false;
            if (prefab != null)
            {
                var instance = Instantiate(prefab, _modelRoot.transform, false);
                prefabHasMesh = instance.GetComponentInChildren<MeshRenderer>() != null;
                if (!prefabHasMesh) Destroy(instance);
            }

            if (!prefabHasMesh)
                BuildFallbackMesh(form);

            float s = GameConfig.Active.beaconBaseScale;
            _modelRoot.transform.localScale = Vector3.one * s;
        }

        /// <summary>Distinct primitive composition per form (spec §9.2). Zero-art fallback.</summary>
        private void BuildFallbackMesh(BeaconFormType form)
        {
            switch (form)
            {
                case BeaconFormType.Hoverboard:
                    // Cuboid hoverboard: a flat stretched cube.
                    AddPrimitive(PrimitiveType.Cube, new Vector3(0, 0.15f, 0), new Vector3(0.5f, 0.08f, 1.4f));
                    break;

                case BeaconFormType.Sphere:
                    AddPrimitive(PrimitiveType.Sphere, new Vector3(0, 0.5f, 0), Vector3.one * 0.7f);
                    break;

                case BeaconFormType.Drone:
                    // Cylinder body with 4 rotor spheres.
                    AddPrimitive(PrimitiveType.Cylinder, new Vector3(0, 0.4f, 0), new Vector3(0.5f, 0.12f, 0.5f));
                    for (int i = 0; i < 4; i++)
                    {
                        float a = i * Mathf.PI / 2f + Mathf.PI / 4f;
                        AddPrimitive(PrimitiveType.Sphere,
                            new Vector3(Mathf.Cos(a) * 0.45f, 0.45f, Mathf.Sin(a) * 0.45f),
                            Vector3.one * 0.18f);
                    }
                    break;

                case BeaconFormType.AbstractShape:
                    BuildTetrahedron(new Vector3(0, 0.5f, 0), 0.8f);
                    break;

                case BeaconFormType.FloatingCube:
                    AddPrimitive(PrimitiveType.Cube, new Vector3(0, 0.55f, 0), Vector3.one * 0.6f);
                    break;

                case BeaconFormType.Motorcycle:
                    // Chassis + two wheels.
                    AddPrimitive(PrimitiveType.Cube, new Vector3(0, 0.35f, 0), new Vector3(0.3f, 0.3f, 1.2f));
                    AddPrimitive(PrimitiveType.Cylinder, new Vector3(0, 0.25f, 0.55f), new Vector3(0.5f, 0.06f, 0.5f), new Vector3(0, 0, 90));
                    AddPrimitive(PrimitiveType.Cylinder, new Vector3(0, 0.25f, -0.55f), new Vector3(0.5f, 0.06f, 0.5f), new Vector3(0, 0, 90));
                    break;

                case BeaconFormType.Phoenix:
                    // Capsule body with two wing quads.
                    AddPrimitive(PrimitiveType.Capsule, new Vector3(0, 0.6f, 0), new Vector3(0.35f, 0.5f, 0.35f), new Vector3(90, 0, 0));
                    AddPrimitive(PrimitiveType.Quad, new Vector3(0.55f, 0.65f, 0), new Vector3(0.9f, 0.4f, 1f), new Vector3(0, 0, 25));
                    AddPrimitive(PrimitiveType.Quad, new Vector3(-0.55f, 0.65f, 0), new Vector3(0.9f, 0.4f, 1f), new Vector3(0, 0, -25));
                    break;

                case BeaconFormType.Waveform:
                    // A row of sine-height bars.
                    for (int i = 0; i < 7; i++)
                    {
                        float x = (i - 3) * 0.16f;
                        float h = 0.25f + 0.35f * Mathf.Abs(Mathf.Sin(i * 0.9f));
                        AddPrimitive(PrimitiveType.Cube, new Vector3(x, h * 0.5f + 0.1f, 0), new Vector3(0.1f, h, 0.1f));
                    }
                    break;
            }
            ApplyColorToModel();
        }

        private GameObject AddPrimitive(PrimitiveType type, Vector3 localPos, Vector3 localScale, Vector3 euler = default)
        {
            var go = GameObject.CreatePrimitive(type);
            // Physics colliders are pointless on a visual-only avatar and cost physics time.
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.SetParent(_modelRoot.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            go.transform.localEulerAngles = euler;
            return go;
        }

        /// <summary>Custom tetrahedron mesh (the "Prism" form) — no Unity primitive for it.</summary>
        private void BuildTetrahedron(Vector3 localPos, float size)
        {
            var go = new GameObject("Tetrahedron", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(_modelRoot.transform, false);
            go.transform.localPosition = localPos;

            float s = size * 0.5f;
            var verts = new[]
            {
                new Vector3(0, s, 0),
                new Vector3(-s, -s, s),
                new Vector3(s, -s, s),
                new Vector3(0, -s, -s),
            };
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    verts[0], verts[1], verts[2],
                    verts[0], verts[2], verts[3],
                    verts[0], verts[3], verts[1],
                    verts[1], verts[3], verts[2],
                },
                triangles = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },
            };
            mesh.RecalculateNormals();
            go.GetComponent<MeshFilter>().mesh = mesh;
            go.GetComponent<MeshRenderer>().material = MakeGlowMaterial(_color);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Color / motion / FX
        // ─────────────────────────────────────────────────────────────────────
        public void SetTrailColor(Color color)
        {
            _color = color;
            EnsureRig();
            if (_light != null) _light.color = color;
            if (_glowParticles != null)
            {
                var main = _glowParticles.main;
                main.startColor = color;
            }
            if (_unityTrail != null)
            {
                _unityTrail.startColor = color;
                var end = color; end.a = 0f;
                _unityTrail.endColor = end;
            }
            ApplyColorToModel();
        }

        private void ApplyColorToModel()
        {
            if (_modelRoot == null) return;
            foreach (var r in _modelRoot.GetComponentsInChildren<MeshRenderer>())
            {
                if (r.sharedMaterial == null || !r.sharedMaterial.name.StartsWith("BeaconGlow"))
                    r.material = MakeGlowMaterial(_color);
                else
                {
                    r.material.SetColor("_BaseColor", _color);
                    r.material.SetColor("_EmissionColor", _color * GameConfig.Active.beaconGlowIntensity);
                }
            }
        }

        private static Material MakeGlowMaterial(Color c)
        {
            Shader s = Shader.Find("LightRunners/BeaconGlow");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("Standard");
            var m = new Material(s) { name = "BeaconGlow_runtime" };
            m.SetColor("_BaseColor", c);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * GameConfig.Active.beaconGlowIntensity);
            }
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.7f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.8f);
            return m;
        }

        /// <summary>Apply position + heading with bob and spin (spec §9.2). Called by NetworkPlayer / local driver.</summary>
        public void UpdatePosition(Vector3 worldPos, float headingDegrees)
        {
            GameConfig cfg = GameConfig.Active;
            _bobPhase += Time.deltaTime * cfg.beaconBobFrequency;
            _spinDegrees += Time.deltaTime * cfg.beaconRotationSpeed;

            float bob = Mathf.Sin(_bobPhase * Mathf.PI * 2f) * cfg.beaconBobAmplitude;
            transform.position = worldPos + Vector3.up * bob;
            transform.rotation = Quaternion.Euler(0, headingDegrees, 0);
            if (_modelRoot != null)
                _modelRoot.transform.localRotation = Quaternion.Euler(0, _spinDegrees, 0);
        }

        /// <summary>Particle burst on crash (spec §9.2).</summary>
        public void PlayCrashEffect()
        {
            if (_glowParticles == null) return;
            var main = _glowParticles.main;
            main.startColor = _color;
            _glowParticles.Emit(48);
        }
    }
}
