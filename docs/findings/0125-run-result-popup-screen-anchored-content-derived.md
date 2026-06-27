# findings 0125 — run_result を dock plane から screen-anchored 右上ポップアップへ（content-derived 表示＋manual close latch）

**方針: ADR-0037**（#138 / findings 0110 §7 の `DriveRunResult` 決定を supersede）。実装 issue: **#172**（ポップアップ cutover・content-derived 表示・S1–S5）＋ **#173**（× close ＋ dismiss latch・§F1 / S6–S7）。本 finding は設計の木と codebase 裏取りを固定する slice 記録。

> 番号注記: issue #172/#173 本文は「方針: ADR-0037 / findings 0125」と名指していたが、オープン時点では ADR-0037 も findings 0125 も repo に未作成だった（次空き番号の予約）。本 finding は `ls docs/findings/ | sort` の次空き番号 0125 で採番し、ADR も次空き 0037 で起こした（grill deliverable＝合意済み設計を docs に固定）。

## 要求（owner・2026-06-27）

run_result（戦略 run の成績サマリ）を「奥プレーン `DockLayer`（1.0倍パララックス）の常設 base dock panel」から、**run データがあるときだけ右上に浮く screen-anchored ポップアップ card** へ cutover する。

- ポップアップは `ScreenSpaceOverlay` Canvas 直下（infinite canvas の `Content` の子では **ない**）＝ pan しても screen 座標が動かない（3D 空間＝パララックス層から除外）。
- canvas に重ねて浮く（ガター予約なし）・固定サイズ・drag/resize 不可（title ＋ × close のみ）。
- 表示は content-derived（#172）: run データがあるとき出現・honest-empty で消える。Replay（running / complete）と LiveAuto（telemetry）で出る。LiveManual は出ない。
- × close ＋ dismiss latch（#173）: × で閉じると次 run まで再出現しない。再 arm は新 run の rising edge。Replay と LiveAuto で対称に保つ。

## codebase 裏取り（grill F1–F9・2026-06-27）

すべて `C:\Users\sasai\Documents\backcast` で実コード確認済み（grill の「主張はコードで裏取り」規律）。

