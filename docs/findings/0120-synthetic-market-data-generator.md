# findings 0120 — 合成マーケットデータ生成器（synth_bars / PricePath / Replay↔Auto parity）

Issues: **#151**（生成器コア + Replay feed）/ **#152**（Auto feed + Replay↔Auto parity）/
**#153**（既存合成バーの移行 = 一般性の実証）/ **#154**（確率過程ビルダー gbm/sine/ramp + volume）。

方針 ADR: **ADR-0006**（DuckDB 直読み = Replay 市場データ源）/ **ADR-0025**（marimo cell が Replay と Auto の
両方を駆動）/ **ADR-0031**（`bt.universe.*`）。本 finding は新規 ADR を起こさない（ADR-0025 の延伸を
findings に記録する規約 = sibling findings 0117–0119 と同型）。

---

## 1. なぜ要るか（the seam no existing helper covers）

既存テストは合成バーを**各ファイルで個別に手書き**している（`_synthetic_bars` / `_series` / `_bar` / `_bars` が
8+ ファイルに散在・volume 定数が 1.0/10.0/100.0/1000.0 でバラバラ・OHLC 構築が flat/drift/band で不統一）。
ランキング戦略（価格を動かして順位を変える）を宣言的に書ける**単一の供給源**が無い。さらに「合成シナリオを
Replay と Auto の両方に流して**同じ決定が出る**」ことを担保するゲートが無い（= Replay↔Auto seamless の floor）。

---

## 2. コード裏取り（grill で確認した既存シーム・正確なパスと行）

| シーム | 場所 | 実態 |
|---|---|---|
| `Bar`（値の単位） | `engine/kernel/duckdb_bars.py:47-58` | **nautilus-free frozen dataclass** `(instrument_id, ts_event_ns, open, high, low, close, volume)`。issue 文の「nautilus Bar」は誤読 |
| `load_universe_bars` | `engine/kernel/duckdb_bars.py:320-332` | `-> list[Bar]`（per-iid を `merge_bars_by_ts` で **merge 済み**）。`{iid:[Bar]}` ではない |
| `merge_bars_by_ts` | `engine/kernel/duckdb_bars.py:309-317` | per-iid ts 昇順列を stable-merge。同 ts は universe 順保持 |
| Daily ts 規約 | `engine/kernel/duckdb_bars.py:85-89` | 15:30 JST（東証引け）→ UTC ns |
| Minute ts 規約 | `engine/kernel/duckdb_bars.py:92-101` | `HH:MM:59.999999` JST（**bar END ラベル**）→ UTC ns |
| `granularity_to_interval_ns` | `engine/kernel/duckdb_bars.py:182-198` | Daily=1日 / Minute=60s（live cadence の単一真実源・#112 D6） |
| Replay 注入 | `tests/test_v19_marimo_parity.py:150` | `monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a,**k: list(_synthetic_bars()))` |
| `KernelStepper(bars=...)` | `engine/kernel/stepper.py` | bars を直接受ける（midstream join テストはこちらを使う） |
| `TickBarAggregator` | `engine/live/aggregator.py:20-96` | bucket=`ts_ns//interval_ns`。open=最初tick / high=max / low=min / close=最後tick / volume=Σsize。**前 bucket は次 bucket の tick 到来で初めて closed を emit**（最終 bar は flush tick が要る） |
| `KlineUpdate`→`Bar` | `engine/kernel/live/driver.py:305-323` | `is_closed=True` のみ on_bar。`bar.ts_event_ns = evt.ts_ns`（= aggregator の `bucket*interval` = **bar 開始**） |
| `TradesUpdate` | `engine/live/adapter.py:120-130` | `(kind="trades", instrument_id, ts_ns, price, size, aggressor_side)` frozen |
| `MockVenueAdapter.inject_tick` | `engine/live/mock_adapter.py:118-125` | **subscribe-gated**: `if event.instrument_id in self._subscribed:` のみ queue 投入 |
| venue-selectable feed | `tests/test_kabu_live_universe_churn.py:159-164` | `_make_feed(venue, ids)` → `_KabuFeed`/`_TachibanaFeed`、各 `decode(rec) -> TradesUpdate | None` |
| Auto 駆動ハーネス | `tests/test_v19_cell_auto_parity.py` / `tests/test_kabu_live_universe_churn.py:192-345` | `LiveRunner`+`MockVenueAdapter`+`KernelLiveEngineController.attach(strategy_cls=bridge_factory)` |
| cell→scenario | `engine/strategy_runtime/live_cell_runtime.py:130` + `scenario.py:366 load_scenario` | cell の sidecar `<cell>.json`（`{"scenario": {...}}`）から instruments/start/end/granularity/initial_cash |
| import-purity gate | `tests/test_gate_import_purity.py` | **nautilus 漏れのみ**禁止。numpy/random は対象外 |

