// LayoutDocument.cs — issue #12 "Replay layout" (DURABLE tier, schema)
//
// The Unity-OWNED, versioned layout-persistence schema (ADR-0003: capability
// parity, NOT byte-compat with the dead Bevy sidecar). This is the durable POCO
// the whole slice round-trips: LayoutStore serializes it to a JSON sidecar,
// LayoutBinder captures it from / applies it to the live uGUI. See
// docs/findings/0004-replay-layout.md for the locked design.
//
// SHAPE (findings §3):
//   LayoutDocument { int version; List<PanelLayout> panels }   // chart is id="chart"
//   PanelLayout    { string id; int slot; bool visible; LayoutRect rect }
//   LayoutRect     { float minX, minY, maxX, maxY }            // NORMALIZED 0..1
//
// DELIBERATELY UnityEngine-FREE (no RectTransform / pixels): a plain [Serializable]
// POCO so the AFK probe round-trips it headless and the binder owns the
// RectTransform <-> normalized-rect conversion (findings §4). `class` (not struct)
// for forward extensibility. JsonUtility binds these by VERBATIM field name and
// serializes List<T> of [Serializable] T.
//
// LayoutRect SEMANTICS (findings §3, owner-locked): the rect is the panel's
// NORMALIZED DISPLAY rect against its parent region — it is NOT the raw Unity
// anchor value. The current UI carries anchors PLUS pixel offsets, so anchors
// alone cannot express the displayed rect; LayoutBinder does the two-way mapping.
//
// `slot` is the panel's LOGICAL ordering only; it is NOT identified with a future
// floating-window zOrder (a separate field gets added when that lands, findings §3).

using System;
using System.Collections.Generic;

[Serializable]
public class LayoutRect
{
    public float minX;
    public float minY;
    public float maxX;
    public float maxY;

    public LayoutRect() { }

    public LayoutRect(float minX, float minY, float maxX, float maxY)
    {
        this.minX = minX;
        this.minY = minY;
        this.maxX = maxX;
        this.maxY = maxY;
    }

    public LayoutRect Clone() => new LayoutRect(minX, minY, maxX, maxY);

    public static bool Approx(LayoutRect a, LayoutRect b, float eps)
    {
        if (a == null || b == null) return a == b;
        return Math.Abs(a.minX - b.minX) <= eps
            && Math.Abs(a.minY - b.minY) <= eps
            && Math.Abs(a.maxX - b.maxX) <= eps
            && Math.Abs(a.maxY - b.maxY) <= eps;
    }
}

// CanvasView — the infinite-canvas pan/zoom state (issue #13), an ADDITIVE capability-
// surface item on the layout schema (findings 0006 §3; ADR-0003 capability parity, the
// "canvas pan/zoom" slot findings 0004 §10 parked for the shell slices). NOT a version
// bump: an additive, identity-defaulting field is exactly what #12's forward-evolution
// tolerance (findings 0004 §6) was designed for, so CURRENT_VERSION stays 1.
//
// SEMANTICS (findings 0006 §2, owner-locked): panX/panY are the canvas LOGICAL point at
// the Viewport CENTRE (resolution-independent — NOT screen pixels, NOT zoom-scaled px),
// zoom is a uniform scalar (localScale = (zoom,zoom,1)). Y is up-positive (uGUI local).
// IDENTITY = (0, 0, 1) = no pan, 100%. zoom defaults to 1f so a freshly-minted CanvasView
// is identity even before Sanitize; LayoutStore.Sanitize() is the authoritative normalizer
// (null/missing -> identity, non-finite pan -> 0/axis, non-finite or <=0 zoom -> 1, then
// clamp [0.2,5.0]). The binder/math never sanitize — that lives at the persistence boundary.
[Serializable]
public class CanvasView
{
    public const float MIN_ZOOM = 0.2f;
    public const float MAX_ZOOM = 5.0f;

    public float panX;
    public float panY;
    public float zoom = 1f;   // identity default (also re-asserted by Sanitize)

    public CanvasView() { }

    public CanvasView(float panX, float panY, float zoom)
    {
        this.panX = panX;
        this.panY = panY;
        this.zoom = zoom;
    }

    public static CanvasView Identity() => new CanvasView(0f, 0f, 1f);

    public CanvasView Clone() => new CanvasView(panX, panY, zoom);

