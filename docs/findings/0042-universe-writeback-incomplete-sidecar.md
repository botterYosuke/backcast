# findings 0042 — universe-only writeback が不完全 sidecar を作り live register を壊す（#67）

issue #67（bug・着手可能）。`#42` cutover slice 2 の owner HITL（2026-06-16・findings 0027 §3(e)）で判明した
非-slice2 ブロッカーを根治する。方針: **ADR-0005（scenario sidecar の merge-write は full-DOM read-modify-write・
sibling 全保存）**。oracle = TTWR `src/ui/scenario_sidecar/write.rs`（`set_instruments` / `set_startup_params` /
`atomic_mutate_scenario_object`）。`grill-with-docs`（2026-06-17）で導出。ADR-0005 は自己保護条項を持つため本
findings に実装事実を記録し、ADR は「方針: ADR-0005」として参照のみ（書き戻さない）。

## 1. 症状（HITL 再現・findings 0027 §3(e)）

sidebar で universe を 1 つでも編集すると `UniverseSidebarController` → `UniverseWriteback.Flush` →
`ScenarioSidecarStore.SetInstruments` が走る。**sidecar が未だ存在しない**（strategy `.py` の inline SCENARIO だけ）
状態でこれが走ると `{scenario: {schema_version, instruments}}` だけ（`start/end/granularity/initial_cash` 欠落）の
**不完全 sidecar を新規作成**する。

`load_scenario`（`scenario.py:382-395`）は sidecar の `"scenario"` キーがあれば**最優先で採用**し inline `.py` へ
フォールバックしない。不完全 scenario → `validate()` が `missing required keys: ['end','granularity','initial_cash','start']`
で reject → `register_live_strategy` が `STRATEGY_LOAD_FAILED`（`strategy_registry.py:87`）で失敗。

→ LiveAuto は「universe を編集した瞬間に壊れる」。Replay の Run-commit（`ScenarioStartupController.Commit` →
`SetStartupParamsAndInstruments`・5 キー完全 sidecar）は壊れない＝不整合。**しかも reject しているのは
`load_scenario`/`validate` という live・replay 共通の seam** なので、同じ不完全 sidecar は Replay 実行でも落ちる
（live 固有の問題ではない）。

## 2. 根本原因（grill でコード確証・2026-06-17）

`ScenarioSidecarStore.Mutate`（`ScenarioSidecarStore.cs:117-122`）が

```csharp
JObject root = File.Exists(path) ? ParseFile(path) : new JObject();
if (!(root["scenario"] is JObject scenario)) { scenario = new JObject(); root["scenario"] = scenario; }
```

で **sidecar 不在 / scenario object 不在のとき空オブジェクトを新規作成**する。これを共有する 3 経路のうち、
universe-only の `SetInstruments` でこれが走ると不完全 sidecar を生む。

### oracle 突き合わせ（TTWR・決定的）

TTWR `set_instruments` → `rewrite_scenario_instruments_atomic` → `atomic_mutate_scenario_object`（`write.rs:262`）は:
- `read_json_with_bom_strip(path)?` で読む → **ファイル不在なら `?` が IO エラーを伝播**
- `value.get_mut("scenario").and_then(as_object_mut).ok_or("missing scenario object")?` → **scenario object 不在ならエラー**

`set_instruments`（`write.rs:141`）も `set_startup_params`（`write.rs:173`）も同経路で **mutate-existing-only**。
新規 sidecar を一切作らず、エラーは `last_error` に積まれ registry 編集は in-memory に残る。**完全 sidecar の新規作成は
個別 writeback の外＝Run-commit 相当でのみ起きる**。backcast の「新規作成する `Mutate`」だけが oracle から乖離。

## 3. 確定した設計判断（Q1〜Q2・2026-06-17）

### (D1) 方針 = B（writeback を mutate-existing-only に）一択。A（live register 緩和）は不採用
A は根治しない: reject は `load_scenario`/`validate`（live・replay 共通 seam）であって live register 固有チェック
ではないため、live を緩めても同じ不完全 sidecar が Replay で落ちる。症状を片経路で隠すだけで strict-validate 契約
（ADR-0005 が依拠する不変条件）を弱める純損。A+B は B の劣化版（B で根治するため A は非対称と契約侵食を増やすだけ）。
B は oracle と同形で scenario 契約を維持する。

### (D2) 共有 `Mutate` を一律 mutate-only にしてはならない — `allowCreate` フラグで分岐
`Mutate` は 3 つの呼び出し元で共有される。素朴に「不在なら書かない」にすると Run-commit の正規な完全 sidecar 作成
（`ScenarioStartupController.Commit`）まで壊れる。`allowCreate` を `Mutate` に足し:

