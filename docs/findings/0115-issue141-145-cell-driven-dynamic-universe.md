# findings 0115 — 戦略 cell の動的 universe 編集 `bt.universe.*`（#141-145 / ADR-0031 S1-S5）

方針正本: [ADR-0031](../adr/0031-cell-driven-dynamic-universe-bt-universe-api.md)（accepted・自己保護節あり）。本 findings は
ADR-0031 の下位の実装事実（registry ブリッジの seam / stepper の mid-stream merge / 購読フックの配置 / RED→GREEN・再走手順）を
slice ごとに記録する。ADR-0031 は無改変で「方針: ADR-0031」として参照する。

CONTEXT.md glossary「戦略 cell による動的 universe 編集（`bt.universe.*`・方針 ADR-0031）」（L630-637）が用語の正本。

---

## §S1 (#141) — `bt.universe.add/remove/clear/list` ＋ Python→C# registry ブリッジ（背骨）

### 設計の確定（grill でコード裏取り・2026-06-25）

ブリッジ機構は **#65（`ReplayKernelObserver → engine.last_portfolio → poll lane`）と対称な双方向クライアント**（ADR-0031 D2）:

- **両 bt ハンドルは `Backtester`**: Replay = `Backtester(KernelStepper)`、LiveAuto = `LiveCellBridge` が `Backtester(LiveCellBackend)`
  を組む（`cell_bridge.py:182`）。よって `bt.universe` を **`Backtester` に 1 箇所**追加すれば両モードで効く。
- **write 経路（Python→C#）**: `bt.universe.add/remove/clear` → `EngineUniverseBridge` → `DataEngine.enqueue_universe_edit(op,id)`
  が edit op を `_universe_edits` に積む（cell worker thread・`_lock` で thread-safe）。C# 主スレッドが毎フレーム
  `InprocLiveServer.drain_universe_edits()`（JSON 配列）で drain し、`UniverseBridge.ApplyJson` で `InstrumentRegistry` に適用。
  `InstrumentRegistry.Changed` が発火 → `SyncChartWindowsToUniverse`（既配線 `BackcastWorkspaceRoot.cs:454`）で chart 窓が
  **追加配線ゼロ**で spawn/despawn（D2「chart 反映はタダ」）。
- **read-back 経路（C#→Python）**: C# が registry の現 Ids を `push_universe_ids(ids_json)` で `DataEngine.universe_ids` mirror へ
  push（毎フレーム coalesced＝Ids が変わったときだけ。UI 編集も同経路で拾う・初フレーム self-seed）。`bt.universe.list()` は
  `EngineUniverseBridge.list()` → `DataEngine.get_universe_ids()` で **mirror を読む**＝Python は自前 SoT を持たない（D2）。
- **clear → `ReplaceAll(empty)`**: `InstrumentRegistry` に `Clear()` は無い。空 list の wholesale replace が idempotent な「空にする」
  primitive（非空→`Changed` 発火、空→`SequenceEqual` で発火せず）。`PruneRetain`（system-prune backdoor・Editable bypass）は
  **使わない**——`bt.universe.*` は user-triggered populate であって autonomous prune ではない（D7）。

### 実装した seam（file:line）

| 層 | 変更 |
|---|---|
| `python/engine/core.py` | `DataEngine` に `_universe_edits` / `universe_ids` チャネル ＋ `enqueue_universe_edit` / `drain_universe_edits` / `push_universe_ids` / `get_universe_ids`（全 `_lock` 下） |
| `python/engine/strategy_runtime/universe_bridge.py`（新規） | `EngineUniverseBridge`（engine チャネルに add/remove/clear/list を委譲・marimo-free） |
| `python/engine/strategy_runtime/backtester.py` | `UniverseBridge` Protocol ＋ `_UniverseHandle`（`bt.universe`）＋ `_normalize_instrument_id`（空 id→ValueError）。`Backtester.__init__`/`from_scenario` に `universe_bridge` 引数。`NoScenarioBacktester.universe` = `_NoScenarioUniverse`（fail-closed guidance） |
| `python/engine/_backend_impl.py` | `_build_notebook_bt` が `universe_bridge=EngineUniverseBridge(self.engine)` を渡す（Replay 経路） |
| `python/engine/inproc_server.py` | `drain_universe_edits()`（JSON）/ `push_universe_ids(ids_json)` RPC（`force_stop_replay` と同じく engine 直） |
| `Assets/Scripts/Universe/UniverseBridge.cs`（新規） | pure C#：`ParseEdits`（JSON 配列を `{"items":...}` で wrap して `JsonUtility`）＋ `Apply`（add/remove/clear→registry）＋ `ApplyJson` |
| `Assets/Scripts/Live/WorkspaceEngineHost.cs` | `DrainUniverseEdits()` / `PushUniverseIds(idsJson)`（main thread・GIL・`_serverReady` self-guard） |
| `Assets/Scripts/Live/BackcastWorkspaceRoot.cs` | `DriveUniverseBridge()` を `Update()` に追加（drain→apply→coalesced push）＋ `UniverseIdsToJson` ＋ `_lastPushedUniverse` |

