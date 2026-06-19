# behavior-to-e2e — TTWR(Bevy + Python gRPC) 専用メカニクス（legacy reference）

> **このファイルは移植元 TTWR(Bevy) リポ向け。backcast（Unity C#＋埋め込み Python）では使わない。**
> backcast の正本は `SKILL.md` 本体（AFK probe＋findings＋ADR-0015 台本）。
> TTWR は凍結 fallback で going-forward 開発は backcast（CONTEXT.md）。TTWR リポで E2E を触るときだけ本ファイルを読む。
> `tests/e2e/FLOWS.md` / `tests/e2e_replay.rs` / Bevy `Harness` / `BackendStatusUpdate`→resource seam の詳細は以下。

## なぜこの形なのか（先に理解する）

本アプリには複数の検証面がある。backend（Python gRPC、replay/live runner）が push する状態は
`BackendStatusUpdate` / `BackendEvent` / replay clock を注入すれば deterministic に検証できる。一方で、
メニュー、モード切替 gating、Strategy Editor、Startup パネル、銘柄ピッカー、チャート操作、注文フォーム、
モーダル、レイアウト保存は、ユーザー入力・Bevy UI entity・ファイル I/O・描画 state が本体であり、
backend→ECS seam だけでは十分条件にならない。

したがって flow には必ず `kind` を割り当てる:

| kind | 使う場面 | 主な観測 |
|---|---|---|
| `state` | backend→ECS seam で十分に検証できる挙動 | resource 状態 |
| `ui` | Bevy UI 操作、キーボード、focus、modal、gating | `Interaction` / `ButtonInput<KeyCode>` / text input 注入、entity `Text` / `Display` / command channel |
| `render` | headless resource では画面崩れを検出しづらい挙動 | 実ウィンドウ smoke、スクリーンショット、構造化 UI dump |
| `integration` | CLI、backend、file I/O、env guard、実プロセス | temp fixture、出力ファイル、gRPC 応答、exit status |
| `manual-gate` | 実口座など自動化が危険または不可能なもの | 手順、期待結果、実施記録。必ず自動 smoke と組み合わせる |

## ワークフロー

1. **挙動を 1 文の不変条件に言い換える**。「Run したら完了する」→「`RunStarted` の後 `RunComplete` を受けると
   `RunState` が `Running`→`Completed` になり `parsed_summary` が埋まる」。UI 操作なら
   「クリック/入力後、どの entity/resource/file/command が変わればユーザー行動が保証されたと言えるか」まで落とす。
   曖昧なら何を観測すれば「動いた」と言えるかをユーザーに確認する。
2. **FLOWS.md に該当 flow があるか見る**。あれば ID（A1/B2/D3…）と seam・観測がそのまま設計。
   なければ新規 flow として、近いセクション（A〜L）に `- [ ]` 行を追記してよい。
   ⚠️ **「この挙動は既にテストされているか」を判定するときは `tests/e2e/` だけ見て『未カバー』と結論しない**。
   spawn/dedup/視認性のような **UI system は `src/ui/**` の `#[cfg(test)] mod tests` に unit テストが
   ある**ことが多い（dispatcher の spawn/重複は `tests/e2e/flows/m1,m5` ではなく `floating_window.rs` の
   `order_dispatcher_tests` にある等、system 本体の隣に置かれる）。`grep -rn '<system名>\|<Marker>' src/ tests/`
   で **src と tests の両方**を当たってから gap を申告する（#25 の確認で、dispatcher Order arm を
   m1/m5 だけ見て「欠落」と誤判定し、実際は floating_window.rs unit テストで既出だった）。
   ⚠️ **新規 ID を採番する前に衝突を必ず確認する**: FLOWS.md の「保留中の `A5`/`C5`/`D8` など」リストと
   `docs/wiki/**` の `[<ID>]` 参照を両方 grep する。**保留中（planned）の ID が wiki では既に別挙動に
   bind 済み**ということがある（例: #32 Slice 2 で C5 を採ろうとしたら、FLOWS.md は C5 を planned 扱いの一方
   wiki venues.md が `[C5]`=`SelectedSymbol` 更新の planned flow に既に割当済みだった → C6 に採番し直した）。
   `grep -rn '\[C5\]' docs/wiki tests/e2e/FLOWS.md` で空でない ID は避ける。
3. **kind を決める**。backend→ECS の resource 変化だけで十分なら `kind:state`。UI 操作・入力・未送信 gating は
   `kind:ui`。ファイル/CLI/backend/env は `kind:integration`。見た目崩れや overlap は `kind:render`。
   実口座など自動化が危険なら `kind:manual-gate` だが、必ず自動 smoke と組み合わせる。
4. **state flow は seam を特定する**（下表）。`src/backend_sync.rs` の `apply_status_update` と
   `backend_event_drain_system` が「どの `BackendStatusUpdate` / `BackendEvent` がどの resource をどう変えるか」の
   正解。推測せずここを読む。
5. **ui flow はユーザー入力 seam を特定する**。`Interaction::Pressed`、`ButtonInput<KeyCode>`、`MouseWheel`、
   text input、focused entity、time advance、command channel のどれを注入し、どの entity/resource/file を assert するかを決める。
   「command を送らない」ことが仕様なら、transport channel を監視して未送信を assert する。
6. **integration/render/manual-gate flow は代替方式を書く**。OS file dialog は選択済み path event/resource を注入する。
   実ウィンドウ smoke は `BACKCAST_E2E=1` の固定 fixture、スクリーンショットまたは構造化 UI dump で確認する。
   実 venue は env isolated backend integration と手順付き manual gate に分ける。
7. **実装する**。`kind:state` は `tests/e2e_replay.rs` に `#[test]` を足す。`Harness::new()` → seam を send →
   観測を assert。`kind:ui` / `kind:integration` / `kind:render` は既存 harness が無ければ `tests/e2e/support/`
   に薄い helper を足し、flow ごとに最小実装する。
8. **観測アクセサが無ければ Harness に足す**（`tests/e2e/support/mod.rs`）。既存の `portfolio()` と同形:
   `self.app.world().resource::<T>().clone()`。`BackendStatus` は `Clone` 非実装なのでフラグを直接返す
   （`backend_connected()` 等の前例あり）。
9. **走らせる**。state harness は `cargo test --test e2e_replay`。**初回コンパイルは Bevy 全体をリンクするため ~11 分**
   かかる（バックグラウンド実行推奨）。integration/render は flow に書いた release gate command を実行する。green を確認。
