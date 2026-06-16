# findings 0014 — Live demo roundtrip（#23・done-gate・grill 確定）

方針: **ADR-0001**（decision 3 = orphan 不在 / decision 6 = 正常終了 best-effort 取消 / decision 8 = 単一 adapter
層）＋ **ADR-0004 案 C**（pure-Python kernel・Nautilus を Mono に入れない）。本書は #23 スライスの下位確定事実を
記録し、ADR は「方針」として参照する（ADR は自己保護のため編集しない）。

#23 は **Step 2（#4）の最終統合 done-gate**。先行 5 子スライス（#20 Live adapter tracer / #21 Venue login &
secret flow / #22 Live safety & graceful shutdown / #24 kernel tracer / #25 Kernel Live Foundation）の上に、
**通常起動 Unity app の production venue UI を結線**し、demo venue（kabu Verify / tachibana demo）で
**発注→約定→建玉反映**＋**正常終了で resting order 取消**を owner が確認する。あわせて #25 から繰り越した
**cancel-ACK sibling consumer gap（3 点）**を実 kabu live 経路で end-to-end に塞ぐ。

## スコープ（owner grill 2026-06-14 確定）

3 workstream を 1 done-gate に束ねる:

| | workstream | 性質 | 担い手 |
|---|---|---|---|
| **A** | cancel-ACK 3 点（(a) kabu flip / (b) async 確定配線 / (c) sibling consumer honor） | Python production・AFK 検証可 | 実装（RED→GREEN pytest） |
| **B** | production Live shell 結線（chrome + canvas data panels に #21 durable 型を載せる） | C# 結線・AFK probe 可 | 実装（+ owner Mono 走） |
| **C** | demo roundtrip + graceful-shutdown cancel・`[LIVE DEMO ROUNDTRIP PASS]`・#4 close | HITL owner-manual（実 venue + 平日場中 + Windows-Mono） | owner 手動 |

**#22 はクローズ前提**で進める（別作業者が最終調整中・owner 合意 2026-06-14）。

## 確定事実

### D1. venue 接続 UI の配置 = screen-fixed chrome（infinite-canvas Content の外）

issue は配置候補を infinite-canvas / hakoniwa / floating-window と列挙したが、**CONTEXT.md「infinite canvas」項が
menu / sidebar / footer / modal を screen-fixed chrome（Content 外・pan/zoom 非追従）と既に定義**している。よって:

- **venue connect/disconnect メニュー・connection badge・secret modal は chrome**（`ScreenSpaceOverlay`・Content 外）。
  badge は venue セッション全体の canonical state を示すため pan/zoom や workspace layout に依存させない。secret modal
  は認証待ちの blocking overlay であり canvas 配下の z-order / 画面外配置に晒さない。
- **order / fill / position 表示は Hakoniwa tile**（`Orders` / `Positions` / `Run Result`）。**Live UI 配送の権威は
  既存 `LiveBackendEventSink → LivePanelViewModel`**（findings 0011 D2）。`fill` は独立 tile ではなく `OrderEvent` の
  `FILLED` ＋ telemetry / account 更新として表現する。
- floating-window / hakoniwa は data 表示・自由配置作業面専用で、接続「メニュー」はそぐわない（CONTEXT 既定）。

新 ADR 不要（ADR-0003 capability parity ＋ CONTEXT「infinite canvas」chrome 定義が既に所有）。

### D2. #23 が backcast 初の production composition を立てる

backcast には composed production shell が存在せず、各スライス（infinite-canvas #13 / hakoniwa #14 /
floating-window #15 / replay panels / strategy editor / live adapter / venue login）は全て `*HitlHarness`
MonoBehaviour で owner 手動 wiring されている（`SampleScene` は Camera/Light/Volume の 3 component のみ）。
#23 done-gate は **chrome と canvas data panels の production composition を同時に立てる**:

```text
Canvas (ScreenSpaceOverlay)
├─ Viewport
│  └─ Content
│     ├─ HakoniwaRoot          # Orders / Positions / Run Result tile（LiveBackendEventSink 給電）
│     └─ FloatingWindowLayer   # Order ticket（LiveManual のみ）
└─ ChromeLayer
   ├─ VenueMenu + ConnectionBadge
   ├─ Manual / Auto mode + Auto Run ▶ footer
   └─ SecretModalOverlay       # 最前面・入力遮断
```

durable 型 `VenueMenuViewModel` / `VenueConnectionViewModel` / `SecretModalController` / `LiveRpcLanes`
（#21・findings 0012）と `LiveBackendEventSink` / `LivePanelViewModel`（#20/#26）を**再利用**しロジックは再実装しない。

### D3. 発注導線 = Manual ticket ＋ Auto run の二本（TTWR parity）

移植元 TTWR は Manual と Auto を別の正式導線として持つ（`docs/wiki/modes.md` / `screen-layout.md`）。一方だけでは
#23 の reachable bug-class を両方は塞げないため両方立てる。両者は**同一 Live Session ＋ venue 接続を共有**する:

- **Manual**: `Order` floating window → `ManualOrderFacade`（`order_facade.py`）。`live_orchestrator.place_order`
  → `facade.place`（`:1114`）/ `cancel`（`:1171`）。