> **LiveAuto 経路の bridge 配線は S4 で行う**（S1 では `LiveCellBridge` は `universe_bridge` 無しで `Backtester` を組むので、LiveAuto の
> `bt.universe` は S4 着地まで fail-closed）。S1 のスコープは Replay の mutation＋chart 反映＋読み返し（ADR-0031 の S1 範囲）。

### gate（Action-ID）

- **Python 半（pytest `test_bt_universe_bridge.py`・`@pytest.mark.scenario`）**: `BTUNIV-01`（add/remove/clear が op を enqueue）/
  `02`（add round-trip→list 反映）/ `03`（remove→list drop）/ `04`（clear→list 空）/ `05`（**D2：list は host mirror・enqueue
  しただけでは list 不変／UI 編集も mirror 経由で見える**）/ `06`（空 id→ValueError）/ `07`（bridge 無し→RuntimeError fail-closed）/
  `08`（unused run は edit ゼロ＝#24 golden 不変）。in-memory `_FakeRegistryHost` が C# の drain→`InstrumentRegistry` apply→push を
  忠実模写（first-occurrence dedup・idempotent）。**11 passed**。
- **C# 半（AFK `UniverseBridgeE2ERunner.cs`・Python-FREE）**: `BTUNIV-09`（add→実 registry＋chart spawn）/ `10`（remove→despawn・
  survivor 残）/ `11`（clear→全 chart despawn）/ `12`（read-back seam：engine edit JSON→ops の `ParseEdits`／registry Ids→
  `push_universe_ids` 配列の `UniverseIdsToJson`）。engine の `drain_universe_edits()` と同じ JSON 形を `UniverseBridge` に食わせ、
  **実 `BackcastWorkspaceRoot` 合成の実 registry＋実 chart cascade** を駆動（source だけ fake＝2 ゲート分割）。**4 PASS / 0 FAIL**。

### RED→GREEN litmus

- Python: `DataEngine.enqueue_universe_edit` の append を消す（write ch 断）→ BTUNIV-01/02 RED。`push_universe_ids` の mirror 書きを
  消す（read ch 断）→ BTUNIV-02/05 RED。
- C#: `UniverseBridge.Apply` の `registry.Add` case を消す → BTUNIV-09 RED（chart spawn せず）。`registry.Remove` → BTUNIV-10 RED。
  `clear`→`ReplaceAll(empty)` → BTUNIV-11 RED。`UniverseIdsToJson`/`ParseEdits` を壊す → BTUNIV-12 RED。

### 再走手順

```
cd python && uv run pytest tests/test_bt_universe_bridge.py -v        # BTUNIV-01..08
pwsh scripts/run-live-e2e.ps1 -Method UniverseBridgeE2ERunner.Run     # BTUNIV-09..12（4 PASS）
pwsh scripts/run-live-e2e.ps1 -CompileOnly                            # error CS 0 件
pwsh scripts/run-all-tests.ps1 -Method UniverseBridgeE2ERunner.Run    # merged rollup
```

### 教訓

- **両 bt ハンドルが同一 `Backtester`** だったので `bt.universe` は 1 箇所追加で両モード対応——LiveAuto 用に別ハンドルを作る必要は無い
  （cell_bridge が `Backtester(LiveCellBackend)` を組む構造を grill で裏取り）。
