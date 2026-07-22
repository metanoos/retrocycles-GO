#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using LightRunners.Core;

namespace LightRunners.Editor
{
    /// <summary>
    /// <c>Tools → Validate Setup</c> EditorWindow (spec §14.3). Checks: GameConfig asset,
    /// auth service present, both scenes in Build Settings, beacon prefabs exist, Supabase
    /// URL non-empty (warns if blank — expected for phase 4). Prints a pass/fail report.
    /// </summary>
    public class SetupValidator : EditorWindow
    {
        [MenuItem("Light-Runners/Validate Setup")]
        public static void Open() => GetWindow<SetupValidator>("Light Runners Validator");

        private Vector2 _scroll;
        private string _report = "";
        private bool _lastRunAllPassed;

        private void OnEnable() => Run();

        private void OnGUI()
        {
            GUILayout.Label("Light Runners — Setup Validation", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Re-run", GUILayout.Width(120))) Run();
                GUILayout.FlexibleSpace();
                if (_lastRunAllPassed) GUILayout.Label("✅ All checks passed", EditorStyles.boldLabel);
                else GUILayout.Label("⚠ See report", EditorStyles.boldLabel);
            }
            EditorGUILayout.Space();

            using (var s = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                GUILayout.Label(_report);
                _scroll = s.scrollPosition;
            }
        }

        private void Run()
        {
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;

            // GameConfig asset
            var cfg = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Resources/GameConfig.asset");
            sb.AppendLine($"[{(cfg ? "PASS" : "FAIL")}] GameConfig.asset present (Resources/GameConfig.asset)");
            if (cfg) pass++; else fail++;

            // Auth service (interface registered by PlatformServiceRegistry at runtime; in editor
            // we check the type compiles — i.e. the Identity assembly exists).
            bool identityCompiles = TypeExists("LightRunners.Identity.EditorAnonymousAuthService");
            sb.AppendLine($"[{(identityCompiles ? "PASS" : "FAIL")}] Identity assembly (IAuthService + stub) compiles");
            if (identityCompiles) pass++; else fail++;

            // Both scenes in Build Settings (Login index 0)
            bool loginInBuild = false, gameInBuild = false;
            int loginIndex = -1;
            for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
            {
                var s = EditorBuildSettings.scenes[i];
                if (s.path.EndsWith("Login.unity")) { loginInBuild = true; loginIndex = i; }
                if (s.path.EndsWith("Game.unity")) gameInBuild = true;
            }
            sb.AppendLine($"[{(loginInBuild ? "PASS" : "FAIL")}] Login scene in Build Settings");
            if (loginInBuild) pass++; else fail++;
            sb.AppendLine($"[{(loginIndex == 0 ? "PASS" : "WARN")}] Login scene at index 0 (currently index {loginIndex})");
            if (loginIndex == 0) pass++;
            sb.AppendLine($"[{(gameInBuild ? "PASS" : "FAIL")}] Game scene in Build Settings");
            if (gameInBuild) pass++; else fail++;

            // Beacon prefabs (any of the 8). Warn-level: game runs with fallback meshes.
            int beaconCount = 0;
            if (AssetDatabase.IsValidFolder("Assets/Resources/Beacons"))
            {
                foreach (var d in BeaconFormData.Defaults)
                    if (File.Exists($"Assets/Resources/Beacons/{d.prefabName}.prefab")) beaconCount++;
            }
            sb.AppendLine($"[{(beaconCount == 8 ? "PASS" : "WARN")}] Beacon prefabs ({beaconCount}/8; missing ones use fallback primitives)");

            // Supabase URL — warn if blank (expected for phase 4).
            bool sbConfigured = cfg != null && !string.IsNullOrEmpty(cfg.supabaseUrl);
            sb.AppendLine($"[{(sbConfigured ? "PASS" : "WARN")}] Supabase URL configured{(sbConfigured ? "" : " (blank — expected for phase 4; runs without backend)")}");

            // URP pipeline asset assigned
            bool urp = GraphicsSettings.defaultRenderPipeline != null;
            sb.AppendLine($"[{(urp ? "PASS" : "WARN")}] URP pipeline asset assigned");

            // AR Foundation presence
            bool ar = TypeExists("UnityEngine.XR.ARFoundation.ARSession");
            sb.AppendLine($"[{(ar ? "PASS" : "WARN")}] AR Foundation present (needed only for phase 9 AR)");

            // Fusion presence
            bool fusion = TypeExists("Fusion.NetworkRunner");
            sb.AppendLine($"[{(fusion ? "PASS" : "WARN")}] Photon Fusion present (needed only for phase 8 multiplayer)");

            // Custom shaders (phase 11)
            int shaders = 0;
            foreach (var s in new[] { "LightRunners/NeonTrailEnhanced", "LightRunners/BeaconGlow", "LightRunners/ScreenCrashFlash" })
                if (Shader.Find(s) != null) shaders++;
            sb.AppendLine($"[{(shaders == 3 ? "PASS" : "WARN")}] Custom shaders present ({shaders}/3; fallback materials cover gaps)");

            // ───────────────────────────────────────────────────────────────
            // Lightfield match core (Track G — active decisions 2026-07-18).
            // All WARN-level: the editor still runs on a Phase-0 worktree; a
            // missing track or unconfigured field is a designer hint, not a
            // blocker. See SPEC §1.5 for the decision map.
            // ───────────────────────────────────────────────────────────────
            ValidateLightfieldFields(sb, cfg, ref pass);
            ValidateLightfieldPrefabs(sb, ref pass);
            ValidateLightfieldAssemblies(sb, ref pass);
            ValidateMatchManagerInGameScene(sb, ref pass);

            sb.AppendLine();
            sb.AppendLine($"Total: {pass} pass / {fail} fail (warnings do not block editor playmode).");
            _lastRunAllPassed = fail == 0;
            _report = sb.ToString();
            Debug.Log("[SetupValidator]\n" + _report);
        }