- **Auto**: chrome footer `▶` → `RegisterLiveStrategy` → `StartLiveStrategy` → kernel `LiveBroker`（findings 0011）。

**gate roundtrip 2 本**:
1. **Manual roundtrip**: MARKET 発注 → fill → position 反映 ／ LIMIT 発注 → manual cancel → **poll-confirmed
   `CANCELED`**（cancel-ACK c-1 を実 venue 検証）。
2. **Auto roundtrip**: strategy 起動 → order/fill/position ／ resting LIMIT を残し **正常終了 → cancel ACK
   `PENDING_CANCEL` → poll terminal `CANCELED` → broker open order 0**（cancel-ACK (a)(b) と #22 graceful
   shutdown を実 venue 検証）。

### D4. cancel-ACK 3 点（#25 round 9 繰り越し・CONTEXT「取消受付 / 取消確定」）

ack-then-poll venue（kabu）では `PUT /cancelorder` 成立は**取消受付**にすぎず、終端 `CANCELED` は `GET /orders`
polling が後追いする。#25 は **broker 側の honoring のみ**（mock 経路）を実装済み。実 kabu live で end-to-end に
塞ぐには次の 3 つが揃う必要があり、#23 で一括対応する:

- **(a) kabu adapter の返し分け** — `kabusapi_execution.cancel_order` の受付成立返り status を
  `CANCELED`（`:340`）→ `PENDING_CANCEL` に。終端 `CANCELED` は `_poll_orders_once`（`:200`）が後追い。
  取消拒否（`ack.rejected`）は従来どおり `REJECTED`。
- **(b) async 確定配線（kernel 経路）** — 現状 adapter の async poll event は
  `set_execution_hooks(on_order_event=self._publish_order_event)`（`live_orchestrator.py:252`）で **UI へしか
  流れず kernel `broker.apply_venue_update` に届かない**。poll event を kernel broker にも fan-out し、受付〜確定の
  隙間の競合約定を会計し、poll 終端で broker を terminal 化する配線を足す。`LiveBroker.apply_venue_update` は #25 で
  既に `PENDING_CANCEL`/`PENDING_UPDATE` を非終端 honor 済み（event を流すだけ）。
- **(c) sibling consumer 2 経路**:
  - **(c-1) `ManualOrderFacade.cancel`（`order_facade.py:320`）** — `status="CANCELED"` ハードコードを `res.status`
    尊重に。`PENDING_CANCEL` は非終端で注文を open に保つ。sync facade は confirmation path を持たないため、**終端
    `CANCELED` の訂正は backend event（poll → `_publish_order_event`）経由**＝(b) 配線とセットで完結。manual order も
    `facade.place → adapter.submit_order` で kabu poll registry に登録される（`_register_order`/`_ensure_orders_poll`）。
  - **(c-2) `NautilusVenueExecClient._cancel_order`（`nautilus_exec_client.py:241`）** — legacy auto 経路。
    **現 live 経路から orphan**（`live_orchestrator.py:157` は `KernelLiveEngineController` のみ生成。
    `NautilusVenueExecClient` を作る `NautilusLiveEngineController` は orchestrator から非生成）。よって **修正は
    「将来使うため」ではなく、削除までの間 residual code を既知の誤契約にしないための保守修正**（ADR-0004 は
    Windows-Mono の Nautilus-live 復活を認めない）。`res.status == "PENDING_CANCEL"` で `generate_order_canceled`
    を呼ばない分岐を足す（非終端のまま async confirmation を待つ）。**production HITL gate には含めず focused unit
    test のみ**。legacy 経路の完全削除は別 issue。

### D5. AFK 検証 vehicle = throwaway async-emit adapter（full-chain）＋ 実 kabu adapter unit test（(a)）

実 kabu poll は平日場中＋Windows が要るため、回帰ガードは AFK で二層に張る（実 kabu HITL は最終受け入れであり
回帰ガードの代替にしない）:

1. **focused production adapter unit test（(a) 必須併設）** — `kabusapi_execution.cancel_order()` の HTTP ACK
   成立が必ず `PENDING_CANCEL` を返すことを httpx mock で固定（既存 `tests/test_kabu_orders_poll.py` 近傍）。
   throwaway が `PENDING_CANCEL` を返しても production adapter が `CANCELED` のままという偽緑を防ぐ。
2. **権威 full-chain AFK** — throwaway kabu-like adapter（`python/spike/`・ack-then-poll を模す async emit）を
   production orchestration に注入し通す:
   ```text
   cancel ACK: PENDING_CANCEL → kernel order open
     → 競合 PARTIALLY_FILLED を async emit → Portfolio に増分約定を計上
     → poll CANCELED を async emit → broker terminal・open order 0
     → UI OrderEvent / AccountEvent も収束
   ```
   これで (b)・kernel consumer・manual consumer (c-1) を被覆。legacy (c-2) は focused unit test に分離。

### D6. done-bar と既知の環境制約

