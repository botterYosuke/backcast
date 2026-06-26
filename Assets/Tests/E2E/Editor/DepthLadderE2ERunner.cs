// DepthLadderE2ERunner.cs — bid/ask depth ladder サーフェスの E2E 回帰ゲート（台本: 同ディレクトリの
// DepthLadderE2ERunner.md）。第二波。`WorkspaceDepthLadderProbe`（throwaway AFK gate, Assets/Editor）から
// 昇格・改名（git mv／ADR-0015 の回帰ゲート命名規約。先例 ScenarioStartup=findings 0054 / FooterMode=0055 /
// InfiniteCanvas=0056 / FloatingWindow=0057 / UniverseSidebar=0058）。実証済み Probe の §1（price decode 共有
// locator）と §2-4（per-tile mount / Live-Replay mode-sync / per-instrument render）を assert 1 行も削らず移送し
// 各 section に `Covers:` を付与、台本で `要新規自動化` だった DEPTH-03（固定 21 行・"---" fill）/ DEPTH-06
// （受信順忠実描画・defensive sort しない）/ DEPTH-09（Replay は decode 自体を skip）/ DEPTH-10（content
// signature early-out）を新規 section として追加した。Python-FREE（板 payload は decode 入力の JSON 文字列を直接
// 渡す——実 venue 不要）。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod DepthLadderE2ERunner.Run -logFile <log>
//   # expect: [E2E DEPTH LADDER PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// section ↔ Action ID は各 Section の `Covers:` コメント参照（台本の操作一覧表と双方向に追える）。gate 形は probe の
// `Execute()`-形（各 section が null=PASS、最初の失敗文字列を返す）をそのまま温存。`EditorApplication.Exit` は
// self-failing gate として無条件化。
//
// 据え置きの仕分け（台本「既存 Probe との対応」）: DEPTH-04（per-side 色 + LAST + テーマ切替）は production
// `DepthLadderView` graphics をサンプルする `ThemeProbe` が正本（findings 0054 で bid/ask を Hakoniwa の
// cream-legible roles hakoniwa_up/down/last へ移しつつ ThemeProbe を更新済み）——本 runner では扱わない。
// DEPTH-11（実ピクセル montage / 行背景の見た目）は `DepthLadderHitlMenu` の HITL 専用。

