# S9a Findings: orderbook 板 (bid/ask ladder) を C# が get_state_json からデコード・描画

- Issue: #26（S9a — orderbook 先行 / mock depth・実 venue 不要・AFK GREEN）
- Parent: #17（S9 orderbook）／ Epic #1。方針: [ADR-0001 — Unity + pythonnet embedded frontend](../adr/0001-unity-pythonnet-embedded-frontend.md)（status: **proposed**）
- 関連 findings: [0011 — live adapter tracer](./0011-live-adapter-tracer.md)（get_state_json poll seam・DepthCache 由来 bid/ask の元）
- 配置の根拠: ADR self-protection ルール（slice 内で確定する下位事実は ADR に書き戻さず本ファイルに記録し、ADR を方針として参照）。**backcast には `tests/e2e/FLOWS.md` が無いため、behavior-to-e2e の flow 正本は本 findings**（TTWR 側 Bevy リポジトリの FLOWS.md とは別系）。

---

## 1. 判定サマリ

#26 は #17（orderbook）のうち **実 venue を要しない vertical slice**。データ契約 `DepthSnapshot`
（`engine.models`）・mock 供給源 `mock_adapter.emit_depth_snapshot()`・`DepthCache`・`get_state_json()` への
depth 合成（[_backend_impl.py](../../python/engine/_backend_impl.py) `get_state_json` line 661–674）はすべて **既存**。
本 slice の新規は **C# 側のみ**: dict-keyed な `per_instrument[id].depth` を durable にデコードし、
bid/ask ladder として描画・リアルタイム更新する。

成果物（#10/#11 の 3-tier を踏襲）:

| tier | file | 役割 |
| --- | --- | --- |
| durable | [`Assets/Scripts/Live/DepthDecoder.cs`](../../Assets/Scripts/Live/DepthDecoder.cs) | get_state_json → `DepthSnapshotView`（bid/ask ladder）の唯一のデコード入口 |
| AFK gate | [`Assets/Editor/DepthDecodeProbe.cs`](../../Assets/Editor/DepthDecodeProbe.cs) | characterization（純文字列 10 fixture）＋ realtime E2E（in-proc MOCK・連続 emit 全置換 assert） |
| throwaway HITL | [`Assets/Scripts/LiveSpike/DepthLadderHitlHarness.cs`](../../Assets/Scripts/LiveSpike/DepthLadderHitlHarness.cs) + [`Assets/Editor/DepthLadderHitlMenu.cs`](../../Assets/Editor/DepthLadderHitlMenu.cs) | owner が実 Canvas 上で ladder の配置・色・更新挙動を目視 |
| 注入 helper | [`python/spike/live_adapter/mock_inject.py`](../../python/spike/live_adapter/mock_inject.py) `emit_depth_ladder` | multi-level 非対称な板を CSV 引数で注入（`emit_depth` 単段版の throwaway 拡張） |

## 2. 中核の設計判断（grill-with-docs 2026-06-14）

### 2.1 dict-keyed depth のデコード = ハイブリッド（構造認識 locator → JsonUtility）

`get_state_json()` は `TradingState.model_dump_json()`。その `per_instrument` は **instrument-id キーの dict**
（`{"8918.TSE": {price, ohlc_points, depth}, ...}`）であり、**Unity の `JsonUtility` は dict をモデル化できない**
（[ReplayBarDecoder.cs](../../Assets/Scripts/ReplayChart/ReplayBarDecoder.cs):29 が明言。`per_instrument` が durable に
デコードされてこなかった理由）。

採用方針は [LiveBackendEventDecoder.PeelTag](../../Assets/Scripts/Live/LiveBackendEventDecoder.cs) と同型の **ハイブリッド**:
小さな構造認識 locator が動的な外殻（`per_instrument` → 目的 id member → その `depth` 値）だけを剥がし、
**固定形の depth オブジェクト部分文字列**を `JsonUtility` に渡す（`bids`/`asks` の配列 of オブジェクトは
`JsonUtility` が native に扱える）。

- **却下: Newtonsoft 導入** — manifest 不在。ReplayBar/Panel decoder が「premature だから入れない」と明記しており、
  この slice 1 本のための widening は過剰。
- **却下: Python に focused accessor（`get_depth_json(id)`）追加** — #26 は「GetState の既存 payload を C# でデコード」
  と明記。契約変更になり、poll が二本（GIL 呼び増）になる。
- locator は **naive `IndexOf` ではない**: JSON 文字列・エスケープ・brace nesting を認識する value-span scanner。
  これにより `live_last_error` 等の **文字列値の中に現れる `"per_instrument"`/id/`"depth"` の decoy** に騙されない
  （characterization F7/F8 でガード）。既存の throwaway `DepthTop()`（`IndexOf` で top price のみ）は durable に昇格させない。

