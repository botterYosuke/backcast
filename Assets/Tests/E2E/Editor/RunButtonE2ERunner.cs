// RunButtonE2ERunner.cs — Strategy Editor title-bar ▶ Run サーフェスの E2E 回帰ゲート（台本: 同ディレクトリの
// RunButtonE2ERunner.md）。第二波10本目。pure 行（RUN-02/04/07 readiness 真理値表・RUN-08 single-entry 構造）は
// throwaway `WorkspaceUiCutoverProbe`（Assets/Editor）の Section1/Section2 を verbatim 移送・昇格（ADR-0015）。
// RUN-03（block reason ラベル view）と RUN-01/05/06（OnRun→host 配線）は新規 section。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod RunButtonE2ERunner.Run -logFile <log>
//   # expect: [E2E RUN BUTTON PASS] ... / exit=0  （確認は Bash `grep -a "E2E RUN BUTTON"`。ripgrep/Select-String は取りこぼす）
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// host 配線 section（RUN-01/05/06）の設計判断: WorkspaceEngineHost は sealed・TryStartRun 非 virtual・_host は
// 具象 readonly フィールド（差し替え不可）なので「MOCK/spy host」は組めない。代わりに ReplayToHakoniwaE2ERunner
// と同型に実 root を反射合成し host.InitializePython("MOCK") で server-ready にし（batchmode 所有権スキップを迂回
// する正当手）、OnRun を反射 invoke して host の private `_req`（TryStartRun が同期で set）を読む。server-ready な
// 同一 host 上で「RUN-01=ready な OnRun は _req を埋める」を実証してから「RUN-05/06=blocked OnRun は _req を埋め
// ない」を assert するので vacuous でない（host が呼べる経路であることを先に立証）。Python は host 配線 section で
// のみ起こし、finally で host.Stop()（force_stop + lanes/launcher join + close、ADR-0001 で interpreter は生存）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class RunButtonE2ERunner
{
    const string ResumeKey = "backcast.lastDocument";   // mirrors BackcastWorkspaceRoot.ResumeKey
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    static WorkspaceEngineHost s_host;

    static string TempDir => Path.Combine(Application.temporaryCachePath, "run_button_e2e");
    static string StrategyPy => Path.Combine(TempDir, "kernel_spike_buy_sell.py");

    public static void Run()
    {
        string fail;
        try
        {
            fail = SectionA_RunReadinessTruthTable()   // RUN-02/04/07 (verbatim from WorkspaceUiCutoverProbe S1)
                ?? SectionB_BlockReasonLabel()          // RUN-03 (new uGUI view)
                ?? SectionC_SingleRunEntry()            // RUN-08 (verbatim from WorkspaceUiCutoverProbe S2)
                ?? SectionD_OnRunHostWiring();          // RUN-01/05/06 (new; Python-FULL MOCK host)
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E RUN BUTTON PASS] readiness truth table + block-reason label + single-entry + OnRun host wiring green.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E RUN BUTTON FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── A. RUN-02/04/07: Run-readiness truth table (pure VM; the OnRun gate order).
    //    Verbatim from WorkspaceUiCutoverProbe.Section1_RunReadinessTruthTable. ──
    // Covers: RUN-02, RUN-04, RUN-07
    static string SectionA_RunReadinessTruthTable()
    {
        // all gates pass → Run enabled, no reason.
        if (RunReadinessViewModel.Reason(true, false, true, true) != null) return "readiness: all-OK should be runnable";

        // each single block, in isolation.
        if (RunReadinessViewModel.Reason(true, true, true, true) != RunReadinessViewModel.Running) return "readiness: running not blocked";
        if (RunReadinessViewModel.Reason(true, false, false, true) != RunReadinessViewModel.NoStrategy) return "readiness: unsaved strategy not blocked";
        if (RunReadinessViewModel.Reason(true, false, true, false) != RunReadinessViewModel.InvalidScenario) return "readiness: invalid scenario not blocked";
        if (RunReadinessViewModel.Reason(false, false, true, true) != RunReadinessViewModel.NotOwner) return "readiness: not-owner not blocked";

        // precedence (OnRun order: running → no-strategy → invalid-scenario → not-owner).
        if (RunReadinessViewModel.Reason(true, true, false, false) != RunReadinessViewModel.Running) return "readiness: running must win over strategy/scenario";
        if (RunReadinessViewModel.Reason(true, false, false, false) != RunReadinessViewModel.NoStrategy) return "readiness: no-strategy must win over scenario/owner";
        if (RunReadinessViewModel.Reason(false, false, true, false) != RunReadinessViewModel.InvalidScenario) return "readiness: invalid-scenario must win over owner";

        // Evaluate() mirrors Reason() into CanRun / BlockReason.
        var vm = new RunReadinessViewModel();
        vm.Evaluate(true, false, true, true);
        if (!vm.CanRun || vm.BlockReason != null) return "readiness: Evaluate(all-OK) should be CanRun";
        vm.Evaluate(true, false, false, true);
        if (vm.CanRun || vm.BlockReason != RunReadinessViewModel.NoStrategy) return "readiness: Evaluate(unsaved) should block";
        return null;
    }

    // ── B. RUN-03: the block-reason label view. StrategyEditorRunButton.Refresh(vm) must mirror the VM
    //    into the uGUI button (interactable) + the status label (text/enabled). Headless: build under a
    //    bare RectTransform and reflect _btn/_status (no GPU — read interactable/text/enabled, not pixels).
    //    vacuity guard: assert _btn + _status EXIST first, then assert both the enabled (CanRun) and the
    //    disabled+reason states — a renamed field → null → the negative asserts would false-green. ──
    // Covers: RUN-03
    static string SectionB_BlockReasonLabel()
    {
        var go = new GameObject("run_button_view_e2e", typeof(RectTransform));
        try
        {
            var bar = (RectTransform)go.transform;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var view = new StrategyEditorRunButton(() => { });
            view.Build(bar, font);

            var btn = typeof(StrategyEditorRunButton).GetField("_btn", BF).GetValue(view) as Button;
            var status = typeof(StrategyEditorRunButton).GetField("_status", BF).GetValue(view) as Text;
            if (btn == null) return "label: _btn not built";
            if (status == null) return "label: _status not built";

            // all gates pass → button interactable, status hidden + empty.
            var ok = new RunReadinessViewModel();
            ok.Evaluate(true, false, true, true);
            view.Refresh(ok);
            if (!btn.interactable) return "label: Run not interactable when CanRun";
            if (status.enabled || !string.IsNullOrEmpty(status.text)) return "label: status shown when runnable (should be empty/hidden)";

            // each block reason → button greyed (non-interactable) + status shows the single-vocabulary reason.
            var noStrat = new RunReadinessViewModel(); noStrat.Evaluate(true, false, false, true);
            view.Refresh(noStrat);
            if (btn.interactable) return "label: Run interactable while NoStrategy";
            if (!status.enabled || status.text != RunReadinessViewModel.NoStrategy) return "label: status not NoStrategy text";

            var running = new RunReadinessViewModel(); running.Evaluate(true, true, true, true);
            view.Refresh(running);
            if (btn.interactable || status.text != RunReadinessViewModel.Running) return "label: Running reason not shown";

            var invalid = new RunReadinessViewModel(); invalid.Evaluate(true, false, true, false);
            view.Refresh(invalid);
            if (btn.interactable || status.text != RunReadinessViewModel.InvalidScenario) return "label: InvalidScenario reason not shown";

            var notOwner = new RunReadinessViewModel(); notOwner.Evaluate(false, false, true, true);
            view.Refresh(notOwner);
            if (btn.interactable || status.text != RunReadinessViewModel.NotOwner) return "label: NotOwner reason not shown";

            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // ── C. RUN-08: single Run entry (structural). Verbatim from WorkspaceUiCutoverProbe.Section2_SingleRunEntry. ──
    // Covers: RUN-08
    static string SectionC_SingleRunEntry()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "BackcastWorkspaceRoot missing in scene";

        // U1: the adopted editor title bar HAS a Run button (built + a "RunButton" GameObject on the bar).
        if (ty.GetField("_editorRunButton", BF).GetValue(root) == null) return "U1: adopted editor title-bar Run button not built";
        var titleInput = ty.GetField("_strategyEditorTitleInput", BF).GetValue(root) as Component;
        if (titleInput == null) return "U1: adopted editor title input missing in scene";
        if (FindChildButton((RectTransform)titleInput.transform, "RunButton") == null)
            return "U1: no 'RunButton' under the adopted editor title bar";

        // U4: the footer has the mode segments and NO replay-transport buttons.
        var footerContainer = ty.GetField("_footerContainer", BF).GetValue(root) as RectTransform;
        if (footerContainer == null) return "U4: footer container missing in scene";
        var footerBtnNames = ButtonNames(footerContainer);
        foreach (var seg in new[] { "btn:Replay", "btn:Manual", "btn:Auto" })
            if (!footerBtnNames.Contains(seg)) return "U4: footer missing mode segment " + seg;
        foreach (var transport in new[] { "btn:▶", "btn:⏸", "btn:⏭", "btn:⏹", "btn:1x", "btn:2x", "btn:5x", "btn:10x", "btn:50x" })
            if (footerBtnNames.Contains(transport)) return "U4: footer still has a retired transport button " + transport;

        // U5: the startup tile has NO Run button.
        var startupTile = ty.GetField("_startupTile", BF).GetValue(root) as RectTransform;
        if (startupTile == null) return "U5: startup tile missing in scene";
        if (ButtonNames(startupTile).Contains("btn:Run Replay")) return "U5: startup tile still has its Run button";

        return null;
    }

    // ── D. RUN-01/05/06: OnRun → host wiring. Compose the REAL root, CLAIM Python on this host
    //    (host.InitializePython MOCK — bypassing the batchmode WorkspaceOwnership skip, same legitimate
    //    move ReplayToHakoniwaE2ERunner makes), then reflect-invoke OnRun and read the host's private
    //    `_req` (TryStartRun sets it synchronously before launching). NON-VACUOUS ORDER: prove the
    //    blocked cases leave _req default FIRST (RUN-05 unbound, RUN-06 invalid scenario), then prove a
    //    ready OnRun POPULATES _req (RUN-01) — on the SAME server-ready host, so "host not called" is
    //    meaningful (a wrong wiring that called host would flip _req / start a run). ──
    // Covers: RUN-01, RUN-05, RUN-06
    static string SectionD_OnRunHostWiring()
    {
        if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
        Directory.CreateDirectory(TempDir);

        var root = ComposeRoot(out var ty);     // reuse the shared real-root composition (OpenScene/_font/SetSynthesizer/ResolvePaths/BuildWorkspace)
        if (root == null) return "host wiring: BackcastWorkspaceRoot missing in scene";

        var isOwnerField = ty.GetField("_isOwner", BF);
        if (isOwnerField == null) return "host wiring: _isOwner field not found (renamed?)";
        isOwnerField.SetValue(root, true);                      // OnRun's owner gate must pass for RUN-01

        var hostField = ty.GetField("_host", BF);
        if (hostField == null) return "host wiring: _host field not found (renamed?)";
        var host = hostField.GetValue(root) as WorkspaceEngineHost;
        if (host == null) return "host wiring: could not read _host";
        s_host = host;
        var onRun = ty.GetMethod("OnRun", BF);
        if (onRun == null) return "host wiring: OnRun method not found (renamed?)";
        var reqField = typeof(WorkspaceEngineHost).GetField("_req", BF);
        if (reqField == null) return "host wiring: _req field not found (renamed?)";

        try
        {
            // Claim Python on THIS host so TryStartRun's serverReady guard passes — otherwise "host not
            // called" (_req default) would be ambiguous (a serverReady bail also leaves _req default).
            host.InitializePython("MOCK");
            if (!host.ServerReady) return "host wiring: host server not ready after InitializePython";

            // RUN-05: a no-resume boot is the File→New blank (UNBOUND) state → the provider is not
            // supplyable → OnRun gates on BlockedNoStrategy and must NOT reach host.TryStartRun.
            PlayerPrefs.DeleteKey(ResumeKey);
            var resume = ty.GetMethod("ResumeLastDocumentOrDefault", BF);
            if (resume == null) return "host wiring: ResumeLastDocumentOrDefault method not found (renamed?)";
            resume.Invoke(root, null);
            onRun.Invoke(root, null);
            if (((WorkspaceEngineHost.RunRequest)reqField.GetValue(host)).Instruments != null)
                return "RUN-05: OnRun called host.TryStartRun with no strategy (host must be skipped on BlockedNoStrategy)";

            // File→Open the kernel-native fixture: its inline SCENARIO seeds the universe + the run
            // params (8918.TSE / 2024-10-01..2025-01-10 / Daily) and binds the notebook (supplyable).
            File.Copy(FixtureSource(), StrategyPy, true);
            root.SetFileDialog(new StubFileDialog { NextResult = StrategyPy });
            var onFileOpen = ty.GetMethod("OnFileOpen", BF);
            if (onFileOpen == null) return "host wiring: OnFileOpen method not found (renamed?)";
            onFileOpen.Invoke(root, null);

            var scenarioField = ty.GetField("_scenario", BF);
            if (scenarioField == null) return "host wiring: _scenario field not found (renamed?)";
            var scenario = scenarioField.GetValue(root) as ScenarioStartupController;
            if (scenario == null) return "host wiring: could not read _scenario";
            var openedIds = new List<string>(scenario.Universe.Ids);
            if (openedIds.Count == 0) return "host wiring: File→Open did not seed the universe from inline SCENARIO";

            // RUN-06: a supplyable strategy with an INVALID scenario (empty universe) → OnRun gates on
            // BlockedInvalidScenario and must NOT reach host. (_req still default from RUN-05.)
            foreach (var id in openedIds) scenario.RemoveInstrument(id);
            if (!scenario.Validate().Any) return "RUN-06: empty universe did not invalidate the scenario (precondition)";
            onRun.Invoke(root, null);
            if (((WorkspaceEngineHost.RunRequest)reqField.GetValue(host)).Instruments != null)
                return "RUN-06: OnRun called host.TryStartRun with an invalid scenario (host must be skipped on BlockedInvalidScenario)";

            // restore the valid universe → all four gates pass.
            foreach (var id in openedIds) scenario.AddInstrument(id);
            if (scenario.Validate().Any) return "host wiring: restored scenario unexpectedly invalid (precondition)";

            // RUN-01: a ready OnRun assembles the RunRequest from _scenario + the gate's StrategyPath and
            // calls host.TryStartRun → host._req is populated. THE non-vacuous positive (delete OnRun's
            // gate.IsReady branch and RUN-05/06 would start runs; delete the host call and this fails).
            onRun.Invoke(root, null);
            var req = (WorkspaceEngineHost.RunRequest)reqField.GetValue(host);
            if (req.Instruments == null) return "RUN-01: OnRun did not call host.TryStartRun on a ready scenario";
            if (req.Instruments.Length != openedIds.Count) return "RUN-01: req.Instruments count != universe";
            for (int i = 0; i < openedIds.Count; i++)
                if (req.Instruments[i] != openedIds[i]) return "RUN-01: req.Instruments[" + i + "] != universe order";
            if (req.Start != scenario.Params.Start) return "RUN-01: req.Start != scenario start";
            if (req.End != scenario.Params.End) return "RUN-01: req.End != scenario end";
            if (req.Granularity != ScenarioStartupParams.GranularityToString(scenario.Params.Granularity))
                return "RUN-01: req.Granularity != scenario granularity";
            // path identity: production normalises the bound path via Path.GetFullPath (StrategyDocument/
            // MarimoNotebookDocument.Open), so req.StrategyPath is all-backslash; StrategyPy is mixed-separator
            // (Application.temporaryCachePath returns '/' on Windows + Path.Combine joins with '\'). Compare
            // both via GetFullPath so the assert tests file identity, not separator style.
            if (Path.GetFullPath(req.StrategyPath) != Path.GetFullPath(StrategyPy))
                return "RUN-01: req.StrategyPath != opened strategy (got " + req.StrategyPath + ", want " + Path.GetFullPath(StrategyPy) + ")";

            return null;
        }
        finally
        {
            // teardown like production (force_stop + lanes/launcher join + server close; interpreter left
            // alive per ADR-0001). The RUN-01 OnRun started a real MOCK launcher — Stop() unblocks/joins it.
            try { s_host?.Stop(); } catch (Exception e) { Debug.LogWarning("[E2E RUN BUTTON] host.Stop failed (non-fatal): " + e.Message); }
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
        }
    }

    // ---- helpers (ComposeRoot / ButtonNames / FindChildButton migrated with Section C from WorkspaceUiCutoverProbe) ----
    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }

    static HashSet<string> ButtonNames(RectTransform rt)
    {
        var names = new HashSet<string>();
        foreach (var b in rt.GetComponentsInChildren<Button>(true)) names.Add(b.gameObject.name);
        return names;
    }

    static Button FindChildButton(RectTransform rt, string name)
    {
        foreach (var b in rt.GetComponentsInChildren<Button>(true))
            if (b.gameObject.name == name) return b;
        return null;
    }

    // the kernel-native fixture (inline SCENARIO + sys.path imports); a temp copy binds fine once
    // InitializePython has inserted ProjectRoot (mirrors ReplayToHakoniwaE2ERunner.FixtureSource).
    static string FixtureSource()
        => Path.Combine(PythonRuntimeLocator.ProjectRoot, "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py");
}