- **C（実 demo roundtrip 観測）は owner-manual**: 実 venue 接続（tkinter prompt / 実資格情報）・**FILL は JST
  平日取引時間**が要る（findings 0012 は週末実施で FILL 未確認・ACCEPTED 止まり）。`[LIVE DEMO ROUNDTRIP PASS]`
  記録と #4 close は owner が実施する。
- **Unity-Mono gate は現在 flaky**（findings 0013 §深掘り）: 埋め込み CPython × Mono × pythonnet の GIL
  cross-thread marshal の borderline race（#23 非起因・S2-spike #7 / #25 GREEN が勝ち抜いた同 race）。クリーンな
  machine state で数回リトライ＝2026-06-14 と同様に GREEN になり得る。**A の Python 権威ゲート（pytest）と
  full-chain AFK が回帰ガードの正本**で、Mono host は環境健全性確認。

## 変更インベントリ（計画・grill 確定 D1–D5）

**production Python（A）**:
- `engine/exchanges/kabusapi_execution.py` — `cancel_order` 受付返り status を `CANCELED`→`PENDING_CANCEL`（a）。
- `engine/live/live_orchestrator.py` — adapter async poll event を kernel `broker.apply_venue_update` へ fan-out（b）。
- `engine/live/order_facade.py` — `cancel` の `status="CANCELED"` ハードコードを `res.status` 尊重へ（c-1）。
- `engine/live/nautilus_exec_client.py` — `_cancel_order` に `PENDING_CANCEL` 非終端分岐（c-2・保守修正）。

**production C#（B・`Assets/Scripts/Live/`）**:
- `ProductionLiveShell.cs` — backcast 初の production composition（MonoBehaviour）。engine bring-up
  （pythonnet・`InprocLiveServer`・main GIL-free）＋ #20/#21/#26 durable 型（`LiveBackendEventSink`→
  `LivePanelViewModel` / `VenueConnectionViewModel` / `LiveLogoutCoordinator` / `SecretModalController` /
  `VenueMenuViewModel` / `LiveRpcLanes`）を **再利用結線**。chrome（venue 接続メニュー・badge・secret modal
  overlay・Manual/Auto mode・Auto Run ▶）＋ manual Order ticket（BUY/SELL・qty・MARKET/LIMIT・price・Place・
  Cancel last）＋ Orders/Positions/Run-Result data panels を **screen-fixed chrome（IMGUI）** で描画（D1）。
  Auto は `register_live_strategy`→`start_live_strategy`→`stop_live_strategy`。ロジック再実装なし。

**throwaway（B/C・`Assets/Editor/`）**:
- `ProductionLiveShellProbe.cs`（B・AFK 権威ゲート）— production `InprocLiveServer(MOCK)` ＋ durable 合成で
  connect→badge / place→FILLED→panel order+(account) / logout→DISCONNECTED を main GIL-free で検証
  （`[PRODUCTION LIVE SHELL PASS]` / self-failing exit 0/1）。cancel-ACK FSM は A の Python 層で権威証明済み
  なので本 probe は **合成 seam** を gate（mock は受付を即 CANCELED 化＝PENDING_CANCEL の resting は実 venue 限定）。
- `LiveDemoRoundtripMenu.cs`（C・owner HITL launcher）— `Tools > Backcast > Live Demo Roundtrip
  (Tachibana demo / Kabu verify)` で production shell を demo venue 向けに spawn。`Record [LIVE DEMO
  ROUNDTRIP PASS]` で marker 記録（owner が両 roundtrip 目視確認後にクリック）。

**throwaway（A full-chain・`python/tests/`）**: `test_kernel_live_cancel_ack_async.py` の throwaway
async-emit（`SimpleNamespace` venue event）で受付→競合約定→poll 終端を本番 orchestration seam に通す（D5）。
別途 `python/spike/` の kabu-like adapter は本実装では不要だった（controller 経路の full-chain で被覆）。

**テスト/ゲート**:
- (a) kabu `cancel_order` → `PENDING_CANCEL` unit test（httpx mock）。
- (b)+(c-1) full-chain AFK（受付→競合約定→poll 終端→broker open 0・UI 収束）。
- (c-2) `_cancel_order` が `PENDING_CANCEL` で `generate_order_canceled` を呼ばない focused unit test。
- shell composition AFK probe（chrome/panel 結線・GIL-free drain）。
- HITL（owner）: Manual roundtrip / Auto roundtrip / 正常終了 resting cancel・`[LIVE DEMO ROUNDTRIP PASS]`。

## 実装結果（workstream A・GREEN 2026-06-14）

cancel-ACK 3 点を RED→GREEN で実装（各テスト先行赤を確認後に最小修正）。**新規 10 テスト GREEN・
既存退行ゼロ・import-purity 権威ゲート PASS**:

- **(a)** `kabusapi_execution.cancel_order` 受付返り `CANCELED`→`PENDING_CANCEL`（取消拒否は REJECTED 不変）。
  test: `tests/test_kabu_cancel_ack.py`（3・httpx 非依存の `_cancel_venue_order` スタブ）。
