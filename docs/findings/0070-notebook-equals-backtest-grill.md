# findings 0070 — notebook = backtest 一本化（#95 Phase 1 grill 設計の木）

方針: [ADR-0016](../adr/0016-notebook-equals-backtest-per-cell-run.md)（per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化・ADR-0012 の「単一再生ボタン」facet を supersede）。本 findings は **#95 Phase 1 で起案**した同 ADR の grill 設計の木と、立て付けの根拠（B1 却下理由・速度復活理由・命令型 .py UI sunset の踏み込み判断・`bt` lifecycle の owner HITL 訂正）を**会話で消えないように固定する**。

backcast に `FLOWS.md` は無いため、本 findings ＋ ADR-0016 ＋ #95 Phase 2–6 各 slice の `docs/findings/` が設計＋検証の正本となる。ADR-0012 / ADR-0013 / ADR-0006 / ADR-0007 は immutable（書き戻さない）。

---

## Grill source（2026-06-20 owner セッション）

「**marimo notebook で kernel/engine を制御する**」本体の設計分岐の grill。`KernelRunner.run` の per-bar ループが host 所有（`engine/kernel/runner.py:230`）で、ユーザー cell は `get_bar()` を読み `submit_market(qty)` を呼ぶ reactive 反応役にすぎない既存モデルに対し、owner が「**ユーザーが marimo に書くコードで backtest 経路へ明示接続できないか**」を問うた。

3 案を検討（互いに排他・1 案採用）:

| 案 | ユーザーが書く strategy cell | loop 所有 | marimo らしさ | 状態 |
|----|--------------------------|----------|-------------|------|
| **B1**（#96） | reactive cell を**複数**（`bt.bar()` を読む） | ホスト | ✅ per-cell output / DAG が残る | **却下** |
| **B2**（#97） | `for bar in bt.replay(): ...` ループ **1 cell** | ユーザー | ❌ 1 スクリプトに縮む | **採用** |
| **B3**（#98） | `bar = bt.step()` cell（押すたびに 1 bar） | ユーザー | △ 状態持ち・marimo idempotent 前提と衝突 | **採用** |

**結論**: B1 却下・**B2 ＋ B3 併存**（同一 `bt` ハンドルで replay/step 両方を提供）。共通前提として `config` は **`ScenarioStartupTile`** が所有・`結果` は **Hakoniwa** が所有・cell に書くのは strategy だけ、を全 3 案で共有。

### B1 却下の理由（決定的）

owner が grill 中盤に B1 を「再 reactive 系」として温存しようとしたが、以下で却下:

- **「明示接続」の telos に届かない**: B1 は接続を panel で明示するが、per-bar 駆動は依然 host 所有。ユーザーから見て「loop はどこかで誰かが回している」隠れ機構が残る。owner が問うた「**ユーザーが notebook に書くコードで backtest 経路へ明示接続**」の telos は満たさない（ユーザーが `bt.replay()` / `bt.step()` を**書く**から「明示接続」になる）。
- **「reactive」と「明示接続」が概念衝突**: B1 は cell が reactive に再計算される（host の per-bar push がトリガー）。「ユーザーが engine を駆動」と「engine が cell を駆動」が同居し、どちらが authoritative かが曖昧。ユーザー側の mental model が割れる。
- **マーケットでの代替**: marimo らしさを保ちたい局面は「**土台層**＝engine 非接続の純粋計算 per-cell」が担う。B2/B3 cell は backtest 駆動という別意味論を持たせるので、reactive と命令的を意味の階層で分離する方が clean。
- **B2/B3 を 1 つにできない理由**: B2 は「視覚 playback / 早期 break / bar 跨ぎ任意状態」、B3 は「bar-by-bar デバッグ・per-cell RUN と自然に噛み合う」。両者は user task が違う（書きながら見たい vs 一発で全期間流したい）ので、API を 2 つ持つことに合理性がある。**同じ `bt` の 2 つの呼び方**として併存させると state 複雑化を避けられる（D10）。

