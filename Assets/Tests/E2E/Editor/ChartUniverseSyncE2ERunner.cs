// ChartUniverseSyncE2ERunner.cs — Issue #123 release-gate slice runner (台本: same-dir
// ChartUniverseSyncE2ERunner.md). 方針: findings 0095.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartUniverseSyncE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART UNIVERSE SYNC PASS] ... / exit=0  (確認は Bash `grep -a "CHART UNIVERSE SYNC"`)
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHAT THIS GATES — the #123 invariant "chart windows == universe (SoT)" must hold ACROSS a layout
// restore, NOT only when Universe.Changed fires. A saved `chart:<iid>` is restored by ApplyLayout →
// RestoreFloating BEFORE the universe is seeded — a SECOND spawn path that bypasses the
// SyncChartWindowsToUniverse subscription (the only intended spawn/close path). The seed's ReplaceAll
// then does NOT fire Changed when the resulting set is unchanged — `SequenceEqual` on an empty→empty
// reseed, or `!Editable` (instruments_ref lock) returns false (InstrumentRegistry :85/:98) — so the
// subscribed sync never runs and a restored chart whose instrument is ABSENT from the (re)seeded
// universe survives as an ORPHAN even though the sidebar reads "No instruments".
//
// FIX (ReseedFromEditor tail): one Changed-INDEPENDENT SyncChartWindowsToUniverse() at the canonical
// reseed tail — the SINGLE point every restore→reseed entry passes (Resume / File→Open / Save / New).
// It MUST run AFTER the seed (ordering it before re-spawns the just-restored chart at a default grid
// slot, destroying restored geometry — the alternative the issue rejected).
//
// RED→GREEN litmus (findings 0095): delete the `SyncChartWindowsToUniverse();` line at ReseedFromEditor's
// tail → CHARTSYNC-01 (empty universe via File→Open) and CHARTSYNC-03 (instruments_ref lock via File→Open)
// go RED (the orphan chart survives). CHARTSYNC-02 (boot resume, universe=[X]) and CHARTSYNC-04 (subset
// via File→Open) pin the position-persistence regression: the matched chart must survive WITH its restored
// geometry (the explicit sync must despawn-the-orphan-only, never despawn+respawn a survivor — which a
// before-seed ordering would do, relocating it to a default grid slot).
//
// Drives the REAL restore→reseed entries on the REAL BackcastWorkspaceRoot composition:
//   * CHARTSYNC-02 → ResumeLastDocumentOrDefault (boot resume, PlayerPrefs pointer)
//   * CHARTSYNC-01/03/04 → OnFileOpen → DoFileOpen (File→Open)
// Python-FREE: layout restore + reseed need no kernel (the File-op mode side-effect is a no-op while
// disconnected; the notebook synthesiser is the FakeMarimoSynthesizer).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ChartUniverseSyncE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const float EPS = 1e-3f;
    const string RESUME_KEY = "backcast.lastDocument";   // mirror of BackcastWorkspaceRoot.ResumeKey
    const string IID = "7203.TSE";                       // survivor instrument (matched to universe)
    const string IID_ORPHAN = "9984.TSE";                // orphan instrument (restored chart, absent from universe)
    // distinctive restored geometry (catalog chart defaultSize 520×360 so RestoreFloating's Spawn does
    // NOT clamp; far from any default grid slot so a despawn+respawn regression is unmistakable).
    static readonly Vector2 CHART_TL = new Vector2(812f, -456f);
    const float CHART_W = 520f, CHART_H = 360f;
    static readonly Vector2 ORPHAN_TL = new Vector2(140f, -700f);

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "chart_universe_sync_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section1_FileOpen_EmptyUniverse_DespawnsOrphan()       // CHARTSYNC-01 (empty→empty SequenceEqual cell)
                ?? Section2_BootResume_MatchedUniverse_KeepsGeometry()    // CHARTSYNC-02 (survive + geometry)
                ?? Section3_FileOpen_LockedRegistry_DespawnsOrphan()      // CHARTSYNC-03 (Editable=false instruments_ref lock cell)
                ?? Section4_FileOpen_SubsetUniverse_MixedOrphanSurvivor(); // CHARTSYNC-04 (subset: orphan + survivor mix)
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); PlayerPrefs.SetString(RESUME_KEY, ""); PlayerPrefs.Save(); }

        if (fail == null)
        {
            Debug.Log("[E2E CHART UNIVERSE SYNC PASS] layout 復元が universe-sync をバイパスして残す孤児 chart:<iid> を、"
                    + "ReseedFromEditor 末尾の Changed 非依存 SyncChartWindowsToUniverse が全 restore→reseed 入口で despawn する。"
                    + "CHARTSYNC-01 空 universe(File→Open)・03 instruments_ref ロック(File→Open) で孤児 despawn、"
                    + "02 boot resume(universe=[X])・04 subset(File→Open) で残存 chart は復元ジオメトリ(x/y/w/h)を保持。findings 0095。");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART UNIVERSE SYNC FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── CHARTSYNC-01: File→Open a doc whose layout key carries chart:7203 but whose scenario key has an
    //   EMPTY universe. The reseed's ReplaceAll(empty) leaves the (already empty) universe unchanged →
    //   SequenceEqual → Changed NOT fired → the subscribed sync never runs. The restored chart is an
    //   orphan; only the explicit tail sync despawns it. NON-VACUOUS: the chart WAS restored (asserted
    //   live before the despawn-check would be impossible — instead we assert the universe is genuinely
    //   empty AND the chart family is gone, so a no-op "never spawned" can't masquerade as a pass). ──
    static string Section1_FileOpen_EmptyUniverse_DespawnsOrphan()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S1: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var onOpen = ty.GetMethod("OnFileOpen", BF);
        if (scenario == null || dockWindows == null || chartViews == null) return "S1: root seams not built (renamed? _scenario/_dockWindows/_chartViews)";
        if (onOpen == null) return "S1: OnFileOpen not found (renamed?)";
        if (scenario.Universe.Ids.Count != 0) return "S1: precondition — universe must start empty";

        string py = WriteDoc("empty", new List<string>(),               // scenario universe = EMPTY
            new[] { ChartWin(IID, CHART_TL) });                          // layout has chart:7203
        var pc = AssertRestoreActuallySpawns(root, ty, chartViews, py, IID, "S1 CHARTSYNC-01");
        if (pc != null) return pc;                                        // non-vacuity: the orphan WAS live before reseed
        DriveFileOpen(root, ty, onOpen, py);

        // the orphan chart must be GONE and the universe consistently empty (sidebar "No instruments").
        if (scenario.Universe.Ids.Count != 0)
            return "S1 CHARTSYNC-01: universe not empty after reseed (got " + scenario.Universe.Ids.Count + ")";
        if (chartViews.Contains(IID))
            return "S1 CHARTSYNC-01: orphan chart:" + IID + " survived the empty-universe restore (ReseedFromEditor tail sync missing/ineffective)";
        if (dockWindows.RectOf(DockShape.ChartId(IID)) != null)
            return "S1 CHARTSYNC-01: orphan chart window chart:" + IID + " still on the back plane after reseed";
        Debug.Log("[E2E CHARTSYNC-01 PASS] File→Open chart:7203 + empty universe (ReplaceAll empty→empty, no Changed) → orphan despawned by reseed-tail sync.");
        return null;
    }

    // ── CHARTSYNC-02: boot resume (ResumeLastDocumentOrDefault) a doc whose layout key carries chart:7203
    //   AND whose scenario universe = [7203]. The chart is matched, so it MUST survive — and keep its
    //   RESTORED geometry (x/y/w/h). This pins the position-persistence regression: the tail sync runs
    //   AFTER the seed, so a survivor is never despawned+respawned at a default grid slot. ──
    static string Section2_BootResume_MatchedUniverse_KeepsGeometry()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S2: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var resume = ty.GetMethod("ResumeLastDocumentOrDefault", BF);
        var captureLayout = ty.GetMethod("CaptureLayout", BF);
        if (scenario == null || dockWindows == null || chartViews == null) return "S2: root seams not built (renamed?)";
        if (resume == null) return "S2: ResumeLastDocumentOrDefault not found (renamed?)";
        if (captureLayout == null) return "S2: CaptureLayout not found (renamed?)";

        string py = WriteDoc("resume", new List<string> { IID },        // scenario universe = [7203]
            new[] { ChartWin(IID, CHART_TL) });                          // layout has chart:7203 at distinctive rect
        PlayerPrefs.SetString(RESUME_KEY, py); PlayerPrefs.Save();        // the resume pointer the boot path reads
        resume.Invoke(root, null);                                       // REAL boot resume → ApplyLayout + Open + ReseedFromEditor

        // confirm the RESUME path was taken (sets _currentLayoutPath = py), not the Default-layout
        // fallback (sets ""). Without this, a _coordinator.Open failure would fall through to
        // OpenFileNewDefault (empty universe) and the universe assertion below would fire with a
        // MISLEADING "did not seed" message rather than "resume did not happen".
        var bound = ty.GetField("_currentLayoutPath", BF)?.GetValue(root) as string;
        if (string.IsNullOrEmpty(bound) ||
            !string.Equals(Path.GetFullPath(bound), Path.GetFullPath(py), StringComparison.OrdinalIgnoreCase))
            return "S2 CHARTSYNC-02: boot resume fell through to the Default layout (coordinator.Open failed?) — _currentLayoutPath='" + bound + "'";
        if (!scenario.Universe.Ids.Contains(IID))
            return "S2 CHARTSYNC-02: universe did not seed [" + IID + "] on boot resume";
        if (!chartViews.Contains(IID))
            return "S2 CHARTSYNC-02: matched chart:" + IID + " was despawned by the reseed-tail sync (must survive when in universe)";
        if (dockWindows.RectOf(DockShape.ChartId(IID)) == null)
            return "S2 CHARTSYNC-02: matched chart window vanished from the back plane";

        // restored geometry must be preserved — a despawn+respawn (before-seed ordering bug) would land it
        // at a default grid slot, NOT this distinctive (812,-456,520,360).
        var capDoc = captureLayout.Invoke(root, null) as LayoutDocument;
        var capChart = capDoc?.FindWindow(DockShape.ChartId(IID));
        if (capChart == null) return "S2 CHARTSYNC-02: chart:" + IID + " missing from captured layout";
        if (Mathf.Abs(capChart.x - CHART_TL.x) > EPS || Mathf.Abs(capChart.y - CHART_TL.y) > EPS
            || Mathf.Abs(capChart.w - CHART_W) > EPS || Mathf.Abs(capChart.h - CHART_H) > EPS)
            return "S2 CHARTSYNC-02: restored chart geometry NOT preserved (got " + capChart.x + "," + capChart.y + ","
                 + capChart.w + "," + capChart.h + " want " + CHART_TL.x + "," + CHART_TL.y + "," + CHART_W + "," + CHART_H
                 + " — survivor was relocated, sync ran before seed?)";
        Debug.Log("[E2E CHARTSYNC-02 PASS] boot resume chart:7203 + universe=[7203] → chart survives AND restored geometry (812,-456,520,360) preserved.");
        return null;
    }

    // ── CHARTSYNC-03: File→Open with the universe registry LOCKED (Editable=false, the instruments_ref
    //   case). The sidecar names [7203] but ReplaceAll([7203]) returns false on a locked registry
    //   (InstrumentRegistry :85) → universe stays EMPTY → Changed NOT fired. The restored chart:7203 is an
    //   orphan (the in-memory SoT is empty / sidebar "No instruments"); the explicit tail sync despawns it.
    //   NOTE: production does not yet wire instruments_ref → Editable=false; we set Editable=false directly,
    //   mirroring UniverseSidebarE2ERunner / ScenarioStartupE2ERunner's locked-registry simulation. ──
    static string Section3_FileOpen_LockedRegistry_DespawnsOrphan()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S3: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var onOpen = ty.GetMethod("OnFileOpen", BF);
        if (scenario == null || dockWindows == null || chartViews == null) return "S3: root seams not built (renamed?)";
        if (onOpen == null) return "S3: OnFileOpen not found (renamed?)";

        scenario.Universe.Editable = false;   // instruments_ref lock — ReplaceAll becomes a no-op (no Changed)
        if (scenario.Universe.Ids.Count != 0) return "S3: precondition — locked universe must start empty";

        string py = WriteDoc("locked", new List<string> { IID },        // sidecar names [7203] but…
            new[] { ChartWin(IID, CHART_TL) });                          // …ReplaceAll no-ops (locked) → universe stays empty
        // ApplyLayout/RestoreFloating does not consult the registry, so the chart spawns even under the
        // lock — the positive control proves the orphan was live before the locked reseed despawns it.
        var pc = AssertRestoreActuallySpawns(root, ty, chartViews, py, IID, "S3 CHARTSYNC-03");
        if (pc != null) return pc;
        DriveFileOpen(root, ty, onOpen, py);

        if (scenario.Universe.Ids.Count != 0)
            return "S3 CHARTSYNC-03: locked ReplaceAll unexpectedly populated the universe (Editable gate broke?)";
        if (chartViews.Contains(IID))
            return "S3 CHARTSYNC-03: orphan chart:" + IID + " survived under a LOCKED registry (ReplaceAll no-op → no Changed → tail sync must despawn)";
        if (dockWindows.RectOf(DockShape.ChartId(IID)) != null)
            return "S3 CHARTSYNC-03: orphan chart window chart:" + IID + " still on the back plane (locked-registry cell)";
        Debug.Log("[E2E CHARTSYNC-03 PASS] File→Open chart:7203 + locked registry (instruments_ref, ReplaceAll no-op, no Changed) → orphan despawned.");
        return null;
    }

    // ── CHARTSYNC-04: File→Open a doc whose layout carries TWO charts (chart:7203 + chart:9984) but whose
    //   scenario universe = [7203] only. 9984 is an orphan (despawn); 7203 is matched (survive). The seed
    //   empty→[7203] DOES fire Changed, so the subscribed sync removes 9984 — and the explicit tail sync is
    //   the idempotent confirm. The survivor must KEEP its restored geometry (proves "despawn the orphan
    //   only", not "despawn all + respawn from universe at default grid"). ──
    static string Section4_FileOpen_SubsetUniverse_MixedOrphanSurvivor()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S4: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var onOpen = ty.GetMethod("OnFileOpen", BF);
        var captureLayout = ty.GetMethod("CaptureLayout", BF);
        if (scenario == null || dockWindows == null || chartViews == null) return "S4: root seams not built (renamed?)";
        if (onOpen == null || captureLayout == null) return "S4: OnFileOpen/CaptureLayout not found (renamed?)";

        string py = WriteDoc("subset", new List<string> { IID },        // universe = [7203] only
            new[] { ChartWin(IID, CHART_TL), ChartWin(IID_ORPHAN, ORPHAN_TL) });  // layout has BOTH charts
        DriveFileOpen(root, ty, onOpen, py);

        // orphan 9984 despawned…
        if (chartViews.Contains(IID_ORPHAN))
            return "S4 CHARTSYNC-04: orphan chart:" + IID_ORPHAN + " (absent from universe) survived the subset restore";
        if (dockWindows.RectOf(DockShape.ChartId(IID_ORPHAN)) != null)
            return "S4 CHARTSYNC-04: orphan chart window chart:" + IID_ORPHAN + " still on the back plane";
        // …survivor 7203 kept, with restored geometry intact.
        if (!chartViews.Contains(IID))
            return "S4 CHARTSYNC-04: matched chart:" + IID + " was wrongly despawned (subset sync over-pruned)";
        var capDoc = captureLayout.Invoke(root, null) as LayoutDocument;
        var capChart = capDoc?.FindWindow(DockShape.ChartId(IID));
        if (capChart == null) return "S4 CHARTSYNC-04: survivor chart:" + IID + " missing from captured layout";
        if (Mathf.Abs(capChart.x - CHART_TL.x) > EPS || Mathf.Abs(capChart.y - CHART_TL.y) > EPS
            || Mathf.Abs(capChart.w - CHART_W) > EPS || Mathf.Abs(capChart.h - CHART_H) > EPS)
            return "S4 CHARTSYNC-04: survivor geometry NOT preserved (got " + capChart.x + "," + capChart.y + ","
                 + capChart.w + "," + capChart.h + " — despawn+respawn instead of despawn-orphan-only?)";
        Debug.Log("[E2E CHARTSYNC-04 PASS] File→Open chart:7203+chart:9984, universe=[7203] → 9984 despawned, 7203 survives with restored geometry.");
        return null;
    }

    // ---- helpers ----

    // Write a <strategy>.py + <strategy>.json document pair: scenario key (universe = `instruments`) +
    // layout key (the supplied floating windows). Returns the .py path.
    static string WriteDoc(string name, List<string> instruments, FloatingWindowLayout[] wins)
    {
        string py = Path.Combine(TempRoot, name, name + ".py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "x = 1\n");
        // scenario key first (creates the sidecar) …
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            py, new StartupParamsForWrite("2025-01-06", "2025-01-10", "Daily", "1000000"), instruments);
        // … then splice the layout key (Newtonsoft merge preserves the scenario key).
        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            hakoniwaProfiles = null,
            canvasView = null,
            floatingWindows = new List<FloatingWindowLayout>(wins),
            strategyEditors = new List<StrategyEditorState>(),
            cellPositions = new List<CellPosition>(),   // empty → coordinator auto-cascades the .py cells
        };
        LayoutSidecarStore.WriteLayout(py, doc);
        // Non-vacuity guard: confirm the layout key round-trips AND that LayoutStore's
        // NormalizeFloatingWindows did NOT silently drop the chart entries (an unknown kind / non-finite
        // w-h / duplicate id would be dropped at load → RestoreFloating spawns nothing → the despawn
        // assertions in Section1/3 would pass vacuously). With the chart entries proven present on disk,
        // the suite's positive control that RestoreFloating actually SPAWNS them is Section2/4 (the
        // survivor chart must be live + at restored geometry, else those go RED).
        if (!LayoutSidecarStore.TryReadLayout(py, out var readBack) || readBack == null)
            throw new Exception("WriteDoc(" + name + "): layout key did not round-trip back through TryReadLayout");
        foreach (var w in wins)
            if (readBack.FindWindow(w.id) == null)
                throw new Exception("WriteDoc(" + name + "): chart entry '" + w.id + "' was dropped by layout normalization on round-trip");
        return py;
    }

    static FloatingWindowLayout ChartWin(string iid, Vector2 topLeft) =>
        new FloatingWindowLayout(DockShape.ChartId(iid), FloatingWindowCatalog.KIND_CHART,
            topLeft.x, topLeft.y, CHART_W, CHART_H, 0, true);

    static void DriveFileOpen(BackcastWorkspaceRoot root, Type ty, MethodInfo onOpen, string py)
    {
        root.SetFileDialog(new StubFileDialog { NextResult = py });
        onOpen.Invoke(root, null);
        var bound = ty.GetField("_currentLayoutPath", BF)?.GetValue(root) as string;
        if (string.IsNullOrEmpty(bound) ||
            !string.Equals(Path.GetFullPath(bound), Path.GetFullPath(py), StringComparison.OrdinalIgnoreCase))
            throw new Exception("File→Open did not bind _currentLayoutPath to '" + py + "' (got '" + bound + "')");
    }

    // Positive control (per-section NON-VACUITY) for the orphan-despawn sections (S1/S3). The despawn
    // assertions only prove the chart is GONE after the reseed; they cannot, on their own, distinguish
    // "RestoreFloating spawned it then the tail sync despawned it" (the real fix) from "RestoreFloating
    // silently never spawned it" (e.g. a kind-routing regression → the despawn passes VACUOUSLY). So
    // before the real File→Open, invoke the SAME production spawn path (ApplyLayout → RestoreFloating)
    // on this doc and assert chart:<iid> actually landed in _chartViews. The subsequent real OnFileOpen
    // re-applies geometry idempotently (RestoreFloating's Has(id) branch → ApplyGeometry, no respawn)
    // and the reseed tail despawns it — so this control changes NOTHING about the end-to-end outcome,
    // it only pins that there WAS a live orphan to despawn.
    static string AssertRestoreActuallySpawns(BackcastWorkspaceRoot root, Type ty, IDictionary chartViews, string py, string iid, string tag)
    {
        var applyLayout = ty.GetMethod("ApplyLayout", BF);
        if (applyLayout == null) return tag + ": ApplyLayout not found (renamed?)";
        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null)
            return tag + ": positive-control could not read the layout back from " + py;
        applyLayout.Invoke(root, new object[] { doc });
        if (!chartViews.Contains(iid))
            return tag + ": positive-control FAILED — RestoreFloating did not spawn chart:" + iid
                 + " from the layout entry, so the despawn assertions below would pass vacuously";
        return null;
    }

    // Edit-mode compose: OpenScene does NOT run Awake, so ResumeLastDocumentOrDefault is NEVER auto-invoked
    // here (we drive ResolvePaths + BuildWorkspace by hand, like the sibling runners). That is why the
    // PlayerPrefs RESUME_KEY set in Section2 cannot leak an auto-resume into Section1/3/4's File→Open paths —
    // only the EXPLICIT resume.Invoke in Section2 reads it. The `finally` clears it after the run regardless.
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

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
