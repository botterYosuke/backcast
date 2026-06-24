# findings 0094 — AddChartLadder Journey E2E（.env kabu ログイン → 7203 +Add → Ladder 付チャート表示）

owner ストーリー「venue: kabu Station に `.env` でログイン → 7203 を `+Add` → Ladder 付チャートを表示」を backcast の
E2E 回帰ゲートに落とした記録。`/behavior-to-e2e` 2026-06-24。

## 挙動（1 文の不変条件）

**Live が既に on の状態で `[+ Add]` した chart は、その spawn 時点で Ladder が可視（active）で chart が
LADDER_WIDTH だけ inset して出る** — 次のモードトグルを待たない。これは `BackcastWorkspaceRoot.BuildChartContent`
が **spawn 時に `_lastLadderLive` を読む**（`:729` の chartArea.offsetMax、`:741` の `ladderAreaGo.SetActive`）ことで成立する。

## 棚卸し（既存カバレッジと死角）

ストーリーは三層に分かれ、二層は既存ゲートが正本だった:

| 層 | 正本 | 状態 |
|---|---|---|
| ログイン（`.env` 実 kabu → Live） | `KabuLiveE2ERunner`（KABU-LIVE-01） | 既存・実 venue/本体・HITL |
| DATA（`[+ Add]`→実 subscribe→実板 `HasDepth`） | `LiveSubscribeWiringE2ERunner`（SUBWIRE-02/03） | 既存・MOCK venue・full-stack |
| **UI（`[+ Add]` した chart が Ladder 可視で spawn）** | — | **死角** |

死角の根拠:
- `LiveSubscribeWiringE2ERunner` は `DepthDecoder.Decode(state).HasDepth` で **state JSON を decode するだけ**——
  spawn した chart window も ladder GameObject も一切見ない。
- `DepthLadderE2ERunner`（DEPTH-01/02）は `Universe.ReplaceAll` で **Replay 中に** ladder を建ててから mode を
  *トグル*する。**Live 突入 *後* に `[+ Add]` された chart** の spawn 経路を通らない。
- ⇒ `BuildChartContent` の **spawn 時 `_lastLadderLive` 読み取り**を gate するテストが無かった。

## 著したゲート

**`Assets/Tests/E2E/Editor/AddChartLadderJourneyE2ERunner.{cs,md}`**（Journey・Python-FREE・AFK）。実
`BackcastWorkspaceRoot` を反射合成し、実 production の `[+ Add]` 入口（`UniverseSidebarController.AddFromPicker`＝
SIDEBAR-14 経路）を駆動して `Universe.Changed`→`SyncChartWindowsToUniverse`→`SpawnChartWindowAt`→`BuildChartContent`
を本番どおり通す。

- Python-FREE の根拠: `AddFromPicker(…,Live)` は `LiveSubscribeHook` を発火するが、`LaneSubscribeSink.Subscribe` は
  `host.Lanes`（`InitializePython` 前は null）で null-guard → no-op。subscribe RPC 半分は踏まない（SUBWIRE が担当）。
- Live 突入は `FooterModeViewModel.ApplyPoll("{execution_mode:LiveManual,venue_state:CONNECTED}")` → 反射
  `DriveDepthLadders()` で `_lastLadderLive=true` を本番経路で latch（0 ladder を harmless にループ）。

| Action ID | section | 不変条件 |
|---|---|---|
| ADDLADDER-01 | S1 | 空 universe → Live 突入で `_lastLadderLive` false→true |
| ADDLADDER-02 | S1 | `AddFromPicker(7203,Live)` → universe membership + chart window spawn |
| ADDLADDER-03 | S1 | tile が ChartView + chartArea + sibling DepthLadderView を構成 |
| ADDLADDER-04 | S1 | **spawn 時 ladder ACTIVE + chart inset（2 銘柄 per-spawn）** ← 中核 |
| ADDLADDER-05 | S2 | Replay 負コントロール: 同 `+Add` 経路で ladder HIDDEN + chart 全幅 |

ADDLADDER-06/07/08（ログイン / DATA / mode トグル）は別 Runner 正本を台本で参照、09 は HITL。

## RED→GREEN（delete-the-production-logic litmus）

`BuildChartContent:741` の `ladderAreaGo.SetActive(_lastLadderLive)` を `SetActive(false)` に改変して実走:

```
[E2E ADDLADDER-01 PASS] ...
[E2E ADDLADDER-02 PASS] ...
[E2E ADDLADDER-03 PASS] ...
[E2E ADD CHART LADDER JOURNEY FAIL] S1 ADDLADDER-04: ladder spawned HIDDEN despite Live entered before +Add
```
＝ADDLADDER-04 が **RED**（ladder が Live でも hidden）。手前の 01–03 は到達済みで PASS タグが立つ（§5 到達マイルストン）。

revert（`SetActive(_lastLadderLive)` に戻す）後の実走 = **GREEN**:
```
[E2E ADDLADDER-01..05 PASS] / [E2E ADD CHART LADDER JOURNEY PASS] / exit 0 / error CS 0 件
E2E Action-ID Rollup: 5 PASS / 0 FAIL / 0 SKIP / 5 total
```

非 vacuity の対称性: `SetActive(true)` 固定なら **ADDLADDER-05（Replay 負コントロール）が RED**、`SetActive(false)`
固定なら **ADDLADDER-04 が RED**。二 section で「spawn 時 active/inset は `_lastLadderLive` を *追従*する（定数でない）」をピン。

## 再走手順

```
pwsh scripts/run-live-e2e.ps1 -Method AddChartLadderJourneyE2ERunner.Run
# expect: [E2E ADD CHART LADDER JOURNEY PASS] / exit 0 / rollup に ADDLADDER-01..05 = PASS
# 確認は Bash: grep -a "ADDLADDER" Temp/Unity_E2E.log
```
（この dev 環境は `pwsh` 未導入で Windows PowerShell 5.1 のみ＝`& .\scripts\run-live-e2e.ps1 -Method …` で起動。）

## HITL（owner 専用）

ストーリーのログイン半分（`.env` 実 kabu ログイン）＋実板 Ladder 目視は KABU-LIVE-01/02（実 kabu 本体・API 有効・
`DEV_KABU_API_PASSWORD`・場中）。`pwsh scripts/run-live-e2e.ps1 -Venue kabu` で実走（owner 手元）。
