# 0059 — E2E 第二波: DepthLadderE2ERunner 昇格（6本目）

**日付**: 2026-06-19 / **ブランチ**: `e2e/depth-ladder-runner`（`main` から分岐）
**関連**: [ADR-0015](../adr/0015-e2e-runner-layout-and-script-convention.md) / [台本](../../Assets/Tests/E2E/Editor/DepthLadderE2ERunner.md) /
[findings 0054](0054-e2e-scenario-startup-runner.md)（昇格の型・1本目） / [findings 0058](0058-e2e-universe-sidebar-runner.md)（直前5本目） /
[findings 0028](0028-depth-ladder-mainline-mount.md)（`WorkspaceDepthLadderProbe` 本体・#57） /
[findings 0024](0024-depth-ladder-view-extraction.md)（`DepthLadderView` 抽出・21行parity） /
[findings 0054-theme](0054-hakoniwa-bright-theme-roles.md)（DEPTH-04 の色ロール移行・**番号衝突あり下記**）

## 文脈

第二波の「安い昇格」枠（既存 Probe あり）。bid/ask 板 ladder サーフェスを `WorkspaceDepthLadderProbe`（throwaway AFK gate,
`Assets/Editor`）から昇格。型は findings 0054〜0058 で確立した「throwaway probe → `<Surface>E2ERunner`（**git mv**・class 改名・
PASS/FAIL タグ統一・`EditorApplication.Exit` 無条件化）＋(B) 自然な検証単位＋`Covers:`＋AFK RED→GREEN」。

**今回は git mv が実際に成立**（findings 0058 では mv 漏れで Write 退避になったが、今回は `git mv` で `.cs`＋`.cs.meta` を移送
＝**GUID 保全・履歴連続**）。`Assets/Editor/WorkspaceDepthLadderProbe.cs(.meta)` → `Assets/Tests/E2E/Editor/DepthLadderE2ERunner.cs(.meta)`。

### セッション中の base 移動（実測で検出・要記録）

handoff は「HEAD=`03548bf`・`e2e/replay-to-hakoniwa-runner`・未 push・~9 ahead」を想定していたが、`git status` で確認すると
**owner が既に `e2e/replay-to-hakoniwa-runner` を `main` へ merge＋push 済み**（HEAD=`8d1122b`＝`origin/main`・clean）。さらに
**作業着手後に sibling theme feature が `origin/main` へ merge**（`9de09fc` "Hakoniwa-isolated bright roles"）＝`behind 2`。
この repo は複数 feature 並行 flight＋owner 手動 git の前提（memory `unity-afk-probe-run` / handoff）どおり、agent 報告でなく
**git を信じて** 実態を確認。`main` で直接コミットせず `e2e/depth-ladder-runner` へ分岐 → `origin/main` を fast-forward merge して
新 base 上で authoring。

theme merge は `DepthLadderView.cs`（+32）と `BackcastWorkspaceRoot.cs`（+55）＝**本 runner の依存先**を触っていたため、
authoring 前に diff を再読。結果:
- `DepthLadderView.Render` の **21行ループ / wire順 best 追跡 / no-board placeholder / `BestBid/BestAsk/LastRow` seam は不変**
  （変更は `ApplyTheme` の色ロールとコメントのみ）＝本 runner の §2/§3/§5/§6 は無影響。
- 色ロールが `status.bid/ask/warning`→`hakoniwa_up/down/last` へ移行し、**sibling が `ThemeProbe` を更新して新ロールを assert 済み**。
  ＝**DEPTH-04 を `ThemeProbe` 据え置きにする handoff 判断を裏付け**（設計衝突は「相手の機構を相手の seam で採用」で両立解決・
  memory `grill-with-docs` の sibling-merge 調停 (a) 型）。本 runner は DEPTH-04 を扱わない。
- depth seam（`DriveDepthLadders`/`ApplyDepthLadderMode`/`RenderDepthLadders`/`DepthSignature`）は行番号がずれただけで挙動不変。
  本 runner は **名前で反射**するので行ズレ無影響。

## 昇格モデル（probe との対応と新規 section）

`WorkspaceDepthLadderProbe` の §1（price decode 共有 locator）と §2-4（mount/mode-sync/per-instrument render）を **assert 1 行も
削らず移送**。gate 形は probe の **Execute() 形**（section chain を `??` で連結・最初の失敗文字列を返す・null=PASS）を温存。

