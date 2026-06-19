// QuitConfirmE2ERunner.cs — アプリ終了確認サーフェスの E2E 回帰ゲート
// （台本: 同ディレクトリの QuitConfirmE2ERunner.md / canonical 契約と RED→GREEN は findings 0068）。
// issue #89: 従来の autosave-on-quit を確認ダイアログ方式に置換。QuitConfirmController を直接 new し
// isDirty/isBound/pickerReturnedPath を真理値注入する pure-logic ゲート（SecretModalE2ERunner 同型・
// Python/venue/pythonnet 不要）。
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod QuitConfirmE2ERunner.Run -logFile <log>
//   # expect: [E2E QUIT CONFIRM PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。ログは UTF-8 = ripgrep で grep。
//
// section ↔ Action ID は各 Section の `Covers:` コメント参照（台本の操作一覧表と双方向に追える）。gate 形は
// Check-counter（_fail 累積→Exit）。EditorApplication.Exit は self-failing gate（PASS=Exit(0) / FAIL・例外=Exit(1)）。
//
// 据え置き（台本「カバー状態」）: QUIT-08（batchmode 抑制・latch）は実 BackcastWorkspaceRoot 反射 harness を
// 要するため本ゲートでは追加せず 要新規自動化 のまま（SecretModalE2ERunner の SECRET-07/08/09 同方針）。
// QUIT-09（実 OS ウィンドウ close・実 native picker）は HITL専用。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class QuitConfirmE2ERunner
{
    static readonly List<string> _fail = new List<string>();
    static void Check(bool cond, string msg) { if (!cond) _fail.Add(msg); }

    public static void Run()
    {
        try
        {
            RequestQuitGate();
            ChooseOutcomes();
            SaveAsResolve();
            SaveResolve();
        }
        catch (Exception e) { _fail.Add("exception: " + e); }

        if (_fail.Count == 0)
        {
            Debug.Log("[E2E QUIT CONFIRM PASS] clean→QuitNow (QUIT-01) / dirty→Confirm+IsOpen (QUIT-02) / " +
                      "Save(bound)→SaveThenQuit (QUIT-03) / Discard→QuitWithoutSave (QUIT-04,07) / " +
                      "Cancel→AbortQuit (QUIT-05) / Save(untitled)→SaveAsThenQuit + ResolveSaveAs path/cancel (QUIT-06) / " +
                      "ResolveSave saved→SaveThenQuit / failed→AbortQuit (QUIT-10 decision) — " +
                      "pure QuitConfirmController, findings 0068, under Unity Mono");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E QUIT CONFIRM FAIL]\n  - " + string.Join("\n  - ", _fail));
            EditorApplication.Exit(1);
        }
    }

    // Covers: QUIT-01 (clean→QuitNow・ダイアログなし), QUIT-02 (dirty→Confirm・IsOpen)
    static void RequestQuitGate()
    {
        // clean: ダイアログを出さず即終了。
        var clean = new QuitConfirmController();
        Check(clean.RequestQuit(false) == QuitDecision.QuitNow, "clean quit should be QuitNow");
        Check(!clean.IsOpen, "clean quit must not open the dialog");

        // dirty: 確認ダイアログを表示。
        var dirty = new QuitConfirmController();
        Check(dirty.RequestQuit(true) == QuitDecision.Confirm, "dirty quit should be Confirm");
        Check(dirty.IsOpen, "dirty quit must open the dialog");
    }

    // Covers: QUIT-03 (Save bound→SaveThenQuit), QUIT-04 (Discard→QuitWithoutSave),
    //         QUIT-05 (Cancel→AbortQuit), QUIT-07 (untitled Discard→QuitWithoutSave・isBound 非依存)
    static void ChooseOutcomes()
    {
        // QUIT-03: dirty+bound で「保存」→ SaveThenQuit・ダイアログ閉じる。
        var save = Opened();
        Check(save.ChooseSave(true) == QuitOutcome.SaveThenQuit, "bound Save should be SaveThenQuit");
        Check(!save.IsOpen, "dialog must close after Save");
        Check(save.LastOutcome == QuitOutcome.SaveThenQuit, "LastOutcome should be SaveThenQuit");

        // QUIT-04: 「保存しない」→ QuitWithoutSave（選択前に IsOpen を liveness 確認＝vacuous 回避）。
        var discard = Opened();
        Check(discard.IsOpen, "precondition: dialog open before Discard");
        Check(discard.ChooseDiscard() == QuitOutcome.QuitWithoutSave, "Discard should be QuitWithoutSave");
        Check(!discard.IsOpen, "dialog must close after Discard");

        // QUIT-05: 「キャンセル」→ AbortQuit（選択前に IsOpen を liveness 確認＝vacuous 回避）。
        var cancel = Opened();
        Check(cancel.IsOpen, "precondition: dialog open before Cancel");
        Check(cancel.ChooseCancel() == QuitOutcome.AbortQuit, "Cancel should be AbortQuit");
        Check(!cancel.IsOpen, "dialog must close after Cancel");

        // QUIT-07: untitled でも「保存しない」→ QuitWithoutSave（isBound 非依存）。
        var untitledDiscard = Opened();
        Check(untitledDiscard.ChooseDiscard() == QuitOutcome.QuitWithoutSave,
              "untitled Discard should be QuitWithoutSave (isBound-independent)");

        // Choose* は閉じた modal で no-op（AbortQuit を返す・LastOutcome を上書きしない）。
        Check(save.ChooseDiscard() == QuitOutcome.AbortQuit, "closed-dialog Choose should no-op to AbortQuit");
        Check(save.LastOutcome == QuitOutcome.SaveThenQuit, "no-op Choose must not overwrite LastOutcome");
    }

    // Covers: QUIT-06 (untitled Save→SaveAsThenQuit; ResolveSaveAs path→commit / cancel→AbortQuit・案A)
    static void SaveAsResolve()
    {
        // untitled で「保存」→ SaveAsThenQuit・ダイアログ閉じて picker へ。
        var c = Opened();
        Check(c.ChooseSave(false) == QuitOutcome.SaveAsThenQuit, "untitled Save should be SaveAsThenQuit");
        Check(!c.IsOpen, "dialog must close after untitled Save (picker takes over)");
        Check(c.LastOutcome == QuitOutcome.SaveAsThenQuit, "LastOutcome should be SaveAsThenQuit");

        // picker が path を返す → 終了続行（commit）。
        Check(c.ResolveSaveAs(true) == QuitOutcome.SaveAsThenQuit, "picker path should commit SaveAsThenQuit");
        Check(c.LastOutcome == QuitOutcome.SaveAsThenQuit, "LastOutcome after commit should be SaveAsThenQuit");

        // picker を cancel → 終了取りやめ（案A）。
        var c2 = Opened();
        c2.ChooseSave(false);
        Check(c2.ResolveSaveAs(false) == QuitOutcome.AbortQuit, "picker cancel should abort the quit (案A)");
        Check(c2.LastOutcome == QuitOutcome.AbortQuit, "LastOutcome after picker cancel should be AbortQuit");
    }

    // Covers: QUIT-10 decision (bound Save 後のデータ保護ガード — #89 の核)。配線は ChooseSave(true)→実 Save()→
    //         ResolveSave(saved) と流す。saved→終了続行 / 書込失敗(still dirty)→AbortQuit で終了中断・編集保全。
    //         実 .py 書込と IsDirty 検出自体は配線/HITL（pure gate では実 Save() を持たない）。
    static void SaveResolve()
    {
        // bound で「保存」→ SaveThenQuit・ダイアログ閉じる（QUIT-03 の遷移を前提として再確認）。
        var c = Opened();
        Check(c.ChooseSave(true) == QuitOutcome.SaveThenQuit, "bound Save should be SaveThenQuit");
        Check(!c.IsOpen, "dialog must close after bound Save");

        // 実 Save() が成功（保存後 not dirty）→ 終了続行（commit）。
        Check(c.ResolveSave(true) == QuitOutcome.SaveThenQuit, "saved Save should commit SaveThenQuit");
        Check(c.LastOutcome == QuitOutcome.SaveThenQuit, "LastOutcome after saved commit should be SaveThenQuit");

        // 実 Save() が失敗（保存後も dirty）→ 終了取りやめ（データ保護ガード・編集を失わない）。
        var c2 = Opened();
        c2.ChooseSave(true);
        Check(c2.ResolveSave(false) == QuitOutcome.AbortQuit, "failed Save should abort the quit (data-protection guard)");
        Check(c2.LastOutcome == QuitOutcome.AbortQuit, "LastOutcome after failed Save should be AbortQuit");
    }

    // dirty で開いた modal を返すヘルパ。
    static QuitConfirmController Opened()
    {
        var c = new QuitConfirmController();
        c.RequestQuit(true);
        return c;
    }
}
