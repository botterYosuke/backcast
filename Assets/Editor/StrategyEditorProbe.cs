// StrategyEditorProbe.cs — issue #16 "Strategy Editor" (THROWAWAY AFK regression gate)
//
// The headless, Python-FREE regression gate for the Strategy Editor seam. Run:
//
//   <Unity> -batchmode -projectPath /Users/sasac/backcast \
//           -executeMethod StrategyEditorProbe.Run -logFile <log>
//   # expect: [STRATEGY EDITOR PASS] ... / exit=0
//
// AUTHORITATIVE for the lexical highlighter, the undo/redo history, the file model, the provider
// contract + registry, the layout round-trip, the restore semantics, and the (non-scroll) mesh
// colouring (findings 0010 §9). The editing FEEL — InputField sync, undo/redo keys, IME, and
// visible-range SCROLL colouring — is the owner-launched HITL harness (Tools > Backcast >
// Strategy Editor HITL). Like #12–#15 this spawns no auto-bootstrap.
//
// EIGHT SECTIONS (findings 0010 §9); #12–#15 regression is run by executing those probes
// individually (recorded in findings §11), not from inside this one.
//   1. PythonHighlighter lexical tokens + ascending/non-overlap/in-range invariant
//   2. EditHistory boundary coalescing + cap-200
//   3. StrategyDocument file model (Open/Save/atomic-replace-preserves)
//   4. IStrategyFileProvider 5-condition contract
//   5. StrategyProviderRegistry lookup + deterministic ordinal enumeration
//   6. layout round-trip (REAL JsonUtility) + sanitize + back-compat + Clone/StructurallyEqual
//   7. restore full-replacement on REAL windows/controller (Open-failure keeps the window)
//   8. non-scroll mesh colouring (real Text component + synthetic base mesh; real TextGenerator
//      glyph count cross-check when available, else HITL fallback per findings §9)

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class StrategyEditorProbe
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
                ?? Section12_Coordinator(spawned);
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
            Debug.Log("[STRATEGY EDITOR PASS] lexical highlighter (keyword/string/comment/number/decorator/definition; " +
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
                      "round-trip preserves body+name+config, >=1 delete guard, Open fail-soft non-destructive, supplyable " +
                      "5-condition, ResetUnboundEmpty) + SpawnPlacement (anchor-start, diagonal cascade, overlap-allowed, " +
                      "<10 threshold, full-chain clear) + NotebookCellCoordinator (cell0->region_001, AddCell->region_002 spawn, " +
                      "DeleteCell despawn region_002 / hide-dormant region_001, >=1 guard, dormant reuse, CapturePositions cell-order) " +
                      "— Unity-owned, ADR-0003/0013 capability parity, under Unity Mono");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[STRATEGY EDITOR FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ======================================================================
    // 1. PythonHighlighter
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
            Debug.LogWarning("[STRATEGY EDITOR] S3: could not force a save failure on this platform " +
                             "(read-only dir still writable, e.g. running as root) -> replace-preservation HITL-only.");
        return null;
    }

    // ======================================================================
    // 4. IStrategyFileProvider 5-condition contract
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
                Debug.LogWarning($"[STRATEGY EDITOR] S8: real TextGenerator visible glyphs {visible} != synthetic {rank} " +
                                 "-> whitespace-skipping premise needs the HITL check.");
            else if (visible == 0)
                Debug.LogWarning("[STRATEGY EDITOR] S8: TextGenerator produced no glyphs in batchmode -> glyph-count cross-check HITL-only.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[STRATEGY EDITOR] S8: TextGenerator unavailable in batchmode (" + e.Message + ") -> glyph-count cross-check HITL-only.");
        }
        return null;
    }

    // ======================================================================
    // 10. #81 MarimoNotebookDocument aggregate (ADR-0013 / findings 0050) — driven by the
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

        // Open fail-soft (broken / non-marimo .py): Open false, buffer NON-destructive, LastError set.
        var failSynth = new FakeMarimoSynthesizer { FailDecompose = true };
        var nb3 = new MarimoNotebookDocument(failSynth);
        nb3.Cells[0].SetBody("keep me");
        int before = nb3.CellCount;
        if (nb3.Open(py)) return "S10: Open of a 'broken' .py should fail (fail-soft)";
        if (nb3.CellCount != before || nb3.Cells[0].Body != "keep me") return "S10: fail-soft Open mutated the buffer";
        if (nb3.LastError == null) return "S10: fail-soft Open did not set LastError";

        // ResetUnboundEmpty = one empty cell, unbound (File→New).
        nb2.ResetUnboundEmpty();
        if (nb2.CellCount != 1 || nb2.Cells[0].Body != "" || nb2.IsBound) return "S10: ResetUnboundEmpty not 1 empty unbound cell";
        return null;
    }

    // ======================================================================
    // 11. #81 SpawnPlacement.Next — pure cascade (marimo calcSpawnPosition parity).
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
}