**ts ミスマッチ（parity の肝）**: Replay の Bar は **bar END ラベル**（Daily 15:30 / Minute :59.999999）。
Auto の aggregator は **bucket 開始**（`bucket*interval`）を bar.ts にする。同一 minute/day へ落ちるので
**time-of-day（分・日）は保存される**が、ns ラベルは一致しない → parity は **ts ラベルではなく
(instrument, side, qty) の決定列**で比較する。

---

## 3. 設計の木（決定）

### D1 — 生成器の置き場所 = `python/engine/synth/`（test-support だが engine 配下）
`MockVenueAdapter`（`engine/live/`）の前例に倣う。engine 型（`Bar`/`TradesUpdate`）を産出/消費し、spike からも
import できる単一供給源にする。import-purity は nautilus-only なので問題なし（synth は production runtime から
import されない）。

```
engine/synth/__init__.py     # 公開 API re-export
engine/synth/core.py         # BarPoint / PricePath(Protocol) / synth_bars / 営業日・セッション grid
engine/synth/builders.py     # explicit / constant / trend / from_fn               (#151)
engine/synth/stochastic.py   # gbm / sine / ramp / volume builders                 (#154 — core 無改変の証明)
engine/synth/feed.py         # universe_bars(scenario)->list[Bar] / auto_trades(scenario)->list[TradesUpdate]  (#152)
```

### D2 — `BarPoint`（1 足の値）
`close` 必須、`open/high/low/volume` 任意で close から賢く既定。**`open` 既定 = 前足終値**（gap 表現の要）。
`high` 既定 = max(open, close)、`low` 既定 = min(open, close)、`volume` 既定 = 既定定数（後述 D6）。
`open=None` かつ前足が無い（最初の足）なら `open=close`。

### D3 — `PricePath`（拡張の要 = callable Protocol）
シグネチャ `(bar_index: int, ts_event_ns: int, prev_close: float | None) -> BarPoint | float`。
float を返せば close-only の簡易形（BarPoint(close=x) と等価）。**synth_bars コアは Protocol しか知らない**ので、
新ビルダーは `engine/synth/stochastic.py` にファイルを足すだけ（core.py 無改変 = #154 AC の構造的担保）。

### D4 — `synth_bars(symbols, start, end, granularity, *, path=..., volume=..., session=...) -> dict[str, list[Bar]]`
- `granularity="Daily"`: start..end の**平日**を 15:30 JST 引けで 1 足ずつ（`_date_to_ts_event_ns` と一致）。
- `granularity="Minute"`: 既定で**東証セッション足**（09:00–11:30, 12:30–15:30 の毎分）。`session=` で
  `(hh,mm)` スロット列を上書き可能（v19 の疎なスロット `[(9,55)…(14,55)]` を byte-identical 再現するため）。
- `path` は単一 `PricePath` または `{symbol: PricePath}`（銘柄ごとに別パス = ランキング・シナリオの肝）。
- 返り値は `{iid: [Bar]}`（AC #151）。各 iid のリストは ts 昇順。

### D5 — Replay feed = `universe_bars(scenario, *, paths) -> list[Bar]`（`feed.py`）
`synth_bars` の `{iid:[Bar]}` を `merge_bars_by_ts` で merge し、`load_universe_bars` と**同型の `list[Bar]`** を返す。
テストは `monkeypatch.setattr(runner_mod, "load_universe_bars", lambda *a,**k: universe_bars(...))`（既存イディオム）。

### D6 — `auto_trades(scenario, *, paths) -> list[TradesUpdate]`（`feed.py`・#152）
各 `Bar` を **aggregator が同じ OHLCV を再構成する tick 列**へ変換する:
- flat 足（O==H==L==C）: 1 tick `(price=C, size=V)`。
- 一般足: `O, H, L, C` の 4 tick を**同一 bucket 内**に順注入（open=first / high=max / low=min / close=last を
  満たし、bar の妥当性 `L≤O≤H` から OHLC が厳密復元される）。volume は 4 tick へ分配して **Σ=V を厳密保持**。
