// StrategyPickerModel.cs — issue #80 "in-app Open Strategy .py picker" (DURABLE tier, pure logic)
//
// The Python-FREE, never-throws enumerator behind the menu-bar Strategy picker. It lists the
// strategy `.py` files under python/strategies/** and annotates each with its scenario status so
// the owner sees — BEFORE opening — what will happen on Run (findings 0047 §2 ③). The UI shell
// (MenuBarView Strategy dropdown) is the HITL surface; THIS enumeration + status classification is
// AFK-authoritative (StrategyPickerProbe).
//
// DESIGN (findings 0047, owner-locked 2026-06-17):
//   * Enumerate `.py` ONLY — `.json` is the derived sidecar (SidecarPathFor), never an open target;
//     an orphan `.json` (no sibling `.py`) is structurally absent from a `.py` enumeration (§1).
//   * NO scenario filter — list EVERY `.py` (incl. ones with no scenario). Filtering by scenario
//     presence would hide the #80 repro (a new `.py` with no scenario yet) and contradict
//     "Open is unconditional" (§2 ①, §3 A). A non-strategy helper `.py` is annotated `none`, not
//     hidden — the Run gate (empty universe → blocked, no crash) absorbs an accidental bind.
//   * status = sidecar exists? → Sidecar; else the inline `.py` SCENARIO read status →
//     Found→Inline / Absent→None / Unparseable→Unreadable. sidecar wins over inline (the same
//     priority ScenarioStartupController.Populate uses: ReadScenario(sidecar) ?? inline fallback).
//   * NEVER THROWS (mirrors ScenarioInlineReader): a missing/unreadable directory → empty list.
//   * `__pycache__` is excluded (a `*.py` glob already skips `.pyc`, but guard any stray `.py`).

using System;
using System.Collections.Generic;
using System.IO;

public enum StrategyScenarioStatus { Sidecar, Inline, None, Unreadable }

public struct StrategyPickerEntry
{
    public string Path;          // canonical absolute .py path (what StrategyEditorView.Open receives)
    public string DisplayName;   // path relative to the strategies dir, forward-slashed (e.g. "v19/v19_morning.py")
    public StrategyScenarioStatus Status;
}

public static class StrategyPickerModel
{
    // Enumerate every `.py` under `strategiesDir` (recursive), annotate each with its scenario
    // status, sorted by path. Never throws; returns an empty list for a null/missing/unreadable dir.
    public static List<StrategyPickerEntry> Enumerate(string strategiesDir)
    {
        var result = new List<StrategyPickerEntry>();
        if (string.IsNullOrEmpty(strategiesDir) || !Directory.Exists(strategiesDir)) return result;

        string baseFull;
        try { baseFull = Path.GetFullPath(strategiesDir).Replace('\\', '/').TrimEnd('/') + "/"; }
        catch { return result; }

        string[] files;
        try { files = Directory.GetFiles(strategiesDir, "*.py", SearchOption.AllDirectories); }
        catch { return result; }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            // GetFiles("*.py") can over-match longer extensions via 8.3 short names — pin it to ".py".
            if (!string.Equals(Path.GetExtension(f), ".py", StringComparison.OrdinalIgnoreCase)) continue;

            string norm = f.Replace('\\', '/');
            if (norm.Contains("/__pycache__/")) continue;   // never a runnable strategy

            string full;
            try { full = Path.GetFullPath(f); }
            catch { continue; }

            string rel = full.Replace('\\', '/');
            if (rel.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(baseFull.Length);

            result.Add(new StrategyPickerEntry
            {
                Path = full,
                DisplayName = rel,
                Status = StatusFor(full),
            });
        }
        return result;
    }

    // Classify a single `.py`: sidecar existence wins (the going-forward SoT), else the inline
    // SCENARIO read status. Never throws.
    public static StrategyScenarioStatus StatusFor(string pyPath)
    {
        // §2 ③: a present sidecar file => "sidecar" (matches Populate's ReadScenario(sidecar) ?? inline
        // priority; we annotate on existence, not on completeness — a malformed/incomplete sidecar
        // that Populate would skip is a separate edge, out of #80's locked scope).
        if (!string.IsNullOrEmpty(pyPath) && File.Exists(ScenarioSidecarStore.SidecarPathFor(pyPath)))
            return StrategyScenarioStatus.Sidecar;

        ScenarioInlineReader.Read(pyPath, out ScenarioReadStatus inlineStatus);
        switch (inlineStatus)
        {
            case ScenarioReadStatus.Found: return StrategyScenarioStatus.Inline;
            case ScenarioReadStatus.Unparseable: return StrategyScenarioStatus.Unreadable;
            default: return StrategyScenarioStatus.None;   // Absent — a blank/new/helper .py
        }
    }

    // The picker-row suffix the owner reads BEFORE opening (findings 0047 §2 ③).
    public static string StatusLabel(StrategyScenarioStatus status)
    {
        switch (status)
        {
            case StrategyScenarioStatus.Sidecar: return "scenario: sidecar";
            case StrategyScenarioStatus.Inline: return "scenario: inline";
            case StrategyScenarioStatus.Unreadable: return "scenario: ⚠ unreadable";
            default: return "scenario: none (Run blocked)";
        }
    }
}
