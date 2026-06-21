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
using UnityEditor;
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
                ?? Section20_ConsoleAndDynamicLayout(spawned)
                ?? Section21_ConsoleAuditGaps(spawned);
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
                      "Open-failure keeps window) + non-scroll mesh colouring (real Text, token glyph vertex colour, Default unchanged, " +
                      "no tag injection) + registry run-wiring (#78 RegistryStrategyFileProvider: unregistered/unbound/dirty/" +
                      "torn-down -> false -> Run blocked; saved editor .py flows through, re-resolved live each call) " +
                      "+ #81 notebook aggregate (fresh=1 empty cell, AddCell dirties, body-edit dirties, SaveAs/Save->Open " +
                      "round-trip preserves body+name+config, >=1 delete guard, non-marimo Open wraps as 1 cell (#86), supplyable " +
                      "5-condition, ResetUnboundEmpty) + SpawnPlacement (anchor-start, diagonal cascade, overlap-allowed, " +
                      "<10 threshold, full-chain clear) + NotebookCellCoordinator (cell0->region_001, AddCell->region_002 spawn, " +
                      "DeleteCell despawn region_002 / hide-dormant region_001, >=1 guard, dormant reuse, CapturePositions cell-order) " +
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
                      "+ #102 console + dynamic output layout (STRATEGY-34..38: per-cell stdout/stderr " +
                      "segments paint into the console block in arrival order with stderr amber-tagged, " +
                      "an empty rich+console body collapses to editor-only — blocks deactivate so the " +
                      "body's VerticalLayoutGroup skips them, populated blocks cap at body * 0.45, " +
                      "cell rebind clears both rich and console panes) " +
                      "+ #102 audit gaps (STRATEGY-39..46 / findings 0076 §6: '&' literal not entity-" +
                      "escaped, multi-cell index→region routing without bleed, re-press replaces (not " +
                      "appends), re-press with empty hides the block, overflow → real ScrollRect with " +
                      "operable verticalNormalizedPosition, first-frame bodyH==0 still paints visibly, " +
                      "'</color>' injection escaped, dormant-reuse race dropped via ListMutated → Invalidate) " +
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
    // 8. non-scroll mesh colouring (real Text component + synthetic base mesh)
    // Covers: STRATEGY-05 (実 mesh 着色の non-scroll 部 — scroll 着色は HITL専用)
    // ======================================================================
    static string Section8_MeshColoring(List<GameObject> spawned)
    {
        const string src = "def f(): return 1  # c";
        var tokens = PythonHighlighter.Tokenize(src);

        var go = new GameObject("MeshProbeText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(PythonSyntaxMeshEffect));
        spawned.Add(go);
        var text = go.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        Color baseColor = new Color(0.9f, 0.9f, 0.9f, 1f);
        text.color = baseColor;
        text.text = src;
        var effect = go.GetComponent<PythonSyntaxMeshEffect>();
        effect.SetTokens(tokens);

        // Build a synthetic base mesh with one white quad per glyph-producing char (the real
        // TextGenerator's whitespace-skipping is cross-checked separately below). Map char index
        // -> quad rank so we can read back a known glyph's colour after ModifyMesh.
        var vh = new VertexHelper();
        var rankOf = new Dictionary<int, int>();
        int rank = 0;
        var quad = new UIVertex[4];
        for (int i = 0; i < 4; i++) quad[i] = UIVertex.simpleVert;
        for (int j = 0; j < src.Length; j++)
        {
            if (src[j] == ' ' || src[j] == '\t' || src[j] == '\n' || src[j] == '\r') continue;
            rankOf[j] = rank++;
            vh.AddUIVertexQuad(quad);
        }

        effect.ModifyMesh(vh);

        // 'd'(0)=keyword, 'f'(4)=definition, 'r'(9)=keyword, '1'(16)=number, '#'(19)=comment,
        // '('(5)=Default(unchanged base).
        if (!GlyphColor(vh, rankOf, 0, effect.keyword)) return "S8: 'def' keyword glyph not keyword-coloured";
        if (!GlyphColor(vh, rankOf, 4, effect.definition)) return "S8: definition name glyph not definition-coloured";
        if (!GlyphColor(vh, rankOf, 9, effect.keyword)) return "S8: 'return' glyph not keyword-coloured";
        if (!GlyphColor(vh, rankOf, 16, effect.number)) return "S8: number glyph not number-coloured";
        if (!GlyphColor(vh, rankOf, 19, effect.comment)) return "S8: comment glyph not comment-coloured";
        if (!GlyphColor(vh, rankOf, 5, baseColor)) return "S8: Default glyph '(' was recoloured";

        // No tag injection: the source string the editor holds is never mutated by colouring.
        if (text.text != src) return "S8: colouring mutated the source text (tag injection)";

        // Cross-check the whitespace-skipping premise against the REAL TextGenerator when fonts are
        // available; otherwise HITL (findings 0010 §9 fallback).
        try
        {
            var settings = text.GetGenerationSettings(new Vector2(2000f, 2000f));
            text.cachedTextGenerator.Populate(src, settings);
            int visible = text.cachedTextGenerator.vertexCount / 4;
            if (visible > 0 && visible != rank)
                Debug.LogWarning($"[E2E STRATEGY NOTEBOOK] S8: real TextGenerator visible glyphs {visible} != synthetic {rank} " +
                                 "-> whitespace-skipping premise needs the HITL check.");
            else if (visible == 0)
                Debug.LogWarning("[E2E STRATEGY NOTEBOOK] S8: TextGenerator produced no glyphs in batchmode -> glyph-count cross-check HITL-only.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[E2E STRATEGY NOTEBOOK] S8: TextGenerator unavailable in batchmode (" + e.Message + ") -> glyph-count cross-check HITL-only.");
        }
        return null;
    }

    // ======================================================================
    // 10. #81 MarimoNotebookDocument aggregate (ADR-0013 / findings 0050) — driven by the
    //     Covers: STRATEGY-01, STRATEGY-10, STRATEGY-12, STRATEGY-15, STRATEGY-16, STRATEGY-17
    //     (notebook 集約: add/edit dirty・≥1 guard・supplyable・ResetUnboundEmpty(File→New aggregate 側,
    //      MenuBar MENU-02 が正本)・Open 分解・Save 合成 round-trip)
    //     Python-FREE FakeMarimoSynthesizer (the SAME round-trip contract the layer-2 pythonnet +
    //     layer-3 marimo golden assert, so a drifting fake is caught mechanically).
    // ======================================================================
    static string Section10_NotebookAggregate()
    {
        Directory.CreateDirectory(TempDir);
        var synth = new FakeMarimoSynthesizer();
        var nb = new MarimoNotebookDocument(synth);

        // fresh notebook: one empty cell, unbound, not dirty, not supplyable (>=1 invariant).
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
        // F3: a valid-marimo Open is NOT wrap mode (the toast must stay "Opened X" with no wrap hint).
        if (nb2.WrapMode) return "S10/F3: valid-marimo Open should not set WrapMode (toast would falsely warn about marimo conversion)";
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

        // >=1 delete guard: the last cell cannot be removed.
        var single = new MarimoNotebookDocument(synth);
        if (single.RemoveCell(single.Cells[0])) return "S10: removed the last cell (>=1 guard breached)";
        if (single.CellCount != 1) return "S10: last-cell removal mutated count";
        if (!nb2.RemoveCell(nb2.Cells[0])) return "S10: RemoveCell on a 2-cell notebook failed";
        if (nb2.CellCount != 1) return "S10: RemoveCell did not shrink";

        // #86: Open of a non-marimo `.py` (Decompose returns null) BOOTSTRAPS a 1-cell wrap whose body
        // is the raw file content verbatim — Open SUCCEEDS, the notebook binds, and Run becomes possible
        // (the on-disk file is still imperative, so Run goes through the imperative branch until Save).
        // The synthesiser's null contract is unchanged; the wrap is the aggregate's policy.
        var failSynth = new FakeMarimoSynthesizer { FailDecompose = true };
        var nb3 = new MarimoNotebookDocument(failSynth);
        string rawPath = Path.Combine(TempDir, "nb_nonmarimo.py");
        string rawContent = "class V19MorningStrategy(Strategy):\n    def on_bar(self, bar):\n        pass\n";
        File.WriteAllText(rawPath, rawContent);
        if (!nb3.Open(rawPath)) return "S10: Open of a non-marimo .py should succeed via 1-cell wrap";
        if (nb3.CellCount != 1) return "S10: non-marimo wrap should produce exactly 1 cell";
        if (nb3.Cells[0].Body != rawContent) return "S10: 1-cell wrap body != raw file content";
        if (nb3.Cells[0].Name != "_") return "S10: 1-cell wrap should use anonymous name '_'";
        if (!nb3.IsBound || nb3.IsDirty) return "S10: 1-cell wrap should bind + not be dirty";
        if (!nb3.TryGetStrategyFile(out _)) return "S10: 1-cell wrap notebook not supplyable";
        // F3 (#86, findings 0054 §D2a): the wrap leg MUST surface WrapMode=true so OnFileOpen can
        // warn "Save will convert to marimo" — without this the destructive §D2 conversion is silent.
        if (!nb3.WrapMode) return "S10/F3: wrap-Open did not set WrapMode (toast cannot distinguish wrap from clean marimo Open)";
        // Lock the wrap-cell dirty hook: editing the wrapped body must flip IsDirty (a future refactor
        // that drops the per-cell BindBodyChanged loop would otherwise pass S10 yet break supplyable).
        nb3.Cells[0].SetBody(rawContent + "# edited\n");
        if (!nb3.IsDirty) return "S10: wrap cell body edit did not dirty the notebook (BindBodyChanged lost)";

        // F1 (#86 review): a wrap Open MUST NOT silently overwrite an unsaved notebook. With IsDirty=true
        // the Open of a non-marimo `.py` is REFUSED (fail-soft) and the existing cell list is PRESERVED
        // verbatim — discard-confirm is a higher-layer UX slice; the aggregate just guards the invariant.
        var nb5 = new MarimoNotebookDocument(failSynth);
        nb5.AddCell();                                            // 2 cells
        nb5.Cells[0].SetBody("unsaved_work = 42");                // dirty
        if (!nb5.IsDirty) return "S10/F1: precondition — nb5 should be dirty";
        int beforeCount = nb5.CellCount;
        string beforeBody0 = nb5.Cells[0].Body;
        string beforeBody1 = nb5.Cells[1].Body;
        string rawPath2 = Path.Combine(TempDir, "nb_nonmarimo_dirty.py");
        File.WriteAllText(rawPath2, "class Other(Strategy):\n    pass\n");
        if (nb5.Open(rawPath2)) return "S10/F1: dirty notebook Open(non-marimo) should fail-soft (must refuse to overwrite)";
        if (nb5.LastError == null) return "S10/F1: refused Open did not set LastError";
        if (nb5.CellCount != beforeCount) return "S10/F1: refused Open mutated cell count";
        if (nb5.Cells[0].Body != beforeBody0 || nb5.Cells[1].Body != beforeBody1) return "S10/F1: refused Open mutated cell bodies";
        if (!nb5.IsDirty) return "S10/F1: refused Open cleared the dirty flag";

        // F1-DISCARD (#87 slice 2 — MarimoNotebookDocument discard-authorization seam): the F1 refuse
        // above is the DEFAULT (discardDirty:false). When the caller AUTHORIZES a discard via
        // discardDirty:true (the higher-layer SaveGuard "Discard" verdict, wired in a later slice),
        // a dirty notebook DISCARDS its unsaved cells and wraps the new non-marimo `.py` as 1 cell,
        // binding clean — this is the exact aggregate seam the SaveGuard→Discard→Open(discardDirty:true)
        // path will call. Non-vacuous litmus (two stakes, mutually protective): deleting the
        // `&& !discardDirty` relaxation makes THIS section RED (Open refuses an authorized discard),
        // while removing the `_dirty` guard entirely makes the nb5 F1-refuse section above RED.
        var nb7 = new MarimoNotebookDocument(failSynth);
        nb7.AddCell();                                            // 2 cells
        nb7.Cells[0].SetBody("unsaved_work = 99");                // dirty
        if (!nb7.IsDirty) return "S10/F1-DISCARD: precondition — nb7 should be dirty";
        if (!nb7.Open(rawPath2, discardDirty: true))
            return "S10/F1-DISCARD: dirty Open(non-marimo, discardDirty:true) should succeed (authorized discard, LastError=" + (nb7.LastError ?? "<null>") + ")";
        if (nb7.CellCount != 1) return "S10/F1-DISCARD: authorized-discard wrap should produce exactly 1 cell (dirty cells not discarded)";
        if (nb7.Cells[0].Body != "class Other(Strategy):\n    pass\n")
            return "S10/F1-DISCARD: authorized-discard wrap body != the new file content (stale dirty cell survived)";
        if (nb7.Cells[0].Name != "_") return "S10/F1-DISCARD: authorized-discard wrap should use anonymous name '_'";
        if (!nb7.IsBound || nb7.IsDirty) return "S10/F1-DISCARD: authorized-discard wrap should bind + be clean";
        if (!nb7.WrapMode) return "S10/F1-DISCARD: authorized-discard wrap did not set WrapMode (toast cannot warn about §D2 conversion)";
        if (nb7.LastError != null) return "S10/F1-DISCARD: authorized-discard wrap set LastError (it is success, not fail-soft)";
        if (!nb7.TryGetStrategyFile(out _)) return "S10/F1-DISCARD: authorized-discard wrap notebook not supplyable";

        // Sanity: a CLEAN notebook still wraps (F1 only guards the dirty case — the happy path above stays green).
        var nb6 = new MarimoNotebookDocument(failSynth);
        if (!nb6.Open(rawPath2)) return "S10/F1: clean notebook should still wrap-Open a non-marimo .py";
        if (nb6.CellCount != 1) return "S10/F1: clean wrap should produce 1 cell";
        // F3: clean wrap-Open also lights WrapMode (same invariant as nb3, locked separately so a
        // future split of the wrap path under F1 dirty gating cannot regress the clean leg silently).
        if (!nb6.WrapMode) return "S10/F3: clean wrap-Open did not set WrapMode";
        // F3: SaveAs converts the on-disk to marimo form (§D2) so WrapMode MUST clear — otherwise
        // the toast would keep warning about a conversion that already happened.
        string saveAsPath = Path.Combine(TempDir, "nb_wrap_saveas.py");
        if (!nb6.SaveAs(saveAsPath)) return "S10/F3: SaveAs after wrap-Open failed";
        if (nb6.WrapMode) return "S10/F3: SaveAs did not clear WrapMode (stale wrap warning after marimo conversion)";

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
    //     (add region_002 / dormant reuse / delete despawn・hide-dormant / ≥1 guard /
    //      CapturePositions cell-order / Open orphan 一掃)
    //     (bare-RT) windows: cell0->region_001, AddCell->region_002 spawn, delete routing
    //     (despawn region_002 / hide-dormant region_001), >=1 guard, dormant reuse, positions.
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

        // >=1 guard: deleting the last cell (region_001) is refused, shell preserved.
        if (coord.DeleteCell(R1)) return "S12: deleted the last cell (>=1 guard breached)";
        if (!controller.Has(R1)) return "S12: region_001 destroyed by a refused delete";

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
                var v = StrategyEditorContentBuilder.Build(body, font: font);
                if (v != null) views[id] = v;
                return rootRt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        // adopt the scene-authored region_001 shell — a real frame + view (so it owns an output pane).
        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody, font: font);
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
                var v = StrategyEditorContentBuilder.Build(body, font: font);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody, font: font);
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
                var v = StrategyEditorContentBuilder.Build(body, font: font);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody, font: font);
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
                var v = StrategyEditorContentBuilder.Build(body, font: font);
                if (v != null) views[id] = v;
                return rootRt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody, font: font);
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
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson)
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
                var v = StrategyEditorContentBuilder.Build(body, font: font);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody, font: font);
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
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson)
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
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer19", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body, font: font);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody, font: font);
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
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson)
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

        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson)
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
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson)
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
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson)
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

    static bool GlyphColor(VertexHelper vh, Dictionary<int, int> rankOf, int charIndex, Color expected)
    {
        if (!rankOf.TryGetValue(charIndex, out int r)) return false;
        var v = new UIVertex();
        vh.PopulateUIVertex(ref v, r * 4);
        return ColorApprox(v.color, expected);
    }

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

    // ====== Section 20 — #102: console + dynamic output layout (findings 0076) ======
    //
    // Covers: STRATEGY-34 console paints stdout/stderr segments in arrival order with stderr amber;
    //         STRATEGY-35 empty rich + empty console → both blocks deactivated, editor takes the full body;
    //         STRATEGY-36 rich populated + console empty → console block hidden;
    //         STRATEGY-37 rich populated + console populated → both blocks visible, capped at body * 0.45;
    //         STRATEGY-38 cell rebind clears both rich and console panes.
    //
    // Python-FREE (the segments are produced by a fake executor); the Python pytest gate
    // (test_notebook_console.py) covers the marimo-side capture.
    static string Section20_ConsoleAndDynamicLayout(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var views = new Dictionary<string, StrategyEditorView>();

        var layerGo = new GameObject("FWLayer20", typeof(RectTransform));
        spawned.Add(layerGo);
        var controller = new FloatingWindowController(
            layerGo.GetComponent<RectTransform>(), FloatingWindowCatalog.Default(),
            (spec, id) =>
            {
                var rt = StrategyEditorWindowFrame.Build(id, out _, out var body);
                spawned.Add(rt.gameObject);
                var v = StrategyEditorContentBuilder.Build(body, font: font);
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
        var view1 = StrategyEditorContentBuilder.Build(adoptBody, font: font);
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
        if (view.RichBlockVisible) return "S20/STRATEGY-35: rich block is initially visible (should be hidden)";
        if (view.ConsoleBlockVisible) return "S20/STRATEGY-35: console block is initially visible (should be hidden)";

        // STRATEGY-34: a single stdout segment paints the console; stderr stays absent.
        exec.SetOutput(string.Empty, string.Empty, string.Empty);
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "a\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.ConsoleBlockVisible) return "S20/STRATEGY-34: console block did not become visible after a stdout segment";
        var ctext = view.CurrentConsoleText ?? string.Empty;
        if (!ctext.Contains("a")) return "S20/STRATEGY-34: console text missing the stdout payload, got [" + ctext + "]";
        if (ctext.Contains("<color=")) return "S20/STRATEGY-34: stdout-only payload wrongly wrapped in a colour tag, got [" + ctext + "]";

        // STRATEGY-34 (stderr amber): a stderr segment paints amber via UGUI rich-text colour tags.
        exec.SetConsole(new[] {
            new ConsoleSegment { Stream = "stdout", Text = "o1\n" },
            new ConsoleSegment { Stream = "stderr", Text = "e1\n" },
            new ConsoleSegment { Stream = "stdout", Text = "o2\n" },
        });
        run.RunCell(R1); run.DrainAndRoute();
        ctext = view.CurrentConsoleText ?? string.Empty;
        if (!ctext.Contains("<color=")) return "S20/STRATEGY-34: a stderr segment did not produce a colour tag, got [" + ctext + "]";
        int oIdx = ctext.IndexOf("o1");
        int eIdx = ctext.IndexOf("e1");
        int o2Idx = ctext.IndexOf("o2");
        if (oIdx < 0 || eIdx < 0 || o2Idx < 0) return "S20/STRATEGY-34: arrival order not preserved (o1/e1/o2 missing), got [" + ctext + "]";
        if (!(oIdx < eIdx && eIdx < o2Idx)) return "S20/STRATEGY-34: arrival order broken (expected o1<e1<o2), got [" + ctext + "]";

        // STRATEGY-34 (UGUI rich-text escape regression): a stdout segment containing `<EOF>` MUST
        // survive — UGUI Text with supportRichText=true would otherwise treat `<EOF>` as an unknown
        // tag and strip it (`print("<EOF>")` would silently vanish). BuildConsoleRichText escapes `<`
        // to `&lt;` BEFORE concatenating with the color tag, so the user sees the literal characters.
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "<EOF>" } });
        run.RunCell(R1); run.DrainAndRoute();
        ctext = view.CurrentConsoleText ?? string.Empty;
        if (!ctext.Contains("&lt;EOF")) return "S20/STRATEGY-34: literal '<' from stdout was not escaped — UGUI would strip the tag, got [" + ctext + "]";

        // STRATEGY-37 (both populated): rich block + console block both visible, capped under body * 0.45.
        exec.SetOutput("hello", "text/plain", null);
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "console!\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.RichBlockVisible) return "S20/STRATEGY-37: rich block did not become visible after a text/plain payload";
        if (!view.ConsoleBlockVisible) return "S20/STRATEGY-37: console block did not become visible alongside rich output";
        // Per-block cap: each output block's preferredHeight must not exceed body.height * fraction.
        var richBlockRT = view1.transform.parent.Find("RichOutputBlock") as RectTransform;
        var consoleBlockRT = view1.transform.parent.Find("ConsoleOutputBlock") as RectTransform;
        if (richBlockRT == null || consoleBlockRT == null) return "S20/STRATEGY-37: rich/console block RectTransform not found under body";
        float bodyH = adoptBody.rect.height;
        float cap = bodyH * StrategyEditorContentBuilder.OutputBlockMaxFractionOfBody + 1f;   // 1px tolerance for rebuild rounding
        var richLE = richBlockRT.GetComponent<LayoutElement>();
        var conLE = consoleBlockRT.GetComponent<LayoutElement>();
        if (richLE.preferredHeight > cap) return "S20/STRATEGY-37: rich block preferredHeight " + richLE.preferredHeight + " exceeded cap " + cap;
        if (conLE.preferredHeight > cap) return "S20/STRATEGY-37: console block preferredHeight " + conLE.preferredHeight + " exceeded cap " + cap;

        // STRATEGY-36 (rich only): a press that emits rich but no console keeps the console hidden.
        exec.SetOutput("only", "text/plain", null);
        exec.SetConsole(System.Array.Empty<ConsoleSegment>());
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.RichBlockVisible) return "S20/STRATEGY-36: rich block hidden when a rich-only payload arrived";
        if (view.ConsoleBlockVisible) return "S20/STRATEGY-36: console block stayed visible after an empty console segment list";

        // STRATEGY-35 (back to empty): an empty rich + empty console returns to editor-only.
        exec.SetOutput(null, null, null);
        exec.SetConsole(System.Array.Empty<ConsoleSegment>());
        run.RunCell(R1); run.DrainAndRoute();
        if (view.RichBlockVisible) return "S20/STRATEGY-35: rich block stayed visible after an empty payload";
        if (view.ConsoleBlockVisible) return "S20/STRATEGY-35: console block stayed visible after an empty payload";

        // STRATEGY-38 (rebind clears both panes): paint something, then Bind a new cell — both panes clear.
        exec.SetOutput("painted", "text/plain", null);
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "x\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.RichBlockVisible || !view.ConsoleBlockVisible) return "S20/STRATEGY-38: precondition — paint did not populate both blocks";
        var freshCell = new Cell("y = 1");
        view.Bind(freshCell);
        if (view.RichBlockVisible) return "S20/STRATEGY-38: cell rebind did not clear the rich block";
        if (view.ConsoleBlockVisible) return "S20/STRATEGY-38: cell rebind did not clear the console block";

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
        public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson)
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

    // ====== Section 21 — #102 audit gaps (findings 0076 §6) ======
    //
    // Covers: STRATEGY-39 `&` literal must NOT be entity-escaped (UGUI does not decode, so
    //         Replace("&","&amp;") would paint `a & b` as `a &amp; b` — pure regression);
    //         STRATEGY-40 multi-cell routing: a press that produces output for the pressed cell
    //         AND a reactive descendant routes each console to ITS OWN region (no bleed);
    //         STRATEGY-41 re-press of the same cell REPLACES the prior console (does not append);
    //         STRATEGY-42 re-press of the same cell with EMPTY hides the console block;
    //         STRATEGY-43 overflow → real ScrollRect: Content > Viewport, verticalNormalizedPosition
    //         operable end-to-end (findings 0076 §6 D5 — supersedes RectMask2D-clip);
    //         STRATEGY-44 first-frame bodyH==0: paint must still produce a visible block with
    //         preferredHeight > 0 (no `ForceRebuildLayoutImmediate` priming);
    //         STRATEGY-45 `</color>` injection-resistance: a stderr segment containing `</color>`
    //         must be escaped to `&lt;/color>` so it cannot close our amber wrapper;
    //         STRATEGY-46 dormant-reuse race: a press → DeleteCell → AddCell that reuses dormant
    //         R1 must NOT paint the prior cell's stdout onto the rebound view (ListMutated bumps
    //         the run controller's generation, identical to how Open/New already drops stale).
    //
    // Python-FREE.  The Python pytest gate (test_notebook_console.py) already covers the marimo-side
    // capture; this section pins the C# routing, layout, escape, and race guards.
    static string Section21_ConsoleAuditGaps(List<GameObject> spawned)
    {
        const string R1 = NotebookCellCoordinator.AdoptedRegionId;
        const string R2 = "strategy_editor:region_002";
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
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
                var v = StrategyEditorContentBuilder.Build(body, font: font);
                if (v != null) views[id] = v;
                return rt;
            },
            go => UnityEngine.Object.DestroyImmediate(go));

        // STRATEGY-44 leans on the adopted window being at default zero size before any
        // ForceRebuildLayoutImmediate — build the view INSIDE that bodyH==0 frame.  Subsequent tests
        // resize the root to 400 for stable layout.
        var adoptRoot = StrategyEditorWindowFrame.Build(R1, out _, out var adoptBody);
        spawned.Add(adoptRoot.gameObject);
        var view1 = StrategyEditorContentBuilder.Build(adoptBody, font: font);
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
        // #102 findings 0076 §6 D7: production wiring — coord mutations drop in-flight runs.
        coord.ListMutated += () => run.Invalidate();
        var view = views[R1];

        // ---- STRATEGY-44: bodyH==0 first-frame race (no ForceRebuildLayoutImmediate priming) ----
        // The adopted window's RectTransform has not yet resolved (no parent canvas + no force-rebuild),
        // so adoptBody.rect.height == 0 on this very first paint.  ApplyBlockSize must take the
        // no-cap branch (natural drives) and still leave a visible block with preferredHeight > 0.
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "first-frame\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.ConsoleBlockVisible) return "S21/STRATEGY-44: console block not visible on first-frame paint (bodyH==0)";
        var conBlockRT = view.transform.parent.Find("ConsoleOutputBlock") as RectTransform;
        if (conBlockRT == null) return "S21/STRATEGY-44: console block RT not found under body";
        var conLE0 = conBlockRT.GetComponent<LayoutElement>();
        if (conLE0 == null || !(conLE0.preferredHeight > 0f))
            return "S21/STRATEGY-44: preferredHeight stayed 0 on first-frame paint, got " + (conLE0 != null ? conLE0.preferredHeight : 0f);

        // Stabilise the body for the remaining assertions.
        adoptRoot.sizeDelta = new Vector2(400f, 400f);
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(adoptRoot);

        // ---- STRATEGY-39: `&` literal must NOT be entity-escaped ----
        exec.SetOutput(null, null, null);
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "a & b\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        var ctext = view.CurrentConsoleText ?? string.Empty;
        if (!ctext.Contains("a & b"))
            return "S21/STRATEGY-39: literal '&' missing — payload not preserved verbatim, got [" + ctext + "]";
        if (ctext.Contains("&amp;"))
            return "S21/STRATEGY-39: '&' was double-escaped to '&amp;' — UGUI would paint the entity literally, got [" + ctext + "]";

        // ---- STRATEGY-45: `</color>` in stderr must not close our amber wrapper ----
        exec.SetConsole(new[] {
            new ConsoleSegment { Stream = "stderr", Text = "start" },
            new ConsoleSegment { Stream = "stderr", Text = "</color>middle" },
            new ConsoleSegment { Stream = "stdout", Text = "end" },
        });
        run.RunCell(R1); run.DrainAndRoute();
        ctext = view.CurrentConsoleText ?? string.Empty;
        // BuildConsoleRichText wraps EACH stderr segment in its own <color>...</color> pair, so the
        // safety property is "wrapper balance" — every <color=#ffa01c> open has its own </color>
        // close, and the user's `</color>` cannot leak past escape.  An unescaped `</color>` would
        // close our most recent open prematurely, breaking the balance (more closes than opens).
        if (!ctext.Contains("&lt;/color>"))
            return "S21/STRATEGY-45: user's '</color>' was not escaped — UGUI would close our amber tag, got [" + ctext + "]";
        if (ctext.Contains("</color>middle"))
            return "S21/STRATEGY-45: user's '</color>middle' rendered literally — escape did not run before paint, got [" + ctext + "]";
        int opens = CountSubstring(ctext, "<color=#ffa01c>");
        int closes = CountSubstring(ctext, "</color>");
        if (opens != closes)
            return "S21/STRATEGY-45: amber wrapper unbalanced (opens=" + opens + " closes=" + closes + ") — user's </color> may have leaked, got [" + ctext + "]";
        if (opens != 2)
            return "S21/STRATEGY-45: expected 2 amber wrapper pairs (one per stderr segment), got opens=" + opens;

        // ---- STRATEGY-41: re-press REPLACES (does not append) ----
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "round1\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "round2\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        ctext = view.CurrentConsoleText ?? string.Empty;
        if (ctext.Contains("round1"))
            return "S21/STRATEGY-41: re-press did not REPLACE — prior 'round1' leaked, got [" + ctext + "]";
        if (!ctext.Contains("round2"))
            return "S21/STRATEGY-41: re-press did not paint 'round2', got [" + ctext + "]";

        // ---- STRATEGY-42: re-press with EMPTY hides the console block ----
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "transient\n" } });
        run.RunCell(R1); run.DrainAndRoute();
        if (!view.ConsoleBlockVisible) return "S21/STRATEGY-42: precondition — console did not become visible";
        exec.SetConsole(Array.Empty<ConsoleSegment>());
        run.RunCell(R1); run.DrainAndRoute();
        if (view.ConsoleBlockVisible) return "S21/STRATEGY-42: re-press with empty segments did not hide the console block";

        // ---- STRATEGY-43: overflow → real ScrollRect (findings 0076 §6 D5) ----
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append("line ").Append(i).Append('\n');
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = sb.ToString() } });
        run.RunCell(R1); run.DrainAndRoute();
        var scroll = view.ConsoleScrollRect;
        if (scroll == null) return "S21/STRATEGY-43: ConsoleScrollRect not wired";
        if (scroll.content == null) return "S21/STRATEGY-43: ScrollRect.content not wired";
        if (scroll.viewport == null) return "S21/STRATEGY-43: ScrollRect.viewport not wired";
        if (scroll.verticalScrollbar == null) return "S21/STRATEGY-43: ScrollRect.verticalScrollbar not wired";
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(adoptRoot);
        float contentH = scroll.content.rect.height;
        float viewportH = scroll.viewport.rect.height;
        if (!(contentH > viewportH + 1f))
            return "S21/STRATEGY-43: 40-line payload did not overflow viewport (content=" + contentH + " viewport=" + viewportH + ")";
        // Setting verticalNormalizedPosition end-to-end (0=bottom, 1=top) must stick — proof the
        // ScrollRect is genuinely controlling content position, not a no-op clip.
        scroll.verticalNormalizedPosition = 0f;
        if (Mathf.Abs(scroll.verticalNormalizedPosition - 0f) > 0.01f)
            return "S21/STRATEGY-43: setting verticalNormalizedPosition=0 did not stick (got " + scroll.verticalNormalizedPosition + ")";
        scroll.verticalNormalizedPosition = 1f;
        if (Mathf.Abs(scroll.verticalNormalizedPosition - 1f) > 0.01f)
            return "S21/STRATEGY-43: setting verticalNormalizedPosition=1 did not stick (got " + scroll.verticalNormalizedPosition + ")";

        // ---- STRATEGY-40: multi-cell routing (pressed R1 + descendant R2) ----
        // AddCell so coord.RegionOf(cells[1]) resolves to R2 (region_002 spawn).
        var cellB = coord.AddCell();
        if (coord.RegionOf(cellB) != R2)
            return "S21/STRATEGY-40: precondition — second cell not bound to region_002, got " + coord.RegionOf(cellB);
        StrategyEditorView view2;
        if (!views.TryGetValue(R2, out view2) || view2 == null)
            return "S21/STRATEGY-40: precondition — region_002 view not built by FW factory";
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
            return "S21/STRATEGY-40: R1 (pressed) console missing 'from-cell-0', got [" + t1 + "]";
        if (t1.Contains("from-cell-1"))
            return "S21/STRATEGY-40: descendant text leaked into pressed cell R1, got [" + t1 + "]";
        if (!t2.Contains("from-cell-1"))
            return "S21/STRATEGY-40: R2 (descendant) console missing 'from-cell-1', got [" + t2 + "]";
        if (t2.Contains("from-cell-0"))
            return "S21/STRATEGY-40: pressed text leaked into descendant cell R2, got [" + t2 + "]";

        // Reset multi-mode (empty params → _multi=null) and prep both views for STRATEGY-46.
        exec.SetMulti();
        exec.SetConsole(Array.Empty<ConsoleSegment>());
        run.RunCell(R1); run.DrainAndRoute();
        run.RunCell(R2); run.DrainAndRoute();

        // ---- STRATEGY-46: dormant-reuse race (findings 0076 §6 D7) ----
        // State here: notebook = [cellA, cellB], R1 = cellA, R2 = cellB (AddCell(cellB) above).
        // Goal: a press queued AGAINST cellA must NOT paint onto a cellC that later reuses dormant
        // R1.  The synchronous lane queues the press result inside RunCell with generation N; the
        // subsequent DeleteCell(R1) → AddCell() reuses dormant R1 and binds it to a fresh cellC.
        // ListMutated bumps the controller's generation each mutation, so the queued result is
        // dropped at drain — otherwise cellA's stdout would paint onto cellC's view.
        exec.SetConsole(new[] { new ConsoleSegment { Stream = "stdout", Text = "stale-from-A\n" } });
        run.RunCell(R1);   // synchronous lane queues result with current generation N
        if (!coord.DeleteCell(R1)) return "S21/STRATEGY-46: precondition — DeleteCell(R1) failed";
        // After DeleteCell, R1 is hidden+dormant.  AddCell reuses the dormant R1 (the adopted shell
        // is never-Destroy, findings 0050) and binds a fresh cellC to it — same GameObject + same
        // StrategyEditorView, different Cell.
        var cellC = coord.AddCell();
        if (coord.RegionOf(cellC) != R1)
            return "S21/STRATEGY-46: precondition — AddCell did not reuse dormant R1, got " + coord.RegionOf(cellC);
        if (!ReferenceEquals(views[R1], view))
            return "S21/STRATEGY-46: precondition — R1 view recreated (should be same adopted shell)";
        if (view.BoundCell != cellC)
            return "S21/STRATEGY-46: precondition — R1 view not rebound to cellC";
        // NOW drain.  The press's result frame predates DeleteCell+AddCell — generation bump must drop it.
        run.DrainAndRoute();
        if (view.ConsoleBlockVisible)
            return "S21/STRATEGY-46: stale drain painted onto cellC's rebound view — generation guard missing";
        if ((view.CurrentConsoleText ?? string.Empty).Contains("stale-from-A"))
            return "S21/STRATEGY-46: stale stdout 'stale-from-A' leaked into cellC's view";

        lane.Dispose();
        return null;
    }
}
