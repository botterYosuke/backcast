// FileNavGuardE2ERunner.cs — issue #87「未保存変更があるまま File→New / File→Open すると確認なしに破棄される」の
// 横断 E2E 回帰ゲート（台本: 同ディレクトリの FileNavGuardE2ERunner.md / FILEGUARD-01..09）。第二波・全行新規。
// #89 の SaveGuard（Save/Discard/Cancel 純判定・findings 0068）を File→New / File→Open の未保存ガードへ再利用
// した配線（#87 slice 3・findings 0069）を、実 BackcastWorkspaceRoot を反射駆動して観測する。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod FileNavGuardE2ERunner.Run -logFile <log>
//   # expect: [E2E FILE-NAV-GUARD PASS] ... / exit=0  （確認は Bash `grep -a "FILE-NAV-GUARD"`）
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// Python-FREE: File→New / File→Open / SaveGuard はすべて host 非依存（modeReq は disconnected で null・
// FileOpenModeSideEffect も null = host 無接触）。実 server を一切要しない（AuthorToRun の steps 2-9 と同型）。
//
// 層分け — SaveGuardController 単体の純判定正本は QuitConfirmE2ERunner（QUIT-01..10）。本 runner は移送せず、
// 実 root を通した「dirty な File→New / File→Open が SaveGuard を挟む」配線（defer / Cancel 据え置き / Discard
// 続行 / Save 続行 / データ保護 Abort / clean 素通し / valid-marimo も確認必須＝owner-veto supersession）を観測する。
//
// owner-veto supersession（findings 0069 slice 3）: 旧挙動「valid marimo `.py` は dirty でも黙って切替」を #87 で
// 廃し、File→Open は valid-marimo への切替も含め必ず Save/Discard/Cancel を出す（FILEGUARD-07）。
//
// delete-the-production-logic litmus:
//   * OnFileNew の GuardThenProceed を外し DoFileNew を直呼び → FILEGUARD-01/02/03 FAIL（defer/Cancel/Discard 区別が消える）。
//   * OnFileOpen の GuardThenProceed を外し DoFileOpen 直呼び → FILEGUARD-05/07 FAIL（dirty Open が黙って切替）。
//   * DoFileOpen の coordinator.Open(..., discardDirty:true) を discardDirty 無し（既定 false）に戻す → FILEGUARD-06 FAIL（F1 refuse で buffer 据え置き）。
//   * OnGuardSave のデータ保護（ResolveSaveAs(false)→Abort）を「常に proceed」に壊す → FILEGUARD-09 FAIL（編集を失って Open が走る）。

