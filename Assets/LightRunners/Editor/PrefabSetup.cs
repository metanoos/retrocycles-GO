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
    ///
    /// Lightfield match core (Track G, active decisions 2026-07-18): also generates
    /// <c>Resources/Gates/LumenGate.prefab</c> and <c>Resources/Gates/StolenLumenPickup.prefab</c>
    /// (Track B), and — when <c>FUSION_WEAVER</c> is defined —
    /// <c>Resources/Player/NetworkMatchState.prefab</c> (Track C). The gate prefabs carry the
    /// component recipe their <c>[RequireComponent]</c> attributes demand, but the visual rig is
    /// built in code at runtime (see <c>LumenGate.Awake</c> / <c>StolenLumenPickup.Initialize</c>),
    /// so the prefab itself is a thin GameObject. That keeps the scene runnable with zero art.
    /// </summary>
    public static class PrefabSetup
    {
        private const string BeaconDir = "Assets/Resources/Beacons";
        private const string NetworkPlayerDir = "Assets/Resources/Player";
        private const string GatesDir = "Assets/Resources/Gates";

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

        /// <summary>
        /// Legacy Fusion prefab generation. Fusion 2 is a paid SDK that is no longer
        /// used (Mirror replaced it). This method is kept as a no-op for menu compat.
        /// Use GenerateMirrorPlayerPrefab instead.
        /// </summary>
        [MenuItem("Light-Runners/Setup/NetworkPlayer Prefab (Legacy Fusion)")]
        public static void GenerateNetworkPlayerPrefab()
        {
            Debug.Log("[PrefabSetup] Fusion NetworkPlayer prefab skipped — use Mirror Player Prefab instead.");
        }

        /// <summary>
        /// Mirror prefab generation (free open-source networking — replaces Fusion).
        /// Generates <c>Resources/Player/MirrorPlayer.prefab</c> with the Mirror
        /// components: NetworkIdentity + MirrorNetworkPlayer + MirrorNetworkTrailSync.
        /// The MirrorLauncher (NetworkManager) references this as its playerPrefab.
        /// </summary>
        [MenuItem("Light-Runners/Setup/Mirror Player Prefab")]
        public static void GenerateMirrorPlayerPrefab()
        {
            if (!AssetDatabase.IsValidFolder(NetworkPlayerDir))
            {
                Directory.CreateDirectory(NetworkPlayerDir);
                AssetDatabase.Refresh();
            }
            const string path = NetworkPlayerDir + "/MirrorPlayer.prefab";
            if (File.Exists(path)) return;

            var go = new GameObject("MirrorPlayer");
            // Mirror: NetworkIdentity is the equivalent of Fusion's NetworkObject.
            AddComponentByName(go, "Mirror.NetworkIdentity");
            AddComponentByName(go, "LightRunners.Multiplayer.MirrorNetworkPlayer");
            AddComponentByName(go, "LightRunners.Multiplayer.MirrorNetworkTrailSync");
            PrefabUtility.SaveAsPrefabAsset(go, path);
            UnityEngine.Object.DestroyImmediate(go);
            Debug.Log("[PrefabSetup] MirrorPlayer prefab generated at " + path);
        }

        /// <summary>
        /// Track C (decision Q/T) — host-side authoritative match-state
        /// NetworkObject. The host spawns one per match at match start; it
        /// carries the networked <c>FrozenTailRadius</c> so every peer renders
        /// tails at the same width for the whole match (decision T). Not
        /// placed in the scene (Fusion NetworkObjects can only live on a
        /// NetworkRunner) — the host's MatchManager resolves this prefab and
        /// calls <c>Runner.Spawn</c>.
        ///
        /// Generated only under <c>FUSION_WEAVER</c>. Idempotent.
        /// </summary>
        /// <summary>
        /// Legacy Fusion NetworkMatchState prefab. Fusion 2 is a paid SDK that is no longer
        /// used (Mirror replaced it). This method is kept as a no-op for menu compat.
        /// </summary>
        [MenuItem("Light-Runners/Setup/NetworkMatchState Prefab (Legacy Fusion)")]
        public static void GenerateNetworkMatchStatePrefab()
        {
            Debug.Log("[PrefabSetup] Fusion NetworkMatchState prefab skipped — Mirror handles state via SyncVar.");
        }

        /// <summary>
        /// Track B (decisions G, L, M, R) — Lightfield gate prefabs. Generates
        /// <c>Resources/Gates/LumenGate.prefab</c> and
        /// <c>Resources/Gates/StolenLumenPickup.prefab</c>. Idempotent (skips
        /// existing files), mirrors the beacon-prefab path.
        ///
        /// The recipe per prefab is dictated by the behaviours'
        /// <c>[RequireComponent(typeof(SphereCollider))]</c> attribute (a trigger
        /// volume of radius <c>GameConfig.gateCollectionRadius</c>) plus the
        /// behaviour itself. The visual hemisphere / emissive orb is built in
        /// code at runtime (<c>LumenGate.Awake</c> / <c>StolenLumenPickup.EnsureRig</c>)
        /// so the prefab ships with zero art.
        /// </summary>
        [MenuItem("Light-Runners/Setup/Gate Prefabs")]
        public static void GenerateGatePrefabs()
        {
            if (!AssetDatabase.IsValidFolder(GatesDir))
            {
                Directory.CreateDirectory(GatesDir);
                AssetDatabase.Refresh();
            }

            // LumenGate: trigger sphere + LumenGate component.
            // StolenLumenPickup: trigger sphere + StolenLumenPickup component.
            // Both behaviours carry [RequireComponent(typeof(SphereCollider))] — adding the
            // SphereCollider ourselves (instead of relying on Unity's auto-add) makes the
            // prefab's intent explicit and survives a track-merge re-import.
            GenerateGatePrefab("LumenGate", "LightRunners.Lightfield.LumenGate", isTrigger: true);
            GenerateGatePrefab("StolenLumenPickup", "LightRunners.Lightfield.StolenLumenPickup", isTrigger: true);

            AssetDatabase.SaveAssets();
            Debug.Log("[PrefabSetup] Gate prefabs generated under " + GatesDir);
        }

        private static void GenerateGatePrefab(string name, string fullTypeName, bool isTrigger)
        {
            string path = $"{GatesDir}/{name}.prefab";
            if (File.Exists(path)) return;

            var go = new GameObject(name);
            // Trigger volume. The runtime behaviour re-reads GameConfig.gateCollectionRadius
            // and resets the radius in Awake/Initialize, so the prefab's radius is just a
            // sane default that survives a no-track compile.
            var sphere = go.AddComponent<SphereCollider>();
            sphere.isTrigger = isTrigger;
            sphere.radius = 2.0f;

            // Behaviour (reflection so the file compiles before Track B is merged).
            AddComponentByName(go, fullTypeName);

            PrefabUtility.SaveAsPrefabAsset(go, path);
            UnityEngine.Object.DestroyImmediate(go);
        }

        [MenuItem("Light-Runners/Setup/All Prefabs")]
        public static void GenerateAll()
        {
            GenerateBeaconPrefabs();
            GenerateNetworkPlayerPrefab();
            GenerateMirrorPlayerPrefab();
            // Lightfield match core (Track G): gates + NetworkMatchState.
            GenerateGatePrefabs();
            GenerateNetworkMatchStatePrefab();
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
                // Track not merged — log once so a Phase-0 worktree can still run All Prefabs.
                Debug.Log($"[PrefabSetup] {fullTypeName} not found in loaded assemblies (track not merged yet); prefab will be empty.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PrefabSetup] Could not add {fullTypeName}: {e.Message}");
            }
        }
    }
}
#endif