| 呼び出し元 | TTWR 対応 | allowCreate | あるべき挙動 |
|---|---|---|---|
| `SetInstruments`（universe-only・#31 picker/sidebar） | `set_instruments` | **false** | 不在ならスキップ（新規作成しない） |
| `SetStartupParams`（params-only・現状 probe のみが使用） | `set_startup_params` | **false** | 同上（oracle も mutate-only。params のみ = instruments 欠落でこれも不完全 sidecar を生む footgun だった） |
| `SetStartupParamsAndInstruments`（Run-commit） | StartupController commit 経路 | **true** | create-on-absent を温存（5 キー完全 sidecar をここだけが作る） |

これで「不完全 sidecar を作れる経路」が消え、完全 sidecar の作成は `SetStartupParamsAndInstruments` 1 点に集約される。

### (D3) skip の通知 = 戻り値 nullable（`WritebackOutcome?`）。`UniverseWriteback.Flush` は黙ってデフォー
`SetInstruments` / `SetStartupParams` は `WritebackOutcome?` を返し、**`null` = sidecar 不在で書かなかった**（skip）。
`SetStartupParamsAndInstruments` は `allowCreate=true` で常に書くため非 nullable のまま。
`UniverseWriteback.Flush` は `null` を受けたら `_lastFlushed` を進めず `false` を返す（編集は in-memory に残り、Run-commit
が完全 sidecar を作るときに universe ごと永続化される）。これは既存の「path 未解決なら skip without recording」と同じ
deferred 永続化セマンティクスで、**`LastError` には積まない**（実エラーではなく Run-commit 待ちの遅延）。corrupt JSON の
`ScenarioSidecarException` は従来どおり `LastError` に積む（別物）。

## 4. 検証（RED 先行・正本は本 findings。backcast に FLOWS.md は無い）

Unity 6000.4.11f1 実機 batchmode（この dev 環境に在る）。判定は `UNITY_EXIT=0` + ログ PASS + `error CS` 0 件
（`grep -c "error CS"` の 0-match exit-1 落とし穴に注意）。

### 反転する既存テスト（旧バグ契約を assert している＝tdd の invert パターン）
- **`UniverseSidebarProbe.Section7_Writeback`**: 現状 line 272「first flush did not write」は **universe-only Flush が
  fresh sidecar を作る**ことを期待している＝旧バグ契約。→ 完全 sidecar を seed してから flush が mutate-existing する形に
  反転し、**sidecar 不在では Flush が `false`＋ファイル不作成**を assert する #67 回帰を追加。
- **`ScenarioStartupProbe.Section4_ReadRoundTripAndNewSidecar`**: 「brand-new sidecar creation」を `SetStartupParams`+
  `SetInstruments` で行っていた。→ 唯一の creator になった `SetStartupParamsAndInstruments` に repoint。
- **`ScenarioStartupProbe.Section5`（line 297-298）**: bootstrap の `SetStartupParams`+`SetInstruments` を
  `SetStartupParamsAndInstruments` に repoint。

### 新規 RED（#67 の核）
- `ScenarioStartupProbe` に section 追加: sidecar 不在で `SetInstruments` → `null` 返却＋**ファイル不作成**（旧コードは
  `{schema_version, instruments}` を作っていた＝the kill）。次に完全 sidecar を作り `SetInstruments` で銘柄差し替え →
  非 null・`start/end/granularity/initial_cash` が verbatim 保存される。

### 検証実績（TDD・Unity 6000.4.11f1 実機 batchmode・2026-06-17）
- **RED（fix 前・production 未変更）**: `UniverseSidebarProbe.Section7` を「universe-only Flush が fresh sidecar を
  作る」旧契約から反転 → `[UNIVERSE SIDEBAR FAIL] flush created a sidecar before one existed (incomplete-sidecar
  regression #67)`・`error CS` 0・UNITY_EXIT=1。旧 `Mutate` の create-on-absent が不完全 sidecar を作るバグを実証。
  （`ScenarioStartupProbe.Section9` は nullable-return API 依存のため RED フェーズは body を一時退避して compile を通した。）
- **GREEN（fix 後）**: `[UNIVERSE SIDEBAR PASS]` / `[SCENARIO STARTUP PASS]`（Section9 復元・#67 核を value-assert）の
  両方 UNITY_EXIT=0・`error CS` 0。

### 実装（GREEN・本 issue 成果）
- `ScenarioSidecarStore.Mutate(strategyPath, mutate, bool allowCreate)`: `allowCreate=false` で sidecar/scenario object
  不在なら `null` を返し**書かない**（TTWR `atomic_mutate_scenario_object` の IO エラー / "missing scenario object" 相当）。
