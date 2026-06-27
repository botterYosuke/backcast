# findings 0126 — アカウントサマリーバーが buying_power / orders / positions の dock パネルを置換

**方針: [[ADR-0038]]**（ADR-0017 D1 / ADR-0018 / ADR-0020 を 3 種について点 supersede）。姉妹: [[ADR-0037]] / findings 0125
（run_result→popup・同日 2026-06-27 並行）。実装 issue は `to-issues` で別途。本 finding は grill-with-docs（2026-06-27・
owner HITL）で固定した設計の木と codebase 裏取りを記録する slice 記録。

> 番号注記: 本 finding は `ls docs/findings/ | sort` の次空き番号 0126 で採番（0125 は姉妹 run-result-popup が消費）。
> 対応 ADR は 0038（0037 は姉妹）。

## 要求（owner・2026-06-27）

1. `buying_power` / `orders` / `positions` の 3 dock パネルを**廃止**する。
2. ヘッダー（メニューバー）の**直下**にゲーム風リソースバー（アイコン＋summary 数値）を出す。
3. マウスホバーで**今のパネルと同じ詳細情報**を出す。

参考画像はゲームのリソースバー（アイコン下げに数値 1 つ）。実アセット指定ではなく「雰囲気」参照。

## 設計の木（grill で確定した下位決定）

### D1 — バーの指標（owner 回答）
4 スロット。①②はペア指標を 1 アイコンに束ねる:

| # | スロット | バー主数値 | ペア（ホバー） |
|---|---|---|---|
| ① | 純資産＆含み損益 | 純資産 Equity | 含み損益 Unrealized PnL |
| ② | 買付余力＆現金 | 買付余力 Buying Power | 現金 Cash |
| ③ | 建玉件数 | Open Positions 件数 | — |
| ④ | 注文件数 | Open Orders 件数 | — |

owner は最初の推奨 7 枠（bp/cash/equity/realized/unrealized/orders/positions）を **4 枠にペア集約**する案を選択。

### D2 — バー上の見せ方（owner 回答）
**主数値 1 つ＋色**。①は含み損益（Unrealized PnL）の符号で数値色を緑/赤に振る。ペアの第 2 値（含み損益・現金）は
バーには出さずホバー詳細に回す。画像の「アイコン下げ数値 1 つ」スタイルを保つ。

### D3 — ①のホバー詳細（owner 回答）
**口座サマリー**: 純資産 Equity / 含み損益 Unrealized PnL / 確定損益 Realized PnL / 現金 Cash の 4 行。①は現状どの単独
パネルにも無い新集約。②③④は退役パネルと 1:1（D5）。Run Result（成績＝sharpe 等・姉妹 ADR-0037 でポップアップ化）とは
役割が分かれる（口座状態 vs 走行成績）。

### D4 — 設置形態（決定・コード裏取り）
`MenuBarView`（scene-wired chrome）直下に全幅オーバーレイの chrome ストリップ。`ScreenSpaceOverlay` 側に置き `Content`
（infinite canvas）の子に**しない**＝パンで動かない・パララックス層に乗らない・ガター予約なしで canvas に重なる。姉妹
ADR-0037 の run-result popup と同じ「3D 空間から除外」原則。

### D5 — ホバー詳細マッピング（owner 回答・format 関数再利用）
- ② → `FormatBuyingPower`（Live）/ `FormatReplayBuyingPower`（Replay）と同一文字列。
- ③ → `FormatPositions`（Live）/ `FormatReplayPositions`（Replay）と同一（銘柄別 qty/avg/uPnL ＋ cash/bp）。
- ④ → `FormatOrders`（Live）/ `FormatReplayOrders`（Replay）と同一（最新注文 ＋ filled-order count）。
- ① → 新 formatter（口座サマリー 4 行）。Live=telemetry＋account 集約、Replay=`PortfolioSnapshot`。

