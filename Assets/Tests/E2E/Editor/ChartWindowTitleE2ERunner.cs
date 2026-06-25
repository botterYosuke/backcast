// ChartWindowTitleE2ERunner.cs — Issue #140 release-gate slice runner (台本: same-dir
// ChartWindowTitleE2ERunner.md). 方針: findings 0112.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartWindowTitleE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART WINDOW TITLE PASS] / exit=0  (確認は Bash `grep -a "CHART-TITLE"`)
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHAT THIS GATES — chart window chrome title shows `<id> <name>` (e.g. "7203.TSE トヨタ自動車")
// instead of the kind-fixed "Chart" caption, so two chart windows side-by-side are visually
// distinguishable by their instrument. Formatting is shared with the picker via
// InstrumentLabel.Compose; name source is IAvailableInstrumentsProvider.TryGetName (cache-only,
// mode-agnostic — the picker is the SOLE fetch trigger, and a Replay→Live mode flip after a
// picker open does NOT discard the cached name). Spawn-time read only; the owner decision
// 2026-06-25 explicitly forbids a live differ seam, so cold paths (layout restore before the
// first picker open) accept the id-only fallback (CHART-TITLE-04/05).
//
// RED→GREEN litmus (findings 0112 §4 + review fixes):
//   (a) BuildDockWindowFrame: pass spec.title verbatim (skip ResolveChartTitleForId) → 01..05, 08 RED
//   (b) InstrumentLabel.Compose: drop the `name == id` / null / empty collapse → 03, 06 RED
//   (c) PickerRow.Candidate: hand-roll the label instead of calling InstrumentLabel.Compose → 07 RED
//
// (F5 owner decision 2026-06-25: CHART-TITLE-09 / construction-order guard 09 は retire — 番号は欠番)
//
// The probe is Python-FREE: composition is the real BackcastWorkspaceRoot scene with
// BuildWorkspace invoked via Reflection (sibling ChartUniverseSyncE2ERunner.ComposeRoot pattern).
// One Compose() serves every section (review finding #6 — was 12+ scene reopens before); per-row
// state is reset via Universe.Remove + StubProvider.Next + _provider field swap.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class ChartWindowTitleE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string IID = "7203.TSE";
    const string NAME = "トヨタ自動車";

    // The provider stub the chart-title resolver reads. Per row we set `Names` (the id→name table
    // the production BackendAvailableInstrumentsProvider keeps in `_nameByIid`); TryGetName scans
    // it cache-only with no (mode, end) coupling. `Next` is the AvailableInstrumentsResult Query
    // would return if a regression rewired Query back into the chart-title path — CHART-TITLE-05
    // varies it across 5 Kinds so the fallback is genuinely exercised per-state (not just 5
    // repetitions of the same call — review finding 2026-06-25 fix). Today the production
    // resolver does NOT call Query (cache-only via TryGetName), so Next has no effect on a clean
    // implementation; it's a forward-compatibility gate.
    sealed class StubProvider : IAvailableInstrumentsProvider
    {
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>();
        public AvailableInstrumentsResult Next = AvailableInstrumentsResult.Loading;
        public AvailableInstrumentsResult Query(UniverseSourceMode mode, string replayEndDate) => Next;
        public bool TryGetName(string instrumentId, out string name) => Names.TryGetValue(instrumentId, out name);
    }

    public static void Run()
    {
        string fail;
        try
        {
            fail = RunAll();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART WINDOW TITLE PASS] chart 窓 (id=chart:<iid>) のタイトルが provider の "
                    + "id→name 索引を引いて InstrumentLabel.Compose 経由で <id> <name> を描画する。"
                    + "name あり=01 / name 不在=02 / name==id collapse=03 / cache cold=04 / "
                    + "5 通り cold 状態=05 / Compose unit=06 / PickerRow.Candidate drift gate=07 / "
                    + "他 iid のみ cached=08 / "
                    + "listed_info rename 上書き semantics=10 / malformed id ('chart:') → spec.title fallback=11 / "
                    + "2 窓同時 spawn で各 iid の name 独立描画=12。09 は retire (F5 owner 決定 2026-06-25 — 欠番)。findings 0112。");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART WINDOW TITLE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string RunAll()
    {
        // One Compose() → one scene + one BuildWorkspace serves every section (review finding #6).
        var ctx = Compose();
        if (ctx.Fail != null) return "Compose: " + ctx.Fail;

        // Table-driven coverage of the resolver's primary outputs (rows 1-5, 8) — same Compose,
        // same stub, just resetting Names + universe membership between rows. Each row emits its
        // own [E2E CHART-TITLE-NN PASS] tag for the rollup.
        var rows = new (string Id, string Provider, Dictionary<string, string> Names, string Want, string Note)[]
        {
            ("01", "Ready/name",            DictOf(IID, NAME),         IID + " " + NAME, "name cached → <id> <name>"),
            ("02", "Ready/no-name",         DictOf(),                  IID,              "name not cached → id alone"),
            ("03", "Ready/name==id",        DictOf(IID, IID),          IID,              "name==id collapse → id alone"),
            ("04", "Cache cold (Loading)",  DictOf(),                  IID,              "cold cache → id alone (cold path fallback)"),
            ("08", "Different iid cached",  DictOf("9984.TSE", "ソフトバンクグループ"), IID, "other iid's name does NOT leak"),
        };
        foreach (var r in rows)
        {
            ctx.Provider.Names.Clear();
            foreach (var kv in r.Names) ctx.Provider.Names[kv.Key] = kv.Value;
            string t = SpawnAndReadTitle(ctx, IID);
            if (t == null) return "S CHART-TITLE-" + r.Id + " (" + r.Provider + "): chart window did not spawn / no Title text";
            if (t != r.Want) return "S CHART-TITLE-" + r.Id + " (" + r.Provider + "): title='" + t + "' want='" + r.Want + "' — " + r.Note;
            Debug.Log("[E2E CHART-TITLE-" + r.Id + " PASS] " + r.Provider + " → title='" + r.Want + "' (" + r.Note + ")");
        }

        // CHART-TITLE-05: every non-Ready provider state falls back to id alone. The production
        // resolver doesn't call Query so the "state" here is effectively "name absent from cache",
        // but the row pins the 5 Kind variants the provider can produce so a future regression that
        // wired Query back into the resolver and consulted Kind would still take the id-fallback.
        var coldStates = new (string Label, AvailableInstrumentsResult R)[]
        {
            ("Empty",        AvailableInstrumentsResult.Empty),
            ("NotConnected", AvailableInstrumentsResult.NotConnected),
            ("Unsupported",  AvailableInstrumentsResult.Unsupported),
            ("Error",        AvailableInstrumentsResult.Error("boom")),
            ("EndUnset",     AvailableInstrumentsResult.EndUnset),
        };
        foreach (var s in coldStates)
        {
            ctx.Provider.Names.Clear();   // cache cold for this iid
            ctx.Provider.Next = s.R;       // a regression that rewired Query into the resolver would see THIS Kind
            string t = SpawnAndReadTitle(ctx, IID);
            if (t == null) return "S CHART-TITLE-05 (" + s.Label + "): chart window did not spawn / no Title text";
            if (t != IID) return "S CHART-TITLE-05 (" + s.Label + "): title='" + t + "' want='" + IID + "'";
        }
        Debug.Log("[E2E CHART-TITLE-05 PASS] Empty / NotConnected / Unsupported / Error / EndUnset → all title='" + IID + "'");

        // CHART-TITLE-06: InstrumentLabel.Compose unit (4 inputs) — the shared formatter contract.
        string a = InstrumentLabel.Compose(IID, NAME);
        if (a != IID + " " + NAME) return "S CHART-TITLE-06: Compose(id, name) = '" + a + "' want '" + IID + " " + NAME + "'";
        string b = InstrumentLabel.Compose(IID, null);
        if (b != IID) return "S CHART-TITLE-06: Compose(id, null) = '" + b + "' want '" + IID + "'";
        string c = InstrumentLabel.Compose(IID, "");
        if (c != IID) return "S CHART-TITLE-06: Compose(id, \"\") = '" + c + "' want '" + IID + "'";
        string d = InstrumentLabel.Compose(IID, IID);
        if (d != IID) return "S CHART-TITLE-06: Compose(id, id) = '" + d + "' want '" + IID + "' (de-dup collapse)";
        Debug.Log("[E2E CHART-TITLE-06 PASS] InstrumentLabel.Compose unit: name / null / \"\" / id-collapse all correct");

        // CHART-TITLE-07: drift gate — PickerRow.Candidate's Label MUST equal InstrumentLabel.Compose.
        string[] names = { NAME, null, "", IID };
        foreach (var n in names)
        {
            var row = PickerRow.Candidate(IID, n, alreadyAdded: false);
            string want = InstrumentLabel.Compose(IID, n);
            if (row.Label != want)
                return "S CHART-TITLE-07: PickerRow.Candidate(id, '" + (n ?? "<null>") + "').Label = '"
                     + row.Label + "' but InstrumentLabel.Compose = '" + want
                     + "' (PickerRow stopped routing through the shared formatter — drift introduced)";
        }
        Debug.Log("[E2E CHART-TITLE-07 PASS] PickerRow.Candidate.Label === InstrumentLabel.Compose for 4 inputs (no drift)");

        // CHART-TITLE-10 (review F2): drive the REAL BackendAvailableInstrumentsProvider's name-
        // merge code path so the production seam `_nameByIid[id] = nm` is structurally pinned. A
        // `TryAdd` regression (first-wins) would leak `name1` past the second snapshot and break
        // the listed_info rename / cache-overwrite contract — owner expects the LATEST Ready to
        // win so renamed CompanyNames appear without restarting the app. We construct a fresh
        // Backend instance (host's default-ctor needs no Python init for this seam) and reflection-
        // invoke `MergeNameIndex` twice on the same iid with different names; TryGetName must
        // return the LATER name.
        {
            var hostType = typeof(WorkspaceEngineHost);
            var host = Activator.CreateInstance(hostType);
            var backendType = typeof(BackendAvailableInstrumentsProvider);
            var backend = Activator.CreateInstance(backendType, host) as BackendAvailableInstrumentsProvider;
            var merge = backendType.GetMethod("MergeNameIndex", BF);
            if (merge == null) return "S CHART-TITLE-10: MergeNameIndex helper missing on BackendAvailableInstrumentsProvider (renamed? regression on the AFK seam)";
            const string iid10 = "7203.TSE";
            var first = AvailableInstrumentsResult.Ready(new[] { iid10 }, new[] { "トヨタ自動車 (旧称)" });
            var second = AvailableInstrumentsResult.Ready(new[] { iid10 }, new[] { "トヨタ自動車" });
            merge.Invoke(backend, new object[] { first });
            if (!backend.TryGetName(iid10, out var n10a) || n10a != "トヨタ自動車 (旧称)")
                return "S CHART-TITLE-10: after 1st merge TryGetName returned '" + (n10a ?? "<null>") + "' want='トヨタ自動車 (旧称)'";
            merge.Invoke(backend, new object[] { second });
            if (!backend.TryGetName(iid10, out var n10b) || n10b != "トヨタ自動車")
                return "S CHART-TITLE-10: listed_info rename / cache 上書き path: `_nameByIid[id] = nm` が `TryAdd` regression に落ちたら RED (got='" + (n10b ?? "<null>") + "' want='トヨタ自動車')";
        }
        Debug.Log("[E2E CHART-TITLE-10 PASS] BackendAvailableInstrumentsProvider.MergeNameIndex: 2nd Ready overwrites 1st (listed_info rename semantics, NOT TryAdd)");

        // CHART-TITLE-11 (review F3): `ResolveChartTitleForId` ハス an unguarded malformed-id path
        // (`InstrumentOfChartId("chart:") == ""` → `string.IsNullOrEmpty(iid)` → return specTitle).
        // Universe.Add("") is rejected by the SoT so the production spawn path can't reach this
        // branch — reflection-invoke BuildDockWindowFrame directly with the KIND_CHART spec and
        // id="chart:" so the malformed-iid fallback is exercised. The returned frame's title must
        // be `"Chart"` (spec.title verbatim).
        {
            var catalogField = ctx.Ty.GetField("_catalog", BF);
            if (catalogField == null) return "S CHART-TITLE-11: _catalog field missing (renamed?)";
            var catalog = catalogField.GetValue(ctx.Root) as FloatingWindowCatalog;
            if (catalog == null) return "S CHART-TITLE-11: _catalog not built (BuildWorkspace skipped?)";
            if (!catalog.TryGet(FloatingWindowCatalog.KIND_CHART, out var chartSpec)) return "S CHART-TITLE-11: KIND_CHART spec missing from catalog";
            var build = ctx.Ty.GetMethod("BuildDockWindowFrame", BF);
            if (build == null) return "S CHART-TITLE-11: BuildDockWindowFrame method missing (renamed?)";
            var frame = build.Invoke(ctx.Root, new object[] { chartSpec, "chart:" }) as RectTransform;
            if (frame == null) return "S CHART-TITLE-11: BuildDockWindowFrame returned null for id='chart:' (malformed-id path crashed?)";
            var titleRt11 = frame.Find("TitleBar/Title") as RectTransform;
            if (titleRt11 == null) return "S CHART-TITLE-11: TitleBar/Title missing on malformed-id frame";
            var titleText11 = titleRt11.GetComponent<Text>();
            if (titleText11 == null) return "S CHART-TITLE-11: TitleBar/Title has no Text component";
            if (titleText11.text != "Chart") return "S CHART-TITLE-11: malformed-id title='" + titleText11.text + "' want='Chart' (spec.title fallback regression)";
            // Teardown: the orphan frame was parented to _dockLayer by DockWindowFrame.Build under
            // id="chart:" but _dockWindows never registered it; destroy the GO so subsequent rows'
            // RectOf / Find lookups don't trip over a sibling parented to the same layer.
            UnityEngine.Object.DestroyImmediate(frame.gameObject);
        }
        Debug.Log("[E2E CHART-TITLE-11 PASS] ResolveChartTitleForId malformed-id (`chart:` → empty iid) → spec.title fallback ('Chart')");

        // CHART-TITLE-12 (review F4 / 0112 §1 本旨): two chart windows side-by-side must each
        // show their OWN iid's name — this is the user-facing wedge that motivates the whole
        // slice. Spawn 7203.TSE and 9984.TSE in sequence (both stay in `_chartViews`), assert
        // each title independently via ReadChartTitle (which does NOT touch Universe membership),
        // then teardown both with the F1 despawn sentinel so the row state is clean for any
        // future section that re-uses this Compose.
        ctx.Provider.Names.Clear();
        ctx.Provider.Names["7203.TSE"] = "トヨタ自動車";
        ctx.Provider.Names["9984.TSE"] = "ソフトバンクグループ";
        if (ctx.Scenario.Universe.Ids.Contains("7203.TSE")) ctx.Scenario.Universe.Remove("7203.TSE");
        if (ctx.Scenario.Universe.Ids.Contains("9984.TSE")) ctx.Scenario.Universe.Remove("9984.TSE");
        if (ctx.ChartViews.ContainsKey("7203.TSE")) return "S CHART-TITLE-12: stale 7203.TSE chart view before spawn (row bleed)";
        if (ctx.ChartViews.ContainsKey("9984.TSE")) return "S CHART-TITLE-12: stale 9984.TSE chart view before spawn (row bleed)";
        if (!ctx.Scenario.Universe.Add("7203.TSE")) return "S CHART-TITLE-12: Universe.Add(7203.TSE) returned false";
        if (!ctx.Scenario.Universe.Add("9984.TSE")) return "S CHART-TITLE-12: Universe.Add(9984.TSE) returned false";
        if (!ctx.ChartViews.ContainsKey("7203.TSE")) return "S CHART-TITLE-12: 7203.TSE chart was not spawned by SyncChartWindowsToUniverse";
        if (!ctx.ChartViews.ContainsKey("9984.TSE")) return "S CHART-TITLE-12: 9984.TSE chart was not spawned by SyncChartWindowsToUniverse";
        string t12a = ReadChartTitle(ctx, "7203.TSE");
        string t12b = ReadChartTitle(ctx, "9984.TSE");
        if (t12a == null) return "S CHART-TITLE-12: ReadChartTitle(7203.TSE) returned null (TitleBar/Title missing)";
        if (t12b == null) return "S CHART-TITLE-12: ReadChartTitle(9984.TSE) returned null (TitleBar/Title missing)";
        if (t12a != "7203.TSE トヨタ自動車") return "S CHART-TITLE-12: 7203.TSE title='" + t12a + "' want='7203.TSE トヨタ自動車' — two-charts-side-by-side: each window must show its own instrument label";
        if (t12b != "9984.TSE ソフトバンクグループ") return "S CHART-TITLE-12: 9984.TSE title='" + t12b + "' want='9984.TSE ソフトバンクグループ' — two-charts-side-by-side: each window must show its own instrument label";
        ctx.Scenario.Universe.Remove("7203.TSE");
        ctx.Scenario.Universe.Remove("9984.TSE");
        if (ctx.ChartViews.ContainsKey("7203.TSE")) return "S CHART-TITLE-12: 7203.TSE chart not despawned after teardown (Universe.Changed despawn path broke)";
        if (ctx.ChartViews.ContainsKey("9984.TSE")) return "S CHART-TITLE-12: 9984.TSE chart not despawned after teardown (Universe.Changed despawn path broke)";
        Debug.Log("[E2E CHART-TITLE-12 PASS] two charts side-by-side: 7203.TSE='トヨタ自動車' / 9984.TSE='ソフトバンクグループ' rendered independently (each window shows its OWN iid's name)");

        // CHART-TITLE-09 is retire (F5 owner decision 2026-06-25): the previous construction-order
        // `if (_provider == null) return specTitle;` guard in ResolveChartTitleForId was removed in
        // favor of letting a reorder regression NRE LOUD at dev time. Number 09 is left as a gap
        // (do not renumber 10/11/12) so historical references — commits, PR threads, findings 0112
        // — keep resolving to the retired guard's history without dangling.

        return null;
    }

    // ── helpers ──

    sealed class Ctx
    {
        public BackcastWorkspaceRoot Root;
        public Type Ty;
        public StubProvider Provider;
        public ScenarioStartupController Scenario;
        public FloatingWindowController DockWindows;
        public IDictionary<string, ChartView> ChartViews;
        public string Fail;
    }

    // Compose ONCE per Run — the runner used to OpenScene+BuildWorkspace per section (12+ times)
    // which dominated wall-clock; review finding #6 collapses that to one scene + per-row state
    // reset. The production _provider is field-swapped to our StubProvider here so every row's
    // TryGetName goes through controllable state.
    static Ctx Compose()
    {
        var ctx = new Ctx();
        try
        {
            EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
            ctx.Root = UnityEngine.Object.FindAnyObjectByType<BackcastWorkspaceRoot>();
            ctx.Ty = typeof(BackcastWorkspaceRoot);
            if (ctx.Root == null) { ctx.Fail = "BackcastWorkspaceRoot missing in scene"; return ctx; }

            ctx.Ty.GetField("_font", BF).SetValue(ctx.Root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            ctx.Root.SetSynthesizer(new FakeMarimoSynthesizer());
            ctx.Ty.GetMethod("ResolvePaths", BF).Invoke(ctx.Root, null);
            ctx.Ty.GetMethod("BuildWorkspace", BF).Invoke(ctx.Root, null);

            ctx.Provider = new StubProvider();
            var pf = ctx.Ty.GetField("_provider", BF);
            if (pf == null) { ctx.Fail = "_provider field missing (renamed?) — chart title cannot reach a stub provider"; return ctx; }
            pf.SetValue(ctx.Root, ctx.Provider);

            ctx.Scenario = ctx.Ty.GetField("_scenario", BF)?.GetValue(ctx.Root) as ScenarioStartupController;
            ctx.DockWindows = ctx.Ty.GetField("_dockWindows", BF)?.GetValue(ctx.Root) as FloatingWindowController;
            ctx.ChartViews = ctx.Ty.GetField("_chartViews", BF)?.GetValue(ctx.Root) as IDictionary<string, ChartView>;
            if (ctx.Scenario == null || ctx.DockWindows == null || ctx.ChartViews == null)
            { ctx.Fail = "root seams not built (renamed? _scenario/_dockWindows/_chartViews)"; return ctx; }
            return ctx;
        }
        catch (Exception e) { ctx.Fail = "Compose exception: " + e; return ctx; }
    }

    // Drive the production spawn path: Universe.Remove (clear prior row's state) → Universe.Add →
    // InstrumentRegistry.Changed → SyncChartWindowsToUniverse → SpawnChartWindowAt →
    // _dockWindows.Spawn → BuildDockWindowFrame → ResolveChartTitleForId. Reads TitleBar/Title.
    static string SpawnAndReadTitle(Ctx ctx, string iid)
    {
        if (ctx.Scenario.Universe.Ids.Contains(iid)) ctx.Scenario.Universe.Remove(iid);
        // F1 anti-bleed sentinel: SyncChartWindowsToUniverse fires synchronously off Universe.Changed
        // (BackcastWorkspaceRoot:470), so the prior row's chart view MUST be despawned by the time
        // we return from Remove. If `_chartViews` still has this iid we'd read the PREVIOUS row's
        // window state (its rect / its title) and silently mark the new row green — a structural
        // regression that this sentinel turns into a loud failure with a diagnostic title.
        if (ctx.ChartViews.ContainsKey(iid)) return "stale chart view of iid='" + iid + "' was not despawned by SyncChartWindowsToUniverse — row state bleed risk";
        if (!ctx.Scenario.Universe.Add(iid)) return null;     // Add returned false → Changed not fired → spawn won't run
        if (!ctx.ChartViews.ContainsKey(iid)) return null;     // production spawn path failed (non-vacuity sentinel)
        var rt = ctx.DockWindows.RectOf(DockShape.ChartId(iid));
        if (rt == null) return null;
        var titleRt = rt.Find("TitleBar/Title") as RectTransform;
        if (titleRt == null) return null;
        var text = titleRt.GetComponent<Text>();
        if (text == null) return null;
        return text.text;
    }

    // Read a chart window's TitleBar/Title text WITHOUT touching Universe membership — F4
    // (CHART-TITLE-12) drives two charts in parallel, so SpawnAndReadTitle's Remove+Add cycle
    // would tear down the just-spawned sibling. Returns null on any lookup miss.
    static string ReadChartTitle(Ctx ctx, string iid)
    {
        if (!ctx.ChartViews.ContainsKey(iid)) return null;
        var rt = ctx.DockWindows.RectOf(DockShape.ChartId(iid));
        if (rt == null) return null;
        var titleRt = rt.Find("TitleBar/Title") as RectTransform;
        if (titleRt == null) return null;
        var text = titleRt.GetComponent<Text>();
        if (text == null) return null;
        return text.text;
    }

    static Dictionary<string, string> DictOf(params string[] pairs)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
        return d;
    }
}
