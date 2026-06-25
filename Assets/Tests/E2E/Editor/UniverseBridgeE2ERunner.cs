// UniverseBridgeE2ERunner.cs — ADR-0031 S1 (#141) release-gate slice runner (台本: same-dir
// UniverseBridgeE2ERunner.md). 方針: ADR-0031 + findings 0115.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod UniverseBridgeE2ERunner.Run -logFile <abs>
//   # expect: [E2E UNIVERSE BRIDGE PASS] ... / exit=0  (確認は Bash `grep -a "UNIVERSE BRIDGE"`)
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHAT THIS GATES — the C#/Unity half of bt.universe.* (the Python half is gated by pytest
// test_bt_universe_bridge.py::BTUNIV-01..08). The cell's bt.universe.add/remove/clear enqueue edit
// ops the engine drains as a JSON array; the host applies them on the main thread via
// UniverseBridge → the C# InstrumentRegistry SoT (ADR-0031 D2). Because chart spawn/despawn is
// already wired to InstrumentRegistry.Changed (SyncChartWindowsToUniverse), a Python-driven add
// must spawn a chart window and a remove/clear must despawn it WITH ZERO EXTRA WIRING (D2 reflection).
//
// Python-FREE (2-gate split): the runner feeds UniverseBridge the SAME JSON shape the engine's
// drain_universe_edits() emits (`[{"op":"add","id":"7203.TSE"}]`) and drives the REAL registry +
// REAL chart cascade on the REAL BackcastWorkspaceRoot composition. Only the SOURCE of the edits (a
// live Python cell run) is faked — exactly the seam test_bt_universe_bridge.py owns from the Python
// side. The read-back direction (C#→Python mirror) JSON serialization contract is pinned here
// (UniverseIdsToJson) and exercised end-to-end by BTUNIV-02/05 in pytest.
//
// RED→GREEN litmus (findings 0115):
//   * BTUNIV-09 add → chart spawns: delete the `registry.Add(e.id)` case in UniverseBridge.Apply
//     (or the SyncChartWindowsToUniverse spawn) → the chart window never appears → RED.
//   * BTUNIV-10 remove → chart despawns: delete the `registry.Remove(e.id)` case → RED.
//   * BTUNIV-11 clear → all charts despawn: delete the `clear`→ReplaceAll(empty) case → RED.
//   * BTUNIV-12 read-back JSON: break UniverseIdsToJson / ParseEdits → the seam contract assertion RED.
//   * BTUNIV-16 mirror-dirty latch: make DriveUniverseBridge clear _universeMirrorDirty regardless of
//     the push result (or PushUniverseIds return true while !_serverReady) → the not-ready-window seed
//     is lost → RED (the §Review F2/F4/F5 "clear dirty ONLY on a confirmed push" regression).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class UniverseBridgeE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags SBF = BindingFlags.NonPublic | BindingFlags.Static;
    const string IID_A = "7203.TSE";
    const string IID_B = "9984.TSE";

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_Add_SpawnsChart()              // BTUNIV-09
                ?? Section2_Remove_DespawnsChart()         // BTUNIV-10
                ?? Section3_Clear_DespawnsAllCharts()      // BTUNIV-11
                ?? Section4_ReadBack_JsonContract()        // BTUNIV-12
                ?? Section5_MirrorDirtyLatch();            // BTUNIV-16
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E UNIVERSE BRIDGE PASS] bt.universe.* edits (engine JSON) を UniverseBridge が "
                    + "InstrumentRegistry SoT に適用し、Changed→SyncChartWindowsToUniverse で chart 窓が "
                    + "spawn(add)/despawn(remove・clear) する（追加配線ゼロ・ADR-0031 D2）。read-back の "
                    + "UniverseIdsToJson / ParseEdits seam 契約も pin。read-channel の dirty-latch（not-ready "
                    + "push で seed を落とさない・§Review F2/F4/F5）も pin。BTUNIV-09..12,16。findings 0115。");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E UNIVERSE BRIDGE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── BTUNIV-09: a Python bt.universe.add("7203.TSE") (drained as JSON) applied via UniverseBridge
    //   spawns the chart window for 7203 — NO chart-specific wiring, just the registry Changed cascade. ──
    static string Section1_Add_SpawnsChart()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S1: BackcastWorkspaceRoot missing in scene";
        if (!Seams(root, ty, out var scenario, out var dockWindows, out var chartViews, out var why)) return "S1: " + why;
        if (scenario.Universe.Ids.Count != 0) return "S1: precondition — universe must start empty";
        if (chartViews.Contains(IID_A)) return "S1: precondition — chart:" + IID_A + " must not pre-exist";

        int changed = UniverseBridge.ApplyJson(AddEdit(IID_A), scenario.Universe);

        if (changed != 1) return "S1 BTUNIV-09: UniverseBridge.Apply reported " + changed + " changes, want 1 (add did not mutate the registry)";
        if (!scenario.Universe.Ids.Contains(IID_A)) return "S1 BTUNIV-09: registry SoT does not contain " + IID_A + " after add";
        if (!chartViews.Contains(IID_A)) return "S1 BTUNIV-09: chart:" + IID_A + " did not spawn (Changed→SyncChartWindowsToUniverse not fired by add)";
        if (dockWindows.RectOf(DockShape.ChartId(IID_A)) == null) return "S1 BTUNIV-09: chart window chart:" + IID_A + " absent from the back plane";
        Debug.Log("[E2E BTUNIV-09 PASS] bt.universe.add(7203.TSE) → registry SoT + chart window spawn (free via Changed).");
        return null;
    }

    // ── BTUNIV-10: add two, then remove one. The removed instrument's chart despawns; the survivor's
    //   chart window stays. ──
    static string Section2_Remove_DespawnsChart()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S2: BackcastWorkspaceRoot missing in scene";
        if (!Seams(root, ty, out var scenario, out var dockWindows, out var chartViews, out var why)) return "S2: " + why;

        UniverseBridge.ApplyJson(AddEdit(IID_A), scenario.Universe);
        UniverseBridge.ApplyJson(AddEdit(IID_B), scenario.Universe);
        if (!chartViews.Contains(IID_A) || !chartViews.Contains(IID_B))
            return "S2 BTUNIV-10: positive-control — both charts must spawn before the remove";

        int changed = UniverseBridge.ApplyJson(RemoveEdit(IID_A), scenario.Universe);

        if (changed != 1) return "S2 BTUNIV-10: remove reported " + changed + " changes, want 1";
        if (scenario.Universe.Ids.Contains(IID_A)) return "S2 BTUNIV-10: " + IID_A + " still in registry after remove";
        if (chartViews.Contains(IID_A)) return "S2 BTUNIV-10: chart:" + IID_A + " survived the remove (despawn cascade missing)";
        if (dockWindows.RectOf(DockShape.ChartId(IID_A)) != null) return "S2 BTUNIV-10: chart window chart:" + IID_A + " still on the back plane after remove";
        if (!chartViews.Contains(IID_B)) return "S2 BTUNIV-10: survivor chart:" + IID_B + " was wrongly despawned";
        Debug.Log("[E2E BTUNIV-10 PASS] bt.universe.remove(7203.TSE) → registry drop + chart despawn; survivor 9984 kept.");
        return null;
    }

    // ── BTUNIV-11: clear empties the universe — every chart window despawns. ──
    static string Section3_Clear_DespawnsAllCharts()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S3: BackcastWorkspaceRoot missing in scene";
        if (!Seams(root, ty, out var scenario, out var dockWindows, out var chartViews, out var why)) return "S3: " + why;

        UniverseBridge.ApplyJson(AddEdit(IID_A), scenario.Universe);
        UniverseBridge.ApplyJson(AddEdit(IID_B), scenario.Universe);
        if (chartViews.Count < 2) return "S3 BTUNIV-11: positive-control — two charts must spawn before clear (got " + chartViews.Count + ")";

        int changed = UniverseBridge.ApplyJson(ClearEdit(), scenario.Universe);

        if (changed != 1) return "S3 BTUNIV-11: clear reported " + changed + " changes, want 1 (ReplaceAll(empty) on a non-empty set)";
        if (scenario.Universe.Ids.Count != 0) return "S3 BTUNIV-11: universe not empty after clear (got " + scenario.Universe.Ids.Count + ")";
        if (chartViews.Contains(IID_A) || chartViews.Contains(IID_B))
            return "S3 BTUNIV-11: a chart survived clear (chartViews=" + chartViews.Count + ")";
        if (dockWindows.RectOf(DockShape.ChartId(IID_A)) != null || dockWindows.RectOf(DockShape.ChartId(IID_B)) != null)
            return "S3 BTUNIV-11: a chart window survived clear on the back plane";
        Debug.Log("[E2E BTUNIV-11 PASS] bt.universe.clear() → registry empty + all chart windows despawn.");
        return null;
    }

    // ── BTUNIV-12: pin the read-back seam JSON contract both directions (C#-side of the round-trip
    //   pytest BTUNIV-02/05 exercises end-to-end): the engine's edit JSON parses to the right ops, and
    //   the registry Ids serialize to the array push_universe_ids expects. ──
    static string Section4_ReadBack_JsonContract()
    {
        // forward: engine edit JSON → ops
        var edits = UniverseBridge.ParseEdits("[{\"op\":\"add\",\"id\":\"7203.TSE\"},{\"op\":\"clear\",\"id\":\"\"}]");
        if (edits.Count != 2) return "S4 BTUNIV-12: ParseEdits returned " + edits.Count + " ops, want 2";
        if (edits[0].op != "add" || edits[0].id != IID_A) return "S4 BTUNIV-12: ParseEdits op0 mismatch (op=" + edits[0].op + " id=" + edits[0].id + ")";
        if (edits[1].op != "clear") return "S4 BTUNIV-12: ParseEdits op1 mismatch (op=" + edits[1].op + ")";
        if (UniverseBridge.ParseEdits("[]").Count != 0 || UniverseBridge.ParseEdits("").Count != 0)
            return "S4 BTUNIV-12: empty/blank edits must parse to zero ops";

        // back: registry Ids → push_universe_ids JSON array (reflect the root's private serializer).
        var toJson = typeof(BackcastWorkspaceRoot).GetMethod("UniverseIdsToJson", SBF);
        if (toJson == null) return "S4 BTUNIV-12: BackcastWorkspaceRoot.UniverseIdsToJson not found (renamed?)";
        IReadOnlyList<string> ids = new List<string> { IID_A, IID_B };
        string json = toJson.Invoke(null, new object[] { ids }) as string;
        if (json != "[\"7203.TSE\",\"9984.TSE\"]") return "S4 BTUNIV-12: UniverseIdsToJson mismatch: " + json;
        string empty = toJson.Invoke(null, new object[] { (IReadOnlyList<string>)new List<string>() }) as string;
        if (empty != "[]") return "S4 BTUNIV-12: empty registry must serialize to []: " + empty;
        Debug.Log("[E2E BTUNIV-12 PASS] read-back seam: engine edit JSON → ops, registry Ids → push_universe_ids array.");
        return null;
    }

    // ── BTUNIV-16: the read-channel coalesce/latch glue in DriveUniverseBridge (findings 0115 §Review
    //   F2/F4/F5 — the latent "lost seed" fix). A registry edit marks the mirror dirty; a push that
    //   does NOT confirm (here the in-proc server is not ready, so PushUniverseIds returns false) must
    //   NOT clear the dirty flag — otherwise a seed made during the not-ready window is lost forever and
    //   bt.universe.list() stays empty. Python-FREE: the runner never calls InitializePython, so _host
    //   is present but _serverReady is false (DrainUniverseEdits→"" / PushUniverseIds→false). ──
    static string Section5_MirrorDirtyLatch()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S5: BackcastWorkspaceRoot missing in scene";
        if (!Seams(root, ty, out var scenario, out _, out _, out var why)) return "S5: " + why;

        var host = ty.GetField("_host", BF)?.GetValue(root) as WorkspaceEngineHost;
        if (host == null) return "S5 BTUNIV-16: _host not present (renamed?)";
        if (host.ServerReady) return "S5 BTUNIV-16: precondition — server must be NOT ready (Python-FREE runner)";

        // The bool contract the latch rides on: a not-ready push/drain are no-ops that report failure.
        if (host.PushUniverseIds("[\"" + IID_A + "\"]")) return "S5 BTUNIV-16: PushUniverseIds returned true while server not ready (must be false — the latch would drop the seed)";
        if (host.DrainUniverseEdits() != "") return "S5 BTUNIV-16: DrainUniverseEdits must return \"\" while server not ready";

        var dirtyField = ty.GetField("_universeMirrorDirty", BF);
        if (dirtyField == null) return "S5 BTUNIV-16: _universeMirrorDirty field not found (renamed?)";
        var driveTick = ty.GetMethod("DriveUniverseBridge", BF);
        if (driveTick == null) return "S5 BTUNIV-16: DriveUniverseBridge method not found (renamed?)";

        // A registry edit (any source) must mark the mirror dirty (the += MarkUniverseMirrorDirty wiring).
        dirtyField.SetValue(root, false);
        scenario.Universe.Add(IID_A);
        if (!(bool)dirtyField.GetValue(root)) return "S5 BTUNIV-16: a registry edit did not mark the mirror dirty (MarkUniverseMirrorDirty not wired to Universe.Changed)";

        // One DriveUniverseBridge tick with the server NOT ready: the push cannot confirm, so the dirty
        // flag must SURVIVE (the seed is retried next frame, not lost). This is the §Review F2/F4/F5 litmus.
        driveTick.Invoke(root, null);
        if (!(bool)dirtyField.GetValue(root)) return "S5 BTUNIV-16: dirty flag was CLEARED on a not-ready (unconfirmed) push — the not-ready-window seed is lost (latch regression)";

        Debug.Log("[E2E BTUNIV-16 PASS] mirror-dirty latch: registry edit → dirty; not-ready push keeps dirty (no lost seed; clears only on a confirmed push).");
        return null;
    }

    // ---- helpers ----

    static string AddEdit(string iid) => "[{\"op\":\"add\",\"id\":\"" + iid + "\"}]";
    static string RemoveEdit(string iid) => "[{\"op\":\"remove\",\"id\":\"" + iid + "\"}]";
    static string ClearEdit() => "[{\"op\":\"clear\",\"id\":\"\"}]";

    static bool Seams(BackcastWorkspaceRoot root, Type ty, out ScenarioStartupController scenario,
        out FloatingWindowController dockWindows, out IDictionary chartViews, out string why)
    {
        scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        if (scenario == null || dockWindows == null || chartViews == null)
        {
            why = "root seams not built (renamed? _scenario/_dockWindows/_chartViews)";
            return false;
        }
        why = null;
        return true;
    }

    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }
}
