// HakoniwaProfileProbe.cs — issue #62 "per-mode layout profile" (headless AFK regression gate)
//
// The headless, Python-FREE, UnityEngine-light gate for the #62 PURE logic (findings 0029 §7): the
// HakoniwaLayoutProfiles validity matrix (TTWR is_valid_for parity) + forward-compat seed + the
// LayoutStore disk round-trip of the additive hakoniwaProfiles field. Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod HakoniwaProfileProbe.Run -logFile <log>
//   # expect: [HAKONIWA PROFILE PASS] ... / exit=0
//
// SECTIONS (the owner's AFK 4-case set, grill Q4):
//   (a) valid honor: a profile whose base id set matches the mode → BaseOrderForMode returns the SAVED
//       (user-swapped) base order; Replay and Live carry DIFFERENT orders.
//   (b) legacy/collision → canonical: Default() / #60-era / set-mismatch profiles are IsValidForMode=false
//       → BaseOrderForMode = canonical Kinds(mode) (the #61 collision-safe fallback generalized).
//   (c) chart per-mode order: chart ids never affect validity (universe-owned) and are excluded from the
//       base Reorder prefix; the stored chart order is preserved in Get().
//   (d) seed + round-trip: an old single-`panels` doc seeds BOTH profiles NON-empty (forward-compat,
//       AC2); a #62 doc round-trips through LayoutStore with the per-mode slots present on disk.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class HakoniwaProfileProbe
{
    const float EPS = 1e-3f;
    static readonly LayoutRect R = new LayoutRect(0f, 0f, 1f, 1f);

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = SectionA_ValidProfileHonorsSavedBaseOrder()
                ?? SectionB_LegacyAndCollisionFallToCanonical()
                ?? SectionC_ChartOrderIsExcludedFromBaseAndPreserved()
                ?? SectionD_SeedAndDiskRoundTrip();
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[HAKONIWA PROFILE PASS] is_valid_for matrix + per-mode base honor/canonical + chart exclusion + forward-compat seed + disk round-trip verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[HAKONIWA PROFILE FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── (a) a VALID profile honors the saved (user-swapped) base order; Replay ≠ Live ──
    static string SectionA_ValidProfileHonorsSavedBaseOrder()
    {
        var p = new HakoniwaLayoutProfiles();
        // Replay: startup + 4 panels, with orders swapped ahead of buying_power (a user header-drag swap).
        p.Set(false, Panels("startup", "orders", "buying_power", "positions", "run_result", "chart:A", "chart:B"));
        // Live: the 4 panels, with run_result pulled to the front (a DIFFERENT per-mode arrangement).
        p.Set(true, Panels("run_result", "buying_power", "orders", "positions", "chart:A"));

        if (!p.IsValidForMode(false)) return "(a) valid Replay profile (base set matches Kinds(Replay)) must be valid";
        if (!p.IsValidForMode(true)) return "(a) valid Live profile (base set matches Kinds(Live)) must be valid";

        var rep = p.BaseOrderForMode(false);
        if (!SeqEqual(rep, new[] { "startup", "orders", "buying_power", "positions", "run_result" }))
            return "(a) Replay base order not honored (got [" + string.Join(",", rep) + "])";
        var liv = p.BaseOrderForMode(true);
        if (!SeqEqual(liv, new[] { "run_result", "buying_power", "orders", "positions" }))
            return "(a) Live base order not honored (got [" + string.Join(",", liv) + "])";
        // the whole point of per-mode: the two orders DIFFER.
        if (SeqEqual(rep, liv)) return "(a) Replay and Live base orders must DIFFER (per-mode is meaningless otherwise)";
        return null;
    }

    // ── (b) legacy / collision / set-mismatch → invalid → canonical Kinds(mode) ──
    static string SectionB_LegacyAndCollisionFallToCanonical()
    {
        var canonReplay = HakoniwaBaseTiles.Kinds(false);
        var canonLive = HakoniwaBaseTiles.Kinds(true);

        // LayoutDocument.Default() = [chart, status, positions, orders, run_result]: base set
        // {positions, orders, run_result} ≠ Kinds(Replay) (no startup/buying_power) → invalid.
        var def = new HakoniwaLayoutProfiles();
        def.SeedFromLegacy(LayoutDocument.Default().panels);
        if (def.IsValidForMode(false)) return "(b) Default() seed must be INVALID for Replay";
        if (def.IsValidForMode(true)) return "(b) Default() seed must be INVALID for Live";
        if (!SeqEqual(def.BaseOrderForMode(false), canonReplay)) return "(b) Default() Replay must fall to canonical";
        if (!SeqEqual(def.BaseOrderForMode(true), canonLive)) return "(b) Default() Live must fall to canonical";

        // #60-era sidecar [startup, chart:A, chart:B]: base set {startup} ≠ Kinds(Replay) → invalid → canonical.
        var era60 = new HakoniwaLayoutProfiles();
        era60.Set(false, Panels("startup", "chart:A", "chart:B"));
        if (era60.IsValidForMode(false)) return "(b) #60-era seed must be INVALID for Replay";
        if (!SeqEqual(era60.BaseOrderForMode(false), canonReplay)) return "(b) #60-era Replay must fall to canonical";

        // a Live profile that still carries startup: base set has an EXTRA id vs Kinds(Live) → invalid.
        var liveWithStartup = new HakoniwaLayoutProfiles();
        liveWithStartup.Set(true, Panels("startup", "buying_power", "orders", "positions", "run_result"));
        if (liveWithStartup.IsValidForMode(true)) return "(b) Live profile with startup must be INVALID (extra base id)";
        if (!SeqEqual(liveWithStartup.BaseOrderForMode(true), canonLive)) return "(b) over-set Live must fall to canonical";

        // a Replay profile MISSING a base id (run_result dropped): base set short → invalid → canonical.
        var missing = new HakoniwaLayoutProfiles();
        missing.Set(false, Panels("startup", "buying_power", "orders", "positions"));
        if (missing.IsValidForMode(false)) return "(b) Replay profile missing run_result must be INVALID";
        if (!SeqEqual(missing.BaseOrderForMode(false), canonReplay)) return "(b) under-set Replay must fall to canonical";

        // null/absent profile → invalid → canonical.
        var empty = new HakoniwaLayoutProfiles();
        if (empty.IsValidForMode(false) || empty.IsValidForMode(true)) return "(b) empty profiles must be invalid for both modes";
        if (!SeqEqual(empty.BaseOrderForMode(false), canonReplay)) return "(b) empty → Replay canonical";
        if (!SeqEqual(empty.BaseOrderForMode(true), canonLive)) return "(b) empty → Live canonical";
        return null;
    }

    // ── (c) chart ids never affect validity and are excluded from the base prefix; chart order preserved ──
    static string SectionC_ChartOrderIsExcludedFromBaseAndPreserved()
    {
        var p = new HakoniwaLayoutProfiles();
        // a valid Replay base set, with charts in a specific (non-id-sorted) order B before A.
        p.Set(false, Panels("startup", "buying_power", "orders", "positions", "run_result", "chart:B", "chart:A"));

        // charts must NOT break validity (membership is universe-owned, #60).
        if (!p.IsValidForMode(false)) return "(c) chart tiles must NOT affect base-set validity";

        // the base prefix excludes every chart id.
        foreach (var id in p.BaseOrderForMode(false))
            if (HakoniwaBaseTiles.IsChartId(id)) return "(c) BaseOrderForMode must exclude chart ids (got " + id + ")";

        // the stored profile preserves the chart order B,A (honored at apply via _hako.Apply).
        var stored = p.Get(false);
        int idxB = stored.FindIndex(x => x.id == "chart:B");
        int idxA = stored.FindIndex(x => x.id == "chart:A");
        if (idxB < 0 || idxA < 0 || idxB > idxA) return "(c) stored chart order (B before A) not preserved";
        return null;
    }

    // ── (d) forward-compat seed (AC2) + LayoutStore disk round-trip of hakoniwaProfiles ──
    static string SectionD_SeedAndDiskRoundTrip()
    {
        // forward-compat: an OLD single-`panels` doc (no hakoniwaProfiles) seeds BOTH modes non-empty.
        // Drive it through LoadFromJson (the real persistence boundary) so JsonUtility's absent-field
        // behavior is exercised, not a hand-built object.
        string oldJson =
            "{\"version\":1,\"panels\":[" +
            "{\"id\":\"startup\",\"slot\":0,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}," +
            "{\"id\":\"buying_power\",\"slot\":1,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}," +
            "{\"id\":\"orders\",\"slot\":2,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}," +
            "{\"id\":\"positions\",\"slot\":3,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}," +
            "{\"id\":\"run_result\",\"slot\":4,\"visible\":true,\"rect\":{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1}}]}";
        var oldDoc = LayoutStore.LoadFromJson(oldJson);
        var seeded = HakoniwaLayoutProfiles.FromDocument(oldDoc);
        if (seeded.Get(false) == null || seeded.Get(false).Count != 5) return "(d) forward-compat: Replay must seed 5 panels from legacy `panels`";
        if (seeded.Get(true) == null || seeded.Get(true).Count != 5) return "(d) forward-compat: Live must seed 5 panels from legacy `panels`";
        if (!seeded.IsValidForMode(false)) return "(d) forward-compat: a Replay-shaped legacy doc must seed a VALID Replay profile";
        if (seeded.IsValidForMode(true)) return "(d) forward-compat: a Replay-shaped legacy doc must be INVALID for Live (has startup) → canonical on load";

        // disk round-trip of a #62 doc with DISTINCT per-mode orders.
        var profiles = new HakoniwaLayoutProfiles();
        profiles.Set(false, Panels("startup", "orders", "buying_power", "positions", "run_result"));   // Replay: orders swapped
        profiles.Set(true, Panels("run_result", "buying_power", "orders", "positions"));               // Live: run_result first
        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = profiles.Get(false),               // active-mode mirror
            hakoniwaProfiles = profiles.Clone(),
            canvasView = CanvasView.Identity(),
            floatingWindows = new List<FloatingWindowLayout>(),
            strategyEditors = new List<StrategyEditorState>(),
        };

        string path = Path.Combine(Application.temporaryCachePath, "hakoniwa_profile_probe.json");
        if (File.Exists(path)) File.Delete(path);
        LayoutStore.Save(doc, path);
        if (!File.Exists(path)) return "(d) sidecar not written";

        string text = File.ReadAllText(path);
        if (!text.Contains("hakoniwaProfiles")) return "(d) on-disk text missing the hakoniwaProfiles field";
        if (!text.Contains("\"replay\"") || !text.Contains("\"live\"")) return "(d) on-disk text missing replay/live sub-profiles";

        var loaded = LayoutStore.Load(path);
        if (!LayoutDocument.StructurallyEqual(loaded, doc, EPS)) return "(d) per-mode disk round-trip not structurally equal";
        if (loaded.hakoniwaProfiles == null) return "(d) loaded hakoniwaProfiles is null";

        // non-vacuous: the loaded profiles carry the DISTINCT per-mode orders (not collapsed to seed).
        var lp = HakoniwaLayoutProfiles.FromDocument(loaded);
        if (!SeqEqual(lp.BaseOrderForMode(false), new[] { "startup", "orders", "buying_power", "positions", "run_result" }))
            return "(d) loaded Replay base order lost the swap";
        if (!SeqEqual(lp.BaseOrderForMode(true), new[] { "run_result", "buying_power", "orders", "positions" }))
            return "(d) loaded Live base order lost the swap";
        if (SeqEqual(lp.BaseOrderForMode(false), lp.BaseOrderForMode(true)))
            return "(d) loaded Replay/Live orders collapsed to the same — per-mode not persisted";

        // and a #62 doc is NOT re-seeded from `panels` (per-mode present takes precedence).
        if (!lp.IsValidForMode(true)) return "(d) loaded Live profile must be valid (used directly, not re-seeded)";

        File.Delete(path);
        return null;
    }

    // build a panels list with sequential slots and a unit rect (slot = index, all visible).
    static List<PanelLayout> Panels(params string[] ids)
    {
        var list = new List<PanelLayout>(ids.Length);
        for (int i = 0; i < ids.Length; i++) list.Add(new PanelLayout(ids[i], i, true, R.Clone()));
        return list;
    }

    static bool SeqEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
