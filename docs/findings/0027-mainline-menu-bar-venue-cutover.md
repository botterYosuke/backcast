# findings 0027 — cutover slice 2: 完成版 venue 統合 menu bar を本線 scene へ載せ替え

issue #42（cutover slice 2）。方針: **ADR-0010（永続単一 `WorkspaceEngineHost`）/ ADR-0005（1:1 表面 parity・固定）
/ ADR-0001 / ADR-0003 / ADR-0009（残り decision）**。`grill-with-docs`（2026-06-16）で導出。
oracle = TTWR `src/ui/menu_bar.rs`（Model A は findings 0017 で確定済）。
cross-ref: **findings 0017**（menu bar oracle / Model A / gap①②）, **findings 0026**（#39→#59 統合・WorkspaceEngineHost）。
ADR は自己保護条項を持つため本 findings に実装事実を記録し、ADR は参照のみ（書き戻さない）。backcast に FLOWS.md は
無いため、本 findings が RED→実装→GREEN→HITL の正本。

## 0. このスライスの正味（コード実態 vs slice table / memory）

cutover の slice 2 行は当初「venue 統合 menu bar（ProductionLiveShell chrome 撤去）」と書かれていたが、現 main の
事実関係でその表現は変質している。grill で確定した正味:

- **menu bar の brain は本線に既に載っている**: `BackcastWorkspaceRoot.cs:196-201` が `_host.Conn/_host.Coord` で
  `VenueMenuViewModel` + `MenuBarViewModel` を構築済み。欠けているのは **View と side-effect の配線**であって logic ではない。
- **本線 `MenuBarView` は File-only stub**: `File ▾` + 死んだラベル "Edit/Venue/Help" のみ（venue submenu 無し）。
  root は **mode 副作用を捨てている**（`OnFileNew` の `modeRequest` 破棄・`OnFileOpen` の `FileOpenModeSideEffect()` を `_ =`）。
- **完成版 venue 統合 menu bar は `ProductionLiveShell` の IMGUI**（`DrawMenuBar`/`DrawVenueMenu`/`DrawFileMenu`/
  `DrawOpenSubmenu`・`ConnectEnv`・`SendMode` single-flight・F1 描画順 fix 込み）に在る。同じ 2 brain を使う。
- **`WorkspaceEngineHost` に `VenueLogin`+`SetExecutionMode` はあるが `VenueLogout`/`Disconnect` が無い**（host gap）。
- **未決(B)（findings 0026）**: 本線 scene に venue 接続 UI が無く LiveAuto まで未到達。本スライスで開通する。

→ **slice 2 = 「ProductionLiveShell の実証済み venue submenu + File 操作 mode 副作用を本線 `MenuBarView`/root へ移植し、
host `VenueLogout` を足し、LiveAuto-on-mainline を開通する」**。brain は完成済。

> **memory / 旧 slice table の訂正**: 「ADR-0010」「`0026-production-engine-unification-cutover.md`」という名は
> 実体が無かった。engine 統合は **`WorkspaceEngineHost`**（commit `e905b50`「#39→#59 Step 2」）として shipped、
> cutover の記録は **findings 0026 の #39→#59 統合節**。本スライスで **ADR-0010 を正式起票**（ADR-0009 D4 を supersede）し、
> 名実を一致させた。

## 1. 確定事項（grill 2026-06-16・Q1〜Q7）

### (D1) スコープ = 本線へ足す / Shell は温存（Q1）
本線 `MenuBarView`/root へ venue submenu + mode 副作用 + host `VenueLogout` を足す。`ProductionLiveShell` の venue/menu
chrome は **撤去しない**——`LiveDemoRoundtripMenu.cs:50` が今も spawn する #23 HITL harness の home であり、findings 0026
§248 が全面退役を **slice 4（#23 re-home 前提）** に置く。旧 slice table の「撤去」は本スライスから外し slice 4 へ移す。

### (D2) MOCK 接続 = parity menu は4件のまま / MOCK は out-of-parity dev 裏口（Q2）
`VenueMenuViewModel.ConnectVariants`（4件: Tachibana Demo/Prod・kabu Verify/Prod）は **触らない**（`MenuBarVerify.cs:33`
が `Length==4` を guard）。MOCK は TTWR oracle surface ではない credential-less dev venue。
- **AFK / probe**: `_host.VenueLogin("MOCK","env","",…)` を直接駆動（UI 不要）。
- **owner HITL のみ**: `UNITY_EDITOR` / dev gate 配下で **`Connect MOCK (dev)`** affordance を出す。`ConnectVariants` 由来
  ではなく root/view の HITL-only wiring として扱う（`ProductionLiveShell.ConnectEnv` の `venue=="MOCK"` 特例と同形）。