- **(b)** adapter 非同期 event を kernel broker へ fan-out。新 seam `KernelLiveDriver.apply_venue_async_event`
  （client_order_id 所有判定 → `broker.apply_venue_update(source="venue_stream")` → `_deliver` → 終端で
  `_orders` 除去）／`KernelLiveEngineController.apply_venue_async_event`（driver 転送）／`live_orchestrator
  ._publish_order_event` の fan-out（hasattr ガード）。test: `tests/test_kernel_live_cancel_ack_async.py`（1・
  本番 seam 全通し: resting→sync PENDING_CANCEL 受付→async 競合 PARTIALLY_FILLED 会計→async 終端 CANCELED・
  open leak 無し・未追跡 cid は無視）。
- **(c-1)** `ManualOrderFacade.cancel` の `status="CANCELED"` ハードコードを `res.status` 尊重へ。
  test: `tests/test_order_facade_cancel_ack.py`（3・PENDING_CANCEL は非終端で store も open / instant-confirm
  CANCELED は終端 / REJECTED は store 不変）。
- **(c-2)** `NautilusVenueExecClient._cancel_order` に `PENDING_CANCEL` 非終端分岐（保守修正・orphan 経路）。
  test: `tests/test_nautilus_exec_client_cancel_ack.py`（3・unbound メソッドを duck-typed self に適用し
  `generate_order_canceled` 呼び出しを Mock 観測）。

ゲート結果:
- `uv run pytest -q` → **176 passed**。残 8 failed は **pre-existing 環境要因**（`test_kernel_bars` /
  `test_kernel_golden_cpython`×2 / `test_kernel_risk_gate`×4 / `test_kernel_teardown_mono`・catalog parquet
  fixture 不在＋#34 high-precision build。stash した baseline でも同一 8 件失敗を確認＝#23 非起因。findings 0013
  §既知事項と一致）。
- `uv run python -m spike.kernel_live.run_mock_live` → `[KERNEL LIVE PURITY PASS] full-chain fills=2
  final_net=0.0 realized=200.0 resting=ACCEPTED->CANCELED cancel_calls=1 loop_daemon=True child_count=0
  loop_clean=True nautilus_leaked=0` / exit 0（kernel-live chain 健全・Rust core 非ロード不変）。
- `compileall` 変更 6 モジュール PASS。

### code-review 反映（simplify/high・2026-06-14・GREEN 181 passed）

`/code-review simplify`（recall-biased・7 angle）で挙がった指摘を RED→GREEN で修正し、再 verify で
Medium+ ゼロを確認:

- **(High) manual facade が PENDING_CANCEL のまま strand** — (c-1) で `res.status` を honor した結果、kabu
  manual cancel が `_states` を非終端 `PENDING_CANCEL` に置くが、poll 確定（`_publish_order_event`）は kernel
  driver / UI stream にしか届かず facade `_states` を更新しないため、`list_orders`（reconcile primitive）が
  取消済み注文を working order として永久に残し venue と desync した。**新 seam `ManualOrderFacade
  .apply_venue_event`**（owned 非終端のみ更新・累積後退は無視・terminal で終端化）を追加し
  `_publish_order_event` から routing。test: `test_order_facade_cancel_ack.py`（poll 確定 4 件追加）。
- **(Medium) auto 注文で空タグ UI event ＋ force_resync が二重発火** — `_publish_order_event` の直接 emit と
  broker 再 emit（`_on_auto_order_event`）が同一 poll event で重複。**`apply_venue_async_event` を bool 返却
  （owned）化**し、kernel が owned なら直接 emit ＋ resync を skip（manual / EC-only / 未知のみ従来経路）。
- **(Medium) async fill に反応した戦略注文が drain されない** — `apply_venue_async_event` の `_deliver` →
  `strategy.on_order` が積んだ intent が次の bus event まで送られない。sync 文脈から
  `asyncio.get_running_loop().create_task(self._drain())` を schedule（`_draining` ガードが再入直列化）。
  test: `test_kernel_live_cancel_ack_async.py::test_reaction_order_from_async_fill_reaches_venue`。
- **(Low) `OrderResult` 構築が try 外** → driver で try 内へ（非 canonical status を owned 扱いで握り二重 emit
  回避）。**(Low) isort** → driver の `order_types` import を `adapter` の後ろへ。
- **parity 記録**: legacy `NautilusLiveEngineController` に `apply_venue_async_event` counterpart は無い（Nautilus は
  自前 `LiveExecutionEngine` で venue event を捌くため routing しないのが正・orchestrator の `hasattr` ガードで
  吸収）。再 verify で本 routing 周りの Medium+ ゼロを確認済み。

## 実装結果（workstream B/C・2026-06-14・owner Mono/HITL 待ち）

C# は当 dev 環境で compile/run 不可（Unity/Mono 専用）。**新規 C# の API 参照を実 durable 型シグネチャに
照合して compile-blocking ゼロを確認**（subagent verify・SubmitPlaceOrder/SubmitCancelOrder 引数列・
`OrderRpcResult.OrderId`・`LatestAccount.Positions` 列挙・GUILayout.Toggle overload・static field
initializer・Editor↔runtime assembly 参照すべて整合）。

