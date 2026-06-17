# WYSIWYR: Run reads the editor's strategy (not an env-default .py) — #78

- Issue: #78（bug, 着手可能）— Strategy Editor と Run が別ソースを読む（空エディタでも env 既定 .py が走る）
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0005 — cutover = 1:1 surface parity with TTWR](../adr/0005-cutover-scope-1to1-surface-parity-with-ttwr-ui.md)（accepted, 自己保護節あり）, [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)
- 設計確定: `grill-with-docs`（2026-06-17、owner インタビュー 5 問）
- 先行: #16（Strategy Editor code buffer / provider seam・findings 0010）, #66（inline-SCENARIO populate fallback・findings 0043）, #67（universe writeback・findings 0042）, #74（LiveAuto venue 余力）

> **状態: 設計ロック済み。** 実装結果・ゲートログは §6 に後追記。

---

## 0. 結論（owner 確定 2026-06-17）

issue #78 の AC は TTWR の **region split / merge_fragments / cache autosave** を 1:1 移植せよと読めるが、
**採らない**。backcast は #16 で TTWR と別アーキの WYSIWYR seam を **owner ロック済**（findings 0010 / ADR-0003）：

- 1 窓 = 1 つの実 `.py` を Open/Save（`StrategyDocument`）
- `IStrategyFileProvider.TryGetStrategyFile(out path)` は **保存済み path** を返す。**dirty/未バインド/欠落なら false**。
- `StrategyProviderRegistry` は window id（`"strategy_editor:region_001"`）で provider を引ける durable seam。

TTWR の `run_disabled = !is_active && cache_path.is_none()`（footer.rs:453）は backcast の
`!TryGetStrategyFile(...)` に 1:1 で写り、しかも #16 provider は **dirty なら false / path==disk を構造保証** ＝
TTWR より強い WYSIWYR 不変条件。region/merge/cache は TTWR **内部 infra** であり、ADR-0005 §Consequences が
「TTWR 内部 Bevy infra は 1:1 表面 parity の対象外」と明記したまさにその類（UI 表面ではない）。よって **B を採用**。

## 1. 本番バグ（確証）

`BackcastWorkspaceRoot` は editor の provider が入っている `_registry`（:90, editor を :306 で登録済み）を
**Run 経路で一切見ず**、env 既定 `.py`（`BACKCAST_HITL_STRATEGY` ?? `spike/fixtures/.../kernel_spike_buy_sell.py`）を
`BoundStrategyFileProvider(_strategyFile)` で 3 経路に注入していた：

| 行 | consumer | 役割 |
|----|----------|------|
| 620 | `OnRun` → `ScenarioStartupController.TryStartRun(provider)` | Replay Run gate（footer ▶ / Startup tile Run 両方） |
| 343 | `LiveAutoTransportViewModel` | LiveAuto ▶ start gate |
| 379 | sidebar `Bind(..., provider)` | sidecar writeback のターゲット path |
| 217/218 | `ScenarioInlineReader.Read` + `_scenario.Populate` | 起動時 universe seed（#66） |

両 run gate（`TryStartRun:124` / `LiveAutoTransportViewModel:144`）は **既に provider false で Run を封鎖する設計**。
LiveAuto VM のコメント（:161）は「backcast's provider returns the real saved canonical .py (no cache/original split
like TTWR)」と明記＝seam は **B のために作られていた**。バグは「正しくない窓口を 3 経路に注入していた」1 点に閉じる。

`OnFooterStep`（:1244）= `_host.Step()` は **strategy gate を通らない**（起動済み engine を進めるだけ）ので consumer ではない。

## 2. 採用設計（B）

### 2-1. RegistryStrategyFileProvider（新規・UnityEngine-free）

registry の文言「the run layer consults the registry to find a provider by window id」をそのまま実現する遅延 adapter：

```
class RegistryStrategyFileProvider : IStrategyFileProvider {
    registry; windowId = "strategy_editor:region_001";
    TryGetStrategyFile(out path) =>
        registry.TryGet(windowId, out p) && p.TryGetStrategyFile(out path);
}
```

