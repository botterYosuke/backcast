# findings 0064 — OrderTicket サーフェス E2E runner 昇格（第二波11本目・全行新規）

## 概要

手動 Order ticket サーフェス（`OrderTicketView` フォーム ＋ `BackcastWorkspaceRoot.OnManualPlace/OnManualCancel/
DriveOrderTicket` の検証・lane marshalling）の回帰ゲートを `Assets/Tests/E2E/Editor/OrderTicketE2ERunner.cs` として
**全行新規**著述（台本 `OrderTicketE2ERunner.md`）。既存 view 側 probe はゼロ（`WorkspaceLiveSeamProbe`/
`VenueLoginSecretProbe` は lane 機構を直接叩くもので view 側フォーム＋検証ゲートは未カバー）。ORDER-01..15 を
`自動(E2E済)` 化、ORDER-16 は HITL専用（実 venue 約定）。

## section ↔ Action ID

- **SectionA_FormView**（`OrderTicketView` を bare RectTransform 下に Build・Python-FREE）= **ORDER-01/02/03/04**。
  `_sideBtn`/`_typeBtn` の `onClick.Invoke()` で BUY/SELL・MARKET/LIMIT トグル（+ price 行 activeSelf）、`_qty`/`_price`
  の `.text` 設定で `Qty`/`Price` prop を観測。
- **SectionB_ValidationGates**（未接続の実 root・Python-FREE）= **ORDER-06/07/08/11a**。`OnManualPlace`/`OnManualCancel`
  を反射 invoke し `OrderTicketView._status` テキスト（"last order: …"）で拒否理由を観測。
- **SectionC_DisplayState**（未接続の実 root・Python-FREE）= **ORDER-12/13(disabled)/14/15**。`FooterModeViewModel.ApplyPoll`
  で footer mode を駆動し `DriveOrderTicket` を反射 invoke。window activeSelf / instrument ラベル / interactable / dirty
  marshal を観測。
- **SectionD_MockLaneWiring**（接続済み MOCK lane）= **ORDER-05/09/10/11b/13(enabled)**。`host.InitializePython("MOCK")`
  → `VenueLogin` → pump で badge CONNECTED 収束 → `OnManualPlace`/`OnManualCancel` を反射 invoke。

## 設計判断 — 反射駆動（OrderTicketValidation を抽出しない）

issue #94 は「`OrderTicketValidation` 抽出 or mock venue を grill で決める」と書くが、**抽出は不要**と判断（production
変更なし＝parity-first・最小 diff）。根拠: `OnManualPlace`/`OnManualCancel` の検証ゲートはいずれも `_orderTicket.SetStatus(...)`
を**同期で呼んで return**する。よって実 root を反射合成し（RunButton SectionD と同型）private メソッドを反射 invoke し、
`OrderTicketView._status` を反射で読めば拒否理由・受領 status が観測できる。抽出は production を変えるコストに見合わない。
これは設計原則で決まる（owner 判断ではない）。

## ゲート順と ORDER-09 の配置（非 vacuity）

production の発注検証ゲート順（`BackcastWorkspaceRoot.cs` 1213→1225）:
1. qty parse 失敗 / `qty<=0` → "invalid qty"
2. （LIMIT 時）price parse 失敗 / `p<=0` → "invalid limit price"
3. `!ServerReady || !Conn.IsConnected || Lanes==null` → "connect a venue first"
4. `ManualInstrument()` 空 → "select an instrument …"（live-order safety: 任意銘柄へ流さず拒否）

connect ゲート(3)が instrument ゲート(4)の**手前**にあるため、**ORDER-09（instrument 未解決）は接続済み host でしか
非 vacuous に検査できない**。未接続 root で qty 妥当 place を投げると(3)で先に弾かれ "connect a venue first"＝ORDER-08 を
検査してしまう。よって台本の「ORDER-09 は Python-FREE 候補」は誤りで、ORDER-09 を SectionD（接続済み MOCK）に置いた。

## vacuity 回避（delete-the-production-logic litmus）

拒否系 section は同期 `_status` テキストが early-return 経路の証拠。SectionD が同一接続 host 上で ORDER-05 happy place が
実際に lane を呼ぶ（`_manualOrderId` が立つ）ことを実証するので、ORDER-09 の「lane 未呼出（`_manualOrderId`/
`_manualStatusDirty` が clean のまま）」が意味を持つ（RunButton SectionD の blocked-vs-ready 同型）。

