# findings 0112 — チャート窓タイトルに銘柄ラベル `<id> <name>` を表示（#140）

> Slice: #140 / 関連: [[CONTEXT.md::銘柄表示ラベル（<id> <name>）]] / picker 由来の整形は findings 0024・
> 0105。**番号注記**: issue 本文は `docs/findings/0111-chart-window-instrument-label-in-title.md` を
> 名指していたが、issue がオープンされた後に別 finding（`0111-kabu-live-chart-not-updating-investigation.md`）
> が 0111 を消費したので本 finding は **0112** で採番。issue 本文の参照は本ファイルへ読み替える。

---

## 1. 何が間違っているか（症状）

各チャート窓（id `chart:<iid>`・back plane / `_dockWindows`）の窓枠 chrome タイトルが **全インスタンス共通の
固定文字列 `"Chart"`**（`FloatingWindowCatalog.KIND_CHART` の `spec.title`）になっており、複数のチャート窓を
並べたとき**どの窓がどの銘柄か区別できない**。picker 行は `<id> <name>` 形式（例「7203.TSE トヨタ自動車」）で
出ているので、ユーザは「picker で選んだ銘柄」と「タイトルが Chart の窓」を視線でしか結べない。

## 2. 設計の木（owner と grill 済み・#140 issue 本文の凍結決定）

### D1. 整形は picker と同一・**共有 helper** に抽出

- 整形ルール: `<id> <name>`。`name` が **null / 空 / `id` と一致** の 3 ケースで **id 単独へ collapse**
  （`7203.TSE 7203.TSE` の二重表示を防ぐ — `_snapshot_to_list_result` が CompanyName 欠落時に id を
  CompanyName に代入する仕様の合成防衛策。findings 0024 picker 行ロジックの de-dup を共有）。
- 抽出先: `Assets/Scripts/Universe/InstrumentLabel.cs`（新規）に
  `public static class InstrumentLabel { public static string Compose(string id, string name); }`。
  `PickerRow.Candidate(id, name, alreadyAdded)` はこの helper を呼ぶよう書き換え、chart 窓の
  タイトル解決も同じ helper を経由する。**整形 drift（picker と chart 窓で collapse 条件が片方
  だけ更新されて分岐する）を構造的に断つ**のがこの抽出の主目的。
- id は **venue suffix 付きフル id**（picker と一致）。コードや venue へ切り詰めない（picker と
  異なる短縮を入れると drift する）。

### D2. name の唯一の source = `list_instruments()` RPC（chart 窓は **cache-only / mode-agnostic** seam で読む）

- universe SoT（`_scenario.Universe`）にも、poll state JSON `per_instrument[<iid>]` にも name は無い
  （`AvailableInstrumentsResult.Names` だけが name を運ぶ）。
- chart 窓側の seam = `IAvailableInstrumentsProvider.TryGetName(iid, out name)`（**code-review altitude
  fix 2026-06-25**）。Backend 実体は picker の Query が Ready を返した時点で id→name を
  `Dictionary<string,string> _nameByIid` に蓄積し、TryGetName は O(1) でそれを引く。
  - **cache-only**: TryGetName は決して fetch を起こさない（picker が唯一の fetch 起動源）。layout 復元で
    N チャート窓を生成しても N 個の concurrent `PickerInstrumentFetch` thread が走らない。
  - **mode-agnostic**: name は per-instrument の属性であり (mode, end) に依存しない。Replay→Live mode
    flip 後でも `_nameByIid` は累積保持されているのでチャート窓 title は失われない。
- 旧設計（Query + Ids/Names 並列配列の `IndexOf` で O(n) スキャン）は code-review #2/#3/#4 で altitude
  不適切と指摘され、TryGetName seam へ移行（review #2 = (mode, end) coupling 解消、#3 = fetch trigger
  契約縮小、#4 = O(n)→O(1)）。
- **2 つ目の name source を立てない**（例: chart 用に listed_info を別経路で fetch する dedicated RPC を
  追加するのは禁止）。SoT 二重化禁止 — CONTEXT.md「銘柄表示ラベル」項に明記。

