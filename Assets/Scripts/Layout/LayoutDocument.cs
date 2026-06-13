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

[Serializable]
public class LayoutDocument
{
    // Bumped when the schema changes incompatibly. Load() treats <=0 / missing /
    // future versions per findings §6 (default-fallback vs best-effort).
    public const int CURRENT_VERSION = 1;

    public int version;
    public List<PanelLayout> panels;

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
        return doc;
    }

    public LayoutDocument Clone()
    {
        var doc = new LayoutDocument { version = version }; // ctor already gives an empty panels list
        if (panels != null)
            foreach (var p in panels)
                if (p != null) doc.panels.Add(p.Clone());
        return doc;
    }

    public PanelLayout Find(string id)
    {
        if (panels == null || id == null) return null;
        for (int i = 0; i < panels.Count; i++)
            if (panels[i] != null && panels[i].id == id) return panels[i];
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
        int ca = a.panels?.Count ?? 0;
        int cb = b.panels?.Count ?? 0;
        if (ca != cb) return false;
        if (ca == 0) return true;
        foreach (var pa in a.panels)
        {
            if (pa == null) return false;
            var pb = b.Find(pa.id);
            if (pb == null) return false;
            if (pa.slot != pb.slot) return false;
            if (pa.visible != pb.visible) return false;
            if (!LayoutRect.Approx(pa.rect, pb.rect, eps)) return false;
        }
        return true;
    }
}
