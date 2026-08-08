#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using LightRunners.Core;
using LightRunners.Gameplay;

namespace LightRunners.Editor
{
    /// <summary>
    /// Generates the Login and Game scenes from code (spec §14). Scenes must be regenerated,
    /// never hand-YAML-edited — this keeps scene state reviewable and reproducible.
    ///
    /// Phase-conditional objects (FusionLauncher, SupabaseManager, AR stack) are added by
    /// reflection so the generator compiles regardless of whether those phases are present.
    /// </summary>
    public static class SceneSetup
    {
        private const string ScenesDir = "Assets/LightRunners/Scenes";
        private const string LoginPath = ScenesDir + "/Login.unity";
        private const string GamePath = ScenesDir + "/Game.unity";

        [MenuItem("Light-Runners/Setup/Generate All Scenes")]
        public static void GenerateAll()
        {
            EnsureScenesDir();
            GenerateLoginScene();
            GenerateGameScene();
            EnsureBuildSettings();
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneSetup] All scenes generated. Login at index 0, Game at index 1.");
        }

        [MenuItem("Light-Runners/Setup/Login Scene")]
        public static void GenerateLoginScene()
        {
            EnsureScenesDir();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // MainCamera
            var cam = new GameObject("MainCamera", typeof(Camera));
            cam.tag = "MainCamera";
            var camComp = cam.GetComponent<Camera>();
            camComp.clearFlags = CameraClearFlags.SolidColor;
            camComp.backgroundColor = new Color(0.04f, 0.04f, 0.07f);
            cam.transform.position = new Vector3(0, 0, -10);

            // Canvas (ScreenSpaceOverlay, 1080×1920 match-height)
            var canvas = MakeCanvas("Canvas", 1080, 1920);

            // LoginPanel (full-stretch)
            var panelGo = new GameObject("LoginPanel", typeof(RectTransform), typeof(CanvasRenderer));
            var panelRT = panelGo.GetComponent<RectTransform>();
            panelRT.SetParent(canvas.transform, false);
            Stretch(panelRT);
            AddImage(panelGo, new Color(0.05f, 0.05f, 0.09f, 0.95f));

            // Title
            var title = MakeLabel(panelRT, "TitleText", "Light-Runners", 64, new Vector2(0, 200));
            // Info
            var info = MakeLabel(panelRT, "InfoText", "Anonymous sign-in — tap Play to start", 28, new Vector2(0, 110));
            // Play button
            var playBtn = MakeButton(panelRT, "PlayButton", "Play", 48, new Vector2(0, -20));
            // Status text
            var status = MakeLabel(panelRT, "StatusText", "", 26, new Vector2(0, -120));
            SetColor(status, Color.yellow);

            // LoginUI wiring
            var loginUI = panelGo.AddComponent<LoginUI>();
            var loginUISO = new SerializedObject(loginUI);
            SetSO(loginUISO, "loginPanel", panelGo);
            SetSO(loginUISO, "loginButton", playBtn.GetComponent<Button>());
            SetSO(loginUISO, "statusText", EnsureAdaptor(status));
            SetSO(loginUISO, "infoText", EnsureAdaptor(info));
            loginUISO.ApplyModifiedPropertiesWithoutUndo();

            // EventSystem
            EnsureEventSystem();

            EditorSceneManager.SaveScene(scene, LoginPath);
            Debug.Log("[SceneSetup] Login scene written to " + LoginPath);
        }