- 毎回 registry を引き直す → editor の dirty/path/exists を常に再評価（fresh）。未登録/未ビルドでも false → Run 封鎖。
- registry は **active を選ばない**（意図的設計）。run 層が `region_001` を決め打つ 1 行が要る。現状 editor 窓は実質 1 個なので自明。
  複数 `.py` を別窓で開いて走らせる窓を選ぶ active-pick は **#78 スコープ外**（将来 additive slice）。
- dirty 封鎖は provider の中に既にあるので **新規封鎖ロジックは書かない**。窓口を付け替えるだけで封鎖も自動でついてくる。

### 2-2. 3 経路の差し替え

343 / 379 / 620 の `new BoundStrategyFileProvider(_strategyFile)` を
`new RegistryStrategyFileProvider(_registry, WINDOW_ID)` に差し替える。

### 2-3. seed も editor 追従・env 既定を本番から撤去（owner 確定）

- ResolvePaths から env 既定 `.py` 読み・`_strategyFile` フィールドを撤去（AC「env 既定 .py 直読みを撤去」）。
- universe seed を **editor restore の後** に移す：`RestoreLayout`（`RestoreEditors` 直後）で、adopted editor の
  provider が supplyable なら `ScenarioInlineReader.Read(path)` + `_scenario.Populate(path, …)` を実行（#66 の
  `ReadScenario ?? inline fallback` ロジックは不変、**path 源だけ env→editor に変わる**）。Awake/OnFileOpen の両 restore を覆う。
- **fresh install（保存 layout 無し）**: editor は UNBOUND-EMPTY → universe 空 → Run 封鎖。「空エディタ→Run 封鎖」を直接目視可能。
  #66 の元動機（fresh-install で universe を空にしない）は #78 の「未ロード→走らない」に **吸収される**（走らない以上 seed 不要）。
  #66 の seed 機構は「**ロード済み editor の `.py` に sidecar が無いとき inline から seed**」として再ホームされる（findings 0043 を supersede せず参照）。

### 2-4. sidecar は元 `.py` 側のまま（#66/#67 と無矛盾）

cache を挟まないので sidecar の original/cache 分裂は発生しない。`TryStartRun` は provider が返す editor の `.py` の
`<strategy>.json` を commit/read する。3 consumer（620/343/379）が **同一の editor path** を使うため sidecar ターゲットも一致する。

## 3. AFK ゲート（RED→GREEN）

- `BackcastWorkspaceProbe.Section11`（旧 #66 env-seed）を **#78 仕様へ反転**：(a) editor に inline-SCENARIO の .py を Open →
  post-restore seed で universe がその .py から seed される / sidecar が inline に勝つ、(b) editor 未バインド → universe 空 かつ
  `TryStartRun` が BlockedNoStrategy。実装前に RED を確認。
- `StrategyEditorProbe` に `RegistryStrategyFileProvider` 単体: 未登録/dirty/未バインド/欠落 → false、supplyable → path。
- pure logic は UnityEngine-free を維持。

## 4. 不採用

- TTWR region split/merge/cache autosave 移植（§0、ADR-0005 §Consequences）。
- editor-open → scenario panel 再 seed を **#78 の超過改造として広げる**ことはしない（seed は restore 後の Populate に限定）。
- 複数 editor からの run 対象 active-pick（将来 slice）。
- **戦略 run 中の cwd 切替（ADR-0011 / #79）は本スライス対象外・未実装**。#78 の grill で「provider が返す canonical な `.py` の親 dir を run 中の cwd にする（裸の相対 I/O を戦略の隣へ）」が派生論点として出たが、`python/engine/` に `os.chdir`/restore は**入れていない**（`grep -rn chdir python/engine/` = 0件）。ADR-0011 は **proposed**（未実装の正直化, 2026-06-17 code-review で accepted→proposed に差し戻し）、#79 は **reopen**（#78 に blocked-by）。実装後に ADR を accepted へ昇格する。**「cwd は #78 で済んだ」と読まないこと**。

## 5. CONTEXT 用語