- **B**: `ProductionLiveShell.cs`（durable）＋ `ProductionLiveShellProbe.cs`（AFK 権威）。production composition は
  既存 `VenueLoginSecretHitlHarness` の engine bring-up / GIL 規律を踏襲し durable 型を再利用。
- **C**: `LiveDemoRoundtripMenu.cs`（owner HITL launcher・`[LIVE DEMO ROUNDTRIP PASS]` recorder）。

**HITL 実走で発見・修正（2026-06-14・Windows）**: production shell の Play 競合と login subprocess バグを 2 件解消し、Connect→login dialog 到達まで確認:
- **Play 競合**: `ReplayPanelsHarness`（既定 Play 所有者・`AutoBootstrapEnabled=true`）が PythonEngine を先取りし shell が `double-init` で停止。menu 駆動 leg へ Play を譲る既定の作法どおり、HITL 中は `ReplayPanelsHarness.cs:166` を一時 `false`（HITL 後 `true` に戻す・コミットしない）。
- **`LOGIN_SUBPROCESS_CRASHED`**（commit `a1ef5a6`・実バグ修正）: embedded Python（Unity/pythonnet）では `_resolve_python_executable()` が base CPython を返し、`_login_subprocess_env()` が venv site-packages を子へ渡していなかったため `login_dialog_runner`→`tachibana_auth` が `ModuleNotFoundError: httpx` でクラッシュ（#21 login 経路の Windows-embedded バグ・macOS 非顕在）。`_login_subprocess_env` が `sys.path` の site-packages を PYTHONPATH に伝播するよう修正。base CPython で reproduction（before=exit 1 httpx / after=exit 124 dialog 到達）＋ `tests/test_login_subprocess_env.py`（2 passed）。
- production shell 自体は GREEN: chrome 描画・badge・Order ticket・panels が production UI として表示、Connect で tkinter login dialog spawn まで到達（実 fill は別PC・JST 平日場中へ）。

### macOS HITL 実走（2026-06-14 日曜 20:57 JST・閉局・Tachibana demo）

owner 手動で `Tools > Backcast > Live Demo Roundtrip (Tachibana demo)` を macOS（Unity 6000.4.11f1・licensed Editor）で実走。閉局のため FILL leg は対象外とし「閉局でも確認できる leg」を回した。

**通過（✅）**:
- **接続**: session file cache（当日 11:00 作成）を自動復元・検証成功 → ログインダイアログ不要で `badge: Connected: TACHIBANA` / Positions・cash 取得。`LOGIN_SUBPROCESS_CRASHED` は macOS で非顕在（cache 経路で login subprocess も非経由）。
- **secret modal（#21）**: 発注時に `Second password (typed; masked, never stored as text)` modal が開き、masked 入力 → Submit → venue まで到達（secret roundtrip 動作）。
- **発注 REQUEST 経路**: BUY LIMIT `@100` は venue が `REJECTED`（8918 は uPnL=-36800/qty=400/avg=102 から現値 ≈ ¥10 と逆算でき、`@100` は値幅上限 ≈¥40 超のバンド外）。`@9` で `ACCEPTED`（resting・`filled=0@0`）。発注→venue 応答→UI 表示が end-to-end で動作。

**失敗（❌ cancel-ACK leg）**:
- resting order（`2e407d0e…` ACCEPTED）に対し **`Cancel last` を押した瞬間に Unity が SIGSEGV クラッシュ**（`~/Library/Logs/DiagnosticReports/Unity-2026-06-14-211808.ips`：`EXC_CRASH / SIGSEGV`）。faulting スタック（Editor.log）:
  ```
  #0 _PyObject_Malloc
  #1 _PyUnicodeWriter_PrepareInternal
  #2 PyUnicode_DecodeUTF16Stateful
  #3 Python.Runtime.Runtime:wrapper_native_indirect ... [Unity Child Domain]
  ```
  → `PyUnicode_DecodeUTF16Stateful` は .NET(UTF-16) 文字列を Python str へ marshal する箇所。
- **2回再現・同一フレーム・根本原因確定（clean state でも再現）**: PC 再起動後の clean state でも **完全に同じフレーム**でクラッシュ。しかも 2 回目は WS churn が `p_errno=2` 6 行のみ（1 回目 36 行）と少ないのに再発 → **churn は主因でなく誘発の補助因子**。原因は **`LiveRpcLanes.SubmitCancelOrder`（`Assets/Scripts/Live/LiveRpcLanes.cs:152`）が GIL 取得前に `new PyString(venue)` / `new PyString(orderId)` を構築している GIL 規律違反**。`new PyString` は内部で `PyUnicode_DecodeUTF16`→`_PyObject_Malloc` を呼び GIL 必須だが、`CallWrite(...)` の引数として評価されるため `CallWrite` 内の `using (Py.GIL())`（line 196）より前に走る。`CallPlace`（line 99-111）/`SubmitModifyOrder`（line 169-176）は **先に GIL を取ってから** PyString を生成しているため安全＝**cancel 経路だけのバグ**。fix: cancel も GIL 内で PyString を構築する（place/modify と同じ順序）。

