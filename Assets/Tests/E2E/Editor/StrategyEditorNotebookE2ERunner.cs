// StrategyEditorNotebookE2ERunner.cs — Strategy Editor / marimo notebook サーフェス（cell-as-floating-window）の
// E2E 回帰ゲート（台本: 同ディレクトリの StrategyEditorNotebookE2ERunner.md）。第二波8本目。throwaway AFK gate
// `StrategyEditorProbe`（Assets/Editor）から git mv＋改名（ADR-0015 の回帰ゲート命名規約。先例 ScenarioStartup=
// findings 0054 / FooterMode=0055 / InfiniteCanvas=0056 / FloatingWindow=0057 / UniverseSidebar=0058 /
// DepthLadder=0059 / Hakoniwa=0060）。12 section を assert 1 行も削らず verbatim 移送し、各 section に台本の
// Action ID を `Covers:` で付与（findings 0061）。Python-FREE（cell 合成/分解は FakeMarimoSynthesizer・marimo
// round-trip 契約を共有）。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod StrategyEditorNotebookE2ERunner.Run -logFile <log>
//   # expect: [E2E STRATEGY NOTEBOOK PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// section ↔ Action ID は各 Section の `Covers:` コメント参照（台本の操作一覧表と双方向に追える）。gate 形は probe の
// Execute()-形（各 section が null=PASS、最初の失敗文字列を返す）をそのまま温存。`EditorApplication.Exit` は
// self-failing gate として無条件化（PASS=Exit(0) / FAIL・例外=Exit(1)）。
//
// SUPPORTING PIN（STRATEGY Action ID に直接対応しない pure core を温存——別サーフェスが正本）:
//   S3 legacy StrategyDocument 原子 .py 書込（AtomicPyFile が §3 を参照）/ S5 StrategyProviderRegistry /
//   S6 layout round-trip（spatial 永続化の正本は Layout/FloatingWindow 台本）/ S7 restore full-replacement /
//   S9 #78 RegistryStrategyFileProvider run-wiring（正本は RunButtonE2ERunner）。verbatim 移送・改名のみ。
//
// 据え置き（台本「カバー状態」）: STRATEGY-11（単一セル placeholder hint）は実 StrategyEditorView+InputField+
// placeholder Graphic harness を要するため本昇格では追加せず 要新規自動化 のまま（findings 0061）。
// STRATEGY-05(scroll 着色)/STRATEGY-18(IME・実キーボード) は HITL専用、STRATEGY-14(click-to-front) は
// FloatingWindow 共有ロジックで 対象外。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class StrategyEditorNotebookE2ERunner
{
    const float EPS = 1e-4f;

    static string TempDir => Path.Combine(Application.temporaryCachePath, "strategy_editor_probe");

    public static void Run()
    {
        string fail = null;
        var spawned = new List<GameObject>();
        try
        {
            ResetTempDir();
            fail = Section1_Highlighter()
                ?? Section2_History()
                ?? Section3_FileModel()
                ?? Section4_Provider()
                ?? Section5_Registry()
                ?? Section6_LayoutRoundTrip()
                ?? Section7_Restore(spawned)
                ?? Section8_MeshColoring(spawned)
                ?? Section9_RegistryRunWiring()
                ?? Section10_NotebookAggregate()
                ?? Section11_SpawnPlacement()
                ?? Section12_Coordinator(spawned)
                ?? Section13_PerCellRun(spawned)
                ?? Section14_Phase4BacktestControl(spawned)
                ?? Section15_Phase5StepResetIdempotency(spawned)
                ?? Section16_PerCellStale(spawned)
                ?? Section17_BlockPopup(spawned)
                ?? Section18_DocumentBadge()
                ?? Section19_RichOutput(spawned)
                ?? Section20_PlaceholderHint(spawned)
                ?? Section21_ConsoleAndDynamicLayout(spawned)
                ?? Section22_ConsoleAuditGaps(spawned)
                ?? Section23_ModeAwareLiveLaunch(spawned)
                ?? Section24_LiveLifecycleEdges(spawned)
                ?? Section25_ModeConditionalVisibility(spawned)
                ?? Section26_ZeroCellsFloor(spawned);
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }
        finally
        {
            foreach (var go in spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            try { ResetTempDir(remove: true); } catch { }
        }

        if (fail == null)
        {
            Debug.Log("[E2E STRATEGY NOTEBOOK PASS] lexical highlighter (keyword/string/comment/number/decorator/definition; " +
                      "triple-unterminated, f-string whole, comment-vs-string, print-not-builtin, surrogate offset, " +
                      "ascending/non-overlap invariant) + edit history (boundary coalescing: insert run / directional " +
                      "backspace+delete / newline standalone / multi-char standalone / redo-clear / save-boundary-undoable / " +
                      "open-clears / no-op-skip / cap-200 drop-oldest) + file model (Open .py-only/existing, atomic UTF-8 " +
                      "save, replace-failure preserves on-disk) + provider 5-condition (dirty->false, save->path, unbound->false) + " +
                      "registry (dup-reject, unregister, ordinal enumeration, multi-instance) + layout round-trip (REAL JsonUtility, " +
                      "additive strategyEditors, on-disk text proof, sanitize null/empty/dup/orphan/missing-path, back-compat, " +
                      "Clone/StructurallyEqual) + restore full-replacement on real windows (state-none->unbound, present->reset+Open, " +
                      "Open-failure keeps window) + #120 TMP per-glyph syntax recolour (real TextMeshProUGUI, OnPreRenderText " +
                      "textInfo.meshInfo[].colors32 by full-source index, Default unchanged, no tag injection) + registry run-wiring (#78 RegistryStrategyFileProvider: unregistered/unbound/dirty/" +
                      "torn-down -> false -> Run blocked; saved editor .py flows through, re-resolved live each call) " +
                      "+ #81 notebook aggregate (fresh=1 empty cell, AddCell dirties, body-edit dirties, SaveAs/Save->Open " +
                      "round-trip preserves body+name+config, #146 last-cell delete reaches 0 cells + X1 persistence floor=1 (empty Save->Open returns 1 empty cell), non-marimo/broken Open REJECTED (marimo-or-error, #113), supplyable " +
                      "5-condition, ResetUnboundEmpty) + SpawnPlacement (anchor-start, diagonal cascade, overlap-allowed, " +
                      "<10 threshold, full-chain clear) + NotebookCellCoordinator (cell0->region_001, AddCell->region_002 spawn, " +
                      "DeleteCell despawn region_002 / hide-dormant region_001, #146 last-cell delete -> 0 cells (region_001 dormant shell), dormant reuse, CapturePositions cell-order) " +
                      "+ #95 Phase 2 土台 per-cell RUN (adopted+spawned both carry a ▶ RUN button via EnsureRunButton find-or-create, " +
                      "idempotent; press routes the run output to ITS window by cell index; pressing one window does not overwrite another; " +
                      "downstream cell output routes to the downstream window) " +
                      "+ #95 Phase 4 bt.replay() control (STRATEGY-21 committed-scenario hand-off + ▶→■ on RUN; " +
                      "STRATEGY-22 2nd RUN rejected while a backtest is in flight; STRATEGY-23 ■ force-stop → ▶ restored, guard clears) " +
                      "+ #95 Phase 5 bt.step() persistence (STRATEGY-24 step press does NOT activate ▶→■ / running guard; " +
                      "STRATEGY-25 same-scenario re-press reuses the cached executor signal (pointer persists); " +
                      "STRATEGY-26 scenario-unset bt.step cell surfaces the guidance error in cell output) " +
                      "+ #95 Phase 6 per-cell stale/block/badge/rich (STRATEGY-27,28 edit/blur restage projects amber ▶ " +
                      "badges by cell index + re-press clears the pressed cell while a stale downstream stays amber; " +
                      "STRATEGY-29 a 2nd RUN while a bt.replay is in flight surfaces the 'already running' block popup, " +
                      "a non-blocked press is silent; STRATEGY-30,31 document-identity badge = basename / '* ' dirty / " +
                      "Untitled on New/Open/edit/Save; STRATEGY-32,33 rich output routes image/png→RawImage " +
                      "(decode+RawImage-activation HITL-only in headless batch; mimetype passthrough AFK), " +
                      "text/markdown+text/html→rich Text, unsupported→labelled plain fallback) " +
                      "+ STRATEGY-11 single-cell host-API placeholder hint (HostApiHint shown only when CellCount==1, " +
                      "cleared at ≥2 cells, restored back at 1, never seeded into Cell.Body — findings 0050) " +
                      "+ #102 console + dynamic output layout (STRATEGY-34..38: per-cell stdout/stderr " +
                      "segments paint into the console block in arrival order with stderr amber-tagged, " +
                      "an empty rich+console body collapses to editor-only — blocks deactivate so the " +
                      "body's VerticalLayoutGroup skips them, populated blocks cap at body * 0.45, " +
                      "cell rebind clears both rich and console panes) " +
                      "+ #102 audit gaps (STRATEGY-39..46 / findings 0079 §6: '&' literal not entity-" +
                      "escaped, multi-cell index→region routing without bleed, re-press replaces (not " +
                      "appends), re-press with empty hides the block, overflow → real ScrollRect with " +
                      "operable verticalNormalizedPosition, first-frame bodyH==0 still paints visibly, " +
                      "'</color>' injection escaped, dormant-reuse race dropped via ListMutated → Invalidate) " +
                      "+ #112 mode-aware launcher (STRATEGY-47..50 / ADR-0025 D3: in LiveAuto+connected a " +
                      "per-cell RUN LAUNCHES a live run via onLiveLaunch (not the Replay lane) and toggles ▶→■; " +
                      "a 2nd press while live-active is rejected; ■ routes to the LIVE stop (not backtest " +
                      "ForceStop) and SyncLiveRunButton restores ▶ when the run terminals; the SAME press in " +
                      "Replay drives a backtest — mode-conditional dispatch) " +
                      "+ #116 live lifecycle edges (STRATEGY-51 start-in-flight deferred-stop: a ■ pressed " +
                      "before the run_id is confirmed is NOT lost — it is deferred and applied once " +
                      "HasActiveRun flips true, then ▶ restores on terminal; STRATEGY-52 dangling reconcile: " +
                      "deleting the launching cell OR File→New (region reuse) drops the ▶/■ tracking without " +
                      "stopping the venue run, a new launch stays blocked by the still-active run, and a deferred " +
                      "stop pending at reconcile time is honored not dropped) " +
                      "+ #138 mode-conditional visibility (STRATEGY-53/54/55 / findings 0110: the strategy_editor " +
                      "authoring surface — ALL cell windows (region_001 + region_002) + the [+] Add Cell button — is " +
                      "hidden in LiveManual and visible in Replay/LiveAuto, the inverse of the order ticket (front-plane " +
                      "exclusivity); hiding is pure visibility — same window instances + geometry preserved across a " +
                      "Replay→LiveManual→Replay round-trip and Save-while-hidden keeps positions (AC5); the toggle " +
                      "re-shows only what it hid, so a dormant region_001 shell is never resurrected) " +
                      "+ #146 zero-cells floor (STRATEGY-56 full delete -> 0 cells / 0 cell windows with region_001 left a dormant " +
                      "never-Destroy shell; STRATEGY-57 0->1 AddCell reuses the dormant region_001 = File->New と同一動線; " +
                      "STRATEGY-58 X1 a 0-cell notebook saved as a header-only .py reopens as exactly ONE empty cell — marimo's " +
                      "load_app floor, mirrored by the fake) " +
                      "— Unity-owned, ADR-0003/0013 capability parity, under Unity Mono");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E STRATEGY NOTEBOOK FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ======================================================================
    // 1. PythonHighlighter
    // Covers: STRATEGY-04 (lexical token 計算)
    // ======================================================================
    static string Section1_Highlighter()
    {
        // def/class definition names.
        var t = PythonHighlighter.Tokenize("def foo():");
        if (!HasExact(t, 0, 3, PythonTokenClass.Keyword)) return "S1: 'def' not keyword [0,3]";
        if (!HasExact(t, 4, 3, PythonTokenClass.Definition)) return "S1: 'foo' not definition [4,3]";

        t = PythonHighlighter.Tokenize("class Bar:");
        if (!HasExact(t, 0, 5, PythonTokenClass.Keyword)) return "S1: 'class' not keyword";
        if (!HasExact(t, 6, 3, PythonTokenClass.Definition)) return "S1: 'Bar' not definition";

        // string literal.
        t = PythonHighlighter.Tokenize("x = \"hello\"");   // " at 4, closing at 10
        if (!HasExact(t, 4, 7, PythonTokenClass.String)) return "S1: \"hello\" not String [4,7]";

        // triple-quoted UNTERMINATED -> String to EOF.
        string tri = "s = \"\"\"abc";   // """ at 4, runs to EOF (len 10)
        t = PythonHighlighter.Tokenize(tri);
        if (!HasExact(t, 4, tri.Length - 4, PythonTokenClass.String)) return "S1: unterminated triple not String-to-EOF";

        // f-string: whole literal is ONE String, no token for the {x} inside.
        string fs = "f\"{x}\"";   // len 6
        t = PythonHighlighter.Tokenize(fs);
        if (!HasExact(t, 0, 6, PythonTokenClass.String)) return "S1: f-string not one String [0,6]";
        if (ClassAt(t, 3) != PythonTokenClass.String) return "S1: f-string interior not String";

        // comment containing a quote: Comment only, NO String.
        string cm = "# \"not a string\"";
        t = PythonHighlighter.Tokenize(cm);
        if (!HasExact(t, 0, cm.Length, PythonTokenClass.Comment)) return "S1: comment-with-quote not whole Comment";
        if (HasClass(t, PythonTokenClass.String)) return "S1: comment produced a String token";

        // string containing a hash: String only, NO Comment.
        string sc = "\"not # comment\"";
        t = PythonHighlighter.Tokenize(sc);
        if (!HasExact(t, 0, sc.Length, PythonTokenClass.String)) return "S1: string-with-hash not whole String";
        if (HasClass(t, PythonTokenClass.Comment)) return "S1: string produced a Comment token";

        // 'print' is NOT a builtin class (shadowable): only the number is tokenized.
        t = PythonHighlighter.Tokenize("print = 1");   // 1 at index 8
        if (ClassAt(t, 0) != null) return "S1: 'print' was coloured (must be Default — no builtin class)";
        if (!HasExact(t, 8, 1, PythonTokenClass.Number)) return "S1: '1' not Number";

        // numbers: hex w/ underscore, float w/ exponent + imaginary suffix.
        t = PythonHighlighter.Tokenize("0xFF_00");
        if (!HasExact(t, 0, 7, PythonTokenClass.Number)) return "S1: 0xFF_00 not whole Number";
        t = PythonHighlighter.Tokenize("1_000.5e-3j");
        if (!HasExact(t, 0, 11, PythonTokenClass.Number)) return "S1: 1_000.5e-3j not whole Number";

        // decorator at line start vs the matrix-mult '@' mid-line (Default).
        t = PythonHighlighter.Tokenize("@deco.sub");
        if (!HasExact(t, 0, 9, PythonTokenClass.Decorator)) return "S1: @deco.sub not Decorator [0,9]";
        t = PythonHighlighter.Tokenize("a @ b");
        if (HasClass(t, PythonTokenClass.Decorator)) return "S1: mid-line '@' wrongly Decorator";

        // surrogate-pair identifier must not shift later offsets (UTF-16 counting).
        string sg = "𠀀 = 1";   // astral ident (2 units) + " = 1"; '1' at index 5
        t = PythonHighlighter.Tokenize(sg);
        if (!HasExact(t, 5, 1, PythonTokenClass.Number)) return "S1: surrogate ident shifted later offsets";

        // invariant on a combined fixture: ascending, non-overlapping, in-range.
        string big = "@app.route\ndef handler(x):\n    # comment\n    s = \"\"\"multi\nline\"\"\"\n    return 0xFF + 1_000.5e2j\n";
        t = PythonHighlighter.Tokenize(big);
        int prevEnd = 0;
        foreach (var tok in t)
        {
            if (tok.length <= 0) return "S1: non-positive token length";
            if (tok.start < prevEnd) return $"S1: tokens overlap/ not ascending at {tok.start} (prevEnd {prevEnd})";
            if (tok.End > big.Length) return "S1: token out of range";
            prevEnd = tok.End;
        }
        return null;
    }

    // ======================================================================
    // 2. EditHistory
    // Covers: STRATEGY-01, STRATEGY-02, STRATEGY-03 (edit/undo/redo history)
    // ======================================================================
    static string Section2_History()
    {
        // (a) coalesce a typing run "" -> a -> ab -> abc into ONE transaction.
        var h = new EditHistory();
        h.Record("", 0, 0, "a", 1, 1);
        h.Record("a", 1, 1, "ab", 2, 2);
        h.Record("ab", 2, 2, "abc", 3, 3);
        if (h.UndoCount != 1) return $"S2a: insert run not coalesced (UndoCount {h.UndoCount})";
        if (!h.Undo(out string txt, out _, out _) || txt != "") return "S2a: undo of insert run != ''";
        if (!h.Redo(out txt, out _, out _) || txt != "abc") return "S2a: redo of insert run != 'abc'";

        // (b) a typed run coalesces; a newline insert is STANDALONE and closes the group.
        h = new EditHistory();
        h.Record("", 0, 0, "a", 1, 1);               // insert (group open)
        h.Record("a", 1, 1, "ab", 2, 2);             // coalesce -> still 1
        h.Record("ab", 2, 2, "ab\n", 3, 3);          // newline -> standalone, group closed -> 2
        h.Record("ab\n", 3, 3, "ab\nc", 4, 4);       // new insert group -> 3
        if (h.UndoCount != 3) return $"S2b: newline not standalone (UndoCount {h.UndoCount})";

        // (c) directional delete coalescing: a backspace run coalesces into one.
        h = new EditHistory();
        h.Record("abc", 3, 3, "ab", 2, 2);           // backspace
        h.Record("ab", 2, 2, "a", 1, 1);             // backspace (continuous) -> coalesce
        if (h.UndoCount != 1) return $"S2c: backspace run not coalesced (UndoCount {h.UndoCount})";

        // switching delete DIRECTION while the caret stays continuous splits the group.
        h = new EditHistory();
        h.Record("abXc", 2, 2, "aXc", 1, 1);         // backspace at caret 2 -> caret 1
        h.Record("aXc", 1, 1, "ac", 1, 1);           // forward-delete at caret 1 (continuous, other dir)
        if (h.UndoCount != 2) return $"S2c: delete-direction switch did not split (UndoCount {h.UndoCount})";

        // (d) multi-char (paste) is standalone; selection-replace is standalone.
        h = new EditHistory();
        h.Record("a", 1, 1, "axxxx", 5, 5);          // paste 4 chars
        h.Record("axxxx", 0, 5, "Y", 1, 1);          // selection-replace (anchor!=focus)
        if (h.UndoCount != 2) return $"S2d: multi-char/replace not standalone (UndoCount {h.UndoCount})";

        // (e) a fresh edit after undo clears the redo branch.
        h = new EditHistory();
        h.Record("", 0, 0, "a", 1, 1);
        h.Undo(out _, out _, out _);
        if (h.RedoCount != 1) return "S2e: redo not available after undo";
        h.Record("", 0, 0, "z", 1, 1);
        if (h.RedoCount != 0) return "S2e: fresh edit did not clear redo";

        // (f) save is a boundary but keeps history undoable.
        h = new EditHistory();
        h.Record("", 0, 0, "ab", 2, 2);
        h.MarkSaveBoundary();
        h.Record("ab", 2, 2, "abc", 3, 3);           // new group (not coalesced into pre-save)
        if (h.UndoCount != 2) return $"S2f: save boundary did not split group (UndoCount {h.UndoCount})";
        if (!h.Undo(out txt, out _, out _) || txt != "ab") return "S2f: undo across save != 'ab'";

        // (g) open/reload clears.
        h.Clear();
        if (h.CanUndo || h.CanRedo) return "S2g: Clear did not wipe history";

        // (h) no-op (text unchanged though selection moved) is not recorded.
        h = new EditHistory();
        h.Record("abc", 1, 1, "abc", 2, 2);
        if (h.UndoCount != 0) return "S2h: selection-only no-op was recorded";

        // (i) cap 200: 201 standalone records -> 200 kept, oldest dropped.
        h = new EditHistory();
        string acc = "";
        for (int k = 0; k < 201; k++)
        {
            string next = acc + "XY";   // 2-char append -> standalone each time
            h.Record(acc, acc.Length, acc.Length, next, next.Length, next.Length);
            acc = next;
        }
        if (h.UndoCount != EditHistory.MaxDepth) return $"S2i: cap not enforced (UndoCount {h.UndoCount})";
        return null;
    }

    // ======================================================================
    // 3. StrategyDocument file model
    // SUPPORTING PIN: legacy StrategyDocument 原子 .py 書込（AtomicPyFile が §3 を参照）。
    //   STRATEGY-17 の atomic save / STRATEGY-12 の provider 契約が乗る pure core。
    // ======================================================================
    static string Section3_FileModel()
    {
        Directory.CreateDirectory(TempDir);
        string py = Path.Combine(TempDir, "strat.py");
        string txt = Path.Combine(TempDir, "notes.txt");
        const string C0 = "def on_bar(self, bar):\n    pass\n";
        File.WriteAllText(py, C0);
        File.WriteAllText(txt, "not python");

        var doc = new StrategyDocument();
        if (!doc.Open(py)) return "S3: Open(existing .py) failed";
        if (doc.Text != C0) return "S3: Open did not load content";
        if (doc.IsDirty) return "S3: freshly opened doc is dirty";
        if (!doc.IsBound) return "S3: opened doc not bound";
        string boundPath = doc.CurrentPath;

        // Open failures leave the document UNCHANGED.
        if (doc.Open(txt)) return "S3: Open(.txt) should fail (extension)";
        if (doc.Open(Path.Combine(TempDir, "missing.py"))) return "S3: Open(missing) should fail";
        if (doc.Open(TempDir)) return "S3: Open(directory) should fail";
        if (doc.Text != C0 || doc.CurrentPath != boundPath) return "S3: failed Open mutated the document";

        // edit -> dirty -> save -> disk == buffer, not dirty.
        const string C1 = "def on_bar(self, bar):\n    self.buy()\n";
        doc.SetText(C1);
        if (!doc.IsDirty) return "S3: SetText did not set dirty";
        if (!doc.Save()) return "S3: Save failed";
        if (doc.IsDirty) return "S3: Save did not clear dirty";
        if (File.ReadAllText(py) != C1) return "S3: on-disk content != buffer after save";

        // atomic replace-failure preserves the on-disk content (directory made read-only).
        const string C2 = "def on_bar(self, bar):\n    self.sell()\n";
        doc.SetText(C2);
        bool forced = false;
        try
        {
            File.SetAttributes(TempDir, File.GetAttributes(TempDir) | FileAttributes.ReadOnly);
            bool saved = doc.Save();
            if (!saved)
            {
                forced = true;
                if (File.ReadAllText(py) != C1) return "S3: replace-failure did NOT preserve on-disk content";
                if (!doc.IsDirty) return "S3: replace-failure cleared dirty (must retain)";
            }
        }
        finally
        {
            File.SetAttributes(TempDir, File.GetAttributes(TempDir) & ~FileAttributes.ReadOnly);
        }
        if (!forced)
            Debug.LogWarning("[E2E STRATEGY NOTEBOOK] S3: could not force a save failure on this platform " +
                             "(read-only dir still writable, e.g. running as root) -> replace-preservation HITL-only.");
        return null;
    }

    // ======================================================================
    // 4. IStrategyFileProvider 5-condition contract
    // Covers: STRATEGY-12 (供給可能性の5条件 — dirty→false / save→path / unbound→false)
    // ======================================================================
    static string Section4_Provider()
    {
        Directory.CreateDirectory(TempDir);
        string py = Path.Combine(TempDir, "prov.py");
        const string C = "SCENARIO = {}\n";
        File.WriteAllText(py, C);

        var doc = new StrategyDocument();
        if (doc.TryGetStrategyFile(out _)) return "S4: unbound document is supplyable";

        if (!doc.Open(py)) return "S4: Open failed";
        if (!doc.TryGetStrategyFile(out string p1)) return "S4: clean opened doc not supplyable";
        if (File.ReadAllText(p1) != C) return "S4: provider path content != buffer (clean)";

        doc.SetText("SCENARIO = {}\n# edit\n");
        if (doc.TryGetStrategyFile(out _)) return "S4: dirty document is supplyable (must be false)";

        if (!doc.Save()) return "S4: Save failed";
        if (!doc.TryGetStrategyFile(out string p2)) return "S4: saved doc not supplyable";
        if (File.ReadAllText(p2) != doc.Text) return "S4: provider path content != buffer (after save)";

        // condition 5: file removed at call time -> not supplyable.
        File.Delete(py);
        if (doc.TryGetStrategyFile(out _)) return "S4: supplyable after the file was deleted";

        // unbound-empty reset -> not supplyable.
        File.WriteAllText(py, C);
        doc.Open(py);
        doc.ResetUnboundEmpty();
        if (doc.TryGetStrategyFile(out _)) return "S4: supplyable after ResetUnboundEmpty";
        return null;
    }

    // ======================================================================
    // 5. StrategyProviderRegistry
    // SUPPORTING PIN: provider registry lookup/ordinal 列挙。STRATEGY Action ID に直接対応しない
    //   （#78 run-wiring の土台 — 正本は RunButtonE2ERunner）。
    // ======================================================================
    static string Section5_Registry()
    {
        var reg = new StrategyProviderRegistry();
        var a = new StrategyDocument();
        var b = new StrategyDocument();
        var c = new StrategyDocument();

        if (!reg.Register("strategy_editor:region_002", a)) return "S5: first register failed";
        if (reg.Register("strategy_editor:region_002", b)) return "S5: duplicate id was accepted";
        if (reg.Count != 1) return "S5: count after dup wrong";

        reg.Register("strategy_editor:region_001", b);
        reg.Register("order", c);
        if (!reg.TryGet("strategy_editor:region_001", out var got) || !ReferenceEquals(got, b)) return "S5: lookup mismatch";

        // deterministic ORDINAL enumeration (not insertion order).
        var ids = reg.WindowIds();
        var expected = new List<string> { "order", "strategy_editor:region_001", "strategy_editor:region_002" };
        expected.Sort(StringComparer.Ordinal);
        if (ids.Count != expected.Count) return "S5: enumeration count wrong";
        for (int i = 0; i < ids.Count; i++) if (ids[i] != expected[i]) return $"S5: enumeration not ordinal at {i} ({ids[i]})";

        if (!reg.Unregister("strategy_editor:region_002")) return "S5: unregister failed";
        if (reg.Contains("strategy_editor:region_002")) return "S5: still present after unregister";
        if (reg.Unregister("nope")) return "S5: unregister of a missing id returned true";
        return null;
    }

    // ======================================================================
    // 9. RegistryStrategyFileProvider — the #78 run-layer wiring (findings 0044 §2-1):
    //    SUPPORTING PIN: #78 WYSIWYR run-wiring。STRATEGY 表面ではなく Run ゲートの土台（正本 RunButtonE2ERunner）。
    //    Run/Step/LiveAuto hold THIS adapter; it re-resolves the editor's live provider through the
    //    registry on every call, so "未ロード/未保存/欠落 → false → Run封鎖" comes for free and a saved
    //    editor .py flows straight through (WYSIWYR). No env-default path anywhere.
    // ======================================================================
    static string Section9_RegistryRunWiring()
    {
        const string WID = "strategy_editor:region_001";
        Directory.CreateDirectory(TempDir);
        string py = Path.Combine(TempDir, "run_wire.py");
        File.WriteAllText(py, "SCENARIO = {}\n");

        var reg = new StrategyProviderRegistry();

        // (a) nothing registered → false (the unloaded case → Run blocked).
        var prov = new RegistryStrategyFileProvider(reg, WID);
        if (prov.TryGetStrategyFile(out _)) return "S9: supplyable with NOTHING registered";

        // (b) null registry / empty id → false (defensive, never throws).
        if (new RegistryStrategyFileProvider(null, WID).TryGetStrategyFile(out _)) return "S9: null registry supplyable";
        if (new RegistryStrategyFileProvider(reg, null).TryGetStrategyFile(out _)) return "S9: null window id supplyable";

        // (c) an UNBOUND document registered → still false (the editor shows nothing to run).
        var doc = new StrategyDocument();
        reg.Register(WID, doc);
        if (prov.TryGetStrategyFile(out _)) return "S9: supplyable while the registered editor is unbound";

        // (d) the editor opens a saved .py → the adapter returns THAT path (WYSIWYR), live through the registry.
        if (!doc.Open(py)) return "S9: Open failed";
        if (!prov.TryGetStrategyFile(out string got) || got != Path.GetFullPath(py))
            return "S9: adapter did not return the editor's saved path (got [" + got + "])";

        // (e) the editor goes dirty → the adapter re-resolves to false on the SAME instance (no stale path).
        doc.SetText("SCENARIO = {}\n# edit\n");
        if (prov.TryGetStrategyFile(out _)) return "S9: dirty editor still supplyable — adapter cached a stale path";

        // (f) unregistered mid-flight → false (window torn down → Run blocked).
        doc.Save();   // clean again so only the unregister can flip it
        if (!prov.TryGetStrategyFile(out _)) return "S9: clean saved editor not supplyable";
        reg.Unregister(WID);
        if (prov.TryGetStrategyFile(out _)) return "S9: supplyable after the editor was unregistered";
        return null;
    }

    // ======================================================================
    // 6. layout round-trip (REAL JsonUtility) + sanitize + back-compat
    // SUPPORTING PIN: layout sidecar round-trip。空間永続化の正本は Layout/FloatingWindow 台本
    //   （STRATEGY-13 の窓位置は CapturePositions=S12、sidecar 書込は MenuBar MENU-05）。
    // ======================================================================
    static string Section6_LayoutRoundTrip()
    {
        Directory.CreateDirectory(TempDir);
        string sidecar = Path.Combine(TempDir, "layout.json");

        // A doc carrying strategyEditors (the #16 additive dimension) + a floating window.
        var mutated = LayoutDocument.Default();
        mutated.floatingWindows.Add(new FloatingWindowLayout(
            "strategy_editor:region_001", FloatingWindowCatalog.KIND_STRATEGY_EDITOR, 10.5f, -20.5f, 400.5f, 300.5f, 0, true));
        mutated.strategyEditors.Add(new StrategyEditorState("strategy_editor:region_001", "/tmp/strat_alpha.py"));
        mutated.strategyEditors.Add(new StrategyEditorState("strategy_editor:region_002", "/tmp/strat_beta.py"));

        if (LayoutDocument.StructurallyEqual(mutated, LayoutDocument.Default(), EPS))
            return "S6: mutated == Default (mutation no-op)";

        LayoutStore.Save(mutated, sidecar);
        string compact = File.ReadAllText(sidecar).Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
        foreach (var needle in new[]
        {
            "\"id\":\"strategy_editor:region_001\"", "strat_alpha.py",
            "\"id\":\"strategy_editor:region_002\"", "strat_beta.py",
        })
            if (!compact.Contains(needle)) return $"S6: on-disk JSON missing {needle}";

        LayoutDocument loaded = LayoutStore.Load(sidecar);
        if (!LayoutDocument.StructurallyEqual(loaded, mutated, EPS)) return "S6: loaded != mutated (lost a field)";
        if (loaded.strategyEditors.Count != 2) return "S6: strategyEditors count wrong after load";

        // sanitize directly on a POCO: null / empty id / empty path drop; dup id first-wins;
        // ORPHAN id and a non-existent path are KEPT (no filesystem check at the store).
        var dirty = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            strategyEditors = new List<StrategyEditorState>
            {
                null,
                new StrategyEditorState("", "/tmp/x.py"),                  // empty id -> drop
                new StrategyEditorState("e1", ""),                          // empty path -> drop
                new StrategyEditorState("e2", "/tmp/keep.py"),              // keep (first e2)
                new StrategyEditorState("e2", "/tmp/dupe.py"),              // duplicate id -> drop
                new StrategyEditorState("orphan", "/does/not/exist.py"),    // orphan + missing path -> KEEP
            },
        };
        LayoutStore.NormalizeStrategyEditors(dirty);
        if (dirty.strategyEditors.Count != 2) return $"S6: sanitize count wrong ({dirty.strategyEditors.Count})";
        if (dirty.FindStrategyEditor("e2") == null || dirty.FindStrategyEditor("e2").filePath != "/tmp/keep.py")
            return "S6: dup id did not keep first";
        if (dirty.FindStrategyEditor("orphan") == null) return "S6: orphan/missing-path entry was dropped (must keep)";

        // back-compat: an old #15 sidecar (floatingWindows present, NO strategyEditors).
        string oldJson =
            "{\"version\":1,\"panels\":[],\"canvasView\":{\"panX\":0,\"panY\":0,\"zoom\":1.5}," +
            "\"floatingWindows\":[{\"id\":\"order\",\"kind\":\"order\",\"x\":0,\"y\":0,\"w\":300,\"h\":200,\"zOrder\":0,\"visible\":true}]}";
        LayoutDocument old = LayoutStore.LoadFromJson(oldJson);
        if (old.strategyEditors == null || old.strategyEditors.Count != 0) return "S6: old sidecar did not yield empty strategyEditors";
        if (old.FindWindow("order") == null) return "S6: old sidecar lost its floatingWindows";
        if (!Approx(old.canvasView.zoom, 1.5f)) return "S6: old sidecar lost its canvasView";

        // Clone independence + StructurallyEqual sensitivity to a strategyEditors change.
        var clone = mutated.Clone();
        if (!LayoutDocument.StructurallyEqual(clone, mutated, EPS)) return "S6: clone != original";
        clone.strategyEditors[0].filePath = "/tmp/changed.py";
        if (LayoutDocument.StructurallyEqual(clone, mutated, EPS)) return "S6: StructurallyEqual blind to a filePath change";
        return null;
    }

    // ======================================================================
    // 7. restore full-replacement on REAL windows / controller
    // SUPPORTING PIN: restore full-replacement（state-none→unbound / Open-failure は窓保持）。
    //   layout 復元の土台 — 正本は Layout 台本。
    // ======================================================================
    static string Section7_Restore(List<GameObject> spawned)
    {
        Directory.CreateDirectory(TempDir);
        string py = Path.Combine(TempDir, "restore.py");
        const string C = "class S:\n    pass\n";
        File.WriteAllText(py, C);
        string missing = Path.Combine(TempDir, "gone.py");

        var layerGo = new GameObject("FloatingWindowLayer", typeof(RectTransform));
        spawned.Add(layerGo);
        var layer = layerGo.GetComponent<RectTransform>();

        var docs = new Dictionary<string, StrategyDocument>();
        var controller = new FloatingWindowController(
            layer, FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var go = new GameObject("W_" + id, typeof(RectTransform));
                spawned.Add(go);
                if (spec.kind == FloatingWindowCatalog.KIND_STRATEGY_EDITOR) docs[id] = new StrategyDocument();
                return go.GetComponent<RectTransform>();
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var doc = LayoutDocument.Default();
        foreach (var (id, z) in new[] { ("e1", 0), ("e2", 1), ("e3", 2) })
            doc.floatingWindows.Add(new FloatingWindowLayout(id, FloatingWindowCatalog.KIND_STRATEGY_EDITOR, 0, 0, 300, 200, z, true));
        doc.strategyEditors.Add(new StrategyEditorState("e1", py));        // valid -> Open succeeds
        doc.strategyEditors.Add(new StrategyEditorState("e2", missing));   // missing -> Open fails
        // e3 has NO strategyEditors entry -> unbound-empty.

        controller.Apply(doc);
        if (controller.Count != 3) return $"S7: expected 3 windows, got {controller.Count}";

        // Pre-bind e3 to a real file to PROVE the state-none restore RESETS prior content.
        docs["e3"].Open(py);
        if (!docs["e3"].IsBound) return "S7: precondition — e3 should be pre-bound";

        // Restore each strategy_editor window's content (full replacement).
        foreach (var id in new[] { "e1", "e2", "e3" })
            StrategyEditorRestore.Apply(docs[id], doc.FindStrategyEditor(id));

        if (!docs["e1"].IsBound || docs["e1"].Text != C) return "S7: e1 (valid path) not restored to disk content";
        if (docs["e1"].IsDirty) return "S7: e1 restored dirty";
        if (docs["e2"].IsBound || docs["e2"].Text != "") return "S7: e2 (missing path) not unbound-empty";
        if (docs["e3"].IsBound || docs["e3"].Text != "") return "S7: e3 (no state) not RESET to unbound-empty (full replacement)";

        // Open-failure must NOT remove the window (FloatingWindowController keeps it).
        if (!controller.Has("e2")) return "S7: window with a failed content Open was removed";
        if (!controller.Has("e3")) return "S7: window with no content state was removed";
        return null;
    }

    // ======================================================================
    // 8. TMP per-glyph syntax recolour (#120 / findings 0096 D4): real TextMeshProUGUI + the
    //    OnPreRenderText recolour hook, asserting textInfo.meshInfo[].colors32 per glyph.
    // Covers: STRATEGY-05 (実 mesh 着色の non-scroll 部 — scroll 着色は HITL専用)
    // ======================================================================
    static string Section8_MeshColoring(List<GameObject> spawned)
    {
        const string src = "def f(): return 1  # c";
        var tokens = PythonHighlighter.Tokenize(src);

        var go = new GameObject("MeshProbeText", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI), typeof(PythonSyntaxMeshEffect));
        spawned.Add(go);
        var text = go.GetComponent<TextMeshProUGUI>();
        Color baseColor = new Color(0.9f, 0.9f, 0.9f, 1f);
        text.color = baseColor;
        var effect = go.GetComponent<PythonSyntaxMeshEffect>();
        effect.SetTokens(tokens);

        // Build a SYNTHETIC TMP_TextInfo (one visible glyph per glyph-producing char, all on material 0,
        // colours pre-seeded to base) and drive the recolour DIRECTLY.  Headless -batchmode -nographics
        // does not run TMP's canvas-driven mesh generation (text.textInfo stays empty), so — exactly as
        // the legacy probe fed a synthetic VertexHelper to ModifyMesh — we feed a deterministic textInfo
        // and assert the per-glyph colours32 the recolour writes.  characterInfo[i].index IS the
        // full-source index (TMP keeps the full text — findings 0096 §#120).  #121 separately gates the
        // real SDF render path (font/material) on the production editor.
        var ti = new TMP_TextInfo();
        ti.characterInfo = new TMP_CharacterInfo[src.Length];
        ti.characterCount = src.Length;
        int rank = 0;
        for (int j = 0; j < src.Length; j++)
        {
            bool vis = !(src[j] == ' ' || src[j] == '\t' || src[j] == '\n' || src[j] == '\r');
            ti.characterInfo[j] = new TMP_CharacterInfo
            {
                index = j,
                isVisible = vis,
                materialReferenceIndex = 0,
                vertexIndex = vis ? rank * 4 : 0,
            };
            if (vis) rank++;
        }
        var colors = new Color32[rank * 4];
        for (int k = 0; k < colors.Length; k++) colors[k] = baseColor;   // pre-seed base (recolour overrides covered glyphs)
        ti.meshInfo = new[] { new TMP_MeshInfo { colors32 = colors } };

        effect.ApplyTokenColours(ti);   // the production recolour hook, fed our synthetic textInfo

        // 'd'(0)=keyword, 'f'(4)=definition, 'r'(9)=keyword, '1'(16)=number, '#'(19)=comment,
        // '('(5)=Default(unchanged base).  token lookup is by FULL-source index directly.
        int checkedGlyphs = 0;
        string err =
            TmpGlyphColor(ti, 0, effect.keyword, ref checkedGlyphs) ??
            TmpGlyphColor(ti, 4, effect.definition, ref checkedGlyphs) ??
            TmpGlyphColor(ti, 9, effect.keyword, ref checkedGlyphs) ??
            TmpGlyphColor(ti, 16, effect.number, ref checkedGlyphs) ??
            TmpGlyphColor(ti, 19, effect.comment, ref checkedGlyphs) ??
            TmpGlyphColor(ti, 5, baseColor, ref checkedGlyphs);   // '(' -> Default unchanged
        if (err != null) return err;
        if (checkedGlyphs == 0)
            return "S8: no visible glyphs were colour-checked (synthetic textInfo had no glyphs)";

        // No tag injection by construction: ApplyTokenColours writes ONLY meshInfo.colors32 — it has no
        // path that mutates the source string (the property the legacy ModifyMesh probe asserted).
        return null;
    }

    // Assert the 4 verts of the glyph at FULL-source index `srcIndex` carry `expected`. null = pass
    // (or the char produces no glyph — whitespace, which TMP gives no quad); increments checkedGlyphs
    // only for a real visible glyph so the caller catches a fully-empty mesh.
    static string TmpGlyphColor(TMP_TextInfo ti, int srcIndex, Color expected, ref int checkedGlyphs)
    {
        for (int i = 0; i < ti.characterCount; i++)
        {
            var ci = ti.characterInfo[i];
            if (ci.index != srcIndex) continue;
            if (!ci.isVisible) return null;   // not a glyph-producing char at this index
            int matIdx = ci.materialReferenceIndex;
            if (matIdx < 0 || matIdx >= ti.meshInfo.Length) return $"S8: glyph[{srcIndex}] material index out of range";
            var colors = ti.meshInfo[matIdx].colors32;
            int vi = ci.vertexIndex;
            if (colors == null || vi + 3 >= colors.Length) return $"S8: glyph[{srcIndex}] mesh not populated";
            for (int k = 0; k < 4; k++)
                if (!ColorApprox(colors[vi + k], expected))
                    return $"S8: glyph[{srcIndex}] vert{k} colour {(Color)colors[vi + k]} != expected {expected}";
            checkedGlyphs++;
            return null;
        }
        return $"S8: source index {srcIndex} not found in textInfo (characterCount {ti.characterCount})";
    }

    // ======================================================================
    // 10. #81 MarimoNotebookDocument aggregate (ADR-0013 / findings 0050) — driven by the
    //     Covers: STRATEGY-01, STRATEGY-10, STRATEGY-12, STRATEGY-15, STRATEGY-16, STRATEGY-17
    //     (notebook 集約: add/edit dirty・#146 last-cell delete -> 0 cells + X1 floor=1・supplyable・
    //      ResetUnboundEmpty(File→New aggregate 側, MenuBar MENU-02 が正本)・Open 分解・Save 合成 round-trip)
    //     Python-FREE FakeMarimoSynthesizer (the SAME round-trip contract the layer-2 pythonnet +
    //     layer-3 marimo golden assert, so a drifting fake is caught mechanically).
    // ======================================================================
    static string Section10_NotebookAggregate()
    {
        Directory.CreateDirectory(TempDir);
        var synth = new FakeMarimoSynthesizer();
        var nb = new MarimoNotebookDocument(synth);

        // fresh notebook: one empty cell (File→New floor=1), unbound, not dirty, not supplyable (unbound).
        if (nb.CellCount != 1) return "S10: fresh notebook not 1 cell";
        if (nb.IsBound || nb.IsDirty) return "S10: fresh notebook bound/dirty";
        if (nb.TryGetStrategyFile(out _)) return "S10: unbound notebook supplyable";

        // AddCell appends + dirties (structural change changes the .py).
        nb.AddCell();
        if (nb.CellCount != 2) return "S10: AddCell did not append";
        if (!nb.IsDirty) return "S10: AddCell did not dirty";

        // SaveAs writes + binds + clears dirty -> supplyable.
        string py = Path.Combine(TempDir, "nb_agg.py");
        if (!nb.SaveAs(py)) return "S10: SaveAs failed";
        if (nb.IsDirty || !nb.IsBound) return "S10: SaveAs did not clear dirty / bind";
        if (!nb.TryGetStrategyFile(out var sp) || sp != Path.GetFullPath(py)) return "S10: saved notebook not supplyable";

        // a cell-body edit dirties the aggregate (Cell.SetBody -> dirty hook) -> not supplyable.
        nb.Cells[0].SetBody("x = 1");
        if (!nb.IsDirty) return "S10: cell body edit did not dirty the notebook";
        if (nb.TryGetStrategyFile(out _)) return "S10: dirty notebook supplyable";

        // Save -> Open round-trips the cell bodies (body fidelity through the seam).
        nb.Cells[1].SetBody("y = x + 1");
        if (!nb.Save()) return "S10: Save failed";
        var nb2 = new MarimoNotebookDocument(synth);
        if (!nb2.Open(py)) return "S10: Open failed";
        if (nb2.CellCount != 2) return "S10: round-trip lost a cell";
        if (nb2.Cells[0].Body != "x = 1" || nb2.Cells[1].Body != "y = x + 1") return "S10: round-trip body mismatch";
        // F4 (#86 review): lock the foreach bind loop on the REAL Decompose leg too. FakeMarimoSynthesizer.Decompose
        // returns `new Cell(...)` instances WITHOUT a MarkDirty hook, so Open()'s foreach is the SINGLE bind site —
        // editing nb2's body after a clean valid-marimo Open MUST flip IsDirty. If a refactor drops the per-cell
        // BindBodyChanged loop, this assertion goes RED (the nb3 wrap-leg assertion below catches it on the wrap
        // path; this one catches it on the real Decompose path — two stakes through the same invariant).
        if (nb2.IsDirty) return "S10/F4: precondition — nb2 should be clean right after Open";
        nb2.Cells[0].SetBody("x = 2");
        if (!nb2.IsDirty) return "S10/F4: valid-marimo Open cell body edit did not dirty the notebook (foreach BindBodyChanged lost)";

        // names are carried opaquely through Open (the #76 named-cell guard; S1 never edits them).
        string named = synth.Synthesize(new List<Cell> { new Cell("a = 1", "_config", "{}"), new Cell("b = 2", "_strat", "{}") });
        string npath = Path.Combine(TempDir, "nb_named.py");
        File.WriteAllText(npath, named);
        var nb4 = new MarimoNotebookDocument(synth);
        if (!nb4.Open(npath)) return "S10: Open of named notebook failed";
        if (nb4.CellCount != 2 || nb4.Cells[0].Name != "_config" || nb4.Cells[1].Name != "_strat")
            return "S10: cell names not carried through Open (seam dropped name+config)";

        // #146 (ADR-0033 supersedes ADR-0013 D5): the 0-cell floor is LIFTED — the last cell CAN now
        // be removed, reaching 0 cells (the empty-canvas state). delete-the-production-logic litmus:
        // re-add `if (_cells.Count <= 1) return false;` to MarimoNotebookDocument.RemoveCell and THIS
        // goes RED (RemoveCell returns false at 1 cell).
        var single = new MarimoNotebookDocument(synth);
        if (!single.RemoveCell(single.Cells[0])) return "S10/#146: the last cell must now be removable (0-cell floor lifted)";
        if (single.CellCount != 0) return "S10/#146: removing the last cell did not reach 0 cells";
        if (single.RemoveCell(null)) return "S10/#146: RemoveCell(null) must still be false (genuine anomaly)";
        if (!nb2.RemoveCell(nb2.Cells[0])) return "S10: RemoveCell on a 2-cell notebook failed";
        if (nb2.CellCount != 1) return "S10: RemoveCell did not shrink";
        if (!nb2.RemoveCell(nb2.Cells[0])) return "S10/#146: RemoveCell down to 0 must succeed";
        if (nb2.CellCount != 0) return "S10/#146: notebook did not reach 0 cells";

        // X1 persistence floor = 1 (marimo-derived, ADR-0033 D2): a 0-cell notebook saves to a
        // header-only `.py`, but marimo's load_app inflates an empty valid-marimo file back to ONE
        // empty cell, so Save→Open round-trips 0 → 1 (NOT 0). The fake mirrors this marimo floor.
        // delete-the-production-logic litmus: drop the empty→1 inflation in FakeMarimoSynthesizer.Decompose
        // and THIS goes RED (the round-trip comes back 0, or the Open 0-cell guard rejects it).
        string emptyPy = Path.Combine(TempDir, "nb_empty.py");
        if (!single.SaveAs(emptyPy)) return "S10/#146 X1: SaveAs of a 0-cell notebook failed (synth must accept [])";
        if (single.CellCount != 0) return "S10/#146 X1: SaveAs mutated the live 0-cell count";
        var reopened = new MarimoNotebookDocument(synth);
        if (!reopened.Open(emptyPy)) return "S10/#146 X1: Open of a saved empty notebook failed (LastError=" + (reopened.LastError ?? "<null>") + ")";
        if (reopened.CellCount != 1) return "S10/#146 X1: empty notebook did not reopen as exactly 1 cell (marimo floor=1)";
        if (reopened.Cells[0].Body != "") return "S10/#146 X1: the reopened floor cell must be empty";

        // #113 (reverses findings 0054 §D1): the editor is "marimo or error" at Open time. Open of a
        // NON-MARIMO `.py` (Decompose -> null, "not a marimo notebook") is NO LONGER wrapped into a
        // 1-cell notebook — it is an explicit Open FAILURE that leaves the buffer untouched.
        // delete-the-production-logic litmus: re-add the `?? new List<Cell> { new Cell(content,...) }`
        // wrap leg to MarimoNotebookDocument.Open and THIS section goes RED (Open returns true).
        var failSynth = new FakeMarimoSynthesizer { FailDecompose = true };
        var nb3 = new MarimoNotebookDocument(failSynth);
        string rawPath = Path.Combine(TempDir, "nb_nonmarimo.py");
        string rawContent = "class V19MorningStrategy(Strategy):\n    def on_bar(self, bar):\n        pass\n";
        File.WriteAllText(rawPath, rawContent);
        string nb3Before = nb3.Cells[0].Body;
        if (nb3.Open(rawPath)) return "S10/#113: Open of a non-marimo .py should FAIL (marimo-or-error), not wrap as 1 cell";
        if (nb3.LastError != "not a marimo notebook") return "S10/#113: non-marimo Open LastError should be 'not a marimo notebook' (got '" + (nb3.LastError ?? "<null>") + "')";
        if (nb3.IsBound) return "S10/#113: a failed non-marimo Open must NOT bind the document";
        if (nb3.CellCount != 1 || nb3.Cells[0].Body != nb3Before) return "S10/#113: a failed non-marimo Open mutated the buffer (must leave it untouched)";

        // #113 AC#2: a BROKEN-SYNTAX source surfaces as a DISTINCT 'syntax error: ...' (never masked as
        // a silent wrap). The fake models decompose_json raising SyntaxError via SyntaxErrorDetail.
        var synSynth = new FakeMarimoSynthesizer { SyntaxErrorDetail = "invalid syntax (line 1)" };
        var nbSyn = new MarimoNotebookDocument(synSynth);
        string brokenPath = Path.Combine(TempDir, "nb_broken.py");
        File.WriteAllText(brokenPath, "def (:\n");
        if (nbSyn.Open(brokenPath)) return "S10/#113: Open of a broken-syntax .py should FAIL";
        if (nbSyn.LastError == null || !nbSyn.LastError.StartsWith("syntax error")) return "S10/#113: broken-syntax Open LastError should start with 'syntax error' (distinct from not-a-marimo), got '" + (nbSyn.LastError ?? "<null>") + "'";
        if (nbSyn.IsBound) return "S10/#113: a failed broken-syntax Open must NOT bind the document";

        // #113: a non-marimo Open is REFUSED regardless of dirty state — and because it fails before
        // touching `_cells`, an unsaved (dirty) buffer is intrinsically preserved (the old #86 F1
        // dirty-refuse / #87 discardDirty seam is gone; protection is now structural).
        var nb5 = new MarimoNotebookDocument(failSynth);
        nb5.AddCell();                                            // 2 cells
        nb5.Cells[0].SetBody("unsaved_work = 42");                // dirty
        if (!nb5.IsDirty) return "S10/#113: precondition — nb5 should be dirty";
        int beforeCount = nb5.CellCount;
        string beforeBody0 = nb5.Cells[0].Body;
        string beforeBody1 = nb5.Cells[1].Body;
        string rawPath2 = Path.Combine(TempDir, "nb_nonmarimo_dirty.py");
        File.WriteAllText(rawPath2, "class Other(Strategy):\n    pass\n");
        if (nb5.Open(rawPath2)) return "S10/#113: dirty notebook Open(non-marimo) must fail (marimo-or-error)";
        if (nb5.LastError != "not a marimo notebook") return "S10/#113: dirty non-marimo Open LastError mismatch";
        if (nb5.CellCount != beforeCount) return "S10/#113: refused Open mutated cell count";
        if (nb5.Cells[0].Body != beforeBody0 || nb5.Cells[1].Body != beforeBody1) return "S10/#113: refused Open mutated cell bodies";
        if (!nb5.IsDirty) return "S10/#113: refused Open cleared the dirty flag";

        // A valid marimo notebook still opens cleanly after the rejections above (no regression, AC#4):
        // bind the prior round-tripped `py` (a real fake-marimo blob) and confirm it replaces the buffer.
        var nb6 = new MarimoNotebookDocument(synth);
        if (!nb6.Open(py)) return "S10/#113: a VALID marimo Open should still succeed (LastError=" + (nb6.LastError ?? "<null>") + ")";
        if (nb6.LastError != null) return "S10/#113: a successful marimo Open must clear LastError";
        if (!nb6.IsBound || nb6.IsDirty) return "S10/#113: a valid marimo Open should bind + be clean";

        // ResetUnboundEmpty = one empty cell, unbound (File→New).
        nb2.ResetUnboundEmpty();
        if (nb2.CellCount != 1 || nb2.Cells[0].Body != "" || nb2.IsBound) return "S10: ResetUnboundEmpty not 1 empty unbound cell";
        return null;
    }

    // ======================================================================
    // 11. #81 SpawnPlacement.Next — pure cascade (marimo calcSpawnPosition parity).
    // Covers: STRATEGY-06, STRATEGY-07 (セル追加/dormant 再利用の cascade spawn 位置)
    // ======================================================================
    static string Section11_SpawnPlacement()
    {
        var anchor = new Vector2(100f, 200f);

        // no existing windows -> the anchor itself.
        if ((SpawnPlacement.Next(new List<Vector2>(), anchor, 30f) - anchor).sqrMagnitude > EPS)
            return "S11: empty set should spawn at the anchor";

        // a collision at the anchor -> diagonal cascade by (+offset,+offset).
        if ((SpawnPlacement.Next(new List<Vector2> { anchor }, anchor, 30f) - new Vector2(130f, 230f)).sqrMagnitude > EPS)
            return "S11: single collision did not cascade diagonally";

        // a full diagonal chain -> the next free slot past the chain.
        var chain = new List<Vector2> { anchor, new Vector2(130f, 230f), new Vector2(160f, 260f) };
        if ((SpawnPlacement.Next(chain, anchor, 30f) - new Vector2(190f, 290f)).sqrMagnitude > EPS)
            return "S11: cascade did not clear a full diagonal chain";

        // a far window does NOT displace the spawn (overlap is allowed; threshold < 10).
        if ((SpawnPlacement.Next(new List<Vector2> { new Vector2(1000f, 1000f) }, anchor, 30f) - anchor).sqrMagnitude > EPS)
            return "S11: a far window wrongly displaced the spawn (overlap must be allowed)";

        // a near window (within the <10 threshold) DOES trigger a cascade.
        if ((SpawnPlacement.Next(new List<Vector2> { new Vector2(105f, 205f) }, anchor, 30f) - anchor).sqrMagnitude < EPS)
            return "S11: a near (<10) window did not trigger a cascade";
        return null;
    }

    // ======================================================================
    // 12. #81 NotebookCellCoordinator — region_id<->Cell binding + window lifecycle on REAL
    //     Covers: STRATEGY-06, STRATEGY-07, STRATEGY-08, STRATEGY-09, STRATEGY-10, STRATEGY-13, STRATEGY-16
    //     (add region_002 / dormant reuse / delete despawn・hide-dormant / #146 last-cell -> 0 cells + 0→1 reuse /
    //      CapturePositions cell-order / Open orphan 一掃)
    //     (bare-RT) windows: cell0->region_001, AddCell->region_002 spawn, delete routing
    //     (despawn region_002 / hide-dormant region_001), #146 last-cell delete -> 0 cells, dormant reuse, positions.
    // ======================================================================
    static string Section12_Coordinator(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string R2 = "strategy_editor:region_002";

        var layerGo = new GameObject("FWLayer12", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) => { var go = new GameObject("W_" + id, typeof(RectTransform)); spawned.Add(go); return go.GetComponent<RectTransform>(); },
            go => UnityEngine.Object.DestroyImmediate(go));

        // adopt the scene-authored region_001 shell.
        var adoptGo = new GameObject("region001", typeof(RectTransform));
        spawned.Add(adoptGo);
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptGo.GetComponent<RectTransform>());

        var synth = new FakeMarimoSynthesizer();
        var nb = new MarimoNotebookDocument(synth);
        var coord = new NotebookCellCoordinator(nb, controller, _ => null, () => Vector2.zero, new Vector2(520f, 380f));

        // sync the default notebook -> cell 0 bound to region_001.
        coord.SyncWindowsToNotebook(null);
        if (!controller.Has(R1)) return "S12: region_001 missing after sync";
        if (coord.RegionOf(nb.Cells[0]) != R1) return "S12: cell 0 not bound to region_001";

        // AddCell -> a region_002 window spawned + bound.
        var c2 = coord.AddCell();
        if (coord.RegionOf(c2) != R2) return "S12: 2nd cell not region_002 (got " + coord.RegionOf(c2) + ")";
        if (!controller.Has(R2)) return "S12: region_002 window not spawned";

        // DeleteCell(region_002) -> despawned; notebook back to 1 cell.
        if (!coord.DeleteCell(R2)) return "S12: DeleteCell(region_002) failed";
        if (controller.Has(R2)) return "S12: region_002 not despawned on delete";
        if (nb.CellCount != 1) return "S12: notebook not 1 cell after delete";

        // #146 (ADR-0033): deleting the LAST cell now SUCCEEDS → 0 cells / 0 windows. region_001 is the
        // never-Destroy shell (ADR-0013 D4), so the last delete HIDES it (dormant), never destroys it —
        // the only authoring affordance left is the screen-fixed, root-owned [+] Add Cell button.
        // delete-the-production-logic litmus: re-add the `_cells.Count<=1` floor and THIS goes RED.
        if (!coord.DeleteCell(R1)) return "S12/#146: deleting the last cell must now succeed (0-cell floor lifted)";
        if (nb.CellCount != 0) return "S12/#146: notebook not 0 cells after deleting the last cell";
        if (!controller.Has(R1)) return "S12/#146: region_001 shell destroyed by the last delete (must hide, dormant)";
        if (controller.RectOf(R1).gameObject.activeSelf) return "S12/#146: region_001 not dormant (still active) at 0 cells";

        // 0 → 1: AddCell from an empty canvas reuses the DORMANT region_001 (File→New と同一動線, ADR-0013 D4).
        var reborn = coord.AddCell();
        if (coord.RegionOf(reborn) != R1) return "S12/#146: AddCell from 0 cells did not reuse the dormant region_001";
        if (!controller.RectOf(R1).gameObject.activeSelf) return "S12/#146: reused region_001 not re-activated from 0 cells";
        if (nb.CellCount != 1) return "S12/#146: notebook not 1 cell after re-add from 0";

        // with 2 cells, delete the region_001 cell -> region_001 HIDDEN (dormant), NOT destroyed.
        coord.AddCell();   // region_002 again
        if (!coord.DeleteCell(R1)) return "S12: delete region_001 cell (2-cell) failed";
        if (!controller.Has(R1)) return "S12: region_001 shell destroyed (must hide, dormant)";
        if (controller.RectOf(R1).gameObject.activeSelf) return "S12: dormant region_001 still active";

        // AddCell reuses the dormant region_001 shell (re-activated).
        var c4 = coord.AddCell();
        if (coord.RegionOf(c4) != R1) return "S12: AddCell did not reuse the dormant region_001";
        if (!controller.RectOf(R1).gameObject.activeSelf) return "S12: reused region_001 not re-activated";

        // CapturePositions is cell-order parallel (regenerated from live).
        if (coord.CapturePositions().Count != nb.CellCount) return "S12: CapturePositions count != cell count";
        return null;
    }

    // ======================================================================
    // 26. #146 zero-cells floor (ADR-0033 / findings 0114) — the FULL-delete → 0-window journey on
    //     REAL (bare-RT) windows, the 0→1 re-spawn (File→New と同一動線), and the X1 persistence floor.
    //     Covers: STRATEGY-56 (全 cell 削除 → 0 cell / 0 窓・region_001 dormant・shell 保全),
    //             STRATEGY-57 (0 → 1: AddCell が dormant region_001 を再利用),
    //             STRATEGY-58 (空ノート Save → Open は marimo 床=1 で空セル 1 枚に戻る・X1)
    //     Emits a per-Action-ID single-token PASS tag at each milestone so run-all-tests' rollup
    //     (`scripts/E2ERollup.ps1`, single-token `[A-Z0-9-]+`) records STRATEGY-56/57/58 — the surface
    //     summary tag `[E2E STRATEGY NOTEBOOK PASS]` has spaces and is NOT picked up by the rollup.
    // ======================================================================
    static string Section26_ZeroCellsFloor(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string R2 = "strategy_editor:region_002";

        var layerGo = new GameObject("FWLayer26", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) => { var go = new GameObject("W_" + id, typeof(RectTransform)); spawned.Add(go); return go.GetComponent<RectTransform>(); },
            go => UnityEngine.Object.DestroyImmediate(go));

        // adopt the scene-authored region_001 shell (never-Destroy).
        var adoptGo = new GameObject("region001_26", typeof(RectTransform));
        spawned.Add(adoptGo);
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptGo.GetComponent<RectTransform>());

        Directory.CreateDirectory(TempDir);
        var synth = new FakeMarimoSynthesizer();
        var nb = new MarimoNotebookDocument(synth);
        var coord = new NotebookCellCoordinator(nb, controller, _ => null, () => Vector2.zero, new Vector2(520f, 380f));

        // 2 windows: cell0 -> region_001, cell1 -> region_002 (the確定生成 of two cells).
        coord.SyncWindowsToNotebook(null);
        coord.AddCell();
        if (nb.CellCount != 2 || !controller.Has(R1) || !controller.Has(R2))
            return "S26: precondition — expected region_001 + region_002 (2 windows)";

        // ── STRATEGY-56: delete BOTH cells → 0 cells / 0 cell windows. region_002 despawns; region_001
        // (never-Destroy shell) goes dormant (hidden but alive). The [+] Add Cell button is root-owned
        // and screen-fixed (not a cell window), so it is structurally untouched by cell deletion.
        if (!coord.DeleteCell(R2)) return "S26/STRATEGY-56: DeleteCell(region_002) failed";
        if (controller.Has(R2)) return "S26/STRATEGY-56: region_002 not despawned";
        if (!coord.DeleteCell(R1)) return "S26/STRATEGY-56: deleting the LAST cell (region_001) must now succeed";
        if (nb.CellCount != 0) return "S26/STRATEGY-56: notebook did not reach 0 cells";
        if (coord.CellOf(R1) != null || coord.CellOf(R2) != null) return "S26/STRATEGY-56: a region still bound to a cell at 0 cells";
        if (!controller.Has(R1)) return "S26/STRATEGY-56: region_001 shell destroyed (must stay as a dormant shell)";
        if (controller.RectOf(R1).gameObject.activeSelf) return "S26/STRATEGY-56: region_001 not dormant (still active) at 0 cells";
        Debug.Log("[E2E STRATEGY-56 PASS] full delete reaches 0 cells / 0 cell windows; region_001 dormant (shell preserved)");

        // ── STRATEGY-57: 0 → 1. AddCell from the empty canvas reuses the dormant region_001 (the SAME
        // host + path as File→New, ADR-0013 D4), re-activated, exactly one window.
        var reborn = coord.AddCell();
        if (nb.CellCount != 1) return "S26/STRATEGY-57: notebook not 1 cell after re-add from 0";
        if (coord.RegionOf(reborn) != R1) return "S26/STRATEGY-57: 0→1 AddCell did not reuse the dormant region_001 (got " + coord.RegionOf(reborn) + ")";
        if (!controller.RectOf(R1).gameObject.activeSelf) return "S26/STRATEGY-57: reused region_001 not re-activated";
        if (controller.Has(R2)) return "S26/STRATEGY-57: a stray region_002 window exists after a single re-add";
        Debug.Log("[E2E STRATEGY-57 PASS] 0->1 AddCell reuses dormant region_001 (File->New と同一動線)");

        // ── STRATEGY-58 (X1): a 0-cell notebook saves as a header-only `.py`, but marimo's load_app
        // inflates an empty valid-marimo file back to ONE empty cell, so Save→Open round-trips 0 → 1
        // (NOT 0). Drive it through the coordinator's own Open path (SyncWindowsToNotebook) so the
        // window rebuild also lands the floor cell on region_001.
        var zero = new MarimoNotebookDocument(synth);
        if (zero.RemoveCell(zero.Cells[0]) == false || zero.CellCount != 0)
            return "S26/STRATEGY-58: could not bring a fresh notebook to 0 cells";
        string emptyPy = Path.Combine(TempDir, "nb_zero_floor.py");
        if (!zero.SaveAs(emptyPy)) return "S26/STRATEGY-58: SaveAs of a 0-cell notebook failed";

        var layerGo2 = new GameObject("FWLayer26b", typeof(RectTransform));
        spawned.Add(layerGo2);
        var controller2 = new FloatingWindowController(
            layerGo2.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) => { var go = new GameObject("W2_" + id, typeof(RectTransform)); spawned.Add(go); return go.GetComponent<RectTransform>(); },
            go => UnityEngine.Object.DestroyImmediate(go));
        var adoptGo2 = new GameObject("region001_26b", typeof(RectTransform));
        spawned.Add(adoptGo2);
        controller2.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptGo2.GetComponent<RectTransform>());
        var nb2 = new MarimoNotebookDocument(synth);
        var coord2 = new NotebookCellCoordinator(nb2, controller2, _ => null, () => Vector2.zero, new Vector2(520f, 380f));

        if (!coord2.Open(emptyPy, null)) return "S26/STRATEGY-58: reopening the saved empty notebook failed (LastError=" + (nb2.LastError ?? "<null>") + ")";
        if (nb2.CellCount != 1) return "S26/STRATEGY-58: empty notebook did not reopen as exactly 1 cell (marimo floor=1)";
        if (nb2.Cells[0].Body != "") return "S26/STRATEGY-58: the reopened floor cell must be empty";
        if (!controller2.Has(R1) || !controller2.RectOf(R1).gameObject.activeSelf)
            return "S26/STRATEGY-58: the floor cell did not land on an active region_001 window";
        Debug.Log("[E2E STRATEGY-58 PASS] empty notebook Save->Open returns 1 empty cell (X1 marimo floor=1)");
        return null;
    }

    // ======================================================================
    // 13. #95 Phase 2 土台 — per-cell RUN button + index->window output routing.
    //     Covers: STRATEGY-19 (adopted+spawned both carry a ▶ RUN via EnsureRunButton find-or-create,
    //              idempotent), STRATEGY-20 (press routes the run output to ITS window by cell index;
    //              a downstream cell's output routes to the downstream window; pressing one window
    //              does not overwrite another).
    //     Python-FREE: a fake INotebookCellExecutor returns canned output keyed by the pressed index
    //     (the real marimo reactive correctness is the pytest gate test_notebook_interactive_run.py).
    //     REAL StrategyEditorView output panes (StrategyEditorContentBuilder) + REAL NotebookRunController
    //     + a synchronous NotebookRunLane, so the production button->controller->lane->view path runs.
    // ======================================================================
    static string Section13_PerCellRun(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string R2 = "strategy_editor:region_002";
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer13", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rootRt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rootRt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rootRt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        // adopt the scene-authored region_001 shell — a real frame + view (so it owns an output pane).
        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S13: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var synth = new FakeMarimoSynthesizer();
        var nb = new MarimoNotebookDocument(synth);
        nb.AddCell();   // 2 cells -> region_001 + region_002
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);   // cell0->region_001, cell1->region_002 spawn
        if (coord.RegionOf(nb.Cells[0]) != R1 || coord.RegionOf(nb.Cells[1]) != R2)
            return "S13: precondition — cells not bound to region_001/region_002";
        if (!views.ContainsKey(R2)) return "S13: region_002 view not built on spawn";

        var lane = new NotebookRunLane(new _FakeCellExecutor(), startWorker: false);   // synchronous
        var run = new NotebookRunController(coord, r => views.TryGetValue(r, out var v) ? v : null, lane);

        // STRATEGY-19: both windows carry a ▶ RUN button (find-or-create), idempotent.
        var btn1 = StrategyEditorWindowFrame.EnsureRunButton(controller.RectOf(R1), font);
        if (btn1 == null) return "S13/STRATEGY-19: adopted region_001 has no RUN button";
        var btn2 = StrategyEditorWindowFrame.EnsureRunButton(controller.RectOf(R2), font);
        if (btn2 == null) return "S13/STRATEGY-19: spawned region_002 has no RUN button";
        if (!ReferenceEquals(StrategyEditorWindowFrame.EnsureRunButton(controller.RectOf(R1), font), btn1))
            return "S13/STRATEGY-19: EnsureRunButton not idempotent (duplicate button on region_001)";
        btn1.onClick.AddListener(() => run.RunCell(R1));
        btn2.onClick.AddListener(() => run.RunCell(R2));

        // Bind cleared any output; both panes start empty.
        if (!string.IsNullOrEmpty(views[R1].CurrentOutput) || !string.IsNullOrEmpty(views[R2].CurrentOutput))
            return "S13: precondition — output panes should start empty";

        // STRATEGY-20a: press region_001 (cell 0). The fake routes cell 0's output to region_001 AND a
        // downstream cell 1's output to region_002 (proves index->window routing for a multi-cell run).
        btn1.onClick.Invoke();
        run.DrainAndRoute();
        if (views[R1].CurrentOutput != "out-0") return "S13/STRATEGY-20: region_001 did not show its own (cell 0) output, got [" + views[R1].CurrentOutput + "]";
        if (views[R2].CurrentOutput != "down-1") return "S13/STRATEGY-20: downstream output not routed to region_002 (cell 1), got [" + views[R2].CurrentOutput + "]";

        // STRATEGY-20b: press region_002 (cell 1). Only cell 1 runs; region_002 updates, region_001 is
        // UNCHANGED (litmus: routing is by cell index, not "always the first window").
        btn2.onClick.Invoke();
        run.DrainAndRoute();
        if (views[R2].CurrentOutput != "out-1") return "S13/STRATEGY-20: region_002 did not show its own (cell 1) output, got [" + views[R2].CurrentOutput + "]";
        if (views[R1].CurrentOutput != "out-0") return "S13/STRATEGY-20: pressing region_002 overwrote region_001's output (routing collapsed to one window)";

        // STRATEGY-20c: a run queued against the current notebook, then Invalidate() (File→Open/New
        // replaced the notebook mid-flight), must be DROPPED at drain — not painted into the new
        // same-index cell. Sentinels prove the stale result never lands (generation-token guard).
        views[R1].SetOutput("keep-1");
        views[R2].SetOutput("keep-2");
        btn1.onClick.Invoke();   // queues a run that WOULD write region_001 + region_002
        run.Invalidate();        // notebook replaced before the result is drained
        run.DrainAndRoute();
        if (views[R1].CurrentOutput != "keep-1" || views[R2].CurrentOutput != "keep-2")
            return "S13/STRATEGY-20: a run invalidated mid-flight (notebook replaced) still painted its output";

        lane.Dispose();
        return null;
    }

    // ======================================================================
    // 14. #95 Phase 4 — bt.replay() run CONTROL: scenario hand-off + running guard + stop (▶→■).
    //     Covers STRATEGY-21 (a bt.replay press hands the committed scenario to the backend and the
    //     cell enters RUNNING: ▶→■), STRATEGY-22 (a 2nd RUN while a backtest is in flight is REJECTED,
    //     not queued — ADR-0016 D3), STRATEGY-23 (■ requests a force-stop; draining the result clears
    //     the guard and restores ▶, and a fresh press is accepted again).
    //     Python-FREE: a recording fake executor captures the scenario JSON; the REAL controller +
    //     REAL StrategyEditorWindowFrame glyph toggle are exercised. The real bt drive (run_cell→bt→
    //     Hakoniwa), pacing and cross-thread stop are the pytest gate test_notebook_replay_afk.py.
    // ======================================================================
    static string Section14_Phase4BacktestControl(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string SCN = "{\"instruments\":[\"8918.TSE\"],\"granularity\":\"Daily\"}";
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer14", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S14: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var nb = new MarimoNotebookDocument(new FakeMarimoSynthesizer());
        nb.Cells[0].SetBody("for bar in bt.replay():\n    bt.submit_market(100)\n");   // a B2 drive cell
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);
        if (coord.RegionOf(nb.Cells[0]) != R1) return "S14: precondition — cell0 not bound to region_001";

        var btn = StrategyEditorWindowFrame.EnsureRunButton(controller.RectOf(R1), font);
        if (btn == null) return "S14: region_001 has no RUN button";
        if (GlyphText(btn) != "▶") return "S14: precondition — RUN button does not start as ▶";

        string gotScenario = null;
        var exec = new _RecordingExecutor(s => gotScenario = s);
        var errors = new List<string>();
        int stopCount = 0;
        var runningEvents = new List<string>();

        var lane = new NotebookRunLane(exec, startWorker: false);   // synchronous: Submit runs inline
        var run = new NotebookRunController(
            coord, r => views.TryGetValue(r, out var v) ? v : null, lane,
            msg => errors.Add(msg),
            () => SCN,                                   // scenarioJsonProvider
            () => stopCount++,                           // onStop (force-stop the in-flight backtest)
            (region, running) =>                          // onRunningChanged: ▶/■ toggle
            {
                runningEvents.Add(region + ":" + running);
                StrategyEditorWindowFrame.SetRunButtonGlyph(btn, running);
            });

        // STRATEGY-21: a bt.replay press hands the committed scenario to the backend and enters RUNNING.
        run.RunCell(R1);
        if (gotScenario != SCN) return "S14/STRATEGY-21: executor did not receive the committed scenario JSON, got [" + gotScenario + "]";
        if (!run.IsBacktestRunning) return "S14/STRATEGY-21: controller not RUNNING after a bt.replay press";
        if (runningEvents.Count != 1 || runningEvents[0] != R1 + ":True") return "S14/STRATEGY-21: running-changed(region,true) not fired";
        if (GlyphText(btn) != "■") return "S14/STRATEGY-21: ▶ did not toggle to ■ while running";

        // STRATEGY-22: a 2nd RUN while a backtest is in flight is REJECTED (no second executor call).
        int callsBefore = exec.Calls;
        run.RunCell(R1);
        if (exec.Calls != callsBefore) return "S14/STRATEGY-22: a 2nd RUN ran while a backtest was in flight (guard failed)";
        if (errors.Count == 0) return "S14/STRATEGY-22: the rejected 2nd RUN surfaced no message";

        // STRATEGY-23: ■ requests a force-stop; draining the result clears the guard + restores ▶.
        run.StopRunning();
        if (stopCount != 1) return "S14/STRATEGY-23: ■ press did not request a force-stop";
        run.DrainAndRoute();
        if (run.IsBacktestRunning) return "S14/STRATEGY-23: still RUNNING after the result drained";
        if (GlyphText(btn) != "▶") return "S14/STRATEGY-23: ■ did not restore to ▶ after the run finished";
        // a fresh press is accepted again (the guard is not stuck).
        run.RunCell(R1);
        if (!run.IsBacktestRunning) return "S14/STRATEGY-23: a new run was not accepted after the prior one finished";
        run.DrainAndRoute();

        lane.Dispose();
        return null;
    }

    // ======================================================================
    // Section 23: #112 ADR-0025 D3 — per-cell RUN is the MODE-AWARE launcher
    //     Covers STRATEGY-47 (in LiveAuto + connected a press LAUNCHES a live run via onLiveLaunch,
    //     NOT the Replay backtest lane; the cell toggles ▶→■), STRATEGY-48 (a 2nd press while a live
    //     run is active is rejected), STRATEGY-49 (■ → StopRunning routes to the LIVE stop, not the
    //     backtest ForceStop; SyncLiveRunButton restores ▶ when the run terminals), STRATEGY-50 (the
    //     SAME press in Replay drives a backtest — the dispatch is mode-conditional).
    //     Python-FREE: fake live callbacks model HasActiveRun∨start-in-flight; the REAL register→start
    //     + the cell parity are pinned in python (test_v19_cell_auto_parity / test_cell_auto_bridge_roundtrip).
    // ======================================================================
    static string Section23_ModeAwareLiveLaunch(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string SCN = "{\"instruments\":[\"8918.TSE\"],\"granularity\":\"Daily\"}";
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer23", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S23: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var nb = new MarimoNotebookDocument(new FakeMarimoSynthesizer());
        nb.Cells[0].SetBody("for bar in bt.replay():\n    bt.submit_market(100)\n");
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);
        if (coord.RegionOf(nb.Cells[0]) != R1) return "S23: precondition — cell0 not bound to region_001";

        var btn = StrategyEditorWindowFrame.EnsureRunButton(controller.RectOf(R1), font);
        if (btn == null) return "S23: region_001 has no RUN button";
        if (GlyphText(btn) != "▶") return "S23: precondition — RUN button does not start as ▶";

        var exec = new _RecordingExecutor(_ => { });
        var errors = new List<string>();
        var runningEvents = new List<string>();
        var liveLaunches = new List<string>();
        int liveStops = 0;
        int btForceStops = 0;
        bool autoMode = true;     // LiveAuto + venue connected
        bool liveActive = false;  // HasActiveRun ∨ start-in-flight (set by launch, cleared by stop)

        var lane = new NotebookRunLane(exec, startWorker: false);   // synchronous: Submit runs inline
        var run = new NotebookRunController(
            coord, r => views.TryGetValue(r, out var v) ? v : null, lane,
            msg => errors.Add(msg),
            () => SCN,
            () => btForceStops++,
            (region, running) => { runningEvents.Add(region + ":" + running); StrategyEditorWindowFrame.SetRunButtonGlyph(btn, running); },
            null, null,
            liveLaunchActive: () => autoMode,
            onLiveLaunch: region => { liveLaunches.Add(region); liveActive = true; },   // register→start → start-in-flight
            onLiveStop: () => { liveStops++; liveActive = false; },                      // stop_live_strategy
            liveRunActive: () => liveActive);

        // STRATEGY-47: in LiveAuto a press LAUNCHES a live run (not the Replay lane); cell ▶→■.
        run.RunCell(R1);
        if (liveLaunches.Count != 1 || liveLaunches[0] != R1) return "S23/STRATEGY-47: per-cell RUN did not launch a live run in Auto";
        if (exec.Calls != 0) return "S23/STRATEGY-47: a live press wrongly drove the Replay backtest lane";
        if (run.IsBacktestRunning) return "S23/STRATEGY-47: backtest guard set on a live launch";
        if (!run.IsLiveRunLaunched) return "S23/STRATEGY-47: live-run-launched flag not set";
        if (GlyphText(btn) != "■") return "S23/STRATEGY-47: ▶ did not toggle to ■ on live launch";

        // STRATEGY-48: a 2nd press while the live run is active is rejected (no 2nd launch).
        run.RunCell(R1);
        if (liveLaunches.Count != 1) return "S23/STRATEGY-48: a 2nd live run launched while one was active";
        if (errors.Count == 0) return "S23/STRATEGY-48: the rejected 2nd live press surfaced no message";

        // STRATEGY-49: ■ → StopRunning routes to the LIVE stop (not the backtest ForceStop); Sync restores ▶.
        run.StopRunning();
        if (liveStops != 1) return "S23/STRATEGY-49: ■ did not route to the live stop";
        if (btForceStops != 0) return "S23/STRATEGY-49: ■ wrongly force-stopped a backtest on a live run";
        run.SyncLiveRunButton();   // liveActive now false → ■→▶
        if (run.IsLiveRunLaunched) return "S23/STRATEGY-49: live-run flag not cleared after the run terminated";
        if (GlyphText(btn) != "▶") return "S23/STRATEGY-49: ■ did not restore ▶ after the live run stopped";
        if (!runningEvents.Contains(R1 + ":False")) return "S23/STRATEGY-49: ▶ restore event not fired";

        // STRATEGY-50: the SAME press in Replay mode drives a backtest — the dispatch is mode-conditional.
        autoMode = false;
        run.RunCell(R1);
        if (exec.Calls == 0) return "S23/STRATEGY-50: in Replay the press did not drive the backtest lane";
        if (liveLaunches.Count != 1) return "S23/STRATEGY-50: a Replay press wrongly launched a live run";
        run.DrainAndRoute();

        lane.Dispose();
        return null;
    }

    // ======================================================================
    // Section 24: #116 — live cell run lifecycle edges (control-logic hardening)
    //     Covers STRATEGY-51 (start-in-flight deferred-stop: a ■ pressed in the register→start window —
    //     before the run_id is confirmed, when a direct stop would no-op and be LOST — is DEFERRED and
    //     applied the moment HasActiveRun flips true, then ▶ restores on terminal), STRATEGY-52 (dangling
    //     reconcile: deleting the launching cell [52a] OR File→New which REUSES region_001 for a new cell
    //     [52b] drops the ▶/■ tracking — keyed on the launching cell's IDENTITY, not its region — WITHOUT
    //     stopping the venue run [findings 0026], and a relaunch stays blocked by the still-active run; a
    //     deferred stop PENDING at reconcile time is honored not dropped [52c — the delete-during-in-flight race]).
    //     Python-FREE: the fake models HasActiveRun and IsStartInFlight SEPARATELY (Section23 collapses
    //     them into one bool, which cannot express the in-flight window). The REAL register→start + venue
    //     teardown are pinned in python (test_v19_cell_auto_parity / test_live_stop_wedged_consumer); the
    //     S7 HITL (#115) closes the live-venue leg. RED litmus per assert below.
    // ======================================================================
    static string Section24_LiveLifecycleEdges(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;

        // ---- STRATEGY-51: start-in-flight deferred-stop ----
        // RED litmus: drop the deferred-stop in StopRunning (always call _onLiveStop directly) → the
        // in-flight ■ no-ops against the unconfirmed run_id and is never retried → LiveStops stays 0 →
        // the "deferred stop applied once confirmed" assert goes RED.
        var f1 = BuildLiveEdgeFixture(spawned, 1);
        if (f1.Glyph(R1) != "▶") return "S24/STRATEGY-51: precondition — RUN button does not start as ▶";

        f1.Run.RunCell(R1);   // launch: register→start ISSUED, run_id not yet confirmed (start-in-flight)
        if (f1.Launches.Count != 1) return "S24/STRATEGY-51: per-cell RUN did not launch a live run in Auto";
        if (!f1.Run.IsLiveRunLaunched) return "S24/STRATEGY-51: live-run flag not set on launch";
        if (f1.Glyph(R1) != "■") return "S24/STRATEGY-51: ▶ did not toggle to ■ on launch";

        // ■ pressed DURING the in-flight window: a direct stop_live_strategy would no-op (run_id
        // unconfirmed) and be LOST, so it must be DEFERRED — not fired now.
        f1.Run.StopRunning();
        if (f1.LiveStops != 0) return "S24/STRATEGY-51: in-flight ■ fired a stop that would no-op (it must defer)";
        f1.Run.SyncLiveRunButton();   // still in-flight: ■ stays, the deferred stop is not yet applicable
        if (f1.LiveStops != 0) return "S24/STRATEGY-51: deferred stop applied before the run was confirmed";
        if (f1.Glyph(R1) != "■") return "S24/STRATEGY-51: ■ reverted while the start was still in flight";
        if (!f1.Run.IsLiveRunLaunched) return "S24/STRATEGY-51: live-run flag lost during the in-flight window";

        // the run becomes CONFIRMED active (run_id known) → the deferred stop now lands.
        f1.StartInFlight = false; f1.HasActiveRun = true;
        f1.Run.SyncLiveRunButton();
        if (f1.LiveStops != 1) return "S24/STRATEGY-51: deferred stop was not applied once the run was confirmed";
        if (f1.Glyph(R1) != "■") return "S24/STRATEGY-51: ■ reverted before the stop terminated the run";

        // the stop terminals the run → ▶ restores.
        f1.HasActiveRun = false;
        f1.Run.SyncLiveRunButton();
        if (f1.Glyph(R1) != "▶") return "S24/STRATEGY-51: ▶ did not restore after the deferred stop terminated the run";
        if (f1.Run.IsLiveRunLaunched) return "S24/STRATEGY-51: live-run flag not cleared after terminal";
        if (!f1.RunningEvents.Contains(R1 + ":False")) return "S24/STRATEGY-51: ▶ restore event not fired";
        f1.Lane.Dispose();

        // ---- STRATEGY-52a: deleting the launching cell reconciles the tracking (venue run NOT stopped) ----
        // RED litmus: revert Invalidate to a bare `_generation++` (no live-lane reconcile) → _liveRunRegion
        // dangles onto the deleted cell → IsLiveRunLaunched stays true → the "dangled after delete" assert
        // goes RED.
        var f2 = BuildLiveEdgeFixture(spawned, 2);   // 2 cells so a surviving cell remains after deleting cell 0
        string r2 = f2.Region(1);                     // the surviving cell's region (captured BEFORE delete)
        f2.Run.RunCell(R1);                           // launch from cell 0 (R1)
        f2.StartInFlight = false; f2.HasActiveRun = true;   // run confirmed active
        f2.Run.SyncLiveRunButton();
        if (!f2.Run.IsLiveRunLaunched) return "S24/STRATEGY-52a: precondition — live run not active before delete";

        if (!f2.Coord.DeleteCell(R1)) return "S24/STRATEGY-52a: could not delete the launching cell";   // → ListMutated → Invalidate
        if (f2.Run.IsLiveRunLaunched) return "S24/STRATEGY-52a: _liveRunRegion dangled after the launching cell was deleted";
        if (f2.LiveStops != 0) return "S24/STRATEGY-52a: deleting the cell wrongly STOPPED the venue run (it is a venue session)";

        // a relaunch is still blocked — the venue run is still active (dual-launch guard via _liveRunActive).
        f2.Run.RunCell(r2);
        if (f2.Launches.Count != 1) return "S24/STRATEGY-52a: a 2nd live run launched while the venue run was still active";
        if (f2.Errors.Count == 0) return "S24/STRATEGY-52a: the blocked relaunch surfaced no message";
        f2.Lane.Dispose();

        // ---- STRATEGY-52b: File→New (region_001 REUSE) reconciles by cell IDENTITY, not region ----
        // RED litmus: a region-only reconcile (`CellOf(_liveRunRegion)==null`) would PASS 52a but FAIL here —
        // SyncWindowsToNotebook rebinds region_001 to a NEW cell 0, so CellOf(region_001) is non-null after
        // File→New and the stale tracking survives. Keying on the launching cell's identity catches it.
        var f3 = BuildLiveEdgeFixture(spawned, 1);
        f3.Run.RunCell(R1);
        f3.StartInFlight = false; f3.HasActiveRun = true;
        f3.Run.SyncLiveRunButton();
        if (!f3.Run.IsLiveRunLaunched) return "S24/STRATEGY-52b: precondition — live run not active before File→New";

        f3.Coord.New();   // ResetUnboundEmpty + SyncWindowsToNotebook → region_001 rebinds to a NEW cell 0 → ListMutated → Invalidate
        if (f3.Run.IsLiveRunLaunched) return "S24/STRATEGY-52b: _liveRunRegion dangled after File→New reused region_001 for a new cell";
        if (f3.LiveStops != 0) return "S24/STRATEGY-52b: File→New wrongly STOPPED the venue run";
        f3.Lane.Dispose();

        // ---- STRATEGY-52c: a deferred stop PENDING at reconcile time is HONORED, not dropped ----
        // The delete-during-in-flight race (■ deferred, then the launching cell deleted before the next
        // SyncLiveRunButton) must NOT re-open the lost-stop this issue fixes. RED litmus: drop the
        // `if (_pendingLiveStop) _onLiveStop()` from Invalidate → LiveStops stays 0 → this assert goes RED.
        var f4 = BuildLiveEdgeFixture(spawned, 2);     // 2 cells so deleting the launching cell 0 is allowed
        f4.Run.RunCell(R1);                            // launch from cell 0 → start-in-flight
        f4.Run.StopRunning();                          // ■ in-flight → DEFERRED (pending), not fired
        if (f4.LiveStops != 0) return "S24/STRATEGY-52c: in-flight ■ fired immediately (it must defer)";
        f4.StartInFlight = false; f4.HasActiveRun = true;   // run confirms, but SyncLiveRunButton has not run yet
        if (!f4.Coord.DeleteCell(R1)) return "S24/STRATEGY-52c: could not delete the launching cell";   // reconcile while pending
        if (f4.LiveStops != 1) return "S24/STRATEGY-52c: a deferred stop pending at reconcile was DROPPED (lost-stop re-opened)";
        if (f4.Run.IsLiveRunLaunched) return "S24/STRATEGY-52c: tracking not cleared after the reconcile";
        f4.Lane.Dispose();

        return null;
    }

    // Python-FREE fixture for the #116 live-lifecycle edges: a real NotebookRunController + coordinator
    // over a FakeMarimoSynthesizer, with the live callbacks wired to a mutable state holder that models
    // HasActiveRun and IsStartInFlight SEPARATELY (the distinction Section23 cannot express). ListMutated→
    // Invalidate is wired AFTER the controller exists, exactly as the production root does.
    sealed class _LiveEdgeFixture
    {
        public bool AutoMode = true;     // LiveAuto + venue connected (liveLaunchActive)
        public bool StartInFlight;       // register→start issued, run_id not yet confirmed
        public bool HasActiveRun;        // run_id confirmed (a stop will land) — liveRunConfirmed
        public int LiveStops;            // host.StopLiveStrategy calls that actually landed
        public readonly List<string> Launches = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> RunningEvents = new List<string>();
        public NotebookRunController Run;
        public NotebookCellCoordinator Coord;
        public MarimoNotebookDocument Notebook;
        public NotebookRunLane Lane;
        public readonly Dictionary<string, Button> Buttons = new Dictionary<string, Button>();
        public string Region(int cellIndex) => Coord.RegionOf(Notebook.Cells[cellIndex]);
        public string Glyph(string region) => Buttons.TryGetValue(region, out var b) && b != null ? GlyphText(b) : null;
    }

    static _LiveEdgeFixture BuildLiveEdgeFixture(List<GameObject> spawned, int cellCount)
    {
        const string SCN = "{\"instruments\":[\"8918.TSE\"],\"granularity\":\"Daily\"}";
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var views = new Dictionary<string, StrategyEditorView>();
        var fx = new _LiveEdgeFixture();

        var layerGo = new GameObject("FWLayer24", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);   // #119: TMP/SDF — `font` param removed
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(NotebookCellCoordinator.AdoptedRegionId, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);   // #119: TMP/SDF — `font` param removed
        if (view1 != null) views[NotebookCellCoordinator.AdoptedRegionId] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, NotebookCellCoordinator.AdoptedRegionId, adoptRoot);

        var nb = new MarimoNotebookDocument(new FakeMarimoSynthesizer());
        nb.Cells[0].SetBody("for bar in bt.replay():\n    bt.submit_market(100)\n");
        for (int i = 1; i < cellCount; i++) nb.AddCell();
        fx.Notebook = nb;

        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);   // binds cells to regions (fires ListMutated, but nothing is wired yet)
        fx.Coord = coord;

        // a RUN button per bound region (the ▶/■ glyph readback target).
        for (int i = 0; i < nb.Cells.Count; i++)
        {
            string region = coord.RegionOf(nb.Cells[i]);
            var rect = region != null ? controller.RectOf(region) : null;
            if (rect != null) fx.Buttons[region] = StrategyEditorWindowFrame.EnsureRunButton(rect, font);
        }

        fx.Lane = new NotebookRunLane(new _RecordingExecutor(_ => { }), startWorker: false);
        fx.Run = new NotebookRunController(
            coord, r => views.TryGetValue(r, out var v) ? v : null, fx.Lane,
            msg => fx.Errors.Add(msg),
            () => SCN,
            () => { },   // onStop (backtest ForceStop) — unused by the live edges
            (region, running) =>
            {
                fx.RunningEvents.Add(region + ":" + running);
                if (fx.Buttons.TryGetValue(region, out var b) && b != null) StrategyEditorWindowFrame.SetRunButtonGlyph(b, running);
            },
            null, null,
            liveLaunchActive: () => fx.AutoMode,
            onLiveLaunch: region => { fx.Launches.Add(region); fx.StartInFlight = true; },   // register→start issued (run_id pending)
            onLiveStop: () => { if (fx.HasActiveRun) fx.LiveStops++; },                       // mirrors prod: stop no-ops if run_id unconfirmed
            liveRunActive: () => fx.StartInFlight || fx.HasActiveRun,
            liveRunConfirmed: () => fx.HasActiveRun);

        coord.ListMutated += () => fx.Run.Invalidate();   // production path: ListMutated → Invalidate (wired after the controller, like the root)
        return fx;
    }

    // ======================================================================
    // Section 15: #95 Phase 5 (#98) — bt.step() persistence, reset, fail-closed
    //     Covers STRATEGY-24 (a bt.step press does NOT activate the running guard / ▶→■ toggle —
    //     step is instant per press and intentionally stateful across presses), STRATEGY-25 (a
    //     scenario-unset bt.step cell surfaces the guidance RuntimeError in the cell output via
    //     the executor's fail-closed payload), STRATEGY-26 (reactive upstream press cascades to
    //     the downstream step cell — findings 0070 F3 allowed footgun).
    //     Python-FREE: a stub executor mirrors the backend's per-press step semantics (counter
    //     advances on each press; the unset-scenario branch returns the guidance text).  The
    //     REAL bt persistence + caching + scenario-reset are pinned in python e2e
    //     (test_notebook_step_afk.py).
    // ======================================================================
    static string Section15_Phase5StepResetIdempotency(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string SCN_A = "{\"instruments\":[\"8918.TSE\"],\"granularity\":\"Daily\"}";
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer15", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S15: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var nb = new MarimoNotebookDocument(new FakeMarimoSynthesizer());
        nb.Cells[0].SetBody("bar = bt.step()\nbar\n");                          // a B3 step cell
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);
        if (coord.RegionOf(nb.Cells[0]) != R1) return "S15: precondition — cell0 not bound to region_001";

        var btn = StrategyEditorWindowFrame.EnsureRunButton(controller.RectOf(R1), font);
        if (btn == null) return "S15: region_001 has no RUN button";
        if (GlyphText(btn) != "▶") return "S15: precondition — RUN button does not start as ▶";

        // The stub executor simulates the backend's step counter (each press advances by 1) and
        // returns the guidance text when the scenario is unset.  This is the C# routing gate —
        // the real backend caching is pytest's job.
        var exec = new _StepExecutor();
        var errors = new List<string>();
        int stopCount = 0;
        var runningEvents = new List<string>();
        string committedScenario = SCN_A;

        var lane = new NotebookRunLane(exec, startWorker: false);   // synchronous: Submit runs inline
        var run = new NotebookRunController(
            coord, r => views.TryGetValue(r, out var v) ? v : null, lane,
            msg => errors.Add(msg),
            () => committedScenario,                                  // scenarioJsonProvider (mutable)
            () => stopCount++,                                        // onStop
            (region, running) =>                                      // onRunningChanged
            {
                runningEvents.Add(region + ":" + running);
                StrategyEditorWindowFrame.SetRunButtonGlyph(btn, running);
            });

        // STRATEGY-24: a bt.step press DOES NOT activate the running guard / ▶→■ toggle.  Step is
        // instant per press and intentionally stateful across presses — guarding it would block
        // the very next press the user expects to fire.
        run.RunCell(R1);
        run.DrainAndRoute();
        if (exec.Calls != 1) return "S15/STRATEGY-24: executor was not called on the step press";
        if (exec.LastScenarioJson != SCN_A) return "S15/STRATEGY-24: executor did not receive the committed scenario, got [" + exec.LastScenarioJson + "]";
        if (run.IsBacktestRunning) return "S15/STRATEGY-24: step press activated the running guard (it must not)";
        if (runningEvents.Count != 0) return "S15/STRATEGY-24: onRunningChanged fired on a step press (it must not)";
        if (GlyphText(btn) != "▶") return "S15/STRATEGY-24: ▶ toggled on a step press (it must stay ▶)";
        if (views[R1].CurrentOutput != "1") return "S15/STRATEGY-24: step cell output should be '1' after first press, got [" + views[R1].CurrentOutput + "]";

        // STRATEGY-25: the SAME scenario re-pressed advances the counter again — the running
        // guard is not stuck and the controller accepts back-to-back step presses (Phase 5
        // persistence is observable as "the counter signal keeps incrementing per press").
        run.RunCell(R1);
        run.DrainAndRoute();
        if (exec.Calls != 2) return "S15/STRATEGY-25: 2nd step press was rejected (the guard must NOT activate for step)";
        if (views[R1].CurrentOutput != "2") return "S15/STRATEGY-25: step cell output should be '2' after second press, got [" + views[R1].CurrentOutput + "]";

        // STRATEGY-26: a scenario-unset bt.step press surfaces the fail-closed guidance text in
        // the cell output (the backend's NoScenarioBacktester placeholder mirror — pytest pins
        // the real placeholder; this gate pins that the executor's guidance payload reaches the
        // window via SetOutput).
        committedScenario = "";  // simulate "no scenario committed" — provider returns empty
        run.RunCell(R1);
        run.DrainAndRoute();
        string lastOut = views[R1].CurrentOutput;
        if (lastOut == null || !lastOut.Contains("commit the startup panel"))
            return "S15/STRATEGY-26: scenario-unset press did not surface the guidance text, got [" + lastOut + "]";
        if (!lastOut.Contains("RuntimeError"))
            return "S15/STRATEGY-26: scenario-unset press output is missing the RuntimeError label, got [" + lastOut + "]";

        lane.Dispose();
        return null;
    }

    // ======================================================================
    // 16. #95 Phase 6 Slice 3/4 — per-cell STALE badge (edit-time + post-run residual).
    //     Covers STRATEGY-27 (an edit/blur restage projects the post-edit stale set to amber ▶ badges
    //     on the edited cell AND its downstream — routed by cell INDEX to the right window, not always
    //     window 0), STRATEGY-28 (re-pressing a stale cell runs it → the result drops it from the stale
    //     set → its amber clears to green ▶, while a still-stale downstream stays amber).
    //     Python-FREE: a fake executor projects the stale set (Restage returns the edited indices; Run
    //     drops the pressed index). The REAL NotebookRunController index→region mapping (ApplyResult)
    //     + the REAL StrategyEditorWindowFrame.SetRunButtonStale amber/green tint are exercised; the
    //     root's SetCellStaleRegions paint loop is mirrored by the onStaleRegionsChanged wiring. The
    //     real marimo incremental stale graph (set_stale downstream propagation) is pytest's job
    //     (test_notebook_stale.py).
    //     RED litmus (lead, non-destructive): (a) collapse ApplyResult's `RegionOf(cells[idx])` to
    //     `cells[0]` → region_002 never badges → the "both amber after restage" assert goes RED;
    //     (b) make the fake's Run NOT drop the pressed index → region_001 stays amber after its press
    //     → the "region_001 green after press" assert goes RED.
    // ======================================================================
    static string Section16_PerCellStale(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string R2 = "strategy_editor:region_002";
        var amber = new Color(0.8510f, 0.6157f, 0.2078f, 1f);   // #d99d35 stale
        var green = new Color(0.2275f, 0.6078f, 0.3608f, 1f);   // #3a9b5c clean/idle
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer16", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rootRt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rootRt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rootRt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S16: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var synth = new FakeMarimoSynthesizer();
        var nb = new MarimoNotebookDocument(synth);
        nb.AddCell();   // 2 cells -> region_001 + region_002
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);
        if (coord.RegionOf(nb.Cells[0]) != R1 || coord.RegionOf(nb.Cells[1]) != R2)
            return "S16: precondition — cells not bound to region_001/region_002";

        var btn1 = StrategyEditorWindowFrame.EnsureRunButton(controller.RectOf(R1), font);
        var btn2 = StrategyEditorWindowFrame.EnsureRunButton(controller.RectOf(R2), font);
        if (btn1 == null || btn2 == null) return "S16: a cell window has no RUN button";
        var btns = new Dictionary<string, Button> { { R1, btn1 }, { R2, btn2 } };

        var exec = new _StaleExecutor();
        var lane = new NotebookRunLane(exec, startWorker: false);
        // Mirror the root's SetCellStaleRegions: paint every known button amber if its region is in the
        // stale set, green otherwise (the controller already mapped indices→regions in ApplyResult).
        var run = new NotebookRunController(
            coord, r => views.TryGetValue(r, out var v) ? v : null, lane,
            onStaleRegionsChanged: regions =>
            {
                var stale = new HashSet<string>(regions);
                foreach (var kv in btns)
                    StrategyEditorWindowFrame.SetRunButtonStale(kv.Value, stale.Contains(kv.Key));
            });

        // both buttons start green (idle, never edited).
        if (!ColorApprox(ImgColor(btn1), green) || !ColorApprox(ImgColor(btn2), green))
            return "S16: precondition — RUN buttons should start green (idle)";

        // STRATEGY-27: an edit/blur restage marks cell 0 AND its downstream cell 1 stale → BOTH amber.
        // (litmus: a routing that collapses every stale index to window 0 leaves region_002 green here.)
        exec.StaleSet = new[] { 0, 1 };
        run.Restage();
        run.DrainAndRoute();
        if (!ColorApprox(ImgColor(btn1), amber)) return "S16/STRATEGY-27: region_001 not amber after an edit restage";
        if (!ColorApprox(ImgColor(btn2), amber)) return "S16/STRATEGY-27: downstream region_002 not amber (stale routing collapsed to one window)";

        // STRATEGY-28: re-pressing region_001 runs cell 0 → the result drops it from the stale set →
        // region_001 clears to green while the still-stale downstream region_002 stays amber.
        run.RunCell(R1);
        run.DrainAndRoute();
        if (!ColorApprox(ImgColor(btn1), green)) return "S16/STRATEGY-28: region_001 amber did not clear to green after its press";
        if (!ColorApprox(ImgColor(btn2), amber)) return "S16/STRATEGY-28: still-stale downstream region_002 wrongly cleared on an unrelated press";

        lane.Dispose();
        return null;
    }

    // S16 fake: projects a per-cell stale set. Restage returns the current stale set (an edit made
    // these cells stale); Run drops the pressed index (it just ran → no longer stale) and returns the
    // remaining stale set, so re-pressing a stale cell clears ITS amber while a downstream stays amber.
    sealed class _StaleExecutor : INotebookCellExecutor
    {
        public int[] StaleSet = Array.Empty<int>();
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson, string strategyPath)
        {
            var remaining = new List<int>();
            foreach (var i in StaleSet) if (i != pressedIndex) remaining.Add(i);
            StaleSet = remaining.ToArray();
            return new NotebookRunResult
            {
                Ok = true,
                Ran = new[] { new NotebookCellOutput { Index = pressedIndex, Output = "out-" + pressedIndex, Ok = true } },
                Stale = StaleSet,
            };
        }
        public int[] Restage(string source) => StaleSet;
    }

    // ======================================================================
    // 17. #95 Phase 6 Slice (P6-4) — block popup: the running-guard rejection surfaces on the
    //     notification line ONLY when a press is actually blocked.
    //     Covers STRATEGY-29 (a 2nd per-cell RUN while a bt.replay backtest is in flight is rejected
    //     and the "already running" message reaches the notification sink; a successful, non-blocked
    //     press surfaces NO notification — no spurious popup).
    //     Python-FREE: a fake OK executor + the REAL NotebookRunController running guard. The onError
    //     lambda mirrors the root's sink (BackcastWorkspaceRoot.cs:383 wires onError →
    //     _menuBarView.ShowMessage("Run cell: " + msg)). The not-owner / server-not-ready guards are a
    //     SEPARATE root button-wire gate (`if (_isOwner && _host.ServerReady)`, e.g. cs:371/535) — root/
    //     HITL, not the controller — so S17 pins the in-flight block message + the silence-on-success
    //     invariant here; the cross-thread real bt stop is the pytest gate test_notebook_replay_afk.py.
    //     RED litmus (lead): removing the `if (_btRunActive)` guard in RunCell → the 2nd press runs and
    //     surfaces no message → the "exactly one notification" assert goes RED; making the notify
    //     unconditional → the silent first press surfaces a popup → the "Count != 0" assert goes RED.
    // ======================================================================
    static string Section17_BlockPopup(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string SCN = "{\"instruments\":[\"8918.TSE\"],\"granularity\":\"Daily\"}";
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer17", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S17: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var nb = new MarimoNotebookDocument(new FakeMarimoSynthesizer());
        nb.Cells[0].SetBody("x = 1\n");   // a pure (non-blocking) cell to start
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);
        if (coord.RegionOf(nb.Cells[0]) != R1) return "S17: precondition — cell0 not bound to region_001";

        var btn = StrategyEditorWindowFrame.EnsureRunButton(controller.RectOf(R1), font);
        if (btn == null) return "S17: region_001 has no RUN button";

        // The notification sink: the root wires onError → _menuBarView.ShowMessage (cs:383). The block
        // popup (P6-4) surfaces ONLY when a press is rejected.
        var notifications = new List<string>();
        var exec = new _OkExecutor();
        var lane = new NotebookRunLane(exec, startWorker: false);
        var run = new NotebookRunController(
            coord, r => views.TryGetValue(r, out var v) ? v : null, lane,
            onError: msg => notifications.Add(msg),
            scenarioJsonProvider: () => SCN,
            onStop: () => { },
            onRunningChanged: (region, running) => StrategyEditorWindowFrame.SetRunButtonGlyph(btn, running));

        // STRATEGY-29a (silence on success): a successful, non-blocked press surfaces NO notification.
        run.RunCell(R1);
        run.DrainAndRoute();
        if (notifications.Count != 0)
            return "S17/STRATEGY-29: a successful (non-blocked) press surfaced a notification, got [" + string.Join(" | ", notifications) + "]";

        // STRATEGY-29b (block popup): a bt.replay press enters RUNNING; a 2nd press WHILE it is in flight
        // is rejected and the block message reaches the notification sink.
        nb.Cells[0].SetBody("for bar in bt.replay():\n    bt.submit_market(100)\n");
        run.RunCell(R1);                                  // enters RUNNING (guard active); not drained
        if (!run.IsBacktestRunning) return "S17: precondition — the bt.replay press did not enter RUNNING";
        run.RunCell(R1);                                  // blocked
        if (notifications.Count != 1)
            return "S17/STRATEGY-29: the blocked 2nd press did not surface exactly one notification (got " + notifications.Count + ")";
        if (!notifications[0].Contains("already running"))
            return "S17/STRATEGY-29: the block message is not the running-guard popup, got [" + notifications[0] + "]";

        run.StopRunning();
        run.DrainAndRoute();
        lane.Dispose();
        return null;
    }

    // S17 fake: a minimal OK executor (one ran cell, no stale) — the running guard is the controller's.
    sealed class _OkExecutor : INotebookCellExecutor
    {
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson, string strategyPath)
            => new NotebookRunResult
            {
                Ok = true,
                Ran = new[] { new NotebookCellOutput { Index = pressedIndex, Output = "ok", Ok = true } },
            };
        public int[] Restage(string source) => Array.Empty<int>();
    }

    // ======================================================================
    // 18. #95 Phase 6 Slice 6 (P6-5 / #90) — document-identity badge string.
    //     Covers STRATEGY-30, STRATEGY-31 (#90 AC: a bound notebook shows its .py basename; an unsaved
    //     edit prefixes "* "; Save clears the "* "; File→New (ResetUnboundEmpty) shows "Untitled").
    //     Python-FREE + root-FREE: drives a REAL MarimoNotebookDocument through New/Open/edit/Save and
    //     asserts the identity TRIPLE (IsBound / IsDirty / CurrentPath — the badge's live source) plus
    //     the projected badge string. The badge format is mirrored from BackcastWorkspaceRoot
    //     .DocumentBadgeText (cs:1542-1543); the per-frame MenuBarView _docBadge surfacing
    //     (MenuBarView.cs:172-176) and the cache-on-source invalidation (cs:1535-1536) are the live
    //     root wire — covered by the responsibility split (the doc-identity badge is a SEPARATE lane
    //     from the venue/mode/message badge, MenuBarView.cs:119-123, so #90 AC4 "wording does not
    //     contradict the Run-disabled reason" holds by construction).
    //     RED litmus (lead): if MarimoNotebookDocument.Open stopped binding (CurrentPath stays null),
    //     the post-Open badge falls back to "Untitled" → the "Open → basename" assert goes RED.
    // ======================================================================
    static string Section18_DocumentBadge()
    {
        Directory.CreateDirectory(TempDir);
        var synth = new FakeMarimoSynthesizer();

        // File→New: a fresh notebook is unbound + clean → "Untitled".
        var nb = new MarimoNotebookDocument(synth);
        if (nb.IsBound || nb.IsDirty) return "S18: precondition — a fresh notebook should be unbound + clean";
        if (Badge(nb) != "Untitled") return "S18/STRATEGY-30: a fresh (unbound) notebook badge != 'Untitled', got [" + Badge(nb) + "]";

        // SaveAs binds the document → the basename is visible, no dirty marker.
        string py = Path.Combine(TempDir, "doc18.py");
        if (!nb.SaveAs(py)) return "S18: SaveAs failed";
        if (!nb.IsBound || nb.IsDirty) return "S18: SaveAs did not bind + clear dirty";
        if (Badge(nb) != "doc18.py") return "S18/STRATEGY-30: a saved notebook badge != basename, got [" + Badge(nb) + "]";

        // An unsaved edit prefixes "* " (the dirty marker).
        nb.Cells[0].SetBody("x = 1\n");
        if (!nb.IsDirty) return "S18: a body edit did not dirty the notebook";
        if (Badge(nb) != "* doc18.py") return "S18/STRATEGY-31: a dirty notebook badge missing the '* ' marker, got [" + Badge(nb) + "]";

        // Save clears the dirty marker → "* " disappears.
        if (!nb.Save()) return "S18: Save failed";
        if (nb.IsDirty) return "S18: Save did not clear dirty";
        if (Badge(nb) != "doc18.py") return "S18/STRATEGY-31: Save did not drop the '* ' marker, got [" + Badge(nb) + "]";

        // File→Open of the same .py on a fresh document → basename visible (the #90 AC4 always-visible
        // basename), bound + clean. (litmus: an Open that fails to bind falls back to 'Untitled'.)
        var nb2 = new MarimoNotebookDocument(synth);
        if (!nb2.Open(py)) return "S18: Open failed";
        if (!nb2.IsBound || nb2.IsDirty) return "S18: Open did not bind + clean";
        if (Badge(nb2) != "doc18.py") return "S18/STRATEGY-30: an opened notebook badge != basename, got [" + Badge(nb2) + "]";

        // File→New (ResetUnboundEmpty) drops back to "Untitled".
        nb2.ResetUnboundEmpty();
        if (nb2.IsBound) return "S18: ResetUnboundEmpty did not unbind";
        if (Badge(nb2) != "Untitled") return "S18/STRATEGY-30: File→New badge != 'Untitled', got [" + Badge(nb2) + "]";
        return null;
    }

    // Mirrors BackcastWorkspaceRoot.DocumentBadgeText (cs:1542-1543): the bound basename (or 'Untitled'
    // when unbound), prefixed with '* ' while dirty. The live root caches this on its source and feeds
    // it to MenuBarView's documentBadgeText provider (cs:461 / MenuBarView.cs:174).
    static string Badge(MarimoNotebookDocument doc)
    {
        string name = doc.IsBound ? Path.GetFileName(doc.CurrentPath) : "Untitled";
        return (doc.IsDirty ? "* " : "") + name;
    }

    // ======================================================================
    // 19. #95 Phase 6 Slice 5 (P6-2) — rich output routing by mimetype.
    //     Covers STRATEGY-32 (text/plain → verbatim Text; text/markdown → rich-text subset <b>…;
    //     text/html <table> → pipe rows), STRATEGY-33 (an unsupported mimetype → labelled plain
    //     fallback; image/png → the sibling RawImage decodes + activates, NOT the Text pane).
    //     Python-FREE: a fake returns a settable {output, mimetype, data} so BOTH the controller's
    //     mimetype passthrough (ApplyResult → view.SetOutput(output,mimetype,data)) and the view's
    //     per-mimetype routing are exercised on the REAL StrategyEditorView (built with its RawImage
    //     sibling by StrategyEditorContentBuilder). The real marimo FormattedOutput(mimetype,data)
    //     production (matplotlib→image/png, mo.md→text/markdown, df→text/html) is pytest's job
    //     (test_notebook_rich_output.py).
    //     RED litmus (lead): collapsing every mimetype to the Text pane leaves the RawImage inactive →
    //     the image/png assert (OutputIsImage) goes RED.
    // ======================================================================
    static string Section19_RichOutput(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        // a real 1x1 PNG (valid header so Texture2D.LoadImage decodes it).
        const string PNG_B64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer19", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S19: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var nb = new MarimoNotebookDocument(new FakeMarimoSynthesizer());
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);
        if (coord.RegionOf(nb.Cells[0]) != R1) return "S19: precondition — cell0 not bound to region_001";

        var exec = new _RichExecutor();
        var lane = new NotebookRunLane(exec, startWorker: false);
        var run = new NotebookRunController(coord, r => views.TryGetValue(r, out var v) ? v : null, lane);
        var view = views[R1];

        // STRATEGY-32 (text/plain): a plain payload paints the Text pane verbatim, no image.
        exec.Set("hello", "text/plain", null);
        run.RunCell(R1); run.DrainAndRoute();
        if (view.OutputIsImage) return "S19/STRATEGY-32: text/plain wrongly routed to the image pane";
        if (view.CurrentOutput != "hello") return "S19/STRATEGY-32: text/plain pane != 'hello', got [" + view.CurrentOutput + "]";

        // STRATEGY-32 (text/markdown): markdown converts to the rich-text subset (<b> tags).
        exec.Set("# Title", "text/markdown", "# Title\n**bold**");
        run.RunCell(R1); run.DrainAndRoute();
        if (view.OutputIsImage) return "S19/STRATEGY-32: text/markdown wrongly routed to the image pane";
        if (view.CurrentOutput == null || !view.CurrentOutput.Contains("<b>"))
            return "S19/STRATEGY-32: text/markdown not rich-converted (no <b>), got [" + view.CurrentOutput + "]";

        // STRATEGY-32 (text/html table): a <table> projects to pipe rows.
        exec.Set("a b", "text/html", "<table><tr><td>a</td><td>b</td></tr></table>");
        run.RunCell(R1); run.DrainAndRoute();
        if (view.OutputIsImage) return "S19/STRATEGY-32: text/html wrongly routed to the image pane";
        if (view.CurrentOutput == null || !view.CurrentOutput.Contains("|"))
            return "S19/STRATEGY-32: text/html table not projected to pipe rows, got [" + view.CurrentOutput + "]";

        // STRATEGY-33 (unsupported mimetype): falls back to plain text with the mimetype labelled.
        exec.Set("{\"k\":1}", "application/json", null);
        run.RunCell(R1); run.DrainAndRoute();
        if (view.OutputIsImage) return "S19/STRATEGY-33: an unsupported mimetype wrongly routed to the image pane";
        if (view.CurrentOutput == null || !view.CurrentOutput.Contains("[application/json]"))
            return "S19/STRATEGY-33: unsupported mimetype not labelled in the fallback, got [" + view.CurrentOutput + "]";

        // STRATEGY-33 (image/png): routes by mimetype into the image codepath. Texture2D.LoadImage decodes
        // a PNG on the CPU then uploads to the GPU; a headless batch (no graphics device) cannot — true here
        // under BOTH -batchmode -nographics AND -batchmode (GPU-allowed). So probe decode capability with a
        // throwaway texture (same API/bytes/process as the view's TryDecodeImage):
        //   * decode-capable env (HITL / interactive GPU): assert the full routing → RawImage activation.
        //   * decode-incapable batch: GPU decode + RawImage activation is HITL-only (S3/S8-style降格). AFK
        //     still positively pins that the controller propagated mimetype="image/png" + data end-to-end —
        //     SetOutput falls to the labelled-plain fallback keyed on the mimetype, so the "[image/png]"
        //     label proves routing did NOT collapse the mimetype away. The four text mimetypes above cover
        //     the mt-branch dispatch in AFK; only the GPU leaf is demoted.
        // RED litmus (AFK): drop Mimetype from the controller passthrough → no "[image/png]" label → RED.
        // RED litmus (HITL): collapse every mimetype to Text → RawImage inactive → RED.
        exec.Set("<figure>", "image/png", PNG_B64);
        run.RunCell(R1); run.DrainAndRoute();
        var imgProbe = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        bool canDecode = imgProbe.LoadImage(Convert.FromBase64String(PNG_B64));
        UnityEngine.Object.DestroyImmediate(imgProbe);
        if (canDecode)
        {
            if (!view.OutputIsImage) return "S19/STRATEGY-33: image/png did not route to the RawImage (RawImage inactive)";
        }
        else
        {
            if (view.OutputIsImage) return "S19/STRATEGY-33: image/png reported RawImage active despite no decode capability";
            if (view.CurrentOutput == null || !view.CurrentOutput.Contains("[image/png]"))
                return "S19/STRATEGY-33: image/png mimetype did not propagate to the view (no [image/png] label in the decode-failure fallback), got [" + view.CurrentOutput + "]";
            Debug.LogWarning("[E2E STRATEGY NOTEBOOK] S19/STRATEGY-33: image/png mimetype propagated end-to-end; Texture2D.LoadImage cannot decode in this headless batch -> RawImage activation is HITL-only (S3/S8-style降格; the four text mimetypes cover the mt-branch dispatch in AFK).");
        }

        lane.Dispose();
        return null;
    }

    // S19 fake: returns a settable rich payload (output + mimetype + data) for the pressed cell so the
    // controller's mimetype passthrough (ApplyResult → view.SetOutput(output,mimetype,data)) and the
    // view's per-mimetype routing are both exercised.
    sealed class _RichExecutor : INotebookCellExecutor
    {
        string _output, _mimetype, _data;
        public void Set(string output, string mimetype, string data) { _output = output; _mimetype = mimetype; _data = data; }
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson, string strategyPath)
            => new NotebookRunResult
            {
                Ok = true,
                Ran = new[] { new NotebookCellOutput { Index = pressedIndex, Output = _output, Ok = true, Mimetype = _mimetype, Data = _data } },
            };
        public int[] Restage(string source) => Array.Empty<int>();
    }

    // Phase 5 stub executor: simulates the backend's step counter (each press +1) and routes the
    // guidance text when the scenario JSON is empty (mirrors the real NoScenarioBacktester).
    sealed class _StepExecutor : INotebookCellExecutor
    {
        public int Calls;
        public string LastScenarioJson;
        int _stepCounter;

        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson, string strategyPath)
        {
            Calls++;
            LastScenarioJson = scenarioJson;
            string output;
            bool ok;
            if (string.IsNullOrEmpty(scenarioJson))
            {
                output = "RuntimeError: bt.step(): no active scenario; commit the startup panel first, then press RUN again";
                ok = false;
            }
            else
            {
                _stepCounter++;
                output = _stepCounter.ToString();
                ok = true;
            }
            return new NotebookRunResult
            {
                Ok = ok,
                Ran = new[] { new NotebookCellOutput { Index = pressedIndex, Output = output, Ok = ok } },
                Error = null,
            };
        }

        // #95 Phase 6 Slice 4: interface satisfaction only (the edit-stale AFK sections S16–S19 are
        // Slice 7). This fake projects no stale.
        public int[] Restage(string source) => Array.Empty<int>();
    }

    // ======================================================================
    // 20. STRATEGY-11 — single-cell host-API placeholder hint (#81 / findings 0050).
    //     Covers STRATEGY-11 (when the notebook has exactly ONE cell, every cell window shows the
    //     HostApiHint placeholder; with ≥2 cells the hint clears; the hint is NEVER written into
    //     Cell.Body — findings 0050 "seed 焼き込み禁止"). Previously the台本's only `要新規自動化` row.
    //     Python-FREE: a bare FloatingWindowController + REAL StrategyEditorView(s) (the Placeholder
    //     Text is built by StrategyEditorContentBuilder) + a real NotebookCellCoordinator whose
    //     UpdatePlaceholders runs on every Sync/Add/Delete.
    //     RED litmus: NotebookCellCoordinator.UpdatePlaceholders' `single ? HostApiHint : null` — drop
    //     the CellCount==1 gate (always HostApiHint) → the 2-cell assert goes RED; seed the hint into
    //     Cell.Body → the "body stays empty" assert goes RED.
    // ======================================================================
    static string Section20_PlaceholderHint(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string R2 = "strategy_editor:region_002";
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer20", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S20: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var nb = new MarimoNotebookDocument(new FakeMarimoSynthesizer());   // 1 empty cell
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);
        if (coord.RegionOf(nb.Cells[0]) != R1) return "S20: precondition — cell0 not bound to region_001";

        // STRATEGY-11a: a single cell shows the host-API hint (placeholder active + text == HostApiHint).
        if (PlaceholderText(view1) != NotebookCellCoordinator.HostApiHint)
            return "S20/STRATEGY-11: single-cell placeholder is not the HostApiHint (got [" + PlaceholderText(view1) + "])";
        if (!PlaceholderActive(view1))
            return "S20/STRATEGY-11: single-cell placeholder GameObject is not active";
        // the hint is NEVER written into the cell body (findings 0050: seed 焼き込み禁止).
        if (!string.IsNullOrEmpty(nb.Cells[0].Body))
            return "S20/STRATEGY-11: the host-API hint leaked into Cell.Body (got [" + nb.Cells[0].Body + "])";

        // STRATEGY-11b: adding a 2nd cell clears the hint on every window (CellCount==2 → null).
        coord.AddCell();
        if (coord.RegionOf(nb.Cells[1]) != R2) return "S20: precondition — 2nd cell not region_002";
        if (PlaceholderActive(view1))
            return "S20/STRATEGY-11: placeholder still active with 2 cells (the single-cell gate failed)";
        if (views.TryGetValue(R2, out var view2) && PlaceholderActive(view2))
            return "S20/STRATEGY-11: the spawned cell's placeholder is active with 2 cells";
        if (!string.IsNullOrEmpty(nb.Cells[0].Body) || !string.IsNullOrEmpty(nb.Cells[1].Body))
            return "S20/STRATEGY-11: a cell body was seeded when the hint cleared";

        // STRATEGY-11c: deleting back to a single cell restores the hint.
        if (!coord.DeleteCell(R2)) return "S20: DeleteCell(region_002) failed";
        if (PlaceholderText(view1) != NotebookCellCoordinator.HostApiHint || !PlaceholderActive(view1))
            return "S20/STRATEGY-11: returning to a single cell did not restore the host-API hint";

        return null;
    }

    static FieldInfo s_placeholderField;
    static TMPro.TMP_Text Placeholder(StrategyEditorView v)   // #119: placeholder is now a TMP_Text
    {
        s_placeholderField ??= typeof(StrategyEditorView).GetField("_placeholder", BindingFlags.NonPublic | BindingFlags.Instance);
        return s_placeholderField?.GetValue(v) as TMPro.TMP_Text;
    }
    static string PlaceholderText(StrategyEditorView v) { var t = Placeholder(v); return t != null ? t.text : null; }
    static bool PlaceholderActive(StrategyEditorView v) { var t = Placeholder(v); return t != null && t.gameObject.activeSelf; }

    static string GlyphText(UnityEngine.UI.Button runButton)
    {
        var glyph = runButton != null ? runButton.transform.Find("RunGlyph") : null;
        var t = glyph != null ? glyph.GetComponent<Text>() : null;
        return t != null ? t.text : null;
    }

    // The RUN button's background tint (run-green / stop-red / stale-amber) — Slice 3/6 badge readback.
    static Color ImgColor(UnityEngine.UI.Button runButton) => runButton.GetComponent<UnityEngine.UI.Image>().color;

    // Records the scenario JSON each press received (Phase 4 control gate). Returns a canned OK result.
    sealed class _RecordingExecutor : INotebookCellExecutor
    {
        readonly Action<string> _onScenario;
        public int Calls;
        public _RecordingExecutor(Action<string> onScenario) { _onScenario = onScenario; }
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson, string strategyPath)
        {
            Calls++;
            _onScenario?.Invoke(scenarioJson);
            return new NotebookRunResult
            {
                Ok = true,
                Ran = new[] { new NotebookCellOutput { Index = pressedIndex, Output = "ran", Ok = true } },
                Error = null,
            };
        }

        // #95 Phase 6 Slice 4: interface satisfaction only (the edit-stale AFK sections S16–S19 are
        // Slice 7). This fake projects no stale.
        public int[] Restage(string source) => Array.Empty<int>();
    }

    // Fake per-cell executor (Python-FREE): pressed cell -> "out-{index}"; pressing cell 0 also emits a
    // downstream cell-1 output to exercise multi-cell index->window routing. Mirrors the JSON the real
    // backend returns (ran = pressed + reactive descendants), without the embedded interpreter.
    sealed class _FakeCellExecutor : INotebookCellExecutor
    {
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson, string strategyPath)
        {
            var ran = new List<NotebookCellOutput>
            {
                new NotebookCellOutput { Index = pressedIndex, Output = "out-" + pressedIndex, Ok = true },
            };
            if (pressedIndex == 0)
                ran.Add(new NotebookCellOutput { Index = 1, Output = "down-1", Ok = true });
            return new NotebookRunResult { Ok = true, Ran = ran.ToArray(), Error = null };
        }

        // #95 Phase 6 Slice 4: interface satisfaction only (the edit-stale AFK sections S16–S19 are
        // Slice 7). This fake projects no stale.
        public int[] Restage(string source) => Array.Empty<int>();
    }

    // ---- helpers ----

    static bool ColorApprox(Color a, Color b)
        => Mathf.Abs(a.r - b.r) <= 0.02f && Mathf.Abs(a.g - b.g) <= 0.02f
        && Mathf.Abs(a.b - b.b) <= 0.02f && Mathf.Abs(a.a - b.a) <= 0.02f;

    // First token covering `index`, or null (Default).
    static PythonTokenClass? ClassAt(List<PythonToken> toks, int index)
    {
        foreach (var t in toks)
        {
            if (index < t.start) break;
            if (index < t.End) return t.cls;
        }
        return null;
    }

    static bool HasExact(List<PythonToken> toks, int start, int length, PythonTokenClass cls)
    {
        foreach (var t in toks)
            if (t.start == start && t.length == length && t.cls == cls) return true;
        return false;
    }

    static bool HasClass(List<PythonToken> toks, PythonTokenClass cls)
    {
        foreach (var t in toks) if (t.cls == cls) return true;
        return false;
    }

    static void ResetTempDir(bool remove = false)
    {
        if (Directory.Exists(TempDir))
        {
            try { File.SetAttributes(TempDir, File.GetAttributes(TempDir) & ~FileAttributes.ReadOnly); } catch { }
            Directory.Delete(TempDir, true);
        }
        if (!remove) Directory.CreateDirectory(TempDir);
    }

    static bool Approx(float a, float b) => Mathf.Abs(a - b) <= EPS;

    // ====== Section 21 — #102: console + dynamic output layout (findings 0079) ======
    //
    // Covers: STRATEGY-34 console paints stdout/stderr segments in arrival order with stderr amber;
    //         STRATEGY-35 empty rich + empty console → both blocks deactivated, editor takes the full body;
    //         STRATEGY-36 rich populated + console empty → console block hidden;
    //         STRATEGY-37 rich populated + console populated → both blocks visible, capped at body * 0.45;
    //         STRATEGY-38 cell rebind clears both rich and console panes.
    //
    // Python-FREE (the segments are produced by a fake executor); the Python pytest gate
    // (test_notebook_console.py) covers the marimo-side capture.
    static string Section21_ConsoleAndDynamicLayout(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer20", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        // Give the root a fixed size so body.rect.height resolves to a meaningful value — the view's
        // dynamic layout reads body.rect.height to compute editor-min and per-block-max.
        adoptRoot.sizeDelta = new Vector2(400f, 400f);
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(adoptRoot);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S20: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var nb = new MarimoNotebookDocument(new FakeMarimoSynthesizer());
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);
        if (coord.RegionOf(nb.Cells[0]) != R1) return "S20: precondition — cell0 not bound to region_001";

        var exec = new _ConsoleExecutor();
        var lane = new NotebookRunLane(exec, startWorker: false);
        var run = new NotebookRunController(coord, r => views.TryGetValue(r, out var v) ? v : null, lane);
        var view = views[R1];

        // STRATEGY-35 (initial empty): neither block visible — editor takes the full body.
        if (view.RichBlockVisible) return "S21/STRATEGY-35: rich block is initially visible (should be hidden)";
        if (view.ConsoleBlockVisible) return "S21/STRATEGY-35: console block is initially visible (should be hidden)";

        // STRATEGY-34: a single stdout segment paints the console; stderr stays absent.
        exec.SetOutput(string.Empty, string.Empty, string.Empty);
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "a\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.ConsoleBlockVisible) return "S21/STRATEGY-34: console block did not become visible after a stdout segment";
        var ctext = view.CurrentConsoleText ?? string.Empty;
        if (!ctext.Contains("a")) return "S21/STRATEGY-34: console text missing the stdout payload, got [" + ctext + "]";
        if (ctext.Contains("<color=")) return "S21/STRATEGY-34: stdout-only payload wrongly wrapped in a colour tag, got [" + ctext + "]";

        // STRATEGY-34 (stderr amber): a stderr segment paints amber via UGUI rich-text colour tags.
        exec.SetConsole(new[] {
            new ConsoleSegment { Stream = "stdout", Text = "o1\n" },
            new ConsoleSegment { Stream = "stderr", Text = "e1\n" },
            new ConsoleSegment { Stream = "stdout", Text = "o2\n" },
        });
        run.RunCell(R1); run.DrainAndRoute();
        ctext = view.CurrentConsoleText ?? string.Empty;
        if (!ctext.Contains("<color=")) return "S21/STRATEGY-34: a stderr segment did not produce a colour tag, got [" + ctext + "]";
        int oIdx = ctext.IndexOf("o1");
        int eIdx = ctext.IndexOf("e1");
        int o2Idx = ctext.IndexOf("o2");
        if (oIdx < 0 || eIdx < 0 || o2Idx < 0) return "S21/STRATEGY-34: arrival order not preserved (o1/e1/o2 missing), got [" + ctext + "]";
        if (!(oIdx < eIdx && eIdx < o2Idx)) return "S21/STRATEGY-34: arrival order broken (expected o1<e1<o2), got [" + ctext + "]";

        // STRATEGY-34 (#118 TMP rich-text escape regression): a stdout segment containing `<EOF>` MUST
        // survive AND stay inert — TMP_Text with richText=true would otherwise treat `<EOF>` as an
        // unknown tag (`print("<EOF>")` shows nothing). BuildConsoleRichText wraps the payload in TMP's
        // `<noparse>…</noparse>` so the user's literal `<EOF>` renders verbatim and is NOT parsed. We do
        // NOT entity-escape: TMP would paint a literal `&lt;` (a regression), so `&lt;` must NOT appear.
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "<EOF>" } });
        run.RunCell(R1); run.DrainAndRoute();
        ctext = view.CurrentConsoleText ?? string.Empty;
        if (!ctext.Contains("<noparse>")) return "S21/STRATEGY-34: stdout payload not wrapped in <noparse> — TMP would parse the user's `<` as a tag, got [" + ctext + "]";
        if (!ctext.Contains("<EOF")) return "S21/STRATEGY-34: literal '<EOF>' from stdout did not survive verbatim, got [" + ctext + "]";
        if (ctext.Contains("&lt;")) return "S21/STRATEGY-34: payload was entity-escaped (&lt;) — TMP renders that literally (regression), got [" + ctext + "]";

        // STRATEGY-37 (both populated): rich block + console block both visible, capped under body * 0.45.
        exec.SetOutput("hello", "text/plain", null);
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "console!\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.RichBlockVisible) return "S21/STRATEGY-37: rich block did not become visible after a text/plain payload";
        if (!view.ConsoleBlockVisible) return "S21/STRATEGY-37: console block did not become visible alongside rich output";
        // Per-block cap: each output block's preferredHeight must not exceed body.height * fraction.
        var richBlockRT = view1.transform.parent.Find("RichOutputBlock") as RectTransform;
        var consoleBlockRT = view1.transform.parent.Find("ConsoleOutputBlock") as RectTransform;
        if (richBlockRT == null || consoleBlockRT == null) return "S21/STRATEGY-37: rich/console block RectTransform not found under body";
        float bodyH = adoptBody.rect.height;
        float cap = bodyH * StrategyEditorContentBuilder.OutputBlockMaxFractionOfBody + 1f;   // 1px tolerance for rebuild rounding
        var richLE = richBlockRT.GetComponent<LayoutElement>();
        var conLE = consoleBlockRT.GetComponent<LayoutElement>();
        if (richLE.preferredHeight > cap) return "S21/STRATEGY-37: rich block preferredHeight " + richLE.preferredHeight + " exceeded cap " + cap;
        if (conLE.preferredHeight > cap) return "S21/STRATEGY-37: console block preferredHeight " + conLE.preferredHeight + " exceeded cap " + cap;

        // STRATEGY-36 (rich only): a press that emits rich but no console keeps the console hidden.
        exec.SetOutput("only", "text/plain", null);
        exec.SetConsole(System.Array.Empty<ConsoleSegment>());
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.RichBlockVisible) return "S21/STRATEGY-36: rich block hidden when a rich-only payload arrived";
        if (view.ConsoleBlockVisible) return "S21/STRATEGY-36: console block stayed visible after an empty console segment list";

        // STRATEGY-35 (back to empty): an empty rich + empty console returns to editor-only.
        exec.SetOutput(null, null, null);
        exec.SetConsole(System.Array.Empty<ConsoleSegment>());
        run.RunCell(R1); run.DrainAndRoute();
        if (view.RichBlockVisible) return "S21/STRATEGY-35: rich block stayed visible after an empty payload";
        if (view.ConsoleBlockVisible) return "S21/STRATEGY-35: console block stayed visible after an empty payload";

        // STRATEGY-38 (rebind clears both panes): paint something, then Bind a new cell — both panes clear.
        exec.SetOutput("painted", "text/plain", null);
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "x\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.RichBlockVisible || !view.ConsoleBlockVisible) return "S21/STRATEGY-38: precondition — paint did not populate both blocks";
        var freshCell = new Cell("y = 1");
        view.Bind(freshCell);
        if (view.RichBlockVisible) return "S21/STRATEGY-38: cell rebind did not clear the rich block";
        if (view.ConsoleBlockVisible) return "S21/STRATEGY-38: cell rebind did not clear the console block";

        lane.Dispose();
        return null;
    }

    // S20 fake: a settable {rich, console} payload for the pressed cell so the controller's
    // passthrough (SetOutput + SetConsole) and the view's dynamic layout are both exercised.  S21
    // adds a multi-cell mode via SetMulti so STRATEGY-40 can return BOTH the pressed cell's output
    // AND a reactive descendant's output from one press (verifying index→region routing across cells).
    sealed class _ConsoleExecutor : INotebookCellExecutor
    {
        string _output, _mimetype, _data;
        ConsoleSegment[] _console = System.Array.Empty<ConsoleSegment>();
        (int idx, string output, string mime, string data, ConsoleSegment[] console)[] _multi;
        public void SetOutput(string output, string mimetype, string data) { _output = output; _mimetype = mimetype; _data = data; }
        public void SetConsole(ConsoleSegment[] console) { _console = console ?? System.Array.Empty<ConsoleSegment>(); }
        public void SetMulti(params (int idx, string output, string mime, string data, ConsoleSegment[] console)[] multi)
            => _multi = (multi != null && multi.Length > 0) ? multi : null;
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson, string strategyPath)
        {
            if (_multi != null)
            {
                var ran = new NotebookCellOutput[_multi.Length];
                for (int i = 0; i < _multi.Length; i++)
                {
                    var m = _multi[i];
                    ran[i] = new NotebookCellOutput
                    {
                        Index = m.idx,
                        Output = m.output,
                        Ok = true,
                        Mimetype = m.mime,
                        Data = m.data,
                        Console = m.console,
                    };
                }
                return new NotebookRunResult { Ok = true, Ran = ran };
            }
            return new NotebookRunResult
            {
                Ok = true,
                Ran = new[] { new NotebookCellOutput {
                    Index = pressedIndex, Output = _output, Ok = true,
                    Mimetype = _mimetype, Data = _data, Console = _console } },
            };
        }
        public int[] Restage(string source) => System.Array.Empty<int>();
    }

    static int CountSubstring(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // ====== Section 22 — #102 audit gaps (findings 0079 §6) ======
    //
    // Covers: STRATEGY-39 `&` literal must NOT be entity-escaped (UGUI does not decode, so
    //         Replace("&","&amp;") would paint `a & b` as `a &amp; b` — pure regression);
    //         STRATEGY-40 multi-cell routing: a press that produces output for the pressed cell
    //         AND a reactive descendant routes each console to ITS OWN region (no bleed);
    //         STRATEGY-41 re-press of the same cell REPLACES the prior console (does not append);
    //         STRATEGY-42 re-press of the same cell with EMPTY hides the console block;
    //         STRATEGY-43 overflow → real ScrollRect: Content > Viewport, verticalNormalizedPosition
    //         operable end-to-end (findings 0079 §6 D5 — supersedes RectMask2D-clip);
    //         STRATEGY-44 first-frame bodyH==0: paint must still produce a visible block with
    //         preferredHeight > 0 (no `ForceRebuildLayoutImmediate` priming);
    //         STRATEGY-45 `</color>` injection-resistance: a stderr segment containing `</color>`
    //         must be guarded inside a TMP `<noparse>` span so it cannot close our amber wrapper (#118);
    //         STRATEGY-46 dormant-reuse race: a press → DeleteCell → AddCell that reuses dormant
    //         R1 must NOT paint the prior cell's stdout onto the rebound view (ListMutated bumps
    //         the run controller's generation, identical to how Open/New already drops stale).
    //
    // Python-FREE.  The Python pytest gate (test_notebook_console.py) already covers the marimo-side
    // capture; this section pins the C# routing, layout, escape, and race guards.
    static string Section22_ConsoleAuditGaps(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string R2 = "strategy_editor:region_002";
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer21", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                // Spawned region windows also need a resolvable body rect; the production root sizes
                // them via FloatingWindowController.Spawn args, but the bare-RT controller here just
                // calls our factory and never sets a sizeDelta — pin it so the dynamic layout works.
                rt.sizeDelta = new Vector2(400f, 400f);
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                var v = StrategyEditorContentBuilder.Build(body);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        // STRATEGY-44 leans on the adopted window being at default zero size before any
        // ForceRebuildLayoutImmediate — build the view INSIDE that bodyH==0 frame.  Subsequent tests
        // resize the root to 400 for stable layout.
        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody);
        if (view1 == null) return "S21: adopted view build failed";
        views[R1] = view1;
        controller.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, R1, adoptRoot);

        var nb = new MarimoNotebookDocument(new FakeMarimoSynthesizer());
        var coord = new NotebookCellCoordinator(
            nb, controller, r => views.TryGetValue(r, out var v) ? v : null, () => Vector2.zero, new Vector2(520f, 380f));
        coord.SyncWindowsToNotebook(null);
        if (coord.RegionOf(nb.Cells[0]) != R1) return "S21: precondition — cell0 not bound to region_001";

        var exec = new _ConsoleExecutor();
        var lane = new NotebookRunLane(exec, startWorker: false);
        var run = new NotebookRunController(coord, r => views.TryGetValue(r, out var v) ? v : null, lane);
        // #102 findings 0079 §6 D7: production wiring — coord mutations drop in-flight runs.
        coord.ListMutated += () => run.Invalidate();
        var view = views[R1];

        // ---- STRATEGY-44: bodyH==0 first-frame race (no ForceRebuildLayoutImmediate priming) ----
        // The adopted window's RectTransform has not yet resolved (no parent canvas + no force-rebuild),
        // so adoptBody.rect.height == 0 on this very first paint.  ApplyBlockSize must take the
        // no-cap branch (natural drives) and still leave a visible block with preferredHeight > 0.
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "first-frame\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.ConsoleBlockVisible) return "S22/STRATEGY-44: console block not visible on first-frame paint (bodyH==0)";
        var conBlockRT = view.transform.parent.Find("ConsoleOutputBlock") as RectTransform;
        if (conBlockRT == null) return "S22/STRATEGY-44: console block RT not found under body";
        var conLE0 = conBlockRT.GetComponent<LayoutElement>();
        if (conLE0 == null || !(conLE0.preferredHeight > 0f))
            return "S22/STRATEGY-44: preferredHeight stayed 0 on first-frame paint, got " + (conLE0 != null ? conLE0.preferredHeight : 0f);

        // Stabilise the body for the remaining assertions.
        adoptRoot.sizeDelta = new Vector2(400f, 400f);
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(adoptRoot);

        // ---- STRATEGY-39: `&` literal must NOT be entity-escaped ----
        exec.SetOutput(null, null, null);
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "a & b\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        var ctext = view.CurrentConsoleText ?? string.Empty;
        if (!ctext.Contains("a & b"))
            return "S22/STRATEGY-39: literal '&' missing — payload not preserved verbatim, got [" + ctext + "]";
        if (ctext.Contains("&amp;"))
            return "S22/STRATEGY-39: '&' was double-escaped to '&amp;' — TMP would paint the entity literally, got [" + ctext + "]";

        // ---- STRATEGY-45 (#118 TMP): `</color>` in stderr must not close our amber wrapper ----
        exec.SetConsole(new[] {
            new ConsoleSegment { Stream = "stderr", Text = "start" },
            new ConsoleSegment { Stream = "stderr", Text = "</color>middle" },
            new ConsoleSegment { Stream = "stdout", Text = "end" },
        });
        run.RunCell(R1); run.DrainAndRoute();
        ctext = view.CurrentConsoleText ?? string.Empty;
        // #118: BuildConsoleRichText wraps EACH segment's payload in <noparse>…</noparse> and puts OUR
        // amber <color=#ffa01c>…</color> OUTSIDE it.  TMP does not parse anything inside <noparse>, so
        // the user's literal `</color>` is inert — it cannot close our wrapper.  The TMP safety property
        // is therefore "the user's </color> is guarded inside a noparse span", not UGUI's string-balance.
        if (ctext.Contains("&lt;"))
            return "S22/STRATEGY-45: user's '</color>' was entity-escaped — TMP renders &lt; literally (regression), got [" + ctext + "]";
        if (!ctext.Contains("<noparse></color>middle</noparse>"))
            return "S22/STRATEGY-45: user's '</color>middle' is not guarded inside a <noparse> span (could close our amber tag), got [" + ctext + "]";
        int opens = CountSubstring(ctext, "<color=#ffa01c>");
        if (opens != 2)
            return "S22/STRATEGY-45: expected 2 amber opens (one per stderr segment), got opens=" + opens;

        // ---- STRATEGY-41: re-press REPLACES (does not append) ----
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "round1\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "round2\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        ctext = view.CurrentConsoleText ?? string.Empty;
        if (ctext.Contains("round1"))
            return "S22/STRATEGY-41: re-press did not REPLACE — prior 'round1' leaked, got [" + ctext + "]";
        if (!ctext.Contains("round2"))
            return "S22/STRATEGY-41: re-press did not paint 'round2', got [" + ctext + "]";

        // ---- STRATEGY-42: re-press with EMPTY hides the console block ----
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "transient\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.ConsoleBlockVisible) return "S22/STRATEGY-42: precondition — console did not become visible";
        exec.SetConsole(Array.Empty<ConsoleSegment>());
        run.RunCell(R1); run.DrainAndRoute();
        if (view.ConsoleBlockVisible) return "S22/STRATEGY-42: re-press with empty segments did not hide the console block";

        // ---- STRATEGY-43: overflow → real ScrollRect (findings 0079 §6 D5) ----
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append("line ").Append(i).Append('\n');
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = sb.ToString() } });
        run.RunCell(R1); run.DrainAndRoute();
        var scroll = view.ConsoleScrollRect;
        if (scroll == null) return "S22/STRATEGY-43: ConsoleScrollRect not wired";
        if (scroll.content == null) return "S22/STRATEGY-43: ScrollRect.content not wired";
        if (scroll.viewport == null) return "S22/STRATEGY-43: ScrollRect.viewport not wired";
        if (scroll.verticalScrollbar == null) return "S22/STRATEGY-43: ScrollRect.verticalScrollbar not wired";
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(adoptRoot);
        float contentH = scroll.content.rect.height;
        float viewportH = scroll.viewport.rect.height;
        if (!(contentH > viewportH + 1f))
            return "S22/STRATEGY-43: 40-line payload did not overflow viewport (content=" + contentH + " viewport=" + viewportH + ")";
        // Setting verticalNormalizedPosition end-to-end (0=bottom, 1=top) must stick — proof the
        // ScrollRect is genuinely controlling content position, not a no-op clip.
        scroll.verticalNormalizedPosition = 0f;
        if (Mathf.Abs(scroll.verticalNormalizedPosition - 0f) > 0.01f)
            return "S22/STRATEGY-43: setting verticalNormalizedPosition=0 did not stick (got " + scroll.verticalNormalizedPosition + ")";
        scroll.verticalNormalizedPosition = 1f;
        if (Mathf.Abs(scroll.verticalNormalizedPosition - 1f) > 0.01f)
            return "S22/STRATEGY-43: setting verticalNormalizedPosition=1 did not stick (got " + scroll.verticalNormalizedPosition + ")";

        // ---- STRATEGY-40: multi-cell routing (pressed R1 + descendant R2) ----
        // AddCell so coord.RegionOf(cells[1]) resolves to R2 (region_002 spawn).
        var cellB = coord.AddCell();
        if (coord.RegionOf(cellB) != R2)
            return "S22/STRATEGY-40: precondition — second cell not bound to region_002, got " + coord.RegionOf(cellB);
        StrategyEditorView view2;
        if (!views.TryGetValue(R2, out view2) || view2 == null)
            return "S22/STRATEGY-40: precondition — region_002 view not built by FW factory";
        // Reset R1 between sub-tests (re-press with empty hides; we want both blocks clean).
        exec.SetConsole(Array.Empty<ConsoleSegment>());
        run.RunCell(R1); run.DrainAndRoute();

        // Executor returns TWO ran entries: pressed (idx=0 → R1) + descendant (idx=1 → R2).
        exec.SetMulti(
            (0, null, null, null, new[] { new ConsoleSegment { Stream = "stdout", Text = "from-cell-0\n" } }),
            (1, null, null, null, new[] { new ConsoleSegment { Stream = "stdout", Text = "from-cell-1\n" } }));
        run.RunCell(R1); run.DrainAndRoute();
        var t1 = view.CurrentConsoleText ?? string.Empty;
        var t2 = view2.CurrentConsoleText ?? string.Empty;
        if (!t1.Contains("from-cell-0"))
            return "S22/STRATEGY-40: R1 (pressed) console missing 'from-cell-0', got [" + t1 + "]";
        if (t1.Contains("from-cell-1"))
            return "S22/STRATEGY-40: descendant text leaked into pressed cell R1, got [" + t1 + "]";
        if (!t2.Contains("from-cell-1"))
            return "S22/STRATEGY-40: R2 (descendant) console missing 'from-cell-1', got [" + t2 + "]";
        if (t2.Contains("from-cell-0"))
            return "S22/STRATEGY-40: pressed text leaked into descendant cell R2, got [" + t2 + "]";

        // Reset multi-mode (empty params → _multi=null) and prep both views for STRATEGY-46.
        exec.SetMulti();
        exec.SetConsole(Array.Empty<ConsoleSegment>());
        run.RunCell(R1); run.DrainAndRoute();
        run.RunCell(R2); run.DrainAndRoute();

        // ---- STRATEGY-46: dormant-reuse race (findings 0079 §6 D7) ----
        // State here: notebook = [cellA, cellB], R1 = cellA, R2 = cellB (AddCell(cellB) above).
        // Goal: a press queued AGAINST cellA must NOT paint onto a cellC that later reuses dormant
        // R1.  The synchronous lane queues the press result inside RunCell with generation N; the
        // subsequent DeleteCell(R1) → AddCell() reuses dormant R1 and binds it to a fresh cellC.
        // ListMutated bumps the controller's generation each mutation, so the queued result is
        // dropped at drain — otherwise cellA's stdout would paint onto cellC's view.
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "stale-from-A\n" } });
        run.RunCell(R1);   // synchronous lane queues result with current generation N
        if (!coord.DeleteCell(R1)) return "S22/STRATEGY-46: precondition — DeleteCell(R1) failed";
        // After DeleteCell, R1 is hidden+dormant.  AddCell reuses the dormant R1 (the adopted shell
        // is never-Destroy, findings 0050) and binds a fresh cellC to it — same GameObject + same
        // StrategyEditorView, different Cell.
        var cellC = coord.AddCell();
        if (coord.RegionOf(cellC) != R1)
            return "S22/STRATEGY-46: precondition — AddCell did not reuse dormant R1, got " + coord.RegionOf(cellC);
        if (!ReferenceEquals(views[R1], view))
            return "S22/STRATEGY-46: precondition — R1 view recreated (should be same adopted shell)";
        if (view.BoundCell != cellC)
            return "S22/STRATEGY-46: precondition — R1 view not rebound to cellC";
        // NOW drain.  The press's result frame predates DeleteCell+AddCell — generation bump must drop it.
        run.DrainAndRoute();
        if (view.ConsoleBlockVisible)
            return "S22/STRATEGY-46: stale drain painted onto cellC's rebound view — generation guard missing";
        if ((view.CurrentConsoleText ?? string.Empty).Contains("stale-from-A"))
            return "S22/STRATEGY-46: stale stdout 'stale-from-A' leaked into cellC's view";

        lane.Dispose();
        return null;
    }

    // ── Section 25: #138 (findings 0110) — the Strategy Editor authoring surface is HIDDEN in LiveManual.
    //    The mirror of the order ticket (visible ONLY in LiveManual): in Replay (backtest needs Python)
    //    and LiveAuto (the cell drives the strategy) the strategy_editor cell windows + the [+] Add Cell button
    //    stay visible; in LiveManual (the human trades via the order ticket — no Python authoring) they are
    //    hidden, and the order ticket is the inverse (front-plane exclusivity). Hiding is PURE VISIBILITY
    //    (SetActive): the windows are NOT destroyed and their geometry is preserved across the round-trip,
    //    and a Save (CapturePositions) taken while hidden still records the live positions (AC5).
    //    A SECOND cell window (region_002) is spawned so the multi-window "ALL windows" contract (AC1) and
    //    the HideKind/ShowHidden loop are exercised — a single-window-only regression would NOT pass.
    //    Real BackcastWorkspaceRoot composed from the authored scene (Python-FREE via FakeMarimoSynthesizer);
    //    the REAL DriveStrategyEditor / DriveOrderTicket / FooterModeViewModel.ApplyPoll / coordinator are
    //    driven (the SAME seam OrderTicketE2ERunner SectionC uses for ORDER-14). Vacuity guard: every
    //    seam/widget is asserted to EXIST first, so a rename FAILS (not vacuously passes).
    //    RUNS LAST: OpenScene(Single) fake-nulls earlier synthetic GameObjects (Run's finally guards != null).
    //    Covers: STRATEGY-53 (mode→visibility for ALL windows + exclusivity), STRATEGY-54 (non-destructive:
    //    same instance + geometry + Save-while-hidden / AC5), STRATEGY-55 (a dormant region_001 shell is NOT
    //    resurrected by the toggle — the remembered-set re-shows only what it hid).
    static string Section25_ModeConditionalVisibility(List<GameObject> spawned)
    {
        const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "S25: BackcastWorkspaceRoot missing in scene";
        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        Func<RectTransform> editorWin = () => ty.GetField("_strategyEditorWindow", BF)?.GetValue(root) as RectTransform;
        var addCellOverlay = ty.GetField("_addCellOverlay", BF)?.GetValue(root) as GameObject;
        var orderWindow = ty.GetField("_orderWindow", BF)?.GetValue(root) as RectTransform;
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root);
        var windows = ty.GetField("_windows", BF)?.GetValue(root) as FloatingWindowController;
        var coord = ty.GetField("_coordinator", BF)?.GetValue(root) as NotebookCellCoordinator;
        if (editorWin() == null) return "S25: _strategyEditorWindow missing in scene";
        if (addCellOverlay == null) return "S25: _addCellOverlay not built ([+] Add Cell toggle target missing — renamed?)";
        if (orderWindow == null) return "S25: _orderWindow not built (front-plane exclusivity mirror target renamed?)";
        if (footerMode == null) return "S25: _footerMode not built";
        if (windows == null) return "S25: _windows controller not built";
        if (coord == null) return "S25: _coordinator not built";

        var applyPoll = footerMode.GetType().GetMethod("ApplyPoll");
        if (applyPoll == null) return "S25: FooterModeViewModel.ApplyPoll not found (renamed?)";
        var driveEditor = ty.GetMethod("DriveStrategyEditor", BF);
        if (driveEditor == null) return "S25: DriveStrategyEditor not found (renamed?)";
        var driveOrder = ty.GetMethod("DriveOrderTicket", BF);
        if (driveOrder == null) return "S25: DriveOrderTicket not found (renamed?)";

        // Deterministic 2-cell notebook so "ALL windows" (AC1) and the multi-window loop are REAL (not
        // vacuously single-window): the authored scene boots with region_001 dormant, so New() first binds
        // cell0 into region_001 (un-dormant), then AddCell spawns a DISTINCT region_002 window.
        coord.New();
        var cell2 = coord.AddCell();
        string r2 = coord.RegionOf(cell2);
        Func<RectTransform> win2 = () => windows.RectOf(r2);
        if (r2 == NotebookCellCoordinator.AdoptedRegionId || win2() == null)
            return "S25: AddCell did not spawn a DISTINCT second strategy_editor window (region_002), got " + r2;

        void Poll(string mode, string venueState)
            => applyPoll.Invoke(footerMode, new object[] { "{\"execution_mode\":\"" + mode + "\",\"venue_state\":\"" + venueState + "\"}" });
        void Drive() { driveEditor.Invoke(root, null); driveOrder.Invoke(root, null); }

        // STRATEGY-53: the WHOLE surface (ALL strategy_editor windows + [+] Add Cell) toggles with mode —
        //   visible in Replay/LiveAuto, hidden in LiveManual — and the order ticket is the inverse.
        Poll("Replay", "");
        Drive();
        if (!editorWin().gameObject.activeSelf) return "STRATEGY-53: region_001 hidden under Replay (must be visible)";
        if (!win2().gameObject.activeSelf) return "STRATEGY-53: region_002 hidden under Replay (ALL cell windows must be visible)";
        if (!addCellOverlay.activeSelf) return "STRATEGY-53: [+] Add Cell hidden under Replay (must be visible)";
        if (orderWindow.gameObject.activeSelf) return "STRATEGY-53: order ticket visible under Replay (front-plane exclusivity)";

        Poll("LiveAuto", "CONNECTED");
        Drive();
        if (!editorWin().gameObject.activeSelf) return "STRATEGY-53: region_001 hidden under LiveAuto (cell drives the strategy — must stay visible)";
        if (!win2().gameObject.activeSelf) return "STRATEGY-53: region_002 hidden under LiveAuto (must stay visible)";
        if (!addCellOverlay.activeSelf) return "STRATEGY-53: [+] Add Cell hidden under LiveAuto (must be visible)";

        Poll("LiveManual", "CONNECTED");
        Drive();
        if (editorWin().gameObject.activeSelf) return "STRATEGY-53: region_001 VISIBLE under LiveManual (must be hidden)";
        if (win2().gameObject.activeSelf) return "STRATEGY-53: region_002 VISIBLE under LiveManual (ALL cell windows must be hidden)";
        if (addCellOverlay.activeSelf) return "STRATEGY-53: [+] Add Cell VISIBLE under LiveManual (must be hidden)";
        if (!orderWindow.gameObject.activeSelf) return "STRATEGY-53: order ticket NOT visible under LiveManual (front-plane exclusivity)";
        Debug.Log("[E2E STRATEGY-53 PASS] the whole strategy editor surface (ALL cell windows + [+] Add Cell) toggles with mode — visible in Replay/LiveAuto, hidden in LiveManual; order ticket is the inverse");

        // STRATEGY-54: hiding is PURE VISIBILITY — same window instances + geometry preserved across the
        //   Replay → LiveManual → Replay round-trip, AND a Save (CapturePositions) taken WHILE hidden in
        //   LiveManual still records the live positions (AC5). A "close+respawn"/"reset geometry"/"capture
        //   skips inactive windows" impl would fail.
        Poll("Replay", "");
        Drive();
        var w1 = editorWin(); var w2 = win2();
        int id1 = w1.GetInstanceID(), id2 = w2.GetInstanceID();
        Vector2 p1 = w1.anchoredPosition, s1 = w1.sizeDelta, p2 = w2.anchoredPosition, s2 = w2.sizeDelta;
        var posReplay = coord.CapturePositions();
        Poll("LiveManual", "CONNECTED");
        Drive();
        if (editorWin() == null || win2() == null) return "STRATEGY-54: a strategy editor window was destroyed on entering LiveManual (must be hide-not-destroy)";
        var posHidden = coord.CapturePositions();   // AC5: Save while the surface is hidden
        if (posHidden.Count != posReplay.Count) return "STRATEGY-54: CapturePositions count changed when Saved during LiveManual (AC5)";
        for (int i = 0; i < posHidden.Count; i++)
            if ((posHidden[i] - posReplay[i]).sqrMagnitude > EPS) return "STRATEGY-54: a strategy editor position was dropped/changed when Saved during LiveManual (AC5)";
        Poll("Replay", "");
        Drive();
        var b1 = editorWin(); var b2 = win2();
        if (b1 == null || b1.GetInstanceID() != id1 || b2 == null || b2.GetInstanceID() != id2)
            return "STRATEGY-54: a strategy editor window is a new instance after a LiveManual round-trip (must be hide-not-destroy)";
        if ((b1.anchoredPosition - p1).sqrMagnitude > EPS || (b1.sizeDelta - s1).sqrMagnitude > EPS
            || (b2.anchoredPosition - p2).sqrMagnitude > EPS || (b2.sizeDelta - s2).sqrMagnitude > EPS)
            return "STRATEGY-54: strategy editor geometry changed across the hide/show (pos/size not preserved)";
        if (!b1.gameObject.activeSelf || !b2.gameObject.activeSelf) return "STRATEGY-54: strategy editor not restored to visible after leaving LiveManual";
        Debug.Log("[E2E STRATEGY-54 PASS] LiveManual hide is pure visibility — same window instances + geometry preserved across the round-trip, and Save-while-hidden keeps positions (AC5)");

        // STRATEGY-55 (#138 regression guard): the mode toggle must re-show ONLY the windows IT hid — never a
        //   window hidden for an INDEPENDENT reason. Delete region_001's cell → it goes dormant (hidden,
        //   ADR-0013 D4) while region_002 stays. DriveStrategyEditor in Replay (show side), and a LiveManual
        //   round-trip, must both leave the dormant shell hidden. A blanket "SetActive(true) on every
        //   strategy_editor window" would resurrect the empty dormant shell — that is the RED this pins.
        string r1 = NotebookCellCoordinator.AdoptedRegionId;
        Poll("Replay", "");
        Drive();
        if (windows.RectOf(r1) == null || !windows.RectOf(r1).gameObject.activeSelf) return "STRATEGY-55: precondition — region_001 not visible before delete";
        if (!coord.DeleteCell(r1)) return "STRATEGY-55: precondition — DeleteCell(region_001) failed";
        if (windows.RectOf(r1).gameObject.activeSelf) return "STRATEGY-55: precondition — DeleteCell did not hide region_001 (dormant shell)";
        Drive();   // Replay show-side: must NOT resurrect the dormant shell
        if (windows.RectOf(r1).gameObject.activeSelf) return "STRATEGY-55: dormant region_001 shell resurrected by the mode toggle in Replay (must stay hidden)";
        if (!win2().gameObject.activeSelf) return "STRATEGY-55: region_002 wrongly hidden (only the dormant shell must stay hidden)";
        Poll("LiveManual", "CONNECTED");
        Drive();
        Poll("Replay", "");
        Drive();
        if (windows.RectOf(r1).gameObject.activeSelf) return "STRATEGY-55: dormant region_001 shell resurrected after a LiveManual round-trip (must stay hidden)";
        if (!win2().gameObject.activeSelf) return "STRATEGY-55: region_002 not restored after the LiveManual round-trip";
        Debug.Log("[E2E STRATEGY-55 PASS] the LiveManual visibility toggle re-shows only the windows it hid — a dormant region_001 shell is never resurrected");

        return null;
    }
}
