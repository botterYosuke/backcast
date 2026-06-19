// QuitConfirmController.cs — issue #89「終了時の確認ダイアログ」(durable tier)
//
// アプリ終了確認の純粋な判定ロジック（findings 0068）。従来の autosave-on-quit（無言で開いている
// ドキュメントを .py 上書き保存）を、明示的な「保存 / 保存しない / キャンセル」プロンプトに置換する。
//
// このオブジェクトは UnityEngine 参照も notebook 参照も時間も持たない pure logic で、入力
// （isDirty / isBound / pickerReturnedPath）はすべて引数注入される——だから AFK ゲートが真理値で
// 決定的に駆動できる（SecretModalController 踏襲）。実 Save()/SaveAs()・native picker・
// Application.Quit()、および「2 回目の wantsToQuit が true を返す」ための _quitConfirmed latch は、
// 所有側 MonoBehaviour（BackcastWorkspaceRoot）が配線する。本 controller は判定だけを行う。
//
//   * RequestQuit(isDirty): 非 dirty → QuitNow（ダイアログ出さない）／ dirty → Confirm（IsOpen=true）。
//   * ChooseSave(isBound): bound → SaveThenQuit（terminal）／ untitled → SaveAsThenQuit（後続 ResolveSaveAs を要する）。
//   * ChooseDiscard(): → QuitWithoutSave（isBound 非依存・保存せず終了続行）。
//   * ChooseCancel(): → AbortQuit（終了中断・ドキュメント据え置き）。
//   * ResolveSave(saved): ChooseSave(true) で閉じた後、配線が実 Save() を走らせた結果を解決する standalone
//     resolver（IsOpen ガードはしない）。saved → SaveThenQuit（終了続行）／ 書込失敗で still dirty → AbortQuit
//     （データ保護ガード＝編集を失わず終了を中断。これが #89 の核）。ResolveSaveAs と対称。
//   * ResolveSaveAs(pickerReturnedPath): 案A。ChooseSave(false) がダイアログを閉じた後の picker 結果を
//     解決する standalone resolver（IsOpen ガードはしない）。path あり → SaveAsThenQuit（終了続行）／
//     picker cancel → AbortQuit（終了取りやめ・ダイアログ再表示なし）。
//   * IsOpen / LastOutcome は AFK が真理値で叩ける観測フィールド。Choose* は IsOpen==false のとき no-op
//     （AbortQuit を返す＝「何もしない・終了しない」の安全な既定）。

public enum QuitDecision { QuitNow, Confirm }
public enum QuitOutcome { SaveThenQuit, SaveAsThenQuit, QuitWithoutSave, AbortQuit }

public class QuitConfirmController
{
    public bool IsOpen { get; private set; }
    public QuitOutcome LastOutcome { get; private set; }

    /// OS close / 終了要求。非 dirty → QuitNow（ダイアログなし）。dirty → Confirm（ダイアログを開く）。
    public QuitDecision RequestQuit(bool isDirty)
    {
        if (!isDirty) return QuitDecision.QuitNow;
        IsOpen = true;
        return QuitDecision.Confirm;
    }

    /// 「保存」押下。bound → SaveThenQuit（terminal）／ untitled → SaveAsThenQuit（後続 ResolveSaveAs を要する）。
    public QuitOutcome ChooseSave(bool isBound)
    {
        if (!IsOpen) return QuitOutcome.AbortQuit;
        var outcome = isBound ? QuitOutcome.SaveThenQuit : QuitOutcome.SaveAsThenQuit;
        Close(outcome);
        return outcome;
    }

    /// 「保存しない」押下（isBound 非依存）: 編集を破棄して終了続行。
    public QuitOutcome ChooseDiscard()
    {
        if (!IsOpen) return QuitOutcome.AbortQuit;
        Close(QuitOutcome.QuitWithoutSave);
        return QuitOutcome.QuitWithoutSave;
    }

    /// 「キャンセル」押下: 終了を中断し、ドキュメントを据え置く。
    public QuitOutcome ChooseCancel()
    {
        if (!IsOpen) return QuitOutcome.AbortQuit;
        Close(QuitOutcome.AbortQuit);
        return QuitOutcome.AbortQuit;
    }

    /// Save 後処理（データ保護ガード・#89 の核）。ChooseSave(true) が既にダイアログを閉じた後に配線が実
    /// Save() を走らせ、その成否（保存後も dirty なら失敗）をここで解決する standalone resolver（IsOpen ガード
    /// はしない）。saved → SaveThenQuit（終了続行）／ 書込失敗 → AbortQuit（編集を失わず終了を中断）。ResolveSaveAs と対称。
    public QuitOutcome ResolveSave(bool saved)
    {
        var outcome = saved ? QuitOutcome.SaveThenQuit : QuitOutcome.AbortQuit;
        LastOutcome = outcome;
        return outcome;
    }

    /// Save As 後処理（案A）。ChooseSave(false) が既にダイアログを閉じた後に配線が native picker を開き、
    /// その結果をここで解決する standalone resolver（IsOpen ガードはしない）。path あり → 終了続行 /
    /// picker cancel → 終了取りやめ（ダイアログは再表示しない）。
    public QuitOutcome ResolveSaveAs(bool pickerReturnedPath)
    {
        var outcome = pickerReturnedPath ? QuitOutcome.SaveAsThenQuit : QuitOutcome.AbortQuit;
        LastOutcome = outcome;
        return outcome;
    }

    void Close(QuitOutcome outcome)
    {
        IsOpen = false;
        LastOutcome = outcome;
    }
}