**副次所見（記録候補）**:
- ⚠️ 注文 `REJECTED` の **理由コードが UI に出ない**（`REJECTED` のみ）。owner が原因を判別できない。
- ⚠️ `SECRET_TIMEOUT`（25s 絶対 timeout）で注文が失敗した**後も secret modal が開いたまま残存**。order 失敗時に modal を閉じる seam が無い。
- ⚠️ クラッシュは `Cancel` 操作中だったため、**demo venue 側に取消未完了の resting order（`2e407d0e…`）が残っている可能性**。再接続時に手動取消すること（demo・低リスク）。

**結論 / 切り分け**:
- cancel-ACK の **Python 層ロジック正当性**は pytest（191 passed）＋ import-purity で既に GREEN（本書 §workstream A）。本クラッシュは **C# adapter 層（`LiveRpcLanes` cancel 経路）の GIL 規律違反**であり Python ロジックバグではない。findings 0013 の「AFK probe flaky（箇所がばらつく偶発 race）」とは別物で、**cancel 操作で決定的に落ちる**ため要修正（fix は上記＝GIL 内で PyString 構築）。
- 本日は閉局のため `[LIVE DEMO ROUNDTRIP PASS]` 未記録。**cancel-ACK 視覚確認（受付→確定）+ FILL leg + depth live を JST 平日場中に再走**。

### fix 適用＋実機検証（2026-06-14 同日・clean state 再 Play）

- **fix（`Assets/Scripts/Live/LiveRpcLanes.cs`）**: `SubmitCancelOrder` を place/modify と同じく **`Py.GIL()` を取ってから `new PyString(venue/orderId)` を構築**する形に変更。GIL 取得前に引数を評価していた footgun helper `CallWrite`（cancel 専用・他に使用なし）を撤去。
- **RED ガード（`Assets/Editor/ProductionLiveShellProbe.cs`）**: `PhaseCancelLane` を追加。place→FILLED フェーズが cancel lane を一度も叩いていなかった（＝この AFK gap が crash を ship させた）。MOCK で cancel lane を駆動し「segfault せず clean return」を gate 化。
- **実機検証（macOS・Tachibana demo・閉局）**: fix 後に再 Play し `BUY LIMIT @9` ACCEPTED → **`Cancel last`** を実行。**一度も落ちず**に Cancel→secret modal（cancel も第二暗証番号要）→Submit→venue cancel RPC→応答まで完走（GIL クラッシュ解消を実機確認）。venue 応答は **`ERR CANCEL_REJECTED`**（`order_facade.py:310-312`：adapter `cancel_order` が `REJECTED`＝**閉局のため demo venue が cancel を拒否**。コードは正しく honor＝注文 live のまま）。
- **未達（market-hours-gated）**: 受付→確定の happy path（`PENDING_CANCEL`→`CANCELED`）は venue が cancel を ACCEPT しないと見えない＝**JST 平日場中が必要**。handoff の「cancel は閉局でも処理される」想定は本 demo では不成立（HITL 所見）。

**残ゲート（owner 実行・別PC 引き継ぎ＝issue #23 コメント 2026-06-14）**:
1. `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod ProductionLiveShellProbe.Run`
   → `[PRODUCTION LIVE SHELL PASS]` exit 0（Mono は findings 0013 §深掘りの borderline race で flaky・
   クリーン state で数回リトライ）。
2. `Tools > Backcast > Live Demo Roundtrip (Tachibana demo / Kabu verify)` で Manual + Auto roundtrip を
   **JST 平日場中**に実走（実 fill 観測）。`Record [LIVE DEMO ROUNDTRIP PASS]` → 本書 §実証結果に記録 → #4 close。

## 記録

- CONTEXT.md は既存項（「infinite canvas」chrome 定義 / 「venue 接続状態」/「取消受付 / 取消確定」/「LiveBroker」/
  「live event sink」）が本スライスの語彙を既に所有。新 term 追加は不要（実装で sharpen が出れば追記）。
- backcast に FLOWS.md は無く、本 findings ＋ AFK probe ＋ owner HITL leg が behavior gate の等価物。
- 新 ADR 不要（ADR-0001 / 0003 / 0004 が全決定を所有）。

## 再 home スライス（#23 再オープン・#59 統合後・grill 2026-06-16 確定）

#39→#59 完了で footer LiveAuto/mode segment は `BackcastWorkspaceRoot`（本線・ADR-0009）へ移管済み。
残る `ProductionLiveShell`（workstream B の IMGUI 暫定 production composition）は **#23 固有資産**を未だ所有する:
手動 order ticket / live panels（orders・positions・run-result）/ secret modal flow / demo roundtrip launcher・probe。
本スライスは **これらを mainline root へ再 home し、capability loss なしで `ProductionLiveShell` を退役**させる。
`ProductionLiveShell` は元から **Editor 起動の HITL harness**であり mainline normal-Play 面ではない（mainline =
`BackcastWorkspace` scene → root）。よって退役で mainline 能力は失われない（harness 接続経路のみ置換が要る）。

### RH1. D1 を mainline root で uGUI 実現し切る（IMGUI 持ち込みは不採用）

