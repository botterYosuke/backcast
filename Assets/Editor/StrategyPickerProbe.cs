// StrategyPickerProbe.cs — issue #80 AFK gate for the in-app "Open Strategy .py" picker.
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod StrategyPickerProbe.Run -logFile <log>
//   # expect: [STRATEGY PICKER PASS] ... / exit=0
//
// Locks the pure enumeration + scenario-status classification (StrategyPickerModel) that the
// menu-bar Strategy dropdown renders. The UI shell is HITL; this logic is AFK-authoritative
// (findings 0047 §3). Python-FREE, throwaway, self-cleaning temp dir.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class StrategyPickerProbe
{
    static string TempRoot => Path.Combine(Application.temporaryCachePath, "strategy_picker_probe");

    [MenuItem("Probes/Run Strategy Picker Probe")]
    public static void Run()
    {
        string fail = null;
        try
        {
            ResetTempDir();
            fail = Section1_StatusClassification()
                ?? Section2_OrphanJsonAndPycacheExcluded()
                ?? Section3_RelativeNameAndSorting()
                ?? Section4_MissingDirIsEmpty()
                ?? Section5_StaleFileGuard();
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
            Debug.Log("[STRATEGY PICKER PASS] .py-only enumeration + scenario status " +
                "(sidecar/inline/none/unreadable) + orphan-json & __pycache__ excluded + relative names/sort " +
                "+ missing-dir-empty + stale-file Open guard");
        else
            Debug.LogError("[STRATEGY PICKER FAIL] " + fail);
    }

    // ── S1: the 4 statuses, plus sidecar-wins-over-broken-inline (non-vacuous) ──
    static string Section1_StatusClassification()
    {
        string dir = Path.Combine(TempRoot, "s1");
        Directory.CreateDirectory(dir);

        // (a) sidecar present — and the inline is intentionally BROKEN so a wrong precedence would
        //     classify it Unreadable; sidecar must win → Sidecar (non-vacuous).
        Write(dir, "withsidecar.py", "SCENARIO = {\n");          // unbalanced inline
        Write(dir, "withsidecar.json", "{\"start\":\"2025-01-06\"}");
        // (b) inline SCENARIO, no sidecar → Inline
        Write(dir, "inline.py",
            "SCENARIO = {\"start\": \"2025-01-06\", \"end\": \"2025-01-10\", " +
            "\"granularity\": \"Minute\", \"initial_cash\": 1000000, \"instruments\": [\"7203.TSE\"]}\n");
        // (c) no SCENARIO at all → None (blank/new/helper .py)
        Write(dir, "helper.py", "def features():\n    return 1\n");
        // (d) malformed SCENARIO, no sidecar → Unreadable
        Write(dir, "broken.py", "SCENARIO = {\n");

        var map = ByName(StrategyPickerModel.Enumerate(dir));

        if (!map.TryGetValue("withsidecar.py", out var a) || a.Status != StrategyScenarioStatus.Sidecar)
            return "S1(a): sidecar should win over broken inline (got " + StatusOf(map, "withsidecar.py") + ")";
        if (!map.TryGetValue("inline.py", out var b) || b.Status != StrategyScenarioStatus.Inline)
            return "S1(b): inline SCENARIO should classify Inline (got " + StatusOf(map, "inline.py") + ")";
        if (!map.TryGetValue("helper.py", out var c) || c.Status != StrategyScenarioStatus.None)
            return "S1(c): no SCENARIO should classify None (got " + StatusOf(map, "helper.py") + ")";
        if (!map.TryGetValue("broken.py", out var d) || d.Status != StrategyScenarioStatus.Unreadable)
            return "S1(d): malformed SCENARIO should classify Unreadable (got " + StatusOf(map, "broken.py") + ")";

        // every status has a distinct, non-empty label
        var labels = new HashSet<string>
        {
            StrategyPickerModel.StatusLabel(StrategyScenarioStatus.Sidecar),
            StrategyPickerModel.StatusLabel(StrategyScenarioStatus.Inline),
            StrategyPickerModel.StatusLabel(StrategyScenarioStatus.None),
            StrategyPickerModel.StatusLabel(StrategyScenarioStatus.Unreadable),
        };
        if (labels.Count != 4) return "S1: status labels are not distinct";
        return null;
    }

    // ── S2: an orphan .json (no sibling .py) is NOT listed; __pycache__ .py is excluded ──
    static string Section2_OrphanJsonAndPycacheExcluded()
    {
        string dir = Path.Combine(TempRoot, "s2");
        Directory.CreateDirectory(dir);
        // orphan sidecar: a .json with no .py beside it
        Write(dir, "orphan.json", "{\"start\":\"2025-01-06\"}");
        // a real strategy alongside it
        Write(dir, "real.py", "SCENARIO = {\"instruments\": [\"7203.TSE\"]}\n");
        // a __pycache__ .py that must be excluded
        string pyc = Path.Combine(dir, "__pycache__");
        Directory.CreateDirectory(pyc);
        File.WriteAllText(Path.Combine(pyc, "real.cpython-313.py"), "SCENARIO = {}\n");

        var entries = StrategyPickerModel.Enumerate(dir);
        var names = ByName(entries);
        if (names.Count != 1) return "S2: expected exactly 1 entry, got " + entries.Count;
        if (!names.ContainsKey("real.py")) return "S2: real.py missing from enumeration";
        foreach (var e in entries)
            if (e.DisplayName.Contains("__pycache__")) return "S2: __pycache__ .py leaked into enumeration";
        return null;
    }

    // ── S3: DisplayName is relative + forward-slashed even when nested; results are sorted ──
    static string Section3_RelativeNameAndSorting()
    {
        string dir = Path.Combine(TempRoot, "s3");
        string sub = Path.Combine(dir, "v19");
        Directory.CreateDirectory(sub);
        Write(dir, "zzz.py", "x = 1\n");
        Write(sub, "v19_morning.py", "x = 1\n");

        var entries = StrategyPickerModel.Enumerate(dir);
        if (entries.Count != 2) return "S3: expected 2 entries, got " + entries.Count;

        var names = ByName(entries);
        if (!names.ContainsKey("v19/v19_morning.py"))
            return "S3: nested DisplayName not relative+forward-slashed (got names: " + string.Join(",", names.Keys) + ")";
        // Path must be absolute (what Open receives)
        if (!Path.IsPathRooted(names["v19/v19_morning.py"].Path)) return "S3: Path is not absolute";
        // sorted ascending by path: "v19/..." sorts before "zzz.py"
        if (entries[0].DisplayName != "v19/v19_morning.py" || entries[1].DisplayName != "zzz.py")
            return "S3: entries not sorted (got " + entries[0].DisplayName + ", " + entries[1].DisplayName + ")";
        return null;
    }

    // ── S4: a missing directory yields an empty list, no throw ──
    static string Section4_MissingDirIsEmpty()
    {
        var entries = StrategyPickerModel.Enumerate(Path.Combine(TempRoot, "does_not_exist"));
        if (entries == null) return "S4: null instead of empty list";
        if (entries.Count != 0) return "S4: missing dir should enumerate empty";
        if (StrategyPickerModel.Enumerate(null).Count != 0) return "S4: null dir should enumerate empty";
        return null;
    }

    // ── S5: the stale-list guard — a vanished .py is rejected by StrategyDocument.Open (the
    //       exact gate OnOpenStrategy relies on to avoid a crash on a stale entry, findings 0047 §1). ──
    static string Section5_StaleFileGuard()
    {
        string dir = Path.Combine(TempRoot, "s5");
        Directory.CreateDirectory(dir);
        string py = Path.Combine(dir, "vanishing.py");
        File.WriteAllText(py, "SCENARIO = {}\n");

        var doc = new StrategyDocument();
        if (!doc.Open(py)) return "S5: Open of an existing .py should succeed";

        File.Delete(py);   // the picker's list is now stale
        if (doc.Open(py)) return "S5: Open of a vanished .py must return false (no crash, caller re-lists)";
        return null;
    }

    // ── helpers ──
    static void Write(string dir, string name, string content) => File.WriteAllText(Path.Combine(dir, name), content);

    static Dictionary<string, StrategyPickerEntry> ByName(List<StrategyPickerEntry> entries)
    {
        var map = new Dictionary<string, StrategyPickerEntry>();
        foreach (var e in entries) map[e.DisplayName] = e;
        return map;
    }

    static string StatusOf(Dictionary<string, StrategyPickerEntry> map, string name)
        => map.TryGetValue(name, out var e) ? e.Status.ToString() : "<absent>";

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }

    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
    }
}
