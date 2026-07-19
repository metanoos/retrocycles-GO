#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
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
    }
}
#endif
