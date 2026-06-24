# 0101 — Replay `[+ Add]` picker showed only the first 15 instruments (take(15) cap) → full listed_info universe, virtualized

- 日付: 2026-06-24
- 区分: bug fix（UI 挙動）＋ E2E 回帰ゲート
- 関連: #31（instrument picker / universe sidebar）/ findings 0024（picker 設計）/ findings 0084（picker 空一覧・context 配線）/ ADR-0005 / ADR-0006（listed_info DuckDB）
- gate: `UniverseSidebarE2ERunner`（Surface E2E・Python-FREE・AFK batchmode）Section3 / Section14、SIDEBAR-06 / SIDEBAR-18

## 症状（owner 報告 2026-06-24）

> replay モードで起動して `+Add` ボタンをクリックするとリストに**数銘柄しか出ない**。
> 期待: `BACKCAST_JQUANTS_DUCKDB_ROOT/listed_info.duckdb` の銘柄が一覧に表示される。

## 真因

`InstrumentPickerController.BuildList` が候補を **`take(15)`**（`for (… && i < MaxRows)`、`MaxRows=15` ＝ TTWR
`picker_list_rebuild_system` parity）で打ち切っていた。供給経路は健全:

- `S:\jp\listed_info.duckdb` の最新スナップショット（2025-12-05）は **4424 銘柄**（distinct Date は 2025-12-03/04/05 の 3 つ）。
- 既定 replay end `2024-12-31` は全スナップショットより前 → `_list_instruments_local` の **latest フォールバック**（findings 0084）が
  効いて **4424 件すべてが C# まで届く**（Python・`BackendAvailableInstrumentsProvider`・`WorkspaceEngineHost.InvokeListInstruments`
  のどこにも絞り込みは無い）。
- ところが picker が 15 件に切る → 検索なしで開くと **ordinal sort 先頭 15 件**（`1301, 1305, … 1330`＝全部 1xxx 番台）だけ。
  これが「数銘柄しか出ない」の正体。検索ボックスに `7203` 等を打てば絞り込み（≤15 件）で到達はできていた。

## owner 決定（2026-06-24）

「**全件＋仮想スクロール**」: take(15) を撤廃し listed_info の全銘柄を一覧に出す。~4400 行を 1 行 1 GameObject で
全件生成（しかも検索キーストロークごとに作り直し）すると UI が固まるので、**view 側を仮想スクロール化**して
「論理高さは全件・mount は可視窓だけ」にする。TTWR からの逸脱は owner の明示要望で正当化（memory `ttwr-parity-first`）。

## 修正

2 レイヤ:

1. **controller**（`InstrumentPickerController.BuildList`）— `MaxRows` cap を撤廃し、filter+sort 済みの**全候補**を返す。
   `MaxRows` const を削除。
2. **view**（`UniverseSidebarView`）— picker リストを仮想スクロール描画:
   - `PopulatePickerListContent` が全候補を `List<PickerRow> _pickerRows` に materialize し、`_pickerListContent.sizeDelta.y`
     を **全件分（`count*ROW_H`）** に（スクロールバーが全銘柄に届く）。
   - `RenderPickerWindow` が **可視窓 +buffer 行だけ** GameObject を mount（各行は絶対位置 `-idx*ROW_H`）。窓が変わらなければ
     再 mount を skip。`Update()` が毎フレーム（スクロール／レイアウト確定後）非強制で再窓化。
   - 窓算術は純関数 **`PickerListWindow.Compute`**（view から切り出し＝headless で `rect.height==0` でも決定論的に gate 可能）。
   - 非同期供給ポーリング（findings 0084 SIDEBAR-17）は、cap 撤廃で全件 sort が毎フレーム走るのを避けるため
     **cheap な `UniverseSidebarController.SupplyRevision`（kind+件数）** に変更（旧 `PickerSignature` の全行 string 連結を廃止）。

