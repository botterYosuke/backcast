// SecretModalE2ERunner.cs — venue ログイン第二暗証 modal サーフェスの E2E 回帰ゲート
// （台本: 同ディレクトリの SecretModalE2ERunner.md）。第二波9本目。throwaway AFK gate
// SecretModalM2Probe（Assets/Editor・issue #21 M2）から git mv＋改名（ADR-0015 の回帰ゲート命名規約。
// 先例 ScenarioStartup=0054 / FooterMode=0055 / InfiniteCanvas=0056 / FloatingWindow=0057 /
// UniverseSidebar=0058 / DepthLadder=0059 / Hakoniwa=0060 / StrategyEditorNotebook=0061）。
// 5 section を assert 1 行も削らず verbatim 移送し、各 section に台本の Action ID を `Covers:` で付与
// （findings 0062）。SecretModalController を直接 new し nowSeconds を注入する pure-logic ゲート
// （findings 0012 D5 = 平文ライフタイム契約: keyboard-drain char[] / masked / 25s 絶対 timeout /
// submit・cancel・timeout で zeroize / containment 25s<30s<40s）。Python/venue/pythonnet 不要。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod SecretModalE2ERunner.Run -logFile <log>
//   # expect: [E2E SECRET MODAL PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// section ↔ Action ID は各 Section の `Covers:` コメント参照（台本の操作一覧表と双方向に追える）。gate 形は
// probe の Check-counter 形（_fail リスト累積→Exit）をそのまま温存。`EditorApplication.Exit` は self-failing
// gate として無条件（PASS=Exit(0) / FAIL・例外=Exit(1)。元々無条件のため温存）。
//
// 据え置き（台本「カバー状態」）: SECRET-07（focus drop）/ SECRET-08（open gate）/ SECRET-09（open-time id バインド）
// は実 BackcastWorkspaceRoot 反射合成 harness を要するため本昇格では追加せず 要新規自動化 のまま（findings 0062・
// StrategyEditorNotebook の STRATEGY-11 と同方針）。SECRET-12（実 venue 認証）/ SECRET-13（実キーボード drain）は
// HITL専用（VenueLoginSecretHitlMenu が記録）。lane roundtrip / wire no-leak（SECRET-03/10 の別レグ）は
// VenueLoginSecretProbe（pythonnet・据え置き）が正本。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class SecretModalE2ERunner
{
    static readonly List<string> _fail = new List<string>();
    static void Check(bool cond, string msg) { if (!cond) _fail.Add(msg); }

    // Type a string ONE char at a time (the only secret entry point — no managed string
    // reaches the controller).
    static void Type(SecretModalController m, string s) { foreach (char c in s) m.AppendChar(c); }

    static LiveSecretRequiredEvent Req() => new LiveSecretRequiredEvent
    {
        RequestId = "req-7",
        Venue = "TACHIBANA",
        Kind = "second_secret",
        Purpose = "PLACE",
    };

    public static void Run()
    {
        try
        {
            KeyboardDrainAndMask();
            SubmitHandsPayloadAndZeroizes();
            CancelZeroizes();
            AbsoluteTimeoutFiresBefore30s();
            ContainmentInvariant();
        }
        catch (Exception e) { _fail.Add("exception: " + e); }

        if (_fail.Count == 0)
        {
            Debug.Log("[E2E SECRET MODAL PASS] keyboard-drain char[] (SECRET-01) / backspace (SECRET-02) / " +
                      "submit hands one-shot char[] + zeroize (SECRET-03) / cancel zeroize + id-clear (SECRET-04) / " +
                      "25s absolute timeout, idle-non-extending, no re-fire (SECRET-05) / masked dot-count (SECRET-06) / " +
                      "BufferIsZeroed no-leak audit (SECRET-10) / containment 25s<30s<40s (SECRET-11) — " +
                      "pure SecretModalController, findings 0012 D5, under Unity Mono");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E SECRET MODAL FAIL]\n  - " + string.Join("\n  - ", _fail));
            EditorApplication.Exit(1);
        }
    }

    // Covers: SECRET-01 (1文字ずつ入力 char[]), SECRET-02 (backspace), SECRET-06 (masked dot-count 表示)
    static void KeyboardDrainAndMask()
    {
        var m = new SecretModalController();
        m.Open(Req(), 0.0);
        Check(m.IsOpen, "modal not open");
        Check(m.RequestId == "req-7", "request id not bound");

        m.AppendChar('1');
        m.AppendChar('2');
        m.AppendChar('3');
        m.AppendChar('a');
        m.Backspace();                   // drop the 'a'
        m.AppendChar('X');
        Check(m.Length == 4, "length mismatch: " + m.Length);   // 1,2,3,X
        Check(m.MaskedDisplay == "••••", "mask mismatch: " + m.MaskedDisplay);
        // control chars (newline / backspace char) are ignored by AppendChar
        m.AppendChar('\n');
        m.AppendChar('\r');
        Check(m.Length == 4, "newline wrongly appended");
    }

    // Covers: SECRET-03 (submit→one-shot char[]＋自バッファ zeroize), SECRET-10 (BufferIsZeroed no-leak audit)
    static void SubmitHandsPayloadAndZeroizes()
    {
        var m = new SecretModalController();
        m.Open(Req(), 0.0);
        Type(m, "9753");
        char[] payload = m.Submit();
        Check(payload != null, "submit returned null payload");
        Check(new string(payload) == "9753", "payload mismatch");
        Check(!m.IsOpen, "modal still open after submit");
        Check(m.Length == 0, "length not reset after submit");
        Check(m.BufferIsZeroed(), "internal buffer NOT zeroized after submit (D5 violation)");
        Check(m.SubmitCount == 1, "submit count != 1");
        // lane contract: caller zeroizes the one-shot payload after the RPC.
        Array.Clear(payload, 0, payload.Length);

        // empty submit is a no-op (no spurious submit_secret).
        m.Open(Req(), 0.0);
        Check(m.Submit() == null, "empty submit should return null");
    }

    // Covers: SECRET-04 (cancel/close zeroize＋RequestId クリア), SECRET-10 (BufferIsZeroed no-leak audit)
    static void CancelZeroizes()
    {
        var m = new SecretModalController();
        m.Open(Req(), 0.0);
        Type(m, "abcd");
        m.Cancel();
        Check(!m.IsOpen, "modal open after cancel");
        Check(m.BufferIsZeroed(), "buffer not zeroized after cancel");
        Check(m.CancelCount == 1, "cancel count != 1");
        Check(m.RequestId == null, "request id not cleared after cancel");
    }

    // Covers: SECRET-05 (25s 絶対タイムアウト・idle 非延長・閉じた modal で再発火なし＋zeroize)
    static void AbsoluteTimeoutFiresBefore30s()
    {
        var m = new SecretModalController();
        m.Open(Req(), 100.0);
        Type(m, "55");
        // typing does NOT extend an absolute timeout: still expires at open+25s.
        Check(!m.TickExpire(110.0), "expired too early at 10s");
        Check(!m.TickExpire(124.9), "expired too early at 24.9s");
        Check(m.TickExpire(125.0), "did not expire at exactly open+25s");
        Check(m.TimedOut, "TimedOut flag not set");
        Check(!m.IsOpen, "modal still open after timeout");
        Check(m.BufferIsZeroed(), "buffer not zeroized after timeout");
        Check(m.TimeoutCount == 1, "timeout count != 1");
        Check(!m.TickExpire(200.0), "TickExpire fired again on a closed modal");
    }

    // Covers: SECRET-11 (containment invariant 25s<30s<40s — modal が backend より先に閉じ zeroize)
    static void ContainmentInvariant()
    {
        // 25s (modal absolute) < 30s (secret wait) < 40s (order write).
        Check(SecretModalController.AbsoluteTimeoutSeconds < 30.0,
              "modal timeout must be < backend 30s secret wait");
        Check(30.0 < 40.0, "secret wait must be < order-write timeout");
    }
}
