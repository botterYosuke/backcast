# AddChartLadderJourneyE2ERunner — 台本（Journey / 操作網羅台帳）

`AddChartLadderJourneyE2ERunner.cs` が自動検証する owner ストーリー **「venue: kabu Station に `.env` でログイン →
7203 を +Add → Ladder 付チャートを表示」** の縦串 release gate。実装者は `.cs` と本 `.md` をセットで読む。
下位事実: [findings 0094](../../../../docs/findings/0094-add-chart-ladder-journey.md)、購読配線の方針:
[ADR-0022](../../../../docs/adr/0022-livemanual-market-data-subscription-production-wiring.md)。採番・カバー語彙・
責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)、配置は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。

> **位置づけ**: *Journey E2E*（UniverseSidebar の `[+ Add]` → Chart/DepthLadder サーフェスを跨ぐ実ユーザーストーリー）。
> このストーリーは三層に分かれ、各層の正本は別々:
> - **ログイン半分**（`.env` で実 kabu にログイン → Live 確立）= `KabuLiveE2ERunner`（KABU-LIVE-01、実 venue・本体・HITL）。
> - **DATA 半分**（`[+ Add]` → 実 subscribe → 実板が `DepthDecoder.HasDepth` で出る）= `LiveSubscribeWiringE2ERunner`（SUBWIRE-02/03、MOCK venue・full-stack）。
> - **UI 半分**（`[+ Add]` した銘柄の chart window が *Ladder 可視で spawn* する）= **本 runner**（Python-FREE・AFK）。
>
> 本 runner が埋めるのは UI 半分の死角。SUBWIRE は state JSON を decode するだけで spawn した chart window / ladder
> GameObject を一切見ない。DepthLadder（DEPTH-01/02）は `Universe.ReplaceAll` で Replay 中に ladder を建ててから mode を
> *トグル* するだけで、**Live 突入 *後* に +Add された chart** を通らない。

## 埋める死角

`BuildChartContent` は **spawn 時に `_lastLadderLive` を読んで**、+Add された chart の ladder を active+inset にするか
hidden+full-width にするかを決める（`BackcastWorkspaceRoot.cs:729`/`:741`）。Live が既に on の状態で +Add した chart は、
*次のモードトグルを待たず* その場で Ladder 可視で出なければならない。この **spawn 時の読み取り**を gate する既存テストが
無かった（SUBWIRE は state JSON だけ・DEPTH は Replay-first の既存 ladder をトグルするだけ）。

## 最重要の不変条件（litmus）

- **テスト自身は `InitializePython` を呼ばない（Python-FREE）。** 実 `AddFromPicker`（Live）は `LiveSubscribeHook` を発火するが、
  `LaneSubscribeSink.Subscribe` は `host.Lanes`（`InitializePython` 前は null）で null-guard → no-op。subscribe RPC 半分は
  本 runner では踏まない（それは SUBWIRE-02/03 の担当）。
- **spawn 時の active/inset は `_lastLadderLive` を *追従* する（定数でない）。** Section1（Live）は active+inset、Section2
  （Replay 負コントロール）は同じ +Add 経路で hidden+full-width。`BuildChartContent` が `true` 固定なら Section2 が RED、
  `false` 固定なら Section1 ADDLADDER-04 が RED ＝二 section で「spawn 時状態は `_lastLadderLive` 追従」をピンする。
- **`[+ Add]` は実 production 入口を駆動**（`UniverseSidebarController.AddFromPicker`＝SIDEBAR-14 経路）。`scenario.AddInstrument`
  ショートカットではなく、`Universe.Changed`→`SyncChartWindowsToUniverse`→`SpawnChartWindowAt`→`BuildChartContent` を本番どおり通す。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| ADDLADDER-01 | Live 突入後（空 universe）に板表示準備が整う | `FooterModeViewModel.ApplyPoll`→`BackcastWorkspaceRoot.DriveDepthLadders:1261` | 空 universe で chart 0／ladder 0→LiveManual poll→`DriveDepthLadders`→`_lastLadderLive`=true | `_lastLadderLive` 反射で false→true、precondition 0 chart/0 ladder | 自動(E2E済) | — |
