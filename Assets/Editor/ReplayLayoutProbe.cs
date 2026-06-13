// ReplayLayoutProbe.cs — issue #12 "Replay layout" (THROWAWAY AFK regression gate)
//
// The headless, Python-FREE regression gate for the layout-persistence seam. Run:
//
//   <Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
//           -executeMethod ReplayLayoutProbe.Run -logFile <log>
//   # expect: [REPLAY LAYOUT PASS] ... / exit=0
//
// #12 is AFK-ONLY (findings §5): the round-trip is deterministic data + rect
// arithmetic, fully assertable headless — no playmode, no Python, NO auto-bootstrap
// (so it never re-triggers the single-Play-owner collision, findings 0003 §8).
//
// THE VACUOUS-ROUND-TRIP KILL (findings §1, the whole point of this slice): layout
// persistence has a brutal false-green cousin of the S0 16-byte pin / #10 zero-fill
// / #11 equity=0 family — save(default)->load->assert(==default) passes GREEN even
// for a serializer that writes/reads NOTHING, because default==default. So this gate
// mutates a NON-DEFAULT document, persists it, loads into a FRESH instance, and
// asserts the MUTATED values survived (loaded==mutated AND loaded!=default) AND that
// the mutation reached the on-disk JSON TEXT (catches an in-memory-only round-trip
// that never actually persists).
//
// FIVE FAILURE SECTIONS (findings §5), each returns null on pass or a reason string:
//   1. document mutation / non-vacuous proof (doc<->disk)
//   2. save / load / version / unknown-field tolerance
//   3. Capture conversion (live anchor+offset -> normalized rect)
//   4. Apply conversion (document -> live) + id tolerance
//   5. fresh-target restore (disk -> load -> Apply to brand-new targets)
//   6. fallback: corrupt JSON AND missing file both -> default
//
// Guarded by Application.isBatchMode is NOT needed (no Python/render); but it lives
// in Assets/Editor and Exit()s the process, so it only runs under -executeMethod.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ReplayLayoutProbe
{
    const float EPS = 1e-3f;

    // A deterministic, writable temp path — NOT the production sidecar
    // (LayoutPathResolver.DefaultPath()), so the gate never clobbers a real layout.
    static string TempDir => Path.Combine(Application.temporaryCachePath, "replay_layout_probe");
    static string TempPath => Path.Combine(TempDir, "layout.json");

    public static void Run()
    {
        string fail = null;
        var spawned = new List<GameObject>();
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);

            fail = Section1_MutationNonVacuous()
                ?? Section2_VersionAndUnknown(spawned)
                ?? Section3_Capture(spawned)
                ?? Section4_ApplyAndIdTolerance(spawned)
                ?? Section5_FreshRestore(spawned)
                ?? Section6_FallbackPaths();
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }
        finally
        {
            foreach (var go in spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
        }

        if (fail == null)
        {
            Debug.Log("[REPLAY LAYOUT PASS] non-vacuous doc<->disk round-trip + version/unknown tolerance " +
                      "+ Capture/Apply conversion + fresh-target restore + corrupt/missing fallback " +
                      "(Unity-owned versioned schema, ADR-0003 capability parity, under Unity Mono)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[REPLAY LAYOUT FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. document mutation / non-vacuous proof (doc <-> disk) ----
    static string Section1_MutationNonVacuous()
    {
        LayoutDocument def = LayoutDocument.Default();

        // Build a NON-DEFAULT document mutating MULTIPLE independent dimensions:
        // panel order (slot), visibility, and chart split + a panel rect.
        LayoutDocument mutated = def.Clone();
        PanelLayout status = mutated.Find("status");
        PanelLayout runres = mutated.Find("run_result");
        PanelLayout orders = mutated.Find("orders");
        PanelLayout chart  = mutated.Find("chart");
        if (status == null || runres == null || orders == null || chart == null)
            return "S1: default doc missing an expected panel id";

        int sTmp = status.slot; status.slot = runres.slot; runres.slot = sTmp; // swap order
        orders.visible = false;                                                // hide a panel
        chart.rect.maxX = 0.50f;                                               // move the split (0.62 -> 0.50)
        status.rect.minY = 0.80f;                                              // move a panel rect

        if (LayoutDocument.StructurallyEqual(mutated, def, EPS))
            return "S1: mutation produced a doc still structurally-equal to default (mutation no-op)";

        LayoutStore.Save(mutated, TempPath);

        if (!File.Exists(TempPath)) return "S1: Save did not create the sidecar file";
        string rawJson = File.ReadAllText(TempPath);
        if (string.IsNullOrWhiteSpace(rawJson)) return "S1: saved sidecar is empty";

        // STRUCTURAL: the mutation must be present in the on-disk TEXT, not just in
        // memory. A serializer that writes default regardless would fail both checks.
        string defaultJson = JsonUtility.ToJson(def, true);
        if (rawJson == defaultJson)
            return "S1: on-disk JSON is byte-identical to default (mutation never reached the file)";
        if (!rawJson.Contains("false"))
            return "S1: on-disk JSON has no 'false' — the hidden-panel mutation did not reach the text";

        // Load into a FRESH instance.
        LayoutDocument loaded = LayoutStore.Load(TempPath);
        if (loaded == null) return "S1: Load returned null";
        if (ReferenceEquals(loaded, mutated)) return "S1: Load returned the same instance (not a real reload)";

        if (!LayoutDocument.StructurallyEqual(loaded, mutated, EPS))
            return "S1: loaded != mutated (round-trip lost state)";
        if (LayoutDocument.StructurallyEqual(loaded, def, EPS))
            return "S1: loaded == default (VACUOUS round-trip — serializer may be a no-op)";

        return null;
    }

    // ---- 2. version + unknown-field/panel tolerance ----
    static string Section2_VersionAndUnknown(List<GameObject> spawned)
    {
        // (a) future version 999 + unknown top-level field + a KNOWN panel carrying an
        // unknown extra field + an UNKNOWN ghost panel. Expect: no throw, version kept
        // best-effort, known values bound, unknown elements don't corrupt known state.
        string injected =
            "{\"version\":999,\"futureGlobalField\":\"ignore-me\",\"panels\":[" +
            "{\"id\":\"status\",\"slot\":7,\"visible\":true,\"unknownPanelField\":42," +
            "\"rect\":{\"minX\":0.1,\"minY\":0.2,\"maxX\":0.3,\"maxY\":0.4}}," +
            "{\"id\":\"ghost_unknown\",\"slot\":9,\"visible\":true," +
            "\"rect\":{\"minX\":0.5,\"minY\":0.5,\"maxX\":0.6,\"maxY\":0.6}}" +
            "]}";

        LayoutDocument loaded = LayoutStore.LoadFromJson(injected);
        if (loaded == null) return "S2: LoadFromJson(version 999) returned null";
        if (loaded.version != 999) return "S2: future version not preserved best-effort (got " + loaded.version + ")";

        PanelLayout st = loaded.Find("status");
        if (st == null) return "S2: known panel 'status' dropped";
        if (st.slot != 7 || !st.visible) return "S2: known panel fields not bound past the unknown field";
        if (!LayoutRect.Approx(st.rect, new LayoutRect(0.1f, 0.2f, 0.3f, 0.4f), EPS))
            return "S2: known panel rect not bound past the unknown field";

        // (b) the unknown elements must not break APPLY of known UI state. Apply to a
        // live target set that has 'status' but NOT 'ghost_unknown'.
        var parent = NewRect("S2Parent", null, spawned);
        var liveStatus = NewRect("status", parent, spawned, anchorMin: Vector2.zero, anchorMax: Vector2.one);
        var liveOther  = NewRect("orders", parent, spawned,
                                 anchorMin: new Vector2(0.9f, 0.9f), anchorMax: Vector2.one); // not in doc
        Vector2 otherAnchorBefore = liveOther.anchorMin;

        var live = new Dictionary<string, RectTransform> { { "status", liveStatus }, { "orders", liveOther } };
        LayoutBinder.Apply(loaded, live);

        if (!Approx(liveStatus.anchorMin, new Vector2(0.1f, 0.2f)) ||
            !Approx(liveStatus.anchorMax, new Vector2(0.3f, 0.4f)))
            return "S2: known panel not applied to live despite version 999 / ghost panel";
        if (!Approx(liveOther.anchorMin, otherAnchorBefore))
            return "S2: live-only panel ('orders', absent from doc) was disturbed";

        // (c) version <= 0 / missing -> default fallback (required-version not vacuous).
        LayoutDocument noVer = LayoutStore.LoadFromJson("{\"panels\":[]}"); // version binds to 0
        if (!LayoutDocument.StructurallyEqual(noVer, LayoutDocument.Default(), EPS))
            return "S2: missing version (binds 0) did NOT fall back to default";
        LayoutDocument negVer = LayoutStore.LoadFromJson("{\"version\":-3,\"panels\":[]}");
        if (!LayoutDocument.StructurallyEqual(negVer, LayoutDocument.Default(), EPS))
            return "S2: negative version did NOT fall back to default";

        return null;
    }

    // ---- 3. Capture conversion (live anchor+offset -> normalized display rect) ----
    static string Section3_Capture(List<GameObject> spawned)
    {
        const float PW = 1000f, PH = 800f;

        // A panel with BOTH anchors AND pixel offsets, so Capture must do the offset
        // math (not just pass anchors through). Hand-computed expectation:
        //   minX=(0.1*1000+20)/1000=0.12   minY=(0.1*800+10)/800=0.1125
        //   maxX=(0.5*1000-30)/1000=0.47   maxY=(0.5*800-5)/800=0.49375
        var parent = NewRect("S3Parent", null, spawned);
        var rt = NewRect("positions", parent, spawned,
                         anchorMin: new Vector2(0.1f, 0.1f), anchorMax: new Vector2(0.5f, 0.5f));
        rt.offsetMin = new Vector2(20f, 10f);
        rt.offsetMax = new Vector2(-30f, -5f);

        var bindings = new[] { new LayoutBinder.PanelBinding("positions", 2, true, rt) };
        LayoutDocument doc = LayoutBinder.Capture(PW, PH, bindings);

        PanelLayout p = doc.Find("positions");
        if (p == null) return "S3: Capture dropped the panel";
        var expected = new LayoutRect(0.12f, 0.1125f, 0.47f, 0.49375f);
        if (!LayoutRect.Approx(p.rect, expected, EPS))
            return $"S3: Capture rect wrong: got ({p.rect.minX},{p.rect.minY},{p.rect.maxX},{p.rect.maxY}) " +
                   $"expected (0.12,0.1125,0.47,0.49375) — offset math broken";
        if (doc.version != LayoutDocument.CURRENT_VERSION) return "S3: Capture did not stamp CURRENT_VERSION";

        return null;
    }

    // ---- 4. Apply conversion (document -> live) + id tolerance ----
    static string Section4_ApplyAndIdTolerance(List<GameObject> spawned)
    {
        var parent = NewRect("S4Parent", null, spawned);
        // Fresh targets at DEFAULT (full-stretch) anchors; Apply must move them.
        var liveStatus = NewRect("status", parent, spawned, anchorMin: Vector2.zero, anchorMax: Vector2.one);
        var liveOrders = NewRect("orders", parent, spawned, anchorMin: Vector2.zero, anchorMax: Vector2.one);
        var liveUntouched = NewRect("untouched", parent, spawned,
                                    anchorMin: new Vector2(0.7f, 0.7f), anchorMax: Vector2.one);
        Vector2 untouchedBefore = liveUntouched.anchorMin;

        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>
            {
                new PanelLayout("status", 0, true,  new LayoutRect(0.2f, 0.6f, 0.8f, 0.9f)),
                new PanelLayout("orders", 1, false, new LayoutRect(0.2f, 0.1f, 0.8f, 0.4f)),
                new PanelLayout("doc_only_ghost", 2, true, new LayoutRect(0f, 0f, 1f, 1f)), // no live target
            }
        };

        var live = new Dictionary<string, RectTransform>
        {
            { "status", liveStatus }, { "orders", liveOrders }, { "untouched", liveUntouched },
        };
        LayoutBinder.Apply(doc, live);

        // status: rect applied as canonical anchors, offsets zeroed, still visible.
        if (!Approx(liveStatus.anchorMin, new Vector2(0.2f, 0.6f)) ||
            !Approx(liveStatus.anchorMax, new Vector2(0.8f, 0.9f)))
            return "S4: status rect not applied as canonical anchors";
        if (!Approx(liveStatus.offsetMin, Vector2.zero) || !Approx(liveStatus.offsetMax, Vector2.zero))
            return "S4: status offsets not zeroed (canonical form broken)";
        if (!liveStatus.gameObject.activeSelf) return "S4: visible=true panel was deactivated";

        // orders: visible=false -> deactivated, but rect still applied.
        if (liveOrders.gameObject.activeSelf) return "S4: visible=false panel was not deactivated";
        if (!Approx(liveOrders.anchorMin, new Vector2(0.2f, 0.1f)))
            return "S4: orders rect not applied";

        // id tolerance: doc_only_ghost (no live target) must be skipped silently;
        // 'untouched' (live but absent from doc) must keep its original anchors.
        if (!Approx(liveUntouched.anchorMin, untouchedBefore))
            return "S4: live-only panel ('untouched', absent from doc) was disturbed";

        return null;
    }

    // ---- 5. fresh-target restore (disk -> load -> Apply to brand-new targets) ----
    static string Section5_FreshRestore(List<GameObject> spawned)
    {
        // Persist a mutated doc, reload from DISK, Apply to a FRESH target set that
        // never participated in Capture — proves the whole seam end-to-end.
        var mutated = LayoutDocument.Default();
        PanelLayout chart = mutated.Find("chart");
        chart.rect.maxX = 0.45f;
        chart.visible = false;
        LayoutStore.Save(mutated, TempPath);

        LayoutDocument loaded = LayoutStore.Load(TempPath);

        var parent = NewRect("S5Parent", null, spawned);
        var liveChart = NewRect("chart", parent, spawned, anchorMin: Vector2.zero, anchorMax: Vector2.one);
        var live = new Dictionary<string, RectTransform> { { "chart", liveChart } };

        LayoutBinder.Apply(loaded, live);

        if (!Approx(liveChart.anchorMax, new Vector2(0.45f, 1f)))
            return "S5: restored chart split not applied to fresh target (got maxX=" + liveChart.anchorMax.x + ")";
        if (liveChart.gameObject.activeSelf)
            return "S5: restored chart visibility=false not applied to fresh target";

        return null;
    }

    // ---- 6. fallback: corrupt JSON AND missing file both -> default ----
    static string Section6_FallbackPaths()
    {
        // corrupt file
        Directory.CreateDirectory(TempDir);
        File.WriteAllText(TempPath, "{ this is not valid json :: ");
        LayoutDocument fromCorrupt = LayoutStore.Load(TempPath);
        if (!LayoutDocument.StructurallyEqual(fromCorrupt, LayoutDocument.Default(), EPS))
            return "S6: corrupt JSON did NOT fall back to default";

        // missing file
        string missing = Path.Combine(TempDir, "does_not_exist.json");
        if (File.Exists(missing)) File.Delete(missing);
        LayoutDocument fromMissing = LayoutStore.Load(missing);
        if (!LayoutDocument.StructurallyEqual(fromMissing, LayoutDocument.Default(), EPS))
            return "S6: missing file did NOT fall back to default";

        return null;
    }

    // ---- helpers ----

    static RectTransform NewRect(string name, RectTransform parent, List<GameObject> spawned,
                                 Vector2 anchorMin = default, Vector2 anchorMax = default)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        if (parent != null) rt.SetParent(parent, false);
        else spawned.Add(go); // only track roots for destroy; children die with the root
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return rt;
    }

    static bool Approx(Vector2 a, Vector2 b) =>
        Mathf.Abs(a.x - b.x) <= EPS && Mathf.Abs(a.y - b.y) <= EPS;
}