10. **FLOWS.md を更新**: 実装した flow を `- [x]` にし、末尾「実装状況」に 1 行追記。
11. 完了したら `CLAUDE.md` 規約に従い `simplify` と `post-impl-skill-update` を発動。

## state seam → resource 早見表

| 検証したい挙動 | 送る seam | 観測する resource |
|---|---|---|
| run のライフサイクル | `RunStarted` / `RunComplete{startup_id,run_id,summary_json}` / `RunFailed{startup_id,error}` | `LastRunResult.state`(`RunState`) / `.parsed_summary` |
| replay clock（pause/resume/step） | `h.push_state(ts)`（`BackendChannel`、backend→ECS clock） | `TradingSession.timestamp_ms` |
| 起動進捗・相関 ID | `ReplayStartup{startup_id,stage}`（要 `h.begin_startup(id)` 先行） | `ReplayStartupProgress.phase` / `.visible` / `.start_engine_accepted` |
| ポートフォリオ | `PortfolioLoaded{...}` | `PortfolioState`(`loaded`/`equity`/`positions`/`orders`) |
| 銘柄ユニバース | `InstrumentsListStarted/Listed/Failed{source,...}` | `Tickers`(`status`/`list`/`source`) |
| 上場銘柄 fetch | `AvailableInstrumentsLoaded/FetchFailed{end_date,...}` | `AvailableInstruments`(`by_end_date`/`in_flight`/`last_error`) |
| venue ライフサイクル | `VenueChanged{state,venue_id,instruments_loaded}` | `VenueStatusRes`(`state`/`instruments_loaded`) |
| 実行モード | `ExecutionModeChanged{mode}` | `ExecutionModeRes.mode` |
| ライブ価格 | `LastPricesUpdated{prices}` | `LastPrices.map` |
| 接続状態 | `Connected(bool)` / `Running(bool)` / `Error(e)` | `BackendStatus`(`connected`/`running`/`last_error`) |

## UI / integration flow 早見表

| 検証したい挙動 | 入力 seam | 観測 |
|---|---|---|
| メニュー開閉 | `Alt+F/E/V`、menu button `Interaction::Pressed`、`Escape` | `OpenMenu`、menu entity の `Display` / `Visibility` |
| モード切替 gating | Replay/Manual/Auto segment click | `TransportCommandSender` の受信有無、error/disabled 表示 |
| File Open | OS dialog をバイパスし selected path event/resource を注入 | sidecar/strategy load、Strategy Editor spawn、scenario metadata |
| レイアウト保存/復元 | window move/close/resize、Save/Load、time advance | temp sidecar JSON、復元後 `WindowRoot` / viewport |
| Strategy Editor 入力 | focused editor + key/text input | `StrategyFragment`、cache file、history、find panel state |
| Startup パネル検証 | field edit + Run button | error label、Run command 未送信/送信、sidecar writeback |
| 銘柄ピッカー | `+ Add`、search text、candidate click、remove | candidate rows、placeholder text、scenario instruments、readonly |
| Chart 操作 | wheel、Ctrl+wheel、drag、double click | `ChartViewState`、camera pan/zoom、autoscale |
| 注文 UI / modal | form input、submit、confirm、context menu、Escape | command channel、modal visibility、feedback resource |
| CLI/backend | process command、gRPC request、env guard | exit status、stdout、files、gRPC response |
| render smoke | `BACKCAST_E2E=1` fixed fixture | screenshot or structured UI dump baseline |

## kind:integration レシピ — File Open → パネル spawn を headless で駆動

`tests/e2e/flows/i5_file_open_spawns_editor_and_chart.rs` が手本。state `Harness` は使わず、bare `App` に**必要な system だけ**を載せる（`UiPlugin` / `LayoutPersistencePlugin` 全体を足すと save/shortcut 系が `ButtonInput`/`Time` 等を要求し resource whack-a-mole になる）。要点:

- **seam**: temp `.json`（`windows:[{kind:"StrategyEditor", region_key:"region_001", ...}]`・`strategy_path:null`）を書き、`LayoutLoadRequested{ path, mode: LayoutLoadMode::UserJsonOpen }` を `send_event`。`apply_layout_system` は **`strategy_path` 付きだと `.py` ロード待ちに defer** するが、`strategy_path:null` + `windows` だと同フレームで `PanelSpawnRequested` を直接送る（headless で素直なのはこちら）。
- **載せる system（すべて pub）**: `apply_layout_system` → `panel_spawn_dispatcher_system`（Strategy Editor spawn）→ `instrument_chart_sync_system`（Chart spawn）を `.chain()` で順序固定。`instrument_chart_sync_system` は `registry.is_changed()` で early-return するので `InstrumentRegistry` を **insert してから**初回 `update()` で spawn される。`scenario` JSON → `InstrumentRegistry` の parse は `scenario_parser` 単体テスト持ちなので registry は直接 insert してよい。
- **resource**: apply_layout=`WindowManager`/`PendingLayoutApply`/`PendingStrategyFragments`/`ScenarioReadTarget` + `Camera2d` entity（`get_single_mut`）。dispatcher=`CosmicFontSystem`/`RegionKeyAllocator`/`AppHistory`/`PendingStrategyFragments`/`StrategyBuffer`。chart sync=`InstrumentRegistry`/`InstrumentTradingDataMap`。event=`LayoutLoadRequested`/`PanelSpawnRequested`/`StrategyFileLoadRequested` を `add_event`。
- **観測**: `query_filtered::<(), (With<StrategyEditorId>, With<WindowRoot>)>()` 件数 ≥ 1、`query_filtered::<&ChartInstrument, With<WindowRoot>>()` に対象 `instrument_id`。
- **cosmic / 依存の罠は `bevy-engine` スキル参照**（`CosmicFontSystem(FontSystem::new())` の手構築、`cosmic-text` を dev-dep 追加、外部 tests/ は normal deps は引けるが transitive は引けない）。

## ハーネス API（`tests/e2e/support/mod.rs`）

- 構築: `let mut h = Harness::new();`（`backend_enabled: true` で明示構築済み。env 非依存）
- 注入: `h.send_status(update)` / `h.send_event(event)` / `h.push_state(ts)` — いずれも送信後 `tick()` まで実行
- フレーム送り: `h.tick()`（= `app.update()` を 1 回。同期実行・即 return）
- 起動窓を開く: `h.begin_startup(startup_id)` — `ReplayStartup`/`RunComplete` の相関ロジックは
  `visible==true` かつ id 一致でないと no-op になるため、起動進捗系テストの前に必須
