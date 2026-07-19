#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Editor
{
    /// <summary>
    /// Generates the prefabs referenced by the runtime (spec §14.3). In phase 4 the only
    /// runtime-referenced prefab paths are <c>Resources/Beacons/*</c> (which the BeaconController
    /// will fall back to a primitive for, so we generate empty ones) and <c>Resources/Player/NetworkPlayer</c>
    /// (Fusion, generated only when <c>FUSION_WEAVER</c> is defined).
    /// </summary>
    public static class PrefabSetup
    {
        private const string BeaconDir = "Assets/Resources/Beacons";
        private const string NetworkPlayerDir = "Assets/Resources/Player";

        [MenuItem("Light-Runners/Setup/Beacon Prefabs")]
        public static void GenerateBeaconPrefabs()
        {
            if (!AssetDatabase.IsValidFolder(BeaconDir))
            {
                Directory.CreateDirectory(BeaconDir);
                AssetDatabase.Refresh();
            }

            foreach (var data in BeaconFormData.Defaults)
            {
                string path = $"{BeaconDir}/{data.prefabName}.prefab";
                if (File.Exists(path)) continue;

                // Empty placeholder GO. BeaconController.SetForm (phase 5) will build a primitive
                // fallback mesh when the prefab has no model, so the game runs with zero art.
                var go = new GameObject(data.prefabName);
                // Tint an empty child so designers can drop a model in later.
                var model = new GameObject("Model");
                model.transform.SetParent(go.transform, false);
                PrefabUtility.SaveAsPrefabAsset(go, path);
                UnityEngine.Object.DestroyImmediate(go);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[PrefabSetup] Beacon prefabs generated under " + BeaconDir);
        }

        [MenuItem("Light-Runners/Setup/NetworkPlayer Prefab")]
        public static void GenerateNetworkPlayerPrefab()
        {
#if FUSION_WEAVER
            if (!AssetDatabase.IsValidFolder(NetworkPlayerDir))
            {
                Directory.CreateDirectory(NetworkPlayerDir);
                AssetDatabase.Refresh();
            }
            const string path = NetworkPlayerDir + "/NetworkPlayer.prefab";
            if (File.Exists(path)) return;

            var go = new GameObject("NetworkPlayer");
            // Spec §14.3: minimal prefab = NetworkObject + NetworkPlayer + NetworkTrailSync.
            // Do NOT add beacon/collision components here — Spawned builds those at runtime.
            // Added by reflection so this file compiles before Fusion is imported.
            AddComponentByName(go, "Fusion.NetworkObject");
            AddComponentByName(go, "LightRunners.Multiplayer.NetworkPlayer");
            AddComponentByName(go, "LightRunners.Multiplayer.NetworkTrailSync");
            PrefabUtility.SaveAsPrefabAsset(go, path);
            UnityEngine.Object.DestroyImmediate(go);
            Debug.Log("[PrefabSetup] NetworkPlayer prefab generated at " + path);
#else
            Debug.Log("[PrefabSetup] Skipped NetworkPlayer prefab — FUSION_WEAVER not defined. (Phase 8.)");
#endif
        }

        [MenuItem("Light-Runners/Setup/All Prefabs")]
        public static void GenerateAll()
        {
            GenerateBeaconPrefabs();
            GenerateNetworkPlayerPrefab();
        }

        private static void AddComponentByName(GameObject go, string fullTypeName)
        {
            try
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType(fullTypeName);
                    if (t != null && t.IsSubclassOf(typeof(MonoBehaviour)))
                    {
                        go.AddComponent(t);
                        return;
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PrefabSetup] Could not add {fullTypeName}: {e.Message}");
            }
        }
    }
}
#endif