| ADDLADDER-02 | 7203 を `[+ Add]` で universe 追加→chart window が spawn | `UniverseSidebarController.AddFromPicker:84`→`Universe.Changed`→`SyncChartWindowsToUniverse:963`→`SpawnChartWindowAt` | `AddFromPicker(7203,Live)`→`Universe.Ids` に 7203／`_dockWindows.RectOf("chart:7203.TSE")≠null` | 実 AddFromPicker を駆動、membership＋window spawn を assert | 自動(E2E済) | — |
| ADDLADDER-03 | spawn した chart tile が ChartView＋chartArea＋sibling DepthLadderView を構成 | `BackcastWorkspaceRoot.BuildChartContent:722` | `_chartViews[7203]`/`_chartAreas[7203]`/`_depthLadders[7203]` 非null、ladder は chartArea の sibling、ChartView は chartArea 上 | 反射 dict＋transform 親子を assert | 自動(E2E済) | `DepthLadderE2ERunner`（DEPTH-01 同型・ReplaceAll 経路） |
| ADDLADDER-04 | **Live 突入後に +Add した chart は spawn 時点で Ladder 可視＋chart inset** | `BackcastWorkspaceRoot.BuildChartContent:729,741`（`_lastLadderLive` 読み取り） | `_depthLadders[7203].gameObject.activeSelf=true`／`_chartAreas[7203].offsetMax.x=-LADDER_WIDTH`、2 銘柄目も同様（per-spawn） | 反射で activeSelf＋offsetMax を assert | 自動(E2E済) | — |
| ADDLADDER-05 | Replay で +Add した chart は Ladder 非表示＋chart 全幅（非 vacuity 負コントロール） | `BackcastWorkspaceRoot.BuildChartContent:729,741`（`_lastLadderLive`=false） | Live 未突入で `AddFromPicker(7203,Replay)`→window は spawn するが `activeSelf=false`／`offsetMax.x=0` | 反射で hidden＋full-width を assert | 自動(E2E済) | — |
| ADDLADDER-06 | `.env` の情報で実 kabu Station にログイン→Live 確立 | `WorkspaceEngineHost.VenueLogin("KABU","env","verify")`→`KabuStationAdapter.login(env)` | `venue_state=CONNECTED` 収束 | 実 kabu 本体＋API＋`DEV_KABU_API_PASSWORD` が要る | 自動(E2E済・KabuLiveE2ERunner) | `KabuLiveE2ERunner`（KABU-LIVE-01） |
| ADDLADDER-07 | `[+ Add]` した銘柄が実 subscribe され実板が出る | `LiveSubscribeWiringE2ERunner`（SUBWIRE-03） | `AddFromPicker(D,Live)`→実 subscribe→`HasDepth(D)=true` | MOCK venue で full-stack | 自動(E2E済・LiveSubscribeWiringE2ERunner) | `LiveSubscribeWiringE2ERunner` |
| ADDLADDER-08 | +Add したタイルの Ladder が Live↔Replay で表示/非表示トグル | `BackcastWorkspaceRoot.ApplyDepthLadderMode:1283` | mode 切替で `activeSelf`＋`offsetMax` が反転 | DepthLadder DEPTH-02 が正本（ReplaceAll 経路で同一述語） | 自動(E2E済・DepthLadderE2ERunner) | `DepthLadderE2ERunner`（DEPTH-02） |
| ADDLADDER-09 | 実 kabu venue の実板で Ladder を目視（場中・本体） | `DepthLadderHitlMenu` / `KabuLiveE2ERunner`（KABU-LIVE-02） | 実 PUSH 板で 21 行 ladder が実描画 | 実 venue・場中・本体・実ピクセルが要る | HITL専用（実 venue・場中・本体） | `KabuLiveE2ERunner`（KABU-LIVE-02）／`DepthLadderHitlMenu` |

## litmus（delete-the-production-logic）

- `BuildChartContent:741` の `ladderAreaGo.SetActive(_lastLadderLive)` を `SetActive(false)` 固定にする → **ADDLADDER-04 RED**（Live で +Add しても ladder hidden）。
- 同 `SetActive(true)` 固定にする → **ADDLADDER-05 RED**（Replay でも ladder 可視＝負コントロール崩壊）。
- `BuildChartContent:729` の `_lastLadderLive ? -LADDER_WIDTH : 0f` を `0f` 固定にする → **ADDLADDER-04 RED**（inset しない）。
- `BackcastWorkspaceRoot.cs:394` の `_scenario.Universe.Changed += SyncChartWindowsToUniverse` を消す → **ADDLADDER-02 RED**（+Add しても chart が spawn しない）。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod AddChartLadderJourneyE2ERunner.Run -logFile <abs log>
# expect: [E2E ADD CHART LADDER JOURNEY PASS] / exit=0  （確認は Bash `grep -a "ADD CHART LADDER"`）
# compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
# ランチャ経由: pwsh scripts/run-live-e2e.ps1 -Method AddChartLadderJourneyE2ERunner.Run
```