- **F1 run_result の現状 = dock base singleton。** `FloatingWindowCatalog.cs:35` `KIND_RUN_RESULT = "run_result"`、`:99-102` の `Default()` factory spec（`defaultSize 380×220` / `minSize 240×140` / `accent players.Get(6)` / `closeable:false`）。`DockShape.cs:43-48` `IsDockKind` が run_result を含む（chart / buying_power / orders / positions / run_result）。`BackcastWorkspaceRoot.cs:50-64` `BaseDockWindowIds`＝4 要素 `{buying_power, orders, positions, run_result}`、`:1031-1043` `SpawnBaseDockWindows` が back plane（`_dockWindows`/`_dockLayer` 1.0×）へ spawn。`:1052` `FormFactoryBaseGroup() => _dockWindows.FormGroup(BaseDockWindowIds)`。
- **F2 `IsCoreKind` は ADR-0024 以降 production 未参照（dead code）。** `DockShape.cs:56-57` `IsCoreKind(kind) => kind == KIND_RUN_RESULT`。`:52` コメント「ADR-0024 §1 since RETIRED the Hakoniwa special, so IsCoreKind now only feeds legacy/diagnostic paths」。call site は production に無し（`BackcastWorkspaceRoot.cs:60` のコメント言及のみ）。run_result 退役で **空集合**化できる（simplify）。
- **F3 content drive の format 関数と poll 経路。** `BackcastWorkspaceRoot.cs`: `FormatRunResult(vm)`（:2206-2218・LiveAuto・`vm.HasLifecycle` で `run=<RunId> <Status>`、`vm.HasTelemetry` で realized/unrealized/orders/fills、両無で `(no run)`）／ `FormatReplayRunResultRunning(snap)`（:2260-）／ `FormatReplayRunResultComplete(RunResult)`（:2270-）。poll: `PushLiveTiles`（:1625-1640・`_runResultView.Refresh(p)`）／ `PushReplayTiles`（:1647-1678・`IsNullOrWhiteSpace(portfolioJson)` の honest-empty 枝で `_runResultView.ShowReplayEmpty()`、非空で `ShowText(running ?? complete)`）。
- **F4（issue 本文の「LiveManual で自然に出ない」を訂正・load-bearing）`LivePanelViewModel` の `HasLifecycle`/`HasTelemetry` は process 内で 1 度生成・決して reset されない sticky。** `WorkspaceEngineHost.cs:60` `readonly LivePanelViewModel _panel = new LivePanelViewModel();`（唯一の生成・差し替え/Clear 無し）。`LivePanelViewModel.cs:66-75` `Apply` は lifecycle/telemetry を受けると `HasLifecycle=true`/`HasTelemetry=true` を立てるだけで、**false へ戻す経路が存在しない**（grep 全走で reset 0 件）。∴ LiveAuto run の後 LiveManual へ flip すると sticky フラグが立ったまま → `hasContent = HasLifecycle || HasTelemetry` だけだと **stale な LiveAuto telemetry がポップアップに漏れる**。これはまさに #138 `DriveRunResult` の存在理由（`BackcastWorkspaceRoot.cs:1791-1811` §7 コメント「would otherwise show STALE LiveAuto telemetry (LivePanelViewModel.Apply never resets the flags)」）。**よって popup の live hasContent には mode ゲートが必須**（D3）。
- **F5 `FormGroup` は live メンバ ≥2 で group を mint。** `FloatingWindowController.cs:1341-1352`：`_windows` dict に在る非空 id を数え `if (live < 2) return null`、`MintGroupId()` を全メンバへ `SetGroupId`。run_result を base から外しても残 3 窓で閾値を満たす。
- **F6 screen-anchored overlay の正本パターン = `BuildAddCellButton`。** `BackcastWorkspaceRoot.cs:1220-1249`：`new GameObject(..., typeof(Canvas), typeof(GraphicRaycaster))` を `transform`（＝BackcastWorkspaceRoot・**`Content` ではない**）直下に `SetParent(transform, false)`、`canvas.renderMode = ScreenSpaceOverlay`、`canvas.sortingOrder = 200`（コメント「above the scene canvas (menu/footer @0), below the secret modal」）、右下アンカー（`anchorMin/Max = (1,0)` / `anchoredPosition (-20, 56)`）。secret/modify/settings modal も同様に `transform` 直下の overlay（:596-673）。**popup はこのパターンで右上（`anchorMin/Max = (1,1)`・`pivot (1,1)`）に置く。**
- **F7（F4 の帰結）`DriveRunResult` の hide-in-LiveManual は popup でも必要——ただし method ではなく content gate で。** 現状 `DriveRunResult`（`:1812-1819`）は `_dockWindows.RectOf(run_result)` を `SetActive(!liveManual)` する。popup 化後は dock tile が無いのでこの method は退役するが、その契約（LiveManual で出さない＝stale telemetry を見せない）は **live hasContent 述語 `(HasLifecycle || HasTelemetry) && DisplayMode == LiveAuto`** に畳んで保持する。`DisplayMode == LiveAuto` の判定は既存 `_footerMode.DisplayMode == FooterModeViewModel.LiveAuto`（`DriveRunResult` 内の `LiveManual` 判定と対）を流用。
- **F8 run_id の供給源。** LiveAuto: `LiveLifecycleEvent.RunId`（`LiveBackendEventDecoder.cs:53/204` `RunId = d.run_id`）と telemetry の `run_id`（`:61/218`）。`FormatRunResult` も `vm.LatestLifecycle.RunId` を表示（`BackcastWorkspaceRoot.cs:2209`）。footer の `LiveAutoTransportViewModel.cs:93` も `LifecycleRunId => _panel.HasLifecycle ? _panel.LatestLifecycle.RunId : null` を読む（既存 consumer・VM を弄らない理由）。Replay: run_id は無く、portfolio/summary json で駆動するので **hasContent の falling→rising edge** が「次 run」の検出子。
- **F9 view の付け替え seam。** `LivePanelTileView`（`LivePanelTileView.cs`）は formatter delegate を持つ Text への dumb sink（`Build(body, font)` で body に Text を生成・`Refresh(vm)`/`ShowText(str)`/`ShowReplayEmpty()`・`ApplyTheme()`）。run_result tile view は `BuildDockContent` の run_result 分岐（Explore 報告 :974-975）で `_runResultView = new LivePanelTileView(FormatRunResult); _runResultView.Build(body, _font)` と構築。popup 化は **`_runResultView` の `Build` 先 body を「ポップアップ card の本文 RectTransform」へ付け替える**だけで、`Refresh`/`ShowText`/`ShowReplayEmpty` の呼び出し（`PushLiveTiles`/`PushReplayTiles`）は無改変。ただし popup は **card root の SetActive で可視を一元管理**するので、`ShowReplayEmpty`（"(no data — Replay)" の文字表示）は popup では **呼ばず**、honest-empty なら card root を hide する（D2）。