format 関数は退役せずホバー card へ流用（ターゲット付け替えのみ）。

### D6 — 表示場面（owner 回答）
**常に表示**（Replay / LiveManual / LiveAuto 全モード）。account/portfolio snapshot 未着時は各数値「—」プレースホルダ。
モード切替でバーは消えない（レイアウトが上下に動かない）。

### D7 — アイコン実体（owner 回答）
当面 **3D プリミティブ**（①cube / ②sphere / ③capsule / ④cylinder）を placeholder。`ScreenSpaceOverlay` には 3D 物体が
直接描画されないので、小さな `RenderTexture`（ミニカメラ＋プリミティブ）を `RawImage` に流す方式（差し替え seam=この
`RawImage`）。将来 owner が用意する実アート（sprite）へ置換可能。

### D8 — Live 純資産のデータ源（owner 回答・コード裏取り）
**C# 側で導出**: `Equity = Cash + Σ(qty × avg_price + unrealized_pnl)`（現金＋建玉時価）。Python 変更不要。ADR-0007 の
equity 定義（MTM）と一致。Replay は `PortfolioSnapshot.Equity` を直接読む。

### D9 — 廃止スコープ（owner 回答）
**完全撤去**。catalog Default() / base-dock spawn / `LivePanelTileView` 配線から 3 kind を全撤去。ウィンドウとして二度と
開けない（バー＋ホバーが唯一の面）。forward-compat skip で旧保存 layout は無害（D-永続）。

## codebase 裏取り（2026-06-27）

- **退役対象 kind**: `FloatingWindowCatalog.cs:31-35`（`KIND_BUYING_POWER` / `KIND_ORDERS` / `KIND_POSITIONS`）＋
  `Default():87-98` の 3 spec。`closeable=false`（workspace-owned）。
- **dock 分岐**: `DockShape.IsDockKind`（dock kind 分類の唯一の述語）。3 kind 分岐を退役（chart のみ残す・run_result は
  姉妹 ADR-0037 が退役）。
- **base spawn / factory group**: `BackcastWorkspaceRoot.cs` の `BaseDockWindowIds` / `SpawnBaseDockWindows` /
  `FormFactoryBaseGroup`（ADR-0020・姉妹で 4→3 縮小済み）。本 ADR で 3→0＝**factory base group 概念ごと退役**。
- **tile 描画**: `LivePanelTileView`（findings 0014 RH2/RH4・1 view を 3 回 Build）＋ formatter デリゲート。`_buyingPowerView`
  / `_ordersView` / `_positionsView`（`BackcastWorkspaceRoot.cs:202`）の dock-tile build（同 949-954）を退役。
- **Live formatter**: `BackcastWorkspaceRoot.cs:2132`（`FormatBuyingPower`：`bp` / `cash`）／`:2143`（`FormatOrders`：最新
  注文 ＋ `FilledOrderCount`）／`:2157`（`FormatPositions`：銘柄別 `qty`/`avg`/`uPnL` ＋ `cash`/`bp`）。
- **Replay formatter**: `:2187`（`FormatReplayBuyingPower`：`bp` / `equity`）他 `FormatReplay*`。Replay は
  `PortfolioSnapshot`（`ReplayPanelDecoder.cs:98` `BuyingPower` / `:99` `Equity` / `:102-103` `RealizedPnl`/`UnrealizedPnl`）。
- **データ型**: `LiveAccountEvent`（`LiveBackendEventDecoder.cs`：`Cash` / `BuyingPower` / `Positions`、**equity 無し**）／
  `LiveTelemetryEvent`（`RealizedPnl` / `UnrealizedPnl` / `OrderCount` / `FillCount`）／`LivePosition`
  （`symbol`/`qty`/`avg_price`/`unrealized_pnl`）。→ Live 純資産は D8 の式で導出。
