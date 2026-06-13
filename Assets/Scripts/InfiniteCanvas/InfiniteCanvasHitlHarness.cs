// InfiniteCanvasHitlHarness.cs — issue #13 "infinite canvas" (THROWAWAY HITL gate)
//
// THROWAWAY turnkey playmode widget the OWNER spawns once (Tools > Backcast > Infinite
// Canvas HITL) to FEEL real pan/zoom: drag the grid to pan, wheel to zoom at the cursor,
// watch a Content-child demo panel follow while the screen-fixed HUD/buttons stay put.
// It owns ONLY the UI scaffolding, the readout, Reset, and Save/Load — it does NOT
// duplicate input handling: the production durable path
//   InputSystemUIInputModule -> InfiniteCanvasInputSurface -> InfiniteCanvasController
//                            -> CanvasViewMath -> Content
// is what it exercises, so the HITL run validates the SAME wiring #14/#15 reuse.
//
// NO auto-bootstrap (menu-spawned only) and Python-FREE, so it never collides with the
// single-Play-owner (#11 ReplayPanelsHarness, findings 0003 §8) — and it does NOT touch
// ReplayPanelsHarness.AutoBootstrapEnabled. Deleted when the mainline scene/DI lands (#14).
//
// The arithmetic gate is InfiniteCanvasProbe (AFK, authoritative); this harness is the
// human-feel half (AC1 input wiring), not a headless assert.

using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.InputSystem.UI;

public class InfiniteCanvasHitlHarness : MonoBehaviour
{
    static readonly Color BG_COLOR    = new Color(0.07f, 0.07f, 0.09f, 1f);
    static readonly Color GRID_COLOR  = new Color(0.20f, 0.20f, 0.26f, 1f);
    static readonly Color AXIS_COLOR  = new Color(0.45f, 0.55f, 0.75f, 1f);
    static readonly Color DEMO_COLOR  = new Color(0.18f, 0.42f, 0.55f, 1f);
    static readonly Color HUD_BG      = new Color(0.0f, 0.0f, 0.0f, 0.6f);
    static readonly Color TEXT_COLOR  = new Color(0.92f, 0.92f, 0.94f, 1f);

    const float GRID_EXTENT = 2000f;   // grid drawn over [-extent, +extent] logical
    const float GRID_STEP   = 200f;

    string SavePath => Path.Combine(Application.persistentDataPath, "infinite_canvas_hitl.json");

    InfiniteCanvasController _controller;
    RectTransform _content;
    Text _readout;
    Font _font;

    void Start()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        EnsureEventSystem();

        // Canvas (ScreenSpaceOverlay).
        var canvasGo = new GameObject("InfiniteCanvasHitlCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        // Viewport: fills the screen, pivot centre, carries the grid Image (raycast target)
        // and the durable input surface. RectMask2D so panned content clips at the edges.
        var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(InfiniteCanvasInputSurface));
        viewportGo.transform.SetParent(canvasGo.transform, false);
        var viewport = viewportGo.GetComponent<RectTransform>();
        viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero; viewport.offsetMax = Vector2.zero;
        viewport.pivot = new Vector2(0.5f, 0.5f);
        var vbg = viewportGo.GetComponent<Image>();
        vbg.color = BG_COLOR;            // opaque -> a raycast target so drag/scroll route here

        // Content: pan/zoom node, anchored + pivoted at the viewport centre (controller contract).
        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(viewport, false);
        _content = contentGo.GetComponent<RectTransform>();
        _content.anchorMin = _content.anchorMax = _content.pivot = new Vector2(0.5f, 0.5f);
        _content.sizeDelta = Vector2.zero;

        BuildGrid(_content);
        BuildDemoWidget(_content, new Vector2(300f, 120f), "DEMO PANEL\n(on canvas — follows pan/zoom)");
        BuildDemoWidget(_content, new Vector2(-650f, -300f), "DEMO PANEL 2");

        _controller = new InfiniteCanvasController(_content);
        viewportGo.GetComponent<InfiniteCanvasInputSurface>().Initialize(_controller, viewport);

        BuildHud(canvasGo.transform);