### (D3) File→New = full TTWR parity（in-memory reset）＋ LiveManual 副作用（Q3/Q4）
findings 0017 §4 の canonical full reset を、canvas を持つ本線で**初めて完全に満たす**。**ただし adopt 不変条件を守る**:
- 実行中（`IsRunning`）は既存 `MenuBarViewModel.FileNew()` の **refuse** を維持（ADR-0001・clear も mode も触らない）。
- idle なら in-memory reset を full に:
  - `_scenario.Clear()` ＋ `_tile.SyncFieldsFromController()`。
  - **adopt 済み Strategy Editor window（`WINDOW_ID = strategy_editor:region_001`・scene-authored）は絶対 destroy しない**
    （findings 0025 §8）。`StrategyEditorView.ResetUnboundEmpty()`（**新規 public method**：内部で `Document.ResetUnboundEmpty()`
    + InputField/token 表示 sync + history clear を view 境界に閉じ込める）で空の unbound editor に戻す。
  - **追加 spawn された editor/window のみ** destroy + `_registry.Unregister(id)` + `_editors.Remove(id)`。
  - canvas pan/zoom と Hakoniwa は **触らない**（§4 reset 対象は strategy/scenario/editor state であって workspace
    camera/layout ではない）。disk layout は **保存しない**（in-memory reset・sidecar 非破壊）。
- 捨てていた `modeRequest` を配線: 接続中（`LiveModeAllowed`）のみ `_host.SetExecutionMode("LiveManual",…)`（gap②ガード）。

> ⚠️ **`_windows.Apply(LayoutDocument.Default())` を File→New に使ってはならない**: `FloatingWindowController.Apply`
> は full replacement で doc に無い live window を `_destroy` する（`FloatingWindowController.cs:166-189`）ため、adopt 済み
> editor を破棄して findings 0025 §8 を破る。本線 `RestoreLayout` が意図的に加算的 `RestoreFloating` を使う理由と同じ。

### (D4) File→Open mode 副作用の配線（Q3）
`OnFileOpen` の `FileOpenModeSideEffect()` 戻り値を捨てず、Live 中（`LiveModeAllowed`）なら **layout load の前に**
`_host.SetExecutionMode("LiveAuto",…)` を送る（TTWR: Open WHILE Live → LiveAuto・findings 0017 §1）。Replay mainline では null＝no-op。

### (D5) host `VenueLogout` を追加（Q6）
`WorkspaceEngineHost.VenueLogout(Action<bool> onResult)` を新設。`_coord.CanUserLogout`（D7 Wall 1）でゲートし、
`ProductionLiveShell.Disconnect()` と同形で **off-main thread・single-flight**（`_liveRpcInFlight`）。menu Disconnect は
`VenueMenuViewModel.CanDisconnect` を満たすときのみ host へ。Connect は `_host.VenueLogin` へ（root は ProductionLiveShell を触らない）。

### (D6) 描画方式 = OnGUI 維持・実証済み IMGUI を移植（Q5）
`MenuBarView` の OnGUI / scene-authored container clipping を維持し、`ProductionLiveShell` の `DrawFileMenu`/`DrawVenueMenu`/
`DrawOpenSubmenu` を移植（F1 教訓: submenu は bar より後段・他 OnGUI chrome に潰されない depth/order で描く）。`Edit`/`Help` は
項目 present・未結線は disabled/stub。**uGUI 化 / OnGUI 除去は follow-up issue**（`MenuBarView` ヘッダの明記どおり・silent drop にしない）。

## 2. 検証（RED 先行・正本は本 findings）（Q6）

- **`MenuBarVerify`（継続）**: VM 純粋ロジック gate。4 variant parity / prod grey-out / File decision / mode 副作用 decision。
- **新規 root AFK probe（`BackcastWorkspaceRoot` 実体対象）**: File→New full reset（adopt editor 空化・追加 editor/window 破棄・
  registry unregister）/ File→New→LiveManual・File→Open→LiveAuto の mode 副作用 routing / `VenueLogin`/`VenueLogout` routing。
- **host roundtrip**: MOCK login → connected poll → `VenueLogout` → disconnected/final snapshot/lanes teardown。
- **本線 HITL（未決 B を閉じる）**: workspace scene で dev MOCK connect → Venue submenu → LiveAuto ▶ → lifecycle/panel 反映 →
  Replay/Manual へ抜けて teardown（orphan 不在）。`ProductionLiveShell`/#23 harness は不改造。