### 2.2 順序は忠実パススルー（defensive sort しない）

`DepthSnapshot` の「bids 降順 / asks 昇順」は **producer 側の『想定』契約**であり、`engine.models` も `DepthCache` も
sort を強制しない。decoder は **wire 順を忠実復元**し、ladder も受信順で描く（asks の表示上の反転は presentation の
都合であって decode の re-sort ではない）。defensive sort は producer 契約違反を UI に隠してしまうため不採用。
順序の検証は characterization 側で「mock の emit が実際に降順/昇順か」を assert する責務に置く。

### 2.3 contract（#10/#11 踏襲）

- `null` / 空 / whitespace / `"null"` stateJson → `Empty`（`HasDepth=false`、no throw）
- `per_instrument` 不在 / 目的 instrument 不在 → `Empty`
- instrument はあるが `depth` 不在 / `depth:null`（**Replay**）→ `Empty`（板は非表示）
- depth オブジェクトあり → `HasDepth=true`、ladder マップ（**空板も `HasDepth=true`**: 「板が無い」≠「両側空の板」）
- navigate 中に **malformed JSON** → 握り潰さず `FormatException`（grounded payload は常に valid な model_dump_json 出力なので、
  構造破綻は surface すべき実バグ。ReplayBar/Panel decoder の規律と同じ）

## 3. behavior-to-e2e flow（release-gate 項目）

> **FLOW-S9a-1: mock depth → bid/ask ladder がリアルタイム更新される**

| 観点 | 内容 |
| --- | --- |
| 保証したい挙動 | mock venue が流す板が、Unity の bid/ask ladder として end-to-end で描画され、**連続 emit で全置換更新**される |
| seam | `emit_depth_snapshot` → bus → `DepthCache` → `get_state_json()`（`per_instrument[id].depth`）→ `DepthDecoder.Decode` → ladder view |
| 自動 gate | `DepthDecodeProbe.Run`（AFK, `-batchmode -executeMethod`）。Phase B が in-proc MOCK live で **gen1（2 bids / 2 asks）→ gen2（1 bid / 3 asks）** の異形2世代を emit し、gen2 の値が乗る AND gen1 の旧 top（10.0）が **消滅**することを値で assert（「描画を見た」ではなく全置換を検証） |
| characterization | `DepthDecodeProbe` Phase A の純文字列 10 fixture: F1 多段非対称（降順/昇順・size）, F2 空板, F3 片側欠, F4 `depth:null`(Replay), F5 instrument 不在, F6 per_instrument 不在, F7 文字列値 locator decoy, F8 substring-key decoy, F9 malformed→throw, F10 退化入力 |
| 目視 gate | `Tools > Backcast > Depth Ladder HITL`（Play 中）。5x5 の板が mid drift で毎 tick 更新されるのを owner が確認（実 Canvas の配置・色・可読性） |
| Replay 保証 | Replay では `depth=None` → ladder は空/非表示（F4 + HITL の「no board」表示） |

## 4. 既知の制約 / TODO

- 実フィード（kabu/立花）adapter での depth は本 slice 対象外（後続 slice）。本 decoder は venue 非依存
  （Python 側で `DepthUpdate` に正規化済み）なので、実 venue でも同一の C# 経路で乗る想定。
- ladder の描画は throwaway HITL の OnGUI（uGUI 即時モード）。本線 UI（floating window / hakoniwa 上のパネル化）への
  載せ替えは mainline scene/DI が来る slice で行う（#11 と同じ deferral）。
- 本 findings は ADR 昇格を主張しない（ADR-0001 は `proposed` 維持）。

## 5. 検証ステータス（Phase A + Phase B AFK GREEN — Mac leg）

**Phase A（characterization）＋ Phase B（realtime 全置換）の AFK 権威ゲートは Mac leg で GREEN**。HITL 目視（実 GPU 描画）のみ owner の手動 leg として残る。当初は Windows-Mono の live-path crash でブロックされていた（下記履歴）が、issue comment が示したクローズ条件「(a) Mac leg で実走」を満たして解消した。

### 実証結果（2026-06-14・mock venue・Mac leg・Unity 6000.4.11f1）