### D3. 更新モデル = **spawn 時に取れた分だけ**（owner 決定）

- チャート窓 spawn 時点で provider cache を **1 回読むだけ**で解決。後追いの live 更新／タイトル
  差し替え seam を意図的に**持たない**。
- 取れれば `<id> <name>`、未取得（`Loading` / `Empty` / `Error` / `Unsupported` / `NotConnected` /
  `EndUnset`、または `Ready` だが iid が `Ids` に無い）なら **id 単独で確定**。
- トレードオフ:
  - **常用パス**: picker から `+ Add` → `Universe.Add(iid)` → `Universe.Changed` →
    `SyncChartWindowsToUniverse` → `SpawnChartWindowAt` の経路では、picker が今しがた provider を
    Query 済みで cache が温いので **必ず name が出る**。
  - **cold path**: layout 復元（`RestoreFloating`）など picker 未経由 spawn では provider cache が
    まだ温まっていない瞬間があり **id 単独になり得る**（owner 承知）。後追い差し替え seam を
    入れない代わりに、画面再描画やマウス操作で問題が顕在化しにくい（タイトル文字列は静的）。

### D4. **共有 instance / 共有 id→name index**（review altitude fix で (mode, end) 共有は撤回）

- picker と chart 窓は **同じ `BackendAvailableInstrumentsProvider` instance** を参照。Backend は内部に
  `_nameByIid` Dictionary を持ち、picker の Query が Ready を返した瞬間に id→name を merge する。
- 「常用パスは温い」は picker が cache を warm → 同 instance の TryGetName を chart spawn が引く、で
  成立。**(mode, end) 共有 helper は撤回**: chart 窓側の (mode, end) 依存を消したので、`DriveSidebarContext`
  だけが (mode, end) を導出する（findings 0084 そのまま、変更なし）。
- review #5 (`_footerMode` 非 null 前提) も自然に解消（chart 窓側がもう `_footerMode` を読まない）。

### D5. seam: factory で title を上書きする・spec を不変に保つ

- `FloatingWindowSpec.title` は **kind ごとの固定 caption**（"Chart" / "Buying Power" / …）。これは
  **そのまま不変に残す**（fallback / 多インスタンス kind の汎用 caption として有効）。
- chart 窓に限り、factory `BackcastWorkspaceRoot.BuildDockWindowFrame(spec, id)` が **id から iid を
  抜き** → `_provider.TryGetName(iid, out var name)` で cache を引き → `InstrumentLabel.Compose` を
  通して **runtime 解決した title** を `DockWindowFrame.Build` に渡す。`spec.title` は chart 以外の
  dock kind では従来通り使う。
- `DockWindowFrame.Build` のシグネチャは無変更（既に `string title` を取る）。
- 解決失敗（provider 未設定 / chart 以外 kind）時の fallback は `spec.title`（"Chart"）。

### D6. **`_provider` 構築順序の structural invariant**（code-review #1 受け / F5 owner 決定 2026-06-25 で 1 段 defense へ削減）

- `_dockWindows = new FloatingWindowController(_dockLayer, _catalog, BuildDockWindowFrame)` で factory
  delegate が captured されるよりも**前**に `_provider = new BackendAvailableInstrumentsProvider(_host)`
  を実行する。これで「`_dockWindows` が chart を spawn し得る瞬間には `_provider` が必ず非 null」が
  positional ではなく structural に成立する。
- **この invariant の唯一 defense は ordering fix**。null guard と `CHART-TITLE-09` AFK pin は F5 owner
  決定（2026-06-25・選択肢 C）で retire — reorder regression は production NRE で大声 fail させ、dev 時の
  検知に有利な方を取る（graceful blank chrome よりも LOUD failure が望ましいという owner 判断）。
- **`CHART-TITLE-09` は retire（欠番）**。番号は付け直さず欠番のまま残し、historical reference（commit / PR
  コメント / findings 0112 旧版）からの参照を破壊しない。

## 3. 影響箇所（ファイル / シンボル）

