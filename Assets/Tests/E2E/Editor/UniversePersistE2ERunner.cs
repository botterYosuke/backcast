// UniversePersistE2ERunner.cs — ADR-0031 S3 (#143) release-gate slice runner (台本: same-dir
// UniversePersistE2ERunner.md). 方針: ADR-0031 D4 + findings 0115 §S3.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod UniversePersistE2ERunner.Run -logFile <abs>
//   # expect: [E2E UNIVERSE PERSIST PASS] / exit=0（確認は Bash `grep -a "UNIVERSE PERSIST"`）
//
// WHAT THIS GATES — bt.universe.* edits persist ONLY at the existing Save timing (ADR-0031 D4): a
// programmatic edit marks the registry dirty + reflects (chart) but writes NO sidecar on its own.
// It falls to disk on the EXISTING full-registry Save path — Run-commit / Save As
// (ScenarioStartupController.Commit → SetStartupParamsAndInstruments(Universe.Ids)) — a Newtonsoft
// co-write that preserves the layout key AND unknown scenario keys, exactly like a startup-tile
// text edit. NO new persistence trigger is added (File→Save keeps its universe-untouched invariant,
// JOURNEY-LAYOUT-07). A saved edit survives a restart; an unsaved one reverts.
//
// Python-FREE: the bt.universe edit is simulated by applying the engine's drain JSON via
// UniverseBridge to the REAL registry (same seam BTUNIV-09 exercises); the persistence path
// (Commit → ScenarioSidecarStore.Mutate) is all C#. The Python half (the edit is dirty, never
// auto-persists) is covered by test_bt_universe_bridge.py BTUNIV-05/08.
//
// RED→GREEN litmus (findings 0115 §S3):
//   * if DriveUniverseBridge did NOT apply the edit to the registry, Commit would write the old
//     universe → PERSIST-02/03 RED.
//   * if bt.universe.add were made to write a sidecar on edit (the rejected "編集ごとに即永続化") →
//     PERSIST-01 RED (the add alone would persist).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class UniversePersistE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string IID_A = "7203.TSE";
    const string IID_B = "9984.TSE";
    const string IID_C = "6758.TSE";
    const string UNKNOWN_KEY = "__owner_note__";
    const string UNKNOWN_VAL = "keep-me-through-merge";

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "universe_persist_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section1_EditDoesNotWriteSidecar()      // PERSIST-01 (AC#3 + AC#1 dirty)
                ?? Section2_SaveCoWritesPreservingKeys()   // PERSIST-02 (AC#2)
                ?? Section3_SavedEditSurvivesReseed()      // PERSIST-03 (AC#4 saved)
                ?? Section4_UnsavedEditReverts();          // PERSIST-04 (AC#4 unsaved)
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
        {
            Debug.Log("[E2E UNIVERSE PERSIST PASS] bt.universe.* 編集は registry を dirty にするだけで sidecar を書かず、"
                    + "既存の Save 経路（Run-commit / Save As → Commit）が scenario.instruments に co-write（layout/unknown キー保持）。"
                    + "Save 済みは restart を跨いで残り、未 Save は revert する（ADR-0031 D4・独自トリガ無し）。PERSIST-01..04。findings 0115 §S3。");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E UNIVERSE PERSIST FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── PERSIST-01: open a doc with universe=[A]; apply bt.universe.add(B). The registry reflects
    //   [A,B] (chart B spawns) but the sidecar on disk is UNCHANGED [A] — the add wrote no sidecar
    //   (no 独自 trigger; the edit is dirty, awaiting the normal Save). ──
    static string Section1_EditDoesNotWriteSidecar()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S1: root missing";
        if (!Seams(root, ty, out var scenario, out var chartViews, out var onOpen, out var why)) return "S1: " + why;

        string py = WriteDoc("s1", new List<string> { IID_A });
        DriveFileOpen(root, ty, onOpen, py);
        if (!scenario.Universe.Ids.SequenceEqual(new[] { IID_A })) return "S1: open did not seed [A] (got " + Join(scenario.Universe.Ids) + ")";

        UniverseBridge.ApplyJson("[{\"op\":\"add\",\"id\":\"" + IID_B + "\"}]", scenario.Universe);

        if (!scenario.Universe.Ids.SequenceEqual(new[] { IID_A, IID_B }))
            return "S1 PERSIST-01: registry did not reflect the add (got " + Join(scenario.Universe.Ids) + ")";
        if (!chartViews.Contains(IID_B))
            return "S1 PERSIST-01: chart:" + IID_B + " did not spawn on the add (reflected-but-not-persisted precondition)";
        var disk = ReadInstruments(py);
        if (!disk.SequenceEqual(new[] { IID_A }))
            return "S1 PERSIST-01: bt.universe.add WROTE the sidecar (got " + Join(disk) + ") — add must not trigger its own write (ADR-0031 D4)";
        Debug.Log("[E2E PERSIST-01 PASS] bt.universe.add(B) reflects (registry+chart) but writes NO sidecar (dirty, awaits Save).");
        return null;
    }

    // ── PERSIST-02: from that dirty state, the existing Save path (Run-commit / Save As → Commit).
    //   The edit co-writes to scenario.instruments ([A,B]) via the existing Newtonsoft merge — the
    //   layout key AND an unknown scenario key both survive (merge-write, not a clobber). ──
    static string Section2_SaveCoWritesPreservingKeys()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S2: root missing";
        if (!Seams(root, ty, out var scenario, out _, out var onOpen, out var why)) return "S2: " + why;

        string py = WriteDoc("s2", new List<string> { IID_A });
        DriveFileOpen(root, ty, onOpen, py);
        UniverseBridge.ApplyJson("[{\"op\":\"add\",\"id\":\"" + IID_B + "\"}]", scenario.Universe);

        if (!scenario.Commit(py))   // the existing Run-commit / Save As universe persistence
            return "S2 PERSIST-02: Commit (the existing Save path) failed — invalid scenario after the edit?";

        var disk = ReadInstruments(py);
        if (!disk.SequenceEqual(new[] { IID_A, IID_B }))
            return "S2 PERSIST-02: Save did not co-write the edit to scenario.instruments (got " + Join(disk) + ")";
        // layout key preserved
        if (!LayoutSidecarStore.TryReadLayout(py, out var layout) || layout == null || layout.FindWindow(DockShape.ChartId(IID_A)) == null)
            return "S2 PERSIST-02: Save clobbered the layout key (chart:" + IID_A + " entry lost — not a merge-write)";
        // unknown scenario key preserved
        var rawScenario = ReadRawScenario(py);
        if (rawScenario == null || (string)rawScenario[UNKNOWN_KEY] != UNKNOWN_VAL)
            return "S2 PERSIST-02: Save dropped the unknown scenario key '" + UNKNOWN_KEY + "' (not a merge-write)";
        Debug.Log("[E2E PERSIST-02 PASS] Commit (existing Save path) co-writes [A,B] to scenario.instruments, preserving layout + unknown keys (merge-write).");
        return null;
    }

    // ── PERSIST-03: a SAVED edit survives a reseed (the restart-equivalent: re-open the same doc). ──
    static string Section3_SavedEditSurvivesReseed()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S3: root missing";
        if (!Seams(root, ty, out var scenario, out _, out var onOpen, out var why)) return "S3: " + why;

        string py = WriteDoc("s3", new List<string> { IID_A });
        DriveFileOpen(root, ty, onOpen, py);
        UniverseBridge.ApplyJson("[{\"op\":\"add\",\"id\":\"" + IID_B + "\"}]", scenario.Universe);
        if (!scenario.Commit(py)) return "S3: Commit failed";

        // re-open the same doc (fresh root = the restart) → the registry seeds [A,B] from disk.
        var root2 = ComposeRoot(out var ty2);
        Seams(root2, ty2, out var scenario2, out _, out var onOpen2, out _);
        DriveFileOpen(root2, ty2, onOpen2, py);
        if (!scenario2.Universe.Ids.SequenceEqual(new[] { IID_A, IID_B }))
            return "S3 PERSIST-03: saved edit did NOT survive restart (got " + Join(scenario2.Universe.Ids) + ")";
        Debug.Log("[E2E PERSIST-03 PASS] saved bt.universe edit survives restart (re-open seeds [A,B] from disk).");
        return null;
    }

    // ── PERSIST-04: an UNSAVED edit does NOT survive a restart. Edit C on one session (no Save),
    //   then RESTART (a fresh root re-reading disk) → the registry seeds [A], C is gone, and disk
    //   never carried C. The restart-symmetric negative of PERSIST-03. ──
    static string Section4_UnsavedEditReverts()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S4: root missing";
        if (!Seams(root, ty, out var scenario, out _, out var onOpen, out var why)) return "S4: " + why;

        string py = WriteDoc("s4", new List<string> { IID_A });
        DriveFileOpen(root, ty, onOpen, py);
        UniverseBridge.ApplyJson("[{\"op\":\"add\",\"id\":\"" + IID_C + "\"}]", scenario.Universe);
        if (!scenario.Universe.Ids.SequenceEqual(new[] { IID_A, IID_C }))
            return "S4: precondition — the edit must be live in this session (got " + Join(scenario.Universe.Ids) + ")";
        // NO save — then RESTART (fresh root reads disk).

        var root2 = ComposeRoot(out var ty2);
        Seams(root2, ty2, out var scenario2, out _, out var onOpen2, out _);
        DriveFileOpen(root2, ty2, onOpen2, py);
        if (!scenario2.Universe.Ids.SequenceEqual(new[] { IID_A }))
            return "S4 PERSIST-04: unsaved edit survived restart (got " + Join(scenario2.Universe.Ids) + ") — must revert to disk [A]";
        var disk = ReadInstruments(py);
        if (disk.Contains(IID_C))
            return "S4 PERSIST-04: unsaved C leaked to disk (got " + Join(disk) + ")";
        Debug.Log("[E2E PERSIST-04 PASS] unsaved bt.universe edit does NOT survive restart; never reaches disk.");
        return null;
    }

    // ---- helpers ----

    static bool Seams(BackcastWorkspaceRoot root, Type ty, out ScenarioStartupController scenario,
        out System.Collections.IDictionary chartViews, out MethodInfo onOpen, out string why)
    {
        scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as System.Collections.IDictionary;
        onOpen = ty.GetMethod("OnFileOpen", BF);
        if (scenario == null || chartViews == null) { why = "root seams not built (renamed? _scenario/_chartViews)"; return false; }
        if (onOpen == null) { why = "OnFileOpen not found (renamed?)"; return false; }
        why = null;
        return true;
    }

    // Write <name>.py + <name>.json: a scenario key (universe=`instruments` + an unknown key) and a
    // layout key (one chart window) — so PERSIST-02 can prove the Save merge preserves both.
    static string WriteDoc(string name, List<string> instruments)
    {
        string py = Path.Combine(TempRoot, name, name + ".py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "x = 1\n");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            py, new StartupParamsForWrite("2025-01-06", "2025-01-10", "Daily", "1000000"), instruments);

        // splice an UNKNOWN scenario key (merge-write survival probe) into the just-written sidecar.
        string json = Path.ChangeExtension(py, ".json");
        var root = JObject.Parse(File.ReadAllText(json));
        ((JObject)root["scenario"])[UNKNOWN_KEY] = UNKNOWN_VAL;
        File.WriteAllText(json, root.ToString());

        // then merge a layout key (Newtonsoft merge preserves the scenario key).
        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            hakoniwaProfiles = null,
            canvasView = null,
            floatingWindows = new List<FloatingWindowLayout>
            {
                new FloatingWindowLayout(DockShape.ChartId(IID_A), FloatingWindowCatalog.KIND_CHART, 200f, -200f, 520f, 360f, 0, true),
            },
            strategyEditors = new List<StrategyEditorState>(),
            cellPositions = new List<CellPosition>(),
        };
        LayoutSidecarStore.WriteLayout(py, doc);
        return py;
    }

    static List<string> ReadInstruments(string py) =>
        ScenarioSidecarStore.TryReadScenario(py, out var snap) && snap != null ? snap.Instruments : new List<string>();

    static JObject ReadRawScenario(string py)
    {
        string json = Path.ChangeExtension(py, ".json");
        if (!File.Exists(json)) return null;
        return JObject.Parse(File.ReadAllText(json))["scenario"] as JObject;
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

    static string Join(IEnumerable<string> ids) => "[" + string.Join(",", ids) + "]";

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