`ProductionLiveShell` は D1 の配置（tile / floating / chrome）を **IMGUI 固定 Rect で暫定実装**し、tile 結線を
「remaining additive integration」として先送りしていた。本線 root は完全 uGUI（scene-authored）なので、再 home は
**D1 を uGUI で realize する好機**。B案（IMGUI lift-and-shift）は「終わったように見えて再実装」になるため不採用。
TTWR `src/ui/{orders,positions,run_result_panel,order_panel,secret_modal}.rs` の分割パネル構成への忠実移植に当たる。

### RH2. Orders / Positions / Run Result = Hakoniwa 常設3タイル（D1 再確認・TTWR 両モード一致）

- TTWR `hakoniwa.rs` の `hakoniwa_tile_kinds(mode)`: Replay=`[Startup,BuyingPower,Orders,Positions,RunResult]` /
  Live=Startup 無しの4枚。**Orders/Positions/RunResult は両モードに存在**＝常設タイルが parity 忠実（D1 の「Hakoniwa tile」を裏取り）。
- backcast Hakoniwa を `[startup, chart]` → **`[startup, chart, orders, positions, run_result]` の5タイル**へ拡張。1枚集約は不採用（IMGUI 暫定の名残）。
- **mode 別 Hakoniwa 切替（TTWR の Startup 有無）は今回やらない**（backcast に無い機能・スコープ膨張回避）。
- **`BuyingPower` タイルは今回対象外**＝intentional divergence（将来スライス）。reopen スコープ・D1 とも非対象。
- tile id は **`orders` / `positions` / `run_result`**（durable `LayoutDocument.Default()` / `HakoniwaController.DEFAULT_ORDER` /
  `HakoniwaProbe` の golden が既に期待する id と完全一致＝テスト改変ゼロ）。`startup` は据え置き（`status` への
  リネームは #14 golden を壊すため不可）。
- **size 配分**: `HakoniwaGridMath` の `ceil(√n)` 均等分割（5→3×2・slot0=左上・6セル目空）。個別サイズ不可（divider resize は将来・findings 0007 §0）。
- **layout migration**: `HakoniwaController.Apply` が未知 id 無視・欠落 id 後追記する tolerance を持つため**自動**。
  旧 `[startup,chart]` 保存は新3タイルが後ろに append。**version bump / migration コード不要**。fresh load の並びは
  durable `Default()` 由来で `chart,positions,orders,run_result,startup`（startup 末尾）だが cosmetic（header swap で並べ替え→保存可）＝本スライスでは直さない。

### RH3. タイルは scene-authored placeholder（startup/chart と対称）

`BackcastWorkspaceSceneBuilder` に `NewRect("orders"/"positions"/"run_result", hakoniwaRoot)` を追加 →
root に `_ordersTile`/`_positionsTile`/`_runResultTile` の SerializedField を追加 → `BackcastWorkspaceProbe` の
serialized-ref assert に3つ追加（構造破損で headless 落ち）。中身は runtime `BuildTileChrome` + uGUI View。

### RH4. 再 home する3 surface の作り方

- **タイル中身**: 再利用1クラス `LivePanelTileView` を3回 Build し、tile ごとに整形 delegate を差す。権威は
  `_host.Panel`（`LivePanelViewModel`）。signature 比較で**変化時のみ Refresh**（footer 同様）。
- **Order ticket**: Strategy Editor と対称＝scene-authored frame → `_windows.Adopt(FloatingWindowCatalog.KIND_ORDER, …)`
  （`KIND_ORDER` は catalog 既存）→ 中身 uGUI `OrderTicketView`（BUY/SELL・qty・LIMIT・price・Place/Cancel）。
  `_host.Lanes.SubmitPlaceOrder/SubmitCancelOrder` 結線。**`LiveManual` の時のみ visible**（footer DisplayMode）。
- **Secret modal**: screen-fixed uGUI overlay（ScreenSpaceOverlay・Content 外・最前面・入力ブロック）。秘匿規律を
  uGUI で守るため **New Input System `Keyboard.current.onTextInput`（`Action<char>`）でモーダル中だけ1文字ずつ
  `SecretModalController.AppendChar`**、Backspace は `Keyboard.current.backspaceKey.wasPressedThisFrame`。
  `Input.inputString` 不使用・平文 managed string を作らない。Submit=`_host.Lanes.SubmitSecret`、
  Cancel=`_host.Modal.Cancel()`+`_host.Coord.SetSecretModalOpen(false)`。**`onTextInput` の購読解除を破棄/閉鎖時に必ず実施**（漏れ事故防止）。hybrid OnGUI は不採用（本線 root uGUI 方針）。

### RH5. venue 接続駆動と退役順（#42 境界）

- secret modal は login の `SecretRequired` で開くので roundtrip に venue 接続が要る。ただし **mainline グローバル
  メニューの Venue submenu UI は #42（OPEN）の責務**。よって:
  - **mainline root には #23 で接続 UI を足さない**（#42 を先取りして境界を濁さない）。root は seam＋surface を持つ。
  - **root-based HITL harness（新規）に接続アフォーダンスだけ**を置く（reopen の "drive the root **or a root-based
    harness**"）。**durable `VenueMenuViewModel.ConnectVariants` + `_host.VenueLogin` を再利用し、ProductionLiveShell の
    Connect UI ロジックは再実装しない**。MOCK / TACHIBANA / KABU を指して roundtrip 駆動。
  - `LiveDemoRoundtripMenu` を root-based harness spawn に張り替え。
