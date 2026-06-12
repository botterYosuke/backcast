# S1 Findings: Replay seam tracer — Python engine → C# push_bar の継ぎ目を実証

- Issue: #9 (S1 — replay seam tracer)
- 関連 ADR: [ADR-0001 — Unity + pythonnet embedded frontend](../adr/0001-unity-pythonnet-embedded-frontend.md)（status: **proposed**）, [ADR-0002 — embedded Python runtime placement & resolution](../adr/0002-embedded-python-runtime-placement-and-resolution.md)（status: **accepted**）, [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（status: **accepted**）
- 配置の根拠: ADR-0002 self-protection ルール（slice 内で確定する下位事実は ADR に書き戻さず `docs/findings/` に記録し ADR を方針として参照）。本ファイルは S1 で確定した下位事実の記録であり、ADR の方針を変更するものではない。
- 実行環境: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1（standalone=Mono）/ pythonnet 3.1.0（`Assets/Plugins/Python.Runtime.dll`）/ CPython 3.13.13（uv install）/ nautilus_trader 1.226.0（`HIGH_PRECISION=false`, 8-byte）/ venv `python/.venv`

---

## 1. 判定サマリ

**S1 の 3 マイルストーン（M1/M2/M3）は Mac leg で全て GREEN。** S1 が tracer として担う本質的リスク＝「Python worker（`Py.GIL()` 保持の daemon backtest スレッド）が bar ごとに **C# オブジェクトの `push_bar` を Unity Mono 上で呼び**、その bar を GIL-free な `ConcurrentQueue<string>` に積んで main 側で drain・parse できるか」を実証済み。これは S0 が証明していない継ぎ目（Python→C# の per-bar push コールバック）であり、S1 でクローズした。

- **M1**: TTWR の `python/engine` を backcast に COPY 移植（105 `.py`、import root `engine`）。`backcast-engine` パッケージ（hatchling、`packages=["engine"]`、9 deps）として `python/.venv` に editable install。
- **M2**: adapter smoke が CPython gate と Unity Mono gate の両方で GREEN（`pushed=68 drained=68 parsed_ok`）。
- **M3**: `PythonRuntimeLocator` を単一リゾルバとして導入（ADR-0002 の Editor/Build 分岐を実体化）。probe をその上に載せ替えて再走し依然 GREEN。orphan-free gate を probe に追加。

これにより **#10（chart UI）は着手可**。#10 は同じ `push_bar` → `ReplayEventSink` 経路を消費するため、本 tracer がその下流をアンブロックする。

**ただし ADR-0001 は `proposed` 維持。** accepted 昇格条件は Windows 再走 PASS（#2 / `docs/spike/s0-result.md` §8）であり、S1 は Mac leg のみ。本ファイルは ADR 昇格を主張しない（§6）。

---

## 2. M1 — engine 移植（COPY）

- TTWR の `python/engine` → backcast `python/engine` を COPY 移植（105 `.py`、import root は `engine`）。
- `python/pyproject.toml` = `backcast-engine`（hatchling、`packages=["engine"]`、9 deps）。`python/.venv` に editable install（+ `httpx` / `pydantic` / `websockets`）。
- 検証: `import engine, engine.core, engine.inproc_server` OK / nautilus 1.226.0 / `PRECISION_BYTES == 8` / PIN OK。
- `uv.lock` は未変更（reconciliation は deferred、§6）。editable install はこれと独立に成立。

---

## 3. M2 — adapter smoke（CPython gate + Unity Mono gate）

S1 の中核継ぎ目。両 gate とも `pushed=68 drained=68 parsed_ok`。

### 3.1 CPython gate（`python/spike/s1_adapter_smoke.py`）

engine の replay 経路 + 5-method push 契約が CPython で疎通することを証明:

```
[ADAPTER SMOKE PASS] pushed=68 drained=68 parsed_ok
exit 0
```

### 3.2 Unity Mono headless gate（`Assets/Editor/S1AdapterSmokeProbe.cs` + C# sink `Assets/Scripts/S1Spike/ReplayEventSink.cs`）

S0 が証明していない継ぎ目をクローズ — Python worker（`Py.GIL()` 保持、daemon backtest スレッド）が **bar ごとに C# オブジェクトの `push_bar` を Mono 上で呼び**、68 bars を GIL-free な `ConcurrentQueue<string>` に積み、main 側で GIL-free に drain・parse:

```
[ADAPTER SMOKE PASS] pushed=68 drained=68 parsed_ok (Python->C# push_bar callback ran under Unity Mono)
exit 0
```

CS エラー 0。

---

## 4. M3 — PythonRuntimeLocator（ADR-0002 の実体化）+ orphan-free gate

### 4.1 単一リゾルバ化（`Assets/Scripts/S1Spike/PythonRuntimeLocator.cs`）

- 単一の Python ランタイム・リゾルバ。`Application.isEditor` で分岐:
  - **Editor**: 派生 `<repo>/python` + `python/.venv`。
  - **Build**: `StreamingAssetsPath`（authored だが deferred — ADR-0002）。uv CPython は documented const のまま保持。
- probe をこのリゾルバ上に載せ替えて再走し、依然 `[ADAPTER SMOKE PASS] pushed=68 drained=68 parsed_ok` exit 0。Editor 分岐のパス・パリティを実証し、S0 のハードコード債を実体ある抽象として retire。

### 4.2 orphan-free gate（probe に追加）

backtest-runner が Python daemon スレッドであり、worker 生存中は GIL を保持していることを assert（`daemon=False` なら exit 1）。これは ADR-0001 d3（同一プロセス内でライフタイムが自動 — Unity が死ねば Python も即死、orphan は構造的に存在しない）を probe レベルで担保する。

最終 run ログ（VERBATIM）:

```
[ADAPTER SMOKE] orphan-free OK: 'backtest-runner' is a daemon thread (dies with the process; cannot block Unity exit). threads=[MainThread(NON-daemon), backtest-runner(daemon)]
[ADAPTER SMOKE PASS] pushed=68 drained=68 parsed_ok (Python->C# push_bar callback ran under Unity Mono)
[ADAPTER SMOKE] skipping Python shutdown (daemon lifecycle nondeterministic; process exits next)
exit=0
```

### 4.3 orphan-free クロージャの ADR 根拠

- **ADR-0001 d3（構造的 in-proc orphan-freedom）**: ライフタイムは同一プロセスで自動。Unity が死ねば Python も即死し、orphan プロセスは構造的に存在しない。
- **ADR-0001 d6（Replay では graceful-stop は safety-critical でない）**: Replay は実発注を伴わないため、プロセス終了時の graceful 窓欠落は受容可能。daemon スレッドが Unity exit をブロックせず即死する設計はこの方針と整合する。

---

## 5. 再現手順

```bash
# CPython gate（self-failing）
python/.venv/bin/python python/spike/s1_adapter_smoke.py

# Unity Mono gate（headless）
<Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
        -executeMethod S1AdapterSmokeProbe.Run -logFile <log>
```

Fixtures:
- `python/spike/fixtures/jquants-catalog`（8918 / 6740 / 3823 の TSE-1-DAY、8-byte parquet）
- `python/spike/fixtures/strategies/spike_bar_consumer.py`（no-trade bar counter）

---

## 6. 未消化・射程外（remaining gates）

- **Windows leg（#2 から継続）**: Mac-green は `win_amd64` wheel を Windows-Mono で動かす経路を未証明。
- **Shippable standalone build 検証**: build post-process での venv → StreamingAssets コピー + 実 exe 起動（ADR-0002）。#2 の Windows leg の下流。リゾルバの BUILD 分岐は authored だが **未実行**。
- **`uv.lock` reconciliation**: 依然 `backcast-spike` を記述。deferred（editable install は独立に成立）。
- **Real-catalog（Synology `ARTIFACTS_PATH`）配線**: spike は S0 fixtures のみ使用。
- **射程外（S1 は seam tracer のみ）**: chart UI（#10）、panels（#11）、layout persistence（#12）は S1 に含まない。

---

## 7. ADR 昇格の扱い

- **ADR-0001 は `proposed` 維持** — accepted 昇格条件は Windows 再走 PASS（#2 / `docs/spike/s0-result.md` §8）であり、S1 は Mac leg のみ。本ファイルは ADR-0001 昇格を主張しない。
- ADR-0002 / ADR-0003 は `accepted`（本グリルで authored）。

---

## 付録: 主要成果物（pin / artifact table）

| 区分 | 成果物 | 役割 |
|---|---|---|
| engine package | `python/engine` + `python/pyproject.toml`（`backcast-engine`, hatchling, `packages=["engine"]`, 9 deps） | TTWR engine の COPY 移植（import root `engine`）、editable install |
| CPython gate | `python/spike/s1_adapter_smoke.py` | engine replay 経路 + 5-method push 契約の CPython 疎通 |
| C# sink | `Assets/Scripts/S1Spike/ReplayEventSink.cs` | `push_bar` を受け GIL-free な `ConcurrentQueue<string>` に積む |
| Mono probe | `Assets/Editor/S1AdapterSmokeProbe.cs` | headless gate（`-executeMethod S1AdapterSmokeProbe.Run`）+ orphan-free assert |
| runtime locator | `Assets/Scripts/S1Spike/PythonRuntimeLocator.cs` | 単一リゾルバ（ADR-0002 の Editor/Build 分岐を実体化） |
| pythonnet | `Assets/Plugins/Python.Runtime.dll` | pythonnet 3.1.0 |
| fixtures | `python/spike/fixtures/jquants-catalog`, `python/spike/fixtures/strategies/spike_bar_consumer.py` | 8918/6740/3823 TSE-1-DAY 8-byte parquet（各 68 bars）+ no-trade bar counter |