    public static bool Approx(CanvasView a, CanvasView b, float eps)
    {
        if (a == null || b == null) return a == b;
        return Math.Abs(a.panX - b.panX) <= eps
            && Math.Abs(a.panY - b.panY) <= eps
            && Math.Abs(a.zoom - b.zoom) <= eps;
    }
}

[Serializable]
public class PanelLayout
{
    public string id;
    public int slot;
    public bool visible;
    public LayoutRect rect;

    public PanelLayout() { }

    public PanelLayout(string id, int slot, bool visible, LayoutRect rect)
    {
        this.id = id;
        this.slot = slot;
        this.visible = visible;
        this.rect = rect;
    }

    public PanelLayout Clone() => new PanelLayout(id, slot, visible, rect?.Clone());
}

// FloatingWindowLayout — a free-floating window's persisted state (issue #15), the ADDITIVE
// capability-surface item ADR-0003 reserved as "floating window の rect / z-order". Unlike a
// PanelLayout/Hakoniwa tile (a 0..1 normalized display rect inside a bounded parent), a
// floating window lives on the UNBOUNDED infinite canvas, so it persists ABSOLUTE CANVAS-
// LOGICAL coordinates (findings 0008 §3, owner-locked):
//   x,y  = the window's TOP-LEFT-pivot anchoredPosition in FloatingWindowLayer-LOCAL space
//          (x right-positive, y up-positive). Equals Content canvas-logical space while the layer
//          is centred; under pan the layer parallax-shifts off Content (the foreground depth cue),
//          so x,y stay layer-local and SpawnAnchorTopLeft compensates by the live layer offset.
//   w,h  = size in canvas-logical px (w,h > 0; LayoutStore drops an entry with non-finite/<=0).
//   zOrder = front/back order, 0 = BACKMOST. Persisted VERBATIM (never normalized at load, so a
//          hand-authored non-contiguous z survives the round-trip); the restore controller
//          stable-normalizes it to a contiguous sibling index at Apply time.
//   kind = the spec key the catalog re-spawns from (e.g. "strategy_editor"); kept DISTINCT from
//          `id` so multiple instances share a kind (id="strategy_editor:region_001"). An unknown
//          kind is PRESERVED here (LayoutStore never drops it); the controller skips its spawn.
//   id   = unique within the document (LayoutStore keeps the FIRST on a duplicate, drops the rest).
// zOrder is the field findings 0004 §3 reserved as separate from PanelLayout.slot — they are NOT
// the same dimension. UnityEngine-free POCO (JsonUtility binds by verbatim field name).
[Serializable]
public class FloatingWindowLayout
{
    public string id;
    public string kind;
    public float x;
    public float y;
    public float w;
    public float h;
    public int zOrder;
    public bool visible;
    // #104 (ADR-0019 / findings 0082 §1, §11): persistent window-group membership. A nullable GUID-shaped
    // string ("grp_<hex32>") shared by every member of the same group; null = singleton (no group). It is
    // the SOLE source of group truth — never re-derived from coordinates, never re-derived from edge
    // adjacency at read time. ADDITIVE schema field (ADR-0017 §6 "schema-add 0" is supersede only here, by
    // ONE field): an old sidecar lacking groupId loads as null on every window (forward-evolution
    // tolerance, findings 0008 §3); not a version bump. Spawn paths leave it null — attach happens ONLY at
    // the user's drag-release in SnapOnRelease (findings 0082 §10), never on programmatic Spawn / restore.
    public string groupId;

    // S7 #162 (ADR-0034 §7 / findings 0119 D-7): per-chart-window pan/zoom state. Null on non-chart
    // windows AND on chart windows whose state has never been captured (old sidecar / freshly spawned
    // → ResetView() default on restore). NOT persisted: cell_height_norm (autoscale-derived; recomputed
    // every OnPopulateMesh from visible price range).
    public ChartViewStateLayout chart_view_state;

    public FloatingWindowLayout() { }

    public FloatingWindowLayout(string id, string kind, float x, float y, float w, float h, int zOrder, bool visible)
        : this(id, kind, x, y, w, h, zOrder, visible, null) { }

    public FloatingWindowLayout(string id, string kind, float x, float y, float w, float h, int zOrder, bool visible, string groupId)
    {
        this.id = id;
        this.kind = kind;
        this.x = x;
        this.y = y;
        this.w = w;
        this.h = h;
        this.zOrder = zOrder;
        this.visible = visible;
        this.groupId = groupId;
    }

