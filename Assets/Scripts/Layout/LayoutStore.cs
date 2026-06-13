// LayoutStore.cs — issue #12 "Replay layout" (DURABLE tier, persistence)
//
// The durable persistence seam for the layout sidecar. JsonUtility is HIDDEN
// inside (mirrors ReplayBarDecoder/ReplayPanelDecoder's parser-hiding swap-point
// discipline): callers never see the parser, so it can be swapped (e.g. to
// Newtonsoft, if unknown-field PRESERVE is ever needed — findings §6a) without
// touching consumers.
//
// API (findings §2): Save(doc, path) / Load(path) take an EXPLICIT path so tests
// pass a deterministic temp path; LayoutPathResolver owns the production path.
//
// TRUST-BOUNDARY DEPARTURE from the decoder discipline (findings §8, owner-locked):
// the decoders THROW on malformed JSON because engine payloads are always valid
// json.dumps output — a parse failure there is a real bug. The layout sidecar is
// the opposite trust boundary: a user-disk file that can be hand-edited, partially
// written, or stale-corrupt. Crashing the app on a bad sidecar is the wrong UX, so
// Load is FAIL-SOFT: malformed / unreadable / missing / invalid-version -> warn +
// LayoutDocument.Default(). (The gate proves both corrupt-JSON and missing-file
// fall back to default — ReplayLayoutProbe.)
//
// VERSION POLICY (findings §6b):
//   version <= 0 / missing / non-numeric / unparseable -> INVALID -> default.
//   1 <= version < CURRENT_VERSION                      -> use known (migrate later).
//   version == CURRENT_VERSION                          -> use.
//   version  > CURRENT_VERSION                          -> warn + best-effort known.
//
// SAVE (findings §8): #12 does a PLAIN overwrite and creates the parent directory.
// atomic write (temp+rename) / debounce are deferred to the real autosave wiring.

using System;
using System.IO;
using UnityEngine;