- **strategy file provider（供給 seam）**: 既存定義のまま。本 slice は「run 層がこの seam を registry 経由で引く」配線を確立。
- 新語なし（region/cache は不採用のため glossary 不変）。

## 6. 実装結果・ゲートログ

実装（2026-06-17）:
- 新規 `Assets/Scripts/StrategyEditor/RegistryStrategyFileProvider.cs`（遅延 registry 引き adapter, UnityEngine-free）。
- `BackcastWorkspaceRoot`: 343/379/620 の `BoundStrategyFileProvider(_strategyFile)` → `RegistryStrategyFileProvider(_registry, WINDOW_ID)`。
  `_strategyFile` フィールド・env 既定読み（旧 ResolvePaths 本体）を撤去。universe seed を `ApplyLayout` 末尾
  （`RestoreEditors` 直後）の `SeedScenarioFromEditor()` に移し、直後に `_sidebarCtrl.PrimeWritebackFromCurrent()` で
  writeback を再 prime（seed 前の空 universe で prime される順序ずれを補正）。`ResolvePaths` は probe の compose seam として
  残置（editor 未バインドなら seed は no-op）。
- AFK: `BackcastWorkspaceProbe.Section11` を #78 仕様へ反転（unbound→universe 空＋`TryStartRun`=BlockedNoStrategy /
  editor bind→inline seed / sidecar が inline に勝つ）。既存 `Section9`（OnRun re-prime）を `_strategyFile` 注入から
  adopted editor の `Document.Open(strat)` 注入へ更新。`StrategyEditorProbe.Section9_RegistryRunWiring` を新設
  （未登録/null/unbound/dirty/torn-down→false、saved→path、live 再評価）。

ゲートログ（Unity 6000.4.11f1 batchmode, 全 exit=0）:
- `[STRATEGY EDITOR PASS]`（Section9 registry run-wiring 含む）
- `[BACKCAST WORKSPACE PASS] all sections green.`（Section11 #78 / Section9 re-prime）
- `[MENU BAR CUTOVER PASS]` / `[HAKONIWA BASE MODE PASS]` / `[WORKSPACE DEPTH LADDER PASS]` / `[SCENARIO STARTUP PASS]`（回帰なし）

`/code-review`（high, 8-angle）round 1:
- **HIGH（修正済）**: seed を BuildWorkspace の後ろへ移したため、build 時の `_tile.SyncFieldsFromController()`（:249）が
  空 Params に対して走り、seed 後に Startup tile の Start/End/cash が**空表示のまま Run は seed 済み Params を使う**
  （tile の scalar は universe と違い Changed event を持たない）WYSIWYR break。修正: `ApplyLayout` の seed 直後に
  `_tile?.SyncFieldsFromController()`。`BackcastWorkspaceProbe.Section11` leg (d) で実 `ApplyLayout` を駆動し tile
  `_startField` を assert（fix 無しで RED=「shows [], Params=2024-01-01」を確認、fix で GREEN）。
- **Medium（修正済）**: `RegistryStrategyFileProvider` を 4 箇所で new していた → 不変 stateless なので lazy 1 field
  `EditorFileProvider`（`??=`）に集約。将来の multi-editor active-pick が 1 箇所差し替えで済む。
- **採用せず（by-design）**: fresh-install で date 欄が空（owner 確定の「空・Run 封鎖」と整合）/ File→Open の再 seed は
  #78 の regression ではない（#78 前も File→Open は editor を rebind しつつ scenario を触らず editor=B/scenario=A だった。
  #78 は dirty でないとき editor=B/scenario=B に**改善**し、dirty-guard の edge は既存の未保存編集保護）。

ゲートログ（最終, 全 exit=0）: `[BACKCAST WORKSPACE PASS]` / `[STRATEGY EDITOR PASS]`（Section9）/
`[MENU BAR CUTOVER PASS]` / `[HAKONIWA BASE MODE PASS]` / `[WORKSPACE DEPTH LADDER PASS]` / `[SCENARIO STARTUP PASS]`。

owner HITL（目視）残: エディタに strategy 表示→編集→Run でその内容が走る／空エディタで Run 封鎖、を実機 Play で確認。