| Section | Covers | 観測 | 出自 |
|---|---|---|---|
| §1 PriceDecoder | DEPTH-07 | `InstrumentPriceDecoder` 共有 locator（decoy/null/absent/non-number/whitespace→null・malformed→FormatException） | 旧 probe §1 |
| **§2 LayoutAndReceiveOrder**（新規） | DEPTH-03, DEPTH-06 | standalone `DepthLadderView` を Build→partial+非ソート snapshot を Render→`_rowsRoot.childCount==21`・top(worst,欠損)行 "---"・best=配列先頭(99/101) | 新規 |
| §3 MountAndModeSync | DEPTH-01, DEPTH-02 | 2銘柄→各 chart タイルに ladder（chartArea の sibling）＋ChartView mount・Replay 隠/full↔Live 表/inset(-LADDER_WIDTH) | 旧 probe §2,§3 |
| **§4 ReplayDecodeSkip**（新規） | DEPTH-09 | host teardown snapshot で payload 注入→Replay で `DriveDepthLadders`→`_depthRendered` 空・板未描画（decode skip 実証） | 旧 §3 を `DriveDepthLadders` 駆動へ拡張 |
| §5 PerInstrumentRender | DEPTH-05, DEPTH-07, DEPTH-08 | X(depth+price105)/Y(no-depth)→X は board＋"LAST 105.00"・Y は placeholder（single-global leak kill） | 旧 probe §4 |
| **§6 SignatureEarlyOut**（新規） | DEPTH-10 | §5 後に LAST text へ sentinel→同一 PAYLOAD_A 再投入で skip（sentinel 残存）→価格 drift PAYLOAD_B で再描画("LAST 106.00") | 新規 |

> **据え置き**: DEPTH-04（色）= `ThemeProbe`（findings 0054 で更新済）。DEPTH-11（実ピクセル montage）= `DepthLadderHitlMenu` の HITL。

### 新規 section の非空虚性（vacuous 回避）

- **§2**: `childCount==21` と `BestBid/BestAsk` 非 null を先に Check してから wire順/"---" を assert（presence guard）。
  litmus: 固定21行ループを消すと `childCount!=21`／bid を defensive 降順 sort すると best=98 で wire順 assert が落ちる。
- **§4**: payload 注入 seam の生存（`LatestStateJson==PAYLOAD_A`）＋ drive 前 `_depthRendered` 空を precondition Check。
  litmus: `if (!isLive...) return;` を消すと Replay でも decode→`_depthRendered` 充填／板描画で落ちる。
- **§6**: 同一 payload skip（sentinel 残存）の後に **drift で再描画("LAST 106.00")** まで assert＝render 経路が live なことを
  同時に証明（no-op render では drift assert が落ちる）。

### DEPTH-09 の payload 注入 seam（Python-FREE の正当手）

Replay decode-skip を **実 `DriveDepthLadders`** で踏むには「payload が在るのに decode を skip」を示す必要がある。Python-FREE
では `_lanes==null` で `LatestStateJson` が null になり vacuous になる。そこで host の **post-logout snapshot 経路**
（`LatestStateJson => _teardownComplete ? _finalStateJson : ...`・`WorkspaceEngineHost.cs:119`・本来の正規パス）を反射で
`_finalStateJson=PAYLOAD_A` / `_teardownComplete=true` に設定して payload を供給し、§4 の finally で復元（後続 section は
`RenderDepthLadders` 直呼びで host 非依存）。`HakoniwaBaseModeProbe` の `TestPortfolioJsonOverride` と同系の edit-mode 注入。

## RED→GREEN（実走実証）

memory `e2e-wave2-runner-promotion` の「昇格元が既稼働 GREEN＋新規 section が presence guard 持ちなら RED は GREEN＋litmus 計画
で代替可」に該当するが、最も subtle な新規不変条件（DEPTH-10 signature early-out）について **実 RED を1本実走**して非空虚性を anchoring:

- **RED**: production `BackcastWorkspaceRoot.cs:1156` の `if (_depthRendered.TryGetValue(...) && prev == sig) continue;` の
  `continue` を no-op 化 → 同一 payload 再投入で再描画され sentinel が消える → **`[E2E DEPTH LADDER FAIL] S6: unchanged board
  must skip the 21-row rebuild (sentinel overwritten -> early-out gone), got 'LAST 105.00'`／UNITY exit 1**。他 section は通過。
- **GREEN**（復元後・最終ツリー）: `[E2E DEPTH LADDER PASS] ...`／UNITY exit 0／sentinel `Found no leaked weakptrs.`／`error CS` 0。
- 他新規 section の litmus は上記「非空虚性」に文書化（serial-AFK コスト管理のため physical RED は §6 の1本に集約）。