CPython 疎通 gate（任意・GREEN）— live 経路（DataEngine + InprocLiveServer(MOCK) facade → register/login/set_execution_mode(LiveAuto)/start → `emit_depth_ladder` gen1→gen2 → `get_state_json`）を純 CPython で先行検証し、Mac では Windows-Mono crash が起きないことを確認:
```
GEN1 depth: bids=[10.0,9.9] asks=[10.1,10.2]
GEN2 depth: bids=[11.0]     asks=[11.1,11.2,11.3]  (old 10.0 gone)
[DEPTH SMOKE CPYTHON PASS] gen1->gen2 full replacement, old 10.0 gone
```

AFK 権威ゲート（GREEN）:
```
<Unity 6000.4.11f1> -batchmode -nographics -projectPath /Users/sasac/backcast \
    -executeMethod DepthDecodeProbe.Run -logFile /tmp/depth_decode_probe.log
→ UNITY_EXIT=0
```
VERBATIM（log）:
```
[DEPTH DECODE MARK] characterization PASS (12 fixtures)
[DEPTH DECODE MARK] python initialized; main GIL-free; starting drive + poll
[DEPTH DECODE PASS] gen1(bids=2 top=10/100 asks=2 top=10.1/150) -> gen2(bids=1 top=11/50 asks=3 last=11.3/80) full-replacement (old 10.0 gone) maxStall=21ms — decoder restored dict-keyed depth ladder from get_state_json
[DEPTH DECODE MARK] PythonEngine.Shutdown OK
```
判定: `UNITY_EXIT=0` + 上記 PASS + `grep -c "error CS"`=0 + 例外 0 + clean Shutdown（`PythonEngine.Shutdown OK`）+ 新規 crash dump 無し。main heartbeat maxStall=21ms（< 200ms）で GIL-free を維持。`LiveAdapterTracerProbe`（#20）と同じく **Mac leg では live teardown crash 無し**（findings 0011 と整合）。

> **履歴（Windows-Mono ブロック・解消済み）**: 旧 owner マシン（Windows・Unity `6000.5.0b11`）では、既定 install `6000.4.11f1` が UPM server バイナリ欠落で起動不可だったため別 Editor で Phase A のみ GREEN を採取、Phase B は live orchestrator のログイン後 bg component 起動で `asyncio.run_coroutine_threadsafe(...).result()` → TimeoutError → `ucrtbase recalloc` native crash（S2-spike #7 が核の未知数とした cross-thread asyncio marshal の GIL 往復が Unity-Mono/Windows で starve＝ADR-0004 / #24 kernel の動機）でブロックされていた。#26 の decode/描画ロジックの欠陥ではなく live 起動段の OS-mono 事象であり、Mac leg 実走で回避した。

**クローズ条件:**
- [x] **Phase A（characterization 12 fixture）GREEN — Mac leg 実機実証済み**（`6000.4.11f1` batchmode `DepthDecodeProbe.Run` の前段。`[DEPTH DECODE MARK] characterization PASS (12 fixtures)`）。durable `DepthDecoder` の全分岐（順序忠実・空板・片側欠・null・decoy・malformed-throw・F12 scrambled 無 re-sort）と **slice 全体の compile**（`error CS`=0＝`Assembly-CSharp`＋`Assembly-CSharp-Editor` clean）を確定。
- [x] `python/.venv`（Mac・nautilus 1.226.0・Python 3.13.11）ステージ済み（engine/spike/nautilus import OK）。`PythonRuntimeLocator` Mac leg は uv CPython 3.13.13 dylib を dlopen（patch 差は同 minor で ABI 互換）。
- [x] **Phase B（gen1→gen2 全置換）GREEN — Mac leg 実機実証済み**。`DepthDecodeProbe.Run` を Unity `6000.4.11f1` batchmode で実走 → `UNITY_EXIT=0`・`[DEPTH DECODE PASS] ... full-replacement (old 10.0 gone) maxStall=21ms`・`PythonEngine.Shutdown OK`（上記 §5 実証結果に VERBATIM）。Windows-Mono の live-path crash は Mac leg で回避（履歴は上記引用ブロック）。
- [ ] `Tools > Backcast > Depth Ladder HITL` 目視（owner の手動 playmode leg） — 実 Canvas 上で 5×5 ladder が mid drift で毎 tick 更新されるのを目視。AFK ゲート（decode + realtime 全置換）が値で確定済みのため、残るのは実 GPU 描画の配置・色・可読性の視認のみ。findings 0011 と同じく owner playmode は default-disabled の分離 leg。
- [x] 静的レビュー: 独立 reviewer 1 周完了（Medium 2 件＝truncation 例外型／teardown poll-close race を修正・順序契約の未被覆を F12 scrambled fixture で補強）。Python/Unity 境界・poll teardown・realtime 全置換は **Phase B（Mac leg）実走で確定済み**。
