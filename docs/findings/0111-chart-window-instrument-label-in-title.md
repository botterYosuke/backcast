# findings 0111 — チャート窓タイトルに銘柄ラベル（`<id> <name>`）を出す

owner 報告: 「チャートに銘柄名が無いから何のチャートか分からない」。複数のチャート窓を並べても、どれがどの銘柄か区別できない。`/grill-with-docs` で設計の木を固定した記録（pre-implementation）。

## ルート原因（コードで裏取り）

- チャート窓は **floating window（dock plane）** で、窓枠 chrome のタイトルバーは `DockWindowFrame.Build(id, title, …)`（`Assets/Scripts/FloatingWindow/DockWindowFrame.cs:26`）が描く。
- そのタイトル文字列は `BackcastWorkspaceRoot.BuildDockWindowFrame`（`:817`）が **`spec.title` をそのまま渡す**。
- chart の spec は `FloatingWindowCatalog`（`:84`）で **全インスタンス共通の固定文字列 `"Chart"`**。chart は multi-instance（id `chart:<iid>`）だが spec は 1 つなので、**全チャート窓が "Chart" 表示**になる＝区別不能。
- 窓 id は `chart:<iid>`、`DockShape.InstrumentOfChartId`（`Assets/Scripts/FloatingWindow/DockShape.cs:32`）で iid（canonical id 例 `7203.TSE`）をいつでも復元可能 → **コードは追加配線ゼロで出せる**。
- ChartView 内部にもタイトルバー実装はある（`Assets/Scripts/Chart/ChartView.cs:106` `BuildTitleBar` の固定 `"CHART"`）が、チャート窓では `showTitleBar: false`（`BackcastWorkspaceRoot.cs:880`）で**無効**。表示面は窓枠 chrome タイトルが本筋。

## 名前の入手源（裏取り）

- 銘柄名（CompanyName 例「トヨタ自動車」）は **universe SoT（`InstrumentRegistry`）には入っていない**（id の List のみ・`Assets/Scripts/ScenarioStartup/InstrumentRegistry.cs`）。
- state JSON の `per_instrument[id]` は `{ price, ohlc_points, depth }` のみで **name は無い**（`InstrumentOhlcDecoder` / `_backend_impl.py:548-613`）。よって毎 poll の render ループでは名前を出せない。
- 名前の唯一の源は **`list_instruments()` RPC** → `BackendAvailableInstrumentsProvider`（`Assets/Scripts/Universe/BackendAvailableInstrumentsProvider.cs`）。`Query(mode, endDate)` が `AvailableInstrumentsResult { Ids, Names }`（並列配列）を返す。**非同期**（背景スレッド fetch）・**success-only cache**・キーは `(mode, endDate)`・**lazy**（picker が Query した時だけ fetch）。
- mode/end 導出は既存パターンを再利用: `DockShape.IsLiveShape(_footerMode.DisplayMode) ? Live : Replay` ＋ `_scenario.Params.End`（`BackcastWorkspaceRoot.DriveSidebarContext :1294` / `DrivePrune :1308` と同型）。

## 設計の木（owner 決定・確定）

- **D1 表示内容 = コード＋名称**（owner）。窓枠 chrome のタイトルバーに出す（ChartView 内部 title bar は使わない・`showTitleBar:false` のまま）。
- **D2 整形 = picker と同じ `<id> <name>`**（owner）。例「7203.TSE トヨタ自動車」。name が null/空/id と同一なら **id 単独へ collapse**。**id は suffix 付きフル id**（picker と一致・コードに切り詰めない）。
  - 既存規約: `PickerRow.Candidate`（`InstrumentPickerController.cs:37-41`）が `(!empty(name) && name!=id) ? id+" "+name : id`。これを**共有 helper に抽出**して picker と chart 窓で再利用し drift を防ぐ（→ CONTEXT「銘柄表示ラベル」項）。
- **D3 更新モデル = spawn 時に取れた分だけ**（owner・grill 中に live-update 案から訂正）。チャート窓 spawn 時に provider cache を **読むだけ**で iid→name 解決。取れれば「id name」、未取得なら **id 単独**。**後追いの live 更新はしない**・タイトル後差し替え seam も不要（`DockWindowFrame.Build` の title 引数で生成時に確定）。
  - **受容するトレードオフ**: picker 経由で銘柄を add した直後は、その `(mode, end)` の cache が温まっているので名前が出る（常用パス）。layout 復元時など **picker 未経由の spawn では cache 未温で id 単独**になり得る。owner はこれを承知の上で「spawn 時に取れた分だけ」を選択。

## 実装スケッチ（次セッション）

1. **共有 helper 抽出**: `<id> <name>`/collapse ロジックを 1 箇所（例 `InstrumentLabel.Format(id, name)`）に出し、`PickerRow.Candidate` を置換。
2. **provider 参照保持**: `BuildWorkspace` 内 local の `provider`（`:671`）を field 化（chart spawn 経路から参照するため）。
3. **chart 窓タイトル解決**: `BuildDockWindowFrame`（`:807`）の chart kind 分岐で `spec.title` の代わりに `ResolveChartTitle(iid)` を渡す。`ResolveChartTitle` は (a) mode/end を `DriveSidebarContext` と同型で導出、(b) `provider.Query(mode, end)`、(c) Ready なら Ids→Names で iid を引いて `InstrumentLabel.Format(iid, name)`、(d) それ以外（Loading/Empty/Error）は iid 単独。
4. **E2E/AFK gate**: chart 窓 spawn 時、provider が name を持つ場合タイトル=`<id> <name>`、持たない場合=`<id>` を assert する probe。

## ADR は不要

3 条件（① 覆すと高コスト ② 文脈無しに驚く ③ 真のトレードオフ）のうち①が弱い（タイトル文字列の解決方針で、後から live-update へ拡張しても破壊的でない）。D3 のトレードオフは本 findings に固定済み。ADR は立てず findings + CONTEXT glossary で記録する。