- **Unity batchmode ゲートは owner 実行**（この dev 環境に editor binary 不在）。判定は wrapper exit code ではなく
  `UNITY_EXIT=0` + ログ `Exiting batchmode successfully` + `error CS` 0 件で（`grep -c "error CS"` の 0 件 exit 1 落とし穴に注意）。

## 2.1 検証実績（TDD・Unity 6000.4.11f1 実機 batchmode・2026-06-16）

`/tdd` で RED 先行。**この dev 環境に Unity 6000.4.11f1 が在る**（`Unity.exe` 確認）ため AFK gate を当 dev で実走。

- **TB1（File→New full reset）RED→GREEN**: `Assets/Editor/MenuBarCutoverProbe.cs` を新設。
  - RED: `OnFileNew` が adopt editor を空化しない実装のまま走らせ、**`[MENU BAR CUTOVER FAIL] File→New did not reset the adopted editor text to empty`**（error CS 0・compile 成功・assertion で正しく RED）を確認。
  - GREEN: `StrategyEditorView.ResetUnboundEmpty()`（Document.ResetUnboundEmpty + history.Clear + SyncFromDocument）/ `FloatingWindowController.Close(id)`（単一 window destroy・adopt 不変条件を守る）/ `OnFileNew` 全書き換え（追加 editor のみ Close+Unregister+_editors.Remove・adopt は ResetUnboundEmpty・scenario.Clear・接続中のみ LiveManual 副作用）を実装 → **`[MENU BAR CUTOVER PASS] all sections green.`**・`UNITY_EXIT=0`・`error CS` 0。
- **回帰 GREEN（全実装後に再走）**: `MenuBarCutoverProbe` PASS / `BackcastWorkspaceProbe`（#59 root・Bind 拡張と OnFileNew を exercise）PASS / `MenuBarVerify`（VM 純粋ロジック）**16/16 PASS**。`error CS` 0。
- **AFK 非対称の判定（findings 0026 と同じ env 制約）**: venue login/logout・mode 副作用の **実 RPC** と uGUI/OnGUI 実描画は Python/UI 依存で headless 決定不能 → **owner 実機 HITL leg**（下記）。compile は当 dev の batchmode で GREEN 済。

### 実装（GREEN・本スライス成果）
- `StrategyEditorView.ResetUnboundEmpty()`（新規・view 境界で document/InputField/token/history を一括リセット）。
- `FloatingWindowController.Close(string id)`（新規・単一 window destroy。Apply(Default()) の adopt 破棄を回避）。
- `BackcastWorkspaceRoot.OnFileNew`（full reset・adopt 安全・LiveManual 副作用配線）/ `OnFileOpen`（LiveAuto 副作用を捨てず送出）/ `OnVenueConnect`・`OnVenueDisconnect`（host RPC routing・MOCK は "env" source）/ worker→main の venue login/logout ack（`DriveFooter` で消費）。
- `WorkspaceEngineHost.VenueLogout`（新規・coord quiet-lane gate・off-main・`_loginRunning` で login と相互排他）。
- `MenuBarView`（File/Edit/Venue/Help を OnGUI 描画・Venue submenu は 4 variant + Disconnect + 編集 only dev MOCK connect・Edit/Help は disabled stub）。

### owner HITL leg（findings 0026 未決 B を閉じる・実機 Play 必須）
本線 `BackcastWorkspace.unity` を Play（root が単一 Python owner）→ **Venue → Connect MOCK (dev)** → 接続 badge →
footer mode を **Auto** → **▶** で LiveAuto 起動 → panel 反映 → **Replay/Manual へ抜けて teardown**（orphan 不在）。
追加目視: File→Open while Live → `execution_mode==LiveAuto`、Venue→Disconnect → badge Disconnected、ProductionLiveShell/#23 harness は不改造。
判定は `UNITY_EXIT=0` + `Exiting batchmode successfully` + `error CS` 0（`grep -c "error CS"` の 0-match exit-1 落とし穴に注意）。

### owner HITL 実機結果（2026-06-16・実機 Play・PASS）
本線 `BackcastWorkspace.unity` で **1〜4 全 PASS**（findings 0026 未決 B クローズ）:
- **Connect MOCK (dev)** → `Connected: MOCK` / `mode: LiveAuto`。
- footer **Auto → ▶ → ⏸**（LiveAuto 起動・footer status に `LiveAuto: <run_id>` 表示）。
- **Replay/Manual へ抜けて ⏸→▶**（orphan-teardown）。
- **Venue → Disconnect**（走行中でも sequenced teardown・下記 review fix）。Console error 0・teardown complete。

