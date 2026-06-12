---
status: proposed
---

# フロントエンドを Unity(C#) で作成し、Python エンジンを pythonnet で Unity プロセスに全埋め込みする（既定＝単純路線）

このリポジトリ（`botterYosuke/backcast`）は、`The-Trader-Was-Replaced`（Bevy + Python エンジンの
in-proc 取引アプリ）のフロントエンドを Unity に置き換える新フロントである。本 ADR はそのホスト構成の
基本決定を記録する。`grill-with-docs`（2026-06-12）で導出。

Related（いずれも `The-Trader-Was-Replaced` リポジトリ側 ADR）:
ADR-0019（In-proc only backend transport）, ADR-0026（Nautilus catalog precision invariant）

## Context

旧構成は Bevy(Rust) UI が PyO3 で Python エンジン（Nautilus）を同一プロセスに埋め込む。
`src/ui` ~4.3万行がこの UI 層。

要望は「**Unity のゲーム内に Rust GUI と同じ空間（Infinite canvas / Hakoniwa / Floating window）を
作り、Python をゲーム内から実行する**」こと。動機は、最新 Python 3.x と ML 系（PyTorch/TensorFlow）を
そのまま使い、かつ **C#↔Python のゼロコピー**でデータ授受を行うこと。

仕組みは単純：Unity 上で **Replay / Live / Auto** を開始すると Python が回り、チャートが更新され続ける。
この単純さを設計の既定とする。複雑化（プロセス分離・IPC・heartbeat 等）は**先回りで入れない**。

### 安全要件（owner 指定）

- 「**アプリが見かけ上死んでも、裏でプロセスだけが生きて実弾を出し続ける**」状態は不可。
- **Unity が死んだら執行機能も落ちる**設計でよい（一緒に死ぬのが正）。

この要件は「Python を Unity に**同一プロセス埋め込み**する」ことで**自動的に**満たされる（Unity が死ねば
同一プロセスの Python も即死。子プロセス・Job Object・heartbeat dead-man's-switch は不要）。

## Decision