- **Python equity**: `engine/backend_service.py:87-89` の state レスポンスは `buying_power`/`cash`/`equity` を持つが、live
  push の account イベント（`controller.py:151` `adapter.fetch_account()` → kabu `kabusapi_execution.py:544` /
  立花 `tachibana.py:726` は `cash=bp, buying_power=bp, positions`）には equity が来ない。よって D8 の C# 導出を採用
  （Python wire 変更を避ける）。

## 並行 feature 調停（#sibling ADR-0037 ↔ 本件）

- **番号**: ADR-0037 / findings 0125 は姉妹（run_result→popup）が取得済み。本件は ADR-0038 / findings 0126。
- **共有機構**: ADR-0020 factory base cluster と CONTEXT.md。姉妹は base を 4→3（`{buying_power, orders, positions}`）に
  縮め、CONTEXT を未コミットで編集中（L341/L361/L484 等「dock=chart + 3 base singleton」）。本件はその 3 窓も外すので
  **base=0＝dock=chart のみ**。factory base group 概念は本 ADR で退役（owner 選択「ADR-0038 を姉妹に重ねて今書く」）。
- **CONTEXT 編集規律**: 姉妹の未コミット編集を**潰さず additive** に重ね、「3 base singleton」記述を「chart のみ」へ更新し
  新項 `アカウントサマリーバー` を追加。
- **`DockShape.IsCoreKind`**: 姉妹 ADR-0037 で既に空集合へ畳み済み。本件でも空のまま（変更なし）。

## 残（実装着手時）

- 実装着手前に `behavior-to-e2e` を formal invoke し、AFK 正本（§ADR-0038 Consequences の probe 列挙）を RED→GREEN で立てる。
- `to-issues` でトレーサ縦スライスに分解（バー骨格＋常時表示 → 4 スロット主数値＋色 → ホバー詳細（format 再利用）→
  3 kind 完全撤去＋factory group 退役＋forward-compat → アイコン RenderTexture placeholder）。
- 姉妹 ADR-0037 が先に commit したら CONTEXT 競合を解消（additive 維持）。
- owner HITL: 実 pan の screen-anchored 目視・実アイコン差し替え。

## 実装着地（2026-06-27・#174-178）

### スコープ調停（#172/#173 は別作業者が並行実装中・owner 2026-06-27）
ADR-0038 / #178 は「姉妹 #172（run_result→popup）が base を 4→3 に縮めた残り」を前提に「base=0・dock=chart のみ・
`FormFactoryBaseGroup` 退役・`IsCoreKind` 空集合」と書くが、**着手時点で #172 は docs のみ commit・コード未実装**
（`BaseDockWindowIds`=4・`IsDockKind`=chart+4 base・`IsCoreKind`={run_result}）。owner 確認で **#172/#173 は別作業者**。
→ 本スライスは **buying_power/orders/positions の 3 kind だけ退役**し、run_result＋`FormFactoryBaseGroup`＋`IsCoreKind` は
#172 の作業者に委ねる。共有 seam の分担:
- `BaseDockWindowIds` → `{ run_result }`（3 削除・run_result 残置）。**`FormGroup` は `live<2` で no-op**
  （`FloatingWindowController.cs:1347`）なので、base が 1 に縮むと **`FormFactoryBaseGroup` は本体を触らず自然に no-op**＝
  「factory group が形成されない」が成立（#172 が最後の run_result を外したとき dead method を消す）。
- `FloatingWindowCatalog`: `KIND_BUYING_POWER/ORDERS/POSITIONS` const＋`Default()` の 3 spec を削除（run_result spec 残置）。
- `DockShape.IsDockKind`: 3 分岐削除（chart+run_result 残置）。`IsCoreKind` 無改変（#172 の領分）。