## 再走手順

```pwsh
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  -batchmode -nographics -quit -projectPath "C:\Users\sasai\Documents\backcast" `
  -executeMethod DepthLadderE2ERunner.Run -logFile "$env:TEMP\dl.log"
# 期待: [E2E DEPTH LADDER PASS] ... / exit 0
```

- compile-only ゲート: `-executeMethod` を外した同コマンドで `error CS\d+` 0 件（`FindFirstObjectByType` の CS0618 警告は旧 probe
  と同一で error ではない＝許容）。
- 確認は **Bash `grep -a "E2E DEPTH LADDER" <log>`**（`→` 等を含む行を ripgrep/`Select-String` は取りこぼす）。AFK の3罠
  （recompile-skip / flush-race=sentinel 待ち / lock-abort=`Get-Process Unity` 空確認）は memory `unity-afk-probe-run` 参照。
  実走は serial 必須（同一 compile 単位＋Unity プロジェクトロック）。

## 改名の波及（現行化済み / dangling）

- 現行化済み: 台本 `DepthLadderE2ERunner.md`（DEPTH-01/02/03/05/06/07/08/09/10 を `自動(E2E済)`・既存 Probe 列・実装済みへ・
  DEPTH-04 色ロールを `hakoniwa_*` へ）、`E2E-INDEX.md`（DepthLadder 行 `9|1|0|1|0`＋✅・rollup に6本目追記・残り未昇格から除外）。
- historical findings（0024/0028 等）の `WorkspaceDepthLadderProbe` 言及は narrative＝falsify しない（report のみ）。
  `OrderTicketE2ERunner.md:93` の「`WorkspaceDepthLadderProbe` と同型に」は設計手本参照なので残置可（昇格後も同じ root-drive 型）。

## code-review(simplify) で潰した Medium（GREEN 後の往復）

`code-review` high で **新規 section の vacuous 2 件**（Medium）を検出→修正→再 GREEN:

1. **DEPTH-06（§2）が空虚だった**: 当初 bids=`[99,98]`/asks=`[101,102]` を渡していたが、これは **canonical 順そのもの**
   （bids 降順・asks 昇順）なので defensive re-sort が no-op＝「再ソートしない」保証を破壊しても best が変わらず PASS して
   しまう（litmus コメントの「降順 sort で best→98」も算術的に誤り）。**非 canonical 順** bids=`[98,99]`（昇順）/
   asks=`[102,101]`（降順）を渡し best bid=98 / best ask=102 を assert する形へ修正（canonical へ再ソートすると 99/101 に
   flip して落ちる＝非空虚）。
2. **DEPTH-10（§6）が timestamp 除外を分離できていなかった**: `PAYLOAD_B` が price(105→106) と timestamp(11→99) を
   **両方**変えていたため、「signature は depth+last のみ・timestamp 除外」（production `DepthSignature` の明示意図）の回帰を
   捕捉できなかった。payload を `Replace` 派生に変えて **1 フィールドだけ drift** を構造保証: `PAYLOAD_B`=price のみ、
   新設 `PAYLOAD_C`=timestamp のみ。§6 を 3 段（同一→skip / ts-only→**なお skip** / price-drift→再描画）に拡張。
   litmus: `DepthSignature` に TimestampMs を戻すと ts-only 段の sentinel が消えて落ちる。
3. cleanup: `_ty` を BuildRoot ローカル化、payload 重複リテラルを `Replace` 派生で解消。

再 GREEN（fix 後）: compile `error CS` 0・`-executeMethod DepthLadderE2ERunner.Run` exit 0・PASS 行（"unchanged +
timestamp-only board skip"）・sentinel 確認。残りの reviewer 指摘（static dict の footprint 等）は Low（単発 AFK ゲートで
`EditorApplication.Exit` 即終了のため stale-state リスク無し）＝churn せず据え置き。

## owner 宛メモ（docs hygiene・本作業の範囲外）

- **findings 番号の重複**: sibling theme feature が `docs/findings/0054-hakoniwa-bright-theme-roles.md` を作り、第二波の
  `0054-e2e-scenario-startup-runner.md` と **0054 が重複**（`0053` も重複あり）。本 runner は衝突しない次番 `0059` を採番したが、
  既存重複の解消（どちらかをリネーム）は owner 判断。
</content>
</invoke>