1. フロントエンドを Bevy(Rust) から **Unity(C#)** に全面移植する。Infinite canvas / Hakoniwa /
   Floating window の「同じ空間」を C# で再実装する。`src/ui` は破棄。
   **これが本 ADR で唯一の非可逆コミットメント**。
2. **Python を pythonnet で Unity プロセスに全埋め込みする（既定・単純路線）**。Nautilus エンジン・
   チャート計算・将来の ML 可視化計算をすべて Unity プロセス内 Python で動かす。Unity 上で
   Replay/Live/Auto を開始すると Python が**バックグラウンドスレッド**で実行され、チャートが更新される。
   UniPy（pythonnet の薄いラッパ・17 commits・未保守）は使わず **pythonnet 直接**。
3. **ライフタイムは同一プロセスで自動**：Unity が死ねば Python も即死。orphan プロセスは構造的に存在
   し得ない。プロセス分離・IPC・heartbeat は導入しない。
4. **GIL/スレッド**：重い Python 計算は C# のサブスレッドで実行し、Unity main thread をブロックしない。
   結果メモリのみ main thread に渡して uGUI / GPU 描画へ流す。numpy/torch のベクトル化（GIL 解放 native op）
   で 16.6ms に収める前提。
5. **viz ゼロコピー（将来要件）**：毎フレーム数千系列を Python が再計算するユースケースが来たら、
   numpy ndarray を **`GraphicsBuffer`/`ComputeBuffer` まで CPU コピー無し**で運び、GPU instancing /
   compute shader で描画する（XCharts/uGUI は数千系列・60fps に不適）。
6. **broker 残注文（Live/Auto のみ）**：**正常終了時**に venue（kabu/tachibana）の resting order を
   best-effort で取消す（cancel-on-disconnect は両 venue に無いため client 側で能動取消）。**crash 時は
   取りこぼす**（どの設計でも crash には graceful 窓が無い）。これは受容し、必要なら別途対処する。
   なお本取消は **S2-spike(#7) の AC(a)（`run_coroutine_threadsafe(...).result()` 越しの GIL 解放）成立が前提**
   — 取消は `engine_controller` の同じ marshal を通るため、(a) が Mono で不成立なら decision 6 はこの経路で
   **達成不能**（別機構 or 受容を再検討）。また adapter は **Python ランタイム破棄の前に engine graceful-stop を
   呼ぶ**（順序を誤ると取消 coroutine が走らず空振りする）。
7. **engine の所有 = backcast（移植・本線）**：取引 engine（`python/engine`、Nautilus ベース）は
   TTWR から backcast へ **移植**し、backcast が唯一の home として所有する。TTWR を生かしたままの
   submodule 参照・pinned-package-from-TTWR は採らない（TTWR は #5 で**廃止**するため）。
   engine は host 非依存を保ち（host 結合は **sink 注入点・`engine.core`/`engine.inproc_server` の 2 入口・
   dict 境界**に既に局所化済み）、Unity(C#) は decision 8 の adapter でこれを駆動する。
   pin（**CPython 3.13.13 / nautilus-trader 1.226.0 / PRECISION_BYTES=8 standard**。Intel Mac は PyPI に
   standard wheel 不在のため sdist を `HIGH_PRECISION=false` で再ビルド必須・共有 catalog も 8）は移植時に
   TTWR `uv.lock` から確定し、以後 backcast の `pyproject`／埋め込み venv に住む。
8. **C#↔Python は単一 adapter 層**：呼び出し面を 1 つの adapter に集約し、C# 製 sink を engine の
   sink 口へ差す。worker→main は **GIL なしで読める C#/native バッファ**で受け渡す（main thread は
   GIL を取得しない＝render loop を GIL 競合から守る）。IPC seam や別プロセス起動は今は作らない。

### Nautilus が Mono を拒んだ場合（フォールバックは事前設計しない）

全埋め込みの唯一の未知数は「**Nautilus（`nautilus_pyo3` native 拡張・precision 焼き込み wheel）が
Unity の Mono+pythonnet 上で load できるか**」。これを **S0 spike** で先に判定する。

- **通れば** → 単純路線で確定。
- **落ちたら** → その場で**初めて分かった事実（どの段階で・どう落ちたか）に基づいて考え直す**。
  別プロセス化・サブインタプリタ・engine 差し替え等の対策を、**今は先回り設計しない**。

### 可逆性のための軽量ハイジーン（配管は作らない）

将来やむなく engine を Unity プロセスから出す判断に備え、**C#↔Python の呼び出し面を 1 つの adapter 層に
集約**し、`python/engine` は host を強く仮定しない状態に保つ。ただし **IPC seam や別プロセス起動は今は
作らない**（単純路線を濁さない）。これは「やすい保険」であって事前構築ではない。

## Considered Options

- **採用：全埋め込み（単純路線）**。仕組みが単純、安全要件（UI 死＝執行死）が同一プロセスで自動成立、
  viz ゼロコピーも可能。代償：Nautilus を Mono+pythonnet で載せる未知数、Python/native crash が
  Unity ごと巻き込む（＝ owner が望む「一緒に死ぬ」と整合）。
- **不採用（先回りしない）：執行エンジンを別プロセス化＋ライフタイム束縛**。crash 隔離と Nautilus を
  通常 CPython に逃がす利点はあるが、子プロセス・Job Object・heartbeat・IPC seam という配管を
  **要件が確定する前に**抱える。安全要件は埋め込みで既に満たせるため、この複雑さは S0 が Nautilus を
  拒んだ時に**初めて**検討する。
- **不採用：loopback HTTP/WS サーバ / gRPC サブプロセス**。C#↔Python ゼロコピー不可（要望者がレイテンシ
  理由で不採用）。

## Consequences

- **S0 spike が全体のゲート**（最重要・UI 着手前）：S0 の green が gate するのは **near-term の simple path
  （Replay/Live）= ① のみ**。① `import nautilus_trader` + **threaded** backtest 1 本（C# サブスレッド＋
  `Py.GIL()`、main は GIL なし）が Unity Mono+pythonnet で通る、を throwaway で検証する。①が落ちれば単純路線の
  前提が崩れるため、その事実で再考する（本 ADR の「Nautilus が Mono を拒んだ場合」）。
  本 ADR は **S0(①) 合格をもって `accepted`** に昇格する。
- **viz zero-copy は S0 から lift（非 blocker）**：② `numpy`/`torch` ndarray を `GraphicsBuffer` まで中間
  CPU コピー無し（＋単一 upload）で運び GPU 描画する seam は **future-payoff（毎フレーム数千系列）** で
  near-term 消費者がゼロ。GPU interop は Mono で finicky になりやすく、S0 に同梱すると go/no-go が
  future-only seam に人質を取られる。→ **viz-spike（独立 issue・非 blocker・S0 直後に実行可）** に分離する
  （S2-spike を S0 から分けたのと同じ規律）。②が落ちても near-term は進み、後日 IPC 一括 or C# 計算で代替。
- **crash で Unity ごと落ちる**：Python/native の crash は同一プロセスの Unity を巻き込む。これは
  「UI が死ねば執行も死ぬ」という owner 要件と方向が一致しており、許容する。
- **配布**：Unity ビルドに CPython 3.13.13 ランタイム＋venv（nautilus-trader 1.226.0／numpy/torch 等の
  wheel）を同梱する必要がある。wheel は **OS 別**（`macosx_*` / `win_amd64`）で native ローダ（Rust core）が
  別物のため、venv は **deploy OS ごとにビルド**する。
- **deploy target = Windows（first-class）**：live の kabuステーションが Windows 専用のため、本番 deploy は
  **Windows desktop（Mono バックエンド）**。Mac は開発・先行検証用で、**Mac-green は `win_amd64` wheel が
  Windows-Mono で載ることを証明しない** → S0 は最終的に Windows で通す（#2、Step 2 着手前に Windows 再走）。
- **pythonnet は Mono バックエンド前提**（IL2CPP=AOT では不可）。Windows / Mac いずれもデスクトップ Mono を対象。

## 移行順序（並行フォーク → TTWR 廃止）

`backcast`(Unity) を**本線**として構築し、TTWR(Bevy) は**凍結した fallback** として
カットオーバーまで本番に温存、その後 **廃止**する（going-forward の開発は backcast 側のみ。engine は
backcast へ移植済みなので二重 co-development は発生せず、fallback 期間中の本番 hotfix のみ TTWR 凍結
コピーに当て backcast へ forward-port する有界コストを受容）。

seam ゲートは段ごとに分ける（前段 green は後段を保証しない）：

- **Step 0（ゲート / #2）**：S0 spike — **threaded** Nautilus backtest（C# サブスレッド＋`Py.GIL()`、
  main は GIL なし・≥300fps、本番 pin と PRECISION_BYTES=8 を in-process assert、real catalog 小サンプル）
  ＋ numpy/torch zero-copy→GraphicsBuffer。**最終的に deploy OS = Windows で通す**。落ちたら事実ベースで再考。
- **Step 1（#3）**：Unity host + 埋め込み Python ランタイム + **Replay parity**（実弾ゼロ）。engine を
  backcast に移植し package 化、埋め込み venv に同一 pin で install。
- **S2-spike（ゲート / #7）**：Step 2 着手前に live **asyncio loop** + sub-thread tick push を
  Unity Mono で検証（venue 実接続前）。
- **Step 2（#4）**：**Live/Auto parity**（kabu 50銘柄・1秒polling約定、立花 EVENT 受信、safety_rails/gates、
  正常終了時の broker 残注文取消、UI 死＝Python 死の確認）。
- **Step 3（#5）**：カットオーバー → backcast 本番化、TTWR 廃止（fallback 温存期間と廃止条件を確定）。
