// HakoniwaHitlHarness.cs — issue #14 "Hakoniwa split-grid" (THROWAWAY HITL gate)
//
// THROWAWAY turnkey playmode widget the OWNER spawns once (Tools > Backcast > Hakoniwa HITL)
// to FEEL the split-grid: a HakoniwaRoot of 5 labelled tiles (chart / status / positions /
// orders / run_result) in a ceil(√5)=3x2 grid (6th cell empty); DRAG A TILE HEADER onto
// another tile to SWAP them; drag the BACKGROUND to pan and wheel to zoom — the whole
// Hakoniwa follows because it is a child of the infinite-canvas Content, while the screen-
// fixed HUD bar stays put. Save/Load round-trips the tile ORDER through the #12 LayoutStore.
//
// It REUSES #13's durable pan/zoom path (InfiniteCanvasInputSurface -> InfiniteCanvasController
// -> CanvasViewMath -> Content) and #14's durable swap path (HakoniwaTileHeaderInput ->
// HakoniwaController), so the HITL run validates the SAME wiring the mainline shell reuses.
//
// NO auto-bootstrap (menu-spawned only), Python-FREE, and it does NOT touch
// ReplayPanelsHarness.AutoBootstrapEnabled — so it never collides with the single-Play-owner
// (#11, findings 0003 §8). The tiles carry PRODUCTION-STABLE ids so a later slice can swap the
// demo content for real chart/panel widgets without changing the persistence contract.
//
// The arithmetic + reorder + persistence gate is HakoniwaProbe (AFK, authoritative); this
// harness is the human-feel half (the header-drag gesture + canvas follow), not a headless assert.

using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.InputSystem.UI;

public class HakoniwaHitlHarness : MonoBehaviour
{
    static readonly Color BG_COLOR     = new Color(0.07f, 0.07f, 0.09f, 1f);
    static readonly Color ROOT_COLOR   = new Color(0.12f, 0.12f, 0.15f, 1f);
    static readonly Color TILE_COLOR   = new Color(0.16f, 0.18f, 0.22f, 1f);
    static readonly Color HEADER_COLOR = new Color(0.27f, 0.30f, 0.38f, 1f);
    static readonly Color HUD_BG       = new Color(0.0f, 0.0f, 0.0f, 0.6f);
    static readonly Color TEXT_COLOR   = new Color(0.92f, 0.92f, 0.94f, 1f);

    const float ROOT_W = 1100f;
    const float ROOT_H = 680f;
    const float HEADER_H = 26f;

    string SavePath => Path.Combine(Application.persistentDataPath, "hakoniwa_hitl.json");

    InfiniteCanvasController _canvas;
    HakoniwaController _hakoniwa;
    RectTransform _root;
    Text _readout;
    Font _font;

    void Start()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        EnsureEventSystem();

        // Canvas (ScreenSpaceOverlay).
        var canvasGo = new GameObject("HakoniwaHitlCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        // Viewport: fills the screen, raycast-target background, durable pan/zoom input surface.
        var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(InfiniteCanvasInputSurface));
        viewportGo.transform.SetParent(canvasGo.transform, false);
        var viewport = viewportGo.GetComponent<RectTransform>();
        viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero; viewport.offsetMax = Vector2.zero;
        viewport.pivot = new Vector2(0.5f, 0.5f);
        viewportGo.GetComponent<Image>().color = BG_COLOR;   // opaque -> raycast target for pan/zoom

