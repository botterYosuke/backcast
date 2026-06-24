// ChartOrphanSyncJourneyE2ERunner.cs — issue #123 release-gate (台本: same-dir
// ChartOrphanSyncJourneyE2ERunner.md, design tree: docs/findings/0095).
//
// THE BUG: RestoreFloating (ApplyLayout) spawns a saved `chart:<iid>` window from the layout
// sidecar's floatingWindows list WITHOUT a universe check — a SECOND spawn path beside the
// Changed-driven SyncChartWindowsToUniverse. When the just-seeded universe ends up set-EQUAL to
// the prior one (empty→empty; or Editable=false / instruments_ref lock making ReplaceAll a no-op),
// InstrumentRegistry.ReplaceAll's SequenceEqual short-circuit does NOT fire Changed, so the
// Changed-subscribed Sync never runs and the restored chart floats on as an ORPHAN while the
// sidebar reads "No instruments".
//
// THE FIX (BackcastWorkspaceRoot.ReseedFromEditor tail): call SyncChartWindowsToUniverse()
// UNCONDITIONALLY (Changed-INDEPENDENT) at the canonical reseed tail shared by ApplyLayout/Resume/
// File→Open/Save, so the universe-is-SoT contract is re-asserted on EVERY reseed entry (structural,
// not point-fix). Idempotent + geometry-preserving: an iid that IS in the universe is a no-op
// (keeps its restored x/y/w/h); only true orphans are despawned.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartOrphanSyncJourneyE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART ORPHAN SYNC JOURNEY PASS] + per-id [E2E CHART-ORPHAN-0N PASS] / exit=0
//   # 確認は Bash `grep -a "E2E CHART-ORPHAN"`. compile-only ゲート: -executeMethod を外して error CS\d+ 0 件。
//
// RED→GREEN litmus: delete the `SyncChartWindowsToUniverse();` line at ReseedFromEditor's tail →
// CHART-ORPHAN-01 / 03 / 04 go RED (the no-Changed cells leave the orphan live). 02 is a
// geometry/no-regression guard (the universe-grows-from-empty path fires Changed either way).
//
// SECTIONS (findings 0095 §gate):
//   01 — empty universe via File→Open: saved chart:X orphan despawned, universe empty (litmus)
//   02 — universe=[X] via File→Open: chart:X KEPT at saved geometry (no-regression / position persist)
//   03 — empty universe via REAL ResumeLastDocumentOrDefault (boot resume entry) (litmus)
//   04 — Editable=false (instruments_ref lock) no-op, mixed survivor+orphan: orphan despawned,
//        survivor kept at geometry (litmus + subset cell + Editable=false cell)

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ChartOrphanSyncJourneyE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const float EPS = 1e-3f;

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "chart_orphan_sync_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section01_EmptyUniverseFileOpen()
                ?? Section02_UniverseHeldGeometryPreserved()
                ?? Section03_EmptyUniverseBootResume()
                ?? Section04_LockedNoOpMixedSurvivorOrphan();
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
        {
            Debug.Log("[E2E CHART ORPHAN SYNC JOURNEY PASS] issue #123 — orphan chart-on-empty-universe sealed: " +
                      "File→Open empty (01) / held-geometry (02) / boot-resume (03) / locked-no-op mixed (04). findings 0095.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART ORPHAN SYNC JOURNEY FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // =====================================================================================================
    // 01 — empty universe via File→Open. A layout sidecar carries chart:7203 in floatingWindows; the
    // scenario sidecar is ABSENT (no inline scenario in the .py), so SeedScenarioFromEditor seeds the
    // EMPTY universe. ReplaceAll([]) over the already-empty registry SequenceEqual-short-circuits → no
    // Changed → the Changed-subscribed Sync never runs. WITHOUT the ReseedFromEditor-tail fix the chart
    // floats on as an orphan; WITH it, the tail Sync despawns it. (delete-the-logic litmus cell)
    // =====================================================================================================
    static string Section01_EmptyUniverseFileOpen()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "CHART-ORPHAN-01: root missing";
        var dockWindows = DockWindowsOf(root, ty);
        var scenario = ScenarioOf(root, ty);

        string py = Path.Combine(TempRoot, "s01_empty", "doc.py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "x = 1\n");   // no inline scenario → empty universe on seed

        // layout sidecar: a saved per-instrument chart with NO matching universe member.
        WriteLayoutWithFloatingWindows(py, new List<FloatingWindowLayout> {
            ChartWin("7203.TSE", -1400f, 700f, 0),
        });

        root.SetFileDialog(new StubFileDialog { NextResult = py });
        InvokeOnFileOpen(ty, root);

        // universe must be empty (sidebar would read "No instruments")…
        if (scenario.Universe.Count != 0)
            return "CHART-ORPHAN-01: universe not empty after open (count=" + scenario.Universe.Count + ")";
        // …and the orphan chart must be GONE (the bug: it survives because ReplaceAll([]) fired no Changed).
        if (dockWindows.Has(DockShape.ChartId("7203.TSE")))
            return "CHART-ORPHAN-01: orphan chart:7203.TSE survived an EMPTY universe (Changed-independent Sync missing at reseed tail)";

        Debug.Log("[E2E CHART-ORPHAN-01 PASS] empty-universe File→Open despawns the restored orphan chart.");
        return null;
    }

    // =====================================================================================================
    // 02 — universe=[7203] via File→Open. The scenario sidecar seeds [7203]; the layout sidecar restores
    // chart:7203 at distinct, non-grid coords. ReplaceAll(empty→[7203]) DOES change the set, so this path
    // fires Changed — the point of this cell is the NO-REGRESSION guarantee: the in-universe chart must
    // SURVIVE and KEEP its restored geometry (the tail Sync must be a no-op for it, not a respawn). Saved
    // coords are far from the grid anchor (-600,-332) so a stray respawn would be caught.
    // =====================================================================================================
    static string Section02_UniverseHeldGeometryPreserved()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "CHART-ORPHAN-02: root missing";
        var dockWindows = DockWindowsOf(root, ty);
        var scenario = ScenarioOf(root, ty);

        string py = Path.Combine(TempRoot, "s02_held", "doc.py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "y = 2\n");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(py,
            new StartupParamsForWrite("2025-01-06", "2025-01-10", "Daily", "1000000"),
            new List<string> { "7203.TSE" });

        var savedPos = new Vector2(-1750f, 920f);   // distinct, non-grid
        WriteLayoutWithFloatingWindows(py, new List<FloatingWindowLayout> {
            ChartWin("7203.TSE", savedPos.x, savedPos.y, 0),
        });

        root.SetFileDialog(new StubFileDialog { NextResult = py });
        InvokeOnFileOpen(ty, root);

        if (scenario.Universe.Count != 1 || scenario.Universe.Ids[0] != "7203.TSE")
            return "CHART-ORPHAN-02: universe not [7203.TSE] after open";
        var rt = dockWindows.RectOf(DockShape.ChartId("7203.TSE"));
        if (rt == null)
            return "CHART-ORPHAN-02: in-universe chart:7203.TSE was despawned (false-positive orphan kill)";
        if (!Approx2(rt.anchoredPosition, savedPos))
            return "CHART-ORPHAN-02: restored geometry NOT preserved (got " + rt.anchoredPosition +
                   ", want " + savedPos + ") — tail Sync respawned instead of no-op";

        Debug.Log("[E2E CHART-ORPHAN-02 PASS] in-universe chart survives + keeps restored geometry (no regression).");
        return null;
    }

    // =====================================================================================================
    // 03 — empty universe via the REAL ResumeLastDocumentOrDefault (boot-resume entry, not File→Open).
    // Proves the fix covers EVERY reseed entry, not just File→Open: the AC names ResumeLastDocumentOrDefault
    // explicitly. Set the PlayerPrefs resume pointer to the doc, then drive the real method by reflection.
    // Same orphan condition as 01 (empty universe + saved chart) — litmus cell on the resume entry.
    // =====================================================================================================
    static string Section03_EmptyUniverseBootResume()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "CHART-ORPHAN-03: root missing";
        var dockWindows = DockWindowsOf(root, ty);
        var scenario = ScenarioOf(root, ty);

        string py = Path.Combine(TempRoot, "s03_resume", "doc.py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "z = 3\n");   // no inline scenario → empty universe on seed
        WriteLayoutWithFloatingWindows(py, new List<FloatingWindowLayout> {
            ChartWin("6758.TSE", -1200f, 640f, 0),
        });

        const string ResumeKey = "backcast.lastDocument";   // BackcastWorkspaceRoot.cs:177
        string prevResume = PlayerPrefs.GetString(ResumeKey, "");
        try
        {
            PlayerPrefs.SetString(ResumeKey, py);
            ty.GetMethod("ResumeLastDocumentOrDefault", BF).Invoke(root, null);
        }
        finally
        {
            PlayerPrefs.SetString(ResumeKey, prevResume);   // don't leak the pointer to the real app
        }

        if (scenario.Universe.Count != 0)
            return "CHART-ORPHAN-03: universe not empty after resume (count=" + scenario.Universe.Count + ")";
        if (dockWindows.Has(DockShape.ChartId("6758.TSE")))
            return "CHART-ORPHAN-03: orphan chart:6758.TSE survived boot-resume into an EMPTY universe";

        Debug.Log("[E2E CHART-ORPHAN-03 PASS] boot-resume (ResumeLastDocumentOrDefault) despawns the restored orphan.");
        return null;
    }

    // =====================================================================================================
    // 04 — Editable=false (instruments_ref lock) makes ReplaceAll a NO-OP, mixed survivor+orphan. The
    // registry is locked to [7203] (Editable=false), so SeedScenarioFromEditor's ReplaceAll returns early
    // at the Editable gate (no Changed). The layout restores BOTH chart:7203 (in the locked set → survivor)
    // and chart:6758 (NOT in the set → orphan). The tail Sync must despawn ONLY 6758 and keep 7203 at its
    // restored geometry. This is the single cell that exercises the Editable-gate no-op AND the
    // orphan+survivor mix at once — both depend on the Changed-independent tail Sync (litmus).
    //
    // NOTE (findings 0095 §matrix): no PRODUCTION path currently sets Universe.Editable=false (the
    // `instruments_ref` external-reference lock is documented in InstrumentRegistry.cs but unwired — grep
    // `.Editable =` is empty outside the default). This cell is a FORWARD-DEFENSE characterization of the
    // latent `if (!Editable) return false` guard at ReplaceAll:85: the precondition (locked registry) is
    // injected; the logic under test (ReseedFromEditor→tail Sync over the real File→Open path) is production.
    // =====================================================================================================
    static string Section04_LockedNoOpMixedSurvivorOrphan()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "CHART-ORPHAN-04: root missing";
        var dockWindows = DockWindowsOf(root, ty);
        var scenario = ScenarioOf(root, ty);
        var universe = scenario.Universe;

        // Inject the locked precondition: registry held to [7203.TSE] by an external ref, user-uneditable.
        // Seed _ids directly (bypassing ReplaceAll, which would itself no-op under Editable=false) so the
        // lock holds a non-empty set, then lower the Editable gate.
        SeedRegistryIdsDirect(universe, new[] { "7203.TSE" });
        universe.Editable = false;

        string py = Path.Combine(TempRoot, "s04_locked", "doc.py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "w = 4\n");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(py,
            new StartupParamsForWrite("2025-02-03", "2025-02-07", "Daily", "1000000"),
            new List<string> { "7203.TSE" });   // ignored on seed (Editable=false), kept for narrative coherence

        var survivorPos = new Vector2(-1600f, 880f);   // distinct, non-grid
        WriteLayoutWithFloatingWindows(py, new List<FloatingWindowLayout> {
            ChartWin("7203.TSE", survivorPos.x, survivorPos.y, 0),   // survivor (in locked set)
            ChartWin("6758.TSE", -300f, 200f, 1),                    // orphan  (NOT in locked set)
        });

        root.SetFileDialog(new StubFileDialog { NextResult = py });
        InvokeOnFileOpen(ty, root);

        // Lock must have held (ReplaceAll no-op): universe is still exactly [7203].
        if (universe.Count != 1 || universe.Ids[0] != "7203.TSE")
            return "CHART-ORPHAN-04: locked universe mutated (Editable gate bypassed?) — got count=" + universe.Count;
        // Orphan (6758, not in the locked set) must be despawned by the Changed-independent tail Sync.
        if (dockWindows.Has(DockShape.ChartId("6758.TSE")))
            return "CHART-ORPHAN-04: orphan chart:6758.TSE survived under a locked (no-Changed) universe";
        // Survivor (7203, in the locked set) must remain AND keep its restored geometry.
        var rt = dockWindows.RectOf(DockShape.ChartId("7203.TSE"));
        if (rt == null)
            return "CHART-ORPHAN-04: survivor chart:7203.TSE was wrongly despawned";
        if (!Approx2(rt.anchoredPosition, survivorPos))
            return "CHART-ORPHAN-04: survivor geometry NOT preserved (got " + rt.anchoredPosition +
                   ", want " + survivorPos + ")";

        Debug.Log("[E2E CHART-ORPHAN-04 PASS] locked no-op reseed despawns the orphan, keeps the in-set survivor's geometry.");
        return null;
    }

    // ---- root composition (mirrors ChartPlacementJourneyE2ERunner.ComposeRoot) ----
    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindAnyObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }

    static FloatingWindowController DockWindowsOf(BackcastWorkspaceRoot root, Type ty) =>
        ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;

    static ScenarioStartupController ScenarioOf(BackcastWorkspaceRoot root, Type ty) =>
        ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;

    static void InvokeOnFileOpen(Type ty, BackcastWorkspaceRoot root) =>
        ty.GetMethod("OnFileOpen", BF).Invoke(root, null);

    // Seed InstrumentRegistry._ids directly (test-only construction of a locked external-ref set, since
    // ReplaceAll would no-op under Editable=false). Order-preserving append, mirrors the registry's dedup.
    static void SeedRegistryIdsDirect(InstrumentRegistry reg, IEnumerable<string> ids)
    {
        var f = typeof(InstrumentRegistry).GetField("_ids", BindingFlags.NonPublic | BindingFlags.Instance);
        var list = (List<string>)f.GetValue(reg);
        list.Clear();
        foreach (var id in ids) if (!list.Contains(id)) list.Add(id);
    }

    static FloatingWindowLayout ChartWin(string iid, float x, float y, int z) =>
        new FloatingWindowLayout(DockShape.ChartId(iid), FloatingWindowCatalog.KIND_CHART,
            x, y, 520f, 360f, z, true, null);

    static void WriteLayoutWithFloatingWindows(string py, List<FloatingWindowLayout> wins)
    {
        var doc = LayoutDocument.Default();
        doc.floatingWindows = new List<FloatingWindowLayout>(wins);
        LayoutSidecarStore.WriteLayout(py, doc);
    }

    static bool Approx2(Vector2 a, Vector2 b) =>
        Mathf.Abs(a.x - b.x) <= EPS && Mathf.Abs(a.y - b.y) <= EPS;

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