        [MenuItem("Light-Runners/Setup/Game Scene")]
        public static void GenerateGameScene()
        {
            EnsureScenesDir();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // PlatformServiceRegistry (Awake FIRST — DefaultExecutionOrder handles it)
            new GameObject("PlatformServiceRegistry", typeof(PlatformServiceRegistry));

            // GameManager GO with fallback detector + trail repository slot
            var gmGo = new GameObject("GameManager", typeof(GameManager));
            // GameManager already adds a TrailCollisionDetector in Awake if null; we don't need to add one here.

            // LocationProvider
            new GameObject("LocationProvider", typeof(Location.LocationProvider));
            // TrailManager
            new GameObject("TrailManager", typeof(Trail.TrailManager));
            // BeaconFormManager (phase 5 — add by reflection if present)
            TryAddType("LightRunners.Beacon.BeaconFormManager", "BeaconFormManager");
            // MirrorLauncher (free multiplayer — replaces Fusion). Mirror's NetworkManager
            // is a MonoBehaviour so TryAddType creates it. The playerPrefab is wired below
            // after the MirrorPlayer prefab is generated.
            TryAddMirrorLauncher();
            // FusionLauncher (FUSION_WEAVER — phase 8, legacy dead code)
            TryAddType("LightRunners.Multiplayer.FusionLauncher", "FusionLauncher");
            // SupabaseManager (phase 7)
            TryAddType("LightRunners.Backend.SupabaseManager", "SupabaseManager");
            // PerformanceMonitor
            new GameObject("PerformanceMonitor", typeof(PerformanceMonitor));
            // GPSPowerManager
            new GameObject("GPSPowerManager", typeof(Location.GPSPowerManager));

            // Map/ minimap (spec §10.2): corner RawImages (tiles + overlay) + expand button.
            // ScreenSpaceCamera so the 3D MainCamera (top half) renders on top of the map
            // (bottom half) — Pokemon Go style: neon trails + lumen gates in 3D above, map below.
            var mapCanvas = MakeCanvas("Map", 1080, 1920, sortingOrder: -1,
                renderMode: RenderMode.ScreenSpaceCamera);
            var mapGroup = mapCanvas.gameObject.AddComponent<CanvasGroup>();
            mapGroup.alpha = 1f;

            var minimapGo = new GameObject("Minimap", typeof(RectTransform));
            var minimapRT = minimapGo.GetComponent<RectTransform>();
            minimapRT.SetParent(mapCanvas.transform, false);
            // Bottom half of the screen, full width. Player feedback: the map is the primary
            // gameplay surface during a run, not a corner peek.
            minimapRT.anchorMin = new Vector2(0f, 0f);
            minimapRT.anchorMax = new Vector2(1f, 0.5f);
            minimapRT.pivot = new Vector2(0.5f, 0.5f);
            minimapRT.offsetMin = new Vector2(0f, 0f);
            minimapRT.offsetMax = new Vector2(0f, 0f);
            minimapRT.sizeDelta = new Vector2(0f, 0f);

            var mapBase = new GameObject("Base", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var mapBaseRT = mapBase.GetComponent<RectTransform>();
            mapBaseRT.SetParent(minimapRT, false);
            Stretch(mapBaseRT);

            var mapOverlay = new GameObject("Overlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var mapOverlayRT = mapOverlay.GetComponent<RectTransform>();
            mapOverlayRT.SetParent(minimapRT, false);
            Stretch(mapOverlayRT);

            var expandGo = new GameObject("ExpandButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            var expandRT = expandGo.GetComponent<RectTransform>();
            expandRT.SetParent(minimapRT, false);
            Stretch(expandRT);
            expandGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.01f); // invisible hit target

            var minimapView = minimapGo.AddComponent<Map.OSMMinimapView>();
            var minimapSO = new SerializedObject(minimapView);
            SetSO(minimapSO, "baseImage", mapBase.GetComponent<RawImage>());
            SetSO(minimapSO, "overlayImage", mapOverlay.GetComponent<RawImage>());
            SetSO(minimapSO, "expandButton", expandGo.GetComponent<Button>());
            minimapSO.ApplyModifiedPropertiesWithoutUndo();

            // World-space trail renderers + the solo-mode local beacon (spec §7.5 / §8.4).
            new GameObject("TrailRenderingManager", typeof(TrailRenderingManager));
            new GameObject("LocalBeaconDriver", typeof(LocalBeaconDriver));

            // AR/ stack (UNITY_XR_ARFOUNDATION — phase 10) — added by reflection only
            Camera arCameraComp = TryAddARStack(gmGo);

            // MainCamera (top-down, depth 0)
            var mainCam = new GameObject("MainCamera", typeof(Camera));
            mainCam.tag = "MainCamera";
            var mainCamComp = mainCam.GetComponent<Camera>();
            mainCamComp.depth = 0;
            mainCam.transform.position = new Vector3(0, 50, 0);
            mainCam.transform.rotation = Quaternion.Euler(90, 0, 0);

            // Wire the map canvas to render behind the 3D camera. ScreenSpaceCamera
            // mode requires a worldCamera; the canvas renders at planeDistance behind
            // everything the camera draws, so 3D trails/gates appear on top.
            mapCanvas.worldCamera = mainCamComp;
            mapCanvas.planeDistance = 100f;

            // AR UI canvas group (alpha 0; cross-faded against the map by ViewTransitionManager).
            var arCanvas = MakeCanvas("ARCanvas", 1080, 1920, sortingOrder: -2);
            var arGroup = arCanvas.gameObject.AddComponent<CanvasGroup>();
            arGroup.alpha = 0f;

            // ViewTransitionManager (wires main + ar cameras + canvas groups)
            var vtm = mainCam.AddComponent<ViewTransitionManager>();
            var vtmSO = new SerializedObject(vtm);
            SetSO(vtmSO, "mainCamera", mainCam.GetComponent<Camera>());
            SetSO(vtmSO, "mapCanvasGroup", mapGroup);
            SetSO(vtmSO, "arCanvasGroup", arGroup);
            if (arCameraComp != null) SetSO(vtmSO, "arCamera", arCameraComp);
            vtmSO.ApplyModifiedPropertiesWithoutUndo();

            // TrailLODManager
            new GameObject("TrailLODManager", typeof(Trail.TrailLODManager));

            // CrashSequence (with a flash overlay image on the HUD canvas — created below)
            var crashGo = new GameObject("CrashSequence", typeof(CrashSequence));

            // HUD Canvas (sortingOrder 10)
            var hudCanvas = MakeCanvas("HUDCanvas", 1080, 1920, sortingOrder: 10);
            var hudPanel = new GameObject("HUDPanel", typeof(RectTransform), typeof(CanvasRenderer));
            var hudPanelRT = hudPanel.GetComponent<RectTransform>();
            hudPanelRT.SetParent(hudCanvas.transform, false);
            Stretch(hudPanelRT);
            AddImage(hudPanel, new Color(0, 0, 0, 0.0f)); // transparent container

            // HUD text fields
            var speed = MakeLabel(hudPanelRT, "SpeedText", "0.00 m/s", 28, new Vector2(-400, 850));
            var altitude = MakeLabel(hudPanelRT, "AltitudeText", "0 m", 28, new Vector2(-400, 800));
            var time = MakeLabel(hudPanelRT, "TimeText", "00:00", 28, new Vector2(-400, 750));
            var distance = MakeLabel(hudPanelRT, "DistanceText", "0 m", 28, new Vector2(-400, 700));
            var players = MakeLabel(hudPanelRT, "PlayersText", "1", 28, new Vector2(-400, 650));

            // Buttons
            var viewToggle = MakeButton(hudPanelRT, "ViewToggle", "AR Mode", 32, new Vector2(400, 850));
            var beaconBtn = MakeButton(hudPanelRT, "BeaconFormButton", "Hoverboard", 32, new Vector2(400, 790));
            var endRun = MakeButton(hudPanelRT, "EndRunButton", "End Run", 40, new Vector2(0, -800));

            // StartRunButton (sibling of HUDPanel, visible at Lobby). Lifted above center so it
            // doesn't sit on the boundary with the bottom-half map and stay unnoticed.
            var startRun = MakeButton(hudCanvas.transform, "StartRunButton", "Start Run", 56, new Vector2(0, 250));

            // SummaryPanel (full-stretch, hidden by default)
            var summary = new GameObject("SummaryPanel", typeof(RectTransform), typeof(CanvasRenderer));
            var summaryRT = summary.GetComponent<RectTransform>();
            summaryRT.SetParent(hudCanvas.transform, false);
            Stretch(summaryRT);
            AddImage(summary, new Color(0.02f, 0.02f, 0.05f, 0.92f));
            summary.SetActive(false);

            var crashText = MakeLabel(summaryRT, "SummaryCrashText", "You crashed!", 40, new Vector2(0, 600));
            var totalScore = MakeLabel(summaryRT, "TotalScoreText", "0", 96, new Vector2(0, 480));
            MakeLabel(summaryRT, "TotalScoreLabel", "TOTAL SCORE", 28, new Vector2(0, 400));
            MakeLabel(summaryRT, "DistanceText", "0 m", 32, new Vector2(-350, 280));
            MakeLabel(summaryRT, "TimeText", "00:00", 32, new Vector2(0, 280));
            MakeLabel(summaryRT, "AvgSpeedText", "0.00 m/s", 32, new Vector2(350, 280));
            MakeLabel(summaryRT, "DistanceScoreText", "0/40", 28, new Vector2(-350, 180));
            MakeLabel(summaryRT, "SpeedScoreText", "0/20", 28, new Vector2(-115, 180));
            MakeLabel(summaryRT, "BeautyScoreText", "0/30", 28, new Vector2(115, 180));
            MakeLabel(summaryRT, "ProximityScoreText", "0/10", 28, new Vector2(350, 180));
            var runAgain = MakeButton(summaryRT, "RunAgainButton", "Run Again", 44, new Vector2(-220, -250));
            var continueBtn = MakeButton(summaryRT, "ContinueButton", "Continue to Lobby", 44, new Vector2(220, -250));

            // RunSummaryUI wiring
            var summaryUI = summary.AddComponent<RunSummaryUI>();
            var summarySO = new SerializedObject(summaryUI);
            SetSO(summarySO, "summaryPanel", summary);
            SetSO(summarySO, "summaryCrashText", EnsureAdaptor(crashText));
            SetSO(summarySO, "totalScoreText", EnsureAdaptor(totalScore));
            SetSO(summarySO, "distanceText", EnsureAdaptor(FindChild(summaryRT, "DistanceText")));
            SetSO(summarySO, "timeText", EnsureAdaptor(FindChild(summaryRT, "TimeText")));
            SetSO(summarySO, "avgSpeedText", EnsureAdaptor(FindChild(summaryRT, "AvgSpeedText")));
            SetSO(summarySO, "distanceScoreText", EnsureAdaptor(FindChild(summaryRT, "DistanceScoreText")));
            SetSO(summarySO, "speedScoreText", EnsureAdaptor(FindChild(summaryRT, "SpeedScoreText")));
            SetSO(summarySO, "beautyScoreText", EnsureAdaptor(FindChild(summaryRT, "BeautyScoreText")));
            SetSO(summarySO, "proximityScoreText", EnsureAdaptor(FindChild(summaryRT, "ProximityScoreText")));
            SetSO(summarySO, "runAgainButton", runAgain.GetComponent<Button>());
            SetSO(summarySO, "continueButton", continueBtn.GetComponent<Button>());
            summarySO.ApplyModifiedPropertiesWithoutUndo();

            // HUDController wiring. Attach to hudCanvas (always active), NOT hudPanel — the
            // panel is toggled inactive in Lobby state, which would prevent HUDController.Start
            // from running and the Start Run button from ever being shown or wired.
            var hud = hudCanvas.gameObject.AddComponent<HUDController>();
            var hudSO = new SerializedObject(hud);
            SetSO(hudSO, "hudPanel", hudPanel);
            SetSO(hudSO, "speedText", EnsureAdaptor(speed));
            SetSO(hudSO, "altitudeText", EnsureAdaptor(altitude));
            SetSO(hudSO, "timeText", EnsureAdaptor(time));
            SetSO(hudSO, "distanceText", EnsureAdaptor(distance));
            SetSO(hudSO, "playersText", EnsureAdaptor(players));
            SetSO(hudSO, "startRunButton", startRun);
            SetSO(hudSO, "endRunButton", endRun.GetComponent<Button>());
            SetSO(hudSO, "viewToggleButton", viewToggle.GetComponent<Button>());
            SetSO(hudSO, "beaconFormButton", beaconBtn.GetComponent<Button>());

            // Offline badge (spec §8.1): hidden by default; HUD shows it when a connect
            // attempt failed or dropped.
            var offlineBadge = MakeLabel(hudPanelRT, "OfflineBadge", "OFFLINE RACE", 24, new Vector2(0, 850));
            SetColor(offlineBadge, new Color(1f, 0.55f, 0.1f));
            offlineBadge.SetActive(false);
            SetSO(hudSO, "offlineBadge", offlineBadge);
            hudSO.ApplyModifiedPropertiesWithoutUndo();

            // Friend-match UI (spec §8.5).
            BuildFriendMatchUI(hudCanvas);

            // One-time safety disclaimer (spec §23).
            BuildSafetyDisclaimer(hudCanvas);

            // Wire GameManager refs
            var gm = gmGo.GetComponent<GameManager>();
            var gmSO = new SerializedObject(gm);
            SetSO(gmSO, "crashSequence", crashGo.GetComponent<CrashSequence>());
            gmSO.ApplyModifiedPropertiesWithoutUndo();

            // Flash overlay image (full-screen, on HUD canvas, hidden)
            var flashGo = new GameObject("CrashFlashOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var flashRT = flashGo.GetComponent<RectTransform>();
            flashRT.SetParent(hudCanvas.transform, false);
            Stretch(flashRT);
            flashGo.GetComponent<Image>().color = new Color(1, 0, 0, 0);
            flashGo.SetActive(false);
            var flashImg = flashGo.GetComponent<Image>();
            var csSO = new SerializedObject(crashGo.GetComponent<CrashSequence>());
            SetSO(csSO, "flashOverlay", flashImg);
            csSO.ApplyModifiedPropertiesWithoutUndo();

            // ───────────────────────────────────────────────────────────────
            // Lightfield match core (Track G — active decisions 2026-07-18).
            //
            // All additions below are reflection-driven so this generator compiles
            // whether or not the Lightfield / Afterglow / Multiplayer / Gameplay
            // tracks are merged at compile time. Pure-C# host authorities
            // (LightfieldVolume, GateSpawner) are NOT MonoBehaviours — Track D's
            // MatchManager constructs and registers them at runtime on the
            // ServiceLocator — so TryAddType gracefully no-ops for them today;
            // the call sites remain so a future MonoBehaviour conversion wires
            // automatically. See SPEC §1.5 and the "Lightfield Architecture"
            // section for the contract map.
            // ───────────────────────────────────────────────────────────────

            // MatchManager (Track D — decisions E/F/I/O/P/Q/T/U): the match
            // sub-FSM (Idle→Warmup→Countdown→Live→Scoring→Expired). It wakes
            // after PlatformServiceRegistry — that GO is created first above,
            // so Unity's MonoBehaviour Awake order is correct by creation
            // order; [DefaultExecutionOrder] on MatchManager pins it further.
            TryAddType("LightRunners.Gameplay.MatchManager", "MatchManager");

            // ViewModeBootstrap (Track D — decision H): forces AR as the
            // default view on scene load. Lives on its own GO so designers
            // can disable it without touching GameManager.
            TryAddType("LightRunners.Gameplay.ViewModeBootstrap", "ViewModeBootstrap");

            // Pure-C# host authorities. NO scene GameObject is needed today —
            // MatchManager constructs and registers these on the ServiceLocator.
            // TryAddType is a graceful no-op for non-MonoBehaviour types; the
            // calls stay so a future conversion wires them automatically.
            TryAddType("LightRunners.Lightfield.LightfieldVolume", "LightfieldVolume");
            TryAddType("LightRunners.Lightfield.GateSpawner", "GateSpawner");

            // Round-2 review fix R2-F2: the LumenGateVisualizer (Round-1 fix R2-F2) and
            // StolenLumenPickupSpawner (Round-1 fix R1-F2) components existed but were never
            // placed in the scene, so the gate-collection and stolen-Lumen loops were dead code
            // despite the commit messages claiming they were closed. Mount both here.
            TryAddType("LightRunners.Lightfield.LumenGateVisualizer", "LumenGateVisualizer");
            TryAddType("LightRunners.Lightfield.StolenLumenPickupSpawner", "StolenLumenPickupSpawner");

            // Afterglow Overview stack (Track F — decisions A/U/T/S).
            BuildAfterglowStack();

            // MatchHUD canvas (Track D — decisions H/I): TacticalRadar,
            // OffScreenIndicator, LeaderCrown.
            BuildMatchHUD();

            // NetworkMatchState (Track C — decisions Q/T): the host-authoritative
            // frozen tail-radius state. GLM-C1 fix: the old code never spawned this
            // object, making PublishFrozenConfigToNetwork dead. Place it in the scene
            // with a NetworkIdentity so the host owns it and SyncVars replicate to clients.
            TryAddMirrorMatchState();

            EnsureEventSystem();

            EditorSceneManager.SaveScene(scene, GamePath);
            Debug.Log("[SceneSetup] Game scene written to " + GamePath);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────
        private static void EnsureScenesDir()
        {
            if (!AssetDatabase.IsValidFolder(ScenesDir))
            {
                Directory.CreateDirectory(ScenesDir);
                AssetDatabase.Refresh();
            }
        }

        private static Canvas MakeCanvas(string name, int width, int height, int sortingOrder = 0,
            RenderMode renderMode = RenderMode.ScreenSpaceOverlay)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var c = go.GetComponent<Canvas>();
            c.renderMode = renderMode;
            c.sortingOrder = sortingOrder;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(width, height);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f; // match-height per spec §1.2
            return c;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static Image AddImage(GameObject go, Color color)
        {
            var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        private static GameObject MakeLabel(Transform parent, string name, string text, int fontSize, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(600, fontSize + 16);
            rt.anchoredPosition = pos;
            var t = go.GetComponent<Text>();
            t.text = text;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = fontSize;
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return go;
        }

        private static GameObject MakeButton(Transform parent, string name, string label, int fontSize, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(420, fontSize + 40);
            rt.anchoredPosition = pos;
            go.GetComponent<Image>().color = new Color(0.15f, 0.2f, 0.35f, 1f);

            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var labelRT = labelGo.GetComponent<RectTransform>();
            labelRT.SetParent(go.transform, false);
            Stretch(labelRT);
            var t = labelGo.GetComponent<Text>();
            t.text = label;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = fontSize;
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            return go;
        }

        private static void SetColor(GameObject labelGo, Color c)
        {
            var t = labelGo.GetComponent<Text>();
            if (t != null) t.color = c;
        }

        private static TMP_TextAdaptor EnsureAdaptor(GameObject labelGo)
        {
            if (labelGo == null) return null;
            var adapt = labelGo.GetComponent<TMP_TextAdaptor>() ?? labelGo.AddComponent<TMP_TextAdaptor>();
            // Hook the adaptor to the existing Text component.
            var so = new SerializedObject(adapt);
            var textProp = so.FindProperty("_textComponent");
            if (textProp != null && textProp.objectReferenceValue == null)
            {
                var txt = labelGo.GetComponent<Text>();
                if (txt != null) textProp.objectReferenceValue = txt;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return adapt;
        }

        private static GameObject FindChild(Transform parent, string name)
        {
            var t = parent.Find(name);
            return t?.gameObject;
        }

        private static void SetSO(SerializedObject so, string field, UnityEngine.Object value)
        {
            if (so == null) return;
            var prop = so.FindProperty(field);
            if (prop != null) prop.objectReferenceValue = value;
        }

        private static void EnsureEventSystem()
        {
            // Avoid duplicates: a scene typically wants exactly one EventSystem.
            var existing = UnityEngine.Object.FindAnyObjectByType<EventSystem>();
            if (existing != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        private static void TryAddType(string fullTypeName, string gameObjectName)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = asm.GetType(fullTypeName);
                    if (type != null && type.IsSubclassOf(typeof(MonoBehaviour)))
                    {
                        var go = new GameObject(gameObjectName);
                        go.AddComponent(type);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneSetup] Could not add {fullTypeName}: {e.Message}");
            }
        }

        /// <summary>
        /// Add the MirrorNetworkMatchState to the scene with a NetworkIdentity.
        /// GLM-C1 fix: without this GameObject, PublishFrozenConfigToNetwork can't
        /// find the state object and the frozen tail radius (decision T) is never
        /// replicated to clients.
        /// </summary>
        private static void TryAddMirrorMatchState()
        {
            try
            {
                Type stateType = FindTypeByName("LightRunners.Multiplayer.MirrorNetworkMatchState");
                if (stateType == null || !stateType.IsSubclassOf(typeof(MonoBehaviour)))
                {
                    Debug.Log("[SceneSetup] MirrorNetworkMatchState type not found — skipping.");
                    return;
                }

                var go = new GameObject("MirrorNetworkMatchState");
                // NetworkIdentity is required for any NetworkBehaviour to function.
                Type identityType = FindTypeByName("Mirror.NetworkIdentity");
                if (identityType != null) go.AddComponent(identityType);
                go.AddComponent(stateType);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneSetup] Could not add MirrorNetworkMatchState: {e.Message}");
            }
        }

        /// <summary>
        /// Add the MirrorLauncher to the scene and wire its playerPrefab to the
        /// MirrorPlayer prefab. If the prefab doesn't exist yet, creates the GO
        /// anyway (the prefab can be wired later via the inspector).
        /// </summary>
        private static void TryAddMirrorLauncher()
        {
            try
            {
                Type launcherType = FindTypeByName("LightRunners.Multiplayer.MirrorLauncher");
                if (launcherType == null || !launcherType.IsSubclassOf(typeof(MonoBehaviour)))
                {
                    Debug.Log("[SceneSetup] MirrorLauncher type not found — Mirror not installed.");
                    return;
                }

                var go = new GameObject("MirrorLauncher");
                var launcher = go.AddComponent(launcherType);

                // CRITICAL: Mirror's NetworkManager requires a Transport component
                // on the same GameObject. Without it, StartHost() NREs inside
                // NetworkServer.Listen(). KCP (UDP) is Mirror's default transport.
                // The class is kcp2k.KcpTransport (not Mirror.KcpTransport).
                Type transportType = FindTypeByName("kcp2k.KcpTransport");
                if (transportType != null)
                {
                    go.AddComponent(transportType);
                }
                else
                {
                    Debug.LogError("[SceneSetup] KcpTransport type not found! " +
                                   "StartHost() will fail without a transport.");
                }

                // Wire the playerPrefab if the MirrorPlayer prefab exists.
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Resources/Player/MirrorPlayer.prefab");
                if (prefab != null)
                {
                    var so = new SerializedObject(launcher);
                    var prop = so.FindProperty("playerPrefab");
                    if (prop != null) prop.objectReferenceValue = prefab;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log("[SceneSetup] MirrorLauncher wired with MirrorPlayer prefab.");
                }
                else
                {
                    Debug.LogWarning("[SceneSetup] MirrorLauncher created but MirrorPlayer.prefab not found. " +
                                     "Run Light-Runners → Setup → Mirror Player Prefab first.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneSetup] Could not add MirrorLauncher: {e.Message}");
            }
        }

        /// <summary>
        /// Add the AR stack (spec §14.2) — only if AR Foundation is installed. Uses reflection
        /// throughout so this editor assembly compiles without the package:
        ///   AR/
        ///     ARSession
        ///     XROrigin (+ARPlaneManager) / CameraOffset / ARCamera (+manager, background, pose driver)
        ///     ARTrails, ARBeacons
        ///     ARViewManager (fully wired)
        /// Returns the AR camera for ViewTransitionManager wiring, or null.
        /// </summary>
        private static Camera TryAddARStack(GameObject gameManagerGo)
        {
            try
            {
                Type sessionType = FindTypeByName("UnityEngine.XR.ARFoundation.ARSession");
                Type planeMgrType = FindTypeByName("UnityEngine.XR.ARFoundation.ARPlaneManager");
                Type camMgrType = FindTypeByName("UnityEngine.XR.ARFoundation.ARCameraManager");
                Type camBgType = FindTypeByName("UnityEngine.XR.ARFoundation.ARCameraBackground");
                Type originType = FindTypeByName("Unity.XR.CoreUtils.XROrigin");
                Type poseDriverType = FindTypeByName("UnityEngine.InputSystem.XR.TrackedPoseDriver");
                Type viewMgrType = FindTypeByName("LightRunners.AR.ARViewManager");

                if (sessionType == null || originType == null)
                {
                    Debug.Log("[SceneSetup] AR stack skipped — AR Foundation not installed.");
                    return null;
                }

                var arRoot = new GameObject("AR");

                var sessionGo = new GameObject("ARSession");
                sessionGo.transform.SetParent(arRoot.transform, false);
                var session = sessionGo.AddComponent(sessionType);

                var originGo = new GameObject("XROrigin");
                originGo.transform.SetParent(arRoot.transform, false);
                var origin = originGo.AddComponent(originType);
                var planeMgr = planeMgrType != null ? originGo.AddComponent(planeMgrType) : null;

                var offsetGo = new GameObject("CameraOffset");
                offsetGo.transform.SetParent(originGo.transform, false);

                var camGo = new GameObject("ARCamera");
                camGo.transform.SetParent(offsetGo.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.depth = -1;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                if (camMgrType != null) camGo.AddComponent(camMgrType);
                if (camBgType != null) camGo.AddComponent(camBgType);
                if (poseDriverType != null) camGo.AddComponent(poseDriverType); // pitfall #6
                camGo.SetActive(false); // ViewTransitionManager enables it on EnterAR

                // XROrigin.Camera + CameraFloorOffsetObject (properties, set reflectively).
                originType.GetProperty("Camera")?.SetValue(origin, cam);
                originType.GetProperty("CameraFloorOffsetObject")?.SetValue(origin, offsetGo);

                var trailsGo = new GameObject("ARTrails");
                trailsGo.transform.SetParent(arRoot.transform, false);
                var beaconsGo = new GameObject("ARBeacons");
                beaconsGo.transform.SetParent(arRoot.transform, false);

                if (viewMgrType != null && viewMgrType.IsSubclassOf(typeof(MonoBehaviour)))
                {
                    var mgr = (Component)arRoot.AddComponent(viewMgrType);
                    var so = new SerializedObject(mgr);
                    SetSO(so, "arSession", session);
                    SetSO(so, "xrOrigin", origin);
                    if (planeMgr != null) SetSO(so, "planeManager", planeMgr);
                    SetSO(so, "arTrailParent", trailsGo.transform);
                    SetSO(so, "arBeaconParent", beaconsGo.transform);
                    so.ApplyModifiedPropertiesWithoutUndo();
                }

                return cam;
            }
            catch (Exception e)
            {
                Debug.Log($"[SceneSetup] AR stack skipped ({e.Message}).");
                return null;
            }
        }

        private static Type FindTypeByName(string fullTypeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullTypeName); if (t != null) return t; }
                catch (Exception) { /* skip */ }
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Friend match UI (spec §8.5)
        // ─────────────────────────────────────────────────────────────────────
        private static void BuildFriendMatchUI(Canvas hudCanvas)
        {
            // Entry button, below Start Run (Lobby only — LobbyUIController manages visibility).
            var friendBtn = MakeButton(hudCanvas.transform, "FriendMatchButton", "Friend Match", 36, new Vector2(0, -140));

            // Entry panel: Create / Join.
            var entry = new GameObject("FriendEntryPanel", typeof(RectTransform), typeof(CanvasRenderer));
            var entryRT = entry.GetComponent<RectTransform>();
            entryRT.SetParent(hudCanvas.transform, false);
            entryRT.sizeDelta = new Vector2(700, 520);
            entryRT.anchoredPosition = new Vector2(0, -420);
            AddImage(entry, new Color(0.04f, 0.05f, 0.1f, 0.95f));

            var createBtn = MakeButton(entryRT, "CreateButton", "Create Room", 36, new Vector2(0, 160));
            var codeInput = MakeInputField(entryRT, "CodeInput", "ENTER CODE", new Vector2(0, 40));
            var joinBtn = MakeButton(entryRT, "JoinButton", "Join Room", 36, new Vector2(0, -70));
            var errorLabel = MakeLabel(entryRT, "ErrorText", "", 24, new Vector2(0, -180));
            SetColor(errorLabel, new Color(1f, 0.5f, 0.4f));
            entry.SetActive(false);

            // Party panel: code + roster + host start.
            var party = new GameObject("PartyPanel", typeof(RectTransform), typeof(CanvasRenderer));
            var partyRT = party.GetComponent<RectTransform>();
            partyRT.SetParent(hudCanvas.transform, false);
            partyRT.sizeDelta = new Vector2(800, 900);
            partyRT.anchoredPosition = Vector2.zero;
            AddImage(party, new Color(0.04f, 0.05f, 0.1f, 0.95f));

            var codeLabel = MakeLabel(partyRT, "CodeText", "------", 96, new Vector2(0, 320));
            var copyBtn = MakeButton(partyRT, "CopyButton", "Copy", 28, new Vector2(0, 210));
            var roster = MakeLabel(partyRT, "RosterText", "", 30, new Vector2(0, 20));
            var hint = MakeLabel(partyRT, "HintText", "", 26, new Vector2(0, -160));
            var startRace = MakeButton(partyRT, "StartRaceButton", "Start Race", 44, new Vector2(0, -280));
            var leave = MakeButton(partyRT, "LeaveButton", "Leave", 32, new Vector2(0, -380));
            party.SetActive(false);

            var controller = hudCanvas.gameObject.AddComponent<LobbyUIController>();
            var so = new SerializedObject(controller);
            SetSO(so, "friendMatchButton", friendBtn);
            SetSO(so, "entryPanel", entry);
            SetSO(so, "createButton", createBtn.GetComponent<Button>());
            SetSO(so, "joinButton", joinBtn.GetComponent<Button>());
            SetSO(so, "codeInput", codeInput);
            SetSO(so, "errorText", EnsureAdaptor(errorLabel));
            SetSO(so, "partyPanel", party);
            SetSO(so, "codeText", EnsureAdaptor(codeLabel));
            SetSO(so, "rosterText", EnsureAdaptor(roster));
            SetSO(so, "hintText", EnsureAdaptor(hint));
            SetSO(so, "copyButton", copyBtn.GetComponent<Button>());
            SetSO(so, "startRaceButton", startRace.GetComponent<Button>());
            SetSO(so, "leaveButton", leave.GetComponent<Button>());
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static InputField MakeInputField(Transform parent, string name, string placeholder, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(420, 70);
            rt.anchoredPosition = pos;
            go.GetComponent<Image>().color = new Color(0.1f, 0.12f, 0.2f, 1f);

            var textGo = MakeLabel(rt, "Text", "", 32, Vector2.zero);
            var placeholderGo = MakeLabel(rt, "Placeholder", placeholder, 32, Vector2.zero);
            SetColor(placeholderGo, new Color(1f, 1f, 1f, 0.35f));

            var field = go.GetComponent<InputField>();
            field.textComponent = textGo.GetComponent<Text>();
            field.placeholder = placeholderGo.GetComponent<Text>();
            field.characterLimit = 8;
            return field;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Safety disclaimer (spec §23)
        // ─────────────────────────────────────────────────────────────────────
        private static void BuildSafetyDisclaimer(Canvas hudCanvas)
        {
            var panel = new GameObject("SafetyDisclaimerPanel", typeof(RectTransform), typeof(CanvasRenderer));
            var rt = panel.GetComponent<RectTransform>();
            rt.SetParent(hudCanvas.transform, false);
            Stretch(rt);
            AddImage(panel, new Color(0.02f, 0.02f, 0.05f, 0.97f));

            MakeLabel(rt, "Title", "Heads up", 56, new Vector2(0, 360));
            var body = MakeLabel(rt, "Body",
                "Light Runners is played in the real world.\n\n" +
                "Eyes on the world, not the screen.\n" +
                "Watch for traffic, obstacles, and people.\n" +
                "Your trails are visible to nearby runners for 24 hours.",
                30, new Vector2(0, 80));
            body.GetComponent<RectTransform>().sizeDelta = new Vector2(880, 400);
            var ack = MakeButton(rt, "AcknowledgeButton", "Got it — run smart", 40, new Vector2(0, -320));
            panel.SetActive(false);

            var ui = panel.AddComponent<SafetyDisclaimerUI>();
            var so = new SerializedObject(ui);
            SetSO(so, "panel", panel);
            SetSO(so, "acknowledgeButton", ack.GetComponent<Button>());
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lightfield match core additions (Track G — active decisions 2026-07-18)
        //
        // Every block below uses TryAddType / FindTypeByName so the file
        // compiles regardless of which tracks/assemblies are present. The
        // blocks intentionally do NOT throw when a track is missing — they
        // log and skip, so the scene still generates on a Phase-0 worktree.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Track F — Afterglow Overview stack (decisions A, U, T, S).
        ///
        /// Adds an "Afterglow" GameObject with an <c>AfterglowViewController</c>
        /// component (decision U) and a child "OverviewCamera" GameObject that
        /// carries the <c>OverviewCameraController</c> + an orthographic
        /// <c>Camera</c>. The controller's <c>[RequireComponent(typeof(Camera))]</c>
        /// attribute means we add the Camera first; AddComponent on the
        /// controller then resolves it.
        ///
        /// Walk-Inside view is intentionally NOT added (decision S — aerial
        /// milestone deferred). The controller's walkInsideView slot stays null;
        /// switching to WalkInside no-ops per the controller's own contract.
        /// </summary>
        private static void BuildAfterglowStack()
        {
            Type viewControllerType = FindTypeByName("LightRunners.Afterglow.AfterglowViewController");
            Type overviewCamType = FindTypeByName("LightRunners.Afterglow.OverviewCameraController");
            if (viewControllerType == null && overviewCamType == null)
            {
                Debug.Log("[SceneSetup] Afterglow stack skipped — Track F (LightRunners.Afterglow) not present.");
                return;
            }

            try
            {
                // Parent GO stays active so runtime lookup can always resolve the controller;
                // the controller toggles the individual view children.
                var root = new GameObject("Afterglow");

                // Overview camera (Track F: top-down orthographic, frames all captured trails).
                GameObject overviewView = null;
                Camera overviewCam = null;
                if (overviewCamType != null)
                {
                    overviewView = new GameObject("OverviewCamera");
                    overviewView.transform.SetParent(root.transform, false);
                    overviewCam = overviewView.AddComponent<Camera>();
                    overviewCam.orthographic = true;
                    overviewCam.enabled = false; // ViewTransitionManager-equivalent: start hidden.
                    // RequireComponent(Camera) — adding the behaviour won't create a second camera.
                    var overviewCtrl = overviewView.AddComponent(overviewCamType);
                }

                if (viewControllerType != null)
                {
                    var controller = root.AddComponent(viewControllerType);
                    if (controller is Component c)
                    {
                        var so = new SerializedObject(c);
                        SetSO(so, "overviewView", overviewView);
                        // walkInsideView intentionally null (decision S — deferred).
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                if (overviewView != null)
                    overviewView.SetActive(false); // hidden until IMatchSession raises MatchExpired.
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneSetup] Afterglow stack setup failed: {e.Message}");
            }
        }

        /// <summary>
        /// Track D — Match HUD (decisions H, I): a small canvas above the HUD
        /// carrying <c>TacticalRadar</c>, <c>OffScreenIndicator</c>, and
        /// <c>LeaderCrown</c>. All three are decoupled UI widgets that self-wire
        /// to <see cref="GameEvents"/> + the locator, so the scene generator only
        /// needs to add the components on a Canvas — the widgets resolve their
        /// own children/refs from their own <c>Awake</c>/<c>OnEnable</c>.
        /// </summary>
        private static void BuildMatchHUD()
        {
            try
            {
                // Canvas above the existing HUDCanvas (sortingOrder 11) so the
                // radar / indicators / crown paint on top of the gameplay HUD.
                var matchCanvas = MakeCanvas("MatchHUD", 1080, 1920, sortingOrder: 11);

                // Full-stretch indicator layer (OffScreenIndicator repositions
                // arrows along the screen edge each LateUpdate).
                var indicatorLayer = new GameObject("IndicatorLayer", typeof(RectTransform), typeof(CanvasRenderer));
                var indicatorRT = indicatorLayer.GetComponent<RectTransform>();
                indicatorRT.SetParent(matchCanvas.transform, false);
                Stretch(indicatorRT);

                // TacticalRadar slot (corner). The component builds its own
                // ring/blip children procedurally, so we just need a RectTransform
                // root + the component.
                var radarRoot = new GameObject("TacticalRadar", typeof(RectTransform));
                var radarRT = radarRoot.GetComponent<RectTransform>();
                radarRT.SetParent(matchCanvas.transform, false);
                // Top-right corner of the screen (decision H — corner radar).
                radarRT.anchorMin = new Vector2(1f, 1f);
                radarRT.anchorMax = new Vector2(1f, 1f);
                radarRT.pivot = new Vector2(1f, 1f);
                radarRT.sizeDelta = new Vector2(280f, 280f);
                radarRT.anchoredPosition = new Vector2(-40f, -120f);

                // Add the three UI behaviours. TryAddType-style reflection so
                // the file compiles without Track D's UI assembly present.
                AddUIComponent(matchCanvas.gameObject, "LightRunners.UI.TacticalRadar");
                AddUIComponent(matchCanvas.gameObject, "LightRunners.UI.OffScreenIndicator");
                AddUIComponent(matchCanvas.gameObject, "LightRunners.UI.LeaderCrown");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneSetup] MatchHUD setup failed: {e.Message}");
            }
        }

        /// <summary>
        /// Reflect-add a MonoBehaviour to a GameObject by full type name.
        /// Same pattern as <see cref="TryAddType"/> but on an explicit GO
        /// (the match HUD behaviours must land on the MatchHUD canvas, not
        /// a freshly-created root).
        /// </summary>
        private static void AddUIComponent(GameObject host, string fullTypeName)
        {
            try
            {
                var type = FindTypeByName(fullTypeName);
                if (type == null || !type.IsSubclassOf(typeof(MonoBehaviour)))
                {
                    Debug.Log($"[SceneSetup] UI component {fullTypeName} skipped — type not found (track not merged yet).");
                    return;
                }
                host.AddComponent(type);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneSetup] Could not add {fullTypeName}: {e.Message}");
            }
        }

        [MenuItem("Light-Runners/Setup/Add Scenes to Build Settings")]
        public static void EnsureBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes;
            bool hasLogin = Array.Exists(scenes, s => s.path == LoginPath);
            bool hasGame = Array.Exists(scenes, s => s.path == GamePath);

            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes);
            if (!hasLogin)
            {
                list.Insert(0, new EditorBuildSettingsScene(LoginPath, true));
                Debug.Log("[SceneSetup] Added Login scene at index 0.");
            }
            if (!hasGame)
            {
                // Login must be index 0; place Game right after it.
                int insertAt = Mathf.Min(1, list.Count);
                list.Insert(insertAt, new EditorBuildSettingsScene(GamePath, true));
                Debug.Log("[SceneSetup] Added Game scene.");
            }
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}
#endif