- 新規: `Assets/Scripts/Universe/InstrumentLabel.cs` — `Compose(id, name)` helper（picker 行 + chart
  窓 chrome の 2 surface 限定。OrderTicketView / ChartView 内 title は未統一・follow-up）。
- 編集: `Assets/Scripts/Universe/AvailableInstruments.cs` — `IAvailableInstrumentsProvider` に
  `TryGetName(iid, out name)` を追加（cache-only / mode-agnostic seam・review altitude fix）。
  `MockAvailableInstrumentsProvider` は name を持たないので `return false;`。
- 編集: `Assets/Scripts/Universe/BackendAvailableInstrumentsProvider.cs` — `_nameByIid` Dictionary を
  追加し、Fetch が Ready を caching する瞬間に id→name を merge。`TryGetName` は `_lock` 配下で O(1)
  Dictionary lookup（fetch を起こさない）。
- 編集: `Assets/Scripts/Universe/InstrumentPickerController.cs` — `PickerRow.Candidate` が
  `InstrumentLabel.Compose` を呼ぶ。**picker の見た目は不変**（同じ collapse 規則）。
- 編集: `Assets/Scripts/Live/BackcastWorkspaceRoot.cs` —
  - `_provider` フィールド化 + 構築順を `_dockWindows` controller の**前**へ移動（review #1 structural）。
  - `BuildDockWindowFrame` の chart 分岐で `ResolveChartTitleForId` を呼ぶ。
  - `ResolveChartTitleForId` は `_provider.TryGetName` を読むだけ（(mode, end) も Query も使わない）。
- 編集: 既存 `IAvailableInstrumentsProvider` test stub（`UniverseSidebarE2ERunner.StubProvider` /
  `RecordingProvider`）に `TryGetName` を no-op で実装（interface 要件）。
- 新規: `Assets/Tests/E2E/Editor/ChartWindowTitleE2ERunner.cs`（+ `.md` 台本） — AFK probe。
  CHART-TITLE-01..08, 10..12 を Compose 1 回で table-driven 駆動（review #6 で 12+ OpenScene → 1 へ）。
  09 は F5 owner 決定 2026-06-25 で retire（欠番）。
- 編集: `scripts/run-all-tests.ps1` 経由で `-Method ChartWindowTitleE2ERunner.Run` を rollup に合流。
- 編集: `CONTEXT.md` — 「銘柄表示ラベル」項を新設（本 finding と並行で済）。

## 4. RED → GREEN 手順（AFK probe・findings 0112 タグ `CHART-TITLE-01..09`）

`ChartWindowTitleE2ERunner` を新設し、以下の Section で 2 分岐＋境界を assert（**provider seam は
`TryGetName(iid, out name)` ＝ id→name cache の直接読み**。Query は呼ばれない）:

| Action-ID         | 何を assert                                                                                            |
|-------------------|--------------------------------------------------------------------------------------------------------|
| `CHART-TITLE-01`  | provider cache に `{7203.TSE → トヨタ自動車}` が入っているとき タイトル = `7203.TSE トヨタ自動車`   |
| `CHART-TITLE-02`  | provider cache に該当 iid のエントリが無いとき タイトル = `7203.TSE` 単独                              |
| `CHART-TITLE-03`  | provider cache に `{7203.TSE → "7203.TSE"}`（name == id collapse）のとき タイトル = `7203.TSE`         |
| `CHART-TITLE-04`  | provider cache 全空（cold）のとき タイトル = `7203.TSE` 単独                                           |
| `CHART-TITLE-05`  | （provider Kind 5 通りの fallback symmetry — TryGetName は Kind に依らないので全状態で id 単独）       |
| `CHART-TITLE-06`  | `InstrumentLabel.Compose` 単体: name 通常 / null / "" / id一致 の 4 通り                              |
| `CHART-TITLE-07`  | drift gate — `PickerRow.Candidate(id, name, false).Label` ≡ `InstrumentLabel.Compose(id, name)`        |
| `CHART-TITLE-08`  | provider cache に**別 iid**（9984.TSE）の name のみあるとき → target iid (7203.TSE) は id 単独（他 iid の name が漏れない） |
| `CHART-TITLE-09`  | **retire（欠番・F5 owner 決定 2026-06-25）** — 旧 construction-order guard `if (_provider == null) return specTitle;` を撤去し、reorder regression は production NRE で fail させる方針へ pivot |
| `CHART-TITLE-10`  | **review F2 / listed_info rename semantics** — `BackendAvailableInstrumentsProvider._nameByIid[id] = nm` が 2 回目の Ready で上書きされる（`TryAdd` regression に落ちたら RED）。merge ヘルパ `MergeNameIndex` を AFK が reflection invoke |
| `CHART-TITLE-11`  | **review F3 / malformed-id fallback** — `BuildDockWindowFrame(spec=KIND_CHART, id="chart:")` を reflection 直接 invoke し、`ResolveChartTitleForId` の `string.IsNullOrEmpty(iid)` 枝が `spec.title`（`"Chart"`）を返す |
| `CHART-TITLE-12`  | **review F4 / 0112 §1 本旨** — 2 つの chart 窓 (7203/9984) を同時 spawn し、各 TitleBar/Title が独立に `<id> <name>` を描画する（complete user wedge: side-by-side 区別可能）。teardown で双方 despawn 完了も assert |