- **末尾 flush**: 全 instrument の最後の実 bar の次 bucket に 1 tick（共通 future ts）を足し、最後の実 bar を
  close させる（churn テストの `future_ts` イディオムと同型）。flush tick は never-closed partial を作るだけで
  on_bar を駆動しない（無害）。
- `aggressor_side` は price ≥ 前 close で "buy"、それ以外 "sell"（決定論・値に意味は無いが固定）。

`auto_trades` を churn の `_make_feed` に **`"synthetic"` feed** として合流させる（`decode(rec)->TradesUpdate|None`
の正規化規約は kabu/tachibana と同一・rec は事前生成した TradesUpdate を素通しする薄い codec）。

### D7 — ランキング戦略 cell（#151 AC#4 + #152 parity の共通 oracle）
`python/spike/fixtures/strategies/synth_rank_cell.py`（+ sidecar `.json`）を新設。各 bar で universe を
**直近 close で降順ランキングし top-1 を保持**（保持先が変われば乗り換え = signed-delta 発注）。sklearn 等の
外部 artifact 非依存・完全決定論。価格設計（A 下落・C 上昇）で **途中で順位が入れ替わり picks が変わる**ことを
Replay で assert（#151 AC#4）、同 cell・同シナリオで Auto と決定一致を assert（#152 parity）。

### D8 — parity 比較 = 決定列 `(instrument_id, signed_qty)` の sequence
Replay（KernelStepper 経由・記録 sink）と Auto（controller.attach + auto_trades 注入・order_events 記録）の
**発注列を正規化して一致 assert**。ts ラベル差（D2 の bar END vs bucket 開始）は比較対象外。

### D9 — #153 移行（一般性の実証）
- `test_v19_marimo_parity` の `_synthetic_bars`（4 銘柄 × 2 日 × 8 疎スロット）→ `synth_bars(granularity="Minute",
  session=[(9,55)…(14,55)], path={iid: from_fn(...)})`。**既存 close 値 `_close(iid,minute)` を再現**し parity 維持。
- `test_v19_replay_core` の `_synthetic_bars`（3 銘柄）→ 同様。
- `test_bt_universe_midstream_join` の `_series`（Daily 終値配列）→ `explicit(closes)` ビルダー。`_bar` の
  OHLC 式（open=close, high=close+1, low=close-1, vol=10）を `BarPoint` 既定で再現し **MIDJOIN-01..06 +
  byte-identical（MIDJOIN-06）維持**。ts grid（`_BASE + i*_STEP`、1s grid）は `explicit` の grid 指定で再現。
- 移行は「重複削減 + 生成器が既存値を忠実再現」を実証。各テストの assert は無改変で GREEN を保つ。

### D10 — #154 ビルダー（core 無改変）
- `gbm(s0, mu, sigma, seed, dt)`: **stdlib `random.Random(seed)` + `math`**（numpy 非使用 = engine の
  numpy-free 規約を尊重・決定論再現）。`S_{n+1}=S_n·exp((mu−σ²/2)dt+σ√dt·Z)`、`Z=Random.gauss(0,1)`。
- `sine(base, amp, period, phase)` / `ramp(start, stop)`: 決定論ビルダー。
- volume パス: `synth_bars(..., volume=<PricePath-like>)` で出来高を時間変化（ADV/turnover ランキング用）。
  volume も「callable で時間変化」= 価格パスと同じ拡張パターン。

---

## 4. E2E ゲート（Action-ID・behavior-to-e2e で確定）