- 新規 AFK runner は sibling の `using` を丸写し——`using System.Linq` を落として `IReadOnlyList<string>.Contains` が
  `MemoryExtensions.Contains(ReadOnlySpan<char>,…)` に誤束縛され CS7036 で初回 compile を 1 往復無駄にした（[[backcast-unity-afk-probe-runner]] / #123 既出の罠を再演）。

---

## §S2 (#142) — Replay 走行中追加の mid-stream join

### 設計の確定

`KernelStepper` は start 時に固定 bar list（`self._bars`）を `self._index` で iterate する。mid-stream join は
**残ストリーム `self._bars[self._index+1:]` に新銘柄の future bars を ts 順 merge** する操作。`_open_bar` は各 bar を
`sink.push_bar(bar)` で observer に流すので、merge した X の bar は既存 cascade（observer → reducer → `per_id_ohlc_points`
→ chart）をそのまま流れる＝**C# 側は追加配線ゼロ**（S2 の gate は pure-Python で閉じる）。

- **added-time-onward**: 現在 bar の ts より厳密に後の bar だけ join（巻き戻さない・履歴を再生しない）。first bar 前（setup-time
  add）は `cur_ts=None` で全履歴 join＝「setup 追加→最初から流れる」（ADR-0031 D5）。
- **既存銘柄不変**: head（`[:index+1]`）は触らず tail だけ再 merge。`merge_bars_by_ts` の stable sort で同 ts は既存→追加の順。
- **remove**: `drop_instrument` が tail から X の future bars を除去（既流の履歴は残る）。**clear**: future bars 全除去で次 `open_next_bar` が END。
- **cross-venue 拒否**（`__init__` の単一 venue 不変条件を mid-run でも維持）。**missing DuckDB は membership only**（窓は出るが bars 無し・S1）。

### 実装した seam

| 層 | 変更 |
|---|---|
| `python/engine/kernel/stepper.py` | `KernelStepper.__init__` に `data_root`/`start`/`end`/`granularity`/`bar_loader`。`join_instrument`/`drop_instrument`/`clear_instruments`（lazy `load_bars`/`merge_bars_by_ts`） |
| `python/engine/strategy_runtime/backtester.py` | `from_scenario` が data source を stepper へ forward。`_UniverseHandle` が `bar_source` を受け、add→`join_instrument`（venue 検証先・bridge.add 前）／remove→`drop_instrument`／clear→`clear_instruments`（duck-typed＝LiveAuto の `LiveCellBackend` には無いので skip） |

### gate（pytest `test_bt_universe_midstream_join.py`）

`MIDJOIN-01`（added-time-onward・履歴非再生・ts 単調・同 ts は既存先）/ `02`（既存銘柄の bar 列が join で不変）/ `03`（drop→以降の
X bar 停止）/ `04`（setup-time add→最初から）/ `05`（clear→stream 終了）/ `06`（**unused run は loader 不呼出＝#24 golden
byte-identical**）＋ cross-venue 拒否／missing-DuckDB membership-only／`bt.universe.add` 配線。fake `bar_loader` 注入で DuckDB
mount 不要・決定論。**9 passed**。回帰：backtester phase3/4・v19 parity・golden cpython/scenario = 44 passed（byte-identical 維持）。

### RED→GREEN litmus

`join_instrument` の merge を消す → MIDJOIN-01/04 RED（X 流れず）。ts filter（`b.ts_event_ns > cur_ts`）を消す → MIDJOIN-01 RED
（履歴再生）。`drop_instrument` の tail filter を消す → MIDJOIN-03 RED。`from_scenario` の data_root forward を消す → join が
membership-only に落ち MIDJOIN-01 RED。

### 再走手順

```
cd python && uv run pytest tests/test_bt_universe_midstream_join.py -v   # MIDJOIN-01..06
```

教訓: 既存 `_open_bar`→`push_bar` cascade が join した bar を自動で chart へ流すので、S2 は **stepper の bars list を編集するだけ**で
完結し C# 変更ゼロ。`merge_bars_by_ts([tail, new_bars])` の stable sort が同 ts 順序（既存→追加）を保証。

---

## §S3 (#143) — Python universe 編集の永続化を既存 Save タイミングに限定

### 設計の確定（既存ストアを先に裏取り）