        Debug.Log("[INFINITE CANVAS HITL] ready: drag to pan, wheel to zoom at cursor. " +
                  "Demo panels are on the canvas (follow); the HUD bar is screen-fixed (immobile).");
    }

    void Update()
    {
        if (_controller == null || _readout == null) return;
        CanvasView v = _controller.CaptureView();
        _readout.text = $"pan = ({v.panX:0.0}, {v.panY:0.0})   zoom = {v.zoom:0.000}   " +
                        "[drag=pan  wheel=zoom@cursor]";
    }

    // ---- UI construction ----

    void EnsureEventSystem()
    {
        var es = FindAnyObjectByType<EventSystem>();
        if (es == null)
        {
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(transform, false);
            return;
        }
        // A scene EventSystem may carry the legacy StandaloneInputModule. Under
        // activeInputHandler=1 (new Input System only) that module is not merely inert: its
        // ShouldActivateModule reads UnityEngine.Input, which THROWS every frame and aborts
        // module selection BEFORE any new module activates -> drag/scroll dead + console spam.
        // Disable it, then guarantee an ENABLED InputSystemUIInputModule (GetComponent also
        // returns a disabled instance, so re-enable rather than assume present==working).
        var legacy = es.GetComponent<StandaloneInputModule>();
        if (legacy != null) legacy.enabled = false;
        var module = es.GetComponent<InputSystemUIInputModule>();
        if (module == null) module = es.gameObject.AddComponent<InputSystemUIInputModule>();
        module.enabled = true;
    }

    void BuildGrid(RectTransform content)
    {
        for (float x = -GRID_EXTENT; x <= GRID_EXTENT; x += GRID_STEP)
        {
            bool axis = Mathf.Abs(x) < 0.5f;
            AddLine(content, new Vector2(x, 0f), new Vector2(axis ? 3f : 1f, 2f * GRID_EXTENT),
                    axis ? AXIS_COLOR : GRID_COLOR);
        }
        for (float y = -GRID_EXTENT; y <= GRID_EXTENT; y += GRID_STEP)
        {
            bool axis = Mathf.Abs(y) < 0.5f;
            AddLine(content, new Vector2(0f, y), new Vector2(2f * GRID_EXTENT, axis ? 3f : 1f),
                    axis ? AXIS_COLOR : GRID_COLOR);
        }
    }

    void AddLine(RectTransform parent, Vector2 center, Vector2 size, Color c)
    {
        var go = new GameObject("line", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = center;
        rt.sizeDelta = size;
        var img = go.GetComponent<Image>();
        img.color = c;
        img.raycastTarget = false;   // lines must not eat drag/scroll events
    }

    void BuildDemoWidget(RectTransform content, Vector2 logicalPos, string label)
    {
        var go = new GameObject("DemoWidget", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(content, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = logicalPos;
        rt.sizeDelta = new Vector2(320f, 160f);
        var img = go.GetComponent<Image>();
        img.color = DEMO_COLOR;
        img.raycastTarget = false;   // let drags pass through to the canvas surface

        var t = AddText(rt, label, TextAnchor.MiddleCenter);
        t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
        t.rectTransform.offsetMin = new Vector2(8f, 8f); t.rectTransform.offsetMax = new Vector2(-8f, -8f);
        t.raycastTarget = false;
    }

    void BuildHud(Transform canvas)
    {
        // Screen-fixed top bar (NOT a child of Content) — proves chrome stays put under pan/zoom.
        var barGo = new GameObject("HudBar", typeof(RectTransform), typeof(Image));
        barGo.transform.SetParent(canvas, false);
        var bar = barGo.GetComponent<RectTransform>();
        bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = new Vector2(1f, 1f); bar.pivot = new Vector2(0.5f, 1f);
        bar.offsetMin = new Vector2(0f, -40f); bar.offsetMax = new Vector2(0f, 0f);
        barGo.GetComponent<Image>().color = HUD_BG;

        _readout = AddText((RectTransform)bar, "pan = …", TextAnchor.MiddleLeft);
        _readout.rectTransform.anchorMin = Vector2.zero; _readout.rectTransform.anchorMax = Vector2.one;
        _readout.rectTransform.offsetMin = new Vector2(12f, 0f); _readout.rectTransform.offsetMax = new Vector2(-360f, 0f);

        AddButton(bar, "Reset View", 1f, () => _controller.ApplyView(CanvasView.Identity()));
        AddButton(bar, "Save",      120f, SaveView);
        AddButton(bar, "Load",      230f, LoadView);
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

    // ---- Save/Load: real LayoutStore round-trip of the canvas view (panels = default) ----

    void SaveView()
    {
        var doc = LayoutDocument.Default();
        doc.canvasView = _controller.CaptureView();
        LayoutStore.Save(doc, SavePath);
        Debug.Log($"[INFINITE CANVAS HITL] saved view {doc.canvasView.panX:0.0},{doc.canvasView.panY:0.0}," +
                  $"{doc.canvasView.zoom:0.000} -> {SavePath}");
    }

    void LoadView()
    {
        LayoutDocument doc = LayoutStore.Load(SavePath);   // sanitized (identity if absent/corrupt)
        _controller.ApplyView(doc.canvasView);
        Debug.Log($"[INFINITE CANVAS HITL] loaded view {doc.canvasView.panX:0.0},{doc.canvasView.panY:0.0}," +
                  $"{doc.canvasView.zoom:0.000} <- {SavePath}");
    }
}