    public FloatingWindowLayout Clone()
    {
        var c = new FloatingWindowLayout(id, kind, x, y, w, h, zOrder, visible, groupId);
        if (chart_view_state != null) c.chart_view_state = chart_view_state.Clone();
        return c;
    }

    public static bool Approx(FloatingWindowLayout a, FloatingWindowLayout b, float eps)
    {
        if (a == null || b == null) return a == b;
        return a.id == b.id && a.kind == b.kind
            && Math.Abs(a.x - b.x) <= eps && Math.Abs(a.y - b.y) <= eps
            && Math.Abs(a.w - b.w) <= eps && Math.Abs(a.h - b.h) <= eps
            && a.zOrder == b.zOrder && a.visible == b.visible
            && a.groupId == b.groupId
            && ChartViewStateLayout.Approx(a.chart_view_state, b.chart_view_state, eps);
    }
}

// S7 #162 (findings 0119 D-7): persisted ChartViewState. Only the 3 fields that survive a reopen
// (translation_ms / cell_width_px / auto_scale) — cell_height_norm is recomputed every render from
// the visible price range. JsonUtility binds by exact field name (snake_case to match the disk
// schema example in the finding). Null means "no captured state" → ResetView() default on restore
// (legacy sidecar / freshly spawned chart).
[Serializable]
public class ChartViewStateLayout
{
    public long translation_ms;
    public float cell_width_px;
    public bool auto_scale;

    public ChartViewStateLayout() { }

    public ChartViewStateLayout(long translation_ms, float cell_width_px, bool auto_scale)
    {
        this.translation_ms = translation_ms;
        this.cell_width_px = cell_width_px;
        this.auto_scale = auto_scale;
    }

    public ChartViewStateLayout Clone() =>
        new ChartViewStateLayout(translation_ms, cell_width_px, auto_scale);

    public static bool Approx(ChartViewStateLayout a, ChartViewStateLayout b, float eps)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return a.translation_ms == b.translation_ms
            && Math.Abs(a.cell_width_px - b.cell_width_px) <= eps
            && a.auto_scale == b.auto_scale;
    }
}

// StrategyEditorState — a Strategy Editor window's persisted CONTENT state (issue #16), the
// ADDITIVE capability-surface item ADR-0003 decision 2 named as "(後で) Strategy Editor の
// 開いていたファイル等". A SEPARATE DIMENSION from floatingWindows (findings 0010 §7, owner-
// locked): FloatingWindowLayout stays kind-agnostic (it owns geometry/z/visibility), so the
// content-specific open-file does NOT pollute it. Matched to its window by `id`.
//   id       = the strategy_editor window id this state belongs to (e.g. "strategy_editor:region_001").
//   filePath = the canonical absolute .py the editor had open. PERSISTED VERBATIM — LayoutStore
//              does NOT verify existence or canonicalize; an orphan id or a missing path is KEPT
//              (forward-evolution tolerance) and the restore controller decides what to do.
// Only the PATH is persisted (findings 0010 §7) — never the unsaved buffer, dirty, history, caret,
// selection, or scroll. UnityEngine-free POCO (JsonUtility binds by verbatim field name).
[Serializable]
public class StrategyEditorState
{
    public string id;
    public string filePath;

    public StrategyEditorState() { }

    public StrategyEditorState(string id, string filePath)
    {
        this.id = id;
        this.filePath = filePath;
    }

    public StrategyEditorState Clone() => new StrategyEditorState(id, filePath);

    public static bool Equal(StrategyEditorState a, StrategyEditorState b)
    {
        if (a == null || b == null) return a == b;
        return a.id == b.id && a.filePath == b.filePath;
    }
}

// CellPosition — a notebook cell window's spatial position in CELL ORDER (issue #81, ADR-0013 /
// findings 0050). The cell-as-floating-window model has ONE `.py` (the notebook) but N windows; the
// `.py` cell order is the authoritative ordering, and these positions run PARALLEL to it
// (position[i] <-> cell[i]). NO id key: marimo's CellId is random + not written to the `.py`
// (ids.ts), so an id-keyed lookup breaks on cold-open; an order-parallel list survives append /
// delete / cold-open so windows don't jump (more faithful than marimo's own 3D positions). Only x,y
// are persisted — S1 has no resize (all cells share the default size) and z = cell order = spawn
// order (deterministic), so w/h/z are additive-reserved, NOT written now. Cells are EXCLUDED from
// floatingWindows (single source of truth — the id-vs-index duplication is root-caused away,
// findings 0050). UnityEngine-free POCO (JsonUtility binds by verbatim field name).
[Serializable]
public class CellPosition
{
    public float x;
    public float y;