        private static bool TypeExists(string fullTypeName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetType(fullTypeName) != null) return true;
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lightfield match-core validators (Track G — active decisions 2026-07-18)
        //
        // All WARN-level: the editor still runs on a Phase-0 worktree or with
        // one or more tracks unmerged. A missing field/prefab/assembly is a
        // designer hint, not a playmode blocker. See SPEC §1.5 for the
        // decision map.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Decision M / G / T / O / K — checks the Lightfield tunables on
        /// <see cref="GameConfig"/> are non-default. WARN (not FAIL): the
        /// defaults are playable, but a zeroed field indicates the bootstrap
        /// hasn't been refreshed or the asset is stale.
        /// </summary>
        private static void ValidateLightfieldFields(System.Text.StringBuilder sb, GameConfig cfg, ref int pass)
        {
            if (cfg == null) return; // already reported by the GameConfig row above.

            // gatesPerPlayer > 0 (decision M — density formula floor is 1, but 0 means
            // "no gates ever", almost certainly a stale config).
            bool gatesOk = cfg.gatesPerPlayer > 0f;
            sb.AppendLine($"[{(gatesOk ? "PASS" : "WARN")}] GameConfig.gatesPerPlayer > 0 (currently {cfg.gatesPerPlayer}; decision M)");
            if (gatesOk) pass++;

            // Legal tail values are a discrete host control; player radius remains fixed in code.
            int tailCm = Mathf.RoundToInt(cfg.tailRadius * 100f);
            bool tailOk = Mathf.Abs(cfg.tailRadius * 100f - tailCm) < 0.01f
                          && FrozenMatchConfig.IsLegalTailRadiusCm(tailCm);
            sb.AppendLine($"[{(tailOk ? "PASS" : "WARN")}] GameConfig.tailRadius is 1.5–4.0 m in 0.5 m steps (currently {cfg.tailRadius}; player radius fixed at 2 m)");
            if (tailOk) pass++;

            // Ground-alpha host clock: 3–10 whole minutes.
            bool durOk = cfg.matchDurationSeconds >= 180f
                         && cfg.matchDurationSeconds <= 600f
                         && Mathf.Approximately(cfg.matchDurationSeconds % 60f, 0f);
            sb.AppendLine($"[{(durOk ? "PASS" : "WARN")}] GameConfig.matchDurationSeconds is 3–10 whole minutes (currently {cfg.matchDurationSeconds}s; decision O)");
            if (durOk) pass++;

            // Ground-alpha Gate radius plus its cross-setting packing constraint.
            bool radiusOk = cfg.gateCollectionRadius >= 3f
                            && cfg.gateCollectionRadius <= 20f
                            && cfg.gateCollectionRadius <= 0.2f * cfg.lightfieldBaseRadiusMeters;
            sb.AppendLine($"[{(radiusOk ? "PASS" : "WARN")}] GameConfig.gateCollectionRadius is 3–20 m and <= 20% of Lightfield radius (currently {cfg.gateCollectionRadius}m; decision G)");
            if (radiusOk) pass++;
        }