using System;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class DepthLadderE2ERunner
{
    const float EPS = 1e-3f;
    const float LADDER_WIDTH = 120f;   // mirror of BackcastWorkspaceRoot.LADDER_WIDTH (TTWR viewstate)
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    // X-has-depth / Y-no-depth payload (per_instrument keyed by id). X price 105 + a 2-level board;
    // Y price 20 + depth null. Shared by the render + signature sections so the early-out re-feed is
    // byte-identical (DEPTH-10). PAYLOAD_B / PAYLOAD_C are DERIVED by a single-token Replace so each
    // drifts EXACTLY ONE field — guaranteeing the confounder-free DEPTH-10 isolation below.
    static readonly string PAYLOAD_A =
        "{\"execution_mode\":\"LiveAuto\",\"per_instrument\":{" +
          "\"X.TSE\":{\"price\":105.0,\"depth\":{" +
            "\"bids\":[{\"price\":99.0,\"size\":5.0},{\"price\":98.0,\"size\":4.0}]," +
            "\"asks\":[{\"price\":101.0,\"size\":3.0},{\"price\":102.0,\"size\":2.0}],\"timestamp_ms\":11}}," +
          "\"Y.TSE\":{\"price\":20.0,\"depth\":null}" +
        "}}";
    // Drifts ONLY X's price (105 -> 106) — depth + timestamp byte-identical to A. DepthSignature is over
    // depth+last, so this re-renders.
    static readonly string PAYLOAD_B = PAYLOAD_A.Replace("\"price\":105.0", "\"price\":106.0");
    // Drifts ONLY the board timestamp (11 -> 777) — depth + last price byte-identical to A. DepthSignature
    // DELIBERATELY excludes TimestampMs, so the early-out MUST STILL skip this (a venue that bumps the
    // timestamp every poll while the board is unchanged can't force a 21-row rebuild).
    static readonly string PAYLOAD_C = PAYLOAD_A.Replace("\"timestamp_ms\":11", "\"timestamp_ms\":777");

    // ---- root context, built once and shared by the root-driven sections (S3-S6) ----
    static BackcastWorkspaceRoot _root;
    static IDictionary _depthLadders, _chartAreas, _chartViews, _depthRendered;
    static MethodInfo _applyMode, _render, _drive;
    static WorkspaceEngineHost _host;

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = Section1_PriceDecoder()
                ?? Section2_LayoutAndReceiveOrder()      // DEPTH-03, DEPTH-06 (standalone view)
                ?? BuildRoot()
                ?? Section3_MountAndModeSync()           // DEPTH-01, DEPTH-02
                ?? Section4_ReplayDecodeSkip()           // DEPTH-09
                ?? Section5_PerInstrumentRender()        // DEPTH-05, DEPTH-07, DEPTH-08
                ?? Section6_SignatureEarlyOut()         // DEPTH-10
                ?? Section7_LadderMesh();               // LADDER-RENDER-01 / LADDER-RENDER-02
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[E2E DEPTH LADDER PASS] price decode shared-locator (decoy/null/absent/non-number/malformed) + " +
                      "fixed 21-row layout (10 ask + LAST + 10 bid, missing levels '---') + faithful wire order " +
                      "(best = array[0], no defensive sort) + per-tile mount (ladder sibling of chartArea + ChartView) + " +
                      "Live/Replay mode-sync (hidden+full-width <-> shown+inset by LADDER_WIDTH) + Replay decode-skip " +
                      "(DriveDepthLadders early-out, _depthRendered untouched) + per-instrument render (X board + LAST " +
                      "from per_instrument price; Y no-board placeholder, no single-global leak) + content-signature " +
                      "early-out (unchanged + timestamp-only board skip the 21-row rebuild; price drift re-renders) verified.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E DEPTH LADDER FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── 1. InstrumentPriceDecoder shared locator (feeds DEPTH-07's LAST row) ──
    // Covers: DEPTH-07
    // decoy inside a string value / null / absent id / no per_instrument / non-number price / whitespace
    // -> null (no throw); malformed structure -> FormatException (NOT swallowed).
    static string Section1_PriceDecoder()
    {
        const string state =
            "{\"price\":1.0,\"live_last_error\":\"spurious price: 999.99 in a string\"," +
            "\"per_instrument\":{" +
              "\"X.TSE\":{\"price\":105.5,\"ohlc_points\":null,\"depth\":{\"bids\":[{\"price\":99.0,\"size\":5.0}],\"asks\":[{\"price\":101.0,\"size\":3.0}],\"timestamp_ms\":7}}," +
              "\"Y.TSE\":{\"price\":20.0,\"ohlc_points\":null,\"depth\":null}," +
              "\"Z.TSE\":{\"price\":\"oops\",\"depth\":null}" +
            "}}";

        var x = InstrumentPriceDecoder.Decode(state, "X.TSE");
        if (!x.HasValue || Math.Abs(x.Value - 105.5) > EPS) return "S1: X.TSE price wrong (decoy leak or miss)";
        var y = InstrumentPriceDecoder.Decode(state, "Y.TSE");
        if (!y.HasValue || Math.Abs(y.Value - 20.0) > EPS) return "S1: Y.TSE price wrong";

        if (InstrumentPriceDecoder.Decode(state, "Z.TSE").HasValue) return "S1: string price must be null";
        if (InstrumentPriceDecoder.Decode(state, "0000.TSE").HasValue) return "S1: absent id must be null";
        if (InstrumentPriceDecoder.Decode("{\"price\":1}", "X.TSE").HasValue) return "S1: no per_instrument must be null";
        if (InstrumentPriceDecoder.Decode("null", "X.TSE").HasValue) return "S1: \"null\" must be null";
        if (InstrumentPriceDecoder.Decode("   ", "X.TSE").HasValue) return "S1: whitespace must be null";

        bool threw = false;
        try { InstrumentPriceDecoder.Decode("{\"per_instrument\":\"oops\"}", "X.TSE"); }
        catch (FormatException) { threw = true; }
        if (!threw) return "S1: malformed (per_instrument not an object) must throw FormatException";
        return null;
    }

    // ── 2. fixed 21-row layout + faithful wire order, on a standalone DepthLadderView (root-FREE) ──
    // Covers: DEPTH-03, DEPTH-06
    // A partial snapshot fed in NON-canonical order on purpose: bids ASCENDING [98, 99] (the producer
    // contract is bids DESCENDING) and asks DESCENDING [102, 101] (contract is asks ASCENDING), only 2 of
    // 10 levels each. The view must draw wire order verbatim: best = array[0] (index 0, regardless of
    // value) -> best bid = 98, best ask = 102. Always 21 rows; missing levels render "---".
    // delete-the-logic litmus: drop the fixed-21 loop -> childCount != 21; add a defensive re-sort to the
    // canonical order -> best bid flips 98->99 / best ask flips 102->101 -> the wire-order asserts FAIL
    // (this is why the data is fed UN-sorted — canonical data would make the no-re-sort guard vacuous).
    static string Section2_LayoutAndReceiveOrder()
    {
        var parentGo = new GameObject("S2LadderParent", typeof(RectTransform));
        try
        {
            var parent = parentGo.GetComponent<RectTransform>();
            parent.sizeDelta = new Vector2(LADDER_WIDTH, 420f);
            var viewGo = new GameObject("S2Ladder", typeof(RectTransform));
            viewGo.transform.SetParent(parent, false);
            var view = viewGo.AddComponent<DepthLadderView>();
            view.Build(parent);

            var snapshot = new DepthSnapshotView
            {
                HasDepth = true,
                Bids = new[] { new DepthLevelView { Price = 98.0, Size = 4.0 }, new DepthLevelView { Price = 99.0, Size = 5.0 } },
                Asks = new[] { new DepthLevelView { Price = 102.0, Size = 2.0 }, new DepthLevelView { Price = 101.0, Size = 3.0 } },
                TimestampMs = 1,
            };
            view.Render(snapshot, 105.0);

            // S8 #161 (findings 0120 D-9): the legacy _rowsRoot.childCount==21 seam moves to RowCount
            // (Mesh widget pre-allocates 21 Text children; placeholder mode reports 1 active).
            if (view.RowCount != 21)
                return $"S2: RowCount={view.RowCount}, want 21 on a HasDepth snapshot (fixed-count ladder)";

            // The TOP row (display index 0) is ask index 9 (worst, missing here) -> "ASK   ---" (fixed-
            // count "---" fill, worst-ask-on-top ordering). Catches a dynamic-count ladder that only
            // renders present levels. Walk the Rows GameObject children since indexing is stable.
            var rowsRoot = typeof(DepthLadderView).GetField("_rowsRoot", BF).GetValue(view) as RectTransform;
            if (rowsRoot == null) return "S2: _rowsRoot not found (DepthLadderView renamed?)";
            var topText = rowsRoot.GetChild(0).GetComponent<Text>();
            if (topText == null || !topText.text.Contains("---"))
                return $"S2: top (worst, missing) ask level must render '---' (got '{topText?.text}')";

            // Wire order: best bid = bids[0] = 98, best ask = asks[0] = 102 (index 0, regardless of value).
            // A defensive re-sort to canonical (bids desc / asks asc) would flip these to 99 / 101.
            if (view.BestBidRowText == null || view.BestAskRowText == null) return "S2: best bid/ask row missing on a HasDepth snapshot";
            if (!view.BestBidRowText.Contains("98.0"))
                return $"S2: best bid must track bids[0]=98 (wire order, no re-sort to canonical descending), got '{view.BestBidRowText}'";
            if (!view.BestAskRowText.Contains("102"))
                return $"S2: best ask must track asks[0]=102 (wire order, no re-sort to canonical ascending), got '{view.BestAskRowText}'";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(parentGo); }
    }

    // Build the REAL BackcastWorkspaceRoot headlessly + a 2-instrument universe, then cache the private
    // seams the root-driven sections reflect (mirrors WorkspaceDepthLadderProbe §2's root drive).
    static string BuildRoot()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        _root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (_root == null) return "build: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(_root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(_root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(_root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(_root) as ScenarioStartupController;
        _depthLadders = ty.GetField("_depthLadders", BF).GetValue(_root) as IDictionary;
        _chartAreas = ty.GetField("_chartAreas", BF).GetValue(_root) as IDictionary;
        _chartViews = ty.GetField("_chartViews", BF).GetValue(_root) as IDictionary;
        _depthRendered = ty.GetField("_depthRendered", BF).GetValue(_root) as IDictionary;
        _host = ty.GetField("_host", BF).GetValue(_root) as WorkspaceEngineHost;
        _applyMode = ty.GetMethod("ApplyDepthLadderMode", BF);
        _render = ty.GetMethod("RenderDepthLadders", BF);
        _drive = ty.GetMethod("DriveDepthLadders", BF);
        if (scenario == null || _depthLadders == null || _chartAreas == null || _chartViews == null
            || _depthRendered == null || _host == null || _applyMode == null || _render == null || _drive == null)
            return "build: root internals not found (renamed?)";

        scenario.Universe.ReplaceAll(new[] { "X.TSE", "Y.TSE" });
        return null;
    }

    // ── 3. per-tile mount + Live/Replay mode-sync (WorkspaceDepthLadderProbe §2-3) ──
    // Covers: DEPTH-01, DEPTH-02
    static string Section3_MountAndModeSync()
    {
        // mount: a 2-instrument universe spawns 2 chart tiles, each with a DepthLadderView in a right
        // strip + a sibling chartArea hosting the ChartView (per-tile mount, not an orphan widget).
        foreach (var id in new[] { "X.TSE", "Y.TSE" })
        {
            var ladder = _depthLadders[id] as DepthLadderView;
            var area = _chartAreas[id] as RectTransform;
            var cv = _chartViews[id] as ChartView;
            if (ladder == null) return "S3: no DepthLadderView for " + id;
            if (area == null) return "S3: no chartArea for " + id;
            if (cv == null) return "S3: no ChartView for " + id;
            if (ladder.transform.parent != area.parent)
                return "S3: ladder not a sibling of chartArea (not mounted in the tile) for " + id;
            if (cv.transform != area)
                return "S3: ChartView is not on the chartArea for " + id;
        }
        if (_depthLadders.Count != 2) return "S3: expected exactly 2 ladders (one per instrument)";

        // mode-sync: built in Replay -> ladder hidden + chart full width; Live -> shown + inset; back to
        // Replay -> hidden + full width again.
        foreach (var id in new[] { "X.TSE", "Y.TSE" })
        {
            var ladder = _depthLadders[id] as DepthLadderView;
            var area = _chartAreas[id] as RectTransform;
            if (ladder.gameObject.activeSelf) return "S3: ladder must be HIDDEN at build (Replay default) for " + id;
            if (Mathf.Abs(area.offsetMax.x) > EPS) return "S3: chart must be FULL width in Replay for " + id;
        }
        _applyMode.Invoke(_root, new object[] { true });   // -> Live
        foreach (var id in new[] { "X.TSE", "Y.TSE" })
        {
            var ladder = _depthLadders[id] as DepthLadderView;
            var area = _chartAreas[id] as RectTransform;
            if (!ladder.gameObject.activeSelf) return "S3: ladder must be SHOWN in Live for " + id;
            if (Mathf.Abs(area.offsetMax.x - (-LADDER_WIDTH)) > EPS) return "S3: chart must inset by LADDER_WIDTH in Live for " + id;
        }
        _applyMode.Invoke(_root, new object[] { false });  // -> Replay
        foreach (var id in new[] { "X.TSE", "Y.TSE" })
        {
            var ladder = _depthLadders[id] as DepthLadderView;
            var area = _chartAreas[id] as RectTransform;
            if (ladder.gameObject.activeSelf) return "S3: ladder must HIDE again in Replay for " + id;
            if (Mathf.Abs(area.offsetMax.x) > EPS) return "S3: chart must reclaim full width in Replay for " + id;
        }
        return null;
    }

    // ── 4. Replay decode-skip: DriveDepthLadders short-circuits BEFORE decoding when !isLive ──
    // Covers: DEPTH-09
    // The root is in Replay (built default, left there by S3) with NOTHING rendered yet. We inject a
    // board payload via the host's post-logout snapshot seam (LatestStateJson serves _finalStateJson
    // when _teardownComplete) so a payload IS present, then drive the REAL DriveDepthLadders. The
    // `if (!isLive ...) return;` guard must skip the decode entirely: _depthRendered stays empty and no
    // board is rendered. delete-the-logic litmus: drop that guard and DriveDepthLadders reads the
    // injected payload, decodes X, and renders -> _depthRendered gains X / BestBid becomes non-null.
    static string Section4_ReplayDecodeSkip()
    {
        var hty = typeof(WorkspaceEngineHost);
        var finalState = hty.GetField("_finalStateJson", BF);
        var teardown = hty.GetField("_teardownComplete", BF);
        if (finalState == null || teardown == null) return "S4: host snapshot seam not found (WorkspaceEngineHost renamed?)";

        finalState.SetValue(_host, PAYLOAD_A);
        teardown.SetValue(_host, true);   // LatestStateJson now serves PAYLOAD_A
        try
        {
            if (_host.LatestStateJson != PAYLOAD_A) return "S4: payload injection seam broke (LatestStateJson not serving the snapshot)";
            if (_depthRendered.Count != 0) return "S4: precondition — nothing should be rendered before the Replay drive";

            _drive.Invoke(_root, null);   // Replay (default DisplayMode) -> must early-out before decode

            if (_depthRendered.Count != 0)
                return "S4: Replay must SKIP decode (DriveDepthLadders early-out) — _depthRendered was populated";
            foreach (var id in new[] { "X.TSE", "Y.TSE" })
            {
                var ladder = _depthLadders[id] as DepthLadderView;
                // S8 #161 (findings 0120): the legacy `BestBid()/BestAsk()/LastRow() != null` seam is
                // replaced by HasDepth check on CurrentSnapshot. Build calls Render(Empty) once → that
                // bumps RebuildCount to 1 (HasDepth=false). If the Replay drive ALSO called Render on a
                // real board, RebuildCount would have advanced AND CurrentSnapshot.HasDepth=true.
                if (ladder.CurrentSnapshot.HasDepth)
                    return "S4: Replay decoded + rendered a board for " + id + " (CurrentSnapshot HasDepth=true — the !isLive skip is broken)";
            }
            return null;
        }
        finally
        {
            teardown.SetValue(_host, false);   // restore so the later sections drive the views directly
            finalState.SetValue(_host, null);
        }
    }

    // ── 5. per-instrument render: X board (+LAST from per_instrument price); Y no-board placeholder ──
    // Covers: DEPTH-05, DEPTH-07, DEPTH-08
    // X has depth + price 105; Y has no depth. RenderDepthLadders decodes each tile's OWN board: X shows
    // best bid/ask + "LAST 105.00"; Y stays a placeholder (X's board is NOT leaked to Y — single-global
    // regression kill).
    static string Section5_PerInstrumentRender()
    {
        _applyMode.Invoke(_root, new object[] { true });   // show the ladders so Render targets live views
        _render.Invoke(_root, new object[] { PAYLOAD_A });

        var lx = _depthLadders["X.TSE"] as DepthLadderView;
        if (lx.BestAskRowText == null || lx.BestBidRowText == null) return "S5: X board not rendered (best bid/ask row text null)";
        if (lx.LastRowText == null) return "S5: X LAST row missing";
        if (lx.LastRowText != "LAST 105.00") return "S5: X LAST not from per_instrument price (got '" + lx.LastRowText + "')";

        var ly = _depthLadders["Y.TSE"] as DepthLadderView;
        // Y has no depth → placeholder mode; BestBidRowText / BestAskRowText / LastRowText all null
        // (CurrentSnapshot.HasDepth=false guard in the getters).
        if (ly.CurrentSnapshot.HasDepth)
            return "S5: Y (no depth) must be a placeholder — X's board leaked to Y (single-global regression)";
        return null;
    }

    // ── 6. content-signature early-out: unchanged board skips the 21-row rebuild; drift re-renders ──
    // Covers: DEPTH-10
    // S5 has rendered X from PAYLOAD_A, so _depthRendered[X] holds its signature. We stamp a sentinel on
    // the live LAST text, then drive three re-feeds:
    //   (a) BYTE-IDENTICAL PAYLOAD_A   -> signature match  -> SKIP    (sentinel survives)
    //   (b) timestamp-only PAYLOAD_C   -> signature match  -> SKIP    (sentinel survives — ts is EXCLUDED)
    //   (c) price-drift PAYLOAD_B      -> signature drift   -> RE-RENDER ("LAST 106.00")
    // delete-the-logic litmus: drop `if (prev == sig) continue;` -> (a) rebuilds, sentinel gone -> FAIL.
    // add TimestampMs back into DepthSignature -> (b) rebuilds, sentinel gone -> FAIL. So the two checks
    // independently pin "unchanged skips" AND "timestamp is not part of the signature".
    static string Section6_SignatureEarlyOut()
    {
        var lx = _depthLadders["X.TSE"] as DepthLadderView;
        // S8 #161 migration: the legacy sentinel-on-Text seam is replaced by RebuildCount. The root
        // computes DepthSignature and either calls ladder.Render (bumps RebuildCount) or skips. We
        // baseline the count, push PAYLOAD_A again (unchanged sig → skip), then PAYLOAD_C (ts-only
        // diff → still skip since DepthSignature excludes timestamp), then PAYLOAD_B (price diff → real
        // rebuild). The B step also verifies LastRowText is the new "LAST 106.00".
        int baselineRC = lx.RebuildCount;

        _render.Invoke(_root, new object[] { PAYLOAD_A });   // unchanged -> signature match -> skip rebuild
        if (lx.RebuildCount != baselineRC)
            return $"S6: unchanged board must skip the 21-row rebuild (RebuildCount went up by {lx.RebuildCount - baselineRC} → root early-out gone)";

        _render.Invoke(_root, new object[] { PAYLOAD_C });   // ONLY timestamp drifted -> signature excludes it -> still skip
        if (lx.RebuildCount != baselineRC)
            return $"S6: a timestamp-only drift must STILL skip (DepthSignature excludes TimestampMs) — RebuildCount advanced by {lx.RebuildCount - baselineRC} => ts leaked into the signature";

        _render.Invoke(_root, new object[] { PAYLOAD_B });   // price drift -> re-render
        if (lx.RebuildCount == baselineRC)
            return "S6: price drift must re-render the board (RebuildCount unchanged → signature is over-eager)";
        if (lx.LastRowText != "LAST 106.00")
            return $"S6: price drift re-render wrong (got LastRowText='{lx.LastRowText}', expected 'LAST 106.00')";
        return null;
    }

    // ── 7. S8 #161 / findings 0120 D-9: Mesh widget invariants (LADDER-RENDER-01 / LADDER-RENDER-02). ──
    // The new MaskableGraphic ladder reports RowCount=21 when HasDepth, RowCount=1 when placeholder.
    // The 21 per-side alpha bg quads (10 ask + LAST + 10 bid) and the placeholder bg are emitted into
    // ONE Mesh batch — RebuildCount advances on each effective Render call, which means OnPopulateMesh
    // gets dirtied. Drawcall=1 itself is implied by single-batch architecture (no separate Image
    // GameObjects per row anymore — verified structurally by RowCount being a Mesh-reported count).
    static string Section7_LadderMesh()
    {
        // LADDER-RENDER-01: HasDepth -> RowCount==21.
        var lx = _depthLadders["X.TSE"] as DepthLadderView;
        if (!lx.CurrentSnapshot.HasDepth)
            return "S7 LADDER-RENDER-01: precondition — X must have a HasDepth snapshot after S5's render";
        if (lx.RowCount != 21)
            return $"S7 LADDER-RENDER-01: HasDepth ladder RowCount={lx.RowCount}, want 21 (10 ask + LAST + 10 bid)";

        // LADDER-RENDER-02: !HasDepth -> RowCount==1 placeholder.
        var ly = _depthLadders["Y.TSE"] as DepthLadderView;
        if (ly.CurrentSnapshot.HasDepth)
            return "S7 LADDER-RENDER-02: precondition — Y must be placeholder (no depth) after S5";
        if (ly.RowCount != 1)
            return $"S7 LADDER-RENDER-02: placeholder ladder RowCount={ly.RowCount}, want 1 (single \"no board\" row)";

        // Single-source Bid/Ask via ChartPalette (LADDER-PALETTE-01 also gated by ThemeProbe Section 4c).
        if (lx.BestBidColor != ChartPalette.Bullish())
            return "S7 LADDER-PALETTE-01: BestBidColor != ChartPalette.Bullish() — direct hakoniwa_up read regression";
        if (lx.BestAskColor != ChartPalette.Bearish())
            return "S7 LADDER-PALETTE-01: BestAskColor != ChartPalette.Bearish()";
        return null;
    }
}
