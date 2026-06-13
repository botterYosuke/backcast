// StrategyEditorHitlHarness.cs — issue #16 "Strategy Editor" (THROWAWAY HITL gate)
//
// THROWAWAY turnkey playmode widget the OWNER spawns once (Tools > Backcast > Strategy Editor
// HITL) to FEEL the code buffer: a strategy_editor floating window with a REAL editable Python
// buffer (the #16 content #15 left as a placeholder). TYPE to edit and watch syntax highlighting;
// ⌘/Ctrl+Z / ⌘/Ctrl+Shift+Z (and Ctrl+Y on Windows) undo/redo; Open re-initialises a working copy
// from the repo fixture; Save writes the buffer to that working copy (atomic); Save/Load Layout
// round-trips the OPEN FILE through the #12 schema's additive strategyEditors dimension.
//
// It edits a WORKING COPY under persistentDataPath (NEVER the repo fixture, findings 0010 §4) and
// re-initialises it from the fixture each Open so a prior HITL edit can't skew the result. It
// reuses #13's pan/zoom and #15's move/raise + FloatingWindowController, plus #16's
// StrategyEditorContentBuilder for the body. NO auto-bootstrap (menu-spawned only), Python-FREE.
//
// AFK is authoritative for the highlighter/history/file/provider/layout/restore (findings 0010
// §9); THIS harness is the owner's check of the editing feel, IME, and live highlighting.

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.InputSystem.UI;

public class StrategyEditorHitlHarness : MonoBehaviour
{
    static readonly Color BG_COLOR    = new Color(0.07f, 0.07f, 0.09f, 1f);
    static readonly Color BODY_COLOR  = new Color(0.13f, 0.14f, 0.17f, 0.98f);
    static readonly Color TITLE_COLOR = new Color(0.24f, 0.27f, 0.34f, 1f);
    static readonly Color HUD_BG      = new Color(0.0f, 0.0f, 0.0f, 0.6f);
    static readonly Color TEXT_COLOR  = new Color(0.92f, 0.92f, 0.94f, 1f);

    const float TITLE_H = 28f;
    const string WINDOW_ID = "strategy_editor:region_001";

    string WorkDir => Path.Combine(Application.persistentDataPath, "strategy_editor_hitl");
    string WorkFile => Path.Combine(WorkDir, "spike_buy_sell.py");
    string LayoutPath => Path.Combine(WorkDir, "layout.json");

    InfiniteCanvasController _canvas;
    FloatingWindowController _windows;
    RectTransform _viewport;
    RectTransform _layer;
    StrategyProviderRegistry _registry;
    readonly Dictionary<string, StrategyEditorView> _editors = new Dictionary<string, StrategyEditorView>();
    Text _readout;
    Font _font;

    void Start()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        _registry = new StrategyProviderRegistry();

        InitWorkingCopy();
        EnsureEventSystem();

        var canvasGo = new GameObject("StrategyEditorHitlCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(InfiniteCanvasInputSurface));
        viewportGo.transform.SetParent(canvasGo.transform, false);
        _viewport = viewportGo.GetComponent<RectTransform>();
        _viewport.anchorMin = Vector2.zero; _viewport.anchorMax = Vector2.one;
        _viewport.offsetMin = Vector2.zero; _viewport.offsetMax = Vector2.zero;
        _viewport.pivot = new Vector2(0.5f, 0.5f);
        viewportGo.GetComponent<Image>().color = BG_COLOR;

        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(_viewport, false);
        var content = contentGo.GetComponent<RectTransform>();
        content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 0.5f);
        content.sizeDelta = Vector2.zero;

        _canvas = new InfiniteCanvasController(content);
        viewportGo.GetComponent<InfiniteCanvasInputSurface>().Initialize(_canvas, _viewport);

        var layerGo = new GameObject("FloatingWindowLayer", typeof(RectTransform));
        layerGo.transform.SetParent(content, false);
        _layer = layerGo.GetComponent<RectTransform>();
        _layer.anchorMin = _layer.anchorMax = _layer.pivot = new Vector2(0.5f, 0.5f);
        _layer.anchoredPosition = Vector2.zero;
        _layer.sizeDelta = Vector2.zero;

