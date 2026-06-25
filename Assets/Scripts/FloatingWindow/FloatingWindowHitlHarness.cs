// FloatingWindowHitlHarness.cs — issue #15 "floating windows" (THROWAWAY HITL gate)
//
// THROWAWAY turnkey playmode widget the OWNER spawns once (Tools > Backcast > Floating Window
// HITL) to FEEL the window system: three SPEC-DRIVEN demo windows (two Strategy Editors + one
// Order — NOT chart, which is a Hakoniwa tile) floating on the infinite canvas. DRAG A TITLE BAR
// to move a window (free placement); press/drag a title bar to bring it to FRONT (z-order); drag
// the BACKGROUND or a window BODY to pan and wheel to zoom — every window follows because the
// FloatingWindowLayer is a child of the infinite-canvas Content, while the screen-fixed HUD bar
// stays put. The × button hides a window (visible=false). Save/Load round-trips rect + z-order +
// visibility through the #12 LayoutStore (+ #13 canvasView).
//
// It REUSES #13's durable pan/zoom path (InfiniteCanvasInputSurface -> InfiniteCanvasController)
// and #15's durable move/raise path (FloatingWindowTitleInput -> FloatingWindowController), so
// the HITL run validates the SAME wiring the mainline shell reuses. NO auto-bootstrap (menu-
// spawned only), Python-FREE, and it does NOT touch ReplayPanelsHarness.AutoBootstrapEnabled —
// so it never collides with the single-Play-owner (#11, findings 0003 §8).
//
// Resize is OUT of #15's scope (findings 0008 §0), so the size-restore round-trip is proven non-
// vacuously by the AFK gate (FloatingWindowProbe S5); the "Mutate Demo" HUD button changes a
// window's size PROGRAMMATICALLY so the owner can still SEE size survive a Save/Load.

using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.InputSystem.UI;

public class FloatingWindowHitlHarness : MonoBehaviour
{
    static readonly Color BG_COLOR     = new Color(0.07f, 0.07f, 0.09f, 1f);
    static readonly Color BODY_COLOR   = new Color(0.16f, 0.18f, 0.22f, 0.96f);
    static readonly Color TITLE_COLOR  = new Color(0.24f, 0.27f, 0.34f, 1f);
    static readonly Color CLOSE_COLOR  = new Color(0.78f, 0.28f, 0.28f, 0.9f);
    static readonly Color HUD_BG       = new Color(0.0f, 0.0f, 0.0f, 0.6f);
    static readonly Color TEXT_COLOR   = new Color(0.92f, 0.92f, 0.94f, 1f);

    const float TITLE_H = 28f;

    string SavePath => Path.Combine(Application.persistentDataPath, "floating_window_hitl.json");

    InfiniteCanvasController _canvas;
    FloatingWindowController _windows;
    RectTransform _viewport;
    RectTransform _layer;
    FloatingWindowCatalog _catalog;
    Text _readout;
    Font _font;

    void Start()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        EnsureEventSystem();

        var canvasGo = new GameObject("FloatingWindowHitlCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        // Viewport: fills the screen, raycast-target background, durable pan/zoom input surface (#13).
        var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(InfiniteCanvasInputSurface));
        viewportGo.transform.SetParent(canvasGo.transform, false);
        _viewport = viewportGo.GetComponent<RectTransform>();
        _viewport.anchorMin = Vector2.zero; _viewport.anchorMax = Vector2.one;
        _viewport.offsetMin = Vector2.zero; _viewport.offsetMax = Vector2.zero;
        _viewport.pivot = new Vector2(0.5f, 0.5f);
        viewportGo.GetComponent<Image>().color = BG_COLOR;   // opaque -> raycast target for pan/zoom

