# findings 0066 — E2E AuthorToRunJourney runner（第二波・Journey・全行新規）

`Assets/Tests/E2E/Editor/AuthorToRunJourneyE2ERunner.cs`（台本: 同ディレクトリ `.md`）。第二波 13 本目・
横断ストーリーの Journey E2E。**author→run の縦縫い**（空 notebook → セル編集 → universe/scenario 設定 →
Save As → provider 5 条件 supplyable → run gate が scenario sidecar を commit → `host.TryStartRun` 受理）を
**実 `BackcastWorkspaceRoot` を反射駆動**して観測する。run 開始**後**の kernel replay→箱庭は
`ReplayToHakoniwaE2ERunner` の責務（JOURNEY-AUTHOR-13＝対象外・終端から参照を張るだけ）。

## 実行コマンド

```text
# compile-only ゲート（error CS\d+ が 0 件・return code 0）
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Users\sasai\Documents\backcast" -logFile "<log>"

# AFK GREEN
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Users\sasai\Documents\backcast" \
  -executeMethod AuthorToRunJourneyE2ERunner.Run -logFile "<log>"
# expect: [E2E AUTHOR→RUN PASS] ... / exit=0
# 確認は Bash `grep -a "E2E AUTHOR→RUN" <log>`（ripgrep/Select-String は → を取りこぼす）
```

## section ↔ Action ID

| section | Covers | 観測 |
|---|---|---|
| `Section1_AuthorToRunHappyPath` | JOURNEY-AUTHOR-01..10 | 1 つの composed root で File→New 白紙化 → セル編集（dirty→provider false）→ cell add/delete → universe 追加（→chart tile）→ valid scenario → Save As（provider false→true・canonical path）→ run gate Ready＋sidecar commit → `host.InitializePython("MOCK")` 後 `OnRun`→`host.TryStartRun` 受理 |
| `Section2_RunGateRejects` | JOURNEY-AUTHOR-11, JOURNEY-AUTHOR-12 | positive anchor（Save As→provider true）の後、空 universe→`BlockedInvalidScenario`＋sidecar 不変／dirty editor→`BlockedNoStrategy` |

JOURNEY-AUTHOR-13（run 中 kernel→箱庭）は `ReplayToHakoniwaE2ERunner` steps 6-7 が正本＝section 無し・参照のみ。

## 設計判断

- **実 root を反射合成（steps 2-9 Python-FREE）**: `ComposeRoot` = `OpenScene` → `_font` 注入 →
  `SetSynthesizer(FakeMarimoSynthesizer)` → `ResolvePaths` → `BuildWorkspace`。New/編集/scenario/SaveAs/provider/
  commit は host 非依存。LayoutPersistenceJourney / RunButton SectionD / ReplayToHakoniwa と同型（parity-first・
  production 無改変）。
- **step 10 のみ `host.InitializePython("MOCK")`＋`_isOwner=true`**: `host.TryStartRun` の serverReady guard と
  `OnRun` の owner guard を通すため。batchmode の所有権スキップを迂回する正当手（ReplayToHakoniwa と同型）。受理の
  観測は production `OnRun`（gate 再照会＋req 組立＋`host.TryStartRun`）を反射 invoke し、host が run を launch した
  こと（`IsRunning || RunFinished`）で見る。実データは流さない（MOCK の `load_replay_data` は fast-fail でよい・観測
  対象は受理）。teardown は `host.Stop()`。
- **層分け（移送せず縫い目を観測）**: 各 surface の単体挙動の正本は既存 runner（ScenarioStartup /
  StrategyEditorNotebook / RunButton / MenuBar）。本 Journey はそれらを移送せず、実 root を通した author→run の横断
  データ伝播（セル dirty→provider 反転→gate commit→host 受理）だけを縫う。

## 非空虚化（vacuity litmus）

- **provider 5 条件 supplyable の弧 = false→true→false**:
  - step 3（編集）の `false` は **未バインド由来**（`_path=null`・`_openedOrSaved=false`＝条件①/③）であって dirty
    由来ではない。**dirty ガード（`MarimoNotebookDocument.TryGetStrategyFile` の条件②）の delete-litmus は step 12 に
    帰属**（Save As 後 provider==true [step 8] → セル再編集で dirty → provider==false [step 12]）。
    → **台本 `.md` の「条件②を消すと step 3 が落ちる」はコード上は不正確で、落ちるのは step 12**（step 3 は条件①/③で
    既に false）。本 findings で訂正。間に挟む step 8 の `true` が弧全体を非空虚化する。
