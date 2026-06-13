# findings 0011 — Live adapter tracer（#20・grill 確定）

方針: **ADR-0001 decision 8**（C#↔Python は単一 adapter 層・C# 製 sink を engine の sink 口へ差す）。
live loop の実体は **ADR-0004 案 C** の pure-Python `KernelLiveEngineController`（#25・findings 0010）。
本書は #20 スライスの下位確定事実を記録し、ADR は「方針」として参照する（ADR は自己保護のため編集しない）。

#9 の **Replay seam tracer**（findings 0001・`push_bar`→`ConcurrentQueue`→main drain）の **Live 対応物**。
実 venue 接続前に **mock venue** で、C# adapter が「live loop の lifecycle owner」になり「live event を
GIL-free に drain して Unity panel へ反映」する縦スライスを通す。

## ゴール（issue #20 再掲＋方針更新コメント反映）

- C# adapter が `InprocLiveServer`（生産 inproc 入口）の lifecycle facade を駆動して live loop を
  start/stop する（marshal=`run_coroutine_threadsafe(...).result()` は facade 内部・S2-spike で健全性確認済）。
- live event（order / fill / position / lifecycle / telemetry）を engine が **生産経路で push**し、main が
  **GIL なしで drain** して Unity panel（view-model/Text）へ反映する。
- main thread は終始 **GIL 非取得**（render を GIL 競合から守る S0/S2-spike 規律を継承）。
- 実 venue stack / login は混ぜない（mock adapter で seam のみ判定）。

## 確定事実（grill 2026-06-13）

### D1. live event sink = backend_events（生産配送路・D2 忠実）。EventSink projection は転用しない

- #20 コメントの「Replay sink 契約と同型 / C# decoder 無改修」は **配送路の指定ではない**——あれは #24/#25 の
  **AC④ projection 互換ゲート**（kernel→`EventSink`→`ReplayPanelDecoder` で実値 decode）を指す。Live UI 配送の
  権威は findings 0010 **D2** どおり **backend_events**（`OrderEvent`/`AccountEvent`/`LiveStrategyEvent`/
  `LiveStrategyTelemetry`/…）。`EventSink.push_*` を Live 配送路にしない（CONTEXT.md `live event sink` 項）。
- ⇒ #20 が証明する seam（生産経路そのまま）:
  ```
  KernelLiveEngineController → LiveLoopManager callbacks
    → DataEngineBackend.publish_backend_event            (_backend_impl.py:1315)
    → _backend_event_to_wire_dict (ADR-0018 A2 外部タグ付き {"OrderEvent":{...}})  (event_wire.py)
    → json.dumps(...).encode("utf-8")
    → engine._rust_event_sink.push_json(bytes)           (_backend_impl.py:1322)
    → 【C# LiveBackendEventSink.push_json】→ ConcurrentQueue<string>（GIL-free）
    → Unity main が GIL-free drain → LiveBackendEventDecoder → panel view-model
  ```
- `engine._rust_event_sink` は TTWR では Rust オブジェクト。backcast は **C# `LiveBackendEventSink`** を
  `data_engine.set_rust_event_sink(...)` で差す（名前は TTWR 由来の misnomer・seam は同一）。これが
  ADR-0001 d8「engine の sink 口へ C# 製 sink を差す」の Live 実体。

### D2. drive path = `InprocLiveServer` facade 直叩き＋mock 注入は throwaway helper 経由

- C# worker が次を **個別に**呼ぶ（#25 の「C# は blocking `run()` の受動 caller」未証明部分を解消し、
  C# を真の lifecycle owner にする）:
  ```
  DataEngine 生成
    → LiveBackendEventSink を set_rust_event_sink
    → InprocLiveServer(data_engine, "MOCK")
    → register_live_strategy → venue_login → set_execution_mode("LiveAuto")
    → start_live_strategy → 【mock event 注入】 → stop_live_strategy
    → venue_logout / stop_live_loop / teardown
  ```
- **mock 注入だけ** throwaway Python helper（`spike/live_adapter/...`）が `InprocLiveServer` 内部の共有
  `MockVenueAdapter`（`live_adapter_factory`）へ `live_loop.call_soon_threadsafe(inject_tick, ...)` で行う。
  C# が触るのは helper の注入メソッドのみ。**start/stop は必ず `InprocLiveServer` facade を直接駆動**。
  **生産 API（`InprocLiveServer`）に mock 専用メソッドを足さない**。
- 不採用: `run_mock_live.run()` の step 分割——`publish_backend_event_callback=list.append` で
  `_backend_impl`/`event_wire`/`push_json` を迂回するため、生産 sink seam（D1）を通さない。

### D3. gate form = AFK headless が権威ゲート／実描画のみ owner 手動 playmode