        /// <summary>
        /// Track B prefab presence under <c>Resources/Gates/</c>. WARN — the
        /// gate behaviours self-instantiate their visuals at runtime, so the
        /// game runs without the prefabs, but Track D's MatchManager will need
        /// them once the spawn pipeline is wired.
        /// </summary>
        private static void ValidateLightfieldPrefabs(System.Text.StringBuilder sb, ref int pass)
        {
            const string gatesDir = "Assets/Resources/Gates";
            bool lumenGate = File.Exists($"{gatesDir}/LumenGate.prefab");
            sb.AppendLine($"[{(lumenGate ? "PASS" : "WARN")}] Resources/Gates/LumenGate.prefab present{(lumenGate ? "" : " (run Light-Runners → Setup → Gate Prefabs)")}");
            if (lumenGate) pass++;

            bool stolenPickup = File.Exists($"{gatesDir}/StolenLumenPickup.prefab");
            sb.AppendLine($"[{(stolenPickup ? "PASS" : "WARN")}] Resources/Gates/StolenLumenPickup.prefab present{(stolenPickup ? "" : " (run Light-Runners → Setup → Gate Prefabs)")}");
            if (stolenPickup) pass++;
        }

        /// <summary>
        /// Lightfield + Afterglow assemblies present (Tracks B + F merged). WARN
        /// — the editor still opens and the scene generator still compiles
        /// (reflection-driven); a missing assembly just means the relevant
        /// GameObjects skip silently.
        /// </summary>
        private static void ValidateLightfieldAssemblies(System.Text.StringBuilder sb, ref int pass)
        {
            bool lightfield = TypeExists("LightRunners.Lightfield.LumenGate");
            sb.AppendLine($"[{(lightfield ? "PASS" : "WARN")}] LightRunners.Lightfield assembly present (Track B){(lightfield ? "" : " — Lightfield GameObjects skipped on scene gen")}");
            if (lightfield) pass++;

            bool afterglow = TypeExists("LightRunners.Afterglow.AfterglowViewController");
            sb.AppendLine($"[{(afterglow ? "PASS" : "WARN")}] LightRunners.Afterglow assembly present (Track F){(afterglow ? "" : " — Afterglow stack skipped on scene gen")}");
            if (afterglow) pass++;

            bool gameplay = TypeExists("LightRunners.Gameplay.MatchManager");
            sb.AppendLine($"[{(gameplay ? "PASS" : "WARN")}] LightRunners.Gameplay.MatchManager present (Track D){(gameplay ? "" : " — MatchManager GameObject skipped on scene gen")}");
            if (gameplay) pass++;

            bool trail = TypeExists("LightRunners.Trail.LumenScoreboard");
            sb.AppendLine($"[{(trail ? "PASS" : "WARN")}] LightRunners.Trail.LumenScoreboard present (Track A){(trail ? "" : " — Lumen tally falls back to NullLumenScoreboard")}");
            if (trail) pass++;
        }

        /// <summary>
        /// MatchManager present in the Game scene. WARN — generated scenes
        /// always include it once Track D is merged; a hand-edited scene might
        /// not. Matches the existing pattern (open the scene, probe by type).
        /// </summary>
        private static void ValidateMatchManagerInGameScene(System.Text.StringBuilder sb, ref int pass)
        {
            bool present = false;
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        // Deep-find by full type name (reflection-free) — works whether
                        // or not Track D's assembly is referenced at compile time.
                        var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
                        foreach (var b in behaviours)
                        {
                            if (b == null) continue;
                            if (b.GetType().FullName == "LightRunners.Gameplay.MatchManager")
                            {
                                present = true;
                                break;
                            }
                        }
                        if (present) break;
                    }
                    if (present) break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SetupValidator] MatchManager scene probe failed: {e.Message}");
            }

            string hint = present ? "" : " (open Game.unity + Light-Runners → Setup → Generate All Scenes)";
            sb.AppendLine($"[{(present ? "PASS" : "WARN")}] MatchManager present in Game scene{hint}");
            // WARN-only: only the open scene is probe-able, and most editor runs
            // don't have Game.unity loaded. We surface the row for awareness.
        }
    }
}
#endif