using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FileNavGuardE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string FIRST = "8918.TSE";

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "file_nav_guard_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = FileGuard01_NewDirtyDefers()
                ?? FileGuard02_NewCancelPreserves()
                ?? FileGuard03_NewDiscardProceeds()
                ?? FileGuard04_NewSavePersistsThenProceeds()
                ?? FileGuard05_OpenCancelPreserves()
                ?? FileGuard06_OpenDiscardRelaxesF1()
                ?? FileGuard07_OpenValidMarimoStillGuarded()
                ?? FileGuard08_CleanPassesThrough()
                ?? FileGuard09_OpenSaveCancelledAborts();
        }
        catch (Exception ex) { fail = "driver: " + ex; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
        {
            Debug.Log("[E2E FILE-NAV-GUARD PASS] dirty File→New/Open defer behind the SaveGuard; Cancel preserves the document, Discard proceeds (discardDirty relaxes #86 F1), Save persists-then-proceeds, a cancelled Save aborts (data protection), a clean document passes through, and even a valid-marimo switch is guarded (owner-veto).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E FILE-NAV-GUARD FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── FILEGUARD-01: File→New on a DIRTY notebook DEFERS the clear behind the SaveGuard modal (does not
    //   clear immediately). The modal is open, the controller is awaiting a verdict, the document intact. ──
    static string FileGuard01_NewDirtyDefers()
    {
        var h = Compose(out string err); if (err != null) return "FILEGUARD-01: " + err;
        DirtyTwoCells(h);
        h.OnFileNew();
        if (!h.Sgc.IsOpen) return "FILEGUARD-01: dirty File→New did not OPEN the SaveGuard (no confirm prompt)";
        if (!h.Overlay.IsVisible) return "FILEGUARD-01: SaveGuard modal not visible after dirty File→New";
        if (h.Nb.CellCount != 2) return "FILEGUARD-01: File→New CLEARED the notebook without a prompt (count " + h.Nb.CellCount + ")";
        if (!h.Nb.IsDirty) return "FILEGUARD-01: the deferred document lost its dirty state";
        return null;
    }

    // ── FILEGUARD-02: Cancel on a dirty File→New ABANDONS the New — the document (cells/dirty/universe/path)
    //   is preserved unchanged and the modal closes (issue #87: Cancel keeps the current editor). ──
    static string FileGuard02_NewCancelPreserves()
    {
        var h = Compose(out string err); if (err != null) return "FILEGUARD-02: " + err;
        DirtyTwoCells(h);
        h.Scenario.AddInstrument(FIRST);
        h.OnFileNew();
        h.OnGuardCancel();
        if (h.Sgc.IsOpen || h.Overlay.IsVisible) return "FILEGUARD-02: Cancel did not close the SaveGuard modal";
        if (h.Nb.CellCount != 2) return "FILEGUARD-02: Cancel did not preserve the cells (count " + h.Nb.CellCount + ")";
        if (!h.Nb.IsDirty) return "FILEGUARD-02: Cancel lost the dirty state";
        if (h.Scenario.Universe.Count != 1) return "FILEGUARD-02: Cancel cleared the universe (must be preserved)";
        if (Current(h) != "") return "FILEGUARD-02: Cancel changed _currentLayoutPath";
        return null;
    }

    // ── FILEGUARD-03: Discard on a dirty File→New PROCEEDS — the workspace clears to one empty cell + empty
    //   universe + untitled (the deferred DoFileNew runs after the discard verdict). ──
    static string FileGuard03_NewDiscardProceeds()
    {
        var h = Compose(out string err); if (err != null) return "FILEGUARD-03: " + err;
        DirtyTwoCells(h);
        h.Scenario.AddInstrument(FIRST);
        h.OnFileNew();
        h.OnGuardDiscard();
        if (h.Sgc.IsOpen) return "FILEGUARD-03: Discard left the SaveGuard open";
        if (h.Nb.CellCount != 1) return "FILEGUARD-03: Discard→New left " + h.Nb.CellCount + " cells (expected 1)";
        if (!string.IsNullOrEmpty(h.Nb.Cells[0].Body)) return "FILEGUARD-03: Discard→New cell 0 is not empty";
        if (h.Scenario.Universe.Count != 0) return "FILEGUARD-03: Discard→New did not clear the universe";
        if (Current(h) != "") return "FILEGUARD-03: Discard→New did not drop to untitled";
        return null;
    }

    // ── FILEGUARD-04: Save on a dirty (bound) File→New PERSISTS the work THEN proceeds. Bind via Save As,
    //   dirty the cell, File→New, choose Save: the edit is written to the bound .py (data preserved) AND the
    //   New then clears the workspace. NON-VACUOUS: the saved .py carries the edited body. ──
    static string FileGuard04_NewSavePersistsThenProceeds()
    {
        var h = Compose(out string err); if (err != null) return "FILEGUARD-04: " + err;
        string boundPy = Path.Combine(TempRoot, "bound_save.py");
        h.Root.SetFileDialog(new StubFileDialog { NextResult = boundPy });
        h.OnFileSaveAs();                                  // bind + clean
        if (!h.Nb.IsBound) return "FILEGUARD-04: Save As did not bind the document";
        h.Nb.Cells[0].SetBody("def on_bar():\n    submit_market(7)  # saved edit\n");
        if (!h.Nb.IsDirty) return "FILEGUARD-04: editing did not dirty the bound document";
        h.OnFileNew();
        if (!h.Sgc.IsOpen) return "FILEGUARD-04: dirty bound File→New did not prompt";
        h.OnGuardSave();
        if (h.Sgc.IsOpen) return "FILEGUARD-04: Save left the SaveGuard open";
        if (!File.Exists(boundPy)) return "FILEGUARD-04: Save did not write the bound .py";
        if (!File.ReadAllText(boundPy).Contains("submit_market(7)"))
            return "FILEGUARD-04: the saved .py does not carry the edited body (Save-then-proceed lost the work)";
        if (h.Nb.CellCount != 1 || !string.IsNullOrEmpty(h.Nb.Cells[0].Body))
            return "FILEGUARD-04: Save did not PROCEED with the New (workspace not cleared)";
        if (Current(h) != "") return "FILEGUARD-04: Save→New did not drop to untitled";
        return null;
    }

    // ── FILEGUARD-05: Cancel on a dirty File→Open preserves the CURRENT document (the picked .py is NOT
    //   loaded). Bind to A.py, dirty it, File→Open B.py, Cancel → still bound to A.py, still dirty. ──
    static string FileGuard05_OpenCancelPreserves()
    {
        var h = Compose(out string err); if (err != null) return "FILEGUARD-05: " + err;
        string aPy = Path.Combine(TempRoot, "open_cancel_A.py");
        h.Root.SetFileDialog(new StubFileDialog { NextResult = aPy });
        h.OnFileSaveAs();                                  // bind to A.py (clean)
        h.Nb.Cells[0].SetBody("a_edit = 1\n");             // dirty A
        string bPy = Path.Combine(TempRoot, "open_cancel_B.py");
        File.WriteAllText(bPy, "b_body = 2\n");
        h.Root.SetFileDialog(new StubFileDialog { NextResult = bPy });
        h.OnFileOpen();
        if (!h.Sgc.IsOpen) return "FILEGUARD-05: dirty File→Open did not prompt";
        h.OnGuardCancel();
        if (h.Sgc.IsOpen) return "FILEGUARD-05: Cancel left the SaveGuard open";
        if (!SamePath(Current(h), aPy)) return "FILEGUARD-05: Cancel changed the bound path (B was loaded)";
        if (!h.Nb.IsDirty) return "FILEGUARD-05: Cancel lost the dirty state";
        if (!h.Nb.Cells[0].Body.Contains("a_edit")) return "FILEGUARD-05: Cancel replaced the current document body";
        return null;
    }

    // ── FILEGUARD-06: Discard on a dirty File→Open of a NON-MARIMO .py (FailDecompose) RELAXES the #86 F1
    //   refuse via discardDirty:true — the buffer is replaced (1-cell wrap of the file). LITMUS: without
    //   discardDirty:true the aggregate F1-refuses a dirty non-marimo open and the buffer is preserved. ──
    static string FileGuard06_OpenDiscardRelaxesF1()
    {
        var h = Compose(out string err, failDecompose: true); if (err != null) return "FILEGUARD-06: " + err;
        h.Nb.Cells[0].SetBody("dirty_work = 1\n");         // dirty the untitled doc
        string tgt = Path.Combine(TempRoot, "open_discard.py");
        File.WriteAllText(tgt, "imperative_strategy = True\n");
        h.Root.SetFileDialog(new StubFileDialog { NextResult = tgt });
        h.OnFileOpen();
        if (!h.Sgc.IsOpen) return "FILEGUARD-06: dirty non-marimo File→Open did not prompt";
        h.OnGuardDiscard();
        if (h.Sgc.IsOpen) return "FILEGUARD-06: Discard left the SaveGuard open";
        if (!SamePath(Current(h), tgt)) return "FILEGUARD-06: Discard did not load the picked .py (discardDirty did not relax F1)";
        if (!h.Nb.WrapMode) return "FILEGUARD-06: the non-marimo open was not a 1-cell wrap (WrapMode)";
        if (!h.Nb.Cells[0].Body.Contains("imperative_strategy")) return "FILEGUARD-06: the wrapped cell does not carry the file body";
        return null;
    }

    // ── FILEGUARD-07: owner-veto supersession (findings 0069 slice 3). A dirty File→Open of a VALID-marimo
    //   .py (default synth: Decompose succeeds, so #86 F1 never fires) MUST STILL prompt — the old "marimo
    //   silently switches" behaviour is retired. The modal opens; Discard then switches. LITMUS: drop the
    //   OnFileOpen guard front and this silent-switch path stops prompting → FILEGUARD-07 FAIL. ──
    static string FileGuard07_OpenValidMarimoStillGuarded()
    {
        var h = Compose(out string err); if (err != null) return "FILEGUARD-07: " + err;   // default synth (Decompose succeeds)
        h.Nb.Cells[0].SetBody("dirty_work = 2\n");
        string marimoPy = Path.Combine(TempRoot, "switch_target.py");
        File.WriteAllText(marimoPy, "switched_body = 3\n");
        h.Root.SetFileDialog(new StubFileDialog { NextResult = marimoPy });
        h.OnFileOpen();
        if (!h.Sgc.IsOpen) return "FILEGUARD-07: a dirty switch to a valid-marimo .py was NOT guarded (owner-veto regression — old silent-switch behaviour resurfaced)";
        if (h.Nb.WrapMode) return "FILEGUARD-07: precondition — the default synth open should NOT be a wrap (must exercise the silent-switch path)";
        h.OnGuardDiscard();
        if (!SamePath(Current(h), marimoPy)) return "FILEGUARD-07: Discard did not switch to the picked notebook";
        if (!h.Nb.Cells[0].Body.Contains("switched_body")) return "FILEGUARD-07: the switched buffer does not carry the new file body";
        return null;
    }

    // ── FILEGUARD-08: a CLEAN document passes through with NO prompt (the guard never over-fires). Bind a
    //   clean doc via Save As, then File→Open another .py: no modal opens and the open proceeds immediately
    //   (this is why the clean reflective probes / runners are unaffected by #87). ──
    static string FileGuard08_CleanPassesThrough()
    {
        var h = Compose(out string err); if (err != null) return "FILEGUARD-08: " + err;
        string aPy = Path.Combine(TempRoot, "clean_A.py");
        h.Root.SetFileDialog(new StubFileDialog { NextResult = aPy });
        h.OnFileSaveAs();                                  // bind A (clean)
        string bPy = Path.Combine(TempRoot, "clean_B.py");
        File.WriteAllText(bPy, "clean_b = 1\n");
        h.Root.SetFileDialog(new StubFileDialog { NextResult = bPy });
        h.OnFileOpen();                                    // clean → must NOT prompt
        if (h.Sgc.IsOpen || h.Overlay.IsVisible) return "FILEGUARD-08: a CLEAN File→Open wrongly opened the SaveGuard (over-guarding)";
        if (!SamePath(Current(h), bPy)) return "FILEGUARD-08: a clean File→Open did not load the picked .py immediately";
        return null;
    }

    // ── FILEGUARD-09: data-protection Abort (mirrors QUIT-10). A dirty UNTITLED File→Open → Save routes to
    //   Save As; if the picker is CANCELLED the document stays dirty → the action ABORTS (the picked .py is
    //   NOT loaded — unsaved work is never lost silently). The Save-As picker is cancelled by resetting the
    //   stub to null AFTER the open captured its path. ──
    static string FileGuard09_OpenSaveCancelledAborts()
    {
        var h = Compose(out string err); if (err != null) return "FILEGUARD-09: " + err;
        h.Nb.Cells[0].SetBody("unsaved = 1\n");            // dirty untitled doc
        string tgt = Path.Combine(TempRoot, "open_abort.py");
        File.WriteAllText(tgt, "should_not_load = 9\n");
        var stub = new StubFileDialog { NextResult = tgt };
        h.Root.SetFileDialog(stub);
        h.OnFileOpen();                                    // captures tgt, defers (dirty untitled)
        if (!h.Sgc.IsOpen) return "FILEGUARD-09: dirty File→Open did not prompt";
        stub.NextResult = null;                            // the upcoming Save-As picker is cancelled
        h.OnGuardSave();                                   // untitled → Save As → cancelled → still dirty → Abort
        if (SamePath(Current(h), tgt)) return "FILEGUARD-09: a cancelled Save still LOADED the picked .py (data-protection Abort missing)";
        if (h.Nb.IsBound) return "FILEGUARD-09: the untitled document was wrongly bound after a cancelled Save";
        if (!h.Nb.IsDirty) return "FILEGUARD-09: the unsaved work was lost (must stay dirty after Abort)";
        if (!h.Nb.Cells[0].Body.Contains("unsaved")) return "FILEGUARD-09: the dirty body was replaced despite the Abort";
        return null;
    }

    // ---- helpers ----

    // A reflective handle bundle over one composed BackcastWorkspaceRoot (Python-free).
    sealed class Handle
    {
        public BackcastWorkspaceRoot Root;
        public Type Ty;
        public NotebookCellCoordinator Coord;
        public MarimoNotebookDocument Nb => Coord.Notebook;
        public ScenarioStartupController Scenario;
        public SaveGuardController Sgc;
        public SaveGuardOverlay Overlay;
        MethodInfo _onFileNew, _onFileOpen, _onFileSaveAs, _onGuardSave, _onGuardDiscard, _onGuardCancel;
        public void Bind()
        {
            _onFileNew = Ty.GetMethod("OnFileNew", BF);
            _onFileOpen = Ty.GetMethod("OnFileOpen", BF);
            _onFileSaveAs = Ty.GetMethod("OnFileSaveAs", BF);
            _onGuardSave = Ty.GetMethod("OnGuardSave", BF);
            _onGuardDiscard = Ty.GetMethod("OnGuardDiscard", BF);
            _onGuardCancel = Ty.GetMethod("OnGuardCancel", BF);
        }
        public bool Wired => _onFileNew != null && _onFileOpen != null && _onFileSaveAs != null &&
                             _onGuardSave != null && _onGuardDiscard != null && _onGuardCancel != null;
        public void OnFileNew() => _onFileNew.Invoke(Root, null);
        public void OnFileOpen() => _onFileOpen.Invoke(Root, null);
        public void OnFileSaveAs() => _onFileSaveAs.Invoke(Root, null);
        public void OnGuardSave() => _onGuardSave.Invoke(Root, null);
        public void OnGuardDiscard() => _onGuardDiscard.Invoke(Root, null);
        public void OnGuardCancel() => _onGuardCancel.Invoke(Root, null);
    }

    // Compose the REAL workspace root Python-FREE (mirrors AuthorToRunJourneyE2ERunner.ComposeRoot):
    // OpenScene → builtin font → FakeMarimoSynthesizer (optionally FailDecompose) → ResolvePaths → BuildWorkspace.
    static Handle Compose(out string err, bool failDecompose = false)
    {
        err = null;
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        var ty = typeof(BackcastWorkspaceRoot);
        if (root == null) { err = "BackcastWorkspaceRoot missing in scene"; return null; }
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer { FailDecompose = failDecompose });
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var h = new Handle { Root = root, Ty = ty };
        h.Coord = ty.GetField("_coordinator", BF).GetValue(root) as NotebookCellCoordinator;
        h.Scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        h.Sgc = ty.GetField("_saveGuardController", BF).GetValue(root) as SaveGuardController;
        h.Overlay = ty.GetField("_saveGuardOverlay", BF).GetValue(root) as SaveGuardOverlay;
        h.Bind();
        if (h.Coord == null || h.Scenario == null || h.Sgc == null || h.Overlay == null)
        { err = "root seams not built (renamed?)"; return null; }
        if (!h.Wired) { err = "File/Guard ops not found (renamed?)"; return null; }
        return h;
    }

    // dirty the notebook with a 2nd cell + a body edit (structural + content dirty).
    static void DirtyTwoCells(Handle h)
    {
        h.Coord.AddCell();
        h.Nb.Cells[0].SetBody("dirty = 1\n");
    }

    static string Current(Handle h) => h.Ty.GetField("_currentLayoutPath", BF).GetValue(h.Root) as string;

    // path-identity: both sides Path.GetFullPath + OrdinalIgnoreCase (the temporaryCachePath '/' vs SaveAs '\' trap).
    static bool SamePath(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
