// TachibanaLiveE2ERunner.cs — 立花 demo ライブ「ログイン→成行発注→約定」の E2E 回帰ゲート
// （台本: 同ディレクトリの TachibanaLiveE2ERunner.md / 設計の木: docs/findings/0053）。
//
// これまで owner HITL でしか確認していなかった「実 venue（立花 demo）への接続→発注→約定」を
// batchmode で完全自動化する初の LIVE E2E。MOCK ではなく demo-kabuka.e-shiten.jp を実際に叩く。
//
//   <Unity> -batchmode -nographics -quit -projectPath . \
//           -executeMethod TachibanaLiveE2ERunner.Run -logFile <log>
//   # expect: [E2E TACHIBANA-LIVE PASS] ... / exit=0
//
// ハーネス: WorkspaceEngineHost を単体 new し、venue を "TACHIBANA" 固定で InitializePython する
// （render 経路は対象外なので scene は組まない — host が _sink/_panel/_lanes を自己完結で所有する）。
// ReplayToHakoniwaE2ERunner と同じく host.InitializePython を直接呼んで batchmode の
// WorkspaceOwnership スキップを正当に迂回する（KernelTeardownProbe と同型）。
//
// 完全自動化の肝 = 第二暗証番号。第二暗証は env に載せない（R10）。.env の DEV_TACHIBANA_SECOND を
// runner が char[] で保持し、発注中に push される SecretRequired を main の DrainLiveEvents で拾って
// urgent-secret lane（SubmitSecret）から応答する。GUI secret modal の画素は引き続き owner HITL。
//
// 厳密ゲート: 約定（FILLED OrderEvent）が来なければ FAIL（owner 決定）。成行は場中（前場 09:00–11:30 /
// 後場 12:30–15:30 JST）のみ約定するため、閉局時は is_market_open 診断を FAIL メッセージに添える。
// demo 固定: TACHIBANA_ALLOW_PROD=1 を検出したら発注前に拒否し、environment_hint は常に "demo"。