## 設計の木（D1–D8・ADR-0037 で凍結）

> 本 finding の D1–D8 は ADR-0037 の Decision とほぼ対応するが **番号は厳密な 1:1 ではない**: finding D1–D4/D7/D8 は ADR D1–D4/D7/D8 と一致、finding **D5（format/poll/view 再利用）→ ADR D6**、finding **D6（永続化しない）→ ADR D4/Consequences**、ADR **D5（base factory group 3 窓）** は finding 側では F5 に裏取りされ独立 D 項目を持たない。各項の括弧内に対応する ADR 番号を明記している。

- **D1 cutover の置き場 = screen-anchored ポップアップ card。** 新規 `ScreenSpaceOverlay` Canvas を `transform` 直下に作り（F6 パターン）、右上アンカー（`anchorMin/Max = (1,1)`・`pivot (1,1)`・`anchoredPosition` で右上から内側へ余白）。固定サイズ（dock 既定の 380×220 を踏襲）。card root（背景 Image ＋ title バー "Run Result" ＋ #173 の × ＋ 本文 Text）。sortingOrder は scene(@0) より上・secret/settings modal より下。add-cell button（200・右下）と別 Canvas・別コーナーなので衝突しない（同 200 か近傍値で可・実装時に確定）。drag/resize ハンドルは付けない。
- **D2 content-derived 可視（#172 このスライス）。** `hasContent` で card root を `SetActive`。
  - Replay: `PushReplayTiles` の honest-empty 枝（`IsNullOrWhiteSpace(portfolioJson)`）→ `hasContent=false`（card hide）。非空 → `hasContent=true`、`ShowText(running ?? complete)` で本文更新。
  - LiveAuto: `PushLiveTiles` → `hasContent_live`（D3）。true なら `Refresh(p)`。
  - **popup では `ShowReplayEmpty` の文字表示は使わない**（honest-empty は card 非表示で表現）。format 関数自体は無改変（D5）。
- **D3 LiveManual で出さない＝live hasContent に mode ゲートを畳む（F4/F7 訂正・load-bearing）。**
  ```
  hasContent_live = (vm.HasLifecycle || vm.HasTelemetry) && DisplayMode == LiveAuto
  ```
  sticky フラグ（F4）のため `(HasLifecycle || HasTelemetry)` 単独では LiveAuto→LiveManual flip で stale が漏れる。`DisplayMode == LiveAuto` ゲートで #138 の anti-stale-LiveManual 契約を content-derive モデル内に保持する。`DriveRunResult`（method・呼び出し）は退役。
