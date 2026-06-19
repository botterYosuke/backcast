# 0051 — File→Open opens a bare strategy `.py` (no layout key)

owner-locked 2026-06-19 (grill-with-docs)。関連: #80（CLOSED・Strategy picker）/ #69（findings 0048・multi-document Open/Save）/ #78（findings 0044・WYSIWYR）。

## 背景 — owner の誤解と現状の食い違い

owner は #80 を「**File→Open** で v19 の `.py` を開いて Replay/Auto 再生可能にする」つもりで起票したが、実装は**別建ての "Strategy" 最上位メニュー**になっていた。

調査で判明した現 HEAD（`32b7284`）の実態:

- **"Strategy" メニューは既に撤去済み** — `409fa33 #76 "imperative sunset — retire #80 strategy picker"` が
  `OpenMenu.Strategy` / `OnOpenStrategy` / `EnumerateStrategies` / `StrategyPickerModel` を全撤去し、
  `32b7284 #81` で main に統合済み。→「メニューを消す」作業は不要（完了済み）。
- **しかし本来の目的は未達** — `OnFileOpen`（`BackcastWorkspaceRoot.cs:1550`）は今も strict:
  `LayoutSidecarStore.TryReadLayout` が false なら「無効な layout」で **abort**。
- v19 の sidecar（`v19_morning.json` = `["scenario"]` のみ・**layout キー無し**）は false になり **弾かれる**。
- Strategy メニュー撤去と相まって、**素の戦略 `.py` を開く扉が1つも無くなった**状態だった。

## 決定（設計の木）

**扉は File→Open に一本化する**（owner: 「1ルールに畳む（layout 有無で分岐）」）。
2扉（「保存ワークスペースの復元」vs「素の戦略の採用」）は **layout キーの有無**で分岐する1ルールに畳める。

### D1. File→Open は1ルール（layout 有無で分岐）
```
OnFileOpen:
  py = _fileDialog.OpenStrategy(InitialDir())     ; cancel → no-op
  bool layoutOk = LayoutSidecarStore.TryReadLayout(py, out doc)
  SendModeSideEffect(_menuBar.FileOpenModeSideEffect())
  if (!_coordinator.Open(py, layoutOk ? doc.cellPositions : null))   ; notebook 自体が開けねば abort（現状維持）
      → ShowMessage; return
  if (layoutOk) ApplyLayout(doc)                  ; 妥当 layout のときだけジオメトリ復元
  _currentLayoutPath = py; PersistResumePointer(py); ReseedFromEditor()
  ShowMessage("Opened …")
```

### D2. layout キー「無し」(benign) → bare open（v19 の本線）
ファイル無し / scenario-only sidecar（layout キー無し）は **`.py` を bare open**（auto-cascade 位置・
**現ジオメトリ維持**・`ReseedFromEditor` で scenario/universe を seed）。v19 はこれで開け、Run が解禁される。

### D3. sidecar の JSON 破損 / layout オブジェクト不正 → **これも bare open**（owner 回答を訂正の上で確定）
当初 D4 保護として「破損は abort 維持」としたが、owner が **「壊れていても bare open」** に訂正。
→ corrupt と absent を区別する必要が無くなり、`TryReadLayout` の既存 **bool** がそのまま分岐になる
（true=妥当 layout のみ / false=無し・破損・不正すべて → bare open）。**`LayoutSidecarStore` への変更は不要。**

**D3 実装で判明した実バグ（RED テストが炙り出した）**: corrupt sidecar の bare open 経路は、`ReseedFromEditor
→ SeedScenarioFromEditor → _scenario.Populate → ScenarioSidecarStore.ReadScenario` が malformed JSON で
`ScenarioSidecarException` を **throw**（fail-loud 契約）し、OnFileOpen から伝播してクラッシュしていた。
D3（corrupt でも bare open）を成立させるには **scenario seed seam を耐性化**する必要があった:
- `ScenarioSidecarStore.TryReadScenario(path, out snap)` を追加（corrupt → `false`・tolerant）。
  既存 `ReadScenario` の fail-loud 契約は**他 caller（run-time load / HITL harness）のため維持**。
- `ScenarioStartupController.Populate(path,…)` を、pre-resolved snapshot を受ける `PopulateFrom(snap, today)`
  に分離（sidecar 読みを内包しない）。dirty-guard は両方に残し旧挙動を保存。