### 下位決定（ADR-0038 §自己保護に従い本 finding に固定）
- **バー設置**: 自前 `ScreenSpaceOverlay` canvas の `AccountSummaryBarView`（`BackcastWorkspaceRoot.BuildWorkspace` が
  runtime 生成・scene 再ビルド不要）。sort = `MenuBarView.MENU_SORT-50`（=550：content/sidebar/footer の上・menu
  dropdown(600) の下）。全幅 top strip・`anchoredPosition.y = -menuH`（menuH=メニュー container `sizeDelta.y`＝24・fallback 24）・
  高さ 44。`_content` の子に**しない**＝pan 不変・非永続。
- **主数値フォーマット**: money（純資産・買付余力）= `value.ToString("#,0", InvariantCulture)`（千区切り・小数なし＝game
  resource bar の 1 数値）。件数 = 整数 `ToString(InvariantCulture)`。未取得 = `"—"`（`AccountSummaryFormat.PLACEHOLDER`）。
  ホバー card は退役パネルと同じ raw-double 精度（`Format*`/`FormatReplay*` を **byte 一致**で再利用）。
- **Live 純資産導出（D8）**: `Equity = Cash + Σ(qty×avg_price + unrealized_pnl)`（`AccountSummaryFormat.DeriveLiveEquity`）。
  Replay は `PortfolioSnapshot.Equity` を直読。
- **①の色源**: Live = `Σ position.unrealized_pnl`（account・equity 導出の MTM 項と**同一源で coherent**）の符号、Replay =
  `snap.UnrealizedPnl` の符号。`≥0→hakoniwa_up`（緑）/ `<0→hakoniwa_down`（赤）。
- **件数源**: ③建玉 = Live `account.Positions.Count` / Replay `snap.Positions.Count`。④注文 = Live `telemetry.OrderCount` /
  Replay `snap.Orders.Count`。
- **①ホバー口座サマリー 4 行**: `equity / unrealized / realized / cash`。Live=account（cash/Σunrealized/derived equity）＋
  telemetry（realized・無いとき `"—"`）、Replay=`PortfolioSnapshot`（cash≈bp）。新集約ゆえ `AccountSummaryFormat` に新設。
- **アイコン（S5）**: `AccountSummaryIconStage` が off-world（x=10000）に 4 stage（cube/sphere/capsule/cylinder＋ortho cam→
  per-slot `RenderTexture`）を建て、`RawImage.texture` に流す（差し替え seam）。`-nographics` では描画 inert だが RT object は
  非 null（AFK は seam の非 null のみ assert・実 lit 画素は owner HITL）。
- **テーマ追従**: 主数値の text/色は毎フレ push で再適用。strip/icon枠/card 背景＋card text は build 時 bake ゆえ
  `AccountSummaryBarView.ApplyTheme()` を `ThemeService.Changed` で run_result tile と並べて呼ぶ。

### テストフィクスチャ移行（退役 3 kind を使う既存 runner）
退役 `KIND_BUYING_POWER/ORDERS/POSITIONS` を「任意の plain dock フィクスチャ」に多用していた runner を **生存 kind へ移行**。
移行先は **`KIND_RUN_RESULT`**（minSize 240×140 ≤ 全 spawn サイズ＝geometry-neutral・座標 assert 再計算不要・dock 卷 kind ゆえ
plane routing 保持・`IsCoreKind` は ADR-0024 で inert）。`chart`（minSize 280×200）は 280×180 spawn を clamp-up して座標を割るので不可。
- `FloatingWindowE2ERunner.cs`: S12 を `{chart, run_result}` 解決＋**S12d で退役 3 kind の非解決（forward-compat skip）を gate 化**。
  S32（factory group）は base 数から decouple し代表 4 窓（run_result kind・id `g0..g2/run_result`）で FormGroup 力学を gate。
  残り merge/swap/drag/group/reflow フィクスチャは blanket `KIND_{ORDERS,POSITIONS,BUYING_POWER}→KIND_RUN_RESULT`（id 文字列は
  ラベルとして残置＝geometry/挙動不変）。PASS 要約文字列・section コメントも整合。
