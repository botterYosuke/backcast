// FileOpenNonMarimoE2ERunner.cs — issue #86 release-gate（台本: 同ディレクトリの
// FileOpenNonMarimoE2ERunner.md / 設計正本: docs/findings/0054-file-open-non-marimo-py-wraps-as-1-cell.md）
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod FileOpenNonMarimoE2ERunner.Run -logFile <log>
//   # expect: [E2E FILE OPEN NONMARIMO PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// THE NON-VACUOUS KILL（OPEN-NM-01）: 合成 fixture ではなく **実 on-disk** の
// `python/strategies/v19/v19_morning.py`（imperative `class V19MorningStrategy(Strategy):` を持つ
// 非 marimo `.py`）を集約に開かせ、wrap された body を **テスト側で独立に読んだ生本文と byte-for-byte 等値**で
// assert する。MarimoNotebookDocument.Open:149-150 の `?? new List<Cell> { NewCell(content, "_", "{}") }` を
// 消すと Open() が false になり、本 section が「Open returned false」で確定的に落ちる
// （= production の wrap policy が無いと release-gate が割れる、を delete-the-logic で固定）。
//
// 「Run-gate 解禁」（OPEN-NM-02）の意味: production の Run/Step/LiveAuto は BackcastWorkspaceRoot.cs:272 の
// `RegistryStrategyFileProvider(_registry, NOTEBOOK_ID)` から path を引いて start_engine に渡す。本 runner は
// **同じ NOTEBOOK_ID 文字列**で `StrategyProviderRegistry` を構築し、wrap 直後の集約が registry-resolved
// provider 経由で v19_morning.py の絶対パスを返すことを assert する（≒ ▶ ボタンが押せる状態）。
// 実 pythonnet 実 Run は HITL 領域（layer-3 pytest golden が seam を担う）。

