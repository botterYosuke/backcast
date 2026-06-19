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
    const int PLACE_TIMEOUT_MS = 60000;        // place_order ack（secret resolve 30s 含む）
    const int FILL_TIMEOUT_MS = 30000;         // EC fill push（場中のみ来る）

    static WorkspaceEngineHost s_host;

    // ── cross-thread handoff（callback は worker thread で発火。Volatile で可視性を担保） ──
    static int s_loginDone;
    static bool s_loginOk;
    static string s_loginEc;
    static int s_placeDone;
    static OrderRpcResult s_place;

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
        s_loginDone = 0; s_placeDone = 0;

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
            return "order accepted (id=" + s_place.OrderId + ") but NO FILLED OrderEvent within " +
                   (FILL_TIMEOUT_MS / 1000) + "s" + (marketOpen ? "" : " — 市場閉局中の可能性が高い（場中に実行せよ）");

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
