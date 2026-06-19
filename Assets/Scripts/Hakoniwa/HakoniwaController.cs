// HakoniwaController.cs — issue #14 "Hakoniwa split-grid" (DURABLE tier, Unity boundary)
//
// The thin, input-agnostic boundary between HakoniwaGridMath (pure) and the live tile
// RectTransforms — the analogue of #12's LayoutBinder Unity-half and #13's
// InfiniteCanvasController. A PLAIN C# class (NOT a MonoBehaviour) so the AFK probe can
// drive it against headless RectTransforms. It NEVER reads input (that is
// HakoniwaTileHeaderInput's job) and NEVER sanitizes the on-disk view (that is LayoutStore's).
//
// SLOT IS THE SOURCE OF TRUTH (findings 0007 §3, owner-locked): the canonical state is the
// tile ORDER (index = grid slot). #12's PanelLayout.slot persists it — the very field
// LayoutBinder.Apply deliberately left un-applied (findings 0004 §11) because THIS shell
// slice owns its UI meaning. Each tile's rect is a DERIVED snapshot recomputed from n+order
// via HakoniwaGridMath every Rebuild; it is NOT the source of truth for placement.
//
// TILE PLACEMENT (findings 0007 §4): Rebuild sets each tile's anchorMin/Max to its cell
// corners with offsets zeroed — the canonical, resolution-independent anchor form #12 uses.
// The HakoniwaRoot owns the grid's box; tiles are its children spanning normalized cells.

using System.Collections.Generic;
using UnityEngine;

public class HakoniwaController
{
    // The canonical default tile order (= #12 LayoutDocument.Default() slot order), used by
    // NormalizeOrder to append known-but-unordered tiles in a sensible sequence. This is only a
    // FALLBACK ordering for tiles handed to the constructor; the #61 workspace orchestrator drives
    // the live base order explicitly via the constructor ids + Reorder (it does NOT read this), and
    // the #14 HakoniwaE2ERunner (§1/§3) builds its generic 5-tile grid (chart/status/…) from this set.
    public static readonly string[] DEFAULT_ORDER =
        { "chart", "status", "positions", "orders", "run_result" };

    readonly RectTransform _root;
    readonly Dictionary<string, RectTransform> _tilesById;
    List<string> _order;   // index = grid slot (the source of truth)

    public HakoniwaController(RectTransform root, IDictionary<string, RectTransform> tilesById, IList<string> initialOrder)
    {
        if (root == null) throw new System.ArgumentNullException(nameof(root));
        if (tilesById == null) throw new System.ArgumentNullException(nameof(tilesById));
        _root = root;
        _tilesById = new Dictionary<string, RectTransform>(tilesById);
        _order = NormalizeOrder(initialOrder);
        Rebuild();
    }

    public IReadOnlyList<string> Order => _order;
    public int Count => _order.Count;

    // The current grid slot of a tile id, or -1 if unknown (used by the header input to find
    // its own slot AFTER prior swaps moved it).
    public int SlotOf(string id) => _order.IndexOf(id);

