// KabuLiveE2ERunner.cs — kabu (kabuステーション) demo/verify ライブ「ログイン→本番トリガ購読→板到達」の
// HITL E2E（台本: 同ディレクトリ KabuLiveE2ERunner.md / 方針: ADR-0022 / findings 0086）。
//
//   <Unity> -batchmode -nographics -quit -projectPath . \
//           -executeMethod KabuLiveE2ERunner.Run -logFile <log>
//   # expect: [E2E KABU-LIVE PASS] ... / exit=0
//
// ⚠️ HITL / Windows 限定: kabuステーション本体（Windows GUI アプリ）が起動・ログイン済みで API 有効
// (localhost:18081 検証ポート) でなければ REST/WS が応答しない。CI / macOS では走らない（owner 手元で実行）。
//
// #107 の主眼: 以前 TachibanaLiveE2ERunner が自分で SubmitSubscribeMarketData を叩いていた死角を kabu でも作らない。
// 本 runner は本番と同じ LiveSubscriptionCoordinator + LaneSubscribeSink を universe 投入 → LiveManual 突入の
// 一括購読として駆動し（テスト自身は購読 RPC を呼ばない）、実 demo PUSH の板（depth）が届くことをゲートする
// （AC#3: 選択銘柄の板が live 更新される）。kabu に第二暗証は無い（X-API-KEY トークンのみ・R3）。
//
// verify 固定: KABU_ALLOW_PROD=1 を検出したら拒否（本番 18080 への誤接続防止）。environment_hint は "verify"。

using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class KabuLiveE2ERunner
{
    const string VENUE = "KABU";
    const string ENV_HINT = "verify";         // 検証 18081 固定（本番 18080 は別ゲートまで解禁しない）
    const string INSTRUMENT = "7203.TSE";      // 東証・流動性の高い銘柄（板が来やすい）
    const int PUMP_SLEEP_MS = 50;
    const int LOGIN_TIMEOUT_MS = 30000;
    const int CONNECT_TIMEOUT_MS = 15000;
    const int BOARD_TIMEOUT_MS = 30000;        // 本番トリガ購読 → PUT /register → WS PUSH の板到達（場中のみ）

    static WorkspaceEngineHost s_host;
    static int s_loginDone;
    static bool s_loginOk;
    static string s_loginEc;

    public static void Run()
    {
        string fail = null;
        try { fail = Execute(); }
        catch (Exception e) { fail = "driver: " + e; }
        finally
        {
            try { s_host?.Stop(); }
            catch (Exception e) { Debug.LogWarning("[E2E KABU-LIVE] host.Stop failed (non-fatal): " + e.Message); }
        }

        if (fail == null)
        {
            // NOTE: this leg validates the coordinator→sink→lanes→engine→adapter→venue CHAIN against the real
            // venue (it constructs the coordinator itself, not BackcastWorkspaceRoot's wired _subCoord). The
            // root's BuildWorkspace wiring (_subCoord assignment / LiveSubscribeHook) is gated by the MOCK
            // LiveSubscribeWiringE2ERunner; this leg adds the real-feed half.
            Debug.Log("[E2E KABU-LIVE PASS] logged into kabu verify, drove the subscription coordinator "
                    + "(production trigger path, not a self-subscribe), and observed the live board (depth) for "
                    + INSTRUMENT + " arriving through the production poll.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E KABU-LIVE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Execute()
    {
        s_loginDone = 0;

        // ── step 0: creds（API パスワードのみ・第二暗証は無い / R3 / R10）。 ──
        string apiPw = EnvConfig.Get("DEV_KABU_API_PASSWORD");
        if (string.IsNullOrEmpty(apiPw))
            return "missing DEV_KABU_API_PASSWORD (process env or .env)";
        if (Environment.GetEnvironmentVariable("KABU_ALLOW_PROD") == "1")
            return "refusing to run: KABU_ALLOW_PROD=1 is set (this gate is verify-only; never touches 本番 18080)";

        // ── step 1: live-configured server を venue=KABU で構築。 ──
        var host = new WorkspaceEngineHost();
        s_host = host;
        host.InitializePython(VENUE);
        if (!host.ServerReady) return "host server not ready after InitializePython (kabuステーション本体 is up?)";
        SetOsEnviron("DEV_KABU_API_PASSWORD", apiPw);   // credentials_source="env" が os.environ から直読み

        // ── step 2: venue_login("env","verify") → CONNECTED ──
        host.VenueLogin(VENUE, "env", ENV_HINT, (ok, ec) =>
        {
            s_loginOk = ok; s_loginEc = ec; Volatile.Write(ref s_loginDone, 1);
        });
        if (!SpinUntil(() => Volatile.Read(ref s_loginDone) == 1, LOGIN_TIMEOUT_MS))
            return "venue_login did not return within " + (LOGIN_TIMEOUT_MS / 1000) + "s (kabuステーション本体 未起動/未ログイン?)";
        if (!s_loginOk)
            return "venue_login failed: " + s_loginEc + " (本体ログイン状態 / API パスワード / 検証モードを確認)";

        var conn = new VenueConnectionViewModel();
        if (!SpinUntil(() => { conn.ApplyStatePoll(host.LatestStateJson); return conn.IsConnected; }, CONNECT_TIMEOUT_MS))
            return "logged in but venue_state never reached CONNECTED (got " + conn.VenueState + ")";
        Debug.Log("[E2E KABU-LIVE-01 PASS] venue_login success and CONNECTED reached");

        // ── step 2.4: 本番トリガ経由で購読（テスト自身は SubmitSubscribeMarketData を呼ばない・#107） ──
        // 本番と同じ LiveSubscriptionCoordinator + LaneSubscribeSink で universe 一括購読を起動する。kabu は
        // PUT /register（50 銘柄上限・burst は kabusapi_ratelimit が throttle）→ WS PUSH で板が流れる。
        var universe = new InstrumentRegistry();
        universe.ReplaceAll(new[] { INSTRUMENT });
        var subCoord = new LiveSubscriptionCoordinator(new LaneSubscribeSink(host), universe);
        subCoord.OnModePoll(FooterModeViewModel.LiveManual);

        // ── step 2.5: 実 demo PUSH の板が届くまで gate（AC#3: 板が live 更新される） ──
        // 板（depth）は poll get_state_json の per_instrument[INSTRUMENT].depth に現れる。場中でないと PUSH が
        // 来ないため、timeout は閉局の可能性も併記する。
        if (!SpinUntil(() => DepthDecoder.Decode(host.LatestStateJson, INSTRUMENT).HasDepth, BOARD_TIMEOUT_MS))
            return "no live board (depth) for " + INSTRUMENT + " within " + (BOARD_TIMEOUT_MS / 1000) + "s "
                 + "— 本番トリガ購読が走っていない / 場中でない / 本体未配信 を疑う; state=" + host.LatestStateJson;
        Debug.Log("[E2E KABU-LIVE-02 PASS] live board (depth) arrived");

        return null;   // PASS
    }

    // ── helpers ──
    static void SetOsEnviron(string key, string value)
    {
        using (Py.GIL())
        using (PyObject os = Py.Import("os"))
        using (PyObject environ = os.GetAttr("environ"))
        using (var k = new PyString(key))
        using (var v = new PyString(value))
            environ.SetItem(k, v);
    }

    static bool SpinUntil(Func<bool> pred, int budgetMs)
    {
        int waited = 0;
        while (waited < budgetMs)
        {
            if (pred()) return true;
            Thread.Sleep(PUMP_SLEEP_MS);
            waited += PUMP_SLEEP_MS;
        }
        return pred();
    }
}
