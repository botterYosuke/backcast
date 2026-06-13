# Replay chart Findings: Python engine → C# → uGUI candle render の継ぎ目を実証

- Issue: #10 (Replay chart — bar payload decode → candlestick render)
- 関連 ADR（方針として参照）: [ADR-0001 — Unity + pythonnet embedded frontend](../adr/0001-unity-pythonnet-embedded-frontend.md)（status: **proposed**）, [ADR-0002 — embedded Python runtime placement & resolution](../adr/0002-embedded-python-runtime-placement-and-resolution.md)（status: **accepted**）, [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（status: **accepted**）
- 配置の根拠: ADR-0002 self-protection ルール（slice 内で確定する下位事実は ADR に書き戻さず `docs/findings/` に記録し ADR を方針として参照）。本ファイルは #10 で確定した下位事実の記録であり、ADR の方針を変更するものではない。
- **本 slice は新規 ADR を起こさない**（§7）。decoder の API（`ReplayBarDecoder.Decode`）はパーサを内部で差し替え可能に保つ＝「reverse しづらい決定」の基準を満たさないため、ADR 化しない。
- 実行環境: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1（standalone=Mono）/ pythonnet 3.1.0 / CPython 3.13.13 / nautilus_trader 1.226.0（`HIGH_PRECISION=false`, 8-byte）/ venv `python/.venv`

---

## 1. 判定サマリ

**Replay chart の 3 マイルストーン（M1/M2/M3）は Mac leg で達成。** M1（durable decoder + model）と M2（AFK 回帰ゲート）は GREEN+VERIFIED。M3（throwaway turnkey playmode widget）は **0 CS エラーでコンパイル確認済み + HITL playmode ゲート owner 承認済み**（`REPLAY CHART PASS: frames=2548 bars=68`、§4）。

本 slice が担うリスク＝「S1 がクローズした `push_bar`→`ReplayEventSink` の seam に積まれた **bar payload（JSON）を C# 側で decode し、cumulative OHLC を実値で取り出して uGUI candle として描画できるか」**。S1 は payload を文字列として drain・parse 疎通させただけ（`parsed_ok`）で、OHLC 値の **構造的な束縛**（JsonUtility の silent zero-fill を排した実値 bind）は証明していなかった。これを M2 の AFK ゲートで構造的に固定し、M3 で実 playmode 描画に載せた。

- **M1**（DONE+VERIFIED）: `ReplayBarDecoder.Decode` を durable な decode API として導入。`JsonUtility` を `Decode` の内側に隠蔽。
- **M2**（DONE+VERIFIED）: AFK 回帰ゲート `ReplayChartDecodeProbe`。`decoded=68 finalOhlcPoints=68`、OHLC 不変条件を構造 assert（POINT-A、JsonUtility の zero-fill 偽 green を kill）。
- **M3**（DONE+VERIFIED）: throwaway turnkey MonoBehaviour `ReplayChartHarness`。自前 Canvas + uGUI candle で latest cumulative frame を描画。`Application.isBatchMode` ガードで headless コンパイルは 0 CS エラー。owner playmode 目視ゲート PASS（`REPLAY CHART PASS: frames=2548 bars=68`）。

**二段ゲート設計（本 slice の固有点）**: S1 は headless-only、S0 は playmode-only だったが、Replay chart は **AFK（decode 回帰）＋ HITL（playmode 描画）の両ゲート**を持つ。AFK は CI で decode 契約を守り、HITL は実描画を owner が目視確認する。

**これは Mac leg の結果。** Windows leg は findings/0001 §6 から継続して未消化（§6）。ADR-0001 は `proposed` 維持（§7）。

これにより **#11 panels（`get_state_json` poll 経路）と #5/#7（multi-instrument chart）は着手可**。両者は本 slice の `ReplayBarDecoder` と、S1 由来の `push_bar`→`ReplayEventSink` seam をそのまま再利用する（§付録）。

---

## 2. M1 — durable decoder + model（`Assets/Scripts/ReplayChart/ReplayBarDecoder.cs`）

push_bar payload を decode する durable な値モデルと API:

```csharp
public struct OhlcPoint
{
    public long   open_time_ms;
    public double open, high, low, close, volume;
}

public struct ReplayBarFrame
{
    public double Price;
    public long   TimestampMs;
    public IReadOnlyList<OhlcPoint> Ohlc;
}

public static ReplayBarFrame ReplayBarDecoder.Decode(string json);
```

- `JsonUtility` は `Decode` の内側に**隠蔽**（private DTO が `price` / `timestamp_ms` / `ohlc_points` のみを VERBATIM JSON キーで宣言）。未宣言の payload キー（`history`, `per_instrument`）は silently skip。
- Newtonsoft は manifest 未追加のまま（導入しない＝settled）。
- `ohlc_points` は **cumulative**（各 bar が先頭からの全系列を運ぶ／history cap なし）— M2 で `finalOhlcPoints=68` = 全系列として確認（§3）。

---

## 3. M2 — AFK 回帰ゲート（`Assets/Editor/ReplayChartDecodeProbe.cs`）

S1 の seam（`push_bar`→`ReplayEventSink`）の **下流** を固定。司令塔が `-executeMethod ReplayChartDecodeProbe.Run` を Unity batchmode で実行した最終 run（VERBATIM）:

```
[REPLAY CHART DECODE] orphan-free OK: 'backtest-runner' is a daemon thread (dies with the process; cannot block Unity exit). threads=[MainThread(NON-daemon), backtest-runner(daemon)]
[REPLAY CHART DECODE PASS] decoded=68 finalOhlcPoints=68 (ReplayBarDecoder.Decode bound real OHLC values under Unity Mono)
exit=0
```

**POINT-A — JsonUtility の silent zero-fill 偽 green を kill する構造 assert**:

- `decoded == 68 == pushed`（pushed=0 / decoded≠pushed は FAIL）。
- 各フレームの `ohlc_points` 非空 + last `close > 0` + `open_time_ms > 0` + OHLC 整合（`high >= max(open,close)`, `low <= min(open,close)`）。
- cross-frame で `open_time_ms` 非減少。
- final-frame deep check: 全 68 点の `open/high/low/close > 0`、OHLC 整合、`open_time_ms` 単調。

これにより `JsonUtility` がキー不一致で全フィールドを 0 埋めしても `decoded=68` の見かけ green を作れない（実値 bind を構造で要求）。

**orphan-free gate（S1 から継承、無償の回帰被覆）**: backtest-runner が Python daemon スレッドであることを assert（`daemon=False` なら FAIL）。ADR-0001 d3（同一プロセス内 in-proc orphan-freedom）を probe レベルで担保。

---

## 4. M3 — throwaway turnkey playmode widget（`Assets/Scripts/ReplayChart/ReplayChartHarness.cs`）

使い捨ての turnkey MonoBehaviour。M2 の launcher / cfg / `ReplayEventSink` を再利用し、playmode で実描画する:

- **auto-bootstrap**（`Application.isBatchMode` ガード ⇒ headless コンパイルは 0 CS エラー）が自前の runtime Canvas + uGUI candle `Image` rect を構築（x=`open_time_ms`、body=`open`/`close`、wick=`high`/`low`、autoscale）。
- `Update` で GIL-free に drain、latest cumulative frame を描画。
- 50 フレームごとに `[REPLAY CHART] frame=N bars=M` をログ。
- PASS 条件 `frames >= 300 (TARGET_FRAMES) && sink.Completed && renderedCount > 0` を満たした時点で **一度だけ** `REPLAY CHART PASS: frames={N} bars={M}` をログ（`bars` は cumulative ⇒ 期待値 68）。
- `OnDestroy` は意図的に `PythonEngine.Shutdown()` を**呼ばない**（live な daemon スレッドに対し main で GIL 再取得すると deadlock しうる）。static guard が repeat Play 間で interpreter を再利用（再 Initialize しない）。

**コンパイル検証**: 司令塔が Unity batchmode `-quit` でコンパイル = **0 CS エラー**。

**HITL playmode ゲート（owner 承認済み 2026-06）— 手順と実測**:

1. Unity でプロジェクトを開く。
2. Play を押す（S0 衝突解消後、playmode で auto-bootstrap するのは ReplayChart のみ — §5）。
3. candle が前進し、Console 最終的に:

```
REPLAY CHART PASS: frames=2548 bars=68
```

owner 目視 PASS（2026-06）: candle が 68 本まで順次前進、worker の backtest 実行中も main は滑らかに描画継続（frame 2650+ まで停止/crash なし）。

**観測（健全・記録のため）**:
- `bars=0` が frame ~1700 まで継続し、その後 68 まで増加。これは初回 `nautilus` import が重く（main は GIL-free に ~1700 フレーム描画継続）、import 完了後に 68 bars が 0.1s/bar で流入したため。S0 の「初回ロードが重く main が先に多数フレームを描画」挙動と一致（= main が GIL に一切ブロックされていない証拠）。
- Console の `Can't Generate Mesh, No Font Asset has been assigned.`（`UnityEditor.HandleUtility:BeginHandles` 由来）は **editor scene-view の handle 描画**の警告でチャート無関係・無害（本 widget は `Image` rect のみで Font/Text を使わない）。

---

## 5. S0 衝突解消（owner 適用済み）

playmode に auto-bootstrap する MonoBehaviour が S0 と ReplayChart で衝突する問題を解消:

- `Assets/Scripts/S0Spike/S0SpikeHarness.cs` に `const bool AutoBootstrapEnabled = false` + 早期 return ガードを追加（`if (!AutoBootstrapEnabled) return;`）。
- 文書化: Replay chart #10 が default Play ゲートとして S0 を **supersede**。**削除はしない** — #2 の Windows leg が未消化で、`>=300fps` アサーションはこの playmode harness（`TARGET_FRAMES = 300`）に存在する。Windows 再走時は bool を `true` に戻して resurrect。
- `S0EditorProbe` は無変更。
- 結果: 実 playmode では ReplayChart のみが bootstrap。この編集後もコンパイル 0 CS エラー確認済み。

---

## 6. 再現手順

```bash
# AFK decode 回帰ゲート（self-failing）
<Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
        -executeMethod ReplayChartDecodeProbe.Run -logFile <log>
# 期待: [REPLAY CHART DECODE PASS] decoded=68 finalOhlcPoints=68 / exit=0
```

```text
# HITL playmode 描画ゲート
プロジェクトを開く → Play → candle が前進 → Console:
  REPLAY CHART PASS: frames>=300 bars=68
```

Fixtures（S1 と同一）:
- `python/spike/fixtures/strategies/spike_bar_consumer.py`（no-trade bar counter）
- jquants-catalog 8918.TSE 1-DAY 2024-10-01..2025-01-10（68 bars, 8-byte parquet）

---

## 7. 未消化・射程外

- **Windows leg（#2 / findings/0001 §6 から継承）**: Mac-green は `win_amd64` wheel を Windows-Mono で動かす経路を未証明。S0 の `>=300fps` アサーションは `S0SpikeHarness`（`AutoBootstrapEnabled=false`）に温存され、Windows 再走時に bool を戻して resurrect する。
- ~~HITL playmode ゲート owner 承認~~: **解消済み** — owner 目視 PASS（`REPLAY CHART PASS: frames=2548 bars=68`、§4）。
- **multi-instrument**: 本 slice は single-instrument（`ReplayBarFrame`）のみ。`per_instrument` payload は silently skip。multi は #5/#7（POINT-B、§8）。
- **shippable standalone build**: ADR-0002 の BUILD 分岐（venv → StreamingAssets）は #2 Windows leg の下流で未実行。
- **射程外**: panels（#11）、layout persistence（#12）は本 slice に含まない。

---

## 8. ADR の扱い + POINT-B swap-point

- **新規 ADR を起こさない** — `ReplayBarDecoder.Decode` はパーサを内部で差し替え可能に保つため、「reverse しづらい決定」の基準を満たさない。ADR-0001/0002/0003 を方針として参照するに留める。
- **ADR-0001 は `proposed` 維持** — accepted 昇格条件は Windows 再走 PASS（#2 / `docs/spike/s0-result.md` §8）。本ファイルは Mac leg のみで昇格を主張しない。

**POINT-B — 後続 slice（#5/#7）への正確な swap-point 指示**:

- **parser = internal swap**（`JsonUtility` → Newtonsoft）。`Decode` API は不変。これは純粋な内部差し替え。
- **multi-instrument `per_instrument` = ADDITIVE API extension**。新 API（例: `DecodeMulti` → `IReadOnlyDictionary<string, ReplayBarFrame>`）を**追加**する。既存の single-instrument `ReplayBarFrame` API は据え置き。**これは内部差し替えではない** — multi を「Bevy 風の再解釈」や「internal only」と誤認しないこと。

---

## 付録: 主要成果物（pin / artifact table）

| 区分 | 成果物 | 役割 | durability |
|---|---|---|---|
| decoder + model | `Assets/Scripts/ReplayChart/ReplayBarDecoder.cs` | push_bar payload → `ReplayBarFrame`（cumulative OHLC、JsonUtility を `Decode` に隠蔽） | **durable** |
| AFK 回帰ゲート | `Assets/Editor/ReplayChartDecodeProbe.cs` | headless decode ゲート（`-executeMethod ReplayChartDecodeProbe.Run`）+ POINT-A OHLC 構造 assert + orphan-free | durable（regression gate） |
| HITL playmode widget | `Assets/Scripts/ReplayChart/ReplayChartHarness.cs` | turnkey MonoBehaviour、自前 Canvas + uGUI candle 描画、`REPLAY CHART PASS: frames>=300 bars=68` | throwaway |
| S0 衝突ガード | `Assets/Scripts/S0Spike/S0SpikeHarness.cs` | `AutoBootstrapEnabled=false` + 早期 return（Windows leg 用に温存、削除せず） | — |
| reused seam（S1） | `Assets/Scripts/S1Spike/ReplayEventSink.cs` | `push_bar` を受け GIL-free な `ConcurrentQueue<string>` に積む（#5/#7/#11 が再利用） | durable |
| fixtures | `python/spike/fixtures/strategies/spike_bar_consumer.py` + jquants-catalog 8918.TSE Daily（68 bars） | no-trade bar counter + 8-byte parquet | — |
