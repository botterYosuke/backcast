// ChartUniverseWriteConsistencyE2ERunner.cs — Issue #124 release-gate slice runner (台本: same-dir
// ChartUniverseWriteConsistencyE2ERunner.md). 方針: findings 0099. WRITE-SIDE complement to #123's
// restore-side ChartUniverseSyncE2ERunner (findings 0095) — defense-in-depth, #123 kept as the backstop.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartUniverseWriteConsistencyE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART UNIVERSE WRITE PASS] ... / exit=0  (確認は Bash `grep -a "CHART UNIVERSE WRITE"`)
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHAT THIS GATES — the on-disk invariant: in a saved <strategy>.json, if layout.floatingWindows holds a
// chart:<iid>, then the universe that document RESOLVES TO on reopen (the scenario sidecar key if present,
// ELSE the inline .py SCENARIO — the SAME resolution SeedScenarioFromEditor uses) contains <iid>. The two
// sidecar keys are written by ASYMMETRIC, non-atomic gates (layout: written whenever a doc is bound;
// universe: Replay + Editable + complete-sidecar + content-change gated, mutate-existing-only #67), so a
// chart:<iid> can be baked into layout while its instrument is absent from the resolved universe = an
// on-disk orphan. FIX (TryWriteLayout): PruneOrphanChartWindowsForPersistence drops, from the captured
// LayoutDocument, every chart:<iid> whose instrument is absent from the doc's persisted-resolved universe.
// READ-ONLY w.r.t. scenario → mutate-existing-only / inline-shadow / D5 untouched. FAIL-OPEN: prune only
// when the universe is CONFIDENTLY resolved (unreadable sidecar / unparseable inline → keep all, #123 backstops).
//
// RED→GREEN litmus (findings 0099): delete the PruneOrphanChartWindowsForPersistence(doc, path) call in
// TryWriteLayout → CHARTWRITE-01 (real OnFileSave) / 03 (subset) / 04 (quit autosave) / 07 (grouped orphan) /
// 08 (present-but-empty sidecar) go RED (the orphan chart is baked into the persisted layout). Narrow the
// oracle to the sidecar key ALONE (drop the inline
// fallback) → CHARTWRITE-02 goes RED (an inline-.py universe's chart is wrongly pruned). Make the prune
// FAIL-CLOSED (treat an unreadable universe as empty) → CHARTWRITE-05 / 06 go RED (a transient read failure
// nukes restored chart geometry). CHARTWRITE-02/03/07 also pin position-persistence: a chart whose
// instrument IS in the resolved universe keeps its restored geometry (x/y/w/h) verbatim.
//
// Drives the REAL persistence chokepoint on the REAL BackcastWorkspaceRoot composition:
//   * CHARTWRITE-01 → OnFileOpen → (uncommitted Universe.Add) → OnFileSave   (end-to-end wiring + core fix)
//   * CHARTWRITE-04 → OnFileOpen → (uncommitted Universe.Add) → AutosaveCurrentDocument  (the no-reseed path)
//   * CHARTWRITE-02/03/05/06/07 → ApplyLayout (spawn live charts) → TryWriteLayout  (the chokepoint, hand-crafted oracle)
// Python-FREE: layout capture + scenario READ need no kernel (the File-op mode side-effect is a no-op while
// disconnected; the notebook synthesiser is the FakeMarimoSynthesizer).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ChartUniverseWriteConsistencyE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const float EPS = 1e-3f;
    const string IID = "7203.TSE";          // survivor instrument (present in the resolved universe)
    const string IID_ORPHAN = "9984.TSE";   // orphan instrument (live chart, absent from the resolved universe)
    // distinctive restored geometry (catalog chart defaultSize 520×360 so the Spawn does NOT clamp; far
    // from any default grid slot so a relocate/respawn regression would be unmistakable).
    static readonly Vector2 CHART_TL = new Vector2(812f, -456f);
    const float CHART_W = 520f, CHART_H = 360f;
    static readonly Vector2 ORPHAN_TL = new Vector2(140f, -700f);
    const string GROUP_ID = "grp_0123456789abcdef0123456789abcdef";

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "chart_universe_write_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section1_OnFileSave_UncommittedUniverse_PrunesOrphan()      // CHARTWRITE-01 (real OnFileSave, path ①)
                ?? Section2_DirectWrite_InlineUniverse_KeepsChartGeometry()    // CHARTWRITE-02 (sidecar??inline oracle + geometry)
                ?? Section3_DirectWrite_SubsetUniverse_MixedOrphanSurvivor()   // CHARTWRITE-03 (subset: prune orphan, keep survivor+geom)
                ?? Section4_QuitAutosave_UncommittedUniverse_PrunesOrphan()    // CHARTWRITE-04 (autosave-on-quit, no reseed)
                ?? Section5_DirectWrite_UnreadableSidecar_FailsOpen()         // CHARTWRITE-05 (fail-open: sidecar unreadable)
                ?? Section6_DirectWrite_UnparseableInline_FailsOpen()        // CHARTWRITE-06 (fail-open: inline unparseable)
                ?? Section7_DirectWrite_GroupedOrphan_SurvivorRestoresClean()  // CHARTWRITE-07 (group hygiene)
                ?? Section8_DirectWrite_EmptySidecarUniverse_PrunesOrphan();   // CHARTWRITE-08 (present-but-empty sidecar branch)
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); PlayerPrefs.SetString("backcast.lastDocument", ""); PlayerPrefs.Save(); }

        if (fail == null)
        {
            Debug.Log("[E2E CHART UNIVERSE WRITE PASS] TryWriteLayout の PruneOrphanChartWindowsForPersistence が、保存される "
                    + "<strategy>.json の layout.floatingWindows を「再オープン時に解決される universe (sidecar??inline)」へ整合させる。"
                    + "CHARTWRITE-01 OnFileSave / 03 subset / 04 終了 autosave / 08 present-but-empty sidecar で孤児 chart を disk から落とし、"
                    + "02 inline universe / 07 group は universe 在の chart を復元ジオメトリ保持、05 sidecar unreadable / 06 inline unparseable は fail-open。findings 0099。");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART UNIVERSE WRITE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── CHARTWRITE-01: the path-① core. Open a bare doc (no sidecar scenario key, no inline SCENARIO →
    //   resolved universe = empty), then ADD an instrument to the in-memory universe WITHOUT committing it
    //   (the sidebar-add-then-Save story) — a live chart:7203 spawns via the Universe.Changed sync. Real
    //   OnFileSave writes the .py (still no SCENARIO) and the layout key; the persisted-resolved universe is
    //   still empty, so the prune drops chart:7203 from the layout key. The on-disk doc is consistent (no
    //   orphan). NON-VACUOUS: chart:7203 is asserted LIVE before the save, so the prune removes a real window. ──
    static string Section1_OnFileSave_UncommittedUniverse_PrunesOrphan()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S1: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var onOpen = ty.GetMethod("OnFileOpen", BF);
        var onSave = ty.GetMethod("OnFileSave", BF);
        if (scenario == null || dockWindows == null || chartViews == null) return "S1: root seams not built (renamed? _scenario/_dockWindows/_chartViews)";
        if (onOpen == null || onSave == null) return "S1: OnFileOpen/OnFileSave not found (renamed?)";

        string py = WriteBareDoc("path1");                               // .py = "x = 1" (no SCENARIO), .json = layout-only
        DriveFileOpen(root, ty, onOpen, py);                            // bind _currentLayoutPath + coordinator; universe seeds empty
        if (scenario.Universe.Ids.Count != 0) return "S1: precondition — bare doc must seed an empty universe (got " + scenario.Universe.Ids.Count + ")";

        scenario.Universe.Add(IID);                                     // uncommitted in-memory add → Changed → chart:7203 spawns
        if (!chartViews.Contains(IID) || dockWindows.RectOf(DockShape.ChartId(IID)) == null)
            return "S1 CHARTWRITE-01: positive-control FAILED — the uncommitted add did not spawn a live chart:" + IID + " to prune";

        onSave.Invoke(root, null);                                      // REAL File→Save (writes .py + layout key)

        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null)
            return "S1 CHARTWRITE-01: layout key missing after save (TryWriteLayout failed?)";
        if (doc.FindWindow(DockShape.ChartId(IID)) != null)
            return "S1 CHARTWRITE-01: orphan chart:" + IID + " was BAKED into the persisted layout (universe not persisted → prune missing/ineffective)";
        var scn = TryReadScenarioInstruments(py);                       // the persisted universe must still be empty (we never wrote it)
        if (scn != null && scn.Count != 0)
            return "S1 CHARTWRITE-01: the save unexpectedly persisted the universe (mutate-existing-only / D5 broken?)";
        Debug.Log("[E2E CHARTWRITE-01 PASS] OnFileSave with an uncommitted in-memory add → orphan chart:7203 NOT baked into the persisted layout (resolved universe empty).");
        return null;
    }

    // ── CHARTWRITE-02: the oracle MUST be sidecar??inline, not the sidecar key alone. A doc with NO scenario
    //   sidecar key but an inline .py SCENARIO naming [7203]. The chart:7203 IS in the universe the doc
    //   resolves to on reopen, so the prune must KEEP it WITH its restored geometry. RED if the oracle reads
    //   only the sidecar key (it would see no scenario key → empty → wrongly drop chart:7203, and reseed
    //   would respawn it at a default grid slot = a position-persistence regression). ──
    static string Section2_DirectWrite_InlineUniverse_KeepsChartGeometry()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S2: BackcastWorkspaceRoot missing in scene";
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var tryWriteLayout = ty.GetMethod("TryWriteLayout", BF);
        if (chartViews == null) return "S2: _chartViews not built (renamed?)";
        if (tryWriteLayout == null) return "S2: TryWriteLayout not found (renamed?)";

        string py = WriteInlineDoc("inline", new[] { IID }, new[] { ChartWin(IID, CHART_TL) });   // inline SCENARIO=[7203], NO sidecar key
        // precondition: the oracle must resolve CONFIDENTLY to [7203] via the INLINE branch (no sidecar key).
        // Without this, a kept verdict could come from FAIL-OPEN (if the inline literal ever drifted to
        // Unparseable/Absent) for the WRONG reason — silently masking the very oracle regression litmus (b)
        // (narrowing the oracle to the sidecar key alone) is meant to catch.
        if (ScenarioSidecarStore.TryReadScenario(py, out var scPre) && scPre != null)
            return "S2 CHARTWRITE-02: precondition — fixture unexpectedly carries a sidecar scenario key (would not exercise the inline oracle branch)";
        ScenarioSnapshot inlinePre = ScenarioInlineReader.Read(py, out ScenarioReadStatus stPre);
        if (stPre == ScenarioReadStatus.Unparseable || inlinePre == null || !inlinePre.Instruments.Contains(IID))
            return "S2 CHARTWRITE-02: precondition — inline SCENARIO did not confidently resolve to [" + IID + "] (status=" + stPre + ") — a kept chart would be a fail-open false pass";
        var pc = SpawnLiveFromLayout(root, ty, chartViews, py, IID, "S2 CHARTWRITE-02");           // RestoreFloating spawns the live chart
        if (pc != null) return pc;

        if (!(bool)tryWriteLayout.Invoke(root, new object[] { py })) return "S2 CHARTWRITE-02: TryWriteLayout returned false";

        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null) return "S2 CHARTWRITE-02: layout key did not round-trip";
        var w = doc.FindWindow(DockShape.ChartId(IID));
        if (w == null)
            return "S2 CHARTWRITE-02: chart:" + IID + " was WRONGLY pruned — its instrument IS in the inline .py SCENARIO universe (oracle narrowed to the sidecar key only?)";
        if (!RectMatches(w))
            return "S2 CHARTWRITE-02: restored chart geometry NOT preserved (got " + w.x + "," + w.y + "," + w.w + "," + w.h + " want " + CHART_TL.x + "," + CHART_TL.y + "," + CHART_W + "," + CHART_H + ")";
        Debug.Log("[E2E CHARTWRITE-02 PASS] inline .py SCENARIO universe=[7203] (no sidecar key) → chart:7203 KEPT with restored geometry (sidecar??inline oracle).");
        return null;
    }

    // ── CHARTWRITE-03: subset — sidecar universe=[7203], layout carries chart:7203 (survivor) + chart:9984
    //   (orphan). The prune drops 9984 (absent from the universe) and keeps 7203 WITH its restored geometry
    //   (proves "prune the orphan only", never a despawn-all + respawn that would relocate the survivor). ──
    static string Section3_DirectWrite_SubsetUniverse_MixedOrphanSurvivor()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S3: BackcastWorkspaceRoot missing in scene";
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var tryWriteLayout = ty.GetMethod("TryWriteLayout", BF);
        if (chartViews == null) return "S3: _chartViews not built (renamed?)";
        if (tryWriteLayout == null) return "S3: TryWriteLayout not found (renamed?)";

        string py = WriteSidecarDoc("subset", new List<string> { IID },
            new[] { ChartWin(IID, CHART_TL), ChartWin(IID_ORPHAN, ORPHAN_TL) });   // universe=[7203], layout has BOTH
        var pc = SpawnLiveFromLayout(root, ty, chartViews, py, IID, "S3 CHARTWRITE-03");
        if (pc != null) return pc;
        if (!chartViews.Contains(IID_ORPHAN)) return "S3 CHARTWRITE-03: positive-control FAILED — orphan chart:" + IID_ORPHAN + " did not spawn from the layout entry";

        if (!(bool)tryWriteLayout.Invoke(root, new object[] { py })) return "S3 CHARTWRITE-03: TryWriteLayout returned false";

        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null) return "S3 CHARTWRITE-03: layout key did not round-trip";
        if (doc.FindWindow(DockShape.ChartId(IID_ORPHAN)) != null)
            return "S3 CHARTWRITE-03: orphan chart:" + IID_ORPHAN + " (absent from universe) survived in the persisted layout";
        var w = doc.FindWindow(DockShape.ChartId(IID));
        if (w == null) return "S3 CHARTWRITE-03: survivor chart:" + IID + " was wrongly pruned (over-prune)";
        if (!RectMatches(w))
            return "S3 CHARTWRITE-03: survivor geometry NOT preserved (got " + w.x + "," + w.y + "," + w.w + "," + w.h + " — despawn+respawn instead of prune-orphan-only?)";
        Debug.Log("[E2E CHARTWRITE-03 PASS] subset universe=[7203], layout chart:7203+chart:9984 → 9984 pruned, 7203 kept with restored geometry.");
        return null;
    }

    // ── CHARTWRITE-04: the quit-autosave path. AutosaveCurrentDocument calls TryWriteLayout with NO
    //   _coordinator.Save and NO ReseedFromEditor — so the restore-side / reseed-tail self-heal can NOT cover
    //   it; only the write-side prune does. Same uncommitted-add setup as CHARTWRITE-01, then drive autosave. ──
    static string Section4_QuitAutosave_UncommittedUniverse_PrunesOrphan()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S4: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var onOpen = ty.GetMethod("OnFileOpen", BF);
        var autosave = ty.GetMethod("AutosaveCurrentDocument", BF);
        if (scenario == null || dockWindows == null || chartViews == null) return "S4: root seams not built (renamed?)";
        if (onOpen == null || autosave == null) return "S4: OnFileOpen/AutosaveCurrentDocument not found (renamed?)";

        string py = WriteBareDoc("autosave");
        DriveFileOpen(root, ty, onOpen, py);
        if (scenario.Universe.Ids.Count != 0) return "S4: precondition — bare doc must seed an empty universe";

        scenario.Universe.Add(IID);
        if (!chartViews.Contains(IID) || dockWindows.RectOf(DockShape.ChartId(IID)) == null)
            return "S4 CHARTWRITE-04: positive-control FAILED — uncommitted add did not spawn a live chart:" + IID;

        autosave.Invoke(root, null);                                    // REAL quit autosave (TryWriteLayout only; no reseed)

        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null)
            return "S4 CHARTWRITE-04: layout key missing after autosave";
        if (doc.FindWindow(DockShape.ChartId(IID)) != null)
            return "S4 CHARTWRITE-04: orphan chart:" + IID + " was BAKED into the autosaved layout (reseed can't cover quit — write-side prune missing)";
        Debug.Log("[E2E CHARTWRITE-04 PASS] quit AutosaveCurrentDocument with an uncommitted add → orphan chart:7203 NOT baked into the autosaved layout.");
        return null;
    }

    // ── CHARTWRITE-05: FAIL-OPEN when the sidecar is UNREADABLE. The scenario key is structurally malformed
    //   ("start" is an object) so ScenarioSidecarStore.TryReadScenario returns FALSE (the universe is UNKNOWN,
    //   not empty). The prune must KEEP chart:7203 — a transient read failure must not nuke restored geometry;
    //   #123's restore-side sync is the backstop on reopen. RED if the prune fails CLOSED (treats unreadable
    //   as empty and drops the chart). The layout key still writes (the JSON itself parses). ──
    static string Section5_DirectWrite_UnreadableSidecar_FailsOpen()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S5: BackcastWorkspaceRoot missing in scene";
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var tryWriteLayout = ty.GetMethod("TryWriteLayout", BF);
        if (chartViews == null) return "S5: _chartViews not built (renamed?)";
        if (tryWriteLayout == null) return "S5: TryWriteLayout not found (renamed?)";

        string py = WriteUnreadableScenarioDoc("corrupt", new[] { ChartWin(IID, CHART_TL) });
        // sanity: the fixture really IS unreadable (TryReadScenario false) — else the fail-open path is vacuous.
        if (ScenarioSidecarStore.TryReadScenario(py, out _))
            return "S5 CHARTWRITE-05: precondition — the malformed scenario fixture unexpectedly read OK (not exercising fail-open)";
        var pc = SpawnLiveFromLayout(root, ty, chartViews, py, IID, "S5 CHARTWRITE-05");
        if (pc != null) return pc;

        if (!(bool)tryWriteLayout.Invoke(root, new object[] { py })) return "S5 CHARTWRITE-05: TryWriteLayout returned false (corrupt scenario should not block the layout write)";

        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null) return "S5 CHARTWRITE-05: layout key did not round-trip";
        if (doc.FindWindow(DockShape.ChartId(IID)) == null)
            return "S5 CHARTWRITE-05: chart:" + IID + " was pruned on an UNREADABLE sidecar (must fail OPEN — #123 backstops on reopen)";
        Debug.Log("[E2E CHARTWRITE-05 PASS] unreadable scenario sidecar → fail-open: chart:7203 KEPT (no geometry nuke on a transient read failure).");
        return null;
    }

    // ── CHARTWRITE-06: FAIL-OPEN when the inline .py SCENARIO is present-but-UNPARSEABLE (no sidecar key, an
    //   unbalanced SCENARIO dict literal → ScenarioInlineReader.Read status == Unparseable). Same fail-open
    //   contract as CHARTWRITE-05 but on the inline branch. RED if the prune treats unparseable as empty. ──
    static string Section6_DirectWrite_UnparseableInline_FailsOpen()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S6: BackcastWorkspaceRoot missing in scene";
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var tryWriteLayout = ty.GetMethod("TryWriteLayout", BF);
        if (chartViews == null) return "S6: _chartViews not built (renamed?)";
        if (tryWriteLayout == null) return "S6: TryWriteLayout not found (renamed?)";

        string py = WriteUnparseableInlineDoc("malformed_inline", new[] { ChartWin(IID, CHART_TL) });
        // sanity: no sidecar key AND inline reads Unparseable (the only way this section is non-vacuous).
        if (ScenarioSidecarStore.TryReadScenario(py, out var snap) && snap != null)
            return "S6 CHARTWRITE-06: precondition — fixture unexpectedly has a readable scenario sidecar key";
        ScenarioInlineReader.Read(py, out var status);
        if (status != ScenarioReadStatus.Unparseable)
            return "S6 CHARTWRITE-06: precondition — inline SCENARIO did not read as Unparseable (got " + status + ")";
        var pc = SpawnLiveFromLayout(root, ty, chartViews, py, IID, "S6 CHARTWRITE-06");
        if (pc != null) return pc;

        if (!(bool)tryWriteLayout.Invoke(root, new object[] { py })) return "S6 CHARTWRITE-06: TryWriteLayout returned false";

        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null) return "S6 CHARTWRITE-06: layout key did not round-trip";
        if (doc.FindWindow(DockShape.ChartId(IID)) == null)
            return "S6 CHARTWRITE-06: chart:" + IID + " was pruned on an UNPARSEABLE inline SCENARIO (must fail OPEN)";
        Debug.Log("[E2E CHARTWRITE-06 PASS] unparseable inline .py SCENARIO → fail-open: chart:7203 KEPT.");
        return null;
    }

    // ── CHARTWRITE-07: group hygiene. The layout carries a GROUP of two charts (survivor 7203 + orphan 9984)
    //   sharing one groupId; the universe = [7203]. The prune drops 9984, leaving the survivor with a now-
    //   dangling 1-member group on disk. On a FRESH reopen, RestoreFloating restores chart:7203 at its
    //   geometry and DissolveIfShrunkTo collapses the singleton group — no phantom group, no attempt to
    //   restore the pruned 9984. Proves "a grouped orphan dropped → the survivor is not mis-restored". ──
    static string Section7_DirectWrite_GroupedOrphan_SurvivorRestoresClean()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S7: BackcastWorkspaceRoot missing in scene";
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var tryWriteLayout = ty.GetMethod("TryWriteLayout", BF);
        if (chartViews == null || dockWindows == null) return "S7: root seams not built (renamed?)";
        if (tryWriteLayout == null) return "S7: TryWriteLayout not found (renamed?)";

        string py = WriteSidecarDoc("group", new List<string> { IID }, new[]
        {
            ChartWinGrouped(IID, CHART_TL, GROUP_ID),
            ChartWinGrouped(IID_ORPHAN, ORPHAN_TL, GROUP_ID),
        });
        var pc = SpawnLiveFromLayout(root, ty, chartViews, py, IID, "S7 CHARTWRITE-07");
        if (pc != null) return pc;
        if (!chartViews.Contains(IID_ORPHAN)) return "S7 CHARTWRITE-07: positive-control FAILED — grouped orphan chart:" + IID_ORPHAN + " did not spawn";

        if (!(bool)tryWriteLayout.Invoke(root, new object[] { py })) return "S7 CHARTWRITE-07: TryWriteLayout returned false";

        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null) return "S7 CHARTWRITE-07: layout key did not round-trip";
        if (doc.FindWindow(DockShape.ChartId(IID_ORPHAN)) != null)
            return "S7 CHARTWRITE-07: grouped orphan chart:" + IID_ORPHAN + " survived in the persisted layout";
        var survivor = doc.FindWindow(DockShape.ChartId(IID));
        if (survivor == null) return "S7 CHARTWRITE-07: grouped survivor chart:" + IID + " was wrongly pruned";

        // REOPEN on a FRESH root: RestoreFloating must restore the survivor at geometry and dissolve the now-
        // singleton group (no phantom restore of the pruned orphan).
        var root2 = ComposeRoot(out var ty2);
        if (root2 == null) return "S7 CHARTWRITE-07: second compose failed";
        var chartViews2 = ty2.GetField("_chartViews", BF)?.GetValue(root2) as IDictionary;
        var dockWindows2 = ty2.GetField("_dockWindows", BF)?.GetValue(root2) as FloatingWindowController;
        var applyLayout2 = ty2.GetMethod("ApplyLayout", BF);
        if (chartViews2 == null || dockWindows2 == null || applyLayout2 == null) return "S7 CHARTWRITE-07: reopen seams missing";
        applyLayout2.Invoke(root2, new object[] { doc });

        if (chartViews2.Contains(IID_ORPHAN) || dockWindows2.RectOf(DockShape.ChartId(IID_ORPHAN)) != null)
            return "S7 CHARTWRITE-07: pruned orphan chart:" + IID_ORPHAN + " was phantom-restored on reopen";
        if (!chartViews2.Contains(IID) || dockWindows2.RectOf(DockShape.ChartId(IID)) == null)
            return "S7 CHARTWRITE-07: survivor chart:" + IID + " did not restore on reopen";
        var rt = dockWindows2.RectOf(DockShape.ChartId(IID));
        if (Mathf.Abs(rt.anchoredPosition.x - CHART_TL.x) > EPS || Mathf.Abs(rt.anchoredPosition.y - CHART_TL.y) > EPS)
            return "S7 CHARTWRITE-07: survivor restored at the WRONG position (got " + rt.anchoredPosition + " want " + CHART_TL + ")";
        if (!string.IsNullOrEmpty(dockWindows2.GroupIdOf(DockShape.ChartId(IID))))
            return "S7 CHARTWRITE-07: survivor kept a phantom group (singleton should dissolve, got groupId='" + dockWindows2.GroupIdOf(DockShape.ChartId(IID)) + "')";
        Debug.Log("[E2E CHARTWRITE-07 PASS] grouped orphan chart:9984 pruned → survivor chart:7203 restores at geometry as a singleton (group dissolved, no phantom).");
        return null;
    }

    // ── CHARTWRITE-08: confident-empty via a PRESENT-but-EMPTY sidecar scenario key — the `sidecar != null`
    //   branch of TryResolvePersistedUniverse, DISTINCT from CHARTWRITE-01/04's inline-absent empty (which
    //   reaches confident-empty via the inline fallback). Here the scenario sidecar key EXISTS with
    //   instruments:[] → resolved universe is a confident EMPTY set → chart:7203 is a true orphan to prune.
    //   Guards the empty-instruments-list branch: a regression that mishandled an empty list (e.g. treated
    //   `sidecar.Instruments.Count == 0` as "keep all" / fail-open) would pass every OTHER cell. ──
    static string Section8_DirectWrite_EmptySidecarUniverse_PrunesOrphan()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S8: BackcastWorkspaceRoot missing in scene";
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var tryWriteLayout = ty.GetMethod("TryWriteLayout", BF);
        if (chartViews == null) return "S8: _chartViews not built (renamed?)";
        if (tryWriteLayout == null) return "S8: TryWriteLayout not found (renamed?)";

        string py = WriteSidecarDoc("emptyuniverse", new List<string>(), new[] { ChartWin(IID, CHART_TL) });
        // precondition: the sidecar key is PRESENT and resolves to a non-null EMPTY instruments list — the
        // `sidecar != null` confident-empty branch, NOT the inline-absent path 01/04 take.
        if (!ScenarioSidecarStore.TryReadScenario(py, out var snap) || snap == null)
            return "S8 CHARTWRITE-08: precondition — sidecar scenario key must be PRESENT and readable (empty-universe branch)";
        if (snap.Instruments.Count != 0)
            return "S8 CHARTWRITE-08: precondition — sidecar universe must be EMPTY (got " + snap.Instruments.Count + ")";
        var pc = SpawnLiveFromLayout(root, ty, chartViews, py, IID, "S8 CHARTWRITE-08");
        if (pc != null) return pc;

        if (!(bool)tryWriteLayout.Invoke(root, new object[] { py })) return "S8 CHARTWRITE-08: TryWriteLayout returned false";

        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null) return "S8 CHARTWRITE-08: layout key did not round-trip";
        if (doc.FindWindow(DockShape.ChartId(IID)) != null)
            return "S8 CHARTWRITE-08: orphan chart:" + IID + " survived under a PRESENT-but-EMPTY sidecar universe (empty-instruments branch mishandled — e.g. wrongly kept?)";
        Debug.Log("[E2E CHARTWRITE-08 PASS] present-but-empty sidecar universe (instruments:[]) → orphan chart:7203 pruned (sidecar!=null confident-empty branch).");
        return null;
    }

    // ---- fixture writers ----

    // .py = "x = 1" (NO inline SCENARIO), .json = layout-only (NO scenario key). Resolved universe = absent
    // (a CONFIDENT empty universe). `wins` defaults to none.
    static string WriteBareDoc(string name, FloatingWindowLayout[] wins = null)
    {
        string py = NewPy(name);
        WriteLayoutKey(py, wins ?? Array.Empty<FloatingWindowLayout>());
        if (ScenarioSidecarStore.TryReadScenario(py, out var snap) && snap != null)
            throw new Exception("WriteBareDoc(" + name + "): fixture unexpectedly carries a scenario sidecar key");
        return py;
    }

    // .py with an inline module-level SCENARIO naming `inlineInstruments`, .json = layout-only (NO scenario
    // key) → resolved universe falls back to the inline SCENARIO.
    static string WriteInlineDoc(string name, string[] inlineInstruments, FloatingWindowLayout[] wins)
    {
        string instrs = string.Join(", ", inlineInstruments.Select(s => "\"" + s + "\""));
        string py = Path.Combine(TempRoot, name, name + ".py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py,
            "SCENARIO = {\n" +
            "    \"start\": \"2025-01-06\",\n" +
            "    \"end\": \"2025-01-10\",\n" +
            "    \"granularity\": \"Daily\",\n" +
            "    \"initial_cash\": 1000000,\n" +
            "    \"instruments\": [" + instrs + "],\n" +
            "}\n" +
            "x = 1\n");
        WriteLayoutKey(py, wins);
        return py;
    }

    // .py = "x = 1", .json = sidecar scenario key (universe = `instruments`) + layout key. Mirrors the
    // ChartUniverseSyncE2ERunner fixture: SetStartupParamsAndInstruments creates the complete scenario, then
    // LayoutSidecarStore.WriteLayout merge-splices the layout key (the scenario key survives, ADR-0005).
    static string WriteSidecarDoc(string name, List<string> instruments, FloatingWindowLayout[] wins)
    {
        string py = NewPy(name);
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            py, new StartupParamsForWrite("2025-01-06", "2025-01-10", "Daily", "1000000"), instruments);
        WriteLayoutKey(py, wins);
        return py;
    }

    // .py = "x = 1", .json = a STRUCTURALLY-MALFORMED scenario key ("start" is an object → ReadScenario's
    // (string) cast throws → TryReadScenario returns false) + layout key. The JSON itself is valid so the
    // layout write (LayoutSidecarStore.WriteLayout → JObject.Parse) still succeeds.
    static string WriteUnreadableScenarioDoc(string name, FloatingWindowLayout[] wins)
    {
        string py = NewPy(name);
        string json = SidecarPath(py);
        var root = new JObject
        {
            ["scenario"] = new JObject
            {
                ["start"] = new JObject(),               // malformed: must be a string
                ["end"] = "2025-01-10",
                ["granularity"] = "Daily",
                ["initial_cash"] = 1000000,
                ["instruments"] = new JArray { IID },
            },
        };
        File.WriteAllText(json, root.ToString());
        WriteLayoutKey(py, wins);                          // merge the layout key (scenario key survives verbatim)
        return py;
    }

    // .py with an inline SCENARIO whose dict literal is UNBALANCED (truncated) → ScenarioInlineReader.Read
    // status == Unparseable. .json = layout-only (NO scenario key).
    static string WriteUnparseableInlineDoc(string name, FloatingWindowLayout[] wins)
    {
        string py = Path.Combine(TempRoot, name, name + ".py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "SCENARIO = {\n    \"instruments\": [\"7203.TSE\"\n");   // missing closing ] and }
        WriteLayoutKey(py, wins);
        return py;
    }

    static string NewPy(string name)
    {
        string py = Path.Combine(TempRoot, name, name + ".py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "x = 1\n");
        return py;
    }

    static string SidecarPath(string py) => Path.ChangeExtension(py, ".json");

    // splice the layout key (Newtonsoft merge preserves any scenario key) and verify the chart entries
    // round-trip (an unknown kind / non-finite rect / duplicate id would be dropped by load normalization,
    // making the prune/keep assertions vacuous).
    static void WriteLayoutKey(string py, FloatingWindowLayout[] wins)
    {
        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            hakoniwaProfiles = null,
            canvasView = null,
            floatingWindows = new List<FloatingWindowLayout>(wins),
            strategyEditors = new List<StrategyEditorState>(),
            cellPositions = new List<CellPosition>(),
        };
        LayoutSidecarStore.WriteLayout(py, doc);
        if (!LayoutSidecarStore.TryReadLayout(py, out var readBack) || readBack == null)
            throw new Exception("WriteLayoutKey(" + py + "): layout key did not round-trip");
        foreach (var w in wins)
            if (readBack.FindWindow(w.id) == null)
                throw new Exception("WriteLayoutKey(" + py + "): entry '" + w.id + "' dropped by layout normalization on round-trip");
    }

    // ---- helpers ----

    static FloatingWindowLayout ChartWin(string iid, Vector2 topLeft) =>
        new FloatingWindowLayout(DockShape.ChartId(iid), FloatingWindowCatalog.KIND_CHART,
            topLeft.x, topLeft.y, CHART_W, CHART_H, 0, true);

    static FloatingWindowLayout ChartWinGrouped(string iid, Vector2 topLeft, string groupId) =>
        new FloatingWindowLayout(DockShape.ChartId(iid), FloatingWindowCatalog.KIND_CHART,
            topLeft.x, topLeft.y, CHART_W, CHART_H, 0, true, groupId);

    static bool RectMatches(FloatingWindowLayout w) =>
        Mathf.Abs(w.x - CHART_TL.x) <= EPS && Mathf.Abs(w.y - CHART_TL.y) <= EPS
        && Mathf.Abs(w.w - CHART_W) <= EPS && Mathf.Abs(w.h - CHART_H) <= EPS;

    static List<string> TryReadScenarioInstruments(string py)
        => ScenarioSidecarStore.TryReadScenario(py, out var snap) && snap != null ? snap.Instruments : null;

    // Spawn the live chart(s) via the REAL production restore path (ApplyLayout → RestoreFloating), the same
    // #123 "second spawn path" that bakes orphans. Returns an error string if the chart did not actually go
    // live (so the subsequent prune/keep assertions are non-vacuous).
    static string SpawnLiveFromLayout(BackcastWorkspaceRoot root, Type ty, IDictionary chartViews, string py, string iid, string tag)
    {
        var applyLayout = ty.GetMethod("ApplyLayout", BF);
        if (applyLayout == null) return tag + ": ApplyLayout not found (renamed?)";
        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null) return tag + ": could not read the fixture layout back from " + py;
        applyLayout.Invoke(root, new object[] { doc });
        if (!chartViews.Contains(iid))
            return tag + ": positive-control FAILED — RestoreFloating did not spawn chart:" + iid + " from the layout entry (keep/prune assertions would be vacuous)";
        return null;
    }

    static void DriveFileOpen(BackcastWorkspaceRoot root, Type ty, MethodInfo onOpen, string py)
    {
        root.SetFileDialog(new StubFileDialog { NextResult = py });
        onOpen.Invoke(root, null);
        var bound = ty.GetField("_currentLayoutPath", BF)?.GetValue(root) as string;
        if (string.IsNullOrEmpty(bound) ||
            !string.Equals(Path.GetFullPath(bound), Path.GetFullPath(py), StringComparison.OrdinalIgnoreCase))
            throw new Exception("File→Open did not bind _currentLayoutPath to '" + py + "' (got '" + bound + "')");
    }

    // Edit-mode compose (mirrors ChartUniverseSyncE2ERunner): OpenScene does NOT run Awake, so we drive
    // ResolvePaths + BuildWorkspace by hand; no boot resume auto-runs.
    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
