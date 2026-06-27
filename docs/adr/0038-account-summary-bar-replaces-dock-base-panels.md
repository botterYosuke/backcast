---
status: accepted
---

# buying_power / orders / positions は dock panel を退役し、口座状態はヘッダー直下のアカウントサマリーバー＋ホバー詳細で出す（ADR-0017 D1 / ADR-0018 / ADR-0020 を点 supersede）

owner 依頼（2026-06-27）「buying_power / orders / positions のパネルを廃止。ヘッダーの下にゲーム風リソースバー（アイコン＋
summary 数値）を出し、マウスホバーで今のパネルと同じ詳細情報を出す」を `grill-with-docs`（2026-06-27・owner HITL）で設計
ロックした決定。issue は別途（`to-issues`）。

これは **ADR-0017 Decision 1**（旧 base tile と chart family は **すべて** floating window になる＝buying_power / orders /
positions も floating window）と **ADR-0018**（元箱庭 6 種は奥プレーン `DockLayer`（1.0倍）に乗る）を、**buying_power /
orders / positions の 3 種についてだけ** 点 supersede する。さらに **ADR-0020**（first-launch base cluster group）を、姉妹
[[ADR-0037]] が 4→3 に縮めた残り 3 窓も外すことで **factory base group 概念ごと退役**させる。これら 3 ADR はいずれも自己
保護条項（「覆す場合はこのファイルを編集せず、本 ADR を supersede する新規 ADR を起こす」）を持つため、**3 ADR は無改変**で
本 ADR を起こす。

**姉妹 [[ADR-0037]] との関係（同日並行・2026-06-27）**: ADR-0037 は `run_result` を dock 退役→screen-anchored ポップアップ
にし、base 集合を `{buying_power, orders, positions}` の 3 窓へ縮めた。本 ADR はその残り 3 窓も dock から外す。**両 ADR を
合わせると 4 つの base dock singleton が全滅し、dock plane は `chart`（multi-instance・universe 駆動）のみになる**。
factory base group（ADR-0020）は member が居なくなるので概念ごと退役する（§Decision 6）。

関連: [[ADR-0037]]（run_result→popup・同日姉妹・本 ADR と対で 4 base singleton を退役）／[[ADR-0017]]（Hakoniwa
ドッキング化・本 ADR が D1 を 3 種について supersede）／[[ADR-0018]]（2 深さプレーン・本 ADR が 3 種を `DockLayer` から
外す）／[[ADR-0020]]（factory base cluster・本 ADR が概念ごと退役）／findings 0075（ドッキング下位設計）／ADR-0007
（equity/cash/buying_power の Replay↔Live 同義）。実装下位事実は findings 0126。

## Context

buying_power / orders / positions は現在、奥プレーン `DockLayer`（1.0倍）の **non-closable base-dock singleton** であり、
(a) パンすると canvas（Content）と一緒に動く＝**パララックス「3D空間」に乗っている**、(b) `CaptureLayout` に
x/y/w/h/visible が乗り永続化される、(c) factory base group（ADR-0020・姉妹 ADR-0037 で 4→3 に縮小済み）の member、
(d) 各々 `LivePanelTileView` ＋ formatter デリゲート（`FormatBuyingPower` / `FormatOrders` / `FormatPositions` ＝Live、
`FormatReplayBuyingPower` / `FormatReplayOrders` / `FormatReplayPositions` ＝Replay）で本文 1 枚の Text を描く。

owner は「3 パネルを常設 dock として 3D 空間（パララックス canvas）に置くのをやめ、**ヘッダー直下に常時出るゲーム風
リソースバー**（アイコン＋集約数値）にまとめ、各指標の詳細は **マウスホバー** で今のパネルと同じものを出したい」と要望した。
これは「常時 dock に居る 3 つの base singleton」という現モデルと、「**画面固定の 1 本のサマリーバー＋ホバーポップアップ**」と
いう別モデルの対立であり、深さ（どのプレーンか）と seam（どの controller が描く/保存するか）を 3 種について畳む決定になる。

**equity のモード非対称（裏取り済み）**: Replay の `PortfolioSnapshot` は `Equity` を直接持つが、Live の venue account
スナップショット（`LiveAccountEvent` ＝ `Cash` / `BuyingPower` / `Positions`）と telemetry（`RealizedPnl` /
`UnrealizedPnl`）には **equity フィールドが無い**（venue adapter `fetch_account` も cash/bp/positions のみ）。`LivePosition`
は `symbol` / `qty` / `avg_price` / `unrealized_pnl` を持つので、Live の純資産は **`Cash + Σ(qty×avg_price + uPnL)`**
（現金＋建玉時価）で決定的に導出でき、ADR-0007 の equity 定義（MTM・現金＋建玉時価）と一致する。