using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class TachibanaLiveE2ERunner
{
    const string VENUE = "TACHIBANA";
    const string ENV_HINT = "demo";          // demo 固定（本番は別 issue のリスクゲートまで解禁しない）
    const string INSTRUMENT = "7203.TSE";     // owner 指定（トヨタ）
    const string SIDE = "BUY";
    const double QTY = 100.0;                  // 売買単位 1 口
    const string ORDER_TYPE = "MARKET";        // 成行 → price=null / sOrderPrice="0"
    const string TIF = "DAY";

    const int PUMP_SLEEP_MS = 50;              // main pump cadence（lanes の poll/secret と協調）
    const int LOGIN_TIMEOUT_MS = 30000;        // venue_login（REQUEST login + WS 接続 + set_mode）
    const int CONNECT_TIMEOUT_MS = 15000;      // poll が venue_state=CONNECTED に収束するまで
    const int EC_WS_GATE_TIMEOUT_MS = 60000;   // #85 step 2.5: EC WS handshake (SSL 含む) が成立し最初のフレーム到達まで。SSL handshake (2-5s) + KP keepalive (5-12s server-driven) + 1 reconnect cycle (backoff ≥1s) ＋ flaky network 余裕で 60s。code-review G#2 で 30s→60s に bump。
    const int PLACE_TIMEOUT_MS = 60000;        // place_order ack（secret resolve 30s 含む）
    const int FILL_TIMEOUT_MS = 30000;         // EC fill push（場中のみ来る）
    const int CANCEL_TIMEOUT_MS = 45000;       // #85 不具合 3: cancel も _resolve_secret を内部で呼ぶため 30s+ 余裕

    static WorkspaceEngineHost s_host;

    // ── cross-thread handoff（callback は worker thread で発火。Volatile で可視性を担保） ──
    static int s_loginDone;
    static bool s_loginOk;
    static string s_loginEc;
    static int s_placeDone;
    static OrderRpcResult s_place;
    static int s_cancelDone;
    static OrderRpcResult s_cancel;

    public static void Run()
    {
        char[] second = null;
        string fail = null;
        try
        {
            fail = Execute(out second);
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }
        finally
        {
            if (second != null) Array.Clear(second, 0, second.Length);   // 第二暗証を確実に消す
            try { s_host?.Stop(); }
            catch (Exception e) { Debug.LogWarning("[E2E TACHIBANA-LIVE] host.Stop failed (non-fatal): " + e.Message); }
        }

        if (fail == null)
        {
            Debug.Log("[E2E TACHIBANA-LIVE PASS] logged into TACHIBANA demo, placed a 100-share MARKET BUY on " +
                      INSTRUMENT + " (second password answered via urgent-secret lane), and observed the FILLED " +
                      "OrderEvent through the production sink.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E TACHIBANA-LIVE FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // Returns null on PASS, else the first failure message. `second` は呼び元 finally が zeroize する。
    static string Execute(out char[] second)
    {
        second = null;
        s_loginDone = 0; s_placeDone = 0; s_cancelDone = 0;

        // ── step 0: 資格情報を解決。EnvConfig = process env 優先 → <repo>/.env → <repo>/python/.env
        // （`set -a; source .env` / CI の env 注入も拾える）。USER_ID/PASSWORD は os.environ へ、第二暗証は
        // char[] に持って submit_secret へ渡す（env には入れない / R10）。 ──
        string uid = EnvConfig.Get("DEV_TACHIBANA_USER_ID");
        string pw = EnvConfig.Get("DEV_TACHIBANA_PASSWORD");
        string secondStr = EnvConfig.Get("DEV_TACHIBANA_SECOND");
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(pw))
            return "missing DEV_TACHIBANA_USER_ID / DEV_TACHIBANA_PASSWORD (process env or .env)";
        if (string.IsNullOrEmpty(secondStr))
            return "missing DEV_TACHIBANA_SECOND (process env or .env; required for order placement)";
        second = secondStr.ToCharArray();

        // demo 二重ガード: 本番フラグが立っていたら何もせず拒否（実弾防止）。
        if (Environment.GetEnvironmentVariable("TACHIBANA_ALLOW_PROD") == "1")
            return "refusing to run: TACHIBANA_ALLOW_PROD=1 is set (this gate is demo-only; never fires real orders)";

        // ── step 1: live-configured server を venue=TACHIBANA で構築（Python を直 claim） ──
        var host = new WorkspaceEngineHost();
        s_host = host;
        host.InitializePython(VENUE);                 // sys.path に ProjectRoot/VenvSite を挿入 + server 構築
        if (!host.ServerReady) return "host server not ready after InitializePython";

        // creds を os.environ へ（"env" credentials_source が tachibana.py:255 で直読みする）。
        SetOsEnviron("DEV_TACHIBANA_USER_ID", uid);
        SetOsEnviron("DEV_TACHIBANA_PASSWORD", pw);
        // 第二暗証は os.environ に入れない（R10）。char[] のまま submit_secret へ。

        bool marketOpen = MarketOpenJst();             // 診断のみ（厳密ゲートなので verdict は変えない）
        if (!marketOpen)
            Debug.LogWarning("[E2E TACHIBANA-LIVE] market appears CLOSED (前場 09:00–11:30 / 後場 12:30–15:30 JST). " +
                             "成行は場中でないと約定しないため FILLED を待てず FAIL する可能性が高い。");

        // ── step 2: venue_login("env","demo") → set_execution_mode(LiveManual) ──
        host.VenueLogin(VENUE, "env", ENV_HINT, (ok, ec) =>
        {
            s_loginOk = ok; s_loginEc = ec; Volatile.Write(ref s_loginDone, 1);
        });
        if (!SpinUntil(() => Volatile.Read(ref s_loginDone) == 1, LOGIN_TIMEOUT_MS))
            return "venue_login did not return within " + (LOGIN_TIMEOUT_MS / 1000) + "s";
        if (!s_loginOk)
            return "venue_login failed: " + s_loginEc + " (電話認証未済 / 閉局 / creds 誤り を確認)";

        // poll の連続正本で接続を確認（venue_state CONNECTED）。
        var conn = new VenueConnectionViewModel();
        if (!SpinUntil(() => { conn.ApplyStatePoll(host.LatestStateJson); return conn.IsConnected; }, CONNECT_TIMEOUT_MS))
            return "logged in but venue_state never reached CONNECTED (got " + conn.VenueState + ")";

        // ── step 2.5: EC WS (SSL ハンドシェイク含む) が成立し最初のフレームが届くまで gate ──
        // SUBSCRIBED badge は market-data 購読成立でしか立たないため、本 Runner のように
        // market-data を購読しない経路では SUBSCRIBED に到達しない。Python 側 adapter が露出する
        // ec_ws_subscribed (= EC WS で 1 フレーム以上受信した = SSL ハンドシェイク済) を独立
        // シグナルにし、SSL 失敗時 / WS 未確立時に place する前に fail-fast する。これで demo に
        // 未約定 ACCEPTED order が残置するルートを根本から塞ぐ (findings 0053 §issue#85 / 不具合 2)。
        if (!SpinUntil(() => { conn.ApplyStatePoll(host.LatestStateJson); return conn.IsConnected && conn.EcWsSubscribed; }, EC_WS_GATE_TIMEOUT_MS))
            return "EC WS never subscribed within " + (EC_WS_GATE_TIMEOUT_MS / 1000) + "s " +
                   "(venue_state=" + conn.VenueState + ", ec_ws_subscribed=" + conn.EcWsSubscribed +
                   ") — SSL ハンドシェイク失敗 / WS 未確立を疑う; state=" + host.LatestStateJson;

        // ── step 3: 成行 BUY を発注し、発注中に push される SecretRequired を第二暗証で応答 ──
        host.Lanes.SubmitPlaceOrder(VENUE, INSTRUMENT, SIDE, QTY, null, ORDER_TYPE, TIF, r =>
        {
            s_place = r; Volatile.Write(ref s_placeDone, 1);
        });

        long answeredSecrets = 0;
        int beats = 0, placeCap = PLACE_TIMEOUT_MS / PUMP_SLEEP_MS;
        while (Volatile.Read(ref s_placeDone) == 0 && beats < placeCap)
        {
            // SecretRequired は place_order がブロック中に sink へ push される。main で drain し、
            // 新しい request の edge ごとに urgent-secret lane から応答する（payload は clone を渡す）。
            host.DrainLiveEvents();
            if (host.Panel.SecretRequiredCount > answeredSecrets)
            {
                answeredSecrets = host.Panel.SecretRequiredCount;
                string reqId = host.Panel.LatestSecretRequired.RequestId;
                host.Lanes.SubmitSecret(reqId, (char[])second.Clone(), _ => { });
                Debug.Log("[E2E TACHIBANA-LIVE] answered SecretRequired (request " + reqId + ") on the urgent-secret lane.");
            }
            Thread.Sleep(PUMP_SLEEP_MS);
            beats++;
        }
        if (Volatile.Read(ref s_placeDone) == 0)
            return "place_order did not return within " + (PLACE_TIMEOUT_MS / 1000) + "s (second password not answered?)";
        if (!s_place.Success)
            return "place_order rejected: " + s_place.ErrorCode + " (ack status=" + s_place.Status + ")";
        Debug.Log("[E2E TACHIBANA-LIVE] order accepted by venue (id=" + s_place.OrderId + ", ack=" + s_place.Status + "). awaiting fill...");

        // ── step 4: EC fill push を待つ（panel.FilledOrderCount は OrderEvent.status=="FILLED" で増える） ──
        int fbeats = 0, fillCap = FILL_TIMEOUT_MS / PUMP_SLEEP_MS;
        while (host.Panel.FilledOrderCount == 0 && fbeats < fillCap)
        {
            host.DrainLiveEvents();
            Thread.Sleep(PUMP_SLEEP_MS);
            fbeats++;
        }
        if (host.Panel.FilledOrderCount == 0)
        {
            // ── 不具合 3: FILL timeout 経路 — late-fill race を救った上で cancel フォールバック ──
            // (a) cancel 発射前にもう 1 回 drain。pump cadence (50ms) と venue RTT (数百 ms) の
            //     窓で押し込まれていた late fill を拾い、PASS 経路に合流させる (Q3 verdict matrix row 4)。
            host.DrainLiveEvents();
            if (host.Panel.FilledOrderCount == 0)
            {
                // (b) cancel pump (place と同じ DrainLiveEvents + SecretRequiredCount edge → SubmitSecret パターン)。
                //     cancel も Python `cancel_order` 内部で `_resolve_secret` を呼び SecretRequired を sink へ
                //     push するため、同じ pump で包む。verdict は元の "NO FILLED" を保持し、cancel 結果を suffix に追記。
                host.Lanes.SubmitCancelOrder(VENUE, s_place.OrderId, r =>
                {
                    s_cancel = r; Volatile.Write(ref s_cancelDone, 1);
                });
                int cbeats = 0, cancelCap = CANCEL_TIMEOUT_MS / PUMP_SLEEP_MS;
                while (Volatile.Read(ref s_cancelDone) == 0 && cbeats < cancelCap)
                {
                    host.DrainLiveEvents();
                    if (host.Panel.SecretRequiredCount > answeredSecrets)
                    {
                        answeredSecrets = host.Panel.SecretRequiredCount;
                        string reqId = host.Panel.LatestSecretRequired.RequestId;
                        host.Lanes.SubmitSecret(reqId, (char[])second.Clone(), _ => { });
                        Debug.Log("[E2E TACHIBANA-LIVE] answered SecretRequired during cancel (request " + reqId + ").");
                    }
                    Thread.Sleep(PUMP_SLEEP_MS);
                    cbeats++;
                }
                // #85 code-review A#1 / B#6: cancel pump 中・直後に late fill が到着しているかを
                // もう 1 回 drain で救う。pump cadence 50ms と venue RTT 数百 ms の窓は cancel pump
                // が 45s 走る間に十分開く ⇒ 「cancel に進む寸前」と「cancel 完了直後」の 2 段階で
                // late-fill grace を効かせる。fill が到着していれば cancel 結果は best-effort
                // (CANCELED でも REJECTED でも、約定が成立しているので PASS 経路へ合流)。
                host.DrainLiveEvents();
                if (host.Panel.FilledOrderCount > 0)
                {
                    Debug.Log("[E2E TACHIBANA-LIVE] late fill arrived during cancel pump; treating as PASS path " +
                              "(cancel result was: " + (Volatile.Read(ref s_cancelDone) == 0
                                  ? "timed out"
                                  : (s_cancel.Success ? "canceled cleanly" : "rejected " + s_cancel.ErrorCode)) + ").");
                    // 後段の LatestOrder.Status / FilledQty チェックへ合流。
                }
                else
                {
                    string cancelSuffix;
                    if (Volatile.Read(ref s_cancelDone) == 0)
                        cancelSuffix = " (cancel timed out after " + (CANCEL_TIMEOUT_MS / 1000) + "s — order may still be pending; demo を確認)";
                    else if (s_cancel.Success)
                        cancelSuffix = " (order " + s_place.OrderId + " canceled cleanly; demo に残置なし)";
                    else
                        cancelSuffix = " (cancel also failed: " + s_cancel.ErrorCode + " — owner 手動 cancel 必要)";
                    return "order accepted (id=" + s_place.OrderId + ") but NO FILLED OrderEvent within " +
                           (FILL_TIMEOUT_MS / 1000) + "s" + (marketOpen ? "" : " — 市場閉局中の可能性が高い（場中に実行せよ）") +
                           cancelSuffix;
                }
            }
            else
            {
                Debug.Log("[E2E TACHIBANA-LIVE] late fill arrived during timeout grace; skipping cancel and treating as PASS path.");
            }
        }

        LiveOrderEvent ord = host.Panel.LatestOrder;
        if (ord.Status != "FILLED")
            return "latest OrderEvent status=" + ord.Status + ", expected FILLED";
        if (ord.FilledQty < QTY)
            return "filled qty " + ord.FilledQty + " < ordered " + QTY + " (partial fill not yet complete)";

        return null;   // PASS
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    // os.environ[key]=value を PyString 経由で設定（特殊文字を安全に渡す）。
    static void SetOsEnviron(string key, string value)
    {
        using (Py.GIL())
        using (PyObject os = Py.Import("os"))
        using (PyObject environ = os.GetAttr("environ"))
        using (var k = new PyString(key))
        using (var v = new PyString(value))
            environ.SetItem(k, v);
    }

    // 立花 demo の場中判定（診断のみ）。is_market_open(now_jst) を直接呼ぶ（Exec/__main__ を介さない）。
    // 失敗時は open 扱いにして verdict に影響させない。
    static bool MarketOpenJst()
    {
        try
        {
            using (Py.GIL())
            using (PyObject codec = Py.Import("engine.exchanges.tachibana_ws_codec"))
            using (PyObject dt = Py.Import("datetime"))
            using (PyObject zi = Py.Import("zoneinfo"))
            using (PyObject ziCls = zi.GetAttr("ZoneInfo"))
            using (var tokyoName = new PyString("Asia/Tokyo"))
            using (PyObject tokyo = ziCls.Invoke(tokyoName))
            using (PyObject dtCls = dt.GetAttr("datetime"))
            using (PyObject now = dtCls.InvokeMethod("now", tokyo))
            using (PyObject res = codec.InvokeMethod("is_market_open", now))
                return res.As<bool>();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[E2E TACHIBANA-LIVE] is_market_open probe failed (non-fatal): " + e.Message);
            return true;
        }
    }

    // bounded spin（main thread）。pred が true になれば true、予算超過で false。
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
