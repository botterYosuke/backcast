# S0 Spike 結果: Unity (Mono) + pythonnet で Nautilus を駆動できるか

- Issue: #2 (S0 spike)
- 関連 ADR: [ADR-0001 — Unity + pythonnet embedded frontend](../adr/0001-unity-pythonnet-embedded-frontend.md)（status: **proposed** のまま）
- 実行環境: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1 (apiCompat=6, standalone=Mono)
- nautilus_trader 1.226.0（`HIGH_PRECISION=false` で sdist 再ビルド）, Python 3.13.13, venv `python/.venv`（uv 構成）
  - **interpreter patch drift（#8 grill 2026-06-13）**: この Mac leg は実測 `3.13.13` で走ったが、これは Mac 先行実験の値。
    **Windows production pin は `3.13.11`**（＝TTWR `.venv` 実測・deploy 本番値。`3.13.13` は uv index に存在せず誤記だった）。
    Mac-green の事実は保存するが、production の唯一の真は `3.13.11 win_amd64`。詳細は ADR-0001 decision 7 / #8 findings。

---

## 1. 判定サマリ

**S0 の Mac leg は中核の未知数が GREEN。** S0 唯一の本質的リスク＝「Nautilus（Rust core を含む `nautilus_pyo3`）が Unity Mono ランタイム + pythonnet 上で load し、実 backtest を完走できるか」は、Unity をヘッドレス（`-batchmode -nographics`）で起動して実証済み:

```
[S0 PROBE PASS] bars=204 fills=0 equity=10000000 (nautilus loaded & backtest ran under Unity Mono)
exit 0
```

これにより near-term の simple path（Replay / Live の薄い経路）は de-risk され、**#3 (Step1) は着手可**。

**owner の playmode 目視ゲートも GREEN（2026-06-12）。** SampleScene を Play し、Console で
`S0 PASS: frames=1233 runs=1 bars=204 equity=10000000` を確認。worker が backtest を回す間も main は
1233+ フレーム滑らかに描画継続し、deadlock/crash/フレーム停止なし。`runs=1` は初回 `Py.Import` の
nautilus 初ロードが重く、その完走時点で main が既に 300 フレームを大きく超えていたため（＝main が GIL に
一切ブロックされなかった証拠で、想定どおりの挙動）。→ **AC① は Mac leg で完全充足。**

残る未消化ゲートは Windows のみ:

- **deploy ターゲットは Windows。Mac-green は `win_amd64`-under-Windows-Mono を未証明であり、Step2 (#4) 着手前の Windows 再走が必須ゲートとして残っている**（§6）。

---

## 1.1 ⛔ Windows leg = RED（2026-06-13, #18 / Step2 prereq）— BacktestEngine.run が Windows-Mono で segfault

**deploy OS（Windows）で S0 AC① が FAIL。** ADR-0001 が「唯一の非可逆コミットメントの前提」とした
「Nautilus が Mono+pythonnet 上で load し**実 backtest を完走**できるか」が、**Windows-Mono では完走しない**。

実行環境: Windows 11 / CPython **3.13.11** win_amd64（production pin）/ nautilus-trader 1.226.0 / Unity 6000.4.11f1（Mono）/ `S0EditorProbe.Run` batchmode。

**正確な落下段階（段階マーカーで特定・3 回再現）**:

```
[S0 PROBE MARK] configured (python313.dll / Windows uv home / .venv\Lib\site-packages)  ✓
[S0 PROBE MARK] PythonEngine.Initialize OK                                              ✓
[S0 PROBE MARK] BeginAllowThreads OK; starting worker                                   ✓
[S0 PROBE MARK] worker GIL acquired                                                     ✓
[S0 PROBE MARK] sys.path set; importing spike.s0_backtest (loads nautilus_pyo3)         ✓
[S0 PROBE MARK] nautilus import OK; running gates                                       ✓   ← nautilus_pyo3 は LOAD する
[S0 PROBE MARK] gates OK; running backtest                                              ✓   ← pin/footer (PRECISION_BYTES=8) PASS
→ Crash!!! (Segmentation fault, exit 139) — S0EditorProbe.cs:136 = m.InvokeMethod("run_backtest") の中
```

**つまり load 失敗ではない**。`import nautilus_trader` + 精度 pin 検査までは Windows-Mono で通り、**`BacktestEngine.run()` の実行中に native segfault**する。

**三点測量（原因の切り分け）**:
- standalone Windows-CPython（同一 `python/.venv`）で同じ backtest は完走 → `[S0 BACKTEST OK] bars=204`。**wheel/venv 自体は健全**。
- Mac-Mono では同じ probe が full backtest 完走（§1, `bars=204` / playmode `frames=1233`）。**Mono+pythonnet 一般の問題ではなく Windows 固有**。
- nautilus 非依存の S2-spike は同一 Windows-Mono で GREEN（findings 0005 §8.1）→ **pythonnet の cross-thread/GIL marshal 一般の問題でもない**。
- → 切り分け結果: **「Windows-Mono + pythonnet + nautilus Rust-core の backtest 実行」の組み合わせ固有**。

**ruled out**: worker thread の stack 枯渇（明示 64 MiB stack でも同一段階で crash）。

**証跡**: `C:/tmp/s0_win{,2,3,4}_*.log` / `s0_diag{A1,A2,A3,B1,B2,C}_*.log`、crash dump `%LOCALAPPDATA%\CrashDumps\Unity.exe.*.dmp`。

### 診断ラダー（owner 指定の 3 実験・2026-06-13）— 原因を C# background-thread context に切り分け

owner 確定の安価・高識別 3 変数を順に除外（闇雲な Mono 設定変更 / nautilus 再ビルドには広げない）:

| # | 除外した変数 | 設定 | 結果 |
|---|---|---|---|
| A | logging（`bypass_logging` vs Live 同等） | `LoggingConfig(log_level="ERROR", log_level_file="OFF")`、Mono 3 回 | **RED** → logging init / native→Python log callback **除外** |
| B1 | Python strategy callback（`on_bar`） | `S0_NO_STRATEGY=1`（catalog data・strategy なし） | **RED** → strategy callback **除外** |
| B2 | catalog / parquet / data marshal | `S0_SYNTHETIC=1 S0_NO_STRATEGY=1`（in-memory bars・catalog なし・strategy なし） | **RED** → data 経路 **除外**（pure Rust run） |
| C | **実行スレッド context** | `S0_MAIN_THREAD=1`（C# background worker ではなく **main thread** で run） | **🟢 GREEN** `[S0 PROBE PASS] bars=204` |

**verdict**: segfault は **「C# background worker thread 上で nautilus の Rust run-loop を回す」context 固有**。同一 backtest は (i) standalone Windows-CPython、(ii) Mac-Mono の background worker、(iii) **Windows-Mono の main thread** で完走する。落ちるのは **Windows-Mono + pythonnet で foreign（C# background）thread に GIL state を載せて Rust run-loop を駆動する**組合せのみ。owner 決定表の「worker のみ RED / main GREEN ＝ **pythonnet thread-state/GIL 登録との相互作用**」に一致。**stack 枯渇は除外**（64 MiB でも RED）。

### 含意（owner 判断事項・先回り設計しない＝ADR-0001 方針）

- **Windows-Mono 埋め込み自体の非互換ではない**（main thread で nautilus load + 実 backtest 完走）。「全条件 RED → 新規 superseding ADR」には**該当しない**。
- ただし **ADR-0001 decision 4 の脅威**：decision 4 は「重い Python 計算を C# サブスレッドで実行し main を GIL から守る」を前提とする。Windows-Mono ではその **background-thread 実行経路**が現状 segfault する。S0 AC① は「**main は GIL 非取得**で background が回る」を要求するため、main-thread GREEN は **AC① 充足ではない**（threading 規律を満たさない）。
- **ADR-0001 は `accepted` に昇格できない**（昇格条件＝S0 Windows AC①＝background-worker で GREEN が未達）。`proposed` 据え置き。
- **#18 / #4 は本件が解けるまで前進不可**（Live も同じ background asyncio-loop thread で Rust を回すため同根のはず。ただし S2-spike の seam model は nautilus 非依存だったため GREEN だった ＝ nautilus Rust run-loop を background で回す live exec が本丸）。
- 次アクションは owner 判断（pythonnet が C# background thread を Mono ランタイムへ attach する経路 / `PyGILState` 登録 / Mono thread attach の調査）。**対策は先回り設計せず**、診断はこの 3 実験で打ち切り（owner 指定）。

---

## 2. 検証した本番 pin（16-byte → 8-byte 訂正の経緯）

issue #2 / ADR-0001 の元の pin は「16-byte (high-precision) stock wheel」を前提にしていたが、これは本番 catalog と矛盾していた:

- 本番 catalog（jQuants 由来、Windows 側が書き出す）は **standard precision = 8-byte** の parquet。
- 16-byte wheel で 8-byte の本番 catalog を query すると、`PrecisionMismatch (expected 16, actual 8)` で **SIGABRT**（プロセス強制終了）。
- TTWR の真の本番ランタイムも `rebuild_nautilus_standard.sh`（`HIGH_PRECISION=false`）で組まれており **8-byte**。

owner 確定により pin を **8-byte standard** に訂正済み。issue #2 本文・ADR-0001（decision7 / 移行順序 step0）は訂正反映済み。

pin assert は 3 条件（`python/spike/s0_backtest.py` の `assert_pin` / `assert_footer_widths`、いずれも mismatch 時に `sys.exit(1)` する self-failing gate）:

1. `nautilus_pyo3.PRECISION_BYTES == 8`
2. nautilus バージョン == 1.226.0、Python == 3.13 系
3. 全 fixture parquet の OHLC 列が `fixed_size_binary[8]`（footer width チェック）

negative test（誤った build / catalog を渡す）で exit 1 になることも実証済み。

---

## 3. 再現手順

### 3.1 nautilus を 8-byte standard で再ビルド（venv）

```bash
# venv: /Users/sasac/backcast/python/.venv (uv 構成)
# nautilus_trader 1.226.0 を HIGH_PRECISION=false で sdist 再ビルド
HIGH_PRECISION=false uv pip install --no-binary nautilus_trader nautilus_trader==1.226.0
# 確認: PRECISION_BYTES=8 になっていること
python -c "import nautilus_trader.core.nautilus_pyo3 as n; print(n.PRECISION_BYTES)"   # -> 8
```

### 3.2 fixture を本番 catalog から stage

```bash
# 本番 catalog（8-byte parquet）から spike 用 fixture を rsync コピー（精度変換なし・idempotent）
# 対象シンボル: 8918 / 6740 / 3823、granularity: TSE-1-DAY-LAST-EXTERNAL
bash python/spike/stage_fixtures.sh
```

### 3.3 Python 層 headless gate（self-failing）

```bash
python python/spike/s0_backtest.py
```

pin / footer は mismatch で `sys.exit(1)` する。期待出力は §4.1。

### 3.4 Unity batchmode compile（C# harness のビルド健全性）

```bash
# Unity 6000.4.11f1
<Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast
# exit 0 / CS エラー 0 を確認（lead が 2 回実証済み）
```

### 3.5 Unity Mono 上での実 backtest（中核ゲート, headless）

```bash
<Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
        -executeMethod S0EditorProbe.Run
# exit code で PASS/FAIL を判定（§4.2）
```

---

## 4. 実測結果

### 4.1 Python 層（headless）

```
[S0 PIN OK] PRECISION_BYTES=8 | nautilus 1.226.0 | py 3.13
[S0 FOOTER OK] 3 parquet file(s), OHLC=fixed_size_binary[8]
[S0 BACKTEST OK] bars=204 | fills=0 | final_equity=10000000 JPY
```

- fixture = real 8-byte DAY parquet 3 本（8918 / 6740 / 3823、各 68 bars、計 **204 bars**）。
- fills=0 は spike の strategy が約定を出さない（経路疎通のみを見る）ため期待どおり。

### 4.2 Unity Mono 上の実 backtest（中核ゲート, headless probe）

```
[S0 PROBE PASS] bars=204 fills=0 equity=10000000 (nautilus loaded & backtest ran under Unity Mono)
exit 0
```

- ログ上で Unity Mono（`MonoBleedingEdge` / `unityjit-macos`）ランタイム配下で `nautilus_pyo3` の Rust core が load され、worker スレッド + `Py.GIL()` 経由で実 backtest が完走したことを確認。
- = **S0 で唯一の未知数（Nautilus が Unity Mono + pythonnet で load し実 backtest 完走するか）が Mac で GREEN**。

### 4.3 C# harness の設計（`Assets/Scripts/S0Spike/S0SpikeHarness.cs`）

- worker スレッドが `Py.GIL()` 内で実 backtest をループ実行。main スレッド（`Update`）は **GIL を一切取らず** フレームを描画継続。
- `Interlocked` / `Volatile` でフレーム数・run 数を授受。`TARGET_FRAMES = 300`。
- `OnDestroy`: worker.Join → `EndAllowThreads`（GIL 再取得）→ `Shutdown` で安全終了。
- `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` による auto-bootstrap = **Play を押すだけ**で起動。
- pythonnet 3.1.0（`Assets/Plugins/Python.Runtime.dll`）。明示 PyObject API のみ使用（`dynamic` 不使用）。

---

## 5. AC① 各項目の充足状況

| AC① 項目 | 状況 | 根拠 |
|---|---|---|
| pin assert（PRECISION_BYTES/version/footer 8-byte） | **GREEN** | `s0_backtest.py` self-failing gate（§4.1） |
| Unity Mono 上で threaded backtest 完走 | **GREEN** | headless probe `[S0 PROBE PASS]`（§4.2） |
| real 8-byte catalog の query | **GREEN** | bars=204、fixture=real catalog（§4.1） |
| main スレッドが GIL を取らない | **GREEN** | harness 設計 + probe 完走（§4.3） |
| ≥300 フレーム描画継続 | **GREEN** | playmode `S0 PASS: frames=1233`（owner 目視 2026-06-12、§7） |
| deadlock/crash/フレーム停止なし | **GREEN** | playmode で 1233+ フレーム連続描画・停止/crash なし（§7） |

---

## 6. 未消化・射程外

- **Windows 再走（#4 着手前の必須ゲート）**: deploy ターゲットは Windows。本結果は Mac (x86_64) leg のみで、`win_amd64` wheel を Windows-Mono で動かす経路は未証明。issue #2 / ADR-0001 の移行順序どおり、**Step2 (#4) 着手前に Windows で §3 を再走**すること。
- **viz zero-copy**: S0 射程外。#8 へ lift 済み。

---

## 7. owner playmode 目視ゲート手順

1. Unity Editor で `SampleScene` を開く。
2. **Play を押す**（auto-bootstrap 済みなので追加操作不要）。
3. Console で以下を確認:
   - `[S0] PythonEngine.Initialize OK; worker started; main proceeds GIL-free.`
   - （Python 層ログが出る構成なら）`[S0 PIN OK]` / `[S0 FOOTER OK]` / `[S0 BACKTEST OK]`
   - C# 側の進捗: `[S0] frame=… runs=… …`（フレームと backtest run が共に増えること）
   - 最終: `S0 PASS: frames=… runs=… …`（frames ≥ 300）
4. フレームカウンタが滑らかに増え続け、**deadlock / フレーム停止 / crash が無い**ことを目視。

PASS なら AC① の描画継続項目が満たされる。

---

## 8. ADR-0001 昇格の扱い

issue #2 は「**AC① 合格（Windows で）→ ADR-0001 を accepted へ昇格**」と規定している。

→ **現時点では昇格しない（ADR-0001 status: proposed 維持）。**

- Mac leg の中核ゲート GREEN + owner playmode PASS は **#3 着手を許可**するが、accepted 昇格の条件は **Windows 再走 PASS**（§6）であり未消化。
- owner が別途の判断で前倒し昇格を選ぶ場合はその判断に従う。

---

## 付録: 主要成果物

- `python/spike/s0_backtest.py` — pin / footer self-failing gate + 実 backtest
- `python/spike/stage_fixtures.sh` — 本番 catalog → fixture stage
- `Assets/Scripts/S0Spike/S0SpikeHarness.cs` — playmode harness（worker=Py.GIL / main=GIL-free）
- `Assets/Editor/S0EditorProbe.cs` — headless 中核ゲート（`-executeMethod S0EditorProbe.Run`）
- `Assets/Plugins/Python.Runtime.dll` — pythonnet 3.1.0