変更ファイル:
- `Assets/Scripts/Universe/InstrumentPickerController.cs`（cap 撤廃）
- `Assets/Scripts/Universe/PickerListWindow.cs`（新規・純関数）
- `Assets/Scripts/Universe/UniverseSidebarController.cs`（`SupplyRevision` 追加）
- `Assets/Scripts/Universe/UniverseSidebarView.cs`（仮想スクロール）

## RED → GREEN（AFK 実証済み・2026-06-24）

gate: `UniverseSidebarE2ERunner`（`scripts/run-live-e2e.ps1 -Method UniverseSidebarE2ERunner.Run`）。

- **Section3**（SIDEBAR-06）: 30 件供給 → `rows.Count == 30`（cap なし）。
- **Section14**（SIDEBAR-18）: (a) controller が 4424 件を全件返す / (b) `PickerListWindow.Compute` の窓（top で bounded・
  scroll で row100 を straddle・小リストは全 mount）/ (c) view の `_pickerListContent.sizeDelta.y` が全件高さ・mount 子数は
  `0 < childCount < 4424`（仮想化）。

| 状態 | 結果 |
|---|---|
| cap 復活（`i < 15` を戻す） | **RED**: `[E2E UNIVERSE SIDEBAR FAIL] picker capped the universe (15/30) — take(15) not removed`（Section3 が `??` 短絡で先に発火） |
| 修正適用 | **GREEN**: `[E2E UNIVERSE SIDEBAR PASS] … full-universe-virtualized verified` / exit 0 / `error CS` 0 件 |

delete-the-production-logic litmus（台本にも記載）:
- BuildList に take(15) を戻す → Section3 / Section14(a) FAIL。
- `RenderPickerWindow` の窓化を外して全件 mount → Section14(c) の bounded-mount assert FAIL。
- `_pickerListContent.sizeDelta` を窓分だけにする → Section14(c) の full-height assert FAIL（全件到達不能）。

## HITL（AFK 対象外）

実 4424 行のスクロール体感・板（depth）への focus 追従・実 EventSystem の検索 focus 保持は HITL（SIDEBAR-12/13 と同様）。
AFK は「全件供給」「論理高さ＝全件」「mount は窓のみ」「窓算術」を決定論で固定し、実レンダ/スクロール feel は owner 目視。

## レビュー反映（code-review/simplify・2026-06-24）

初回実装に対する high-effort レビューで挙がった指摘を反映（GREEN 維持・`[E2E UNIVERSE SIDEBAR PASS]` exit 0 で再確認）:

1. **ホットパスの確保/走査爆発（最重要）** — `BuildList` が候補ごとに `registry.Ids.Contains(id)` を呼んでいた。
   `InstrumentRegistry.Ids` は get ごとに `ReadOnlyCollection` を新規確保し `.Contains` は O(curated) 線形走査 → cap 撤廃で
   ~4400 件 × O(n) ＝キーストロークごとに数十万比較＋4400 wrapper 確保。**ループ前に `HashSet` を 1 回構築**して O(1) 化。あわせて
   `id.ToLowerInvariant().Contains(q)`（id ごとに小文字列を確保）を **`IndexOf(q, OrdinalIgnoreCase)`** に（全件分のアロケ撤廃）。
2. **再オープンで scroll が先頭に戻らない（UX 退行）** — ScrollRect は open/close で再利用されるため、4400 行をスクロール後に
   閉じて再オープンすると `verticalNormalizedPosition` が残り、窓算術が**ユニバース中腹**を mount した（cap 15 時代は viewport に
   収まりスクロール不能だったので顕在化しなかった新規退行）。`PopulatePickerListContent` で**毎回 top にリセット**（fresh open /
   新検索 / async Loading→Ready いずれも先頭表示。安定 Ready 時は再 populate されないのでユーザーのスクロールは保持）。
3. **`SupplyRevision` 手書き struct → ValueTuple** — IEquatable/Equals(object)/GetHashCode/== の 5 メンバ中 3 つが dead。
   `(UniverseStatusKind, int)` の ValueTuple で値等価が無償・`!=` がそのまま使える。~11 行のボイラープレート削除。
4. **`_pickerRows` の二重確保** — `PickerList()` は誰も alias しない新規 List を返すのに `new List<>(...)` で再コピーしていた
   （キーストロークごとに全件コピー）。`IReadOnlyList` で**直接参照**。