- **D4 dock plane からの純減（ADR-0037 D4）。** ①`FloatingWindowCatalog.KIND_RUN_RESULT` 定数＋`Default()` spec 削除、②`DockShape.IsDockKind` の run_result 分岐削除、③`DockShape.IsCoreKind` → 空集合（`=> false`・F2 dead code）、④`BaseDockWindowIds` 4→3、⑤`SpawnBaseDockWindows` の run_result spawn は ④ の配列短縮で自動的に消える、⑥`DriveRunResult` method ＋ `Update` の呼び出し削除、⑦`CaptureLayout`/`RestoreFloating` は run_result を書かない/読まない（dock controller に run_result が居なくなるので Capture から自然に落ちる・Restore は未知 id を fall-through）。`WINDOW_ID_RUN_RESULT` const は他参照が消えたら削除。
- **D5 format 関数・poll・view の再利用（ADR-0037 D6）。** `FormatReplayRunResult*`/`FormatRunResult`/`PushReplayTiles`/`PushLiveTiles`/`LivePanelTileView` は無改変で再利用。`_runResultView.Build` 先 body を popup card 本文へ付け替え（F9）。base 3 panel（buying_power/orders/positions）の dock tile・format・poll は無改変。
- **D6 永続化しない（ADR-0037 D6）。** popup の位置/可視は `floatingWindows` に乗らない（D4 で dock controller から外れる）。既存 saved layout の run_result geometry は migrate せず無視。dismiss latch（D7）も session 内のみ。
- **D7 × close ＋ dismiss latch（#173・ADR-0037 D7）。** card に × close ボタン。`visible = hasContent && !_runResultDismissed`。× で `_runResultDismissed = true`。**同一 run 中**は running→complete 遷移でも一度 × したら再出現しない。latch は session 内のみ・毎 run 再 arm（非永続）。可視は D2/D3 の hasContent と latch の AND で card root を `SetActive`。
- **D8 dismiss latch の対称再 arm（#173・ADR-0037 D8）。** 「新しい run の rising edge」で latch をリセット。**runIdentity** を 1 概念に統一し、変化したら `_runResultDismissed = false`:
  - Replay: `hasContent` の **falling→rising edge** を単調カウンタ（`_replayRunEpoch`）でカウントし、それを runIdentity に供給。
  - LiveAuto: **`run_id` の変化**（`LatestLifecycle.RunId`／telemetry `run_id`・F8）を runIdentity に供給（sticky フラグでは boolean falling edge が出ないため・#164 と同型の死角回避）。
  - ★ **対称性**: 片側だけ実装すると「LiveAuto で一度閉じると二度と出ない」死角。両 mode で「次 run → 再出現」を AFK で pin（片側欠落＝RED になる gate）。

## 実装スライス（後続 behavior-to-e2e で RED-first 化）

### #172 — ポップアップ cutover（content-derived）
- **S1**: screen-anchored popup card の構築（`ScreenSpaceOverlay` Canvas・右上・固定サイズ・title＋本文 Text）。`_runResultView` を popup 本文へ Build。AFK: pan-invariance（dock window は pan で動くが popup は screen 座標固定）を probe で pin（実 pan 視覚は owner HITL）。
- **S2**: content-derived 可視（D2）。Replay running/complete/honest-empty で card の出現/消滅を pin。
- **S3**: LiveManual で出さない＝live hasContent の mode ゲート（D3）。**LiveAuto run → LiveManual flip で stale telemetry が出ない**ことを RED-first で pin（F4 の罠を gate 化）。LiveAuto telemetry で出ることも pin。
- **S4**: dock plane 純減（D4）。`BaseDockWindowIds` 3 要素・base group 3 メンバ・`KIND_RUN_RESULT`/`IsDockKind(run_result)`/`IsCoreKind` 退役・`DriveRunResult` 退役・persist 非対象。既存 probe/golden の 4→3 更新。
- **S5**: format/poll/view 再利用の回帰（D5）。Replay/LiveAuto の表示値が従来どおり。base 3 panel 無改変。

### #173 — × close ＋ dismiss latch（§F1）
- **S6**: × close ＋ latch（D7）。× で閉じ、同一 run 中は running→complete でも再出現しない。boot で latch を持ち越さない（非永続）。
- **S7**: 対称再 arm（D8）。次 Replay run（portfolio 再投入＝hasContent falling→rising）で再出現／次 LiveAuto run（新 run_id）で再出現。両 mode を AFK で pin（片側欠落＝RED）。

## 実装着地・RED→GREEN（2026-06-27）