- 権威ゲート = batchmode probe（`LiveAdapterTracerProbe.cs`, self-failing, exit 0/1）。「panel 反映」は
  **queue 件数ではなく** 次まで headless で検証する:
  ```
  sink drain → LiveBackendEventDecoder（production decoder） → panel view-model / Text 値
    → order/account/lifecycle/telemetry の【実値】assert（例: OrderEvent FILLED×2・AccountEvent position・telemetry）
  ```
- 実 GPU 描画の視認だけを **owner 手動 playmode leg**（default-disabled harness）に分離。#10/#11 と同じ境界・
  pixel/GPU 判定を #20 の AFK 成否へ混ぜない。S2-spike render leg と同方式（Play 単一所有・排他再走）。

### D4. main GIL-free 計測 = S2-spike 規律を継承

- main は全工程で `Py.GIL()` を一切取得しない。drive worker と poll worker（D5）が GIL を取り、Unity main は
  GIL-free に drain/decode する。heartbeat 間隔の最大 gap `< 200ms`（frame-hitch baseline・S2-spike §3 と同閾値）。
- GIL を取る C# delegate（`push_json`）は **enqueue だけ**（bytes→string→`ConcurrentQueue.Enqueue`）。block しない
  （#9 の `push_bar` 規律）。

### D5. market-state（depth/kline）= 軽量 poll を 1 点だけ。push sink と明確に分離

- issue 本文が depth を明記するため完全 scope 外にはしない。ただし **push と poll は別チャネル**:
  - **push**: backend_events → `LiveBackendEventSink` → `ConcurrentQueue` → main drain（D1）。
  - **market state**: 専用 **poll worker** が `InprocLiveServer.get_state_json()` を呼ぶ → latest-wins な C# slot
    → main が GIL-free decode。
- 1 回の state assertion で同一 instrument について **kline 由来の price/ohlc_points** と **DepthCache 由来の
  bid/ask 実値** を確認すれば十分（depth は `LiveReducerBridge` が無視し `DepthCache`→`get_state_json` 経由＝
  reducer_bridge.py:75 / depth_cache.py）。
- poll で GIL を取るのは専用 worker だけ。Unity main は終始 GIL 非取得。

### D6. 成果物の durability — decoder/sink は durable、probe/helper は throwaway

- **durable（`Assets/Scripts/`）**: `LiveBackendEventSink`（push_json→queue）/ `LiveBackendEventDecoder`
  （外部タグ付き wire→typed view-model。backcast 初の Live backend-event decoder・将来の Live panel が再利用）/
  最小 Live panel view-model。
- **throwaway（`Assets/Editor/` + `python/spike/live_adapter/`）**: `LiveAdapterTracerProbe.cs`（AFK 権威ゲート）/
  playmode harness（owner 手動）/ mock 注入 Python helper。
- 既存 `ReplayPanelDecoder`/`ReplayBarDecoder` は Replay EventSink/`push_bar` 用で **再利用しない**（wire が別）。

## 実装インベントリ（計画・grill 確定 D1–D6）

**C#（durable・`Assets/Scripts/Live/` 想定）**:
- `LiveBackendEventSink.cs` — `push_json(byte[]/string)` を実装し GIL-free `ConcurrentQueue<string>` に積む。
- `LiveBackendEventDecoder.cs` — 外部タグ付き wire（`{"OrderEvent":{...}}` 等）を typed event/view-model へ。
- 最小 Live panel view-model（order/account/lifecycle/telemetry の実値保持）。

**C#（throwaway・`Assets/Editor/`）**:
- `LiveAdapterTracerProbe.cs` — AFK 権威ゲート。DataEngine 生成→sink 差し込み→InprocLiveServer 駆動→
  mock 注入（helper）→drain/decode→実値 assert→main GIL-free（heartbeat<200ms）→stop/teardown→exit 0/1。
  `[LIVE ADAPTER TRACER PASS]` / `... FAIL]`。
- playmode harness（default-disabled・owner 手動の実描画 leg）。

**Python（throwaway・`python/spike/live_adapter/`）**:
- mock 注入 helper — 共有 `MockVenueAdapter` を `live_adapter_factory` で差し、`call_soon_threadsafe` で
  tick/fill-outcome を注入するメソッドを C# へ公開。生産 `InprocLiveServer` API は無改修。

**生産 engine 改修**: 原則なし（生産経路 `publish_backend_event→event_wire→push_json` をそのまま使う）。
`set_rust_event_sink`/`InprocLiveServer`/`_backend_event_to_wire_dict` は既存。

## ゲート / 再走（実装後に追記）

