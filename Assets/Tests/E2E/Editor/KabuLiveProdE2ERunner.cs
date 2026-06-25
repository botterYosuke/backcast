// KabuLiveProdE2ERunner.cs — kabu (kabuステーション) 本番 (prod 18080)「実ログイン→CONNECTED」の HITL E2E
// （台本: 同ディレクトリ KabuLiveProdE2ERunner.md / 方針: ADR-0027 / findings 0109）。
//
//   <Unity> -batchmode -nographics -quit -projectPath . \
//           -executeMethod KabuLiveProdE2ERunner.Run -logFile <log>
//   # expect: [E2E KABU-LIVE-PROD-01 PASS] ... / exit=0
//
// ⚠️ owner HITL / Windows 限定・本番接触: kabuステーション本体を**本番モード**で起動・ログイン済み・API 有効
// (localhost:18080 本番ポート) にし、PROD_KABU_API_PASSWORD= 本体の API パスワードを env / .env に置く。
// 揃っていなければ即 FAIL ではなく skip 扱いの理由を返して exit 0 はしない — 二重ガードで通常 CI では走らせない。
//
// ADR-0027 D4 の「自動 runner は verify/demo 固定で本番非接触」に対する**唯一の意図的例外**。
// owner が報告した「Connect kabuStation (Prod) のログインがエラーになる」を本番経路で回帰ゲートする
// （findings 0109）。本番口座で発注/購読を走らせないため、検証は login→CONNECTED の確立までに限定する
// （板/depth の live 購読は verify 固定の KabuLiveE2ERunner が担う）。kabu に第二暗証は無い（X-API-KEY のみ・R3）。

using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class KabuLiveProdE2ERunner
{
    const string VENUE = "KABU";
    const string ENV_HINT = "prod";              // 本番 18080。意図的に prod を叩く唯一の runner（findings 0109）
    const string ENV_PASSWORD = "PROD_KABU_API_PASSWORD";  // findings 0109 / D2: prod は verify と別パスワード
    const int PUMP_SLEEP_MS = 50;
    const int LOGIN_TIMEOUT_MS = 30000;
    const int CONNECT_TIMEOUT_MS = 15000;

    static WorkspaceEngineHost s_host;
    static int s_loginDone;
    static bool s_loginOk;
    static string s_loginEc;

    public static void Run()
    {
        string fail = null;
        bool skipped = false;
        try { fail = Execute(out skipped); }
        catch (Exception e) { fail = "driver: " + e; }
        finally
        {
            try { s_host?.Stop(); }
            catch (Exception e) { Debug.LogWarning("[E2E KABU-LIVE-PROD] host.Stop failed (non-fatal): " + e.Message); }
        }

        if (skipped)
        {
            // 前提未充足は SKIP（rollup で中立）。owner HITL でのみ前提が揃う。
            Debug.Log("[E2E KABU-LIVE-PROD-01 SKIP] " + fail);
            EditorApplication.Exit(0);
        }
        else if (fail == null)
        {
            Debug.Log("[E2E KABU-LIVE-PROD-01 PASS] logged into kabu PROD (18080) via PROD_KABU_API_PASSWORD "
                    + "and venue_state reached CONNECTED.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E KABU-LIVE-PROD-01 FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Execute(out bool skipped)
    {
        skipped = false;
        s_loginDone = 0;

        // ── step 0: prod creds（API パスワードのみ・findings 0109 / D2）。 ──
        string apiPw = EnvConfig.Get(ENV_PASSWORD);
        if (string.IsNullOrEmpty(apiPw))
        {
            skipped = true;
            return ENV_PASSWORD + " 未設定 — owner HITL 専用 (prod login をスキップ)";
        }

        // ── step 1: live-configured server を venue=KABU で構築。 ──
        var host = new WorkspaceEngineHost();
        s_host = host;
        host.InitializePython(VENUE);
        if (!host.ServerReady)
        {
            skipped = true;
            return "host server not ready after InitializePython (prod kabuステーション本体 18080 起動・本番モードを確認)";
        }
        SetOsEnviron(ENV_PASSWORD, apiPw);   // credentials_source="env"+env=prod が os.environ から直読み

        // ── step 2: venue_login("env","prod") → CONNECTED ──
        host.VenueLogin(VENUE, "env", ENV_HINT, (ok, ec) =>
        {
            s_loginOk = ok; s_loginEc = ec; Volatile.Write(ref s_loginDone, 1);
        });
        if (!SpinUntil(() => Volatile.Read(ref s_loginDone) == 1, LOGIN_TIMEOUT_MS))
            return "venue_login did not return within " + (LOGIN_TIMEOUT_MS / 1000) + "s (prod 本体 未起動/未ログイン?)";
        if (!s_loginOk)
            return "venue_login failed: " + s_loginEc + " (本体ログイン状態 / PROD_KABU_API_PASSWORD / 本番モードを確認)";

        var conn = new VenueConnectionViewModel();
        if (!SpinUntil(() => { conn.ApplyStatePoll(host.LatestStateJson); return conn.IsConnected; }, CONNECT_TIMEOUT_MS))
            return "logged in but venue_state never reached CONNECTED (got " + conn.VenueState + ")";

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