lead が実走できる RED litmus（production を一時破壊→該当 section FAIL→復元→GREEN）:
- `OnManualPlace` の qty ゲート（`SetStatus("invalid qty"); return;`）を消す → ORDER-06 が "connect a venue first"（未接続）に流れて FAIL。
- LIMIT price ゲート（`SetStatus("invalid limit price"); return;`）を消す → ORDER-07 が "connect a venue first" で FAIL。
- connect ゲート（`SetStatus("connect a venue first"); return;`）を消す → ORDER-08 が "select an instrument …" に流れて FAIL。
- instrument ゲート（`SetStatus("select an instrument …"); return;`）を消す → ORDER-09 が lane を呼び `_manualOrderId` が立つ／reject 文言が出ず FAIL。
- `DriveOrderTicket` の `liveManual` 可視性（`SetActive(liveManual)`）を固定値化 → ORDER-14 FAIL。
- `SetInteractable(...)` の gate を `true`/`false` 固定化 → ORDER-13（disabled/enabled の片方）が FAIL。
- `DriveOrderTicket` の dirty marshal（`SetStatus(_manualStatusLine)`）を消す → ORDER-15 FAIL。
- `ManualInstrument` の `_footerSelected` 優先 / `Universe.Ids[0]` フォールバックを入替 → ORDER-12 FAIL。

各 section は反射 lookup（field/method）を全て null-guard し（rename → opaque NRE を防ぎ `"... not found (renamed?)"`
で明示 FAIL）、widget presence を先に Check してから負 assert を置く。

## MOCK lane の使い方（SectionD）

`WorkspaceLiveSeamProbe` の lane mechanics を参照（移送ではなく recipe 参照）:
`host.InitializePython("MOCK")`（batchmode 所有権スキップを迂回する正当手）→ `host.VenueLogin("MOCK","env","",cb)`
→ pump（`host.DrainLiveEvents()` + `host.Conn.ApplyStatePoll(host.LatestStateJson)`）を `host.Conn.IsConnected` 収束まで
回す → これで `OnManualPlace` の connect ゲートが通る。place ACK は lane の結果スレッドが `_manualOrderId`/
`_manualStatusDirty` を立てるので WaitUntil で待つ。teardown は `host.Stop()`（MOCK を起こした SectionD の finally のみ）。

## Covers

ORDER-01（BUY/SELL）/ ORDER-02（MARKET/LIMIT＋price 行）/ ORDER-03（qty）/ ORDER-04（limit price）/ ORDER-05（妥当 place→ACK）/
ORDER-06（qty 不正拒否）/ ORDER-07（limit price 不正拒否）/ ORDER-08（未接続拒否）/ ORDER-09（instrument 未解決拒否・接続済み非 vacuous）/
ORDER-10（cancel→oid 解決→ACK）/ ORDER-11（cancel 拒否: 11a 未接続 "not connected" / 11b 対象なし "no order to cancel"）/
ORDER-12（instrument 表示・footer 優先＋Universe[0] フォールバック）/ ORDER-13（interactable ゲート: 未接続 off / 接続 on）/
ORDER-14（LiveManual のみ可視）/ ORDER-15（status worker→main marshal）。ORDER-16 = HITL専用（実 venue 約定）。

## 検証（2026-06-19 lead 実走・確定）

- compile-only: `error CS\d+` **0 件**・`Exiting batchmode successfully` / return code 0・新 `.meta` 生成。
- AFK GREEN: `-executeMethod OrderTicketE2ERunner.Run` で `[E2E ORDER TICKET PASS] form toggles + validation gates +
  display/state + MOCK lane place/cancel wiring green.` を bash `grep -a` で **1 件確認**・FAIL 0 件・sentinel
  （`Found no leaked weakptrs` / Package Manager shutdown）あり＝executeMethod 実走（SectionD の MOCK Python init・
  place/cancel lane 到達まで成功）・exit 0。RED litmus（上記 §）は lead 任意（vacuity は SectionD で ORDER-05 happy が
  同一 host で lane 到達を実証する構成＋全反射 lookup null-guard で担保）。