5. **`PickerScrollTopPx` の contentH 再計算** — `_pickerListContent.sizeDelta.y`（＝ populate が書いた正本）を読まず `total*ROW_H`
   を再計算していた。正本を読むよう変更（高さ式が将来変わっても scroll 算術と実サイズが乖離しない）。
6. **テストの buffer 二重定義** — runner の `PickerListWindowBuffer` mirror は view の private const と独立で、drift しても
   canary が**構造的に発火不能**だった。buffer を **`PickerListWindow.DefaultBuffer`（単一正本）** に集約し、view・runner 双方が参照。
7. **第二消費者（IMGUI HITL harness）** — `UniverseSidebarHitlHarness` は仮想化なしで 1 行 1 ボタンを OnGUI に積む。今は 6 件
   mock で安全だが実 provider に繋ぐと freeze するため**警告コメント**を追加（cap 撤廃で `PickerList` が全消費者にとって無制限化した altitude 注意）。

非採用（許容）: スクロール中の GameObject 再生成（pooling 無し・1 フレーム上限で freeze ではない）、`SupplyRevision` が旧 per-row
署名より弱い件（同 Kind・同 Count の id 入替を検出しない＝shipping provider では起きない・コードコメントに明記）。

## 追補（owner 報告 2026-06-24）— 候補一覧の高さが footer まで届かない（SIDEBAR-19）

cap 撤廃で全件出るようになった後、owner から「展開した銘柄一覧が footer まで到達しない高さでスクロールが効いている。
期待: footer まで一覧の高さがほしい」。

**真因**: `UniverseSidebarView.Relayout` が picker open 時に候補リストを `roomAfterHeader * 0.5f`（**余白の半分**）で頭打ち
していた（「picker can't starve the rows」のため）。universe が空（"No instruments"・rows ペインはほぼ 0）でも picker は
半分しか貰えず、中腹で止まってスクロールになる。cap 15 時代は 15 行が半分以内に収まり顕在化しなかった。

**修正**: 分割の優先度を反転。**ROWS ペインを半分に cap**（大きな curated universe が picker を押し出さない）し、**picker
リストは余白を全部使う**（footer まで伸びる）。高さ分割を純関数 `SidebarPaneSplit.Compute(available, headerH, naturalRows,
naturalList, open)→(rowsH, listH)` に切り出し（`Relayout` は headless で rect.height==0・早期 return＝高さは実 layout 観測
不能だが算術は観測可能）。`UniverseSidebarView.cs` の `Relayout` がこれを呼ぶ。

**RED→GREEN**（AFK・`Section15`）:
- (a) closed→rows のみ (b) 空 universe→list が余白の >60% を占有（footer 到達）(c) 巨大 universe→rows/list 折半 (d) 合計が
  余白を超えない。
- RED 実証: `SidebarPaneSplit` で picker を半分 cap に戻す → `[E2E UNIVERSE SIDEBAR FAIL] picker list 427 did not fill toward
  the footer (<=60% of 854) — capped at half?`。GREEN: `… + list-fills-to-footer verified` / exit 0 / `error CS` 0 件。

実 footer までのピクセル到達は HITL（owner 目視）。AFK は分割算術を決定論で固定。

## 備考

- `UniverseSidebarE2ERunner` は人間可読 NAME タグ（`[E2E UNIVERSE SIDEBAR PASS]`）で verdict を出す findings-gated runner
  （SIDEBAR-01..18 すべて同方式）。rollup の per-Action-ID 単一トークン規約（E2E-CONVENTIONS §5）は本 runner 未適用のままで、
  PASS は log grep（`bash grep -a`）で確認する。
- per-frame の全件 sort 回避は cheap な supply revision（`CurrentSupplyRevision`＝(kind,件数) ValueTuple）で対応したが、検索
  キーストローク時の filter+sort（空クエリ時 4424 件）はそのまま（1 操作 1 回・体感問題なし）。将来 controller 側 memo 化の余地あり。