    public CellPosition() { }
    public CellPosition(float x, float y) { this.x = x; this.y = y; }

    public CellPosition Clone() => new CellPosition(x, y);

    public static bool Approx(CellPosition a, CellPosition b, float eps)
    {
        if (a == null || b == null) return a == b;
        return Math.Abs(a.x - b.x) <= eps && Math.Abs(a.y - b.y) <= eps;
    }
}

[Serializable]
public class LayoutDocument
{
    // Bumped when the schema changes incompatibly. Load() treats <=0 / missing /
    // future versions per findings §6 (default-fallback vs best-effort).
    // S7 #162 (findings 0119 D-7): bumped 1 → 2 to carry FloatingWindowLayout.chart_view_state.
    // Older sidecars (version=1) load fine: chart_view_state stays null on every window → ChartView
    // ResetView() default applied on restore (legacy charts pop up at right-anchor + DEFAULT cell_width).
    public const int CURRENT_VERSION = 2;

    public int version;
    public List<PanelLayout> panels;

    // Infinite-canvas pan/zoom state (issue #13). ADDITIVE: an old #12 sidecar lacking
    // this field loads with panels intact and the view normalized to identity by
    // LayoutStore.Sanitize(). NOT identified with any panel's LayoutRect (separate
    // dimension, findings 0006 §3).
    public CanvasView canvasView;

    // Floating windows on the infinite canvas (issue #15). ADDITIVE: an old #12/#13 sidecar
    // lacking this field loads with panels/canvasView intact and floatingWindows normalized
    // to an EMPTY list by LayoutStore.Sanitize(). A separate dimension from panels (tiles are
    // grid slots; windows are free placement in canvas-logical space, findings 0008 §3).
    public List<FloatingWindowLayout> floatingWindows;

    // Strategy Editor open-file content state (issue #16). ADDITIVE: an old #12/#13/#15 sidecar
    // lacking this field loads with panels/canvasView/floatingWindows intact and strategyEditors
    // normalized to an EMPTY list by LayoutStore.Sanitize(). A separate dimension from
    // floatingWindows (geometry vs. content; findings 0010 §7). NOT a version bump (additive,
    // identity-defaulting — the same forward-evolution tolerance as canvasView/floatingWindows).
    public List<StrategyEditorState> strategyEditors;

    // Per-mode Hakoniwa tile order (issue #62, findings 0029). The NEW source of truth for Hakoniwa
    // placement: Replay and Live each carry their own tile order (TTWR HakoniwaLayoutProfiles parity).
    // ADDITIVE: an old single-`panels` doc (no hakoniwaProfiles) loads with panels intact and
    // HakoniwaLayoutProfiles.FromDocument SEEDS both modes from `panels` (forward-compat). `panels`
    // stays the active-mode MIRROR (back-compat for a pre-#62 reader) — read is always profiles-first.
    // NOT a version bump (additive, null-defaulting — same tolerance as canvasView/floatingWindows).
    // #63 grows HakoniwaProfile with cols/rows/box (the additive extension point, grill Q2).
    public HakoniwaLayoutProfiles hakoniwaProfiles;

    // Notebook cell window positions in CELL ORDER (issue #81, ADR-0013 / findings 0050). ADDITIVE:
    // an old pre-#81 sidecar lacking this field loads with everything else intact and cellPositions
    // normalized to an EMPTY list by LayoutStore.Sanitize (the coordinator then auto-cascades). A
    // SEPARATE dimension from floatingWindows (cells are excluded there — single source of truth).
    // NOT a version bump (additive, identity-defaulting — same tolerance as floatingWindows).
    public List<CellPosition> cellPositions;

    // Default ctor leaves version at the UNSET SENTINEL 0 (NOT CURRENT_VERSION) on
    // purpose: JsonUtility's treatment of a JSON-absent field (keep ctor value vs.
    // zero-fill) is version-dependent, so a version-less sidecar must land on 0 EITHER
    // WAY -> invalid -> default (findings §6b). Producers that mint a valid document
    // (Default(), LayoutBinder.Capture) stamp CURRENT_VERSION explicitly.
    public LayoutDocument()
    {
        version = 0;
        panels = new List<PanelLayout>();
    }

