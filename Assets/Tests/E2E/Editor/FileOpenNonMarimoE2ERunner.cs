// FileOpenNonMarimoE2ERunner.cs — issue #113 release-gate（台本: 同ディレクトリの
// FileOpenNonMarimoE2ERunner.md / 設計正本: docs/findings/0098-issue113-open-layer-marimo-only.md）
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod FileOpenNonMarimoE2ERunner.Run -logFile <log>
//   # expect: [E2E FILE OPEN NONMARIMO PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// #113（findings 0054 §D1 の自動 wrap を反転）: editor は marimo notebook 専用。File→Open は「marimo or error」で、
// 非 marimo `.py` を 1-cell に自動 wrap せず **明示エラー**にする。本 runner は #86 の wrap release-gate を
// 反転し、(1) 実 on-disk の非 marimo v19_morning.py が拒否される、(2) broken-syntax が SyntaxError 由来の
// 別エラーになる、(3) 正常な marimo notebook は無回帰で開けて Run-gate を通す、(4) path/IO は従来どおり
// fail-soft、を gate する。run/materialize 契約（#112 ADR-0025 D4 NOT_A_MARIMO_NOTEBOOK）と open〜run で一貫。
//
// THE NON-VACUOUS KILL（OPEN-NM-01）: 合成 fixture ではなく **実 on-disk** の
// `python/strategies/v19/v19_morning.py`（imperative `class V19MorningStrategy(Strategy):` を持つ非 marimo `.py`）
// を集約に開かせ、**Open が false** で **buffer 非破壊**であることを assert する。
// delete-the-logic litmus: MarimoNotebookDocument.Open の null 分岐に `?? new List<Cell> { new Cell(content,…) }`
// の wrap leg を戻すと Open() が true になり、本 section が確定的に落ちる（= wrap を退役させた事実を固定）。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class FileOpenNonMarimoE2ERunner
{
    const string NOTEBOOK_ID = "strategy_editor:notebook";   // BackcastWorkspaceRoot.cs:47 と同一文字列

    static string TempDir => Path.Combine(Application.temporaryCachePath, "file_open_nonmarimo_e2e");
    static string RepoRoot => Directory.GetParent(Application.dataPath).FullName;
    static string V19MorningPath => Path.Combine(RepoRoot, "python", "strategies", "v19", "v19_morning.py");

    public static void Run()
    {
        string fail = null;
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
            Directory.CreateDirectory(TempDir);

            fail = Section1_RealV19MorningRejected()
                ?? Section2_BrokenSyntaxIsDistinctError()
                ?? Section3_ValidMarimoOpensAndRunGate()
                ?? Section4_FailSoftOnlyOnPathIO();
        }
        catch (Exception e)
        {
            fail = "exception: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[E2E FILE OPEN NONMARIMO PASS] real v19_morning.py rejected (not a marimo notebook) + broken-syntax distinct error + valid marimo opens & run-gate + path/IO fail-soft preserved");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E FILE OPEN NONMARIMO FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. open the REAL on-disk python/strategies/v19/v19_morning.py and assert it is REJECTED.
    // Covers: OPEN-NM-01. The non-vacuous kill: we read the real file independently to prove it IS a
    // non-marimo imperative strategy (`class V19MorningStrategy`), then assert the aggregate refuses it
    // (marimo-or-error) WITHOUT touching the buffer. The fake's FailDecompose=true models production
    // PythonnetMarimoSynthesizer.Decompose for a non-marimo .py (decompose_json -> None).
    static string Section1_RealV19MorningRejected()
    {
        if (!File.Exists(V19MorningPath))
            return "v19_morning.py not found at " + V19MorningPath + " (repo invariant)";

        string rawOnDisk = File.ReadAllText(V19MorningPath, System.Text.Encoding.UTF8);
        if (string.IsNullOrEmpty(rawOnDisk))
            return "v19_morning.py is empty (repo invariant: real imperative strategy)";
        if (!rawOnDisk.Contains("class V19MorningStrategy"))
            return "v19_morning.py no longer carries 'class V19MorningStrategy' (oracle drifted — this gate assumes a non-marimo imperative strategy)";

        var synth = new FakeMarimoSynthesizer { FailDecompose = true };
        var nb = new MarimoNotebookDocument(synth);
        string seedBody = nb.Cells[0].Body;   // the fresh untitled doc's one empty cell

        // #113: Open of a non-marimo .py must FAIL — no 1-cell wrap. delete-the-logic litmus is live here.
        if (nb.Open(V19MorningPath))
            return "Open returned TRUE on real non-marimo v19_morning.py (the retired 1-cell wrap leg is back — #113 broken)";
        if (nb.LastError != "not a marimo notebook")
            return "non-marimo Open LastError='" + (nb.LastError ?? "<null>") + "' (expected 'not a marimo notebook')";
        if (nb.IsBound) return "rejected Open bound the document to v19_morning.py (must stay unbound)";
        if (nb.CurrentPath != null) return "rejected Open set CurrentPath (must stay null)";
        if (nb.CellCount != 1 || nb.Cells[0].Body != seedBody)
            return "rejected Open mutated the buffer (must leave the untitled doc untouched)";

        Debug.Log("[E2E OPEN-NM-01 PASS] real non-marimo v19_morning.py rejected (not a marimo notebook), buffer untouched");
        return null;
    }

    // ---- 2. a BROKEN-SYNTAX source surfaces as a DISTINCT 'syntax error: ...' (never masked as a silent
    // wrap or conflated with not-a-marimo). Covers: OPEN-NM-02 (#113 AC#2). The fake's SyntaxErrorDetail
    // models decompose_json letting load_app's SyntaxError propagate (cell_synthesis raise_syntax_error=True).
    static string Section2_BrokenSyntaxIsDistinctError()
    {
        var synth = new FakeMarimoSynthesizer { SyntaxErrorDetail = "invalid syntax (broken.py, line 1)" };
        var nb = new MarimoNotebookDocument(synth);
        string seedBody = nb.Cells[0].Body;

        string brokenPath = Path.Combine(TempDir, "broken_marimo.py");
        File.WriteAllText(brokenPath, "import marimo\napp = marimo.App(\n");   // unbalanced paren
        if (nb.Open(brokenPath))
            return "Open returned TRUE on a broken-syntax .py (must fail)";
        if (nb.LastError == null || !nb.LastError.StartsWith("syntax error", StringComparison.Ordinal))
            return "broken-syntax Open LastError='" + (nb.LastError ?? "<null>") + "' should start with 'syntax error' (distinct from 'not a marimo notebook' — AC#2)";
        if (nb.LastError == "not a marimo notebook")
            return "broken-syntax was conflated with not-a-marimo (AC#2 wants a distinct SyntaxError-derived error)";
        if (nb.IsBound) return "rejected broken-syntax Open bound the document (must stay unbound)";
        if (nb.CellCount != 1 || nb.Cells[0].Body != seedBody)
            return "rejected broken-syntax Open mutated the buffer";

        Debug.Log("[E2E OPEN-NM-02 PASS] broken-syntax surfaces as a distinct syntax error (not conflated with not-a-marimo)");
        return null;
    }

    // ---- 3. a VALID marimo notebook still Opens with no regression (AC#4) and unblocks the Run-gate via
    // the SAME RegistryStrategyFileProvider production uses. Covers: OPEN-NM-03. The path round-trips
    // through the fake's marker so Decompose succeeds the regular way (not a wrap, not a fail).
    static string Section3_ValidMarimoOpensAndRunGate()
    {
        var synth = new FakeMarimoSynthesizer();   // FailDecompose=false: valid marimo decode path
        // Build a real (fake-)marimo file on disk: synthesise 2 cells, then write the blob.
        string marimoPy = Path.Combine(TempDir, "valid_marimo.py");
        string blob = synth.Synthesize(new List<Cell> { new Cell("a = 1", "_config", "{}"), new Cell("b = a + 1", "_strat", "{}") });
        File.WriteAllText(marimoPy, blob);

        var nb = new MarimoNotebookDocument(synth);
        if (!nb.Open(marimoPy))
            return "valid marimo Open failed (LastError=" + (nb.LastError ?? "<null>") + ") — #113 must not regress valid notebooks";
        if (nb.LastError != null) return "valid marimo Open set LastError='" + nb.LastError + "' (success must clear it)";
        if (nb.CellCount != 2) return "valid marimo Open produced " + nb.CellCount + " cells, expected 2";
        if (nb.Cells[0].Name != "_config" || nb.Cells[1].Name != "_strat")
            return "valid marimo Open dropped cell names (seam carries body+name+config)";
        if (!nb.IsBound || nb.IsDirty) return "valid marimo Open should bind + be clean";

        // Run-gate: production Run/Step/LiveAuto pulls the path through RegistryStrategyFileProvider under
        // NOTEBOOK_ID (BackcastWorkspaceRoot.cs:47/:272/:409). The opened notebook must supply its path.
        var registry = new StrategyProviderRegistry();
        registry.Register(NOTEBOOK_ID, nb);
        var provider = new RegistryStrategyFileProvider(registry, NOTEBOOK_ID);
        if (!provider.TryGetStrategyFile(out string runPath))
            return "run-gate: registry-resolved provider returned false right after a valid Open (Run blocked)";
        if (!Path.IsPathRooted(runPath) || !runPath.EndsWith("valid_marimo.py", StringComparison.Ordinal))
            return "run-gate: provider returned wrong path '" + runPath + "'";

        // a body edit dirties → not supplyable; Save re-clears → supplyable (5-condition contract intact).
        nb.Cells[0].SetBody("a = 2");
        if (!nb.IsDirty || provider.TryGetStrategyFile(out _))
            return "run-gate: dirty notebook still supplies a path (5-condition broken)";
        if (!nb.Save()) return "run-gate: Save failed";
        if (!provider.TryGetStrategyFile(out _)) return "run-gate: provider false after Save (Run re-blocked)";

        Debug.Log("[E2E OPEN-NM-03 PASS] valid marimo notebook opens with no regression + run-gate supplyable");
        return null;
    }

    // ---- 4. path/IO failure modes are STILL fail-soft (false + the specific LastError, buffer unchanged).
    // Covers: OPEN-NM-04. Guards against the rejection logic widening to swallow / mislabel IO errors —
    // an empty path / wrong extension / missing file must keep their OWN reasons, distinct from the
    // marimo-or-error reasons. The seed is a VALID marimo open (no wrap exists anymore).
    static string Section4_FailSoftOnlyOnPathIO()
    {
        var synth = new FakeMarimoSynthesizer();
        string marimoPy = Path.Combine(TempDir, "failsoft_seed.py");
        File.WriteAllText(marimoPy, synth.Synthesize(new List<Cell> { new Cell("seed = 1", "_", "{}") }));

        var nb = new MarimoNotebookDocument(synth);
        if (!nb.Open(marimoPy)) return "fail-soft setup: initial valid marimo Open failed";
        int seedCount = nb.CellCount;
        string seedBody = nb.Cells[0].Body;
        string seedPath = nb.CurrentPath;

        // empty path -> "no path".
        if (nb.Open("")) return "fail-soft: empty path Open should fail";
        if (nb.LastError != "no path") return "fail-soft: empty path LastError='" + nb.LastError + "' (expected 'no path')";
        if (nb.CellCount != seedCount || nb.Cells[0].Body != seedBody || nb.CurrentPath != seedPath)
            return "fail-soft: empty path Open mutated the buffer";

        // wrong extension -> "not a .py" (NOT "not a marimo notebook").
        string txtPath = Path.Combine(TempDir, "not_a_python_file.txt");
        File.WriteAllText(txtPath, "irrelevant");
        if (nb.Open(txtPath)) return "fail-soft: .txt Open should fail";
        if (nb.LastError != "not a .py") return "fail-soft: .txt LastError='" + nb.LastError + "' (expected 'not a .py')";
        if (nb.CellCount != seedCount || nb.Cells[0].Body != seedBody || nb.CurrentPath != seedPath)
            return "fail-soft: .txt Open mutated the buffer";

        // missing file -> "file missing".
        string missingPath = Path.Combine(TempDir, "does_not_exist.py");
        if (nb.Open(missingPath)) return "fail-soft: missing .py Open should fail";
        if (nb.LastError != "file missing") return "fail-soft: missing LastError='" + nb.LastError + "' (expected 'file missing')";
        if (nb.CellCount != seedCount || nb.Cells[0].Body != seedBody || nb.CurrentPath != seedPath)
            return "fail-soft: missing .py Open mutated the buffer";

        Debug.Log("[E2E OPEN-NM-04 PASS] path/IO failures stay fail-soft with their own reasons (no path / not a .py / file missing)");
        return null;
    }
}