**HITL 中に判明した非-slice2 ブロッカー 2 件（解消手順）**:
1. **register が `STRATEGY_LOAD_FAILED`**: scenario sidecar `kernel_spike_buy_sell.json` が `{schema_version, instruments}` だけ（params 欠落）で、loader は **sidecar を inline `.py` より優先**するため落ちた。sidebar の universe writeback が params 無し sidecar を書くのが原因（#29/#31）。→ 完全な sidecar（start/end/granularity/initial_cash＋instruments）に置換で解消。
2. **▶ がサイレント no-op（glyph 不変）**: sidecar 削除後、root が `Populate` に **inline-.py fallback を渡さない**ため universe が空 → `BlockedNoInstrument`。→ 完全 sidecar で universe を満たして解消。**観測性 fix**: `FooterAutoStart` の pre-flight block 理由を menu notice に表示（`_autoStatus` は mainline 未描画＝slice 3）。

### review 指摘の修正（external review・2026-06-16）
- **[High] `VenueLogout` の serialization/sequencing**: ① `VenueLogout` を `_loginRunning` 単独 → **`BeginLiveRpc` single-flight 参加**（mode/start/stop と直列化）。② 新規 `WorkspaceEngineHost.StopLiveThenLogout(runId)`＝**stop_live_strategy → set_execution_mode(Replay) → venue_logout を 1 worker で順次**（venue_logout の `_teardown_live_components`（`live_orchestrator.py:848-851`）が footer の auto-replay 復帰と競合しない）。③ `OnVenueDisconnect` を **in-flight 中 block ＋ 走行中は StopLiveThenLogout / それ以外は VenueLogout** に分岐。
- **[Med] AFK 被覆と docs 文言の齟齬**: §2.1 の通り、venue login/logout・mode 副作用の実 RPC は **owner-HITL leg**（headless 決定不能）であることを明記済（AFK probe は File→New full reset のみを value-assert）。
- **観測性の恒久改善（keep）**: `register/start_live_strategy` の `error_message` を C# ログに表面化（サイレント failure 解消）／LiveAuto start 失敗・▶ pre-flight block を menu notice 表示／login 成功で transient notice クリア。

## 3. follow-up（silent drop にしない）
- **(d) mainline `Populate` に inline-.py SCENARIO fallback が渡らない** → sidecar 不在で universe 空＝LiveAuto 不可。root の `ResolvePaths` が `load_scenario` 由来 fallback を渡すか、空時に inline へ倒す（#29 領域）。
- **(e) sidebar の universe writeback が params 無しの不完全 sidecar を書き、live `register_live_strategy`（backtest SCENARIO 必須）を壊す**。live register は backtest 窓（start/end/...）を本来不要 → register の scenario 要件緩和 or writeback の merge 保全（#29/#31/#39 領域）。

- (a) `MenuBarView` の uGUI 化 / OnGUI 除去（本スライスは IMGUI 移植）。
- (b) native file picker + `File→Save As` + 任意パス Open（multi-document layout surface・findings 0017 §9(a)）。
- (c) slice 3（発注/建玉/Auto パネル移設・#23/#39/#57）/ slice 4（`ProductionLiveShell` 全面退役＋parity 仕上げ）。

## 追記（2026-06-16・cutover slice 3/4 を #23 re-home が実現）

本 findings の follow-up (c) に挙げた **cutover slice 3（発注/建玉/Auto パネル移設）と slice 4
（`ProductionLiveShell` 全面退役）は、slice 2（本 findings）マージ後に landed した `#23` re-home で
実現済み**（commit `381d58c`「feat(#23): re-home live trading surfaces into BackcastWorkspaceRoot;
retire ProductionLiveShell」・現 HEAD `872739d`）。正本は **findings 0014 §「再 home スライス（#23
再オープン・#59 統合後）」**（RH1〜RH5・AFK GREEN）。要点:

- Orders/Positions/RunResult → 5タイル Hakoniwa の `LivePanelTileView`（`_host.Panel` 給電）。
- 手動発注 → `OrderTicketView`（`KIND_ORDER` adopt 窓・LiveManual 限定・Place/Cancel→`host.Lanes`）。
- secret modal → `SecretModalOverlay`（ScreenSpaceOverlay・`onTextInput` char-drain）。
- **`ProductionLiveShell.cs`/`ProductionLiveShellProbe.cs` を削除**（capability loss なし・`WorkspaceLiveSeamProbe`
  が cancel-lane GIL RED ガードを継承）。

→ cutover「4 スライス」の旧 framing は #23 が slices 3+4 を吸収して**追い越した**。slice-3 近傍で残る
OPEN は **#57（`DepthLadderView` の本線 chart tile mount・Live で板表示／Replay で隠す）** と owner
実機 demo roundtrip HITL（#4 close・market-hours-gated）。本 findings（slice 2 = menu bar）の成果自体は不変。