**RED 化のリトマス**:
- `BuildDockWindowFrame` の title 上書きを削除 → 01..05, 08, 12 が "Chart" 比較で RED
- `InstrumentLabel.Compose` の collapse 条件（name == id / null / 空）を消す → 03 / 06 RED
- `PickerRow.Candidate` を helper を経由せず直書きに戻す → 07 RED
- `BackendAvailableInstrumentsProvider.MergeNameIndex` の `_nameByIid[id] = nm` を `_nameByIid.TryAdd(id, nm)` に退行させる → 10 が rename 上書き失敗で RED
- `ResolveChartTitleForId` の `if (string.IsNullOrEmpty(iid)) return specTitle;` ガードを消す → 11 が malformed-id で `InstrumentLabel.Compose("", null) == ""` を返し "Chart" 比較で RED
- （09 は F5 owner 決定 2026-06-25 で retire — 旧 `if (_provider == null) return specTitle;` ガードは撤去済で、reorder regression は production NRE 検知に倒した）

**走らせ方**:
```
pwsh scripts/run-live-e2e.ps1 -Method ChartWindowTitleE2ERunner.Run
pwsh scripts/run-all-tests.ps1     # rollup に [E2E CHART-TITLE-01..08 PASS] が並ぶ
```

## 5. 既存ゲートへの影響（GREEN 維持確認）

- `FloatingWindowE2ERunner` — chart 窓を多数 spawn するが title 文字列は assert していない（kind/spec
  の存在チェックのみ）→ GREEN 維持。
- `ChartUniverseSyncE2ERunner` / `ChartUniverseWriteConsistencyE2ERunner` — `_chartViews` の populate を
  assert（タイトル文字列 assert なし）→ GREEN 維持。
- `ChartSpawnPreviewE2ERunner` — host.RequestReplayChartPreview の引数のみ assert → GREEN 維持。
- `UniverseSidebarE2ERunner` — picker の Label を assert する Section があり（`UniverseSidebarE2ERunner.cs:1029`
  付近）、`PickerRow.Candidate` の `<id> <name>` 文字列を期待。`InstrumentLabel.Compose` の collapse 規則は
  既存と**完全に同値**なので GREEN 維持。
- pytest — name supply は Python 側で既に整っており、本 slice の編集対象に Python は無い → 影響なし。

## 6. 非目標 / 範囲外

- **後追い live 更新は実装しない**（D3）。`list_instruments` の後刻 cache 更新（venue 再ログイン後の
  fetch_instruments 完了など）でチャート窓タイトルを差し替える seam を**意図的に持たない**。
- **picker の見た目を変えない**（PickerRow.Candidate の出力 Label は等値・de-dup 規則も等値）。
- **strategy_editor / order / 4 base dock 窓** には触れない（chart 窓だけ）。