- **AFK probe**: `ProductionLiveShellProbe` 相当を `WorkspaceEngineHost` seam 駆動に置換（MOCK で
  connect→place→FILLED→panel→**cancel-lane GIL 安全**→teardown、main GIL-free を assert）。**cancel-lane RED ガード
  （本書 §fix）を必ず引き継ぐ**。
- **退役**: root-based HITL path ＋ MOCK AFK probe が GREEN になったら **#23 内で `ProductionLiveShell` ＋
  `ProductionLiveShellProbe` を削除**し menu 張り替え。mainline は元々 ProductionLiveShell 非依存＝capability loss なし。
  #42 は本線 Venue submenu の描画/接続 UI を引き続き担当。

### RH 実装＋AFK 検証結果（2026-06-16）

新規 uGUI View: `LivePanelTileView`（再利用1クラス・3タイル）/ `OrderTicketView` / `OrderTicketWindowFrame`（`KIND_ORDER` 共有フレーム）/ `SecretModalOverlay`（ScreenSpaceOverlay・`onTextInput` char-drain・破棄時 unsubscribe）。`BackcastWorkspaceRoot` に5タイル化・Order 窓 adopt・secret modal 配線・`ConnectVenue` seam（host.VenueLogin 再利用）を追加。`BackcastWorkspaceSceneBuilder` が3タイル＋Order 窓を authored・6 ref 配線。HITL は `LiveDemoRoundtripHarness`（接続アフォーダンスのみ・root surface を駆動）＋`LiveDemoRoundtripMenu` 張替。AFK gate は `WorkspaceLiveSeamProbe`（`ProductionLiveShellProbe` 置換・cancel-lane RED ガード継承）。`ProductionLiveShell` ＋ `ProductionLiveShellProbe` を削除（外部参照はコメントのみ＝capability loss なし）。

**AFK GREEN（Unity 6000.4.11f1・batchmode・2026-06-16）:**
- compile gate（`-batchmode -quit`）exit 0・CS エラー無し。
- `BackcastWorkspaceSceneBuilder.Build` 成功（`missing serialized field` 無し＝6 新 ref 全配線）。
- `WorkspaceLiveSeamProbe.Run` → `[WORKSPACE LIVE SEAM PASS]`（connect→badge / place→FILLED→panel / cancel-lane GIL 安全 / teardown clean・main GIL-free maxStall=28ms）。cancel-lane は bogus order id で `success=False` だが segfault せず clean return＝RED ガード成立。
- `BackcastWorkspaceProbe.Run` → `[BACKCAST WORKSPACE PASS] all sections green`（5タイル Hakoniwa・ref assert・layout 4 次元 round-trip・ownership 不変）。

**残（owner 手動・market-hours-gated）:** 実 venue demo roundtrip（発注→約定→建玉反映・正常終了 resting 取消）を mainline workspace root ＋ `LiveDemoRoundtripHarness` で実走 → `[LIVE DEMO ROUNDTRIP PASS]` 記録 → #4 close。

### RH5 修正（code-review High/Medium・2026-06-16）

- **High（venue 結線）**: root が `_host.InitializePython()` を venue 無しで呼び `InprocLiveServer(..,"MOCK")` 固定だったため、後続 `venue_login("TACHIBANA"/"KABU")` が `VENUE_MISMATCH`（`live_orchestrator.py:684`）で弾かれ実 venue HITL が不能だった。**venue は one-per-server で server build 時に束縛**されるので、root.Awake で **`LIVE_VENUE` env（既定 MOCK・whitelist {MOCK,TACHIBANA,KABU}）**から解決し `InitializePython(_venue)` で正しい venue の server を建てるよう修正（tachibana skill の `LIVE_VENUE` 機構に準拠）。`.env.example` に `LIVE_VENUE`/`LIVE_INSTRUMENT` を追記。
- **Medium（instrument）**: harness の instrument が display-only だった。`root.FocusInstrument(iid)` を追加し `SelectedSymbol` に Set → manual ticket / chart が実際にその銘柄を指す。instrument は `LIVE_INSTRUMENT` env（無ければ menu preset）。
- **Medium（prod grey-out）**: harness が ConnectVariants 全件を無 gate で描画していた。**configured venue 1件の Connect ボタン**に集約し、`root.CanConnectConfigured()`＝`VenueMenuViewModel.CanConnectEnv`（prod は `*_ALLOW_PROD` 未設定で grey-out）で gate。per-variant / prod-bypass ボタンを撤去（#42 の prod-safety parity を回復、かつ server と異なる venue を harness から叩けない）。
- AFK 再 GREEN（compile gate clean / `[WORKSPACE LIVE SEAM PASS]` / `[BACKCAST WORKSPACE PASS]`）。
