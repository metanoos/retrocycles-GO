#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using LightRunners.Core;

namespace LightRunners.Editor
{
    /// <summary>
    /// Best-effort first-run bootstrapper. Ensures two assets exist so the project opens in a
    /// working state without shipping fragile hand-written pipeline YAML (spec §1.2, §4.4):
    ///   • <c>Assets/Settings/URP/URPAsset.asset</c> (+ a UniversalRendererData)
    ///   • <c>Assets/Resources/GameConfig.asset</c>
    ///
    /// Honest status: URP asset creation is done by reflecting on the real URP types, because
    /// the format of those YAML files changes between Unity point releases. Reflection is the
    /// most robust option, but it is also the part most likely to break across versions. So:
    ///   • We never throw — every failure is caught and logged with a "do this manually" hint.
    ///   • We never override an existing pipeline assignment — if <c>GraphicsSettings.defaultRenderPipeline</c>
    ///     is already set (by the user or a prior run), we leave it alone.
    ///   • The project still opens and the editor menu still works even if this whole class fails.
    ///
    /// If auto-setup fails: Project Settings → Graphics → assign a URP asset, or
    /// right-click → Create → Rendering → URP Asset (with Renderer).
    /// </summary>
    [InitializeOnLoad]
    public static class LightRunnersProjectBootstrap
    {
        private const string URPDir = "Assets/Settings/URP";
        private const string URPPath = URPDir + "/URPAsset.asset";
        private const string URPRendererPath = URPDir + "/URPRenderer.asset";
        private const string GameConfigPath = "Assets/Resources/GameConfig.asset";

        // Run once per editor session, not on every script reload.
        private const string SessionKey = "LightRunners.Bootstrap Ran";

        static LightRunnersProjectBootstrap()
        {
            // delayCall defers past asset import; otherwise AssetDatabase calls can throw
            // "asset is being imported" on first project open.
            EditorApplication.delayCall += RunOnce;
        }

        [MenuItem("Light-Runners/Setup/Ensure Project Assets (URP + GameConfig)")]
        public static void Run()
        {
            // Manual menu invocation always runs, ignoring the session guard.
            RunInternal(force: true);
        }

        private static void RunOnce()
        {
            RunInternal(force: false);
        }

        private static void RunInternal(bool force)
        {
            if (!force && SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);

            EnsureURPAsset();
            EnsureGameConfigAsset();
            GameConfig.ClearCache();
        }

        // ─────────────────────────────────────────────────────────────────────
        // URP
        // ─────────────────────────────────────────────────────────────────────
        private static void EnsureURPAsset()
        {
            try
            {
                // If an asset already exists at our path, just make sure it's assigned.
                var existing = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(URPPath);
                if (existing != null)
                {
                    AssignIfUnset(existing);
                    return;
                }

                Type assetType = FindType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset");
                Type rendererType = FindType("UnityEngine.Rendering.Universal.UniversalRendererData");
                if (assetType == null || rendererType == null)
                {
                    Debug.LogWarning("[Bootstrap] URP types not found. Install com.unity.render-pipelines.universal, then run 'Light-Runners > Setup > Ensure Project Assets', or create a URP asset manually (Create > Rendering > URP Asset).");
                    return;
                }

                System.IO.Directory.CreateDirectory(URPDir);

                // Renderer data.
                var rendererData = ScriptableObject.CreateInstance(rendererType);
                AssetDatabase.CreateAsset(rendererData, URPRendererPath);

                // Pipeline asset.
                var pipelineAsset = ScriptableObject.CreateInstance(assetType);

                // Wire the renderer list + default index. Field names are stable across URP 17.x:
                //   m_RendererDataList  (UniversalRendererData[])
                //   m_DefaultRendererIndex (int)
                SetField(assetType, pipelineAsset, "m_RendererDataList", Array.CreateInstance(rendererType, 1));
                SetElementAt(assetType, pipelineAsset, "m_RendererDataList", 0, rendererData);
                SetField(assetType, pipelineAsset, "m_DefaultRendererIndex", 0);

                AssetDatabase.CreateAsset(pipelineAsset, URPPath);
                AssetDatabase.SaveAssets();

                EditorUtility.SetDirty(pipelineAsset);
                AssignIfUnset((RenderPipelineAsset)pipelineAsset);

                Debug.Log("[Bootstrap] Created URP pipeline asset at " + URPPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bootstrap] URP auto-setup failed. Create a URP asset manually (Create > Rendering > URP Asset) and assign it in Project Settings > Graphics. Cause: {e.Message}");
            }
        }

        /// <summary>Assign the pipeline only if GraphicsSettings has none — never override a deliberate user choice.</summary>
        private static void AssignIfUnset(RenderPipelineAsset asset)
        {
            if (asset == null) return;
            if (GraphicsSettings.defaultRenderPipeline != null) return;
            GraphicsSettings.defaultRenderPipeline = asset;
        }

        // ─────────────────────────────────────────────────────────────────────
        // GameConfig
        // ─────────────────────────────────────────────────────────────────────
        private static void EnsureGameConfigAsset()
        {
            try
            {
                if (AssetDatabase.LoadAssetAtPath<GameConfig>(GameConfigPath) != null) return;
                System.IO.Directory.CreateDirectory("Assets/Resources");
                var cfg = ScriptableObject.CreateInstance<GameConfig>();
                AssetDatabase.CreateAsset(cfg, GameConfigPath);
                AssetDatabase.SaveAssets();
                GameConfig.ClearCache();
                Debug.Log("[Bootstrap] Created default GameConfig at " + GameConfigPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bootstrap] GameConfig auto-create failed: {e.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Reflection helpers
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Search already-loaded assemblies only — never force a load.</summary>
        private static Type FindType(string fullTypeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullTypeName); if (t != null) return t; }
                catch (Exception) { /* skip unsupported assemblies */ }
            }
            return null;
        }

        private static void SetField(Type type, object instance, string fieldName, object value)
        {
            var f = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) return;
            // Coerce: if the field is UniversalRendererData[] but we passed a raw Object[], redo as typed array.
            if (value is Array arr && f.FieldType.IsArray && f.FieldType.GetElementType() != arr.GetType().GetElementType())
            {
                var et = f.FieldType.GetElementType();
                var typed = Array.CreateInstance(et, arr.Length);
                for (int i = 0; i < arr.Length; i++) typed.SetValue(arr.GetValue(i), i);
                value = typed;
            }
            f.SetValue(instance, value);
        }

        private static void SetElementAt(Type type, object instance, string fieldName, int index, object element)
        {
            var f = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) return;
            if (!(f.GetValue(instance) is Array arr) || index >= arr.Length) return;
            var et = f.FieldType.GetElementType();
            if (et != null && et.IsInstanceOfType(element)) arr.SetValue(element, index);
        }
    }
}
#endif