owner は Considered Options で「編集ごとに即永続化」を**明示却下**（「勝手なタイミングで保存しない」）。よって bt.universe 編集は
**dirty にして、いつもの Save で落ちる**——startup tile テキスト編集（`ReplaceAll`＋`Params.Dirty=true`・即 Flush せず Commit/Save で
落ちる）と同型で、sidebar picker の即時 Flush（`AddFromPicker`/`Remove` が edit イベントで `Flush`）とは**別**。

既存の universe 永続化経路は **3 つ**: ① sidebar/picker edit → `UniverseWriteback.Flush`（**即時**・per-edit）、② Run-commit
（`ScenarioStartupController.Commit` → `SetStartupParamsAndInstruments(Universe.Ids)`）、③ Save As（同 Commit）。**`OnFileSave`（File→Save）は
.py＋layout キーだけ書き scenario.instruments は touch しない**（既存不変条件・gate `LayoutPersistenceJourneyE2ERunner` JOURNEY-LAYOUT-07 が pin）。
startup-tile テキスト編集（`OnUniverseChanged`）も即 Flush せず `Params.Dirty=true` のみ＝**Commit/Save As でのみ落ちる**。

### binding decision（ADR が D4 prose と Considered Options で内部矛盾 → findings で確定）

ADR D4 prose は「`writeback_scenario_instruments_system`〔= per-edit Flush〕が発火する従来のタイミング」と書くが、Considered Options は
「編集ごとに即永続化」を**却下**（「勝手なタイミングで保存しない」）。両立する唯一の解＝**bt.universe 編集は startup-tile テキスト編集と同型**：
registry を dirty にし、**既存の full-registry Save 経路（Run-commit / Save As → Commit）でのみ disk に落ちる**。File→Save の universe-untouched
不変条件（JOURNEY-LAYOUT-07）は維持。**production コード変更ゼロ**——Commit は既に `Universe.Ids`（bt.universe 編集込み）を書くので、bt.universe
編集は何も足さずに既存 Commit で永続化される。「独自トリガを引かない」を最も忠実に満たす。

> **却下した実装**: 当初 `OnFileSave` に `Writeback.Flush` を 1 行足したが、`LayoutPersistenceJourneyE2ERunner` JOURNEY-LAYOUT-07 が RED 化
> （テストが `scenario.RemoveInstrument` で registry を空にし `_lastFlushed=[7203.TSE]` のまま → flush が空 registry を disk に書き戻し scenario を
> clobber）。これは「File→Save は universe を永続化しない」既存不変条件を破る設計変更だった＝**File→Save は universe Save 経路ではない**ことが確定。
> 既存 Commit 経路に乗せる方式へ pivot（コード変更ゼロ）。

### 実装した seam

- **production コード変更なし**（既存 Commit が `Universe.Ids` を書く）。`DriveUniverseBridge`（S1）は edit を registry に apply するだけで
  sidecar を書かない＝add 単体は dirty のまま。永続化は Run-commit / Save As の既存 Commit でのみ。

### gate（AFK `UniversePersistE2ERunner.cs`・Python-FREE）

`PERSIST-01`（**add は registry+chart を反映するが sidecar を書かない**＝dirty・AC#3/#1）/ `02`（**既存 Commit が scenario.instruments に
co-write・layout キー＋unknown scenario キーを merge-write で保持**・AC#2）/ `03`（**saved 編集は restart〔fresh root 再 open〕を跨いで残る**・AC#4）/
`04`（**unsaved 編集は restart で消え disk にも漏れない**・AC#4 negative）。bt.universe 編集は `UniverseBridge.ApplyJson`（BTUNIV-09 と同 seam）で
模擬・永続化経路は全 C#。Python 半（編集が dirty・自動永続化しない）は `test_bt_universe_bridge.py` BTUNIV-05/08。**4 PASS / 0 FAIL**。
回帰：`LayoutPersistenceJourneyE2ERunner` GREEN（JOURNEY-LAYOUT-07 維持）。

### RED→GREEN litmus

`DriveUniverseBridge` が edit を registry に apply しなければ Commit は旧 universe を書く → PERSIST-02/03 RED。bt.universe.add を
（却下された）Flush-on-edit にすると add 単体が sidecar を書く → PERSIST-01 RED。