- `SeedScenarioFromEditor` は `TryReadScenario` + `PopulateFrom` を使い、corrupt 時は inline/empty へ degrade
  ＋「SCENARIO unreadable」メッセージを surface。→ Run は空 universe で blocked（「開いて直す」case）。

### D4 保護（findings 0048 D4）との関係 — supersede ではなく**達成手段の差し替え**
findings 0048 D4 は「壊れ/欠落 sidecar で Open が Default() に degrade して**現ワークスペースを wipe**するのを防ぐ」ため
strict-abort を課していた。本決定はその**目的（wipe しない）を維持**しつつ達成手段を変える:
ジオメトリを触るのは `ApplyLayout` のみ。bare open ではそれを**呼ばない＝現ジオメトリ維持で wipe しない**。
唯一の abort は `_coordinator.Open`（notebook 自体）が失敗したとき（従来どおり workspace 維持）。
→ 0048 D4 の「abort」は本 slice で「bare open（ジオメトリ非適用）」へ緩和。`MultiDocLayoutProbe` の
`TryReadLayout` strictness（S3）は **contract 不変なので維持**（変えるのは root の応答のみ）。

## 射程外（別スライス）

- **発見可能性**: Strategy 一覧撤去で native ダイアログだけになるが、`InitialDir()` を `python/strategies` に
  向ける改善は owner 判断で **別スライス**（今回は strict 緩和に集中）。
- 任意パス / OS ネイティブ周辺の UX は #69 既存のまま。

## テスト方針（CLAUDE.md / behavior-to-e2e）— 実走で RED→GREEN 確定

backcast に FLOWS.md は無いので RED は C# AFK probe に置く。`BackcastWorkspaceProbe.Section14_FileOpenBareStrategy`
を新設し、**実 `OnFileOpen`**（`StubFileDialog` で選択 .py を注入）を reflection で駆動する（hand-call の
`coordinator.Open` では abort バグを捕まえられない＝false-green を回避）。3 case:
- **S14a** scenario-only sidecar（v19 形）→ 開けて universe seed（`[BARE.TSE]`）。
- **S14b** malformed JSON sidecar（`{ not json`）→ bare open・universe 空（D3）。
- **S14c** 構造破損 sidecar（`{"scenario":{"start":{}}}`＝valid JSON だが `FromJObject` の `(string)` cast が
  `ArgumentException`）→ bare open・universe 空。`TryReadScenario` の bare catch が非 JSON 例外も握る保証。

実走（Unity 6000.4.11f1 batchmode）:
- **RED**: S14a が abort で fail（`currentPath=[]`、assert 失敗＝正しい理由）。
- **GREEN**: branch fix + D3 耐性化後に全14セクション PASS。`ScenarioStartupProbe`（Populate split）/
  `MultiDocLayoutProbe`（`TryReadLayout` strictness 不変）も PASS で回帰なし。

## code-review(simplify) で潰した指摘

- **Medium（D3 不完全）**: `TryReadScenario` が `ScenarioSidecarException` のみ catch だと、構造破損 sidecar の
  `FromJObject` `(string)` cast が投げる `ArgumentException` や I/O lock が escape して File→Open がクラッシュ。
  → bare `catch` へ広げ（`LayoutSidecarStore.TryReadLayout` と同型）、S14c で固定。
- **Medium（UX）**: corrupt sidecar でも inline fallback が universe を埋めるケースで「unreadable—save a
  scenario sidecar to set the universe」は矛盾。→ corrupt 専用メッセージ（"scenario sidecar unreadable — fix
  <name>.json"）に分離。
- **Low（据え置き・記録）**: ① `SendModeSideEffect`（Live→LiveAuto）が `_coordinator.Open` 失敗前に発火する窓は
  **変更前から存在**し、TTWR の「load 前に mode 遷移」（findings 0017 §1）を踏襲した設計。実 repro（Replay/未接続）
  では `FileOpenModeSideEffect` が null で no-op。② dirty 時の余分な tolerant read は user 起点（reseed は hot path
  でない）で無害（broad catch でクラッシュは解消済み）。

## ADR 判断

新規 ADR は起こさない（可逆・root 配線の局所変更・findings 0048 D4 の達成手段差し替えに留まる）。
正本は本 findings。0048 は参照のみ（書き換えない）。