#96 は本 findings landing と同時に close。

### 速度レジスタを意図的に「復活」させる理由

`#30/#68` で footer transport（`▶/⏸/⏭/⏹ ＋ [1,2,5,10,50]`）は `core.py:42` の在席コメントが残る形で削除された（`backend_service.py:387` / `WorkspaceFooterView.cs:5` / findings 0046）。退役理由は **「reactive drain は一瞬で終わる」**（cold compile + 1-shot drain が想定された時点での真）。

**B2 が前提を変えた**: `bt.replay()` は「目で追う visual playback」を想定し、ユーザーが**走っている様子を見ながら判断する**。`_REPLAY_BAR_INTERVAL_SEC = 0.01` の固定値は遅すぎる（50k bar = 8.3 分）か速すぎる（描画追従 fail）かのどちらかになる。**pacing を user-controllable にしないと B2 のユーザー体験が崩れる**。

**復活の形**: footer transport の UI ボタン群は復活させない（#30/#68 の UI 退役判断は維持）。**「コードで `bt.replay(speed=N)` と指定」だけが復活させた seam**。速度レジスタ自体は host 所有・thread-safe・live-mutable で `DataEngineCore` 上に置く（`_replay_stop_event` の隣）。loop が毎 bar 読むので将来 HITL slider / programmatic test harness が additive で乗っかれる余地は残るが、本 ADR の scope では cell-facing API は **call-time の `speed=N` 引数のみ**。

silent な復活でないこと（findings に理由を残すこと）を本 finding が保証する。

---

## #95 Phase 1 owner HITL（2026-06-20 続行セッション）

issue #95 本文 D1–D12 は 2026-06-20 grill の凍結出力。Phase 1 が新 ADR を起案するに当たって、残った load-bearing な下位決定を 2 問だけ HITL でアンカーした:

### Q1 — 命令型 `.py` の UI 経路の扱い（採用＝formal sunset）

issue D11 が「global ▶ Run / title-bar Run / footer transport は supersede」と言うため、命令型 `.py` の UI 実行手段が消える。この ADR で formal sunset するかを問うた。

**owner 判断**: **採用＝formal sunset**。ただし wording で 2 点訂正:

1. 命令型 `.py` を **「UI から開けない」と書かない**。現実は #80 picker 退役後でも **File→Open は使え、`load_app` が None の場合は findings 0054 の 1-cell wrap で開ける**。`.py` を開く migration / editing affordance としては存続。
2. ADR の主張は「**UI 実行経路の sunset**」であって「`.py` Open の sunset」ではない。実行入口を per-cell RUN だけに一本化し、命令型 `Strategy` クラスを UI で batch 実行する fallback は残さない、が正確。

帰結（ADR-0016 D4 へ反映）:

- global ▶ Run / title-bar Run / footer transport は **#95 D11 により supersede**
- UI からの実行は **per-cell RUN のみ**
- 命令型 `.py` は **File→Open の 1-cell wrap** で編集・移行用に開ける
- imperative `Strategy` class を UI で batch 実行する fallback は残さない
- 命令型 runtime / `strategy_loader` / `KernelRunner` boundary は pytest / golden / programmatic 用に存続

**却下した選択肢**:

- (b) 命令型 fallback UI を残す（global ▶ Run を imperative-only に降格） → 「notebook = backtest 一本化」(D7) の telos に逆行。永続的に非対称 UX を抱える。
- (c) 命令型 `.py` を自動 1-cell marimo wrap して per-cell RUN で走らせる → `Strategy.on_bar` → marimo per-cell の自動変換 adapter が要り工数大。`_select_replay_strategy` の AST detect-first ordering と二重化。設計上も notebook=backtest 一本化を濁す。

### Q2 — `bt` ハンドルの lifecycle・状態共有・reset trigger（採用＝config 単位・単一 bar pointer 共有）

