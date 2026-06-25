# ChartWindowTitleE2ERunner — Issue #140 release-gate slice runner

> 方針: [findings 0112](../../../../docs/findings/0112-issue140-chart-window-instrument-label-in-title.md) /
> [CONTEXT.md::銘柄表示ラベル（`<id> <name>`）](../../../../CONTEXT.md)。

## 何を gate するか

各チャート窓（id `chart:<iid>`・back plane `_dockWindows`）の窓枠 chrome タイトルが、kind 固定の
**`"Chart"`** ではなく **銘柄ラベル `<id> <name>`** を表示する（`<name>` 未取得時は **id 単独で fallback**）。
整形ロジックは [`InstrumentLabel.Compose(id, name)`](../../../Scripts/Universe/InstrumentLabel.cs) を picker と
chart 窓の両方が共有する（drift を構造的に断つ）。`<name>` の唯一の source は
`engine.inproc_server.list_instruments()` RPC ＝ `IAvailableInstrumentsProvider.Query` の `Names` 並列配列。

更新モデルは **spawn 時に取れた分だけ**（後追い差し替え seam を持たない・owner 決定 2026-06-25）。常用パス（picker
経由 add）では cache が温く名前が出る／layout 復元など picker 未経由 spawn では cache 未温の瞬間に id 単独になり得る。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath <abs> \
        -executeMethod ChartWindowTitleE2ERunner.Run -logFile <abs>