using System;
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

            fail = Section1_OpenRealV19MorningWrapsAs1Cell()
                ?? Section2_RunGateOpensAfterWrap()
                ?? Section3_SaveAsRoundTripIsLossless()
                ?? Section4_FailSoftOnlyOnPathIO();
        }
        catch (Exception e)
        {
            fail = "exception: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[E2E FILE OPEN NONMARIMO PASS] real v19_morning.py wrapped as 1 cell + run-gate open + lossless SaveAs + path/IO fail-soft preserved");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E FILE OPEN NONMARIMO FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. open the REAL on-disk python/strategies/v19/v19_morning.py and assert the 1-cell wrap.
    // Covers: OPEN-NM-01. THE non-vacuous kill is comparing the wrap body byte-for-byte against the
    // file text read independently from disk — a writer that drops content or seeds an empty body
    // would pass a naive "Open succeeded + CellCount == 1" check but FAILS here.
    static string Section1_OpenRealV19MorningWrapsAs1Cell()
    {
        if (!File.Exists(V19MorningPath))
            return "v19_morning.py not found at " + V19MorningPath + " (repo invariant)";

        string rawOnDisk = File.ReadAllText(V19MorningPath, System.Text.Encoding.UTF8);
        if (string.IsNullOrEmpty(rawOnDisk))
            return "v19_morning.py is empty (repo invariant: real imperative strategy)";

        // Models production PythonnetMarimoSynthesizer.Decompose for a non-marimo .py (findings 0054 D5):
        // load_app returns None -> C# receives null -> aggregate WRAPs (instead of fail-soft abort).
        var synth = new FakeMarimoSynthesizer { FailDecompose = true };
        var nb = new MarimoNotebookDocument(synth);

        if (!nb.Open(V19MorningPath))
            return "Open returned false on real v19_morning.py (LastError=" + (nb.LastError ?? "<null>") +
                   "); the #86 1-cell wrap is NOT engaging — delete-the-logic litmus is live";

        if (nb.CellCount != 1) return "wrap produced " + nb.CellCount + " cells, expected exactly 1";

        // BYTE-FOR-BYTE equality with the on-disk file text (the non-vacuous kill).
        if (nb.Cells[0].Body != rawOnDisk)
            return "wrap body != raw v19_morning.py text (writer is lossy or seeded an empty body)";

        // The body MUST contain the imperative-strategy signature — proves we wrapped the REAL file,
        // not a synthesized blank, and that the file on disk is still imperative (the case #86 fixes).
        if (!nb.Cells[0].Body.Contains("class V19MorningStrategy"))
            return "wrap body missing 'class V19MorningStrategy' (signature non-vacuous kill)";

        if (nb.Cells[0].Name != "_") return "wrap cell name '" + nb.Cells[0].Name + "' != '_' (anonymous, findings 0054 D1)";
        if (!nb.IsBound) return "wrap notebook not bound to v19_morning.py";
        if (nb.IsDirty) return "wrap notebook is dirty immediately after Open";
        if (nb.LastError != null) return "wrap Open set LastError='" + nb.LastError + "' (the wrap is success, not fail-soft)";
        if (nb.CurrentPath == null || !nb.CurrentPath.EndsWith("v19_morning.py", StringComparison.Ordinal))
            return "wrap notebook path '" + nb.CurrentPath + "' is not v19_morning.py";

        return null;
    }

    // ---- 2. Run-gate opens via RegistryStrategyFileProvider exactly as production does, and flips
    // false when the wrap cell is edited (dirty) / true again after SaveAs (clean).
    // Covers: OPEN-NM-02. Uses the SAME NOTEBOOK_ID constant production wires under
    // (BackcastWorkspaceRoot.cs:47 / :272 / :409). If production renames or skips the Register call,
    // the registry-resolved provider returns false here.
    static string Section2_RunGateOpensAfterWrap()
    {
        var synth = new FakeMarimoSynthesizer { FailDecompose = true };
        var nb = new MarimoNotebookDocument(synth);
        if (!nb.Open(V19MorningPath))
            return "run-gate setup: Open of v19_morning.py failed (LastError=" + (nb.LastError ?? "<null>") + ")";

        var registry = new StrategyProviderRegistry();
        registry.Register(NOTEBOOK_ID, nb);
        var provider = new RegistryStrategyFileProvider(registry, NOTEBOOK_ID);

        if (!provider.TryGetStrategyFile(out string runPath))
            return "run-gate: registry-resolved provider returned false right after wrap (Run blocked)";
        if (string.IsNullOrEmpty(runPath) || !runPath.EndsWith("v19_morning.py", StringComparison.Ordinal))
            return "run-gate: provider returned wrong path '" + runPath + "' (expected v19_morning.py absolute path)";
        if (!Path.IsPathRooted(runPath)) return "run-gate: provider path is not absolute (5-condition contract)";

        // Editing the wrap cell flips supplyable → false (dirty source = body edit, findings 0010 §5).
        nb.Cells[0].SetBody(nb.Cells[0].Body + "# edited\n");
        if (!nb.IsDirty) return "run-gate: wrap body edit did not dirty the notebook (BindBodyChanged lost)";
        if (provider.TryGetStrategyFile(out _))
            return "run-gate: provider still supplies path while notebook is dirty (5-condition broken)";

        // SaveAs to a NEW temp path (NEVER overwrite the real v19_morning.py) re-clears dirty + rebinds.
        string savedPath = Path.Combine(TempDir, "v19_morning_saved.py");
        if (!nb.SaveAs(savedPath)) return "run-gate: SaveAs to temp failed";
        if (nb.IsDirty) return "run-gate: SaveAs did not clear dirty";
        if (!provider.TryGetStrategyFile(out string reboundPath))
            return "run-gate: provider returned false after SaveAs (Run re-blocked)";
        if (!string.Equals(Path.GetFullPath(reboundPath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase))
            return "run-gate: provider path '" + reboundPath + "' != saved-as path '" + savedPath + "'";

        return null;
    }

    // ---- 3. SaveAs from a wrapped notebook produces a marimo-form file that, when re-Opened by a
    // fresh aggregate with a NORMAL (non-failing) synthesizer, reproduces the original v19_morning.py
    // body verbatim. Loss-less one-way migration into the cell-DAG model (findings 0054 D2).
    // Covers: OPEN-NM-03.
    static string Section3_SaveAsRoundTripIsLossless()
    {
        string rawOnDisk = File.ReadAllText(V19MorningPath, System.Text.Encoding.UTF8);

        var synth1 = new FakeMarimoSynthesizer { FailDecompose = true };   // production null leg
        var nb1 = new MarimoNotebookDocument(synth1);
        if (!nb1.Open(V19MorningPath)) return "round-trip: initial Open failed";

        string savedPath = Path.Combine(TempDir, "v19_morning_roundtrip.py");
        if (!nb1.SaveAs(savedPath)) return "round-trip: SaveAs failed";
        if (!File.Exists(savedPath)) return "round-trip: SaveAs produced no file";

        // Re-open the saved file with a NEW aggregate and a NORMAL synthesizer (FailDecompose=false):
        // because the file now carries the synthesizer's marker, Decompose succeeds the regular way —
        // proving the wrap survives a synthesise -> decompose round-trip without loss.
        var synth2 = new FakeMarimoSynthesizer { FailDecompose = false };
        var nb2 = new MarimoNotebookDocument(synth2);
        if (!nb2.Open(savedPath)) return "round-trip: re-Open of saved file failed (LastError=" + (nb2.LastError ?? "<null>") + ")";
        if (nb2.CellCount != 1) return "round-trip: re-Open produced " + nb2.CellCount + " cells, expected 1";
        if (nb2.Cells[0].Body != rawOnDisk)
            return "round-trip: re-Open body != original v19_morning.py text (migration is lossy)";
        if (nb2.Cells[0].Name != "_") return "round-trip: re-Open cell name '" + nb2.Cells[0].Name + "' != '_'";
        if (nb2.IsDirty) return "round-trip: re-Open notebook is dirty";

        return null;
    }

    // ---- 4. findings 0054 D4: the wrap downgraded Decompose-null from fail-soft to policy, but the
    // path/IO failure modes are STILL fail-soft (false + LastError, buffer unchanged). Guards against
    // widening the wrap to swallow IO errors too — a regression that would silently bind to garbage.
    // Covers: OPEN-NM-04.
    static string Section4_FailSoftOnlyOnPathIO()
    {
        var synth = new FakeMarimoSynthesizer { FailDecompose = true };
        var nb = new MarimoNotebookDocument(synth);

        // Seed a known good open so we can prove buffer non-destructive on each subsequent failure.
        if (!nb.Open(V19MorningPath)) return "fail-soft setup: initial Open of v19_morning.py failed";
        int seedCount = nb.CellCount;
        string seedBody = nb.Cells[0].Body;
        string seedPath = nb.CurrentPath;

        // empty path -> "no path".
        if (nb.Open("")) return "fail-soft: empty path Open should fail";
        if (nb.LastError != "no path") return "fail-soft: empty path LastError='" + nb.LastError + "' (expected 'no path')";
        if (nb.CellCount != seedCount || nb.Cells[0].Body != seedBody || nb.CurrentPath != seedPath)
            return "fail-soft: empty path Open mutated the buffer";

        // wrong extension -> "not a .py".
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

        return null;
    }
}