- 観測: `run_state()` / `last_run()` / `portfolio()` / `timestamp_ms()` / `venue()` / `exec_mode()` /
  `tickers()` / `available()` / `last_prices()` / `startup_progress()` / `backend_connected()` / `backend_running()` /
  `live_orders()` / `order_feedback()` / `secret_prompt()`

## 落とし穴（事前に知らないと必ずハマる）

- **Bevy system 順序 race（`.after()` 制約が無い）の RED→GREEN は schedule introspection で
  ヘッドレスに固定できる**（「`.before()` で強制逆順 RED」は不可: fix 後もテスト側が逆順を張り続け
  GREEN にならないため）。症状: `is_changed()` ガード付き（または毎フレーム read の）visibility system が
  `status_update_system` の後に `.after()` 無しで登録されていると、同フレームで前に走ったとき古い mode で
  判断し 1 フレーム遅延する。これには **2 種類のテストを併用** する:
  - **contract test（system logic の回帰ガード。M16/M18/M19。`kind:state`）**: テスト harness 側で
    `visibility_system.after(status_update_system)` を明示し、`h.send_status(ExecutionModeChanged)` で
    同一 tick に mode+visibility が変わることを assert。常に GREEN（順序をテスト側で確保）。これは
    「順序が正しければ logic が正しい」ことだけを保証し、**production の `.after` 付け忘れは検出できない**
    （テストが自前で `.after` を張るため、production の mod.rs 配線そのものは観測しない）。
  - **wiring guard（production 配線そのものの RED→GREEN。M20。`kind:wiring`）**: production の registration を
    `pub fn add_xxx_systems(app: &mut App)` に切り出し、テストは **それ＋`status_update_system` を bare
    `App` に登録** → `app.world_mut().resource_mut::<Schedules>().remove(Update)` で Update を取り出し
    `schedule.initialize(app.world_mut())` で build（system は実行しない＝resource セットアップ不要。
    `initialize` は param のアクセス登録だけで、resource 存在は run 時にしか要求されない）→
    `ScheduleGraph::conflicting_systems()` を読む。`status_update_system` (ResMut) と visibility system (Res) は
    同一 resource アクセスなので、`.after` が無いと衝突対に現れる。**3 段 assert**: (0) 対象 system が全て
    登録されている（登録漏れ検出。`if let Some` 直前に存在チェック）、(1) `status_update_system` が
    visibility system と衝突対に**現れない**（`.after` 欠落＝順序無しを検出）、(2) `Schedule::systems()`
    （build 後の executable は topsort＝実行順）で `status_update_system` が各 visibility system より**前**に
    居る（`.before` 逆付けを検出。(1) だけだと逆向きも「順序付け済み」で素通りする）。
    **これは fix 前 RED → fix 後 GREEN がヘッドレスで決定的に取れる**（`conflicting_systems()` は schedule
    build で ambiguity 設定に関係なく常に算出され、scheduler の実行時発火順序の非決定性に依存しない）。
    罠: `ScheduleGraph::systems()` は build 後 inner が executable へ移動して空になるので名前は
    `Schedule::systems()` から引く / `Schedule::initialize` は内部で `Schedules` resource に触るので
    `resource_scope` 内では呼べない（Update だけ remove して world に対し初期化する）。
  実例: `tests/e2e/flows/m20_mode_visibility_systems_run_after_status_update.rs`（issue #41）。
  fix 後は FLOWS.md の「⚠️ production コードに `.after()` を追加することで fix」注釈を「fix 適用済み」に更新する。
- **ハーネスは wire した system が要求する resource を全部 insert していないと全テストが即死する**。
  `Harness::with_backend_enabled`（`mod.rs`）は `status_update_system` / `backend_update_system` /
  `backend_event_drain_system` / `replay_startup_timeout_system` が取る `ResMut<T>` を**手で全部
  `insert_resource(T::default())` している**。これらの system に新しい resource param が増えたら
  （例: Phase 9 hardening が `status_update_system` に `ResMut<ReconcilePrompt>` を追加）、
  **ハーネスにも同じ 1 行を足さないと**全 e2e_replay が
  `... could not access system parameter ResMut<'_, T>` で 0.05 秒のうちに 35 本まとめてパニックする
  （bevy_ecs の system-param validation）。パニックは `bevy_ecs/.../function_system.rs` を指すので
  ハーネス漏れだと気づきにくい。**正解セットは `src/main.rs` の `insert_resource(...)` 群**＝ハーネスは
  これをミラーする。**ブランチをマージした直後は特に要注意**（片方が system に resource を足し、
  もう片方がハーネスを持つと、テキスト無衝突でも合体時に必ずこれで壊れる）。