# expect: [E2E CHART WINDOW TITLE PASS] ... / exit=0  (確認は Bash `grep -a "CHART-TITLE"`)
# compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
# 単独走行: pwsh scripts/run-live-e2e.ps1 -Method ChartWindowTitleE2ERunner.Run
# rollup 合流: pwsh scripts/run-all-tests.ps1 -Method ChartWindowTitleE2ERunner.Run
```

## RED→GREEN リトマス（findings 0112 §4 と同期）

- (a) `BackcastWorkspaceRoot.BuildDockWindowFrame` の chart 分岐で title を **runtime 解決せず spec.title をそのまま渡す**
  → `CHART-TITLE-01..05, 08, 12` が "Chart" 比較で RED。
- (b) `InstrumentLabel.Compose` の collapse 条件（`name == id` / 空 / null）を消す → `CHART-TITLE-03`（id 一致 collapse）
  と `CHART-TITLE-06`（unit 4 通り）が RED。
- (c) `PickerRow.Candidate` を helper を経由せず直書きに戻す → `CHART-TITLE-07`（drift gate）が RED。
- (d) ~~`ResolveChartTitleForId` の `if (_provider == null) return specTitle;` ガード~~ → **`CHART-TITLE-09` は retire（欠番・F5 owner 決定 2026-06-25）**。旧ガードは撤去し、reorder regression は production NRE で fail させる方針へ pivot。
- (e) `BackendAvailableInstrumentsProvider.MergeNameIndex` の `_nameByIid[id] = nm` を `_nameByIid.TryAdd(id, nm)` に退行
  → `CHART-TITLE-10`（rename 上書き semantics）が「後 name が反映されない」で RED。
- (f) `ResolveChartTitleForId` の `if (string.IsNullOrEmpty(iid)) return specTitle;` ガードを消す
  → `CHART-TITLE-11`（malformed-id fallback）が `InstrumentLabel.Compose("", null) == ""` で "Chart" 比較失敗して RED。

## 操作一覧表

| Action ID         | 行動 / 状況                                                                              | 入口（file:line）                                                                                                                                                                                                                                  | 観測点                                                                  | 自動判定 | カバー状態 |
|-------------------|------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------|---------|-----------|
| `CHART-TITLE-01`  | provider=Ready{ids:[7203.TSE], names:[トヨタ自動車]} で chart:7203 を spawn               | [BackcastWorkspaceRoot.SpawnChartWindowAt](../../../Scripts/Live/BackcastWorkspaceRoot.cs):1212 / [DockWindowFrame.Build](../../../Scripts/FloatingWindow/DockWindowFrame.cs):26                                                                       | `Window_chart:7203.TSE/TitleBar/Title` の Text.text                      | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-02`  | provider=Ready{ids:[7203.TSE], names:[]}（Names 空配列）で spawn                          | 同上                                                                                                                                                                                                                                              | 同上 = `7203.TSE`                                                       | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-03`  | provider=Ready{ids:[7203.TSE], names:["7203.TSE"]}（name==id collapse）で spawn          | 同上                                                                                                                                                                                                                                              | 同上 = `7203.TSE`（二重表示禁止）                                       | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-04`  | provider=Loading（fetch in flight・cache 未温の瞬間）で spawn                              | 同上                                                                                                                                                                                                                                              | 同上 = `7203.TSE`（id 単独 fallback）                                   | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-05`  | provider=Empty / NotConnected / Unsupported / Error / EndUnset の 5 状態を順に試して spawn | 同上                                                                                                                                                                                                                                              | 同上 = `7203.TSE`（全状態で id 単独 fallback）                          | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-06`  | `InstrumentLabel.Compose` 単体: `(id, "トヨタ自動車")` / `(id, null)` / `(id, "")` / `(id, id)` | [InstrumentLabel.Compose](../../../Scripts/Universe/InstrumentLabel.cs)                                                                                                                                                                            | 戻り値 = `"7203.TSE トヨタ自動車"` / `id` / `id` / `id`                | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-07`  | drift gate — `PickerRow.Candidate(id, name, false).Label` ≡ `InstrumentLabel.Compose(id, name)` | [PickerRow.Candidate](../../../Scripts/Universe/InstrumentPickerController.cs):37                                                                                                                                                                  | 等値比較を 4 通り                                                       | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-08`  | provider に別 iid (9984.TSE) の name のみ cached、target iid 不在で spawn                | [BackcastWorkspaceRoot.SpawnChartWindowAt](../../../Scripts/Live/BackcastWorkspaceRoot.cs):1212                                                                                                                                                       | 同上 = `7203.TSE`（他 iid の name は漏らない・iid match のみ）        | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-09`  | **retire（欠番・F5 owner 決定 2026-06-25）** — 旧 construction-order guard `if (_provider == null) return specTitle;` を撤去し、reorder regression は production NRE で fail させる方針へ pivot | —                                                                                                                                                                                                                                                | —                                                                       | —       | 欠番        |
| `CHART-TITLE-10`  | review F2 — listed_info rename / cache 上書き semantics: 同 iid に 2 回 Ready を merge し、後 name が前 name を上書きする | [BackendAvailableInstrumentsProvider.MergeNameIndex](../../../Scripts/Universe/BackendAvailableInstrumentsProvider.cs)（反射 invoke）                                                                                                                                                                                                | TryGetName(iid) が 2 回目の name を返す（`=` の上書き semantics・`TryAdd` regression に落ちたら RED） | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-11`  | review F3 — `ResolveChartTitleForId` malformed-id branch: `BuildDockWindowFrame(spec=KIND_CHART, id="chart:")` を反射直接 invoke | [BackcastWorkspaceRoot.BuildDockWindowFrame](../../../Scripts/Live/BackcastWorkspaceRoot.cs):843（反射 invoke）                                                                                                                                                                                                                                | 返り frame の TitleBar/Title text = `"Chart"`（spec.title fallback・`string.IsNullOrEmpty(iid)` guard 検査） | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-12`  | review F4 / 0112 §1 本旨 — 2 つの chart 窓 (7203/9984) を同時 spawn し、各窓が独立に `<id> <name>` を描画 | [BackcastWorkspaceRoot.SpawnChartWindowAt](../../../Scripts/Live/BackcastWorkspaceRoot.cs):1212 / `_chartViews` で両 iid を維持                                                                                                                                                                                                              | 7203.TSE/TitleBar/Title = `"7203.TSE トヨタ自動車"` ∧ 9984.TSE/TitleBar/Title = `"9984.TSE ソフトバンクグループ"`（side-by-side 区別可能） | ✅ AFK  | 自動(E2E済) |
| `CHART-TITLE-13`  | 実ブート＋picker 経由 add で「7203.TSE トヨタ自動車」が窓枠に映ること（owner 目視）           | owner playmode                                                                                                                                                                                                                                    | 実 picker 行と窓枠タイトルの文字列が一致                                | 👀 HITL | HITL専用（実 DuckDB / 実シーン描画は owner 環境専有） |

## 設計（runner 内部）

- **composition**: `EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single)` →
  `ResolvePaths`/`BuildWorkspace` を Reflection invoke（`ChartUniverseSyncE2ERunner.ComposeRoot` と同型）。
- **stub 注入**: `BuildWorkspace` 後に Reflection で `BackcastWorkspaceRoot._provider` フィールドへ `StubProvider`
  （`UniverseSidebarE2ERunner.StubProvider` と同型・`Next` を section 毎に書き換える）を差し替える。差し替えは
  Workspace 構築直後（picker からの初回 Query が走る前）なので、composite 状態（footer mode 既定 = Replay /
  scenario.end 既定）はそのまま使う。
- **spawn 駆動**: section ごとに `_scenario.Universe.Add(iid)` → `SyncChartWindowsToUniverse` の Changed-subscription
  が `SpawnChartWindowAt` を発火（実 production パス）。section 末で `DespawnChartWindow` も同様にして次 section の
  cache 影響を避ける。または `_dockWindows.RectOf(DockShape.ChartId(iid))` で window root を引いて
  `Find("TitleBar/Title").GetComponent<Text>().text` を読む（一文字一文字の文字列比較）。
- **non-vacuity**: 各 section の手前で「provider が期待 Kind を返している」「window root が live」を gating。
  `Ready` 系は `assert provider.Next.Kind == Ready` ＋ `assert Names[i] is the expected name`。
- **Action-ID PASS タグ**: 各 section 末で `Debug.Log("[E2E CHART-TITLE-NN PASS] <要約>")` を吐く（rollup
  正規表現は単一トークン・E2E-CONVENTIONS §5）。最終 `[E2E CHART WINDOW TITLE PASS]` は人間向け要約。
- **finally**: `Universe.Clear`・`_chartViews` を rewind、scene を破棄しない（Editor が共有スコープ）。

## 既存ゲートとの非衝突

- `FloatingWindowE2ERunner` … chart の spawn は assert するが title 文字列は assert しない → 無影響。
- `ChartUniverseSyncE2ERunner` / `ChartUniverseWriteConsistencyE2ERunner` … `_chartViews` populate / geometry を
  assert（title 無関与）→ 無影響。
- `ChartSpawnPreviewE2ERunner` … `host.RequestReplayChartPreview` の引数を assert（title 無関与）→ 無影響。
- `UniverseSidebarE2ERunner` … picker 行の `.Label`（`<id> <name>`）を assert。`InstrumentLabel.Compose` への抽出は
  picker の **出力 Label を完全に保存** するので無影響。`CHART-TITLE-07` がこの不変条件を drift gate として直接 pin する。

## 非目標

- 実 venue ログイン / 実 listed_info DuckDB / 実 picker クリック → `CHART-TITLE-13`（HITL）。
- 後追い live 差し替え seam の検証（findings 0112 で意図的に持たない決定）。
- 旧 `CHART-TITLE-09`（construction-order guard 検証）は F5 owner 決定 2026-06-25 で retire（欠番） — 構築順 invariant の violation は production NRE で fail させる。