- **scenario commit の帰属**: `OnFileSaveAs` も `_scenario.Commit(newPy)` を呼ぶ（台本順では Save As 時点で scenario は
  valid＝sidecar が既に書かれる）。よって step 9 を「sidecar に scenario」で素朴 assert すると **TryStartRun の Commit を
  消しても落ちない＝空虚**。**Save As 後に `AddInstrument(SECOND)` で universe を成長**させ、step 9 の TryStartRun の
  Commit だけが書ける delta（SECOND）を sidecar で観測する（provider は notebook clean のまま true 維持＝universe 追加は
  scenario dirty であって notebook dirty ではない）。TryStartRun の Commit を消すと SECOND 欠落で step 9 FAIL。
- **reject は positive 先行**: step 11/12 の前に同一 section の anchor で Save As→provider true を実証してから、空
  universe／dirty で block を観測する。step 11 は provider true を保ったまま universe を空にし、block が
  `BlockedNoStrategy` でなく `BlockedInvalidScenario` であることを保証（gate は provider→Commit の順）。step 12 は
  universe を valid へ戻してから notebook を dirty 化し、唯一の block 理由が dirty provider であることを isolate。

## delete-the-production-logic litmus（RED 化の正確な編集箇所）

- `MarimoNotebookDocument.SaveAs` の dirty クリア（`_dirty=false; _openedOrSaved=true;`）を no-op 化 → **steps 7-8 FAIL**
  （provider が true へ反転しない）。
- `ScenarioStartupController.TryStartRun` の `Commit(path)` 呼びを削除（直接 `Ready` を返す）→ **step 9 FAIL**
  （sidecar に SECOND が乗らない）。
- `MarimoNotebookDocument.TryGetStrategyFile` の `if (_dirty) return false;`（条件②）を削除 → **step 12 FAIL**
  （dirty でも true を返す）。
- `BackcastWorkspaceRoot.OnRun` の `_host.TryStartRun(req)` 呼びを削除 → **step 10 FAIL**（run が launch しない）。

## path-identity（#3 RUN-01 / #5 JOURNEY-LAYOUT-01 で2回踏んだ罠）

`MarimoNotebookDocument.SaveAs` は `_path = Path.GetFullPath(newPath)`（`\` 正規化）で保存し provider はそれを返す。
`_currentLayoutPath` は dialog の生値（`Application.temporaryCachePath` の `/` 区切り）。path 一致は `SamePath`
ヘルパで**両辺 `Path.GetFullPath` ＋ `StringComparison.OrdinalIgnoreCase`** で比較する。

## 既存 runner との層分け（重複しない根拠）

- ScenarioStartup（universe/期間/validate/commit の単体）・StrategyEditorNotebook（cell 編集/add/delete/save/provider
  の単体）・RunButton（readiness 真理値表・OnRun→host 配線の単体）・MenuBar（File 操作の単体）が各 surface の正本。
- 本 Journey はそれらの assert を**移送せず**、実 root を貫く **author→run の横断不変条件**（編集 dirty が run を止める→
  保存で supplyable へ反転→gate が sidecar を commit→host が受理）のみを観測する。

## 検証（2026-06-20 lead 実走・確定）

- compile-only: `error CS\d+` **0 件**・`Exiting batchmode successfully` / return code 0・新 `.meta` 生成。
- AFK GREEN: `-executeMethod AuthorToRunJourneyE2ERunner.Run` で `[E2E AUTHOR→RUN PASS] blank notebook → authored
  cell + scenario → save made the strategy supplyable → run gate committed the scenario sidecar → host.TryStartRun
  accepted the run.` を bash `grep -a`（`→` 含むため Select-String/ripgrep は不可）で **1 件確認**・FAIL 0 件・
  sentinel（`Found no leaked weakptrs` / Package Manager shutdown）あり＝executeMethod 実走（step10 の MOCK Python
  受理まで成功）・exit 0。RED litmus（上記 §）は lead 任意（vacuity は Section1 で 08 true/09 Ready/10 accept を
  positive 先行実証する構成で担保）。