public static class LayoutStore
{
    // Plain overwrite. Creating the parent directory is Save's responsibility.
    public static void Save(LayoutDocument doc, string path)
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is empty", nameof(path));

        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string json = JsonUtility.ToJson(doc, /*prettyPrint:*/ true);
        File.WriteAllText(path, json);
    }

    // Fail-soft: missing/unreadable/malformed/invalid-version -> Default().
    public static LayoutDocument Load(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogWarning("[LAYOUT] Load: empty path -> default layout.");
            return LayoutDocument.Default();
        }
        if (!File.Exists(path))
        {
            // first-run / never-saved is the normal case, not an error.
            Debug.Log("[LAYOUT] Load: no sidecar at '" + path + "' -> default layout (first run).");
            return LayoutDocument.Default();
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[LAYOUT] Load: unreadable sidecar '" + path + "' (" + e.Message + ") -> default layout.");
            return LayoutDocument.Default();
        }

        return LoadFromJson(json);
    }

    // The JSON -> document core, exposed so the AFK gate can inject raw JSON
    // (e.g. version:999 + unknown field/panel) without touching the filesystem.
    // ALWAYS returns a usable, sanitized document (never null, never throws).
    public static LayoutDocument LoadFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            Debug.LogWarning("[LAYOUT] Load: empty/blank JSON -> default layout.");
            return LayoutDocument.Default();
        }

        LayoutDocument doc;
        try
        {
            doc = JsonUtility.FromJson<LayoutDocument>(json);
        }
        catch (Exception e)
        {
            // malformed / non-numeric version / type mismatch.
            Debug.LogWarning("[LAYOUT] Load: malformed JSON (" + e.Message + ") -> default layout.");
            return LayoutDocument.Default();
        }

        if (doc == null)
        {
            Debug.LogWarning("[LAYOUT] Load: JSON parsed to null -> default layout.");
            return LayoutDocument.Default();
        }

        if (doc.version <= 0)
        {
            // missing version binds to 0 -> we refuse to treat a version-less file as
            // a valid v1 doc (prevents the required-version gate from passing vacuously).
            Debug.LogWarning("[LAYOUT] Load: invalid version=" + doc.version + " (<=0/missing) -> default layout.");
            return LayoutDocument.Default();
        }

        if (doc.version > LayoutDocument.CURRENT_VERSION)
        {
            Debug.LogWarning("[LAYOUT] Load: future version=" + doc.version +
                             " > CURRENT=" + LayoutDocument.CURRENT_VERSION +
                             " -> best-effort using known fields.");
            // fall through: keep the doc, bind known fields, ignore unknowns.
        }
        else if (doc.version < LayoutDocument.CURRENT_VERSION)
        {
            Debug.LogWarning("[LAYOUT] Load: older version=" + doc.version +
                             " < CURRENT=" + LayoutDocument.CURRENT_VERSION +
                             " -> using known fields (no migration needed yet).");
        }

        Sanitize(doc);
        return doc;
    }

    // Drop entries that would break the binder (null entries, null id, null rect), and
    // NORMALIZE the additive canvasView (issue #13). Best-effort: a partially-bad file
    // still yields its good panels and a usable view.
    static void Sanitize(LayoutDocument doc)
    {
        NormalizeCanvasView(doc);
        NormalizeFloatingWindows(doc);
        NormalizeStrategyEditors(doc);

        if (doc.panels == null)
        {
            doc.panels = new System.Collections.Generic.List<PanelLayout>();
            return;
        }
        doc.panels.RemoveAll(p => p == null || string.IsNullOrEmpty(p.id) || p.rect == null);
    }

    // floatingWindows normalization (issue #15, findings 0008 §3) — the persistence-boundary
    // half of the contract. Drop entries the restore controller can't place: null, null/empty
    // id OR kind, and non-finite/<=0 w/h (a degenerate size — and the spec-catalog minimum
    // clamp is the SPAWN boundary's job, NOT a generic fallback here). Non-finite x/y -> 0 per
    // axis (a recoverable position, not a drop). DUPLICATE id -> keep the FIRST, drop the rest
    // (id is document-unique). An UNKNOWN kind is PRESERVED (the store has no catalog; the
    // controller skips its spawn — forward-evolution discipline). zOrder is kept VERBATIM (the
    // 0..n-1 contiguous normalization is the controller's Apply, so a non-contiguous hand-
    // authored z survives the round-trip the gate asserts). Old #12/#13 sidecar -> empty list.
    public static void NormalizeFloatingWindows(LayoutDocument doc)
    {
        if (doc == null) return;
        if (doc.floatingWindows == null)
        {
            doc.floatingWindows = new System.Collections.Generic.List<FloatingWindowLayout>();
            return;
        }

        var seen = new System.Collections.Generic.HashSet<string>();
        var kept = new System.Collections.Generic.List<FloatingWindowLayout>(doc.floatingWindows.Count);
        foreach (var w in doc.floatingWindows)
        {
            if (w == null) continue;
            if (string.IsNullOrEmpty(w.id) || string.IsNullOrEmpty(w.kind)) continue;
            if (!IsFinite(w.w) || !IsFinite(w.h) || w.w <= 0f || w.h <= 0f) continue;  // degenerate size -> drop
            if (!seen.Add(w.id)) continue;                                             // duplicate id -> keep first
            if (!IsFinite(w.x)) w.x = 0f;
            if (!IsFinite(w.y)) w.y = 0f;
            kept.Add(w);
        }
        doc.floatingWindows = kept;
    }

    // strategyEditors normalization (issue #16, findings 0010 §7) — the persistence-boundary
    // half. Drop entries that can't be associated: null, null/empty id, OR null/empty filePath
    // (a content state with no path is meaningless — restore would reset it to unbound-empty
    // anyway). DUPLICATE id -> keep the FIRST, drop the rest (id is document-unique). An ORPHAN
    // id (no matching floatingWindows entry) and a MISSING/non-existent path are KEPT VERBATIM:
    // LayoutStore does NO filesystem check and NO canonicalization (findings 0010 §7) — existence
    // is the restore controller's concern (Open may fail and leave the window unbound-empty).
    // Old #12/#13/#15 sidecar (no strategyEditors) -> empty list.
    public static void NormalizeStrategyEditors(LayoutDocument doc)
    {
        if (doc == null) return;
        if (doc.strategyEditors == null)
        {
            doc.strategyEditors = new System.Collections.Generic.List<StrategyEditorState>();
            return;
        }

        var seen = new System.Collections.Generic.HashSet<string>();
        var kept = new System.Collections.Generic.List<StrategyEditorState>(doc.strategyEditors.Count);
        foreach (var s in doc.strategyEditors)
        {
            if (s == null) continue;
            if (string.IsNullOrEmpty(s.id) || string.IsNullOrEmpty(s.filePath)) continue;
            if (!seen.Add(s.id)) continue;   // duplicate id -> keep first
            kept.Add(s);
        }
        doc.strategyEditors = kept;
    }

    // canvasView normalization (issue #13, findings 0006 §3) — the AUTHORITATIVE place
    // (the binder/math never sanitize). An old #12 sidecar (no canvasView) or a hand-
    // corrupted view must never yield a degenerate transform: missing/null -> identity,
    // non-finite pan -> 0 per axis, non-finite or <=0 zoom -> 1, then clamp [0.2,5.0].
    // `internal`-equivalent visibility via public so the AFK probe can drive non-finite
    // cases DIRECTLY on a CanvasView (NaN isn't valid JSON, so it can't be reached through
    // LoadFromJson without tripping the whole-document parse fallback instead).
    public static void NormalizeCanvasView(LayoutDocument doc)
    {
        if (doc == null) return;
        if (doc.canvasView == null) { doc.canvasView = CanvasView.Identity(); return; }

        var v = doc.canvasView;
        if (!IsFinite(v.panX)) v.panX = 0f;
        if (!IsFinite(v.panY)) v.panY = 0f;
        if (!IsFinite(v.zoom) || v.zoom <= 0f) v.zoom = 1f;
        v.zoom = Mathf.Clamp(v.zoom, CanvasView.MIN_ZOOM, CanvasView.MAX_ZOOM);
    }

    static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
}