- **別ブランチ由来の 2 つの system が同じ ECS state を書く場合、両方を *同居* させた integration テストが要る**。
  各 system を単体で検証するユニットテストは、マージで初めて生まれる**相互作用バグ**を取りこぼす。実例（#161）:
  `retile_hakoniwa_system`(#151) は chart 無しで（P17/P18）、`hakoniwa_chart_tile_sync_system`(#150) は retile 無しで
  （P25/P26）テストされ、両者を別ブランチで開発・マージした結果、retile の集合比較が `PanelKind::Chart` を mode 変更と
  誤判定して chart tile を毎フレーム巻き戻すバグが**どちらのテストにも映らなかった**。同じ ECS リソース/コンポーネントを
  複数 system が読み書きする箇所をマージしたら、`App::new()` に**両 system を `.chain()` で登録**し、片方が生成した
  state がもう片方を通しても生存することを assert する flow を 1 本足す（#161 の P27 = lib unit `hakoniwa_retile_preserves_chart_tiles_in_replay`）。
- **フェーズマージの健全性確認は「そもそも Rust を触っているか」から始める**。上記のハーネス／テスト
  ダブル破壊は全て Rust 側 (`src/**`/`.proto`/`tests/*.rs`) が変わって初めて起きる。マージ後
  `git diff --cached --name-only HEAD | grep -E '\.rs$|\.proto$|Cargo'` が**空なら**ハーネスは壊れようが
  なく、検証面は Python+docs だけ（`uv run python -m pytest -m "not slow and not kabu_live"`）。空でなければ
  上のチェックを実施し、最後に `cargo test --test e2e_replay`（全 flow green が正＝現状 87 passed /
  2 ignored）で締める。例: Phase 10 Step 8（`7125ff35`）のマージは Python+docs のみ＝Rust e2e は不変、
  Phase 10 Step 9 は `backend_event_drain_system` に `SafetyToast`/`StrategyLogs` を足したので
  ハーネスに 2 行 insert が要った。
- **`git diff --stat main <branch>` の「削除」表示に騙されない（diff 方向の罠）**。これは対称差なので
  「main にあって branch に無い」ファイル（例: e2e_replay.rs / docs/wiki/*）が大量に `---` 削除と出るが、
  マージはそれらを**消さない**（merge-base にも branch にも無ければ touch されない）。マージが実際に何を
  するかは `git show --stat <commit>`（そのコミット自身の diff）で見、マージ後は
  `git diff --cached --diff-filter=D HEAD`（空＝削除なし）で確認する。
- **マージ後に e2e が 1〜数本だけ落ちたら、まず単体で再実行して flake と回帰を切り分ける**。
  `cargo test --test e2e_replay <name>` が単体で green なら、それはマージが**起こした**回帰ではなく、
  プロセスグローバル状態（`std::env::set_var` の env var 等）を複数テストが並列で奪い合う既存の隔離バグを、
  マージのスレッドタイミング変化が**露出させた**だけ。`#[serial]`（`serial_test`、前例 `l3_prod_guard`）で
  該当テストを直列化する。例: Phase 10 Step 9 マージで `BACKCAST_CACHE_DIR` を使う i12/i5/i7/j16/j1 が
  並列衝突し i12 が散発失敗 → 5 本に `#[serial]` 付与で解消。
- **`BackendEvent` / `BackendStatusUpdate` の variant にフィールドが増えると e2e の構築が壊れる**。
  Phase ブランチをマージすると `OrderEvent` / `OrderSeeded` 等にフィールドが足される（例: Phase 10 で
  `OrderEvent.strategy_id` と `BackendStatusUpdate::OrderSeeded.strategy_id`）。テストが
  `tests/e2e/flows/*.rs` に分割された後はドリフトが多数の flow に散るので、`cargo test --test e2e_replay
  --no-run` で `missing field`(E0063) を列挙し、`grep -rn 'OrderSeeded\|BackendEvent::OrderEvent'
  tests/e2e/flows/` で構築箇所を特定して新フィールドを足す（未使用値は `String::new()` 等の中立値で OK＝
  assert 対象でなければ挙動は変わらない）。`OrderSeeded {` / `OrderEvent {` 行でアンカーして該当箇所だけ
  直す（一括置換は誤爆する）。なお e2e ブランチが Phase より前に分岐していると、gutted な
  `tests/e2e_replay.rs`（flow を `#[path]` で束ねるだけのファイル）は `git checkout --theirs` で branch 版を
  採り、登録 flow が main の test 関数の superset であることを確認してから採用する（coverage を落とさない）。
  なお backend 側の gRPC 拡張（新 RPC）は `tests/backend_integration.rs` のモック `impl DataEngine` が
  trait 未実装(E0046)になる別件 — マージ後チェックリストは memory `phase-merge-breaks-test-doubles` 参照。
- **event seam は一部だけ resource を変える（Phase 9 マージ以降）**。`backend_event_drain_system` は
  `OrderEvent` → `LiveOrders.apply_event`、`AccountEvent` → `apply_account_event`（`PortfolioState`）、
  `SecretRequired` → `SecretPrompt.active`、`VenueLogoutDetected` → `ReloginPrompt.active` を反映する
  （= F3/F4/F5/D5 は実装済み・観測可能）。**重要: この欄も含め要約はドリフトしうるので、
  着手時は必ず `src/backend_sync.rs` の `backend_event_drain_system` / `apply_status_update` を実際に
  読んで「どの seam がどの resource を変えるか」を現物確認する**こと。
- **注文 RPC（status seam, Phase 9）**: `OrderSeeded`/`OrderStatusUpdated`/`OrderModified`/`OrderRejected`
  は `apply_status_update` が `LiveOrders`（`upsert_full`/`apply_event`/`apply_modify`）と `OrderFeedback`
  を更新する（FLOWS.md の H セクション=H1〜H5）。`ExecutionModeChanged` は実モード変更時に
  `PortfolioState` を default リセット（Live/Replay 口座データ混線防止）する点が回帰の肝。観測には
  ハーネスの `live_orders()` / `order_feedback()` / `secret_prompt()` アクセサを使う。
- **`TransportCommand` 側（UI → gRPC）は state harness だけでは駆動しない**。`SetSpeed`/`SelectedSymbol`
  のような「UI が backend に投げるコマンド」は backend→ECS seam の手前。これを対象外にせず、
  `kind:ui` で command channel への送信/未送信を assert する。backend ack の variant が無い挙動
  （例: speed ack）は state flow だけで完結させず、UI command 発行テストと transport/integration テストに分ける。
  完全な単一プロセスループ（コマンド注入→mock gRPC→resource 観測）は transport task
  （`main.rs setup_backend_connection`）の lib 抽出 = 別タスク「Phase A-full」。
- **反対側 seam（`TransportCommand`→gRPC→`BackendStatusUpdate`）は `tests/backend_integration.rs` が
  mock tonic サーバで既にカバーしている部分がある**。重複を避けつつ、ユーザー操作として未保証なら
  `kind:ui` または `kind:integration` の flow を追加する。
- **`BackendTradingState` は `Default` を持たない**。clock 以外で必要なら `h.push_state` と同様に
  `serde_json::from_value(json!({"price":0.0,"history":[],"timestamp":0.0, ...}))` で最小構築する
  （必須は `price`/`history`/`timestamp` のみ、他は `#[serde(default)]`）。
- **文字列フィールドは wire フォーマットのまま**。`PortfolioOrder.side`/`.status` は `String`
  （`"BUY"`/`"FILLED"`）。enum 化されていないので文字列リテラルで正しい。
- **bare `App`（UI flow）には input プラグインが無く `ButtonInput<KeyCode>` はフレーム境界で自動 clear されない**。
  `just_pressed` が前フレームから sticky に残り、既に pressed のキーへの再 `press()` は no-op（just_pressed を作らない）。
  各キー操作の前に `keys.reset_all()` してから `press(...)` し直すこと。Escape の連続押下・トグル系（メニュー Alt+F、
  注文/モーダルの Escape 優先テスト）で「2 回目が効かない」のは大抵これ。同様に **`Time::<()>` の delta は最後の
  `advance_by` 値で固定**（`update()` で 0 に戻らない）。cooldown 等を「時間未経過」で検証したいときは
  `advance_by(Duration::ZERO)` で delta=0 にし、cooldown 解除には `advance_by(1s)` する。
- **`tests/e2e/support/mod.rs` の `Harness` は UI 駆動メソッド（`run_via_ui`/`click<M>`/`drain_commands`/`set_replay_state`）
  を持つ拡張版で、A/B/C 群の flow がこれらに依存する**。failing な 1 本を直すために `git checkout HEAD -- mod.rs`
  したり mod.rs / 他 flow を安易に上書きしないこと（未コミットの拡張を消すと 10+ 本が `no method named run_via_ui`
  で全落ちし、working-tree のみの内容は復元不能になりうる）。共有ファイル（mod.rs / runner / 別 flow）を触る前に
  「その working-tree 版に依存する未コミット flow が無いか」を必ず確認する。詳細は memory `e2e-harness-extended-ui-driven`。
- **`click<M>(marker)` の仕組み = `(marker, Button, Interaction::Pressed)` を spawn して `tick()` 1 回**。新規追加した
  `Interaction` は `Changed<Interaction>` に必ずヒットするので、本番ハンドラ（`*_button_system`）が**ちょうど 1 回**発火する
  （実マウス押下と同じ経路）。毎回新 entity なので同じボタンを連続クリックしても再発火する。producer→consumer
  （例 `footer_pause_resume_system`→`handle_strategy_run_system`、remove→`unsubscribe_removed_instruments_system`）は
  harness 側で `.chain()` 済みなので 1 tick で「クリック→`StrategyRunRequested`→`RunStrategy` コマンド」まで通る。
  発射コマンドは `drain_commands()` で受ける。**「実 UI 操作で `TransportCommand` を assert → その後 backend 応答を
  seam から注入」**が A–H の基本パターン。
- **command-level テスト（resource 直 seed ＋ 合成 entity spawn）は実機 wiring を素通りする＝false-green の温床**。
  `Harness` で `set_xxx()` により resource を直接埋め、`click<M>` で `(marker, Button, Interaction)` を**手で spawn**して
  system を回すテストは、「branch logic が完璧入力で正しく動く」ことしか保証しない。**本番 plugin の system 登録漏れ・
  本番 `spawn_xxx` が作る実 entity のマーカー/可視性・`Node.display` gating・pre-flight guard の充足経路**は一切踏まない。
  実例（issue #40 フォローアップ）: footer ▶ の LiveAuto 起動を `N5`（command-level）が「`StartLiveAuto` の送出有無」だけ
  assert して green だったが、実機では ▶ が無反応だった。原因は pre-flight guard が `warn!`+`continue` の **silent block**
  （venue 未接続等）で、N5 はそれを「送らないのが正」として暗黙に許容していた（＝抜け漏れ）。
  **gap を疑ったら、`i5`/`N6`/`N7` の bare-App パターンで本番経路を踏むテストを足す**: `App::new()`＋`MinimalPlugins`＋
  `AssetPlugin`＋`init_asset::<Font>()` に **本番 `spawn_footer`（等の構築 system）を Startup で 1 回回し**、本番の
  visibility/handler system を `add_systems` して、`query_filtered::<Entity, With<RealMarker>>()` で引いた**実 entity**を
  `entity_mut(e).insert(Interaction::Pressed)` で押す。これで「登録・実 entity・可視性・guard」まで丸ごと検証できる
  （resource は `make_app` で本番 `main.rs` と同じ insert セットを揃える＝1 つ漏れると system-param panic）。
- **carry-over / no-bleed / 「○○前は出ない」系の assert は「存在しない不変条件」を突いた vacuous false-green になりやすい。
  必ず *delete-the-production-logic litmus* を通す**: 「その assert を緑にしている production ロジックを消したら、テストは
  *本当に* 落ちるか？」を 1 件ずつ自問する。落ちないなら vacuous。典型的な 3 つの罠（#108 の uj1–uj7 で codex/レビューが
  6 件検出）:
  ① **書かれていない値を「空だ」と assert**（例 uj6「Subscribed 前は価格が空」だが、production の `LastPricesUpdated` は
     venue gate 無しの無条件 `map = prices`＝そんな gate は存在しない。何も書いてないから空、というだけ）。
  ② **触れない system が状態を消さないことを assert**（例 uj7「bar push が Failed を消さない」だが、`backend_update_system` は
     そもそも `CurrentRun` を param に持たず触れない＝守るべきガードが無い）。
  ③ **seam を test 自身が注入して「carry-over した」と主張**（例 uj1 が相関なしの `PortfolioLoaded` を「同一 run」と主張、
     uj3 が `StrategyRunRequested.cache_path` を直注入して editor→cache 導管を素通り、uj4 が flush 直後の cache file を
     ディスク直読みで「再起動復元」と主張）。
  **正しい型**: 実 production 導管を駆動して観測する。例: editor 編集→Run は **実フッター Run**（`footer_pause_resume_system`
  が editor `StrategyFragment` を `merge_fragments`→`flush_strategy_cache` で `cache_path` へ書き出し RunStrategy に載せる）を
  click し、fragment を書き換えて再 Run→cache 内容が変わることを assert（uj3）。再起動復元は **session2 で本番 restore→parse→sync**
  を回して `InstrumentRegistry` 等の live state に値が入ることを assert（uj4。ディスク直読みでなく）。「相関」は startup_id 等の
  **不一致を入れたら no-op になる**ことを対で assert（uj1 の正/誤 startup_id、uj7 の error 貼付）。「no-bleed」は merge でなく
  **replace に依存する**ことを示す（uj6 の連続スナップショットで前銘柄が滞留しない）。
- **構造クラス回帰（同じ欠落が別 entity で再発するバグ）は「マーカー名指しの点ガード」を増やさず ECS relationship で面ガードにする**。
  同型バグが再発するたびに「特定マーカーの存在を数える」点ガードを増設すると、次の新 entity は全てすり抜ける
  （もぐら叩き）。実例: Bevy 0.18 `sprite_picking` の **`Pickable` 必須**取りこぼし（#52 floating window→m21、#93 startup field→j18、
  #100 OrderButton→k23 と 3 つの点ガードが増殖し、4 件目＝chart sprite `window.rs::spawn_chart_panel` を全部が見逃していた）。
  **解法: Bevy が observer 付与時に対象 entity へ自動付与する `ObservedBy`（`bevy::ecs::observer::ObservedBy`、prelude 外）を使い、
  「observer を貼った world-space Sprite は必ず `Pickable` を持つ」をマーカー非依存で 1 本にする**:
  `query_filtered::<Entity, (With<Sprite>, With<ObservedBy>, Without<Pickable>)>()` が空であることを assert（実例 m30）。
  本番 spawn 関数（chart/orders/order form/startup fields）を 1 App に同居させ、chart のように observer を **install system**
  （`Added<ChartViewState>` 駆動）で貼る経路は production の installer を `add_systems` して実際に貼らせる → `app.update()` を数回
  回すと `ObservedBy` が populate される。⚠️ **assertion はマーカー非依存だが fixture（spawn する関数リスト）は手動**なので、
  新規パネルは fixture に 1 行足す必要がある（doc に「自動カバーは *列挙済み spawn 関数内の新 Sprite* に限る」と明記して
  over-claim を避ける）。**併せて fix も altitude を上げる**: 透明 hit-target sprite の inline spawn は `Pickable` 同梱の共有
  ヘルパ（`src/ui/component/hit_target.rs::spawn_transparent_hit_sprite`）に集約し、契約を 1 箇所に寄せる（#100 issue 本文の
  「per-site もぐら叩きを断つ altitude 修正」案）。同じ構図は M16/M18/M19 の contract-test vs M20 の wiring-guard とも通底する。
- **バグを 1 件直したら「同じ穴を共有する sibling 経路」と「入力マトリクス全セル」を必ず掃く（点 fix で満足しない）**。
  単一の症状（report された 1 ケース）だけ RED→GREEN すると、同じ primitive を共有する別経路に同型バグが残る。
  実例（#25 review max round 2026-06）: `broker.modify()` が「ACCEPTED に埋め込まれた約定を `is_filled` gate で
  取りこぼす」バグを直した後、**同じ `is_filled` gate を使う sibling 入口 `apply_venue_update()` にも同型の取りこぼし**
  （ACCEPTED+fill、さらに REJECTED/DENIED+fill）が残っていた。ユーザーの「初歩的な不具合を見逃すな・カバレッジ見直せ」で
  発覚。教訓 2 点: ①**bug-class sweep**＝fix した behavior を表現する primitive（ここでは `is_filled` で会計を gate する設計）を
  `grep` し、それを呼ぶ**全経路**（modify / apply_venue_update / cancel …）に同じテストを当てる。②**full-matrix coverage**＝
  回帰ガードは report された 1 セルでなく `status`×`fill 状態(0/部分/全/超過/malformed)`×`数量方向(増/減/同)` の**直積**を列挙し、
  未カバーのセルにテストを足す（このバグは「ACCEPTED×部分約定」セルが未テストだったから生まれた）。max effort の
  `/code-review` を併用すると sibling 経路・境界セルを機械的に洗い出せる（このラウンドで HIGH 2件+Medium 複数を検出）。
  ⚠️ **sweep は 2〜3 経路で満足せず entry point を全列挙して尽くす**: modify/apply を「掃いた」後の次ラウンドで
  `cancel()` が 4 つ目の sibling だった（REJECTED を apply_venue_update の手前で early-return し embedded fill を捨てていた）。
  ⚠️ **同じ primitive でも取りこぼしパターンは複数**: 「embedded fill を捨てる」だけでなく「fill status の 0・負の累積数量を
  `cumulative<=0` で一律 duplicate 扱いして素通り（FILLED/-1 が OrderAccepted で通る）」も同じ `_fill_violation` の穴だった。
  1 つの primitive に対し「捨てる／重複扱い／over-fill／非正/非有限／status-event 不整合」を網羅的に想定する。
  ⚠️ **sweep は「同一クラス内の sibling 経路」だけでなく「変更した *共有 contract* の全 consumer」まで広げる（別 subsystem を含む）**:
  fix が venue adapter の返り値 (`OrderResult.status` 等) や seam の契約を変えるなら、その method を `grep -rn '\.<method>(' engine/` して
  **すべての呼び出し元**を列挙し、各 consumer が新しい値を正しく扱うか確認する。in-scope の 1 経路（例: kernel `LiveBroker`）だけ直して
  満足すると、同じ adapter を食う別 subsystem（`ManualOrderFacade` / `NautilusVenueExecClient` 等）が旧契約のまま残り **half-applied contract = landmine** になる。
  実例（#25 review・2026-06）: kabu `cancel_order` を `CANCELED→PENDING_CANCEL` に変えたが、その契約は LiveBroker 以外に `order_facade.py:320` と
  `nautilus_exec_client.py:241` も消費していた（どちらも cancel ACK を terminal 扱い）。grill 設計時に consumer を grep し切れず code-review で発覚 →
  **「mock で証明できる broker honoring だけ #25 に残し、実 adapter flip + 全 consumer 更新 + async 確定配線は実 venue slice (#23) へ一括」**にスコープ分割して解決。
  教訓: contract を変える fix は **grill / 設計段階で consumer を全 grep し**、テスト可能な範囲（mock で踏める層）と実 venue 配線が要る範囲を**先に分離**する。
- **pure-helper proxy（PyO3/runtime 経路を unit 化する純粋関数）は、本番コードが実際にその helper を呼んで
  いなければ false-green の test-only ガードになる**。実 backend を起動できない経路（PyO3 worker の startup
  status 列・instrument fetch 判断など）を「現状挙動を写した純粋関数 + desired を assert する RED」で
  ガードするのは有効だが、**fix で本番 worker が helper を経由するよう配線しないと、helper はテスト専用の
  独立した仕様記述になり、本番がインラインで重複実装したまま drift する**（helper のエラー文言・分岐が
  本番と乖離してもテストは通り続け、回帰が再発しても検知できない）。実例（issue #64 レビュー 2 周目）:
  `inproc_startup_status_sequence` を #2 fix で導入したが worker は status 送出をインラインのままにし、
  helper は test からしか呼ばれず DataEngine 失敗文言が `: {e}` 有無で乖離 → Medium 指摘。fix は worker の
  両分岐を `for u in helper(...) { send(u) }` で helper 経由にして「単一の真実」化（#7 の
  `inproc_startup_instrument_fetch` / #3 の `inproc_poll_state_outcome` は最初から worker が呼ぶ形にしたので OK）。
  **チェック: `grep -rn '<helper名>' src/` で「定義 + テスト」以外に本番呼出があるか必ず確認する**。
  M16/M18/M19 の contract-test（テスト側で順序を確保＝production 配線は観測しない）と M20 の wiring-guard
  （production 配線そのものを RED→GREEN）の対比と同じ構図。
- **「クリックしても何も起きない」系のバグは silent guard（`warn!`+`continue` だけで UI に何も出さない）を最優先で疑う**。
  挙動を「保証」するテストは「コマンドが出る/出ない」だけでなく **「ブロック時にユーザーへ理由が surfacing される」**まで
  assert する（例 N7: pre-flight 失敗時に `LastRunResult.state=RunState::Failed{error}` を書き Run Result パネルへ赤字表示）。
  silent block を「送らないのが正」とだけ固定すると、無言の無反応を仕様として温存してしまう。
- **`push_state(ts)` は `TradingSession.replay_state` を `None` に上書きする**（fixture に replay_state が無いため）。
  footer の Pause/Resume は `replay_state` で分岐するので、**`set_replay_state(Some("RUNNING"))` は必ず `push_state` の
  「後」に呼ぶ**。順序を逆にすると clock push が RUNNING を消し、Pause クリックが Run 扱いになって command assert が落ちる
  （A2 で踏んだ）。`unsubscribe_removed_instruments_system` は mode 切替 frame を skip するので、削除クリックの前に
  `set_instruments` + 安定 tick を 1 回挟んで Local の prev 集合を整えてから × ボタンを押す（F2）。
- **`is_changed()` は resource が insert されたフレームの最初の `tick()` で true になる**。`Harness::new()` は
  resource を全部 insert してから返すため、最初の `h.tick()` 前に resource を書き換えても `is_changed()` ガード付き
  system が**「変更あり」として発火してしまう**（insert tick > system 初回実行 tick のため）。実例: D11 テストで
  `h.tick()` 前に `exec_mode.mode = LiveManual` を設定 → `auto_replay_on_venue_disconnect_system` が初回 tick で
  `VenueStatusRes.is_changed() == true`（insert 起因）と判断し Disconnect 状態で LiveManual → Replay に誤切替 →
  assert が `LiveManual` を期待するところ `Replay` が返って fail。
  **パターン**: シナリオを「途中状態」から始めたいテストは、まず `h.tick()` を 1 回走らせて initial change を
  消費してから resource を書き換え、次の seam 注入を行う。
  ```rust
  // ✗ 間違い: tick 前に状態設定 → is_changed が初回フレームで誤発火
  h.app.world_mut().resource_mut::<ExecModeRes>().mode = LiveManual;
  h.tick();
  // ✓ 正解: 先に tick して initial change を吸収、その後シナリオを設定
  h.tick();                    // initial change を消費
  h.send_status(Connected);    // live 遷移
  h.app.world_mut().resource_mut::<ExecModeRes>().mode = LiveManual; // ← ここで設定
  h.send_status(Subscribed);   // live→live = 変化なし を assert
  ```
- **共有 runner（`tests/e2e_replay.rs`）の登録は orchestrator が一括で行う**。並行 subagent に書かせると重複登録・
  順序衝突・cargo の target ロック競合が起きる。subagent には「flow ファイルだけ書く / cargo も runner も触らない」と
  明示し、登録・コンパイル・修正は中央で回す。
- **headless 不可 / 未実装の flow は fake せず doc stub（`//!` のみ・`#[test]` 無し）にして runner 未登録のまま残す**。
  `kind:render`（ShapePainter+Text2d=GPU 必要）、Windows 専用 PowerShell、実ウィンドウ smoke、production 未実装機能が該当。
  外部データ依存（catalog / J-Quants）や OS dialog（rfd 直呼び）は `#[test] #[ignore]` + 理由 doc にする。
- **`MessageWriter` を使う system のテストは `app.update()` を 2 回呼ぶ**。`Messages<T>` はダブルバッファ方式で、system 内の `MessageWriter::write` は `messages_b` に書き込む。1 回目の `app.update()` では `message_update_system`（First schedule）が swap を行い書き込みが完了するが、その後すぐ `update_drain()` を呼んでも旧 `messages_a`（空）側をドレインしてしまう。2 回目の `app.update()` で再 swap されて初めて `update_drain()` が書き込まれた内容を読める。同パターンは既存テスト（`test_user_json_open_reloads_strategy_path_even_if_already_loaded` 等）でも `app.update()` 2 回呼びを使っている。`write_message` 経由の直接書き込みも同様。実例: I21 テスト（#69）で 1 回だけ呼んだところ spawn 数 0 が返り、2 回に変更して GREEN になった。
- **既存 warning は触らない**（`main.rs:33 UnsubscribeRequest` 等、本作業と無関係）。新規 warning は増やさない。
- **コメントは「なぜ」だけ**。何をしているかの説明やタスク言及は書かない（プロジェクト規約）。
- **モジュール/シンボルを撤去した後の現行化 grep は `docs/wiki/` だけでなく `docs/` ツリー全体を当たる**。
  ADR・refactor/plan ドキュメント（`docs/adr/*`, `docs/ui-theme.md`, `docs/ui-refactor-plan.md` 等）は
  「#N で実装予定」「#N で消える」のような**先送りリストに削除予定ファイル名を直書き**していることがあり、
  対象が実際に消えると dangling 参照・偽の将来宣言になる。`grep -rn "<削除したファイル名/シンボル>" docs/`
  を必ず回して 0 件（または「撤去済み」へ現行化）を確認する。実例: #50 Slice 7 で `strategy_editor_spike.rs`
  を削除したが `docs/ui-theme.md` の #48 先送りリストが同ファイルを参照したまま残り、codex セカンドオピニオンが
  Medium 指摘（`src/ tests/ docs/wiki/` だけ grep して `docs/ui-theme.md` を取りこぼした）。

## テストの型（コピーして埋める）

```rust
/// <FlowID> <name>: <1 行で不変条件>。
#[test]
fn <flow_id>_<snake_name>() {
    let mut h = Harness::new();
    // 1. 前提を整える（必要なら h.begin_startup(id) など）
    // 2. seam を注入
    h.send_status(BackendStatusUpdate::RunStarted);
    assert_eq!(h.run_state(), RunState::Running);
    // 3. 続きの seam を注入し、最終状態を assert
    h.send_status(BackendStatusUpdate::RunComplete {
        startup_id: None,
        run_id: "run-x".to_string(),
        summary_json: r#"{"status":"ok"}"#.to_string(),
    });
    assert_eq!(h.run_state(), RunState::Completed);
}
```

import は `tests/e2e_replay.rs` 冒頭の `use backcast::trading::{...}` / `use backcast::replay::{...}` に
必要な型（`BackendStatusUpdate` の variant が使う enum 等）を足す。

## wiki への引用元記載（必須）

テストを追加・更新したら、対応する `docs/wiki/` のページに `[FlowID]` を書く。

1. flow が説明する挙動が wiki のどのページに書かれているかを確認する。
2. そのページの冒頭に引用元注記がなければ追加する（既存ページの書き方に倣う）:
   ```
   > 文中の `[A1]` などは、その挙動を保証する E2E flow の ID。一覧は [`tests/e2e/FLOWS.md`](../../tests/e2e/FLOWS.md) を参照。
   ```
3. 挙動の記述箇所（手順・表・説明文）に `[FlowID]` をインラインで付ける（例: `Run を開始 [A1]`）。
4. 対応する wiki ページが存在しない場合は記載不要（FLOWS.md の flow 行にその旨をコメントする）。

現時点で引用済みのページ: `docs/wiki/**` の全ページ（2026-06 に `[FlowID]` をリンク化済み）。

### `[FlowID]` のリンク化と健全性監査（wiki 全体メンテ時）

ユーザーが「`docs/wiki` の `[L1]` などを漏れなく link に変えて」のように **wiki の FlowID 参照を一括リンク化**したいと言ったとき（このスキルはその担当）は、機械的置換の前に必ず flows ディレクトリの健全性を監査する。今回（2026-06）これで 3 種の隠れた不整合が露見した:

- **リンク形式**: `[L1]` → `[[L1]](../../tests/e2e/flows/<id>_<name>.rs)`。冒頭注記のバッククォート例（`` `[L1]` ``）は置換しない（negative lookbehind ``(?<!`)`` で除外）。
- **flows/*.rs が無い ID** は FLOWS.md フォールバックで濁さず**実テストへ直リンク**する: `kind:ui`/`kind:unit` は `src/ui/*.rs` / `src/camera.rs` 等の `#[cfg(test)] mod tests`、`kind:python` は `python/tests/**`、`kind:state` の table 形式は近い実ファイル。どうしても特定不能な planned/未実装 ID だけ `tests/e2e/FLOWS.md` に張る。
- **ID 衝突の監査**: `ls tests/e2e/flows | grep -oE '^[a-z]+[0-9]+' | sort | uniq -d` で**同一 ID 接頭辞の 2 ファイル**を検出する。両方が生きて `e2e_replay.rs` に登録され FLOWS.md にも 2 エントリあるなら**衝突**＝片方を空き番号へ採番し直す（ファイル名・doc コメント・fn 名・`e2e_replay.rs` の `#[path]`/`mod`・FLOWS.md・wiki 参照を一括更新）。どちらが ID を保持するかは **他 flow / wiki の `[ID]` 参照がどちらの挙動を指すか**で決める。
- **孤児ファイルの監査**: flows/*.rs があるのに `e2e_replay.rs` に未登録なら**デッドコード孤児**（後継ファイルにリネーム済みの取り残し等）。`grep '<basename>' tests/e2e_replay.rs` が空なら `git rm` する。
- **runner 登録ドリフトの監査（双方向・FAIL）**: ① real `#[test]`（`^\s*#[test]` 行）を持つ flow が `e2e_replay.rs` に**未登録**＝回帰ガードが一度も走らない（2026-06 に `j17` がこれで、#62 ガードが死んでいた実例）。② 逆に `//!` だけの **doc stub なのに登録**されている flow は runner 方針に違反（同 `m17_issue41_realapp_smoke` が manual-gate stub なのに登録されていた実例）。両方向を `scripts/check_e2e_flow_consistency.py` の FAIL tier で検出する。`#[test]` カウントは doc コメント内の例（`//! ... #[test]`）を除外すること（手動 grep で 152 と誤計上 → 実 149 だった）。
- **FLOWS.md の陳腐化パス**: 表セルや本文が指す `python/tests/...` / `src/...` のパスは moved/renamed していることがある。リンク前に **実在を検証**（`os.path.exists` 一括チェック）し、移動先を `grep -rln '<test fn 名>'` で突き止めてから張る。
- **未実装挙動を見つけたら**: wiki が実在しない UI（例: footer の `<` StepBack ボタン。`src/ui/footer.rs` に「No StepBack button」コメントあり）を記述していたら、no-op を不変条件化するか実装 issue を起こすかをユーザーに確認する。実装方針なら issue を起票し、FLOWS.md に planned `- [ ]` 行（issue 番号付き）を足して wiki の `[ID]` をそこへ張る。

## 完了基準

- 追加・変更した flow の `kind` に対応する release gate が green。
  `kind:state` は `cargo test --test e2e_replay`、`kind:ui` / `kind:integration` / `kind:render` は
  flow に記載した command または harness、`kind:manual-gate` は手順・期待結果・実施条件が明記されている。
- **例外: 既知バグの回帰ガードは「RED で確定」が完了**。ユーザーが「このバグをテストにして」と
  挙動の **崩れ** を語り、fix を別 issue に委ねるとき（`/to-issues` 併発が典型）、テストは
  わざと **RED**（バグを再現して fail）させて登録する。このとき完了基準は ①RED が**正しい理由で
  落ちる**こと（wiring/compile エラーではなく assert で fail。`cargo test --test e2e_replay <id>` の
  panic メッセージで確認）②他 flow を巻き込まない（`N passed; 1 failed`）③FLOWS.md に「RED＝回帰ガード・
  fix は #issue 後に green」と明記し ✅ にしない、の 3 点。fix 実装時に green へ反転し FLOWS.md を ✅ に更新する。
- 観測が「ユーザーが語った挙動」と対応している。resource 遷移、UI entity、command channel、file output、
  screenshot/structured dump のいずれかが、その挙動の十分条件になっている。
- A8（stale startup_id の相関）/ D7（Live universe が Replay fallback を上書き・prune しない不変条件）の
  ような**回帰の肝**を新規テストで壊していない。
- FLOWS.md のチェックボックスと「実装状況」を更新済み。
- 対応する wiki ページに `[FlowID]` の引用元を記載済み。