    // The canonical default layout = the REAL default UI's panel layout, as NORMALIZED
    // DISPLAY rects (findings §3: LayoutRect is a display rect, NOT a raw Unity anchor).
    // ReplayPanelsHarness builds its OUTER panels (the persisted-layout targets) at these
    // exact boxes with offsets ZERO (chart left [0..0.62], four panels stacked in the
    // right-column quarters): the chart's axis-label gutter and the panels' padding are
    // NOT on these panels — they live in CHILD insets (PlotArea / Text), i.e. widget
    // chrome the layout seam never persists. Because the persisted panels carry NO pixel
    // offsets, there is nothing for the binder to fold into the normalized rect, so this
    // default is resolution-independent and, on a missing/corrupt sidecar, Apply
    // reproduces the SAME displayed boxes as the live default at ALL resolutions (no
    // dropped gutter — the divergence the Medium-2 review caught). The invariant
    // Default() == the harness's offset-zero panel layout is machine-locked by
    // ReplayLayoutProbe Section 7. Returned by LayoutStore on a missing/invalid sidecar,
    // and the baseline the round-trip gate proves it can DEVIATE from (loaded != default).
    public static LayoutDocument Default()
    {
        var doc = new LayoutDocument();
        doc.version = CURRENT_VERSION;
        doc.panels = new List<PanelLayout>
        {
            new PanelLayout("chart",      0, true, new LayoutRect(0.00f, 0.00f, 0.62f, 1.00f)),
            new PanelLayout("status",     1, true, new LayoutRect(0.63f, 0.75f, 1.00f, 1.00f)),
            new PanelLayout("positions",  2, true, new LayoutRect(0.63f, 0.50f, 1.00f, 0.75f)),
            new PanelLayout("orders",     3, true, new LayoutRect(0.63f, 0.25f, 1.00f, 0.50f)),
            new PanelLayout("run_result", 4, true, new LayoutRect(0.63f, 0.00f, 1.00f, 0.25f)),
        };
        doc.canvasView = CanvasView.Identity();   // no pan, 100% (findings 0006 §3)
        doc.floatingWindows = new List<FloatingWindowLayout>();   // none open by default (findings 0008 §3)
        doc.strategyEditors = new List<StrategyEditorState>();    // no editor state by default (findings 0010 §7)
        doc.cellPositions = new List<CellPosition>();             // no cell windows by default (findings 0050)
        return doc;
    }

    public LayoutDocument Clone()
    {
        var doc = new LayoutDocument { version = version }; // ctor already gives an empty panels list
        if (panels != null)
            foreach (var p in panels)
                if (p != null) doc.panels.Add(p.Clone());
        doc.canvasView = canvasView?.Clone();
        if (floatingWindows != null)
        {
            doc.floatingWindows = new List<FloatingWindowLayout>(floatingWindows.Count);
            foreach (var w in floatingWindows)
                if (w != null) doc.floatingWindows.Add(w.Clone());
        }
        if (strategyEditors != null)
        {
            doc.strategyEditors = new List<StrategyEditorState>(strategyEditors.Count);
            foreach (var s in strategyEditors)
                if (s != null) doc.strategyEditors.Add(s.Clone());
        }
        doc.hakoniwaProfiles = hakoniwaProfiles?.Clone();
        if (cellPositions != null)
        {
            doc.cellPositions = new List<CellPosition>(cellPositions.Count);
            foreach (var c in cellPositions)
                if (c != null) doc.cellPositions.Add(c.Clone());
        }
        return doc;
    }

    public PanelLayout Find(string id)
    {
        if (panels == null || id == null) return null;
        for (int i = 0; i < panels.Count; i++)
            if (panels[i] != null && panels[i].id == id) return panels[i];
        return null;
    }

    public FloatingWindowLayout FindWindow(string id)
    {
        if (floatingWindows == null || id == null) return null;
        for (int i = 0; i < floatingWindows.Count; i++)
            if (floatingWindows[i] != null && floatingWindows[i].id == id) return floatingWindows[i];
        return null;
    }

    public StrategyEditorState FindStrategyEditor(string id)
    {
        if (strategyEditors == null || id == null) return null;
        for (int i = 0; i < strategyEditors.Count; i++)
            if (strategyEditors[i] != null && strategyEditors[i].id == id) return strategyEditors[i];
        return null;
    }