- `SetInstruments` / `SetStartupParams` → `WritebackOutcome?` を返す mutate-existing-only（`allowCreate: false`）。
- `SetStartupParamsAndInstruments`（Run-commit）→ `allowCreate: true`・唯一の creator・常に完全 sidecar。
- `UniverseWriteback.Flush`: `SetInstruments` が `null`（skip）なら `_lastFlushed` を進めず `false`（編集は in-memory・
  Run-commit で永続化・`LastError` には積まない）。
- probe 反転/追加: `UniverseSidebarProbe.Section7`（#67 回帰）/ `ScenarioStartupProbe.Section4・Section5`（creator を
  `SetStartupParamsAndInstruments` に repoint）/ 新規 `Section9`（個別 setter の mutate-existing-only を value-assert）。
- Python 側は無改修（`load_scenario`/`validate`/`register_live_strategy` が不完全 sidecar を reject する挙動は正しい backstop）。

### code-review（simplify・high・2026-06-17）の反映
- **[Low・修正済]** `UniverseWriteback.Flush` の null-skip（deferred）が `LastError` をクリアしておらず、corrupt
  sidecar で積まれた `LastError` が「ファイル削除で解消後」も残留しうる。→ null-skip で `LastError = null` を入れて解消。
- **REFUTED**: 「Flush が per-frame で File.Exists を叩く」→ `Flush` は `UniverseSidebarController.AddFromPicker`/`Remove`
  の **add/remove イベント駆動のみ**（per-frame caller 無し）。retry コストは既存の path-unresolved skip と同等で回帰なし。
- **latent（コード修正不要・記録のみ）**:
  - sidebar/picker の universe 編集は `Params.Dirty` を立てない。よって「即時永続化」が再 Populate clobber に対する唯一の盾
    だったが、本 fix で sidecar 不在 strategy の即時永続化が外れた。ただし production の `Populate` は起動時 1 回のみで
    再 Populate 経路は現状無く、編集は in-memory registry が SoT で Run-commit が拾うため **session 内 loss は無い**。
    pre-#67 の「永続化」は**不完全 sidecar を作る当のバグ**であって実質的な盾ではなかった（＝回帰ではなく意図した trade-off）。
    将来 File→Open 等で再 Populate を足すなら、findings 0024 D4 の in-memory authority 窓を再確認すること。
  - sidecar 不在 strategy の Run 前編集は app 終了で失われる（findings 0024 D4 の in-memory authority の延長・意図した trade-off）。
  - `Mutate` 第2 narrowing（file 在・scenario object 不在で no-op）は、layout co-write が `<strategy>.json` へ入る将来
    （現状 layout は別 `layout.json`）にのみ顕在化する latent。今日は到達不能。

### owner HITL leg（実機 Play・headless 決定不能の実 RPC）
本線 `BackcastWorkspace.unity` Play → strategy 保存（inline SCENARIO・sidecar 無し）→ sidebar で universe 編集 →
**STRATEGY_LOAD_FAILED が出ないこと**（編集は in-memory・Run-commit で完全 sidecar 化）→ LiveAuto ▶ 起動。
findings 0027 §3(e) の回避策（完全 sidecar を手置き / Run-commit 先行）が不要になることを確認。

### owner HITL 実機結果（2026-06-17・実機 Play・全レグ PASS）
fixture `kernel_spike_buy_sell.py`（inline SCENARIO・sidecar 不在）。#66 修正（inline-.py SCENARIO seed・別 working tree）込みで実施:
- **Step1**: Play で universe に `8918.TSE` が seed（#66 修正・startup tile 日付 2024-10-01〜2025-01-10 も inline 由来）。
- **Step2（#67 核）**: sidebar picker で `7203.TSE` 追加（universe 2件）→ **`kernel_spike_buy_sell.json` が作られないことを FS で確認**
  （旧コードなら `{schema_version, instruments:[…]}` の不完全 sidecar が出来ていた。`SetInstruments` の mutate-existing-only スキップを実証）。
- **Step3**: Venue → Connect MOCK (dev) → `Connected: MOCK`。
- **Step4（#67 統合）**: footer Auto → ▶ → run_result `RUNNING`・footer `LiveAuto: 436a4c9b…`・**STRATEGY_LOAD_FAILED 出ず**
  （sidecar 不在のまま register が inline `.py` SCENARIO を読む正経路）。
- **Step5**: footer Replay へ → base retile・orphan 無し、Venue → Disconnect → `Disconnected`、**Console error 0**。

→ #67 はコード（AFK RED→GREEN）と実機 HITL の両方でクローズ。findings 0027 §3(e) ブロッカー解消。