### 教訓

- 永続化 slice は既存ストアを先に読め（grill 規律）。**だが「既存 Save 経路に乗せる」は『どの Save が universe を書くか』を *先に AFK 回帰で
  確定*してから配線せよ**——File→Save に flush を足したら JOURNEY-LAYOUT-07（File→Save は universe を書かない既存不変条件）を破った。共有 Save 経路に
  hook するときは sibling の Save 不変条件を先に grep/run する（[[#138 slice2]] の「persisted-hidden brick」と同型＝共有経路の盲点）。
- **「未 Save は revert」の AFK は *fresh root*（restart 等価）で検証**。同一 root での同一 doc 再 open は reseed をスキップするので revert を観測できず偽 RED（PERSIST-04 を fresh root に修正して GREEN）。
- ADR が prose と Considered Options で内部矛盾するとき、ADR は無改変で findings に binding decision を確定（grill-with-docs の「ADR×companion findings 突合」）。

---

## §S4 (#144) — LiveAuto add→subscribe を `Registry.Changed` 起動へ拡張

### 設計の確定（ADR-0022 の決定を ADR-0031 D6 が supersede）

ADR-0022 は **意図的に** universe-Changed 自動購読を避けた（「that would make the hook redundant and break the AC#6
delete-to-RED litmus」）。ADR-0031 D6 がこれを supersede＝「membership 変化 → 購読の対称追従」。`InstrumentRegistry.Changed`
は誰が registry を変えても発火する（UI [+ Add] も Python `bt.universe.add` も）ので、Changed 起動で新規分を購読すれば
**UI でも Python でも**新銘柄が購読される。

### 実装した seam

| 層 | 変更 |
|---|---|
| `Assets/Scripts/Live/LiveSubscriptionCoordinator.cs` | `OnUniverseChanged()` 追加（`IsLive(_lastMode)` のとき fresh ids を購読＝既存 `BulkSubscribeUniverse` 再利用） |
| `Assets/Scripts/Live/BackcastWorkspaceRoot.cs` | `_scenario.Universe.Changed += _subCoord.OnUniverseChanged`（BuildWorkspace・teardown で `-=`） |

- **Replay は no-op**（`IsLive` gate・engine が subscribe を precondition-reject／Replay の add は S2 のデータ合流）。
- **既存 hook 踏襲（additive・"拡張"）**: `OnLiveRowSelected`（row-select の per-instrument 購読）は残す。[+ Add] の hook 購読は
  Changed と二重になるが `_subscribed` dedup で no-op＝無害。bulk-on-entry（`OnModePoll` rising edge）も踏襲。
- **membership 不可侵（AC#3）**: coordinator は Ids を読むだけで registry を書かない。購読失敗は typed エラーで surface し registry に触れない
  （`registry.Add` が Changed 発火前に確定済み＝subscribe 失敗が membership を un-mutate しない）。

### gate（AFK `UniverseSubscribeE2ERunner.cs`・UNISUB-01..08）

2 ゲート分割: **coordinator contract**（recording sink・Python-FREE・決定論）＝`UNISUB-01`（LiveAuto registry.Add→subscribe・hook 非経由）/
`02`（Replay add→no-subscribe・Live-gate）/ `03`（dedup＝entry bulk 済み id は再購読しない）/ `06`（購読失敗が membership 不可侵）。
**実 root 配線**（full-stack MOCK・venue-free）＝`UNISUB-07`（実 `BackcastWorkspaceRoot` で **pure `_scenario.Universe.Add`〔SelectRow/[+ Add]
非経由〕→ Changed 配線で subscribe → MockVenueAdapter depth render**＝私が足した seam を hook から分離して非空虚に証明）。**8 PASS**（exit=139 segfault は
MOCK shutdown・verdict はタグ）。回帰：`LiveSubscribeWiringE2ERunner` GREEN（additive・dedup で無害）。

### RED→GREEN litmus

`OnUniverseChanged` の `BulkSubscribeUniverse` を消す → UNISUB-01/07 RED。`IsLive(_lastMode)` gate を消す → UNISUB-02 RED（Replay が誤購読）。
`Universe.Changed += _subCoord.OnUniverseChanged` 配線を消す → UNISUB-07 RED。

---

## §S5 (#145) — LiveAuto remove→unsubscribe を新設（対称）

### 設計の確定

ADR-0031 D6 の対称側＝remove/clear → venue unsubscribe（add↔remove）。Python 側の unsubscribe 経路（`orchestrator.unsubscribe_market_data`
→ `runner.unsubscribe` → `adapter.unsubscribe`＋price/depth cache remove＋`forget_instrument`＋typed `UNSUBSCRIBE_FAILED`）は**既存**
（venue 非依存・立花/kabu 共通）。C# 側に unsubscribe 配線が**無かった**ので新設。

### 実装した seam

| 層 | 変更 |
|---|---|
| `Assets/Scripts/Live/LiveSubscriptionCoordinator.cs` | `OnUniverseChanged` を拡張：購読済みだが Ids から消えた id（`_subscribed - Ids`）を unsubscribe＋`_subscribed` から除去（再 add で再購読）。subscribe pass の**前**に実行（swap で remove X＋add Y を両立） |
| `ISubscribeSink` | `Unsubscribe(string)` 追加 |
| `Assets/Scripts/Live/LaneSubscribeSink.cs` | `Unsubscribe` 実装（`lanes.SubmitUnsubscribeMarketData`・fire-and-forget・失敗は log のみで membership 不可侵） |
| `Assets/Scripts/Live/LiveRpcLanes.cs` | `SubmitUnsubscribeMarketData` / `CallUnsubscribeMarketData`（write lane・`unsubscribe_market_data` RPC・subscribe の鏡像） |

### gate（AFK `UniverseSubscribeE2ERunner.cs`・UNISUB-04/05/08）

`UNISUB-04`（LiveAuto registry.Remove→unsubscribe・survivor 残）/ `05`（clear→全 unsubscribe）/ `06`（unsubscribe 失敗が membership 不可侵）＝
recording sink。`UNISUB-08`（**実 root：pure `_scenario.Universe.Remove`→Changed 配線で unsubscribe→engine forget→feed 停止**＝`WaitUntilDepthGone`・
UNISUB-07 が直前に HasDepth=true を確立済みで非空虚）。**8 PASS**。

### RED→GREEN litmus

`OnUniverseChanged` の unsubscribe ループを消す → UNISUB-04/05/08 RED。`SubmitUnsubscribeMarketData` の RPC 名を壊す → UNISUB-08 RED。

### 教訓

- `LiveRpcLanes` subscribe の鏡像で unsubscribe を新設＝Python 側 unsubscribe は既に全層完備だったので C# write-lane 1 本＋sink 1 メソッドで対称配線が閉じた。
- full-stack MOCK の Conn 収束は **edit-mode に Update が無いので `WaitUntil` 内で `Conn.ApplyStatePoll(LatestStateJson)` を毎 iteration pump** する必要（SUBWIRE の `Pump()` 同型）。これを落とすと「badge did not converge CONNECTED」で full-stack section が偽 RED（初回踏んで修正）。
- `ISubscribeSink` に member 追加時は全 implementor を grep（`LaneSubscribeSink` ＋ test の `RecordingSink` の 2 つだけと確認）。

---

## §Review — code-review(simplify high) 後の修正（2026-06-25・8 finder angle × verify）

orchestrated review（3 correctness + reuse/simplify/efficiency + altitude/conventions）で surface した指摘の解消:

- **F1（HIGH・修正）— LiveAuto で `bt.universe` が fail-closed だった**: `LiveCellBridge.__init__` が `Backtester(self._backend)` を
  `universe_bridge` 無しで組んでいたため、LiveAuto cell の `bt.universe.add(X)` が `RuntimeError`＝ADR-0031 D3/D6（LiveAuto 対象）と矛盾。
  S1 の §S1 で「LiveAuto bridge 配線は S4 で」と書いたが S4 は C# Changed→subscribe だけで Python bridge を配線し忘れていた（2 finder が独立検出）。
  **修正**: `build_live_marimo_loader(universe_bridge=)` → `_make_bridge_factory` → `LiveCellBridge(universe_bridge=)` → `Backtester(universe_bridge=)`
  に thread。`LiveOrchestrator.__init__`（`self._engine` 既設）が `EngineUniverseBridge(self._engine)` を渡す。gate=`BTUNIV-14`
  （`LiveCellBridge(universe_bridge=fake)._bt.universe.add/remove` が enqueue・bridge 無しは fail-closed・LiveAuto backend は join_instrument 無し）。
- **F2/F4/F5（MEDIUM correctness + efficiency・修正）— `DriveUniverseBridge` の coalesce latch ＋ per-frame 再生成**: 旧実装は
  `_lastPushedUniverse` に毎フレーム `UniverseIdsToJson` を再生成して比較し、push が `!_serverReady` で no-op でも `_lastPushedUniverse` を
  latch していた＝not-ready 窓で seed すると永久に mirror が空（latent・InitializePython が Awake 同期なので顕在化せず）。さらに毎フレーム JSON
  再生成＝GC churn。**修正**: `InstrumentRegistry.Changed` 起動の `_universeMirrorDirty` フラグ（UI も drained Python edit も Changed を焚く）＋
  `PushUniverseIds` を `bool` 化し**確定 push 時のみ dirty クリア**。dirty のときだけ `UniverseIdsToJson` を作る＝per-frame 再生成を撲滅。
  `UniverseBridge.Apply` の change-count は AFK assertion 用（BTUNIV-09/10/11 が `changed==1`）と明記（production coalesce は dirty-flag）。
- **F7（修正・doc honesty）— ADR-0022 の「deliberately no universe-Changed auto-subscribe」コメント反転**: `LiveSubscriptionCoordinator.cs`
  のトリガ doc を 3 トリガ（bulk / OnUniverseChanged〔D6 supersede〕/ hook〔row-select のみ load-bearing〕）へ更新。`LiveSubscribeWiringE2ERunner.cs`
  の AC#6 litmus ヘッダ＋line 128 コメントを D6 supersede 注記へ flip（.md は §S4 で既に更新済み）。
- **drop_instrument の held-position 価格凍結（note・behavior 不変）**: hold 中の銘柄を remove すると open position は最終 close で
  mark-to-market 固定（新データ無し＝唯一の定義価格）。membership 削除は position を清算しない、と docstring に明記。
- **受容（latent/edge・コード変更なし）**: ① `DriveUniverseBridge`→`DrivePrune` 同フレームの registry 二重書き＝prune gate は dormant
  （`NullUniversePruneSource`・#41 実 producer 未実装）なので現状 trigger 不能。② `join_instrument` の同期 DuckDB I/O は明示的 user action なので許容。
  ③ `bt.universe.add`→`list()` の 1-frame stale は **D2 設計どおり**（BTUNIV-05 が pin・mirror が SoT で Python 自前 SoT 無し）。④ push の JSON
  string boundary は drain（list-of-dict＝JSON 必須）と対称な単一 string boundary として維持（PyList 化は marginal・rare push なので codec コスト無視可）。
- **F6（reuse・判断保留）**: `push_universe_ids` の JSON codec vs `CallSubscribeMarketDataBatch` の PyList——push が dirty-gate で稀になったので
  codec コストは無視可。drain と対称な単一 string boundary を優先し維持。

**回帰**: Python 618 passed / 2 skipped（BTUNIV-14 追加）、compile PASS、UniverseBridge 4 / UniversePersist 4 / UniverseSubscribe 8 /
LiveSubscribeWiring すべて GREEN。**Medium+ 指摘は全解消**。

---

## §Review2 — 2 周目 orchestrated review（2026-06-25・5 専門 Agent: dead-code / simplify / regression / behavior-to-e2e / 敵対的 correctness）

owner 依頼で `/zoom-out` → 5 軸の専門 Agent を fan-out（各 Agent は「報告前に grep/trace で自己検証」）。1 周目（§Review）後なので
**dead-code 0・regression 0・High/Medium correctness 0**（12 hazard を trace して safe 確認＝head/tail `_index` split・等 ts stable merge・
`_lock`/GIL・C# CME 無し・再入 Changed cascade 等）。残った実体は **behavior-to-e2e の 3 Medium gap** と **少数の Low 実欠陥**。完成形に向け解消:

- **F-correctness1（修正・PyString leak）** — `WorkspaceEngineHost.PushUniverseIds` が引数 `new PyString(...)` を dispose せず
  result だけ dispose していた（finalizer-thread 回収＝[[issue-133-135-tcl-asyncdelete-login-teardown]] が断った経路）。`using (var arg = …)` で arg も dispose。
- **F-correctness2（修正・無視されていた ack＝latent lost-update）** — `PushUniverseIds` が server の `{"success"}` を捨てて例外が無ければ
  常に `true` を返していた＝`BAD_JSON` 等の**拒否された push でも `_universeMirrorDirty` が clear** され、§Review F2/F4/F5 が確立した「**confirmed push のときだけ dirty を落とす**」不変条件と矛盾。`res.GetItem("success")` を読んで返す（`CallUnsubscribeMarketData` と同型）。現状 `UniverseIdsToJson` は常に valid JSON なので**到達不能だが latent**——`UniverseIdsToJson` が将来壊れたら silent に seed を落とす穴を塞いだ。
- **F-simplify1（修正・dead work）** — `inproc_server.push_universe_ids` の `[str(i) for i in ids]` は `DataEngine.push_universe_ids` が
  無条件に再 coerce するので二重。inproc 側を `ids` 直渡しに（core が SoT coercion 点）。
- **F-coverage1（gate 追加 BTUNIV-15・pytest）** — §Review F1（LiveAuto fail-closed・元 HIGH）の**真の bug 部位は loader→factory→bridge** だったが、
  回帰 BTUNIV-14 は `LiveCellBridge` ctor を直接組むだけで loader/factory/orchestrator の配線を通らない＝「loader で param を再び落としても GREEN のまま」。
  `build_live_marimo_loader(universe_bridge=)` → factory → `LiveCellBridge` → `Backtester` を**実 marimo fixture で端から端まで**駆動し
  `bt.universe.add` が engine に届くことを assert。negative（bridge 無し→全鎖で fail-closed）も追加。
- **F-coverage2（gate 追加 BTUNIV-16・AFK）** — 実 `DriveUniverseBridge` の read-channel coalesce/latch（§Review F2/F4/F5 の latent fix）が
  両半とも bypass されていた。Python-FREE（server 未 ready）で **registry edit→dirty／not-ready push（`PushUniverseIds`＝false）で dirty 維持**＝
  seed を落とさないことを実 root で gate。litmus: push 結果に依らず dirty を clear する／`PushUniverseIds` が not-ready で true を返す → RED。
- **F-coverage3（gate 追加・pytest cross-venue handle）** — cross-venue `bt.universe.add` の拒否が **handle 経由（join-before-bridge 順序）** で
  membership を汚さないことが未 assert だった（既存 `test_cross_venue_join_rejected` は stepper を直接叩き handle を bypass）。
  `bt.universe.add("AAPL.NASDAQ")` が `ValueError` ∧ `bridge.ops==[]` ∧ stepper に未登録、を assert（reorder すると phantom add が漏れ RED）。

### 受容（Low・コード変更なし・設計判断）

- **LiveAuto の cross-venue add は fail-loud にしない（F-correctness3・受容）**: Replay は `join_instrument` が venue 検証で `ValueError`、
  LiveAuto は `LiveCellBackend` に join が無く（duck-type skip）registry に入り subscribe が venue 側で typed soft-fail（membership 不可侵・D3）。
  live session は単一 venue なので無意味な add だが、**「subscribe 失敗は membership を un-mutate しない」D3 と整合**＝soft-fail で error は surface する。
  live venue を handle に結合してまで先回り検証はしない（exotic・follow-up 候補）。
- **`clear_instruments` 後も `_venue` を保持（F-correctness4・受容）**: 空 universe→別 venue add も旧 venue で拒否＝Replay 単一 venue 不変条件を維持。exotic。
- simplify Low2（`getattr` 3× の helper 化）/ Low3（`list()` の二重 copy）は **挙動不変の taste**＝各 caller が順序を持つ explicitness を優先し据え置き。

**回帰**: Python **621 passed / 2 skipped**（+3＝BTUNIV-15・loader fail-closed・cross-venue handle）、compile PASS、
UniverseBridge **5 PASS**（BTUNIV-09..12＋新 16）・UniversePersist 4・UniverseSubscribe 8・LiveSubscribeWiring すべて GREEN。**Medium+ 指摘は全解消**。
