# findings 0015 — Replay 実行設定パネル（scenario startup panel・#29）

Replay 実行パラメータ（granularity / cash / start-end / universe）を通常起動 Unity から構成し、
strategy `.py` に co-locate した v3 scenario sidecar として永続化、production path で run する縦切り。
設計は `grill-with-docs`（2026-06-14）で確定。用語は CONTEXT.md、merge-write 方針は ADR-0005。

## 確定した設計判断（grill 由来）

1. **永続化形式 = engine 既存の v3 scenario sidecar**（`<strategy>.json` の `"scenario"`）。engine の
   `load_scenario`/`strategy_loader.load` がネイティブに読む唯一の形式で、Unity がこれを書く＝変換層ゼロ。
   issue 本文の「ADR-0003 / Unity 独自スキーマ」は category error（ADR-0003 は layout 専用）。**Unity が write
   するのは v3 のみ**（v1/v2 切り捨て）。
2. **panel フィールド = start / end / granularity / initial_cash の 4 つ**（TTWR `populate_startup_tile` parity）。
   issue の `lookback` は run 期間 = `start`/`end` への読み替え（指標窓 lookback は別物 = `strategy_init_kwargs`、#29 外）。
3. **universe は別 seam**（`InstrumentRegistry` SoT ＋ sidecar writeback）。#29 は最小テキストリスト入力まで、
   リッチ picker は #31 が同じ SoT に差し込む。
4. **3 projection**（editing buffer → validated-for-write → on-disk）。AC④「不正値は run しない」は (1)→(2) ゲート。
5. **strategy 選択** = `IStrategyFileProvider`/`StrategyProviderRegistry`（#16）を消費。0→Run ブロック、1→使用、
   N→決定的 active 表示。supplyable は **Run 起動の瞬間に再問い合わせ**（populate 結果をキャッシュしない）。
6. **起動経路 = production state-machine path**（`load_replay_data` → `start_engine`）。sidecar 駆動・`RustBacktestSink`
   不要（`get_state_json`/`get_portfolio` ポーリング）。`start_nautilus_replay(cfg)` は throwaway harness 専用で
   #29 後 deprecated（AC⑤ harness 依存解消の実体）。issue の `start_nautilus_replay` 記述は production path へ読み替え。
7. **catalog_path は config 層**（`DataEngine` ctor / settings）＝ panel フィールドにしない。v3 に catalog キーは無い。
8. **merge-write = Newtonsoft `JObject`**（→ ADR-0005）。`account_type`/`instruments_ref`/任意 nested `strategy_init_kwargs`
   を無損失 preserve。Newtonsoft は `ScenarioSidecarStore` 一点に封じ込め、layout は `JsonUtility` 据え置き。
9. **配置 = Hakoniwa `PanelKind::Startup` タイル（slot 0）**（TTWR parity）。floating window 新設は却下。
10. **復元 = read-on-populate（live watcher なし）**。write seam は TTWR 形（`WritebackOutcome` を返す）に揃え、
    後続の watcher スライス（self-trigger 抑制・Windows mtime 対応）を純加算にする。逸脱ではなくスライシング。
11. **AC③ = bar-by-bar ライブ追従**。`start_engine` の run 完了後一括 `apply_replay_event` を廃し、`engine_run` の
    `on_bar` で per-bar stream（primary bars[0] は prime 済みなので skip）。

## 実装と検証

- engine: `engine_runner.run(on_bar=…)` ＋ `_RunBufferAdapter.get_extra_subscriptions`、`_backend_impl.start_engine`
  の per-bar streamer。RED→GREEN: `python/tests/test_replay_bar_streaming.py`。non-kernel suite 86 passed。
- C#: `Assets/Scripts/ScenarioStartup/`（`ScenarioSidecarStore` / `InstrumentRegistry` /
  `ScenarioStartupParams`+`Validation` / `ScenarioStartupController` / `ScenarioStartupTile` /
  `ScenarioStartupHitlHarness`）。
- AFK gate（Python-free・headless）: `Assets/Tests/E2E/Editor/ScenarioStartupE2ERunner.cs` →
  `[E2E SCENARIO STARTUP PASS]`（#54 で throwaway `ScenarioStartupProbe` から昇格・改名、findings 0054）。非空 merge-preserve kill（nested `strategy_init_kwargs` 落ちを検出）＋ validation（AC④）
  ＋ registry ＋ controller roundtrip（populate→edit→run-gate→commit→**restore**→fallback 優先順位）。
- HITL（owner 実行・display＋catalog 要）: `ScenarioStartupHitlHarness`（`AutoBootstrapEnabled` を ON で Play 所有）。
  Startup タイル編集→Run→production path（`load_replay_data`→`start_engine`）→チャート bar-by-bar。
  ※本環境は catalog データ不在のため full engine run は未実測（owner HITL ゲート）。

## 既知の事前失敗（#29 無関係）

`test_kernel_*`（golden / bars / risk_gate / teardown）は **本変更前から**失敗（catalog データ不在等の環境要因）。
stash で baseline 確認済み。

## 後送り（純加算スライス）

- live `FileSystemWatcher` ＋ writeback self-trigger 抑制（TTWR `scenario_sidecar/watch.rs` ＋ ADR-0020 parity・
  Windows mtime 解像度対応）＋ J15 外部編集アフォーダンス。
- #31 instrument picker（検索・候補・複数選択を同じ `InstrumentRegistry` SoT に差し込む）。
- `.py` inline SCENARIO fallback の populate（pythonnet `load_scenario` 読み）。
- positions/orders/run_result の Hakoniwa タイル統合（現 HITL は chart ＋ status を最小描画）。