    // Structural value-equality, matched BY id (list order is incidental — `slot`
    // is the semantic order field, so reordering the list must NOT change equality,
    // but changing a panel's slot MUST). Used by the gate to assert loaded == mutated
    // and loaded != default. Float rects compared with epsilon.
    public static bool StructurallyEqual(LayoutDocument a, LayoutDocument b, float eps)
    {
        if (a == null || b == null) return a == b;
        if (a.version != b.version) return false;
        // A null canvasView and an identity view are the SAME state (no pan, 100%): a
        // freshly-Captured doc carries null (the binder doesn't own the view) while
        // Default() carries identity, and they must compare equal. Coalesce before Approx.
        if (!CanvasView.Approx(a.canvasView ?? CanvasView.Identity(),
                               b.canvasView ?? CanvasView.Identity(), eps)) return false;
        // floatingWindows matched BY id (list order incidental, like panels). A null list and
        // an empty list are the SAME state (no windows): a freshly-Captured doc with no windows
        // and Default() both carry empty, but a missing field can land as null. Coalesce counts.
        int wa = a.floatingWindows?.Count ?? 0;
        int wb = b.floatingWindows?.Count ?? 0;
        if (wa != wb) return false;
        if (wa > 0)
        {
            foreach (var fa in a.floatingWindows)
            {
                if (fa == null) return false;
                var fb = b.FindWindow(fa.id);
                if (fb == null) return false;
                if (!FloatingWindowLayout.Approx(fa, fb, eps)) return false;
            }
        }

        // strategyEditors matched BY id (list order incidental). A null list and an empty list
        // are the SAME state (no editor content): Default() carries empty, a missing field can
        // land as null. Coalesce counts, then match each by id (findings 0010 §7).
        int sa = a.strategyEditors?.Count ?? 0;
        int sb = b.strategyEditors?.Count ?? 0;
        if (sa != sb) return false;
        if (sa > 0)
        {
            foreach (var ea in a.strategyEditors)
            {
                if (ea == null) return false;
                var eb = b.FindStrategyEditor(ea.id);
                if (eb == null) return false;
                if (!StrategyEditorState.Equal(ea, eb)) return false;
            }
        }

        // hakoniwaProfiles (issue #62) — per-mode Hakoniwa tile order. Compare replay/live panel lists by
        // the same (id, slot, visible, rect) rule as `panels`. A null profiles / null sub-profile and an
        // empty one are the SAME state (coalesced counts): a legacy doc lacks the field, a #62 doc may
        // carry one mode null until visited, and JsonUtility may materialize an absent nested object as
        // empty — all must compare equal to "no per-mode data".
        if (!PanelsEqualById(a.hakoniwaProfiles?.replay?.panels, b.hakoniwaProfiles?.replay?.panels, eps)) return false;
        if (!PanelsEqualById(a.hakoniwaProfiles?.live?.panels, b.hakoniwaProfiles?.live?.panels, eps)) return false;

        // cellPositions (issue #81) — matched BY INDEX (order is the SEMANTIC dimension: position[i]
        // <-> cell[i], unlike the id-keyed panels/windows lists). null and empty coalesce to "no cells".
        int pa = a.cellPositions?.Count ?? 0;
        int pb = b.cellPositions?.Count ?? 0;
        if (pa != pb) return false;
        for (int i = 0; i < pa; i++)
            if (!CellPosition.Approx(a.cellPositions[i], b.cellPositions[i], eps)) return false;

        return PanelsEqualById(a.panels, b.panels, eps);
    }

    // Structural value-equality of two panel lists matched BY id (list order incidental — `slot` is the
    // semantic order field). null and empty coalesce to "no data". Slot/visible exact, rect epsilon.
    // Shared by `panels` and the issue #62 per-mode profile lists.
    static bool PanelsEqualById(List<PanelLayout> a, List<PanelLayout> b, float eps)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca != cb) return false;
        if (ca == 0) return true;
        foreach (var pa in a)
        {
            if (pa == null) return false;
            PanelLayout pb = null;
            foreach (var x in b) if (x != null && x.id == pa.id) { pb = x; break; }
            if (pb == null) return false;
            if (pa.slot != pb.slot || pa.visible != pb.visible) return false;
            if (!LayoutRect.Approx(pa.rect, pb.rect, eps)) return false;
        }
        return true;
    }
}
