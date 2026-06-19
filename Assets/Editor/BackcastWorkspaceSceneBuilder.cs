// BackcastWorkspaceSceneBuilder.cs — issue #59 "Backcast workspace root" (scene authoring tool)
//
// Builds Assets/Scenes/BackcastWorkspace.unity — the production composition root (ADR-0009) — and
// makes it the FIRST AND ONLY enabled build scene. The owner picked the Editor-builder path
// (2026-06-15): authoring the scene-authored hierarchy + serialized references in code (then a
// one-time menu run + visual check) rather than hand-placing every GameObject. The runtime
// BackcastWorkspaceRoot fills inner widgets via the existing builders at Play (findings 0025 §1/§6),
// so this tool authors the FRAME (canvas, containers, viewport/content, Hakoniwa tiles,
// FloatingWindowLayer + the adopted Strategy Editor window frame, footer) and wires the references.
//
// Run: Tools > Backcast > Build Workspace Scene  → expect "[BackcastWorkspaceSceneBuilder] built ...".

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public static class BackcastWorkspaceSceneBuilder
{
    public const string ScenePath = "Assets/Scenes/BackcastWorkspace.unity";

    const float MENU_H = 24f;
    const float FOOTER_H = 40f;
    const float SIDEBAR_W = 200f;
    const string WINDOW_ID = "strategy_editor:region_001";
    const string ORDER_WINDOW_ID = "order:region_001";   // #23 re-home: adopted Order ticket window

    // #93 perspective stage (findings 0068 §15): the dedicated Hakoniwa layer, the RT asset the stage
    // camera renders into (and the Content RawImage displays), and the canvas-unit box for the grid.
    // All stage FRAMING (fov / camDistance / tilt / board dims) derives from the math SoT
    // HakoniwaStageMath.StageParams.Default — no scene magic numbers (§15 F4).
    const string HAKONIWA_LAYER = "Hakoniwa";
    const string STAGE_RT_PATH = "Assets/Settings/HakoniwaStage.renderTexture";
    const int STAGE_RT_W = 1000;
    const int STAGE_RT_H = 640;
    const float HAKO_BOX_W = 1000f;
    const float HAKO_BOX_H = 640f;

    [MenuItem("Tools/Backcast/Build Workspace Scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // ── Canvas (ScreenSpaceOverlay) + EventSystem (new Input System) ──
        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var canvasRt = (RectTransform)canvasGo.transform;

        new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        // ── frame containers (chrome is OUTSIDE Content; findings 0025 §3/§6) ──
        var menuBar = NewRect("MenuBar", canvasRt);
        AnchorTopStrip(menuBar, MENU_H);
        var menuView = menuBar.gameObject.AddComponent<MenuBarView>();

        var footer = NewRect("Footer", canvasRt);
        AnchorBottomStrip(footer, FOOTER_H);

        var sidebar = NewRect("Sidebar", canvasRt);
        AnchorLeftColumn(sidebar, SIDEBAR_W, MENU_H, FOOTER_H);
        var sidebarView = sidebar.gameObject.AddComponent<UniverseSidebarView>();

        // The infinite-space FIELD fills the WHOLE window (owner 2026-06-15: "背景は無限空間・全体に・
        // サイドバーの後ろも背景"). It is the BACKMOST sibling so the chrome (menu/sidebar/footer) and the
        // uGUI footer draw ON TOP of it; the skybox is fully covered by the Viewport's opaque background.
        var center = NewRect("CenterWorkspace", canvasRt);
        Stretch(center);
        center.SetAsFirstSibling();

        // ── center workspace: Viewport → Content → {HakoniwaRoot[startup,chart], FloatingWindowLayer} ──
        var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(InfiniteCanvasInputSurface));
        var viewport = (RectTransform)viewportGo.transform;
        viewport.SetParent(center, false);
        Stretch(viewport);
        // Field bg from the theme's workspace_background (the single source). This bakes an editor PREVIEW
        // value into the scene; at Play BackcastWorkspaceRoot.ApplyViewportTheme re-applies it live, so the
        // hue is changed by editing the theme alone — no scene re-bake required.
        viewportGo.GetComponent<Image>().color = ThemeService.Current.colors.workspace_background;
        var inputSurface = viewportGo.GetComponent<InfiniteCanvasInputSurface>();

        var content = NewRect("Content", viewport);
        Identity(content);

        // #93 perspective stage (findings 0068 §15): HakoniwaRoot lives on its OWN World-Space
        // "Hakoniwa Stage" Canvas on a DEDICATED layer (NOT under Content). A perspective camera
        // renders ONLY that layer into a transparent RT; the Content-side RawImage (authored below)
        // shows the RT (the diorama "photo"). Framing derives from StageParams.Default (§15 F4).
        int hakoLayer = EnsureHakoniwaLayer();
        if (hakoLayer < 0) { Debug.LogError("[BackcastWorkspaceSceneBuilder] no free user layer for '" + HAKONIWA_LAYER + "'"); return; }
        var stageRt = LoadOrCreateStageRT();
        var stageParams = HakoniwaStageMath.StageParams.Default(stageRt.width, stageRt.height);

        var stageCanvasGo = new GameObject("HakoniwaStage", typeof(Canvas));
        stageCanvasGo.layer = hakoLayer;
        var stageCanvas = stageCanvasGo.GetComponent<Canvas>();
        stageCanvas.renderMode = RenderMode.WorldSpace;
        var stageCanvasRt = (RectTransform)stageCanvasGo.transform;
        stageCanvasRt.position = Vector3.zero;
        stageCanvasRt.localScale = Vector3.one * (stageParams.boardW / HAKO_BOX_W);              // board spans boardW world units (§15 F4)
        stageCanvasRt.rotation = Quaternion.Euler(stageParams.pitchDeg, stageParams.yawDeg, 0f); // the BOARD tilts, not the camera (§15 F3)

        var hakoniwaRoot = NewRect("HakoniwaRoot", stageCanvasRt);
        Identity(hakoniwaRoot);
        hakoniwaRoot.sizeDelta = new Vector2(HAKO_BOX_W, HAKO_BOX_H);   // a bounded box on the canvas for the grid
        var startupTile = NewRect("startup", hakoniwaRoot);
        var chartTile = NewRect("chart", hakoniwaRoot);
        // #23 re-home: three live data tiles (authored placeholders; runtime fills chrome + view).
        var ordersTile = NewRect("orders", hakoniwaRoot);
        var positionsTile = NewRect("positions", hakoniwaRoot);
        var runResultTile = NewRect("run_result", hakoniwaRoot);
        SetLayerRecursive(stageCanvasGo, hakoLayer);   // tiles render via the stage camera ONLY (§15 F2)

        // perspective stage camera: renders ONLY the Hakoniwa layer into the RT (§15 F2). Axis-parallel
        // at (0,0,-camDistance) looking +Z so floating windows never shear (§3); FOV from the math SoT.
        var stageCamGo = new GameObject("HakoniwaStageCamera", typeof(Camera));
        var stageCam = stageCamGo.GetComponent<Camera>();
        stageCam.orthographic = false;
        stageCam.fieldOfView = stageParams.fovDeg;
        stageCam.cullingMask = 1 << hakoLayer;
        stageCam.clearFlags = CameraClearFlags.SolidColor;
        stageCam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // transparent RT (HITL §8c verifies compositing)
        stageCam.targetTexture = stageRt;
        stageCamGo.transform.position = new Vector3(0f, 0f, -stageParams.camDistance);
        stageCamGo.transform.rotation = Quaternion.identity;
        stageCanvas.worldCamera = stageCam;

        // Main camera EXCLUDES the Hakoniwa layer (RT-exclusive, no double-draw — §15 F2).
        var mainCam = Camera.main;
        if (mainCam != null) mainCam.cullingMask &= ~(1 << hakoLayer);

        // #93 perspective stage (findings 0068 §15 F1): a RawImage in Content shows the perspective
        // camera's RenderTexture (the diorama "photo"), taking the old HakoniwaRoot slot. Authored
        // before FloatingWindowLayer so the floating windows draw IN FRONT of the stage photo. 段2 wires
        // the RT into .texture (the stage camera renders into the same RT — authored above).
        var hakoniwaRawGo = new GameObject("HakoniwaRawImage", typeof(RectTransform), typeof(RawImage));
        var hakoniwaRawImage = hakoniwaRawGo.GetComponent<RawImage>();
        hakoniwaRawImage.texture = stageRt;
        var hakoniwaRawRt = (RectTransform)hakoniwaRawGo.transform;
        hakoniwaRawRt.SetParent(content, false);
        Identity(hakoniwaRawRt);
        hakoniwaRawRt.sizeDelta = new Vector2(HAKO_BOX_W, HAKO_BOX_H);   // mirrors the old HakoniwaRoot box

        var floatingLayer = NewRect("FloatingWindowLayer", content);
        Identity(floatingLayer);

        // ── scene-authored Strategy Editor window (ADOPTED, never respawned; findings 0025 §8) ──
        // Built by the SHARED frame builder so the authored window matches runtime-spawned ones.
        var window = StrategyEditorWindowFrame.Build(WINDOW_ID, out var titleInput, out var body);
        window.SetParent(floatingLayer, false);
        window.anchorMin = window.anchorMax = new Vector2(0.5f, 0.5f);
        window.pivot = new Vector2(0f, 1f);                  // top-left pivot (canvas-logical contract)
        window.anchoredPosition = new Vector2(-300f, 220f);
        window.sizeDelta = new Vector2(620f, 460f);

        // ── #23 re-home: scene-authored Order ticket window (ADOPTED, KIND_ORDER) ──
        // Same shared-frame discipline as the editor; runtime injects OrderTicketView into the body.
        var orderWindow = OrderTicketWindowFrame.Build(ORDER_WINDOW_ID, out var orderTitleInput, out var orderBody);
        orderWindow.SetParent(floatingLayer, false);
        orderWindow.anchorMin = orderWindow.anchorMax = new Vector2(0.5f, 0.5f);
        orderWindow.pivot = new Vector2(0f, 1f);
        orderWindow.anchoredPosition = new Vector2(40f, -40f);
        orderWindow.sizeDelta = new Vector2(360f, 300f);

        // ── root GameObject/component + serialized reference wiring ──
        var rootGo = new GameObject("BackcastWorkspaceRoot");
        var rootComp = rootGo.AddComponent<BackcastWorkspaceRoot>();
        var so = new SerializedObject(rootComp);
        SetRef(so, "_centerWorkspace", center);
        SetRef(so, "_footerContainer", footer);
        SetRef(so, "_menuBarView", menuView);
        SetRef(so, "_sidebarView", sidebarView);
        SetRef(so, "_viewport", viewport);
        SetRef(so, "_content", content);
        SetRef(so, "_inputSurface", inputSurface);
        SetRef(so, "_hakoniwaRoot", hakoniwaRoot);
        SetRef(so, "_hakoniwaRawImage", hakoniwaRawImage);
        SetRef(so, "_startupTile", startupTile);
        SetRef(so, "_chartTile", chartTile);
        SetRef(so, "_floatingLayer", floatingLayer);
        SetRef(so, "_strategyEditorWindow", window);
        SetRef(so, "_strategyEditorBody", body);
        SetRef(so, "_strategyEditorTitleInput", titleInput);
        SetRef(so, "_ordersTile", ordersTile);
        SetRef(so, "_positionsTile", positionsTile);
        SetRef(so, "_runResultTile", runResultTile);
        SetRef(so, "_orderWindow", orderWindow);
        SetRef(so, "_orderWindowBody", orderBody);
        SetRef(so, "_orderWindowTitleInput", orderTitleInput);
        so.ApplyModifiedPropertiesWithoutUndo();

        // ── save scene + make it the only enabled build scene ──
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScenePath));
        bool ok = EditorSceneManager.SaveScene(scene, ScenePath);
        if (!ok) { Debug.LogError("[BackcastWorkspaceSceneBuilder] FAILED to save scene to " + ScenePath); return; }

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();

        BackcastDefaultScene.Apply();   // also make it the default Play scene immediately (no recompile wait)

        Debug.Log("[BackcastWorkspaceSceneBuilder] built " + ScenePath + " and set it as the only enabled build + default Play scene.");
    }

    // ---- helpers ----

    static RectTransform NewRect(string name, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    // Content/layer/HakoniwaRoot: centre anchors + pivot, zero offset (the infinite-canvas identity).
    static void Identity(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
    }

    static void AnchorTopStrip(RectTransform rt, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(0f, h);
        rt.anchoredPosition = Vector2.zero;
    }

    static void AnchorBottomStrip(RectTransform rt, float h)
    {
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f); rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(0f, h);
        rt.anchoredPosition = Vector2.zero;
    }

    static void AnchorLeftColumn(RectTransform rt, float w, float topInset, float bottomInset)
    {
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 0.5f);
        rt.offsetMin = new Vector2(0f, bottomInset);   // left=0, bottom=footer
        rt.offsetMax = new Vector2(w, -topInset);      // right=width, top=menu
    }

    // #93 perspective stage helpers (findings 0068 §15) ----

    // Ensure a dedicated "Hakoniwa" user layer exists (idempotent); returns its index, or -1 if the
    // 32 layer slots are exhausted. Section16's gate keys off hako.gameObject.layer != Default, not the
    // NAME, so any free user slot (8..31) is valid; this just gives it a stable, readable name.
    static int EnsureHakoniwaLayer()
    {
        int existing = LayerMask.NameToLayer(HAKONIWA_LAYER);
        if (existing >= 0) return existing;
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets == null || assets.Length == 0) { Debug.LogError("[BackcastWorkspaceSceneBuilder] TagManager.asset not found"); return -1; }
        var tagManager = new SerializedObject(assets[0]);
        var layers = tagManager.FindProperty("layers");
        for (int i = 8; i < layers.arraySize; i++)   // 0..7 are Unity built-ins
        {
            var sp = layers.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(sp.stringValue))
            {
                sp.stringValue = HAKONIWA_LAYER;
                tagManager.ApplyModifiedProperties();
                return i;
            }
        }
        return -1;
    }

    // Load (or create + persist) the transparent RenderTexture asset the stage camera renders into and
    // the Content RawImage displays. It MUST be a project asset (not an in-memory RT) so the RawImage /
    // camera references survive scene reload (the headless probe re-opens the committed scene).
    static RenderTexture LoadOrCreateStageRT()
    {
        var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(STAGE_RT_PATH);
        if (rt == null)
        {
            rt = new RenderTexture(STAGE_RT_W, STAGE_RT_H, 24, RenderTextureFormat.ARGB32) { name = "HakoniwaStage" };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(STAGE_RT_PATH));
            AssetDatabase.CreateAsset(rt, STAGE_RT_PATH);
        }
        return rt;
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
    }

    static void SetRef(SerializedObject so, string field, Object value)
    {
        var prop = so.FindProperty(field);
        if (prop == null) { Debug.LogError("[BackcastWorkspaceSceneBuilder] missing serialized field: " + field); return; }
        prop.objectReferenceValue = value;
    }
}
