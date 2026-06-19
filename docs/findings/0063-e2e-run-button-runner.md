# findings 0063 — RunButton サーフェス E2E runner 昇格（第二波10本目）

## 概要

Strategy Editor タイトルバー ▶ Run サーフェスの回帰ゲートを `Assets/Tests/E2E/Editor/RunButtonE2ERunner.cs` として
新規著述（台本 `RunButtonE2ERunner.md`）。RUN-01..08 を `自動(E2E済)` 化。throwaway `WorkspaceUiCutoverProbe`
（`Assets/Editor`）の Section1（readiness 真理値表）/Section2（single Run entry 構造）を verbatim 移送・昇格し、
新規に SectionB（block-reason ラベル view）と SectionD（OnRun→host 配線）を著した。

## section ↔ Action ID

- **SectionA** RunReadiness 真理値表（pure `RunReadinessViewModel.Reason/Evaluate`）= **RUN-02 / RUN-04 / RUN-07**。
  `WorkspaceUiCutoverProbe.Section1` を verbatim 移送。
- **SectionB** block-reason ラベル view（`StrategyEditorRunButton.Build`→`Refresh(vm)` を bare RectTransform 下で駆動し
  `_btn.interactable`・`_status.text`/`.enabled` を reflection 観測）= **RUN-03**（新規）。
- **SectionC** single Run entry 構造（実 root 合成で `_editorRunButton`＋`RunButton` 子・footer transport 不在・startup tile
  Run 不在）= **RUN-08**。`WorkspaceUiCutoverProbe.Section2` を verbatim 移送。
- **SectionD** OnRun→host 配線（実 root＋MOCK Python）= **RUN-01 / RUN-05 / RUN-06**（新規）。

## 設計判断 — sealed host のため MOCK/spy 不可 → `_req` 観測

`WorkspaceEngineHost` は `sealed`・`TryStartRun` 非 virtual・`BackcastWorkspaceRoot._host` は具象 `readonly`
フィールド（差し替え不可）。よって台本が想定した「MOCK/spy host 差し替え」は組めない。代替として
`ReplayToHakoniwaE2ERunner` と同型に実 root を反射合成し `host.InitializePython("MOCK")` で server-ready にし、
`OnRun` を反射 invoke して host の private `_req`（`TryStartRun` が launcher 起動前に同期 set）を読む＝「受領」確認。
production 変更なし（parity-first・最小 diff）。

## vacuity 回避（delete-the-production-logic litmus）

SectionD は server-ready な同一 host 上で:
1. RUN-05（unbound notebook）→ `OnRun` → `_req` 既定（`Instruments==null`）= host 不呼出。
2. RUN-06（supplyable だが空 universe）→ `OnRun` → `_req` 既定 = host 不呼出。
3. RUN-01（valid scenario 復元）→ `OnRun` → `_req` 充填（Instruments/Start/End/Granularity/StrategyPath が `_scenario`
   由来と一致）= host 呼出。

「host が呼べる経路」を RUN-01 が同一 host 上で実証するので RUN-05/06 の負 assert は vacuous でない。
litmus:
- `OnRun` の `if (!gate.IsReady) return;` を消す → RUN-05/06 が host を呼び `_req` が埋まって FAIL。
- `OnRun` 末尾 `_host.TryStartRun(req)` を消す → RUN-01 の `_req` 充填が起きず FAIL。
- `RunReadinessViewModel.Reason` の gate 順を入替 → SectionA の precedence assert が FAIL。
- `StrategyEditorRunButton.Refresh` の `_status.enabled`/`interactable` 反映を消す → SectionB が FAIL。

SectionB は presence guard（`_btn`/`_status` 非 null）を先に置き、CanRun（interactable・status 非表示）と各 block
reason（greyed＋単一語彙 text）の両方を assert（renamed field → null → 負 assert false-green を防ぐ）。

## WorkspaceUiCutoverProbe の移送/残置の仕分け

`WorkspaceUiCutoverProbe`（`Assets/Editor`・throwaway・findings 0046）は 3 サーフェスをまたぐため全体 git mv 不可:
- **S1（U1 readiness 真理値表）→ 移送**（RunButtonE2ERunner SectionA）。
- **S2（U1/U4/U5 single Run entry）→ 移送**（RunButtonE2ERunner SectionC、helper `ButtonNames`/`FindChildButton` も同行）。
- **S3（U3 boot→File→New blank state）→ 残置**（RunButton 外のサーフェス。将来別 runner へ昇格予定）。`ComposeRoot` は
  S3 が使うため残置。

移送後 `WorkspaceUiCutoverProbe` は S3 のみのプローブになり、不要 using（`System.Collections.Generic`/`UnityEngine.UI`）を除去。
**stale-marker**: findings 0046 / 0050 が `WorkspaceUiCutoverProbe` の S1/S2 を名指している箇所は、本昇格後は
RunButtonE2ERunner SectionA/C が正本（S1/S2 は当該 probe から除去済み）。

## 検証（2026-06-19 lead 実走・確定）