## Decision

ADR-0017 D1・ADR-0018・ADR-0020 を以下の点で supersede する（記載 3 種以外は不変）。

1. **buying_power / orders / positions は dock window を退役**（ADR-0017 D1 / ADR-0018 を 3 種について覆す）。`DockLayer`
   から外れ、floating window seam（移動 / z-order / snap / window group / `CaptureLayout`）から完全に抜ける。
   `FloatingWindowCatalog` の `KIND_BUYING_POWER` / `KIND_ORDERS` / `KIND_POSITIONS`・`DockShape.IsDockKind` の該当分岐・
   `BaseDockWindowIds`・`SpawnBaseDockWindows` の該当 spawn・`LivePanelTileView` の dock-tile build 配線を退役。
   姉妹 ADR-0037（run_result 退役）と合わせ、**dock plane は `chart` のみ**に縮退。

2. **口座状態はヘッダー（`MenuBarView`）直下のアカウントサマリーバーで出す**。`ScreenSpaceOverlay` Canvas に全幅で固定
   anchor し、メニューバー直下に **オーバーレイ**で置く（`Content` の子では **ない**＝パンしても動かない・パララックス層に
   乗らない・ガター予約なしで canvas に重なる）。バーは **4 つの指標スロット**を持つ:

   | # | アイコン枠 | バー主数値（＋色） | ペア（ホバーで詳細） |
   |---|---|---|---|
   | ① | 純資産＆含み損益 | 純資産 Equity（含み損益の符号で緑/赤） | 含み損益 Unrealized PnL |
   | ② | 買付余力＆現金 | 買付余力 Buying Power | 現金 Cash |
   | ③ | 建玉件数 | Open Positions 件数 | — |
   | ④ | 注文件数 | Open Orders 件数 | — |

   バー上はアイコン枠＋**主数値 1 つ**（①は含み損益の符号で数値色を緑/赤）。ペアの第 2 値は主数値の隣には出さずホバー詳細へ。

3. **アイコンは差し替え可能な枠**。プロジェクトに財務指標用の画像アセットが無いため、**当面は 3D プリミティブ**
   （①=cube / ②=sphere / ③=capsule / ④=cylinder）を placeholder として置く。`ScreenSpaceOverlay` には 3D 物体が直接
   描画されないので、小さな `RenderTexture`（ミニカメラ＋プリミティブ）を `RawImage` に流す。**差し替え seam はこの
   `RawImage` の表示**で、将来の実アート（sprite / icon）に置換可能。

4. **ホバーで「今のパネルと同じ詳細情報」をポップアップ**。各スロットにポインタが乗ると、その指標のホバー card を出す:
   - ② → 現 `FormatBuyingPower`（`bp` / `cash`）と同一。
   - ③ → 現 `FormatPositions`（銘柄別 `qty` / `avg` / `uPnL` ＋ `cash` / `bp`）と同一。
   - ④ → 現 `FormatOrders`（最新注文の `client_order_id` / status / filled@avg ＋ filled-order count）と同一。
   - ① → **口座サマリー**（純資産 Equity / 含み損益 Unrealized PnL / 確定損益 Realized PnL / 現金 Cash の 4 行）。①は
     現状どの単独パネルにも無い新集約なので、Run Result（成績＝sharpe 等・姉妹 ADR-0037 でポップアップ化）とは役割が分かれる。

   詳細 drive は **退役する dock tile の format 関数をそのまま再利用**し、ターゲットを dock tile view からホバー card へ
   付け替えるだけ（①の口座サマリーは Live=telemetry＋account、Replay=`PortfolioSnapshot` から集約する新 formatter）。

5. **両モード常時表示・content-derived プレースホルダ**。Replay / LiveManual / LiveAuto の全モードでバーを常時表示する
   （現 3 パネルの全モード可視を踏襲）。account / portfolio snapshot が未着のときは各数値を **「—」** プレースホルダで出し、
   モード切替でバーが消えない（レイアウトが上下に動かない）。

6. **factory base group（ADR-0020）を概念ごと退役**。姉妹 ADR-0037 が base 集合を 3 窓に縮めた残りを本 ADR が全部外す
   ので、base dock 窓は 0 になる。`FormFactoryBaseGroup` / `BaseDockWindowIds` の base singleton 列挙・no-resume boot の
   factory grouping 配線を退役する。first-launch dock = `chart` のみ（chart は universe 駆動で base group の member では
   ない）。`DockShape.IsCoreKind` は姉妹 ADR-0037 で既に空集合へ畳まれており本 ADR でも空のまま。