        _windows = new FloatingWindowController(_layer, FloatingWindowCatalog.Default(), BuildWindow);
        _windows.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, WINDOW_ID, -300f, 220f, 620f, 460f, true);

        // Open the working copy in the freshly built editor.
        if (_editors.TryGetValue(WINDOW_ID, out var view)) view.Open(WorkFile);

        BuildHud(canvasGo.transform);

        Debug.Log("[STRATEGY EDITOR HITL] ready: TYPE to edit (syntax highlight live); " +
                  "Cmd/Ctrl+Z undo, Cmd/Ctrl+Shift+Z (or Ctrl+Y) redo; IME for Japanese in comments. " +
                  "Open re-inits the working copy from the fixture; Save writes it (atomic). " +
                  "Save/Load Layout round-trips the open file path. Working copy: " + WorkFile);
    }

    void Update()
    {
        if (_readout == null) return;
        string path = "(none)";
        bool dirty = false, supplyable = false;
        if (_editors.TryGetValue(WINDOW_ID, out var view) && view.Document != null)
        {
            path = string.IsNullOrEmpty(view.Document.CurrentPath) ? "(unbound)" : Path.GetFileName(view.Document.CurrentPath);
            dirty = view.Document.IsDirty;
            supplyable = view.Document.TryGetStrategyFile(out _);
        }
        _readout.text = $"file = {path}   dirty = {dirty}   supplyable = {supplyable}   " +
                        "[type=edit  Cmd/Ctrl+Z/⇧Z=undo/redo]";
    }

    // ---- window factory: title bar (move/raise) + body hosting the real editor content ----
    RectTransform BuildWindow(FloatingWindowSpec spec, string id)
    {
        var rootGo = new GameObject("Window_" + id, typeof(RectTransform), typeof(Image));
        var root = rootGo.GetComponent<RectTransform>();
        rootGo.GetComponent<Image>().color = BODY_COLOR;   // raycast target: editor body is interactive

        var titleGo = new GameObject("TitleBar", typeof(RectTransform), typeof(Image), typeof(FloatingWindowTitleInput));
        titleGo.transform.SetParent(root, false);
        var titleRt = titleGo.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0f, 1f); titleRt.anchorMax = new Vector2(1f, 1f); titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.offsetMin = new Vector2(0f, -TITLE_H); titleRt.offsetMax = new Vector2(0f, 0f);
        titleGo.GetComponent<Image>().color = TITLE_COLOR;
        titleGo.GetComponent<FloatingWindowTitleInput>().Initialize(_windows, _canvas, _viewport, id);

        var titleText = AddText(titleRt, spec.title, TextAnchor.MiddleLeft);
        titleText.rectTransform.anchorMin = Vector2.zero; titleText.rectTransform.anchorMax = Vector2.one;
        titleText.rectTransform.offsetMin = new Vector2(10f, 0f); titleText.rectTransform.offsetMax = new Vector2(-10f, 0f);
        titleText.raycastTarget = false;

        // body region under the title bar hosts the editor content.
        var bodyGo = new GameObject("Body", typeof(RectTransform));
        bodyGo.transform.SetParent(root, false);
        var body = bodyGo.GetComponent<RectTransform>();
        body.anchorMin = Vector2.zero; body.anchorMax = Vector2.one;
        body.offsetMin = new Vector2(4f, 4f); body.offsetMax = new Vector2(-4f, -TITLE_H - 2f);

        if (spec.kind == FloatingWindowCatalog.KIND_STRATEGY_EDITOR)
        {
            var view = StrategyEditorContentBuilder.Build(body, id, _registry, font: _font);
            if (view != null) _editors[id] = view;
        }
        return root;
    }

    // ---- HUD ----
    void BuildHud(Transform canvas)
    {
        var barGo = new GameObject("HudBar", typeof(RectTransform), typeof(Image));
        barGo.transform.SetParent(canvas, false);
        var bar = barGo.GetComponent<RectTransform>();
        bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = new Vector2(1f, 1f); bar.pivot = new Vector2(0.5f, 1f);
        bar.offsetMin = new Vector2(0f, -40f); bar.offsetMax = new Vector2(0f, 0f);
        barGo.GetComponent<Image>().color = HUD_BG;

        _readout = AddText(bar, "file = …", TextAnchor.MiddleLeft);
        _readout.rectTransform.anchorMin = Vector2.zero; _readout.rectTransform.anchorMax = Vector2.one;
        _readout.rectTransform.offsetMin = new Vector2(12f, 0f); _readout.rectTransform.offsetMax = new Vector2(-560f, 0f);

        AddButton(bar, "Open",        1f, () => { if (_editors.TryGetValue(WINDOW_ID, out var v)) { InitWorkingCopy(force: true); v.Open(WorkFile); } });
        AddButton(bar, "Save",      116f, () => { if (_editors.TryGetValue(WINDOW_ID, out var v)) Debug.Log("[STRATEGY EDITOR HITL] Save -> " + v.Save()); });
        AddButton(bar, "Undo",      231f, () => { if (_editors.TryGetValue(WINDOW_ID, out var v)) v.DoUndo(); });
        AddButton(bar, "Redo",      346f, () => { if (_editors.TryGetValue(WINDOW_ID, out var v)) v.DoRedo(); });
        AddButton(bar, "Save Lyt",  461f, SaveLayout);
        AddButton(bar, "Load Lyt",  576f, LoadLayout);
    }

    void AddButton(RectTransform bar, string label, float rightInset, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(bar, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0.5f); rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(1f, 0.5f);
        rt.sizeDelta = new Vector2(108f, 30f);
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
        t.font = _font; t.fontSize = 13; t.color = TEXT_COLOR; t.alignment = anchor;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        t.text = text;
        return t;
    }

    // ---- layout Save/Load: floatingWindows (+ canvasView) + the #16 strategyEditors dimension ----
    void SaveLayout()
    {
        LayoutDocument doc = _windows.Capture();
        doc.canvasView = _canvas.CaptureView();
        doc.strategyEditors = new List<StrategyEditorState>();
        foreach (var kv in _editors)
        {
            var st = kv.Value.CaptureState();
            if (st != null) doc.strategyEditors.Add(st);
        }
        LayoutStore.Save(doc, LayoutPath);
        Debug.Log($"[STRATEGY EDITOR HITL] saved layout ({doc.strategyEditors.Count} editor states) -> {LayoutPath}");
    }

    void LoadLayout()
    {
        LayoutDocument doc = LayoutStore.Load(LayoutPath);
        _windows.Apply(doc);
        _canvas.ApplyView(doc.canvasView);
        foreach (var kv in _editors)
            kv.Value.RestoreFrom(doc.FindStrategyEditor(kv.Key));
        Debug.Log($"[STRATEGY EDITOR HITL] loaded layout <- {LayoutPath}");
    }

    // ---- working copy: copy the repo fixture under persistentDataPath (never edit the repo). ----
    void InitWorkingCopy(bool force = false)
    {
        Directory.CreateDirectory(WorkDir);
        if (File.Exists(WorkFile) && !force) return;

        string repoRoot = Directory.GetParent(Application.dataPath)?.FullName;
        string fixture = repoRoot == null ? null
            : Path.Combine(repoRoot, "python", "spike", "fixtures", "strategies", "spike_buy_sell.py");
        if (fixture != null && File.Exists(fixture)) File.Copy(fixture, WorkFile, overwrite: true);
        else File.WriteAllText(WorkFile, FallbackStrategy());
        Debug.Log("[STRATEGY EDITOR HITL] working copy initialised: " + WorkFile);
    }

    static string FallbackStrategy()
    {
        return "# fallback strategy (repo fixture not found)\n" +
               "SCENARIO = {\n" +
               "    \"instrument\": \"DEMO.TEST\",\n" +
               "}\n\n" +
               "@strategy\n" +
               "class BuySell:\n" +
               "    def on_bar(self, bar):\n" +
               "        # 日本語コメントの IME 確認\n" +
               "        if bar.close > 1_000.5:\n" +
               "            self.sell()\n" +
               "        else:\n" +
               "            self.buy()\n";
    }

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
}
