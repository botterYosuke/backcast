# 0055 — E2E 第二波 2本目: FooterModeE2ERunner 昇格

**日付**: 2026-06-19 / **ブランチ**: `e2e/replay-to-hakoniwa-runner`
**関連**: [ADR-0015](../adr/0015-e2e-runner-layout-and-script-convention.md) / [台本](../../Assets/Tests/E2E/Editor/FooterModeE2ERunner.md) /
[findings 0054](0054-e2e-scenario-startup-runner.md)（昇格の型・1本目） / [findings 0026](0026-footer-liveauto-launch.md)（footer/LiveAuto VM 本体）

## 文脈

第二波 2本目。footer（実行モード）サーフェスを `FooterLiveAutoVerify.cs`（throwaway EditMode verify）から昇格。型は
findings 0054 で確立した「throwaway probe → E2ERunner（git mv・改名・旧削除）＋(B) 自然な検証単位＋Covers＋AFK RED→GREEN」。
当初 #84（footer 可視性）churn を避けて後回しにしたが、base 取り込みで #84 確定済みのため安全に着手。

## 1本目との差分: 昇格元が 2 サーフェスをまたぐ

`FooterLiveAutoVerify` は ScenarioStartupProbe と違い **1:1 でない** — `FooterModeViewModel`（footer-mode サーフェス）と
`LiveAutoTransportViewModel`（Live 運転コントロールサーフェス＝▶ Start/Pause/Resume・double-press・G2）の 2 つを assert していた。
owner 承認の仕分け:
- **`FooterModeViewModel` ブロック（probe line 48-98）= FOOTER-01〜09**。`Check` body を 1 行も削らず温存し、divider に
  `Covers: FOOTER-xx` を付与（D1 poll authority=01/02/03、visibility=07、BlockedVenueNotLive=04、Live-lock=02/03、
  reject=08、Replay-immediate=01、D2=05、G1 `ShouldAutoReplay`=09）。
- **FOOTER-10（同一 mode 再選択=`Ignore`）= 新規 Check**（probe 未カバー）。
- **FOOTER-06/07 の view 反映 = 新規 uGUI section**: `WorkspaceFooterView` を bare RectTransform で `Build` 直呼びし、
  private `_modeSegs`（`List<(Button,Text,string)>`、ValueTuple Item1/Item3 を反射）から venue-gated `gameObject.activeSelf`（07）と
  lock 中 `Button.interactable==false`（06）を assert。これが本 runner の唯一の新規 production 観測（probe は VM のみで view 未検証だった）。
- **`LiveAutoTransportViewModel` ブロック（probe line 100-173）= SUPPORTING PIN**。footer-mode 外（Live 運転コントロールの責務）
  だが回帰網を落とさないため温存し `// SUPPORTING PIN` 明記。FOOTER Action 行には数えず、専用 runner 著述時に移送。
  ※今 relocate は2本目の範囲外（scope creep 回避）。

`Check`-counter 形（累積 _pass/_fail → Exit(1) on any fail）は実証済みなので Execute() 形へは書き換えず温存（"温存"優先）。
最終サマリのみ `[E2E FOOTER MODE PASS/FAIL]` に統一。AFK GREEN = **35 pass / 0 fail**。

## RED→GREEN（delete-the-logic litmus）

新規 uGUI section（FOOTER-06）を litmus 対象に選定:
- **RED**: `WorkspaceFooterView.RefreshModeSegments` の `btn.interactable = !locked` を一時無効化 → `[E2E FOOTER MODE] FAIL:
  FOOTER-06 view: visible segment interactable=false while locked` ＋ `[E2E FOOTER MODE FAIL] 34 pass / 1 fail`。
  **他 34 Check は PASS のまま＝新 section だけが回帰を捕捉**（非空虚）。
- **GREEN**: 復元 → `[E2E FOOTER MODE PASS] 35 pass / 0 fail`。
- compile-only（`-executeMethod` 無し）で `error CS` 0 を先に確認。

## 再走手順

```pwsh
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  -batchmode -nographics -quit -projectPath "C:\Users\sasai\Documents\backcast" `
  -executeMethod FooterModeE2ERunner.Run -logFile "$env:TEMP\fm.log"
# 期待: [E2E FOOTER MODE PASS] 35 pass / 0 fail
```

- 確認は **Bash `grep -a "E2E FOOTER MODE PASS" <log>`**（`→` 含む行を ripgrep/Select-String は取りこぼす＝memory `unity-afk-probe-run`）。

## 今回の運用の罠（3つ踏んだ・次回回避）

1. **lock-abort race**: compile-only の Unity が teardown 中にロック保持していると、直後の AFK 起動が
   `Aborting batchmode due to fatal error: another Unity instance is running` で**即 abort・logFile を作らない**。
   次の Unity を launch する前に `Get-Process Unity` が空かを確認する。
2. **recompile-skip**: production `.cs` を編集（RED 破壊／GREEN 復元）した**直後の初回** AFK 起動は再コンパイル＋domain reload で
   **-executeMethod がスキップ**され、log が起動途中（"Initializing Unity extensions" 付近）で終わる。**2 回目**（assemblies キャッシュ済み）で実行される。
3. **flush race**: background 完了通知の直後に grep すると logFile が未 flush で `0 件`に見える。**shutdown sentinel
   （`Found no leaked weakptr` / `Cleanup mono` / final summary 行）を確認してから判定**する。

## 改名の波及

- active 現行化: 台本（カバー状態 FOOTER-01〜10 → `E2E済`・既存Probe 列・本文の `FooterLiveAutoVerify` 名）、`E2E-INDEX.md`
  （ロールアップ E2E済 10 / HITL 1 / 対象外 1）。
- findings 0026（footer/LiveAuto VM 本体）は operational 再走コマンドを含むので **forward-pointer を追記**（旧名は履歴として保持）。

## 残務メモ（後続スライス）

- `LiveAutoTransportViewModel`（pin）は Live 運転コントロールサーフェスの専用 runner 著述時に移送する。
- footer の実 venue 実 Live 切替（FOOTER-11）は HITL（実 `set_execution_mode` RPC・外部認証）。
</content>