| Action-ID | 何を担保 | RED litmus（production logic を壊すと RED） |
|---|---|---|
| SYNTH-01 | `synth_bars` Daily が平日 grid・15:30 JST ts で `{iid:[Bar]}` 生成 | ts 規約を壊す / 週末を含める |
| SYNTH-02 | `synth_bars` Minute がセッション足（+`session` 上書き）で生成 | session grid を壊す |
| SYNTH-03 | `BarPoint` の OHLCV 既定（`open`=前足終値で gap 表現） | open 既定を close にすると gap が消え RED |
| SYNTH-04 | 4 ビルダー explicit/constant/trend/from_fn（trend は gap_pct/volume 制御） | trend の gap_pct/volume を無視すると RED |
| SYNTH-05 | `PricePath` callable Protocol で core 無改変の新ビルダー追加 | core が具体ビルダーに依存すると拡張で RED |
| SYNTH-06 | Replay feed 経由で**価格設計が順位/選択を決める**決定論（A↓C↑ で picks 入替） | ランキングを close 非依存にすると RED |
| SYNTH-AUTO-01 | `auto_trades` が `{Bar}`→`TradesUpdate` 列（OHLC 復元 + 末尾 flush） | OHLC 復元 or flush を外すと aggregator bar が欠落し RED |
| SYNTH-AUTO-02 | `"synthetic"` feed が `_make_feed` 抽象に合流（kabu/tachibana と同一駆動コード） | feed 契約を破ると churn ハーネスで RED |
| SYNTH-PARITY-01 | **同一 cell・同一シナリオで Replay と Auto の決定（orders/picks）が一致** | aggregator 復元/flush/監視のどれかが崩れると決定列が乖離し RED |
| MIGRATE-V19-01 | v19 parity が `synth_bars` 経由で parity 維持 GREEN | 生成値が旧値と不一致なら parity RED |
| MIDJOIN-01..06 | midstream join が `explicit` ビルダー経由で byte-identical 維持 | `_series` 値の不一致で byte-identical RED |
| SYNTH-GBM-01 | `gbm(seed)` が決定論再現（同 seed = 同系列） | seed を無視すると RED |
| SYNTH-SINE-01 | sine/ramp + volume パスが core 無改変で動作 | core 改変が必要なら設計 RED |

---

## 4.1 RED→GREEN 実証（litmus 実機確認）

- **SYNTH-06**: ランキングを close 非依存（`max(key=id)`）に壊す → flip と dominant の picks が同一化し RED。復元で GREEN。
- **SYNTH-AUTO-01**: `auto_trades(flush=False)` に壊す → 各 instrument の最終 bar が never-closed で復元本数が不足し RED。復元で GREEN。
- 全 SYNTH-* / SYNTH-AUTO-* / SYNTH-PARITY-01 GREEN・タグ出力確認。#153 移行 3 ファイルは
  **byte-identical**（移行前 snapshot と全 OHLCV 一致）を実機確認し、MIDJOIN-01..06 + v19 parity/replay-core GREEN。

## 4.2 code-review high の後処理（2026-06-26・cleanliness + 契約堅牢化）

8-angle code-review（recall-biased）の結果、**correctness hard bug は 0**（litmus 非空虚・byte-identical
移行も line-by-line 確認）。garbage-in の contract-gap 2 件と cleanliness を理想形に寄せて修正:

- **calendar_grid**: `session=` を `sorted()` で正規化。順不同スロットでも出力 ts 昇順を担保（`prev_close`/gap が
  時系列で狂わない）。gate = SYNTH-02 に unsorted session の昇順 assert を追加。
- **auto_trades**: `interval_ns` が bar 間隔より粗いと複数 bar が同一 bucket に潰れ aggregator が 1 本に統合する
  silent 欠落 → **fail-loud `ValueError`**（bucket 衝突検出）。gate = SYNTH-AUTO-01 に `pytest.raises` 追加。
- **simplify**: `merge_universe`（core）を撤去し `universe_bars`（feed・#152 の Replay 入口）に一本化
  （`universe_bars`/`merge_universe`/`merge_bars_by_ts` の三重 alias を解消）。`_resolve_path`/`_resolve_volume_path`
  の near-duplicate を `_resolve_for_symbol` 1 本に統合。
- **coverage**: 未網羅だった `synth_bars(default_close=, path 未指定銘柄→flat バス)` 分岐を SYNTH-04 で gate。

`DEFAULT_VOLUME` / `calendar_grid` / `VolumePath` / `from_fn` は __all__ に残置（生成器ライブラリの公開語彙＝
default 定数・grid builder・path 型・named escape hatch。`linear_grid` と対称で外部 grid 注入の入口）。

## 5. 再走手順

```
cd python && uv run pytest tests/test_synth_generator.py tests/test_synth_auto_parity.py \
  tests/test_synth_builders_stochastic.py tests/test_v19_marimo_parity.py \
  tests/test_v19_replay_core.py tests/test_bt_universe_midstream_join.py -v
# rollup: pwsh scripts/run-all-tests.ps1 -PytestArgs 'tests/test_synth_*.py'
```

番号注記: 採番は `ls docs/findings | sort` の次空き = 0120。