issue D4 が「startup パネル config から `bt` を構築・1 個」とは言っていたが、**いつ reset されるか**・**`bt.replay()` と `bt.step()` の状態共有**は明示されていなかった。

**owner 判断**: **採用＝config 単位・単一 bar pointer 共有**。

- `bt` は **startup-panel config 単位**で 1 個生成
- `bt.replay()` と `bt.step()` は **同じ `bt` / 同じ KernelRunner state machine / 同じ bar pointer** を共有
- startup-panel config を commit し直したら **`bt` は破棄して作り直す**（kernel teardown + 新 kernel）
- `bt.replay(speed=N)` は呼ばれるたび **pointer を 0 に reset** して end まで走る
- `bt.step()` は現在 pointer から **1 bar 進める**（終端で `None`）
- `bt.step()` 同一 cell の再実行は **意図的に stateful**: 各実行で 1 bar 進む
- 完走後 pointer = end。以後 `bt.step()` は `None`、次の `bt.replay()` はまた 0 から再走
- replay 実行中に同じ `bt` へ別 RUN が入る場合は **Phase 4/6 の running guard でブロック**

ユーザーモデルは 3 行で説明できる: ① **config commit が `bt` を新規作成** ② **replay は常に 0 から** ③ **step は現セッションの pointer を進める**。

**却下した選択肢**:

- (b) replay と step が独立 instance（独自 pointer） → 「いまの backtest 状態」が複数になり Hakoniwa の running snapshot / stop / reset の説明が急に増える。D10「同じ土台に乗せる」と相性が悪い。
- (c) ノートセッション単位永続（startup commit で 1 回作って以後 reset なし） → `bt.replay()` が「中断再開」意味論になり B2 の「全期間 visual playback」の直感を壊す。`reset` ボタンが必要になり旧 transport を呼び戻す。

帰結（ADR-0016 D3 へ反映）。

---

## 実装段階（#95 Phase 1–6）

本 findings は **Phase 1 のみ**を着地させる。Phase 2–6 は順序付き別 slice で、各 Phase 着地時に該当 findings を起こして本 finding を参照する。

| Phase | 内容 | gate（issue #95 AC より） |
|---|---|---|
| 1 | 本 ADR + 本 findings + CONTEXT glossary（**本セッション着地**） | owner HITL（実施済み）|
| 2 | 土台（全 cell 窓 RUN ボタン + 純粋計算 per-cell run） | pytest（reactive 下流再計算 / 上流非依存 cell は再計算されない / import-purity 不変）＋ AFK probe（adopted+spawned 両方にボタン在・押下で出力が窓に出る）|
| 3 | `bt` ハンドル + `KernelRunner` state-machine 化 | pytest（step が 1 bar 進む / replay が全 bar / 両者が runner.py と同一順序 = golden 同値）|
| 4 | B2 `bt.replay()`（#97 を吸収） | AFK probe（replay cell RUN → Hakoniwa 逐次更新・速度変更が効く・stop で止まる）|
| 5 | B3 `bt.step()`（#98 を吸収） | AFK probe（step cell RUN → 1 bar 進む）+ reset/idempotency pin |
| 6 | 実行状態 UI + block popup + rich output | per-cell idle/running/stale + `mo.md` / table / chart 出力 |

## 本 finding が**やっていない**こと（Phase 1 範囲外）

- `bt` 実装の具体クラス・module 配置（Phase 3）
- `KernelRunner` を「1 bar 進めて中断/再開」できる形へ切り出す具体 seam（Phase 3）
- 速度レジスタの正確な field 名 / setter API / 同期プリミティブ選定（Phase 4）
- running guard の構造（Phase 4 / 6）
- 全 cell 窓の RUN ボタン C# 配線詳細（Phase 2 / 6）

これらは本 ADR の方針下で各 Phase の findings に固定する（ADR は書き戻さない＝自己保護条項）。
