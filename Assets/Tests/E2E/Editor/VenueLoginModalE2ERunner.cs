// VenueLoginModalE2ERunner.cs — #181 / ADR-0040 venue ログイン uGUI モーダルの C# 半分の E2E 回帰ゲート
// （台本: 同ディレクトリの VenueLoginModalE2ERunner.md）。旧 tkinter ダイアログ（別 OS ウィンドウ・macOS crash）を
// 置換した uGUI モーダルの頭脳 VenueLoginModalController を直接 new し、Python-FREE な fake submit executor で
// 「入力 → 送信 → 結果表示」を駆動する（headless 認証半分は pytest = test_venue_login_headless.py が正本）。
//
//   <Unity> -batchmode -nographics -quit -projectPath <repo> -executeMethod VenueLoginModalE2ERunner.Run -logFile <log>
//   # expect: [E2E VENUE LOGIN MODAL PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// 据え置き（HITL 専用）: 実 venue 認証（kabu verify / tachibana demo）・実モーダル表示は display/venue-bound
// （findings 0131 §HITL）。本ゲートは controller のロジック契約（password・OK gate・fields JSON・mode 再導出・
// probe 連動・PEM ピッカー seam・submit→result）＋ overlay の入力欄 affordance（VLOGIN-MODAL-12・ADR-0042）を決定論で固定する。
// delete-the-production-logic litmus: 各 section 末尾コメント参照（production 述語を壊すと当該 section が RED）。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class VenueLoginModalE2ERunner
{
    static readonly List<string> _fail = new List<string>();
    static void Check(bool cond, string msg) { if (!cond) _fail.Add(msg); }

    // ADR-0042: kabu パスワードは InputField backing の managed string。タイプ＝末尾追記。
    static void Type(VenueLoginModalController m, string s) => m.SetPassword(m.Password + s);

    static VenueLoginFormInit KabuInit(string mode, int port, string pwPrefill = "") => new VenueLoginFormInit
    {
        Venue = "KABU", InitialMode = mode, StationPort = port, ApiPasswordPrefill = pwPrefill,
    };

    static VenueLoginFormInit TachiInit(string mode, string authId = "", string keyPath = "") => new VenueLoginFormInit
    {
        Venue = "TACHIBANA", InitialMode = mode, AuthIdPrefill = authId, KeyPathPrefill = keyPath,
    };

    public static void Run()
    {
        try
        {
            KabuPasswordAndClear();            // VLOGIN-MODAL-01
            SubmitGating();                    // VLOGIN-MODAL-02
            FieldsJsonShape();                 // VLOGIN-MODAL-03
            SecretTransientAndClear();         // VLOGIN-MODAL-04
            ResultRenderingAndRetry();         // VLOGIN-MODAL-05
            ModeRefreshRederives();            // VLOGIN-MODAL-06
            StationProbeGatesOk();             // VLOGIN-MODAL-07
            PemPickerSeam();                   // VLOGIN-MODAL-08
            SubmitRoundtripViaFakeExecutor();  // VLOGIN-MODAL-09
            KabuPasswordIsRealInputField();    // VLOGIN-MODAL-12 (10/11 are HITL real-auth/real-display)
        }
        catch (Exception e) { _fail.Add("exception: " + e); }

        if (_fail.Count == 0)
        {
            Debug.Log("[E2E VENUE LOGIN MODAL PASS] kabu password + clear (VLOGIN-MODAL-01) / " +
                      "OK gate kabu+tachibana (VLOGIN-MODAL-02) / fields JSON (VLOGIN-MODAL-03) / secret transient + " +
                      "clear (VLOGIN-MODAL-04) / result render + retry/allow_retry (VLOGIN-MODAL-05) / mode re-derive " +
                      "(VLOGIN-MODAL-06) / station probe gates OK (VLOGIN-MODAL-07) / PEM picker seam (VLOGIN-MODAL-08) / " +
                      "submit→result roundtrip via Python-FREE fake executor (VLOGIN-MODAL-09) / kabu password is a real " +
                      "InputField(Password) affordance (VLOGIN-MODAL-12) — VenueLoginModalController + Overlay, " +
                      "#181/ADR-0040/ADR-0042/findings 0132, under Unity Mono");
            EmitPerIdTags();
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E VENUE LOGIN MODAL FAIL]\n  - " + string.Join("\n  - ", _fail));
            EditorApplication.Exit(1);
        }
    }

    static void EmitPerIdTags()
    {
        // per-Action-ID single-token tags so scripts/E2ERollup.ps1 picks each up (E2E-CONVENTIONS §5).
        // 01..09 + 12 are the automated gates; 10/11 are owner-HITL (real venue auth / real display), not machine-emitted.
        for (int i = 1; i <= 9; i++) Debug.Log($"[E2E VLOGIN-MODAL-{i:00} PASS]");
        Debug.Log("[E2E VLOGIN-MODAL-12 PASS]");
    }

    // Covers: VLOGIN-MODAL-01 — kabu API パスワードは managed string（InputField backing・ADR-0042）・close で clear。
    static void KabuPasswordAndClear()
    {
        var m = new VenueLoginModalController();
        m.Open("KABU", KabuInit("verify", 18081));
        Check(m.IsOpen, "kabu modal not open");
        Check(m.IsKabu, "venue not kabu");
        m.SetPassword("pw9Z");
        Check(m.Password == "pw9Z", "kabu password mismatch: " + m.Password);
        m.Close();
        Check(m.PasswordCleared(), "kabu password NOT cleared after close");
        // litmus: if Close() skipped Password="" the secret would survive → RED here.
    }

    // Covers: VLOGIN-MODAL-02 — OK 有効判定（kabu=本体起動+PW非空 / tachibana=認証ID+鍵パス非空 / busy で不可）。
    static void SubmitGating()
    {
        var k = new VenueLoginModalController();
        k.Open("KABU", KabuInit("verify", 18081));
        Check(!k.CanSubmit(), "kabu OK enabled with no station + no password");
        k.SetStationProbe(true, 18081);
        Check(!k.CanSubmit(), "kabu OK enabled with station up but empty password");
        Type(k, "pw");
        Check(k.CanSubmit(), "kabu OK still disabled with station up + password");
        k.SetBusy(true);
        Check(!k.CanSubmit(), "kabu OK enabled while busy");

        var t = new VenueLoginModalController();
        t.Open("TACHIBANA", TachiInit("demo"));
        Check(!t.CanSubmit(), "tachibana OK enabled with empty fields");
        t.SetAuthId("id"); t.SetKeyPath("");
        Check(!t.CanSubmit(), "tachibana OK enabled with key path missing");
        t.SetKeyPath("C:/k.pem");
        Check(t.CanSubmit(), "tachibana OK disabled with both fields set");
        // litmus: if CanSubmit ignored StationRunning the first kabu Check (station down) would pass → RED.
    }

    // Covers: VLOGIN-MODAL-03 — fields JSON（tachibana=auth_id/key_path・escape / kabu={}・secret は別経路）。
    static void FieldsJsonShape()
    {
        var t = new VenueLoginModalController();
        t.Open("TACHIBANA", TachiInit("demo"));
        t.SetAuthId("my\"id"); t.SetKeyPath("C:\\keys\\k.pem");
        string json = t.BuildFieldsJson();
        Check(json.Contains("\"auth_id\":\"my\\\"id\""), "auth_id not JSON-escaped: " + json);
        Check(json.Contains("\"key_path\":\"C:\\\\keys\\\\k.pem\""), "key_path not JSON-escaped: " + json);

        var k = new VenueLoginModalController();
        k.Open("KABU", KabuInit("verify", 18081));
        Check(k.BuildFieldsJson() == "{}", "kabu fields json should be empty (secret travels separately)");
    }

    // Covers: VLOGIN-MODAL-04 — TakeSecretTransient は入力 PW の char[] コピー・controller の Password は submit 成功(Close)で clear。
    static void SecretTransientAndClear()
    {
        var m = new VenueLoginModalController();
        m.Open("KABU", KabuInit("verify", 18081));
        Type(m, "9753");
        char[] transient = m.TakeSecretTransient();
        Check(new string(transient) == "9753", "transient secret mismatch");
        Check(m.Password.Length == 4, "controller password cleared too early (before result)");  // still held until result
        Array.Clear(transient, 0, transient.Length);   // host zeroizes the transient char[] after the RPC
        m.ApplyResult(new VenueLoginSubmitResult { Success = true });
        Check(!m.IsOpen, "modal not closed on success");
        Check(m.PasswordCleared(), "controller password NOT cleared after success close");
    }

    // Covers: VLOGIN-MODAL-05 — 結果表示（成功=閉じる / 失敗=閉じず status_text 赤字 + 再試行 / allow_retry=false は OK 据え置き）。
    static void ResultRenderingAndRetry()
    {
        var m = new VenueLoginModalController();
        m.Open("KABU", KabuInit("verify", 18081));
        m.SetStationProbe(true, 18081);
        Type(m, "wrong");

        // failure with allow_retry=true → stays open, shows status, OK re-enabled (password still present).
        m.SetBusy(true);
        m.ApplyResult(new VenueLoginSubmitResult { Success = false, ErrorCode = "KABU_AUTH_REJECTED",
            StatusText = "API パスワードが正しくありません", AllowRetry = true });
        Check(m.IsOpen, "modal closed on a retryable failure");
        Check(m.StatusText.Contains("API パスワード"), "status_text not surfaced: " + m.StatusText);
        Check(m.CanSubmit(), "OK not re-enabled after retryable failure");

        // failure with allow_retry=false → OK stays disabled until recheck/typing clears the block.
        m.SetBusy(true);
        m.ApplyResult(new VenueLoginSubmitResult { Success = false, ErrorCode = "KABU_API_DISABLED",
            StatusText = "API 設定を有効化してください", AllowRetry = false });
        Check(m.IsOpen, "modal closed on a non-retryable failure");
        Check(!m.CanSubmit(), "OK should stay disabled when allow_retry=false");
        // regression guard (code-review F1/ADR-0042): root's DriveVenueLoginModal calls SetPassword(PasswordText)
        // EVERY frame. A per-frame sync with UNCHANGED text must NOT clear the allow_retry=false latch (else OK
        // re-enables next frame against a do-not-retry venue). This drives the production per-frame sync path that
        // the controller-only asserts above bypass.
        m.SetPassword(m.Password);
        Check(!m.CanSubmit(), "per-frame SetPassword(unchanged) wrongly cleared the allow_retry=false latch");
        m.SetStationProbe(true, 18081);   // 再確認 clears the retry-block
        Check(m.CanSubmit(), "再確認 did not clear the retry-block");
        // litmus: if ApplyResult ignored allow_retry, the disabled-OK Check would fail → RED.
    }

    // Covers: VLOGIN-MODAL-06 — モード切替で prefill/ポート再導出（kabu=port+secret zeroize / tachibana=ID/鍵 prefill）。
    static void ModeRefreshRederives()
    {
        var k = new VenueLoginModalController();
        k.Open("KABU", KabuInit("verify", 18081, pwPrefill: "seed"));
        Check(k.Password == "seed", "kabu prefill not seeded");
        Check(k.StationPort == 18081, "kabu verify port wrong");
        k.ApplyModeRefresh("prod", KabuInit("prod", 18080));   // prod prefill is empty (ADR-0033)
        Check(k.Mode == "prod", "kabu mode not switched");
        Check(k.StationPort == 18080, "kabu prod port not re-derived");
        Check(k.PasswordCleared(), "kabu password not cleared on mode switch");

        var t = new VenueLoginModalController();
        t.Open("TACHIBANA", TachiInit("demo", authId: "demoId", keyPath: "demo.pem"));
        Check(t.AuthId == "demoId", "tachibana demo prefill missing");
        t.ApplyModeRefresh("prod", TachiInit("prod"));   // prod clears prefill (ADR-0033)
        Check(t.AuthId == "" && t.KeyPath == "", "tachibana prod prefill not cleared");
    }

    // Covers: VLOGIN-MODAL-07 — kabu 本体起動確認が OK を gate（未起動=不可 / 再確認で起動=可）。
    static void StationProbeGatesOk()
    {
        var m = new VenueLoginModalController();
        m.Open("KABU", KabuInit("verify", 18081));
        Type(m, "pw");
        m.SetStationProbe(false, 18081);   // 本体未起動
        Check(!m.CanSubmit(), "OK enabled while station down");
        m.SetStationProbe(true, 18081);    // 再確認: 起動
        Check(m.CanSubmit(), "OK not enabled after station came up");

        // tachibana には本体概念が無い＝常時 ready（Open で StationRunning=true 相当）。
        var t = new VenueLoginModalController();
        t.Open("TACHIBANA", TachiInit("demo"));
        t.SetAuthId("a"); t.SetKeyPath("b");
        Check(t.CanSubmit(), "tachibana OK should not depend on a station probe");
    }

    // Covers: VLOGIN-MODAL-08 — 秘密鍵 PEM ピッカー seam（StubFileDialog.OpenPrivateKey → SetKeyPath → CanSubmit）。
    static void PemPickerSeam()
    {
        var stub = new StubFileDialog { NextKeyResult = "C:/keys/id.pem" };
        var t = new VenueLoginModalController();
        t.Open("TACHIBANA", TachiInit("demo"));
        t.SetAuthId("id");
        Check(!t.CanSubmit(), "OK enabled before key picked");
        string picked = stub.OpenPrivateKey("C:/keys");
        Check(picked == "C:/keys/id.pem", "PEM picker did not return the path");
        Check(stub.LastKeyInitialDir == "C:/keys", "PEM picker initial dir not recorded");
        t.SetKeyPath(picked);
        Check(t.CanSubmit(), "OK not enabled after PEM picked");

        // cancel (null) leaves the field unchanged (caller ignores null).
        var stub2 = new StubFileDialog { NextKeyResult = null };
        Check(stub2.OpenPrivateKey("C:/keys") == null, "PEM picker cancel should return null");
    }

    // Covers: VLOGIN-MODAL-09 — 入力→送信→結果の roundtrip を Python-FREE な fake executor で駆動。
    // headless 認証の正本は pytest（test_venue_login_headless.py）; ここは C# 配線（submit が secret を
    // executor へ渡し、結果を ApplyResult する）だけを固定する。
    static void SubmitRoundtripViaFakeExecutor()
    {
        // fake executor: mirrors WorkspaceEngineHost.SubmitVenueLogin's contract without Python.
        Func<string, string, string, char[], VenueLoginSubmitResult> exec = (venue, mode, fieldsJson, secret) =>
        {
            string pw = new string(secret ?? Array.Empty<char>());
            Array.Clear(secret ?? Array.Empty<char>(), 0, secret?.Length ?? 0);  // host zeroizes the transient
            // "fake venue": correct password → success; else a retryable rejection.
            if (venue == "KABU" && mode == "verify" && pw == "good-pw")
                return new VenueLoginSubmitResult { Success = true };
            return new VenueLoginSubmitResult { Success = false, ErrorCode = "KABU_AUTH_REJECTED",
                StatusText = "API パスワードが正しくありません", AllowRetry = true };
        };

        // wrong password first → stays open, error shown.
        var m = new VenueLoginModalController();
        m.Open("KABU", KabuInit("verify", 18081));
        m.SetStationProbe(true, 18081);
        Type(m, "bad-pw");
        Check(m.CanSubmit(), "OK not enabled before first submit");
        m.SetBusy(true);
        var r1 = exec("KABU", m.Mode, m.BuildFieldsJson(), m.TakeSecretTransient());
        m.ApplyResult(r1);
        Check(m.IsOpen, "modal closed on wrong password");
        Check(m.StatusText.Contains("正しくありません"), "wrong-pw error not shown");

        // retype the correct password → submit → success closes the modal + clears.
        // (a real failure path keeps the previously typed chars; the user fixes them — here we clear+retype.)
        m.SetPassword("");
        Type(m, "good-pw");
        Check(m.CanSubmit(), "OK not re-enabled for retry");
        m.SetBusy(true);
        var r2 = exec("KABU", m.Mode, m.BuildFieldsJson(), m.TakeSecretTransient());
        m.ApplyResult(r2);
        Check(!m.IsOpen, "modal not closed on success");
        Check(m.PasswordCleared(), "password not cleared after successful submit");
        // litmus: if submit passed the secret by value-less stub (never the typed chars), the success
        // branch (pw=="good-pw") would never fire → modal stays open → RED.
    }

    // Covers: VLOGIN-MODAL-12 — kabu パスワードが overlay 上で「contentType=Password の生きた InputField」として描画される
    // （ADR-0042）。回帰の的: ADR-0040 §D2 の頃は背景なし Text ラベル＋keyboard hook で「入力欄が無い」に見えた。
    static void KabuPasswordIsRealInputField()
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var go = new GameObject("VenueLoginModalOverlay_Probe");
        try
        {
            var ov = go.AddComponent<VenueLoginModalOverlay>();
            ov.Build(font);
            ov.SetVisible(true);

            var kabu = new VenueLoginModalController();
            kabu.Open("KABU", KabuInit("verify", 18081));
            ov.Reflect(kabu);
            Check(ov.KabuPasswordFieldIsLivePasswordInput(),
                  "kabu password is NOT a live contentType=Password InputField (regressed to bare label?)");

            // PasswordText 読み accessor が実フィールドを指していること（root の per-frame sync が読む契約）。
            // SetPasswordText / PasswordText が「同じ _pwField」に配線されているかを確認する（別 field への typo 配線を捕捉）。
            // 注: 本 EditMode gate は overlay 単体・root の DriveVenueLoginModal per-frame sync 自体は駆動しない（HITL/統合側）。
            ov.SetPasswordText("hunter2");
            Check(ov.PasswordText == "hunter2", "overlay SetPasswordText/PasswordText not wired to the same field");

            // tachibana: kabu group (and its password field) is hidden — no spurious password input.
            var tachi = new VenueLoginModalController();
            tachi.Open("TACHIBANA", TachiInit("demo"));
            ov.Reflect(tachi);
            Check(!ov.KabuPasswordFieldIsLivePasswordInput(),
                  "kabu password field still live while venue is tachibana");
            // litmus: if MakeField dropped contentType=Password (back to a plain field/label) the first
            // Check fails → RED; this is the exact affordance the controller-only gate could not see.
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }
}