        // Content: pan/zoom node, anchored + pivoted at the viewport centre (controller contract).
        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(viewport, false);
        var content = contentGo.GetComponent<RectTransform>();
        content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 0.5f);
        content.sizeDelta = Vector2.zero;

        _canvas = new InfiniteCanvasController(content);
        viewportGo.GetComponent<InfiniteCanvasInputSurface>().Initialize(_canvas, viewport);

        // HakoniwaRoot: a fixed-position, fixed-size box on the canvas. Tiles span normalized
        // cells of THIS rect, so the controller is resolution-independent.
        var rootGo = new GameObject("HakoniwaRoot", typeof(RectTransform), typeof(Image));
        rootGo.transform.SetParent(content, false);
        _root = rootGo.GetComponent<RectTransform>();
        _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0.5f, 0.5f);
        _root.anchoredPosition = Vector2.zero;
        _root.sizeDelta = new Vector2(ROOT_W, ROOT_H);
        var rootImg = rootGo.GetComponent<Image>();
        rootImg.color = ROOT_COLOR;
        rootImg.raycastTarget = false;   // gaps between tiles fall through to canvas pan

        // Build the 5 tiles + headers, then wire the controller (which lays them into cells).
        var tiles = new System.Collections.Generic.Dictionary<string, RectTransform>();
        var headers = new System.Collections.Generic.Dictionary<string, HakoniwaTileHeaderInput>();
        foreach (var id in HakoniwaController.DEFAULT_ORDER)
        {
            BuildTile(_root, id, out RectTransform tileRt, out HakoniwaTileHeaderInput header);
            tiles[id] = tileRt;
            headers[id] = header;
        }

        _hakoniwa = new HakoniwaController(_root, tiles, HakoniwaController.DEFAULT_ORDER);
        foreach (var kv in headers) kv.Value.Initialize(_hakoniwa, _root, kv.Key);

        BuildHud(canvasGo.transform);

        Debug.Log("[HAKONIWA HITL] ready: drag a TILE HEADER onto another tile to swap; drag the " +
                  "background to pan, wheel to zoom. The Hakoniwa follows; the HUD bar is screen-fixed.");
    }

    void Update()
    {
        if (_hakoniwa == null || _canvas == null || _readout == null) return;
        CanvasView v = _canvas.CaptureView();
        _readout.text = $"order = [{string.Join(", ", _hakoniwa.Order)}]   zoom = {v.zoom:0.00}   " +
                        "[header-drag=swap  bg-drag=pan  wheel=zoom]";
    }

    // ---- tile construction ----

    // A tile = a body Image (NOT a raycast target, so body drags pan the canvas) + a header
    // bar at the top (raycast target + HakoniwaTileHeaderInput, so header drags swap) + a label.
    void BuildTile(RectTransform root, string id, out RectTransform tileRt, out HakoniwaTileHeaderInput header)
    {
        var tileGo = new GameObject("Tile_" + id, typeof(RectTransform), typeof(Image));
        tileGo.transform.SetParent(root, false);
        tileRt = tileGo.GetComponent<RectTransform>();
        // anchors are owned by HakoniwaController.Rebuild; just inset a hair so tile borders read.
        var tileImg = tileGo.GetComponent<Image>();
        tileImg.color = TILE_COLOR;
        tileImg.raycastTarget = false;

        // header bar pinned to the top of the tile.
        var headerGo = new GameObject("Header", typeof(RectTransform), typeof(Image), typeof(HakoniwaTileHeaderInput));
        headerGo.transform.SetParent(tileRt, false);
        var hRt = headerGo.GetComponent<RectTransform>();
        hRt.anchorMin = new Vector2(0f, 1f); hRt.anchorMax = new Vector2(1f, 1f); hRt.pivot = new Vector2(0.5f, 1f);
        hRt.offsetMin = new Vector2(2f, -HEADER_H); hRt.offsetMax = new Vector2(-2f, -2f);
        headerGo.GetComponent<Image>().color = HEADER_COLOR;   // opaque -> raycast target for the drag
        header = headerGo.GetComponent<HakoniwaTileHeaderInput>();

        var ht = AddText(hRt, id, TextAnchor.MiddleLeft);
        ht.rectTransform.anchorMin = Vector2.zero; ht.rectTransform.anchorMax = Vector2.one;
        ht.rectTransform.offsetMin = new Vector2(8f, 0f); ht.rectTransform.offsetMax = new Vector2(-8f, 0f);
        ht.raycastTarget = false;

        // body label (under the header).
        var bt = AddText(tileRt, id.ToUpperInvariant(), TextAnchor.MiddleCenter);
        bt.fontSize = 18;
        bt.rectTransform.anchorMin = Vector2.zero; bt.rectTransform.anchorMax = Vector2.one;
        bt.rectTransform.offsetMin = new Vector2(8f, 8f); bt.rectTransform.offsetMax = new Vector2(-8f, -HEADER_H - 4f);
        bt.raycastTarget = false;
    }

    // ---- HUD + EventSystem (mirror InfiniteCanvasHitlHarness) ----

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

        _readout = AddText(bar, "order = …", TextAnchor.MiddleLeft);
        _readout.rectTransform.anchorMin = Vector2.zero; _readout.rectTransform.anchorMax = Vector2.one;
        _readout.rectTransform.offsetMin = new Vector2(12f, 0f); _readout.rectTransform.offsetMax = new Vector2(-360f, 0f);

        AddButton(bar, "Reset View", 1f, () => _canvas.ApplyView(CanvasView.Identity()));
        AddButton(bar, "Save",      120f, SaveLayout);
        AddButton(bar, "Load",      230f, LoadLayout);
    }

    void AddButton(RectTransform bar, string label, float rightInset, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(bar, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0.5f); rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(1f, 0.5f);
        rt.sizeDelta = new Vector2(105f, 30f);
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

    // ---- Save/Load: real LayoutStore round-trip of the tile ORDER (slot), findings 0007 §3 ----

    void SaveLayout()
    {
        LayoutDocument doc = _hakoniwa.Capture();        // panels with slot = tile order
        doc.canvasView = _canvas.CaptureView();          // also persist the pan/zoom (additive, #13)
        LayoutStore.Save(doc, SavePath);
        Debug.Log($"[HAKONIWA HITL] saved order [{string.Join(", ", _hakoniwa.Order)}] -> {SavePath}");
    }

    void LoadLayout()
    {
        LayoutDocument doc = LayoutStore.Load(SavePath);  // sanitized (default if absent/corrupt)
        _hakoniwa.Apply(doc);
        _canvas.ApplyView(doc.canvasView);
        Debug.Log($"[HAKONIWA HITL] loaded order [{string.Join(", ", _hakoniwa.Order)}] <- {SavePath}");
    }
}