- `FloatingWindowResizeE2ERunner.cs`: S5 等の fixture を同様に run_result へ。
- `ScenarioStartupE2ERunner.cs` S13: `BaseDockWindowIds.Length==1`＋退役 id 不在を assert（旧 `==4` を更新）。
- `ChartPlacementJourneyE2ERunner.cs` S4: `baseIds={run_result}`。
- `ReplayRunResultTileE2ERunner.cs` RRT-06: 退役 sibling 3 tile の代わりに **chart 窓**を collateral witness に（#172 が DriveRunResult を
  popup 化する際に再構成する想定・本スライスは最小変更）。
- `Assets/Editor/BackcastWorkspaceProbe.cs`: 既に stale（"startup"＋front-plane 誤参照）→ `{run_result}` に是正。

### AFK 正本（RED→GREEN）
新規 `AccountSummaryBarE2ERunner`（Python-FREE・実 root）ASB-01..10 = 全 GREEN・exit 0（2026-06-27 実走）。Replay は
`TestPortfolioJsonOverride`、Live は `LivePanelViewModel.Apply(wire)` で駆動。delete-the-production-logic litmus（.md に列挙）:
② ホバーを `FormatReplayOrders` に誤配線→ASB-05 RED／equity 導出から uPnL 項落とし→ASB-04 RED（literal `154,221` 不一致）／
バーを `_content` 子に→ASB-06 RED／退役 spec を `Default()` 残置→ASB-08＋S12d RED／①色を符号非依存→ASB-03 RED。
回帰確認: `FloatingWindowE2ERunner`（全 section）/ `ScenarioStartupE2ERunner` / `ReplayRunResultTileE2ERunner` /
`ChartPlacementJourneyE2ERunner` / `FloatingWindowResizeE2ERunner` を移行後に再走し GREEN を確認。compile-only `error CS` 0。

### code-review（high）反映（2026-06-27）
- **HIGH（見落とし）`NotebookToHakoniwaJourneyE2ERunner`（NBHAKO・12 PASS の登録 gate）が退役 view field
  `_buyingPowerView/_ordersView/_positionsView` を reflection 参照**（string-name ゆえ compile は通り runtime で RED）。
  → 退役 3 tile の検証を **バーのホバー card `CardText(1/2/3)`**（同じ `FormatReplay*`＝byte 一致）＋ ②主数値
  `PrimaryText(1)` に移行。run_result tile は残置（`TileText("_runResultView")`）。NBHAKO-05/06/11＋.md 整合。
  **教訓: 退役 field は `Assets/Scripts` だけでなく `Assets/Tests` の reflection 参照（string-name で compile を
  すり抜ける）まで grep する。**
- **MEDIUM テーマ flip でバー主数値色が stale**: 主数値色は gated drive でのみ set されるので、Live イベント無し/
  Replay 停止中に Dark↔Light すると主数値が旧テーマ色のまま（ADR-0028 invisibility trap）。→ `SetPrimary` を raw
  `Color` でなく **意味 tint（`PrimaryTint{Neutral/Gain/Loss}`）**で受け、bar 内で tint→theme 色を解決＋`ApplyTheme`
  が各 slot tint から色を**再解決**。owner の drive は色マッピングを持たず tint 意図だけ渡す（altitude 改善）。
- LOW: S23d の「core-bearing vs plain group」コメントは退役 + ADR-0024（IsCoreKind 無 caller）で vacuous → cascade は
  core-agnostic（size-then-dict-min）と正す。
- 受容（非 Medium）: ホバー詳細 4 本を data-change 毎に eager 構築（card は通常非表示）＝退役 dock tile と同じ
  per-change コスト・gated（per-frame ではない）・bar を純 chrome に保つため domain 結合を増やさず eager 維持。
  アイコン RT リグ（4 cam/RT/primitive）は D7 owner-locked 設計（実 art 待ち）＝OnDestroy で RT release・once-per-Build。

