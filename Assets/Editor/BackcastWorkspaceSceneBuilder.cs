// BackcastWorkspaceSceneBuilder.cs — issue #59 "Backcast workspace root" (scene authoring tool)
//
// Builds Assets/Scenes/BackcastWorkspace.unity — the production composition root (ADR-0009) — and
// makes it the FIRST AND ONLY enabled build scene. The owner picked the Editor-builder path
// (2026-06-15): authoring the scene-authored hierarchy + serialized references in code (then a
// one-time menu run + visual check) rather than hand-placing every GameObject. The runtime
// BackcastWorkspaceRoot fills inner widgets via the existing builders at Play (findings 0025 §1/§6),
// so this tool authors the FRAME (canvas, containers, viewport/content, FloatingWindowLayer + the
// two adopted floating window shells — Strategy Editor + Order ticket — and footer) and wires the
// references.
//
// #99 (ADR-0017 / findings 0075): the Hakoniwa split-grid surface and its 5 tile GameObjects
// (startup / chart / orders / positions / run_result) are RETIRED — the dock cluster is built
// entirely at runtime by `SpawnBaseDockWindows` / `SyncChartWindowsToUniverse`, so the scene no
// longer authors them. Two scene-authored floating windows remain: the adopted Strategy Editor
// (region_001 shell) and the Order ticket (the #23 re-home).
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

        // ── center workspace: Viewport → Content → FloatingWindowLayer (#99: no HakoniwaRoot) ──
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
        SetRef(so, "_floatingLayer", floatingLayer);
        SetRef(so, "_strategyEditorWindow", window);
        SetRef(so, "_strategyEditorBody", body);
        SetRef(so, "_strategyEditorTitleInput", titleInput);
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

    static void SetRef(SerializedObject so, string field, Object value)
    {
        var prop = so.FindProperty(field);
        if (prop == null) { Debug.LogError("[BackcastWorkspaceSceneBuilder] missing serialized field: " + field); return; }
        prop.objectReferenceValue = value;
    }
}
