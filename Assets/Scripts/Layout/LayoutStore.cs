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

        if (doc.panels == null)
        {
            doc.panels = new System.Collections.Generic.List<PanelLayout>();
            return;
        }
        doc.panels.RemoveAll(p => p == null || string.IsNullOrEmpty(p.id) || p.rect == null);
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
