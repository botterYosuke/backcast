# backcast

`The-Trader-Was-Replaced`（Bevy + 埋め込み Python の取引アプリ）の**後継・本線フロントエンド**。
Unity(C#) のゲーム内に同じ空間（Infinite canvas / Hakoniwa / Floating window）を再構築し、
取引 engine（Python/Nautilus）を pythonnet で**同一プロセスに埋め込む**。方針は ADR-0001。

## Language

**backcast**:
本線（going-forward）の Unity フロントエンド。取引 engine を所有し、埋め込み Python で
Replay / Live / Auto を動かす。
_Avoid_: Unity 版（曖昧）、新フロント

**The-Trader-Was-Replaced（TTWR）**:
backcast の前身となる Bevy(Rust) アプリ。カットオーバー（#5）までは凍結された fallback として
本番に温存し、その後**廃止**する。going-forward の開発は行わない。
_Avoid_: Bevy 版を「現行/本番」と呼ぶこと（fallback かつ廃止予定であり本線ではない）

**engine**:
host 非依存の Python 取引エンジン（Nautilus ベース、`python/engine`）。TTWR から backcast へ
**移植**して backcast が所有する。host（Bevy/Unity）とは **sink 注入点・2 入口モジュール
（`engine.core` / `engine.inproc_server`）・dict 境界**でのみ接し、host 型を import しない。
_Avoid_: backend、Python バックエンド（engine が正）

**Backcast Execution Kernel（kernel）**:
backcast 専用の**最小 pure-Python 取引エンジン**。NautilusTrader（Rust core `nautilus_pyo3`）を
**置換**し、in-proc を保ったまま Windows-Mono の多重 CRT/FLS teardown crash を構造的に消すための実体
（方針: ADR-0004 案 C）。最小コンポーネント = `EventLoop`/`Strategy`/`OrderEngine`/`Portfolio`/
`RiskEngine`/`ReplayBroker`/`LiveBroker`/`EventSink`。Replay と Live で同一 strategy API。
**Nautilus 互換 framework 全体ではない**（多資産・HFT・汎用 message bus 等は非目標）。tracer は #24。
_Avoid_: 「Nautilus の再実装」「汎用取引フレームワーク」と呼ぶこと（backcast 専用・最小スコープが正）／
nautilus の `DataEngine`/`ExecutionEngine`/`RiskEngine` と同一視すること（同名でも別物・kernel は Rust core を import しない）

**KernelLiveEngineController**:
kernel を Live/Auto 経路へ繋ぐ `LiveEngineController` Protocol（`attach`/`detach`/`cancel_inflight_orders`）の
**pure-Python 実体**。本番既定の `NautilusLiveEngineController`（`NautilusKernel` を起こし Rust core をロードする）を
置換し、Live でも Rust core 非ロードを保つ（方針: ADR-0004 案 C・記録: findings 0010・#25）。`NautilusLiveEngineController`
と**同一の ctor seam**（loop/adapter/runner provider・on_order_event/on_telemetry/on_strategy_log/on_safety_violation・
run_gate_provider）を満たし、swap は `live_orchestrator` の生成箇所の class 名だけ。Live UI 配送は既存 backend_events
seam のまま（`EventSink.push_*` を Live 配送路にしない）。
_Avoid_: `NautilusLiveEngineController` と機能等価と見なすこと（後者は Rust core を引く・前者は引かない）／
`EventSink` を Live UI のチャネルにすること（AC④ は projection 互換ゲートであって配送路変更ではない）

**LiveBroker（kernel）**:
kernel `OrderEngine` ↔ 実 venue `OrderingVenueAdapter` の約定 bridge。Replay の `ReplayBroker`（bar close で決定的約定）
に対応する Live 実体で、`adapter.submit_order/cancel_order/modify_order` を叩き、同期 `OrderResult` と（将来の）非同期
EC イベントを**同一入口 `apply_venue_update` に正規化**して order FSM（SUBMITTED 以降）を駆動する。fill 重複排除は
**累積約定数量 delta**（受信イベント数ではない）。mock venue tracer の authoritative fill source は同期 `OrderResult`
（非同期 reconciliation は #23）。記録: findings 0010。
_Avoid_: `ReplayBroker` と同一視すること（fill source・タイミングが別）／受信イベント数で dedup すること（累積数量が正）

**確定バー / partial バー（`KlineUpdate.is_closed`）**:
`LiveRunner` は bucket-rollover で生成した**確定バー**（`is_closed=True`）と、UI 用に 1 秒間隔で publish する
進行中の**partial バー**（`is_closed=False`）を同じ `KlineUpdate` 型で bus に流す。kernel live driver は
**確定バーだけ** `on_bar` に渡す（partial を渡すと毎秒重複発注する）。UI 側 `LiveReducerBridge` は partial を含む
従来挙動を維持。記録: findings 0010・#25。
_Avoid_: partial バーを strategy の `on_bar` に渡すこと／`is_closed` 無しで bus の `KlineUpdate` を strategy に流すこと

**golden 契約（Backcast vs Nautilus oracle）**:
kernel の正しさを担保するため、**NautilusTrader（standalone CPython）を比較 oracle として温存**し、その実出力を
golden として固定する規律。golden は sink の生 JSON 文字列ではなく **parse・正規化した契約**（order 状態列 /
fill 数・価格 / position 数量 / realized PnL / 最終 cash・equity / **sink イベント順序**）＋ provenance
（nautilus version・`PRECISION_BYTES`・strategy/catalog/scenario の hash）。golden は**計算で組み立てず必ず
oracle 経路から記録**する（自己参照を避ける）。oracle subprocess と kernel subprocess を別プロセスで走らせ、
`capture`（明示生成）／`verify`（read-only・差分で失敗）を分ける。方針: ADR-0004 案 C・記録: findings 0008。
_Avoid_: golden を kernel と同じ仮定から計算すること（oracle ではなく期待値の自己照合になる）／
生 JSON のバイト一致を parity 条件にすること（正規化値＋イベント順が正）

**adapter（C# adapter 層）**:
Unity(C#) 側で pythonnet を介し engine を駆動する単一の境界。engine の sink 口に C# 製 sink を
差し、結果を GIL なしで読める C#/native バッファへ渡す。engine を host 非依存に保つための seam。
_Avoid_: bridge、wrapper

**移植（port）**:
engine のソースを TTWR から backcast へ移し、backcast を唯一の home にすること。
submodule 参照でも pinned-package-from-TTWR でもない（TTWR は廃止されるため）。
_Avoid_: 共有、依存（TTWR を生かしたまま参照する含意を避ける）

**seam ゲート（S0 / S2-spike）**:
threading の継ぎ目を段ごとに検証する throwaway spike。**S0**（#2）= threaded **backtest**
（有界・1 回 run）、**S2-spike**（#7）= live **asyncio loop**（長時間・tokio・venue WS・polling）。
前段の green は後段の保証にならない、を前提に分けて立てる。S2-spike の**核の未知数**は
S0 が触れていない **cross-thread asyncio marshal**：host worker が live loop へ
`run_coroutine_threadsafe(coro, loop).result(timeout)` 越しに仕事を投げ、`.result()` 内部の
GIL 解放→loop スレッドが GIL 取得→coro 実行→worker 再取得、という **Mono 上の GIL 往復**が
健全か。**green 判定は「ハングしない」ではなく「prompt completion」**（elapsed ≈ coro の固有コスト）：
GIL starve でも `.result(timeout)` は無限ハングせず `TimeoutError` を投げるため、毎コール timeout
する壊れた系も「ハングしない」を満たしてしまう。
_Avoid_: spike をまとめて 1 つにすること／「no-hang＝green」と判定すること（prompt completion が正）／
人間向け表記で裸の `S2`（`S2-spike` が正・`Step 2`=#4 と衝突）

**S/Step/slice の呼称規律（命名衝突の回避）**:
接頭辞 `S<n>` の素トークンは **spike 専用に予約**（`S0`=#2 / `S2-spike`=#7）。移行の段は **`Step <n>`**（Step 1=#3 / Step 2=#4 / Step 3=#5）。**Step 1 の子スライスは番号で呼ばず記述名**を使う:
**Replay tracer**（#9・seam tracer・close 済）/ **Replay chart**（#10）/ **Replay panels**（#11）/ **Replay layout**（#12）。
**Step 2（#4）の子スライスも同様に記述名**を使う（依存順）:
**Windows live prerequisites**（S0 Win + S2-spike Win + S2-spike playmode を実測し #7/#2 の残ゲートを閉じる。S0 Windows PASS で ADR-0001 を `accepted` へ昇格）/
**Venue contract verification**（kabu/tachibana の不変条件を実行可能な characterization test + Windows pytest で固定）/
**Live adapter tracer**（C# lifecycle owner・engine marshal・live event sink・Unity panel drain。mock venue で AFK GREEN 先行）/
**Venue login and secret flow**（kabu Verify / tachibana demo・`SecretRequired→submit_secret→SecondSecretResolver`・平文を env/log に残さない）/
**Live safety and graceful shutdown**（rails/gates/watchdog・orphan 不在・`graceful-stop→cancel resting→loop teardown→Python finalize`。demo 発注より前に必須）/
**Live demo roundtrip**（実 venue demo で発注→約定→建玉表示＋正常終了時の残注文取消を owner が確認する最終統合ゲート）。
code / docs / findings / ログの識別子も `replay_chart_*` / `ReplayChart…` / `[REPLAY CHART PASS]`、`live_adapter_tracer_*` / `[LIVE DEMO ROUNDTRIP PASS]` のように記述名で書く（`S2` 等の数字採番は使わない）。
これは `S2-spike(#7)` ≠ `Step 2(#4)` ≠ slice の "2" の三重衝突、および `S1`（slice-1=#9）との再衝突を構造的に消すため。
_Avoid_: Step 1 子スライスを `S1`/`S2`/`S3`/`S4` と数字で呼ぶこと（"S" が Spike/Slice/Step に三重過負荷するため）

**sink（push sink）**:
engine が host へデータを**押し出す**口。Replay では adapter が C# 製 sink を engine の sink 口へ差し、
worker スレッド（GIL 保持）が per-bar で `push_bar` / `push_order` / `push_portfolio` /
`push_run_complete` / `push_run_failed` を呼ぶ。payload は **JSON 文字列**（既存 Bevy `RustBacktestSink`
契約と同一・zero-copy ではない）。C# sink メソッドは enqueue して即 return し、main は GIL なしで drain する。
_Avoid_: bridge、callback（sink が正）／binary buffer と混同すること（zero-copy viz は #8 の別物）

**Replay parity**:
Bevy で出来ていた Replay 体験を Unity 単体で**挙動として**再現すること（status/run_result/positions/
orders/チャートが更新される）。#3 の done ゲートは挙動 parity であり、shippable standalone build は gating
条件ではない（Editor playmode で満たしてよい）。
_Avoid_: バイト/出力完全一致や shippable build を parity の条件に混ぜること

**レイアウト parity（capability parity）**:
レイアウト保存/復元の「Bevy 同等」は**能力等価**であって**形式互換ではない**。Unity は自前の versioned
スキーマで同じ UI 状態（floating window rect/z-order・Hakoniwa tile 順・canvas pan/zoom 等）を round-trip
できればよい。Bevy の sidecar 形式を読む reader は作らない（Bevy は #5 で廃止）。方針: ADR-0003。
_Avoid_: 「Bevy 同等」をバイト互換/同一スキーマと解釈すること

**layout binder**:
live な uGUI（RectTransform: anchor + pixel offset）と、永続化用の **正規化表示矩形**を持つ
`LayoutDocument`（Unity 自前 versioned スキーマ）との間を双方向変換する UI 側の層。`Capture`（live →
document）と `Apply`（document → live）の 2 口。スキーマを RectTransform 実装詳細に固定しないための seam。
実装型は `LayoutBinder`（#12）。
_Avoid_: **adapter と呼ぶこと**（adapter は engine/pythonnet 境界専用の予約語。layout binder は UI⟷document
変換で別物）／bridge、wrapper

**infinite canvas**:
chart / status tiles（Hakoniwa）/ floating window が乗る、無限スクロール・ズーム可能な **同じ空間** の土台
（CONTEXT 冒頭の「同じ空間」の具体物）。uGUI 実現は **固定 Viewport ＋ 単一 Content transform**：
**pan = Content の canvas 論理座標移動**、**zoom = Content scale（カーソル中心）**。canvas 上の widget は
Content の子なので pan/zoom に自動追従する。**screen-fixed chrome（menu / sidebar / footer / modal）は
Content の外**に置き追従しない（TTWR 構造と同型: chart+status は Hakoniwa 内＝world-space、chrome は画面固定）。
Bevy の同等機能の **capability parity**（ADR-0003・形式非互換）。土台の実装は #13、Hakoniwa 移設は #14、
floating window は #15（予定）。
_Avoid_: ScrollRect（有界コンテンツ前提で別物）／world-space＋camera（ScreenSpaceOverlay shell と別系統）／
pan を画面ピクセルで保持すること（canvas 論理座標が正）／#11 panels を恒久 HUD と定義すること（暫定であり
#14 で Hakoniwa として canvas へ載る）／TTWR の `OrthographicProjection.scale`（大きいほど zoom out）と
**zoom 値を数値互換にすること**（uGUI `localScale` は逆向き。Unity ネイティブの意味で持ち capability parity）

**canvas 論理座標（canvas logical coordinate）**:
infinite canvas の Content 座標系の座標。**画面ピクセルでも zoom 後ピクセルでもない**。pan の永続値はこの
論理座標で保持し、resolution / zoom / Viewport サイズに非依存にする。永続化される canvas view 状態
（pan の論理座標 + zoom 倍率）は #12 の `LayoutDocument` に **panel の `LayoutRect` とは独立した additive
フィールド**として載る（capability surface 追加・findings 0004 §10 の予約項目）。
_Avoid_: pan/zoom 状態を panel `LayoutRect`（正規化 0..1 表示矩形）に混ぜること（別次元・別フィールド）

**Hakoniwa（split-grid surface）**:
infinite canvas の Content 上に乗る単一の **split-grid サーフェス**。chart + status 系 tile（`chart` /
`status` / `positions` / `orders` / `run_result`）を **locked `ceil(√n)` グリッド**（n=5 → 3 列×2 行・最終
cell は空）に並べる。TTWR の Hakoniwa（`src/ui/hakoniwa.rs`・ADR 0011/0014）の **capability parity**（ADR-0003・
形式非互換）。Content の子なので pan/zoom に自動追従する（chrome は追従しない）。実装は #14。
_Avoid_: free-float／overlap（tile は grid slot を占めるだけ）／chart を Hakoniwa の外の常設 floating window と
定義すること（TTWR 現行も chart は Hakoniwa tile）

**tile / slot / tile swap**:
**tile** = Hakoniwa の 1 区画（安定 `id` で同定）。**slot** = tile が占める grid スロット番号（row-major・
左→右／上→下）＝ #12 `PanelLayout.slot`（**順序の正本**）。tile の実表示矩形（`LayoutRect`）は n+slot から
**等分グリッドで派生**する snapshot で、自由配置や split 比率の正本ではない。**tile swap** = ヘッダ drag で 2 tile の
slot を入れ替える操作（**swap であって自由配置ではない**・TTWR ADR 0014 parity）。divider resize（列幅/行高の
比率変更・ADR 0015 parity）と box 移動（root の canvas 位置永続化）は #14 **外**＝将来 slice の additive 拡張。
_Avoid_: slot を rect から導く／rect を split 比率や root 位置の正本に流用すること（slot が正本・rect は派生）

**floating window / FloatingWindowLayer / z-order**:
infinite canvas の Content 上を **自由配置（free placement）**で漂う window（Strategy Editor / Order 等）。Hakoniwa の
**tile swap とは別物**（tile は grid slot を占めるだけ・自由配置不可。floating window は canvas 論理座標で position+size を
自由に持つ）。**chart は floating window ではない**（Hakoniwa tile。TTWR で chart floating は廃止＝`dispatcher.rs` が
`PanelKind::Chart` spawn を拒否）。**FloatingWindowLayer** = Content 直下の単一コンテナで、全 floating window はその子。
HakoniwaRoot と sibling order（z-order）を混在させないための層（Content の子なので pan/zoom には追従する）。**z-order** =
window の前後関係。live は **FloatingWindowLayer 内の sibling index**（後の sibling ほど前面）、persist は **`zOrder` int**
（#12 `PanelLayout.slot` とは同一視しない＝findings 0004 §3 が「zOrder は別 field」と予約済み）。**click-to-front** =
window をクリック/drag したとき最前面へ（TTWR `WindowManager.max_z` bump の capability parity・形式非互換）。**move** =
title bar drag で position を移動（screen delta / zoom → canvas 論理 delta）。実装は #15。
_Avoid_: chart を floating window と呼ぶこと（Hakoniwa tile が正）／zOrder を `slot` に相乗りさせること（別 field）／
floating window rect を panel の 0..1 正規化 `LayoutRect` で持つこと（floating は canvas 論理座標の position+size）／
resize/常時最前面 pin を #15 の汎用 window system に含めること（前者は将来 slice・後者は実 editor content 由来の例外）

## Flagged ambiguities

- **「本番」**: backcast の文脈では将来の本線を指すが、移行期間中の **live 実弾**は当面 TTWR(Bevy) が
  担い得る。「本番フロント」=backcast（going-forward）、「現 live 実行系」=TTWR（fallback）と区別する。

- **「≥300fps」（seam ゲート AC の言い回し）**: S0（#2）・viz-spike（#8）の AC が言う「≥300fps」は
  **毎秒 300 フレーム（throughput）ではなく、worker が連続で backtest 実行 / ndarray upload する間に
  main が描画を止めずに ≥300 フレーム継続する**こと＝ main が GIL/upload に一度もブロックされない証明。
  VSync 環境で 300 FPS の throughput を要求するものではない。findings には `frames >= 300` と
  実測 `maxDt`／hitch を記録する。

- **「interpreter pin（CPython patch version）」**: production pin は **`3.13.11 win_amd64`**（deploy=Windows=
  TTWR `.venv` 実測）。docs に一時期 `3.13.13` とあったのは uv index に存在しない phantom pin の誤記で、
  **Mac S0 先行実験のみ** `3.13.13` で走った（patch-version drift・#8 grill 2026-06-13 訂正）。canonical は
  ADR-0001 decision 7。

## Example dialogue

> **Dev:** Live を Unity 側に出すのはいつ？
> **Owner:** S2-spike（#7）が green になってから。S0 は backtest の threading しか見てない。
> **Dev:** engine は TTWR から参照する？
> **Owner:** いや、**移植**。backcast が engine を所有する。TTWR は fallback で温存して、カットオーバーで**廃止**。
> **Dev:** じゃあ engine の host 結合は剥がす必要があるね。
> **Owner:** ほぼ剥がれてる。sink 注入点と `engine.core` / `engine.inproc_server` の 2 入口、dict 境界だけ。
>   そこに C# の **adapter** を差せば host 非依存のまま動く。