7. **永続化は forward-compat skip のみ**。退役 3 kind は `floatingWindows` 次元から外れる。既存保存レイアウトが
   `"buying_power"` / `"orders"` / `"positions"` を名指しても、`FloatingWindowCatalog.TryGet=false` で **spawn が skip**
   される（`startup` 退役と同じ forward-evolution discipline・findings 0008 §3）。**migrate 不要・スキーマ追加 0**。バー
   自体は固定 anchor（座標を持たない）・可視は派生なので **何も保存しない**。

## Considered Options

- **採用：ヘッダー直下の全幅サマリーバー・4 指標スロット・主数値＋色・ホバーで現パネル詳細・両モード常時・永続ゼロ**。
  owner の「ヘッダーの下にアイコン＋summary」「ホバーで今のパネルと同じ詳細」言明に直結。format 関数を再利用し removal は
  純減（catalog kind / dock 分岐 / base 窓 / LivePanelTileView 配線 / 永続次元 / factory group）。
- **不採用：dock panel のまま縮小表示**。パララックス Content に乗ったままなので **パンで動く＝「ヘッダー直下に固定」に
  ならない**。owner 要望と非互換。
- **不採用：バーに 2 値インライン併記（例「3.17k +883」）**。情報量は多いが画像（アイコン下げ数値 1 つ）のシンプルさから
  離れる。owner は「主数値 1 つ＋色」を選択（§Decision 2）。
- **不採用：実アイコン PNG をブロッカーにする**。アセット待ちで実装が止まる。owner は「仮の 3D 物体（cube/sphere）で
  とりあえず」を選択（§Decision 3）。
- **不採用：Live の純資産を「—」にしモード非対称にする**。owner は「C# 側で導出」を選択（§Context・§Decision 4）。
- **不採用：データがある時だけバーを出す**。モード/接続でバーが出没しレイアウトが上下する。owner は「常に表示・未取得は
  —」を選択（§Decision 5）。

## Consequences

- `ScreenSpaceOverlay` Canvas にアカウントサマリーバー GameObject を追加（`Content` 配下では **ない**＝奥行き層に乗らない・
  メニューバー直下・全幅）。scene 再ビルドの要否は findings 0126 に記録。
- `BackcastWorkspaceRoot`：`SpawnBaseDockWindows`／`BaseDockWindowIds`（3→0 base）／`FormFactoryBaseGroup`（退役）／
  `LivePanelTileView` 配線（`_buyingPowerView` / `_ordersView` / `_positionsView` を退役）／`PushReplayTiles`・
  `PushLiveTiles` の 3 tile drive ターゲットをバー＋ホバー card へ付け替え／Live 純資産導出（`Cash + Σ(qty×avg+uPnL)`）／
  ホバーポップアップの open/close を実装。
- `FloatingWindowCatalog` の `KIND_BUYING_POWER` / `KIND_ORDERS` / `KIND_POSITIONS` と `DockShape.IsDockKind` の該当分岐を
  退役。dock kind は `chart` のみ（姉妹 ADR-0037 と合算）。
- 永続（`CaptureLayout` / restore）は 3 kind を **書かない・読まない**。既存 doc の該当 geometry は無視。バーは非永続。
- ADR-0017 D1 / ADR-0018 / ADR-0020 を **無改変**のまま点 supersede（本 ADR が差分を持つ）。姉妹 ADR-0037 とは独立した
  ファイルだが、両者で base singleton を全退役する旨を相互参照。
- CONTEXT.md glossary の `Replay portfolio projection`／`Hakoniwa`／`floating window`／`base tile の集合`／factory base
  group 関連項を 3 種退役に整合し、新項 `アカウントサマリーバー` を追加（findings 0126 と同時）。
- AFK 正本：アカウントサマリーバーの probe を新設（4 スロットの主数値が VM/snapshot の実値と一致／①の数値色が含み損益の
  符号で緑/赤／未取得時「—」／ホバーで現パネルと同一の詳細文字列が出る（②③④は format 関数 byte 一致・①は口座サマリー）／
  Live 純資産＝`Cash + Σ(qty×avg+uPnL)` の導出一致／両モードで常時表示／**pan で動かない（screen-anchored）**／**永続
  しない**（save→boot で復元しない）／退役 kind は restore で skip／base dock 窓 0＝dock=chart のみ）。実装着手前に
  `behavior-to-e2e` を formal invoke する。実 pan の奥行き目視と実アイコン差し替えは owner HITL。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。バーの高さ・
アイコン枠の描画方式（RenderTexture→RawImage の細部）・ホバー card の anchor / 出現遅延・Live 純資産導出の正確な式と
丸め・除去する正確なシンボルなどの下位事実は本 ADR に書き戻さず、`docs/findings/0126` に記録し本 ADR を「方針: ADR-0038」
として参照する。