## 姉妹 #172 マージ調停（2026-06-27・origin/main pull）

#172/#173（run_result→screen-anchored popup・ADR-0037/findings 0125）が origin/main に着地。**両スライスは厳密に相補**＝
#172 が run_result を、#178 が buying_power/orders/positions を退役 → 合算で **全 base singleton 退役・dock=chart のみ**。
merge は 11 conflict（共有 seam）。調停（end state = dock=chart のみ・`IsCoreKind=false`・`BaseDockWindowIds` 空・base group 不形成）:
- **`FloatingWindowCatalog`**: 両者の退役を合流＝KIND_RUN_RESULT(#172)＋KIND_BUYING_POWER/ORDERS/POSITIONS(#178) const＋spec を**全削除**。dock kind は `chart` のみ。
- **`DockShape`**: `IsDockKind = (kind==KIND_CHART)` のみ。`IsCoreKind=>false`（#172 の空集合化を採用）。
- **`BackcastWorkspaceRoot`**: `BaseDockWindowIds = Array.Empty<string>()`（全退役）→ `SpawnBaseDockWindows`/`FormFactoryBaseGroup`
  は **no-op**（loop 0 回・`FormGroup([])`→null）＝ADR-0038 §6「factory base group 退役」を**空列挙で達成**（メソッド本体は残置・
  vestigial）。`BuildDockContent` は chart のみ。`_runResultPopup`(#172)＋`_accountBar`(#178) の両 chrome wiring・両 `ApplyTheme`・
  PushReplayTiles の empty 分岐（popup hide ＋ bar `PushAccountBarEmpty`）を合流。
- **テストフィクスチャの二重退役**: #178 は退役 3 kind を `run_result` へ寄せたが #172 が run_result も退役 → merge 後は
  **kind-agnostic fixture（merge/swap/drag/group・~60 箇所）を front-plane `KIND_ORDER`（280×180・両退役を生存・geometry-neutral）**へ、
  **dock-plane 限定 fixture（S17/S18/S30）を唯一の生存 dock kind `KIND_CHART`（サイズ bump・S18 は 3 chart 窓 round-trip・S30 は
  cross-plane split の dock 側を chart）**へ再寄せ。S12 は `{chart}` のみ＋S12d（退役 5 kind 非解決）。
- **数値 base-count assertion**（kind 名を含まず grep に出ない）も掃いた: ScenarioStartup S13→`BaseDockWindowIds.Length==0`＋
  `IsDockKind` で dock=chart 証明、S14 非空虚 witness を `chart` へ、ChartPlacement CP-S4-01 を「退役 id が spawn されない」へ反転、
  FloatingWindowResize の `run_result` fixture→`order`、各 PASS 要約文字列を「dock=chart のみ」へ。
- **ReplayRunResultTileE2ERunner** は #172 が dock-tile→popup モデルに全面書換（RRT-06 が LiveManual hide→LiveAuto-scoped＋× latch）。
  #178 の RRT-06 sibling 変更は旧モデル用ゆえ superseded → #172 版を全採用。
- compile-only `error CS` 0。全 affected runner（AccountSummaryBar / FloatingWindow / ScenarioStartup / ChartPlacement / NBHAKO /
  ReplayRunResultTile / FloatingWindowResize）を merge 後に再走し GREEN を確認。CONTEXT は #172 の run-result-popup 項と #178 の
  アカウントサマリーバー項・chrome z-order（bar=550）が additive 共存。
- **教訓（skill 反映済）**: sibling 並行退役で「移行先 kind 自体が消える」二重退役・`KIND_*` を含まぬ数値 base-count assertion の
  grep 漏れ。`grep -nE "expected [0-9]|Length [!=]= [0-9]|[0-9] base"` を全 runner＋PASS 要約に走らせて N→N-1 を先に掃く。

## レビュー反映（zoom-out → code-review simplify・8 finder×verify・2026-06-27）

`/zoom-out` → `code-review(simplify)` で 8 角 finder×verify を回し、owner 確認の上で以下を反映（全 affected 再走 GREEN・compile `error CS` 0）。

- **M1（owner: FilledOrderCount に統一）**: slot ④（注文件数）Live 主数値を `telemetry.OrderCount`→`p.FilledOrderCount`。telemetry は
  LiveAuto 走行でしか来ず **LiveManual で ④ が永久「—」**＋主数値(投入累計)とホバー(約定数)が別物だった。FilledOrderCount は
  両モードで増え、④ホバー `FormatOrders`「filled-order count」と一致。ASB-04 を 2 FILLED OrderEvent 駆動＋`④=="2"≠telemetry 3`
  で source を pin（litmus）。Replay ④=`snap.Orders.Count` は不変。
- **M5（owner: 今フル削除）**: base 集合が空（#172 merge で run_result も退役）になったので vestigial 足場を削除——
  `BaseDockWindowIds` / `SpawnBaseDockWindows` / `FormFactoryBaseGroup` / `ResumeLastDocumentOrDefault` の `restoredSavedLayout`
  gate / `DockShape.IsCoreKind`。`FloatingWindowController.FormGroup`・`DockDefaultPlacement.ComputeFlushRects` は saved-layout
  group 復元 / AFK gate(S32) が使うので残置。ScenarioStartup S13 は field reflection を捨て **`DockShape` の end-state（chart=dock /
  退役 5 kind 非 dock）を behavioural assert** に。S32 は FormGroup 機構テストへ reframe（代表窓 spawn・production caller 退役を明記）。
- **M2/M3（AFK 実空白を塞ぐ）**: ASB-02 に **data→empty の anti-stale リセット**（pf1→null→全「—」＝`PushAccountBarEmpty`・#61）を、
  ASB-03 に **idle テーマ flip の色再解決**（`PrimaryTint`/`ApplyTheme`・ADR-0028 trap）を fold。後者は shipped Dark==Light
  （Theme.cs「Light は dark stub」）ゆえ Dark→Light は vacuous → **`Theme.NonDefault()` 検証パレットで flip**（非空虚）。
- **L1–L5（dead code 掃除）**: `AccountSummaryIconStage._cams`（write-only）削除＋icon light/cam を **ICON_LAYER(31) に cullingMask
  隔離**（directional light の全シーン照射を防ぐ）／`AccountSummaryBarView.Slot.hovered`（write-only・SoT 二重）削除／未使用
  `Built` probe accessor 削除／`CollectChartGridAvoidRects` の恒 false `isOtherDockKind` 分岐を chart-only に simplify。退役点の
  嘘 comment（「5 base windows」「3 base panels」「IsCoreKind ≡ false」等）を全実態化。
- **副産物の latent 修正**: `BackcastWorkspaceProbe.Section10`（chartwin・untracked legacy probe）が #172 merge 後 `run_result`
  base 窓を assert して RED 化＋ADR-0018 後も front-plane `_windows` を誤参照していた → **退役 5 kind 不在＋chart family を
  `_dockWindows` で検証**に是正（Section10 GREEN 確認）。同 probe の **S14（File→Open #80/0051）は本件と無関係の pre-existing
  rot**（OnFileOpen は未変更）＝scope 外。canonical File→Open gate は別 runner が担保。
- 再走 GREEN: AccountSummaryBar 10/10・ScenarioStartup 2/2・FloatingWindow 19/19・ChartPlacement core GREEN・compile PASS。
- **意図的で非修正（verify で REFUTE/設計 locked）**: uPnL==0→緑（findings 0126「≥0→緑」locked）／per-event eager hover 構築
  （§受容）／menu 高さ `sizeDelta.y`（scene が top-strip 24px author）／bar strip が 44px chrome をクリック吸収（意図）。
