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
</content>
</invoke>