    // Place every tile into its cell for the current order (canonical anchors, offsets 0).
    public void Rebuild()
    {
        var cells = HakoniwaGridMath.CellRects(_order.Count);
        for (int i = 0; i < _order.Count; i++)
        {
            if (!_tilesById.TryGetValue(_order[i], out RectTransform rt) || rt == null) continue;
            var c = cells[i];
            rt.anchorMin = new Vector2(c.minX, c.minY);
            rt.anchorMax = new Vector2(c.maxX, c.maxY);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }

    // Swap two grid slots (the only mutation — swap, NOT free placement, findings 0007 §6).
    // from==to / out-of-range -> no-op (returns false), matching TTWR ADR 0014's cancel rule.
    public bool Swap(int from, int to)
    {
        if (from == to) return false;
        if (from < 0 || to < 0 || from >= _order.Count || to >= _order.Count) return false;
        (_order[from], _order[to]) = (_order[to], _order[from]);
        Rebuild();
        return true;
    }

    // Runtime tile add/remove (#60 chart tile family): the controller was construction-static; the
    // membership orchestrator (BackcastWorkspaceRoot, on InstrumentRegistry.Changed) now adds/removes
    // chart:<id> tiles at runtime. Rebuild stays box-size-free (box-grow is the orchestrator's job,
    // findings 0027 §6) — these only mutate _tilesById/_order then re-lay the normalized cells.

    // Register a new tile and append it to the end of the order (a new last grid slot). A known id
    // is a no-op for the order (its RectTransform mapping is refreshed). Returns true if newly added.
    public bool AddTile(string id, RectTransform rt)
    {
        if (string.IsNullOrEmpty(id) || rt == null) return false;
        bool isNew = !_tilesById.ContainsKey(id);
        _tilesById[id] = rt;
        if (isNew) _order.Add(id);
        Rebuild();
        return isNew;
    }

    // Unregister a tile (the caller owns destroying its GameObject). Returns true if it was present.
    public bool RemoveTile(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        bool had = _tilesById.Remove(id);
        if (_order.Remove(id) || had) { Rebuild(); return true; }
        return false;
    }

    // Move `desiredPrefix` ids to the FRONT in the given order; every remaining KNOWN tile keeps its
    // current relative order after them. Used by the membership orchestrator to restore the
    // [base…, chart…] invariant after a base retile (#61/findings 0028 §1): the orchestrator passes
    // the mode's canonical base order, and the chart tiles (unlisted) fall through, order-preserved.
    // Unknown ids in `desiredPrefix` are skipped (tolerance, like Apply). Stays box-size-free.
    public void Reorder(IList<string> desiredPrefix)
    {
        var result = new List<string>(_order.Count);
        var seen = new HashSet<string>();
        if (desiredPrefix != null)
            foreach (var id in desiredPrefix)
                if (!string.IsNullOrEmpty(id) && _tilesById.ContainsKey(id) && seen.Add(id)) result.Add(id);
        foreach (var id in _order)
            if (seen.Add(id)) result.Add(id);
        _order = result;
        Rebuild();
    }

    // root-local NORMALIZED point (0..1) -> grid slot, or -1 if outside every cell.
    public int SlotAtNormalized(Vector2 pointNormalized) =>
        HakoniwaGridMath.SlotAt(HakoniwaGridMath.CellRects(_order.Count), pointNormalized);

    // live -> document. slot = the tile's index in the order; rect = its DERIVED cell (so the
    // doc faithfully mirrors the on-screen layout, findings 0007 §3); visible = activeSelf.
    public LayoutDocument Capture()
    {
        var cells = HakoniwaGridMath.CellRects(_order.Count);
        var doc = new LayoutDocument { version = LayoutDocument.CURRENT_VERSION, panels = new List<PanelLayout>() };
        for (int i = 0; i < _order.Count; i++)
        {
            string id = _order[i];
            bool visible = _tilesById.TryGetValue(id, out RectTransform rt) && rt != null
                ? rt.gameObject.activeSelf : true;
            doc.panels.Add(new PanelLayout(id, i, visible, cells[i].Clone()));
        }
        return doc;
    }

    // document -> live. Reorders this controller's tiles by the doc's slots (NOT by the saved
    // rect — rect is regenerated from the grid, findings 0007 §3), applies visibility, rebuilds.
    // Tolerance (findings 0007 §5): doc ids unknown here are ignored; known tiles absent from
    // the doc keep their current relative order and land after the doc-ordered ones.
    public void Apply(LayoutDocument doc)
    {
        _order = DeriveOrder(doc);
        if (doc?.panels != null)
        {
            foreach (var p in doc.panels)
            {
                if (p == null || string.IsNullOrEmpty(p.id)) continue;
                if (_tilesById.TryGetValue(p.id, out RectTransform rt) && rt != null)
                    rt.gameObject.SetActive(p.visible);
            }
        }
        Rebuild();
    }

    // Canonical order from a document's slots. Out-of-range / negative / DUPLICATE slots are
    // tolerated: we sort by (slot, then current order index as a stable tie-break), then the
    // CELL index (0..n-1) — not the raw slot value — is the effective slot, so any gaps or
    // collisions collapse to a contiguous order (findings 0007 §5).
    List<string> DeriveOrder(LayoutDocument doc)
    {
        var entries = new List<(string id, int slot, int orig)>(_order.Count);
        for (int oi = 0; oi < _order.Count; oi++)
        {
            string id = _order[oi];
            var p = doc?.Find(id);
            int slot = p != null ? p.slot : int.MaxValue;   // absent -> append after doc-ordered
            entries.Add((id, slot, oi));
        }
        entries.Sort((a, b) => a.slot != b.slot ? a.slot.CompareTo(b.slot) : a.orig.CompareTo(b.orig));

        var result = new List<string>(entries.Count);
        foreach (var e in entries) result.Add(e.id);
        return result;
    }

    // Keep only KNOWN tile ids (dedup), then append any known tile absent from `initial`,
    // preferring DEFAULT_ORDER then dictionary order — so the controller always has every
    // live tile exactly once.
    List<string> NormalizeOrder(IList<string> initial)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        if (initial != null)
            foreach (var id in initial)
                if (!string.IsNullOrEmpty(id) && _tilesById.ContainsKey(id) && seen.Add(id))
                    result.Add(id);
        foreach (var id in DEFAULT_ORDER)
            if (_tilesById.ContainsKey(id) && seen.Add(id)) result.Add(id);
        foreach (var kv in _tilesById)
            if (seen.Add(kv.Key)) result.Add(kv.Key);
        return result;
    }
}