- CPython: mock 注入 helper 単体の疎通（任意）。
- AFK 権威: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod LiveAdapterTracerProbe.Run`
  → `[LIVE ADAPTER TRACER PASS] ...` exit 0、CS エラー 0、新規 crash dump 無し。
- owner 手動 playmode: 実 panel 描画 leg（Play 単一所有・排他再走手順は本節に追記）。
- behavior gate: backcast に FLOWS.md は無く、findings + AFK probe + owner playmode leg が等価物。
  App 側挙動（C# adapter が Live loop を marshal 駆動・backend_events を GIL-free drain して panel 反映）の
  再走手順を本書に固定する。

### 実証結果（2026-06-13・mock venue・Mac leg）

- **CPython 疎通 gate（任意・GREEN）**:
  ```
  cd python && .venv/bin/python -m spike.live_adapter.run_tracer_smoke
  → [LIVE ADAPTER TRACER CPYTHON PASS] orders_filled=2 accounts=5 lifecycle=2 telemetry=2
  ```
  生産経路（DataEngine + set_rust_event_sink → InprocLiveServer(MOCK) facade 直叩き →
  publish_backend_event → event_wire 外部タグ付き → push_json）を純 CPython で先行検証。
  捕捉した wire tag = OrderEvent / AccountEvent / LiveStrategyEvent / LiveStrategyTelemetry /
  StrategyLogMessage。get_state_json shape を裏取り（kline 価格 = top-level `last_prices["8918.TSE"]`、
  depth = `per_instrument["8918.TSE"].depth.bids/asks[].price`。いずれも instrument-id keyed dict のため
  JsonUtility では bind 不可 → probe 側で string 抽出）。

- **AFK 権威ゲート（GREEN・Unity 6000.4.11f1）**:
  ```
  <Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
          -executeMethod LiveAdapterTracerProbe.Run -logFile /tmp/live_adapter_tracer.log
  → UNITY_EXIT=0
  ```
  VERBATIM PASS（log）:
  ```
  [LIVE ADAPTER TRACER PASS] fills=2 acct=8918x100 telem(realized=200,fills=2) lifecycle=2 state(price=10,bid=9.9,ask=10.1) maxStall=9ms — C# drove InprocLiveServer facade; backend_events drained GIL-free; market-state polled (mock LiveAuto)
  ```
  判定: `UNITY_EXIT=0` + 上記 PASS + `grep -cE "error CS"`=0 + 例外 0 + `[LIVE ADAPTER TRACER MARK] PythonEngine.Shutdown OK (clean teardown)` + 新規 crash dump 無し。
  - D2 充足: C# drive worker が register→login→set_execution_mode(LiveAuto)→start→…→stop→logout→close を façade 直叩き。注入は throwaway `spike.live_adapter.mock_inject`（生産 API 無改修）。
  - D1 充足: backend_events が C# `LiveBackendEventSink.push_json` に着弾、main が GIL-free drain → `LiveBackendEventDecoder` → `LivePanelViewModel` で OrderEvent FILLED×2 / AccountEvent position(8918×100) / LiveStrategyTelemetry(realized=200,fill=2) / LiveStrategyEvent を実値 assert。
  - D4 充足: main は終始 GIL 非取得、heartbeat 最大 stall = 9ms（< 200ms）。
  - D5 充足: 専用 poll worker が get_state_json を latest-wins slot へ、main が GIL-free 抽出して kline 由来 price=10.0・DepthCache 由来 bid=9.9/ask=10.1 を同一 instrument で確認。

- **owner 手動 playmode leg（実描画・default-disabled）**: 実 GPU 描画の視認は AFK ゲートに混ぜず分離（D3）。harness + 起動手順は本書および menu（`Tools > Backcast`）に記載。実行は owner。

- **durable 成果物**: `Assets/Scripts/Live/LiveBackendEventSink.cs` / `LiveBackendEventDecoder.cs` / `LivePanelViewModel.cs`。
  **throwaway**: `Assets/Editor/LiveAdapterTracerProbe.cs` + `python/spike/live_adapter/{__init__,mock_inject,run_tracer_smoke}.py`。

## ADR の扱い

- 新規 ADR は起こさない。本件は ADR-0001 d8（単一 adapter 層/sink）・ADR-0004 案 C（kernel）・ADR-0018 A2
  （外部タグ付き wire）の **充足記録**。findings 0010 D2 が intentionally 開いていた「Live sink wire は何か」を
  #20 が確定（=backend_events 外部タグ付き）したもので、D2 への書き戻しはしない（findings に記録・ADR は参照のみ）。
- #20 issue への注記（owner 提案）: 「AC の『Replay sink と同型』は **projection 互換**を指し、Live sink wire は
  **外部タグ付き BackendEvent**」を issue コメントに追記すると後続の誤読を防げる。
