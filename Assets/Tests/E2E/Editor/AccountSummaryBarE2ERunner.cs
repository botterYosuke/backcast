// AccountSummaryBarE2ERunner.cs — issues #174-178 "account summary bar" (ADR-0038 / findings 0126)
//
// AFK regression gate for the screen-anchored account summary bar that replaces the buying_power /
// orders / positions dock panels. Python-FREE: drives the REAL BackcastWorkspaceRoot (scene-opened,
// BuildWorkspace via reflection) and the production drive path (RefreshLiveTiles), injecting Replay
// snapshots via WorkspaceEngineHost.TestPortfolioJsonOverride (the #65 poll seam) and Live events via
// the real LivePanelViewModel.Apply (the #20 sink-drain seam). The bar exposes probe-observable
// primary text/colour + hover card text/visibility + icon texture, so every AC is asserted without a
// GPU or real pointer events.
//
//   ASB-01 = bar built: own ScreenSpaceOverlay canvas (NOT under Content), 4 slots, "—" placeholder pre-data.
//   ASB-02 = Replay primaries match the snapshot (equity / bp / position count / order count).
//   ASB-03 = slot ① recolours green (uPnL≥0) / red (uPnL<0) by the unrealized-pnl sign.
//   ASB-04 = Live equity = Cash + Σ(qty×avg+uPnL) derived (account has no equity field) + colour sign.
//   ASB-05 = hover detail: ②③④ BYTE-IDENTICAL to the dock-panel Format*/FormatReplay*; ① the new summary;
//            pointer enter shows the card / exit hides it.
//   ASB-06 = pan-immune: a canvas pan/zoom moves Content but NOT the bar (screen-anchored).
//   ASB-07 = non-persistent: the bar is not a Content child / not in any window controller → CaptureLayout
//            captures no bar entry (nothing to restore on boot).
//   ASB-08 = forward-compat: a saved layout that names buying_power/orders/positions is SKIPPED on restore
//            (retired kinds → catalog TryGet=false) while a chart entry still restores (non-vacuity).
//   ASB-09 = always visible across Replay / LiveManual / LiveAuto (mode flips never hide the bar).
//   ASB-10 = icon seam: each slot's RawImage has a non-null texture (the RenderTexture swap seam, #177/S5).
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod AccountSummaryBarE2ERunner.Run -logFile <abs>
//   expect: [E2E ACCOUNT SUMMARY BAR PASS] ASB-01..ASB-10 + per-id tags / exit 0.
// RED litmus: wire slot ② hover to FormatReplayOrders → ASB-05 RED; drop the equity uPnL term → ASB-04 RED;
//             parent the bar under _content → ASB-06 RED; leave a retired spec in Default() → ASB-08 RED.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class AccountSummaryBarE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags BSF = BindingFlags.NonPublic | BindingFlags.Static;

    public static void Run()
    {
        string fail = null;
        try { fail = RunSections(); }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E ACCOUNT SUMMARY BAR PASS] ASB-01..ASB-10 verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E ACCOUNT SUMMARY BAR FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    static string RunSections()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindAnyObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "ASB: BackcastWorkspaceRoot missing in scene";
        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var bar = ty.GetField("_accountBar", BF)?.GetValue(root) as AccountSummaryBarView;
        if (bar == null) return "ASB: _accountBar not built by BuildWorkspace (renamed?)";
        var host = ty.GetField("_host", BF)?.GetValue(root);
        if (host == null) return "ASB: _host not found (renamed?)";
        var hostTy = host.GetType();
        var pfOverride = hostTy.GetField("TestPortfolioJsonOverride", BindingFlags.NonPublic | BindingFlags.Instance);
        if (pfOverride == null) return "ASB: WorkspaceEngineHost.TestPortfolioJsonOverride not found (renamed?)";
        var panel = hostTy.GetProperty("Panel")?.GetValue(host) as LivePanelViewModel;
        if (panel == null) return "ASB: _host.Panel (LivePanelViewModel) not found (renamed?)";

        var refresh = ty.GetMethod("RefreshLiveTiles", BF);
        var lastLiveShape = ty.GetField("_lastLiveShape", BF);
        if (refresh == null || lastLiveShape == null) return "ASB: RefreshLiveTiles / _lastLiveShape not found (renamed?)";

        Action drive = () => refresh.Invoke(root, null);
        Action<bool> setShape = live => lastLiveShape.SetValue(root, live);
        Action<string> setPortfolio = v => pfOverride.SetValue(host, v);
        Action driveReplay = () => { setShape(false); drive(); };

        var colors = ThemeService.Current.colors;

        // ── ASB-01: structure + placeholder ──
        if (bar.SlotCount != 4) return $"ASB-01: bar has {bar.SlotCount} slots, expected 4";
        if (bar.Canvas == null || bar.Canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            return "ASB-01: bar is not on a ScreenSpaceOverlay canvas (must be screen-anchored, not a Content child)";
        var content = ty.GetField("_content", BF)?.GetValue(root) as RectTransform;
        if (content != null && bar.transform.IsChildOf(content))
            return "ASB-01: bar is a CHILD of Content — it would pan with the canvas (must be screen-anchored)";
        setPortfolio(null); driveReplay();   // no portfolio → all "—"
        for (int i = 0; i < 4; i++)
            if (bar.PrimaryText(i) != AccountSummaryFormat.PLACEHOLDER)
                return $"ASB-01: slot {i} primary = '{bar.PrimaryText(i)}', expected '—' before data";
        for (int i = 0; i < 4; i++)
            if (bar.IconImage(i) == null) return $"ASB-01: slot {i} has no RawImage icon frame";
        Debug.Log("[E2E ASB-01 PASS] bar built: ScreenSpaceOverlay, 4 slots, '—' placeholders, icon frames present.");

        // ── ASB-02: Replay primaries match the snapshot ──
        // equity=155321, bp=54321, 1 position, 1 order.
        const string pf1 =
            "{\"buying_power\":54321.0,\"cash\":54321.0,\"equity\":155321.0," +
            "\"positions\":[{\"symbol\":\"7203.TSE\",\"qty\":50,\"avg_price\":2000.0,\"unrealized_pnl\":100.0}]," +
            "\"orders\":[{\"symbol\":\"7203.TSE\",\"side\":\"BUY\",\"qty\":50.0,\"price\":2000.0,\"status\":\"FILLED\",\"ts_ms\":2}]," +
            "\"realized_pnl\":5.0,\"unrealized_pnl\":100.0}";
        setPortfolio(pf1); driveReplay();
        if (bar.PrimaryText(0) != AccountSummaryFormat.Money(155321.0)) return $"ASB-02: ① equity = '{bar.PrimaryText(0)}', expected '{AccountSummaryFormat.Money(155321.0)}' (PortfolioSnapshot.Equity)";
        if (bar.PrimaryText(1) != AccountSummaryFormat.Money(54321.0)) return $"ASB-02: ② buying power = '{bar.PrimaryText(1)}', expected '{AccountSummaryFormat.Money(54321.0)}'";
        if (bar.PrimaryText(2) != "1") return $"ASB-02: ③ position count = '{bar.PrimaryText(2)}', expected '1'";
        if (bar.PrimaryText(3) != "1") return $"ASB-02: ④ order count = '{bar.PrimaryText(3)}', expected '1'";
        Debug.Log("[E2E ASB-02 PASS] Replay primaries match snapshot (equity/bp/positions/orders).");

        // ── ASB-03: ① colour by unrealized-pnl sign (Replay) ──
        if (bar.PrimaryColor(0) != colors.hakoniwa_up)
            return "ASB-03: ① colour for uPnL≥0 is not the gain (up) colour";
        const string pfLoss =
            "{\"buying_power\":54000.0,\"cash\":54000.0,\"equity\":150000.0," +
            "\"positions\":[{\"symbol\":\"7203.TSE\",\"qty\":50,\"avg_price\":2000.0,\"unrealized_pnl\":-250.0}]," +
            "\"orders\":[],\"realized_pnl\":0.0,\"unrealized_pnl\":-250.0}";
        setPortfolio(pfLoss); driveReplay();
        if (bar.PrimaryColor(0) != colors.hakoniwa_down)
            return "ASB-03: ① colour for uPnL<0 is not the loss (down) colour — sign recolour broken";
        Debug.Log("[E2E ASB-03 PASS] ① recolours green (uPnL≥0) / red (uPnL<0).");

        // ── ASB-04: Live equity derivation (Cash + Σ(qty×avg+uPnL)) + colour ──
        // account: cash 54321, one position qty 50 @ 2000 with uPnL -100 → equity = 54321 + (50*2000-100) = 154221.
        const string acctWire =
            "{\"AccountEvent\":{\"cash\":54321.0,\"buying_power\":80000.0," +
            "\"positions\":[{\"symbol\":\"7203.TSE\",\"qty\":50.0,\"avg_price\":2000.0,\"unrealized_pnl\":-100.0}],\"ts_ms\":1}}";
        const string teleWire =
            "{\"LiveStrategyTelemetry\":{\"run_id\":\"r1\",\"strategy_id\":\"s\",\"realized_pnl\":1234.0," +
            "\"unrealized_pnl\":-100.0,\"order_count\":3,\"fill_count\":2,\"ts_ms\":1}}";
        panel.Apply(acctWire);
        panel.Apply(teleWire);
        setShape(true); drive();
        string expEquity = AccountSummaryFormat.Money(154221.0);
        if (bar.PrimaryText(0) != expEquity) return $"ASB-04: Live ① equity = '{bar.PrimaryText(0)}', expected derived '{expEquity}' (Cash + Σ(qty×avg+uPnL))";
        if (bar.PrimaryColor(0) != colors.hakoniwa_down) return "ASB-04: Live ① colour not red for negative Σ unrealized";
        if (bar.PrimaryText(1) != AccountSummaryFormat.Money(80000.0)) return $"ASB-04: Live ② buying power = '{bar.PrimaryText(1)}', expected '{AccountSummaryFormat.Money(80000.0)}'";
        if (bar.PrimaryText(2) != "1") return $"ASB-04: Live ③ position count = '{bar.PrimaryText(2)}', expected '1'";
        if (bar.PrimaryText(3) != "3") return $"ASB-04: Live ④ order count = '{bar.PrimaryText(3)}', expected '3' (telemetry.OrderCount)";
        Debug.Log("[E2E ASB-04 PASS] Live equity derived = Cash + Σ(qty×avg+uPnL); colour by sign; bp/positions/orders.");

        // ── ASB-05: hover detail byte-identity (②③④ = Format*/FormatReplay*) + ① summary; show/hide ──
        // Re-drive Replay so the hover cards are populated from pf1 (a known snapshot).
        setPortfolio(pf1); driveReplay();
        PortfolioSnapshot snap = ReplayPanelDecoder.DecodePortfolio(pf1);
        string expBp = InvokeReplayFmt(ty, "FormatReplayBuyingPower", snap);
        string expPos = InvokeReplayFmt(ty, "FormatReplayPositions", snap);
        string expOrd = InvokeReplayFmt(ty, "FormatReplayOrders", snap);
        if (bar.CardText(1) != expBp) return $"ASB-05: ② hover '{bar.CardText(1)}' != FormatReplayBuyingPower '{expBp}' (routing/byte-identity)";
        if (bar.CardText(2) != expPos) return $"ASB-05: ③ hover '{bar.CardText(2)}' != FormatReplayPositions '{expPos}'";
        if (bar.CardText(3) != expOrd) return $"ASB-05: ④ hover '{bar.CardText(3)}' != FormatReplayOrders '{expOrd}'";
        if (bar.CardText(0) != AccountSummaryFormat.ReplayAccountSummary(snap))
            return "ASB-05: ① hover is not the account summary (equity/unrealized/realized/cash)";
        // pointer enter shows, exit hides.
        if (bar.CardVisible(1)) return "ASB-05: hover card visible before pointer enter";
        bar.SetHovered(1, true);
        if (!bar.CardVisible(1)) return "ASB-05: hover card not shown on pointer enter";
        bar.SetHovered(1, false);
        if (bar.CardVisible(1)) return "ASB-05: hover card not hidden on pointer exit";
        Debug.Log("[E2E ASB-05 PASS] ②③④ hover byte-identical to Format*; ① account summary; pointer show/hide.");

        // ── ASB-06: pan-immune (screen-anchored) ──
        var canvas = ty.GetField("_canvas", BF)?.GetValue(root);
        if (canvas == null) return "ASB-06: _canvas (InfiniteCanvasController) not found (renamed?)";
        var applyView = canvas.GetType().GetMethod("ApplyView");
        var canvasViewTy = Type.GetType("CanvasView, " + typeof(BackcastWorkspaceRoot).Assembly.GetName().Name)
                           ?? FindType("CanvasView");
        if (applyView == null || canvasViewTy == null) return "ASB-06: ApplyView / CanvasView not found (renamed?)";
        Vector3 barBefore = bar.Strip.position;
        Vector3 contentBefore = content != null ? content.position : Vector3.zero;
        object view = Activator.CreateInstance(canvasViewTy, 140f, -90f, 1.6f);
        applyView.Invoke(canvas, new[] { view });
        if (content != null && (content.position - contentBefore).sqrMagnitude < 1e-6f)
            return "ASB-06: Content did not move on pan — the pan-invariance test would be vacuous";
        if ((bar.Strip.position - barBefore).sqrMagnitude > 1e-6f)
            return "ASB-06: bar MOVED on canvas pan — it must be screen-anchored (not a Content child)";
        Debug.Log("[E2E ASB-06 PASS] bar is pan-immune (Content moved, bar stayed put).");

        // ── ASB-07: non-persistent (no bar entry in CaptureLayout) ──
        var capture = ty.GetMethod("CaptureLayout", BF);
        if (capture == null) return "ASB-07: CaptureLayout not found (renamed?)";
        object doc = capture.Invoke(root, null);
        var fwField = doc.GetType().GetField("floatingWindows");
        var fws = fwField?.GetValue(doc) as IEnumerable;
        if (fws != null)
            foreach (var w in fws)
            {
                if (w == null) continue;
                string id = w.GetType().GetField("id")?.GetValue(w) as string;
                string kind = w.GetType().GetField("kind")?.GetValue(w) as string;
                if (id == "AccountSummaryBar" || kind == "account_summary_bar")
                    return "ASB-07: the bar rides CaptureLayout (it must persist NOTHING — fixed anchor, derived visibility)";
            }
        Debug.Log("[E2E ASB-07 PASS] bar persists nothing (absent from CaptureLayout).");

        // ── ASB-08: retired kinds skip on restore; a chart entry still restores (non-vacuity) ──
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var restoreFloating = ty.GetMethod("RestoreFloating", BF);
        if (dockWindows == null || restoreFloating == null) return "ASB-08: _dockWindows / RestoreFloating not found (renamed?)";
        var restoreDoc = new LayoutDocument
        {
            floatingWindows = new List<FloatingWindowLayout>
            {
                new FloatingWindowLayout("buying_power", "buying_power", 0, 0, 340, 140, 0, true),
                new FloatingWindowLayout("orders",       "orders",       0, 0, 380, 220, 1, true),
                new FloatingWindowLayout("positions",    "positions",    0, 0, 380, 220, 2, true),
                new FloatingWindowLayout("chart:ZZZ",    "chart",        0, 0, 520, 360, 3, true),
            },
        };
        restoreFloating.Invoke(root, new object[] { restoreDoc });
        foreach (var retired in new[] { "buying_power", "orders", "positions" })
            if (dockWindows.Has(retired))
                return $"ASB-08: retired '{retired}' SPAWNED on restore (must skip — catalog TryGet=false / forward-compat)";
        if (!dockWindows.Has("chart:ZZZ"))
            return "ASB-08: chart entry did NOT restore — the skip test is vacuous (RestoreFloating processed nothing)";
        if (DockShape.IsDockKind("buying_power") || DockShape.IsDockKind("orders") || DockShape.IsDockKind("positions"))
            return "ASB-08: a retired kind is still a dock kind (IsDockKind) — plane routing not retired";
        Debug.Log("[E2E ASB-08 PASS] retired kinds skip on restore; chart restores; IsDockKind false for retired.");

        // ── ASB-09: always visible across modes ──
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root);
        var applyPoll = footerMode?.GetType().GetMethod("ApplyPoll");
        if (footerMode == null || applyPoll == null) return "ASB-09: _footerMode / ApplyPoll not found (renamed?)";
        foreach (var mode in new[] { "Replay", "LiveManual", "LiveAuto" })
        {
            applyPoll.Invoke(footerMode, new object[] { "{\"execution_mode\":\"" + mode + "\",\"venue_state\":\"CONNECTED\"}" });
            setShape(mode != "Replay"); drive();
            if (!bar.gameObject.activeInHierarchy)
                return $"ASB-09: bar hidden under {mode} — it must be ALWAYS visible in every mode";
        }
        Debug.Log("[E2E ASB-09 PASS] bar always visible across Replay / LiveManual / LiveAuto.");

        // ── ASB-10: icon seam — each slot's RawImage has a non-null texture (RenderTexture swap seam) ──
        for (int i = 0; i < 4; i++)
            if (bar.IconTexture(i) == null)
                return $"ASB-10: slot {i} icon has no texture (the RenderTexture→RawImage swap seam is unwired)";
        Debug.Log("[E2E ASB-10 PASS] each slot's RawImage icon has a (RenderTexture) texture — swap seam wired.");

        return null;
    }

    static string InvokeReplayFmt(Type ty, string name, PortfolioSnapshot snap)
    {
        var m = ty.GetMethod(name, BSF);
        if (m == null) throw new Exception($"ASB: formatter {name} not found (renamed?)");
        return (string)m.Invoke(null, new object[] { snap });
    }

    static Type FindType(string simpleName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(simpleName);
            if (t != null) return t;
        }
        return null;
    }
}