production:
- `Assets/Scripts/Live/RunResultPopup.cs`（新規）= screen-anchored card（`ScreenSpaceOverlay` Canvas を `transform` 直下・右上アンカー固定サイズ・title＋× close・`SetVisible`/`OnClose`/`ApplyTheme`・body は `LivePanelTileView` を Build する）。
- `BackcastWorkspaceRoot.cs`: `BuildRunResultPopup()`（BuildWorkspace で呼ぶ・`_runResultView` を popup body へ Build）／ `DriveRunResultPopup()`（D2/D3/D7/D8 のロジック・`Update` で旧 `DriveRunResult()` を置換）／ `_runResultPopup` ＋ latch 状態 `_runResultDismissed`/`_runResultPrevReplayHasContent`/`_runResultLastRunId`。`BaseDockWindowIds` 4→3・`WINDOW_ID_RUN_RESULT` 撤去・`BuildDockContent` の run_result 分岐撤去・`DriveRunResult` method+call 撤去・`PushReplayTiles` の run_result `ShowReplayEmpty` 撤去（content 分岐の `ShowText` は維持＝D5）・`ApplyViewportTheme` に `_runResultPopup.ApplyTheme()` 追加。
- `DockShape.cs`: `IsDockKind` から run_result 除去・`IsCoreKind` → `=> false`（空集合・F2/D4）。
- `FloatingWindowCatalog.cs`: `KIND_RUN_RESULT` const＋`Default()` spec 撤去（KIND_STARTUP 退役パターン踏襲・forward-compat skip）。

test（dock 純減の blast radius・#126 の「共有 kind 退役＝fixture 移行」教訓どおり）:
- `ReplayRunResultTileE2ERunner`（.cs/.md）を popup 用に作り替え＝**RRT-01..09**（上記）。`host.Panel.Apply(<lifecycle wire>)` で LiveAuto run_id を、override seam で Replay portfolio を駆動。
- `FloatingWindowE2ERunner`: KIND_RUN_RESULT fixture（S12a catalog kinds / S17 dock routing / S18 hidden round-trip / S23d group merge / S26e / S27 / S32 factory base group）を生存 dock kind（buying_power/positions）へ移行・base group **4→3**・PASS summary 文言更新。
- `ScenarioStartupE2ERunner`: S14 非空虚 kind を buying_power へ・**SCENARIO-16 base 4→3**（`Section13_DockClusterIsThree`）。
- `ChartPlacementJourneyE2ERunner`: CP-S4-01 base **4→3**。
- `NotebookToHakoniwaJourneyE2ERunner`: NBHAKO-05/353 のコメントを popup view へ更新（text 駆動チェーンは無改変で PASS）。

RED→GREEN（実機 Unity batchmode・`scripts/run-live-e2e.ps1`・2026-06-27）:
- compile-gate: `error CS` 0・exit 0。
- `ReplayRunResultTileE2ERunner.Run` → `[E2E RRT-06..09B PASS]` ＋ `[REPLAY RUNRESULT TILE PASS]`（RRT-01..05 含む全 GREEN）。RED litmus は .md の通り（D3 の `&& LiveAuto` を外す→RRT-06 RED、× latch no-op→RRT-08 RED、LiveAuto 再 arm を boolean falling edge に→RRT-09B RED）。
- `FloatingWindowE2ERunner.Run` → `[E2E FLOATING WINDOW PASS]`（移行中に S18 で `RectOf("run_result")` 取り残しを検出＝`positions` へ修正して GREEN＝実 AFK が移行漏れを捕捉）。
- `ScenarioStartupE2ERunner.Run` → `[E2E SCENARIO-16 PASS]`/`[E2E SCENARIO-17 PASS]`（初回 base 4→3 漏れを実 AFK が `S13: base dock has 3 windows, expected 4` で RED 検出→修正して GREEN）。
- `ChartPlacementJourneyE2ERunner.Run` / `NotebookToHakoniwaJourneyE2ERunner.Run` → 各 PASS。