- compile-only: `error CS\d+` **0 件**・`Exiting batchmode successfully` / return code 0（trim 後の WorkspaceUiCutoverProbe も含め compile OK・新 .meta 生成）。
- AFK GREEN: `-executeMethod RunButtonE2ERunner.Run` で `[E2E RUN BUTTON PASS] readiness truth table + block-reason
  label + single-entry + OnRun host wiring green.` を bash `grep -a` で **1 件確認**・FAIL 0 件・sentinel
  （`Found no leaked weakptrs` / Package Manager shutdown）あり＝executeMethod 実走（SectionD の MOCK Python init 成功）・exit 0。
- **RUN-01 は実 RED→GREEN で teeth 実証済み**: 初回 AFK で `req.StrategyPath != opened strategy`（separator mismatch）
  により RUN-01 が実 FAIL → `Path.GetFullPath` 正規化で修正 → 再走で GREEN。空虚でない（誤path なら今も FAIL）。
- 残る新規 section の RED litmus（任意・production 一時破壊→該当 section FAIL→復元→GREEN。RUN-05/06 は同一 host 上で
  RUN-01 が `_req` 充填を先に実証する構成＝構造的に非 vacuous）:
  - `BackcastWorkspaceRoot.OnRun` の `if (!gate.IsReady) { ...; return; }` をコメントアウト → SectionD RUN-05/06 FAIL（`_req` が埋まる）。
  - `BackcastWorkspaceRoot.OnRun` 末尾 `_host.TryStartRun(req);` をコメントアウト → SectionD RUN-01 FAIL。
  - `StrategyEditorRunButton.Refresh` の `_status.enabled = !string.IsNullOrEmpty(reason);` を `= false;` → SectionB FAIL。

## 既知の落とし穴 — RUN-01 path 比較は両辺 `Path.GetFullPath` で正規化

SectionD の RUN-01 `StrategyPath` 一致 assert は当初 `req.StrategyPath != StrategyPy` の生文字列比較で AFK RED:
`req.StrategyPath`（=`gate.StrategyPath`）は production が `StrategyDocument`/`MarimoNotebookDocument.Open` で
`Path.GetFullPath` 正規化するため全バックスラッシュ。一方テストの `StrategyPy` は `Application.temporaryCachePath`
が Windows でも `/` 区切りを返す＋`Path.Combine` が `\` で連結する mixed-separator。文字列等価では separator
違い（`C:/...` vs `C:\...`）だけで `!=` になる。両辺を `Path.GetFullPath` に通して比較する形へ修正（同一ファイル
identity の主張は維持、vacuous 化なし）。production は正しく正規化しており変更なし＝test 側の比較を正した。

## レビュー反映（code-review simplify ラウンド・2026-06-19）

- **null-guard 追加（Medium）**: SectionD の反射 lookup（`OnRun`/`_req`/`_isOwner`/`_host`/`ResumeLastDocumentOrDefault`/
  `OnFileOpen`/`_scenario`）に MemberInfo null-guard を追加。production rename 時に opaque NRE が outer try に
  呑まれて原因不明 FAIL になるのを防ぎ、`"... not found (renamed?)"` の明示 FAIL 文言で落とす。
- **ComposeRoot 再利用（簡約）**: SectionD 冒頭の実 root 合成（OpenScene/_font/SetSynthesizer/ResolvePaths/
  BuildWorkspace）を helper `ComposeRoot(out ty)` 呼出に畳んだ（SectionC と同一・挙動等価）。
- **section header に `Covers:` 付与**: E2E-CONVENTIONS.md の section↔Action ID 規約に合わせ各 section へ
  `// Covers: RUN-xx` を追記（コメントのみ・assert 不変）。
- **dead const 除去**: `WorkspaceUiCutoverProbe.WINDOW_ID`（未使用）を削除。
- **reorder 指摘は REFUTE**: 「RUN-01(positive) を先に・間で `_req` reset」案は採らない。`OnRun` の gate 順は
  `if (_host.IsRunning) return;` が最初（`BackcastWorkspaceRoot.OnRun` 先頭）で、RUN-01 成功は
  `host.TryStartRun` が `_running=true` を立てる（`WorkspaceEngineHost.TryStartRun`）。positive-first にすると
  後続 RUN-05/06 の `OnRun` が **running ゲートで先に early-return** し、本来検査したい no-strategy/invalid-scenario
  ゲートに到達しなくなる（＝RUN-05/06 が vacuous 化）。`_req` を反射 reset しても `_running` は消えず、`_running` を
  消すには `host.Stop()`＝server close（`ServerReady=false`）が要り「host が呼べる経路」の非 vacuity 前提が壊れる。
  よって現状の **blocked-first が host lifecycle 上の意図的設計＝正**。非 vacuity は「同一 server-ready host 上で
  RUN-01 が `_req` 充填経路を実証」＋「RUN-01 は実 RED→GREEN で teeth 実証済み（§検証）」で担保済み。

## Covers

RUN-01（host req 組立＋呼出）/ RUN-02（4 ゲート真理値表）/ RUN-03（block-reason ラベル）/ RUN-04（running ブロック）/
RUN-05（no-strategy host 不呼出）/ RUN-06（invalid-scenario host 不呼出）/ RUN-07（not-owner ブロック）/
RUN-08（single Run entry 構造）。RUN-09/10 = 要新規自動化 据え置き、RUN-11 = HITL専用、RUN-12 = 対象外（Journey）。