        // Content: pan/zoom node, anchored + pivoted at the viewport centre (controller contract, #13).
        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(_viewport, false);
        var content = contentGo.GetComponent<RectTransform>();
        content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 0.5f);
        content.sizeDelta = Vector2.zero;

        _canvas = new InfiniteCanvasController(content);
        viewportGo.GetComponent<InfiniteCanvasInputSurface>().Initialize(_canvas, _viewport);

        // FloatingWindowLayer: a single identity-transform container under Content. All windows are
        // its children, so their z-order (sibling index) is isolated from any Hakoniwa, and they
        // ride Content for pan/zoom (findings 0008 §1).
        var layerGo = new GameObject("FloatingWindowLayer", typeof(RectTransform));
        layerGo.transform.SetParent(content, false);
        _layer = layerGo.GetComponent<RectTransform>();
        _layer.anchorMin = _layer.anchorMax = _layer.pivot = new Vector2(0.5f, 0.5f);
        _layer.anchoredPosition = Vector2.zero;
        _layer.sizeDelta = Vector2.zero;

        _catalog = FloatingWindowCatalog.Default();
        _windows = new FloatingWindowController(_layer, _catalog, BuildWindow);

        // Three spec-driven demo windows (multi-instance strategy_editor + singleton order).
        _windows.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "strategy_editor:region_001", -320f, 170f, 520f, 380f, true);
        _windows.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, "strategy_editor:region_002", 40f, 40f, 520f, 380f, true);
        _windows.Spawn(FloatingWindowCatalog.KIND_ORDER, "order", -120f, -210f, 360f, 300f, true);

        BuildHud(canvasGo.transform);

        Debug.Log("[FLOATING WINDOW HITL] ready: drag a TITLE BAR to move; press/drag a title to raise " +
                  "(front); drag the bottom-right \"◢\" GRIP to RESIZE (left-top fixed, right-bottom grow; #139 / " +
                  "ADR-0030 — same-island flush neighbours follow; ESC reverts); × hides a window; drag the " +
                  "background/body to pan, wheel to zoom (windows follow, HUD is screen-fixed). Save/Load " +
                  "round-trips rect + size + z-order + visibility.");
    }

    void Update()
    {
        if (_windows == null || _canvas == null || _readout == null) return;
        CanvasView v = _canvas.CaptureView();
        _readout.text = $"windows = {_windows.Count}   zoom = {v.zoom:0.00}   " +
                        "[title-drag=move/raise  ×=hide  bg/body-drag=pan  wheel=zoom]";
    }

    // ---- window factory (spec-driven): root bg (NOT a raycast target -> body drag pans) + a
    // raycast-target title bar carrying FloatingWindowTitleInput (move/raise) + title text +
    // optional × (hide) + a body label. Anchors/size/position are owned by the controller. ----
    RectTransform BuildWindow(FloatingWindowSpec spec, string id)
    {
        var rootGo = new GameObject("Window_" + id, typeof(RectTransform), typeof(Image));
        var root = rootGo.GetComponent<RectTransform>();
        var rootImg = rootGo.GetComponent<Image>();
        rootImg.color = BODY_COLOR;
        rootImg.raycastTarget = false;   // body falls through to canvas pan (findings 0008 §1)

        // accent rim (thin border tint) for visual identity — non-interactive.
        var rim = new GameObject("Rim", typeof(RectTransform), typeof(Image));
        rim.transform.SetParent(root, false);
        var rimRt = rim.GetComponent<RectTransform>();
        rimRt.anchorMin = Vector2.zero; rimRt.anchorMax = Vector2.one;
        rimRt.offsetMin = new Vector2(-2f, -2f); rimRt.offsetMax = new Vector2(2f, 2f);
        rim.GetComponent<Image>().color = spec.accent;
        rim.transform.SetAsFirstSibling();   // behind the body

        // title bar (raycast target + move/raise input).
        var titleGo = new GameObject("TitleBar", typeof(RectTransform), typeof(Image), typeof(FloatingWindowTitleInput));
        titleGo.transform.SetParent(root, false);
        var titleRt = titleGo.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0f, 1f); titleRt.anchorMax = new Vector2(1f, 1f); titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.offsetMin = new Vector2(0f, -TITLE_H); titleRt.offsetMax = new Vector2(0f, 0f);
        titleGo.GetComponent<Image>().color = TITLE_COLOR;   // opaque -> raycast target for the drag
        titleGo.GetComponent<FloatingWindowTitleInput>().Initialize(_windows, _canvas, _viewport, id);

        var titleText = AddText(titleRt, spec.title, TextAnchor.MiddleLeft);
        titleText.rectTransform.anchorMin = Vector2.zero; titleText.rectTransform.anchorMax = Vector2.one;
        titleText.rectTransform.offsetMin = new Vector2(10f, 0f); titleText.rectTransform.offsetMax = new Vector2(-34f, 0f);
        titleText.raycastTarget = false;

        if (spec.closeable)
        {
            var closeGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
            closeGo.transform.SetParent(titleRt, false);
            var crt = closeGo.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(1f, 0.5f); crt.anchorMax = new Vector2(1f, 0.5f); crt.pivot = new Vector2(1f, 0.5f);
            crt.sizeDelta = new Vector2(20f, 20f); crt.anchoredPosition = new Vector2(-6f, 0f);
            closeGo.GetComponent<Image>().color = CLOSE_COLOR;
            // Hide (visible=false): SetActive(false). Capture records it; Load reshows from disk.
            closeGo.GetComponent<Button>().onClick.AddListener(() =>
            {
                root.gameObject.SetActive(false);
                Debug.Log($"[FLOATING WINDOW HITL] hid '{id}' (visible=false). Save then Load to restore.");
            });
            var ct = AddText(crt, "x", TextAnchor.MiddleCenter); ct.raycastTarget = false;
            ct.rectTransform.anchorMin = Vector2.zero; ct.rectTransform.anchorMax = Vector2.one;
            ct.rectTransform.offsetMin = Vector2.zero; ct.rectTransform.offsetMax = Vector2.zero;
        }

        // body label (under the title) — non-interactive so body drags pan the canvas.
        var bt = AddText(root, spec.title + "\n(" + id + ")", TextAnchor.MiddleCenter);
        bt.fontSize = 16;
        bt.rectTransform.anchorMin = Vector2.zero; bt.rectTransform.anchorMax = Vector2.one;
        bt.rectTransform.offsetMin = new Vector2(8f, 8f); bt.rectTransform.offsetMax = new Vector2(-8f, -TITLE_H - 4f);
        bt.raycastTarget = false;

        return root;
    }

    // ---- HUD + EventSystem ----

    void EnsureEventSystem()
    {
        var es = FindAnyObjectByType<EventSystem>();
        if (es == null)
        {
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(transform, false);
            return;
        }
        var legacy = es.GetComponent<StandaloneInputModule>();
        if (legacy != null) legacy.enabled = false;
        var module = es.GetComponent<InputSystemUIInputModule>();
        if (module == null) module = es.gameObject.AddComponent<InputSystemUIInputModule>();
        module.enabled = true;
    }

    void BuildHud(Transform canvas)
    {
        var barGo = new GameObject("HudBar", typeof(RectTransform), typeof(Image));
        barGo.transform.SetParent(canvas, false);
        var bar = barGo.GetComponent<RectTransform>();
        bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = new Vector2(1f, 1f); bar.pivot = new Vector2(0.5f, 1f);
        bar.offsetMin = new Vector2(0f, -40f); bar.offsetMax = new Vector2(0f, 0f);
        barGo.GetComponent<Image>().color = HUD_BG;

        _readout = AddText(bar, "windows = …", TextAnchor.MiddleLeft);
        _readout.rectTransform.anchorMin = Vector2.zero; _readout.rectTransform.anchorMax = Vector2.one;
        _readout.rectTransform.offsetMin = new Vector2(12f, 0f); _readout.rectTransform.offsetMax = new Vector2(-480f, 0f);

        AddButton(bar, "Reset View", 1f, () => _canvas.ApplyView(CanvasView.Identity()));
        AddButton(bar, "Mutate Demo", 120f, MutateDemo);
        AddButton(bar, "Save",       245f, SaveLayout);
        AddButton(bar, "Load",       355f, LoadLayout);
    }

    void AddButton(RectTransform bar, string label, float rightInset, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(bar, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0.5f); rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(1f, 0.5f);
        rt.sizeDelta = new Vector2(110f, 30f);
        rt.anchoredPosition = new Vector2(-rightInset, 0f);
        go.GetComponent<Image>().color = new Color(0.20f, 0.22f, 0.28f, 1f);
        go.GetComponent<Button>().onClick.AddListener(onClick);
        var t = AddText(rt, label, TextAnchor.MiddleCenter);
        t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
        t.rectTransform.offsetMin = Vector2.zero; t.rectTransform.offsetMax = Vector2.zero;
        t.raycastTarget = false;
    }

    Text AddText(RectTransform parent, string text, TextAnchor anchor)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        t.font = _font; t.fontSize = 14; t.color = TEXT_COLOR; t.alignment = anchor;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        t.text = text;
        return t;
    }

    // Resize a demo window PROGRAMMATICALLY (resize gesture is out of #15's scope) so the owner can
    // SEE size survive Save/Load. Bumps region_001 to a distinct size; Save then Load proves it.
    void MutateDemo()
    {
        RectTransform rt = _windows.RectOf("strategy_editor:region_001");
        if (rt == null) { Debug.Log("[FLOATING WINDOW HITL] Mutate Demo: region_001 not present."); return; }
        rt.sizeDelta = new Vector2(700f, 520f);
        Debug.Log("[FLOATING WINDOW HITL] Mutate Demo: region_001 size -> 700x520. Save then Load to verify it persists.");
    }

    // ---- Save/Load: real LayoutStore round-trip of floatingWindows (+ canvasView, #13) ----

    void SaveLayout()
    {
        LayoutDocument doc = _windows.Capture();      // floatingWindows: rect + z-order + visibility
        doc.canvasView = _canvas.CaptureView();       // also persist pan/zoom (additive, #13)
        LayoutStore.Save(doc, SavePath);
        Debug.Log($"[FLOATING WINDOW HITL] saved {doc.floatingWindows.Count} windows -> {SavePath}");
    }

    void LoadLayout()
    {
        LayoutDocument doc = LayoutStore.Load(SavePath);   // sanitized (default if absent/corrupt)
        _windows.Apply(doc);                                // full replacement: spawn/move/hide/remove
        _canvas.ApplyView(doc.canvasView);
        Debug.Log($"[FLOATING WINDOW HITL] loaded {doc.floatingWindows.Count} windows <- {SavePath}");
    }
}