既知の据え置き（本 slice 対象外）:
- `Assets/Editor/BackcastWorkspaceProbe.cs:348` は **#103/ADR-0026 以前の legacy Probe**（base を `_windows` で引き・retired `startup` を含む）で、prior cutover #126 も触らず stale のまま放置した確立済み debt。E2E rollup の正規 runner ではない（Probe）。本 slice はこの探索 Probe を rewire しない（ADR-0026 と同じ判断）。
- `LayoutDocument.Default()` の `panels`（`"run_result"` を含む）は ADR-0017 以前の split-grid POCO の **dead schema**（CaptureLayout は `panels=[]` を書く・spawn には未使用）で、`ReplayLayoutProbe` Section 7 が serialization 安定として locked。run_result *window* ではないので無改変。

## code-review(simplify high) 反映（2026-06-27・8 finder × verify）

- **[Fix] popup card が canvas pan を食う dead-zone**: `RunResultPopup` の `_cardBg`/`_titleBg` を `raycastTarget=false` に（× ボタンのみ interactive）。dock tile body（`LivePanelTileView` の `raycastTarget=false`・body-drag が pan に落ちる）と同じ pattern に揃え、popup の上で pan が効くようにした。
- **[Fix] LiveAuto re-arm の event-ordering 頑健化**: run_id を `HasLifecycle ? LatestLifecycle.RunId : HasTelemetry ? LatestTelemetry.RunId : null` に（telemetry が lifecycle より先に届く run でも run_id を取りこぼさない・F8 で両者同一 run_id）。
- **[Fix] `ApplyTheme` の冗長排除**: `WindowChrome.Attach` が wire する `WindowChromeApplier` が `_cardBg`（root Image）を `hakoniwa_panel_surface` で theme 追従済みなので、`ApplyTheme` は accent（`_titleBg`）のみ repaint に簡素化。
- **[Test] RRT-06 を強化**: (a) LiveAuto popup の **body text**（`PushLiveTiles→Refresh(p)→FormatRunResult`）を assert＝visible-but-empty 回帰を pin、(b) **mode round-trip で dismissed popup が spurious 再 arm しない**ことを assert＝`DriveRunResultPopup` の load-bearing `if(!IsNullOrEmpty(runId))` guard（LiveManual で run_id tracker を null に潰さない）を pin（naive な無条件代入に戻すと RED）。
- **[受容] Replay re-arm の empty-frame 依存（poll-skip race）**: Replay の「次 run」検出は honest-empty→content の rising edge。`last_portfolio` は `on_run_begin` で clear される（backtester.py:177）ので empty frame は構造的に存在するが、worker が clear→first-bar を 2 つの main-thread poll の間で済ませると empty frame が観測されず再 arm を取りこぼす理論上の race がある。これは #65/#100 の base-tile anti-stale が依存する同じ poll-model 前提で、Replay JSON に run_id が無い以上（追加は Python 変更＝本 slice 対象外）rising edge が design の正本。**accepted edge** として記録（実機 50ms poll では empty frame はほぼ常に観測される・#100 architecture は GREEN 実績）。run_id ベースの完全頑健化は将来 Replay portfolio/summary に run 識別子を足す別 slice。
- **[判断] `RunResultPopup` の chrome が `DockWindowFrame`/`EnsureCloseButton` を再実装**（reuse finder）: popup は意図的に **drag を持たない別 surface**（`DockWindowFrame` は `FloatingWindowTitleInput` drag component を含み × 幅を予約しない）なので、reuse は coupling を増やす。`TitleHeight` const は共有済み。distinct surface として hand-roll を許容（divergence は本 finding で明示）。

## 逆転対象 / ADR 不編集

- **#138 / findings 0110 §7「`DriveRunResult`＝run_result tile を LiveManual で SetActive hide」を ADR-0037 が supersede。** method は退役、anti-stale-LiveManual *契約*は D3 の content gate（`DisplayMode == LiveAuto`）へ移送。findings 0110 §7 に stale-marker「supersede: ADR-0037（契約は popup hasContent gate へ移送）」を追記する（slice 記録なので marker 追記可）。
- ADR-0018（dock depth plane）/ ADR-0020（factory-first base cluster）/ ADR-0024（puzzle-feel drag）/ ADR-0029（grab/eject/pickup）は参照のみ・無編集（別 decision の固定 oracle）。run_result が base group から抜けても 3 窓で group は成立（F5）。
