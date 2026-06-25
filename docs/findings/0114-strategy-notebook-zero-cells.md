# findings 0114 — 戦略ノートブックは在庫 0 セルを許す（全セル削除可・永続化の床は marimo 由来で 1）

方針: [ADR-0033](../adr/0033-strategy-notebook-allows-zero-cells.md)（本スライスの正本・ADR-0013 D5 を supersede）, [ADR-0013](../adr/0013-cell-as-floating-window-notebook-aggregate.md)（cell = floating window・notebook 集約。D1–D4 は不変・D5 のみ覆る）。
関連: findings 0050（cell-as-floating-window・synth/decompose seam）／0110（mode-conditional 可視性）。

本 findings は `/grill-with-docs`（2026-06-25・owner HITL Q1–Q5）で確定した下位決定と、設計を作り直した **load-bearing な実機事実**を会話で消えないように固定する。

> **状態: 実装着地（2026-06-25）。** `behavior-to-e2e` formal invoke → AFK gate RED 先行 → production 反映 → GREEN（§6）。

---

## 0. 要望（owner）

> 「"Strategy Editor" は floating-window で [✕] ボタンをクリックすると消すことができ、cell を削除するが、**全 cell を消すことが可能になってほしい**。」

現状は **最後の 1 枚を消せない**（`MarimoNotebookDocument.RemoveCell` の `if (_cells.Count <= 1) return false;`／削除時に「a notebook keeps at least one cell.」）。これは ADR-0013 D5 が凍結した **≥1 invariant**。本要望はこの床を外す話。

## 1. 現状（コードで両端まで裏取り済み）

| seam | 場所 | 事実 |
|---|---|---|
| [✕] 削除 | `NotebookCellCoordinator.DeleteCell` → `MarimoNotebookDocument.RemoveCell` | `RemoveCell` の `Count<=1` 床が最後の 1 枚を拒否。region_001 → hide（dormant）／region_002+ → despawn |
| 追加 [+] | `BackcastWorkspaceRoot.BuildAddCellButton` → `OnAddCell` → `Coordinator.AddCell` | [+] は **画面固定ボタン**（cell 窓ではない）→ 0 セルでも消えない。`AddCell` は dormant region_001 を最優先で再利用 |
| 合成（保存） | `cell_synthesis.synthesize_json` → marimo `generate_filecontents` | 0 セルでも valid な `.py` を出す。C# 側に空リスト拒否ガードは無い |
| 分解（Open） | `cell_synthesis.decompose_json` → marimo `load_app` | Open ガード `if (decomposed.Count == 0) return Fail("not a marimo notebook")`（`MarimoNotebookDocument.cs`） |

## 2. load-bearing な実機事実（§設計を作り直した発見）

owner が「marimo も最後の 1 枚を消せて、ディスクにはヘッダだけの `.py` が残る」と事実主張。**実機（marimo 0.20.4・プロジェクト seam）で裏取り**：

```
synthesize_json('[]')  → 'import marimo\n\n__generated_with = "0.20.4"\napp = marimo.App()\n...\nif __name__ == "__main__":\n    app.run()\n'
decompose_json(その空.py) → '[{"body": "", "name": "_", "config": {...}}]'   ← セル 1 枚（空ボディ）
```

- ✅ **owner が正しい**: 空 `.py` はヘッダのみの形（`import marimo` / `__generated_with` / `app = marimo.App()` / footer）。
- ❗ **だが marimo はそれを「空セル 1 枚」に展開して返す。** `load_app` は `.py` の中に「セル 0」を表現できず、空ファイルは必ず 1 セルに膨らむ。
- ⚠️ これは findings 0050 当時の想定（および本セッションの静的調査エージェントの「decompose は `[]` を返す」主張）を**実機で覆した**。静的読解では `for i in range(len(codes))` が 0 回と読めたが、`load_app` が空ファイルに 1 セルを差すため実際は 1。**「コードで裏取り」を実機実証まで持ち上げた**結果の訂正。

→ 帰結: **セッション中に 0 窓は作れる**が、**保存→Open の往復は 0 を保てない**（marimo が 1 に戻す）。ADR-0013 D5 が引用した「marimo は最後の 1 枚を消せない」は不正確で、正しくは「`.py` は空にできるが load で必ず 1 に戻る」。

## 3. 確定した設計（Q1–Q5・owner HITL binding）

| # | 決定 | 根拠 |
|---|---|---|
| **Q1** 終端 | **(A) 本当に 0 cell（0 窓）**。空キャンバス＋[+] のみ | 要望文に素直。「空セル 1 枚リセット」(B) は却下（≥1 を残すだけで真の全消しでない） |
| **Q2** 手段 | **(a) 既存 [✕] の壁を外すだけ**、1 枚ずつ 0 まで | 新 UI 不要。専用「全消し」ボタン (b) は別機能 |
| **Q3** 永続化 | **(X1) marimo 追従＝開き直しは空セル 1 枚で戻る** | §2 の実機事実。X2（`.json` 正本で 0 復元）は marimo 意味論から乖離＋複雑化で却下 |
| **Q4** File→New | **(P) 従来どおり空セル 1 枚で始まる** | 0 は「消した結果」到達する状態。新規が真っ白だとタイプ開始点が無い・marimo の New も 1 |
| **Q5** parity | **乖離はほぼ無し**。永続化の床は marimo と同じ 1、新規なのは「編集中に 0 窓を許す」ことだけ | owner 承認。床 1 は backcast の都合でなく marimo 由来 |

**0 窓からの 1 枚目の動線**（owner 明示要求「同じ動線」）: 0 セル到達時 `region_001` は必ず dormant（hide のみ・never-Destroy）。次の `AddCell` は dormant region_001 再利用の既存経路に乗り、File→New（`SyncWindowsToNotebook` で i==0 → region_001）と**同一**。特別分岐は新設しない（ADR-0013 D4）。

## 4. 実装スケッチ（実装時 §6 に証跡を追記）

変更は極小：

1. `MarimoNotebookDocument.RemoveCell` — `if (_cells.Count <= 1) return false;` を**撤去**（≥1 削除床の唯一の実働箇所）。
2. `BackcastWorkspaceRoot.OnDeleteCell` — 死んだ文言「a notebook keeps at least one cell.」を整理（`DeleteCell` の false は未知 region 等の本物の異常時のみに）。
3. `MarimoNotebookDocument.Open` の `if (decomposed.Count == 0)` — §2 で**死にコード**と判明（valid marimo file の decompose が 0 を返すことは無い）。コメント訂正 or 撤去（任意・無害）。marimo 判定は `decomposed == null`（not_marimo / syntax_error）が担い検出力は不変。
4. テスト — ≥1 を assert する E2E（最後の 1 枚削除が失敗する前提の節・`StrategyEditorNotebookE2ERunner` Section10 付近）を**契約反転**（削除成功 → 0 窓）。File→New=1 セルは不変なので FileNavGuard 系は GREEN 継続。

**0 セル安全の裏取り**（下流が床に守られていないか確認済み）: `SyncWindowsToNotebook`（0 件 for）／`CapturePositions`（空 List 返し）／`UpdatePlaceholders`（空 dict for）／run routing（`idx >= cells.Count` 境界・`cell == null` 早期 return）はすべて 0 セルで安全。Save も synth に委譲で divide/index 無し。よって床撤去はクラッシュを生まない（守っていたのは設計判断であってクラッシュではない）。

## 5. behavior-to-e2e ハンドオフ（実装着手の入口）

挙動が変わる（0 セル/0 窓の許容・全消し）ので、実装前に `behavior-to-e2e` を formal invoke し AFK gate を RED 先行で固定する。正本ゲートは **Python-FREE な C# AFK probe**（control logic）。新規/反転ゲート候補：

- **全消し → 0 窓**: region_001＋region_002 の 2 窓を確定生成 → [✕] で両方削除 → `CellCount==0`・cell 窓 0・[+] ボタンは残存を assert（旧「最後の 1 枚削除が false」節を反転）。
- **0 → 1 再 spawn の同一動線**: 0 セルから `AddCell` が dormant region_001 を再利用（File→New と同じ host）し 1 窓で復帰することを assert。
- **保存→Open 床=1（X1）**: 0 セルを保存（ヘッダのみ `.py`）→ Open し直すと **空セル 1 枚**で戻る（0 ではない＝marimo 仕様）を assert。可能なら Python 同居の round-trip で `decompose` が 1 を返すことも固定。
- **回帰**: `StrategyEditorNotebookE2ERunner` 既存節（per-cell RUN／add-delete）・FileNavGuard（File→New=1 セル不変）が GREEN 継続。

### owner-run HITL
Strategy Editor の cell を [✕] で全部消す → キャンバスが空（[+] だけ残る）→ [+] で 1 枚目が File→New と同じ位置感で戻る。全消し→Save→再 Open で**空セル 1 枚**で戻る（0 ではないことを目視）。

## 6. 実装証跡（2026-06-25 着地）

### production 変更（極小・§4 のスケッチどおり）
- `MarimoNotebookDocument.RemoveCell` — `if (_cells.Count <= 1) return false;` を**撤去**。以後 false は `cell==null` / 未知 cell の本物の異常時のみ。class header の「>=1 INVARIANT」コメントを「ZERO-CELL FLOOR LIFTED（永続化の床=1 は marimo 由来）」へ書き換え。
- `MarimoNotebookDocument.Open` — `if (decomposed.Count == 0) return Fail(...)` は **defensive dead code** とコメント訂正（§2 の実機事実: marimo `load_app` が valid な空ヘッダを 1 セルへ膨らませるので valid marimo file の decompose が 0 を返すことは無い。non-marimo は手前の `decomposed == null` が捕捉）。撤去せず belt-and-suspenders として残置（ADR-0033 が「コメント訂正 or 撤去・任意」とした方を採用）。
- `FakeMarimoSynthesizer.Decompose` — marker 一致時に parse 結果が空なら**空セル 1 枚へ inflate**（marimo 床=1 の鏡映）。これをしないと shared-golden が drift（AFK X1 ゲートが 0 を見て実 marimo は 1）。
- `BackcastWorkspaceRoot.OnDeleteCell` — 死んだ文言「a notebook keeps at least one cell.」を「no such cell.」（本物の異常＝未知 region 時のみ発火）へ整理。

### AFK RED→GREEN（C# 正本ゲート・Python-FREE）
- **RED**: テストだけ反転（production の ≥1 床は残置）して `StrategyEditorNotebookE2ERunner.Run` を実走 → `[E2E STRATEGY NOTEBOOK FAIL] S10/#146: the last cell must now be removable (0-cell floor lifted)`（床が load-bearing と実証・compile error 0）。
- **GREEN**: 上記 production 変更を当てて再実走 → `[E2E STRATEGY-56 PASS]` / `[E2E STRATEGY-57 PASS]` / `[E2E STRATEGY-58 PASS]` ＋ surface 要約 `[E2E STRATEGY NOTEBOOK PASS]`・`error CS` 0。
- **反転節**: Section10（aggregate 側 0-cell 到達＋X1 round-trip）／Section12（coordinator 側 0-cell＋0→1 dormant 再利用）を ≥1 assert から反転。**Section26 新設**＝「全消し→0 窓→[+]再 spawn→X1 床=1」を per-Action-ID 単一トークンタグ付きで gate（surface 要約タグは空白入りで rollup 非対象なので、rollup レール（`E2ERollup.ps1`）に載るのは Section26 の `STRATEGY-56/57/58` タグ）。
- **回帰**: `FileNavGuardE2ERunner.Run` GREEN（`[E2E FILE-NAV-GUARD PASS]`）＝File→New=空セル 1 枚は不変（constructor / `ResetUnboundEmpty` 無改変）。

### Python（marimo 床=1 の pin）
- `test_marimo_cell_synthesis_golden.py::test_entry_point_empty_notebook_floor_is_one_cell` を追加＝`synthesize_json("[]")` はヘッダのみ valid marimo（`app = marimo.App()`・`@app.cell` なし）、`decompose_json(それ)` は**ちょうど 1 空セル**を返す（§2 の実機事実を回帰 pin・comment-only file の not_marimo→None とは別物）。11 passed。

### 台帳・docs 更新
- `StrategyEditorNotebookE2ERunner.md`：STRATEGY-10 を「最後の 1 セルも削除→0 cell」へ反転、STRATEGY-56/57/58 行追加、footer の「常に ≥1」を「セッション中 0 セル可・永続化の床=1」へ、litmus を「床を**復活**させると RED」へ反転。
- `E2E-INDEX.md`：`STRATEGY-01..58`／行数 55→58・自動 52→55、Surface 総計 228→231・#146 サマリ 1 行追記。
- `CONTEXT.md`（grill 段で先行更新済）：glossary「cell window / marimo notebook」に 0 セル許容＋床=1＋0→1 dormant 再利用を明記。
- ADR-0033 は immutable（参照のみ）。本 findings が本スライスの正本。

---

> 🤖 `/grill-with-docs`（2026-06-25）セッション記録（Claude Code）。設計の木を本 findings に固定し、ADR-0033 が ADR-0013 D5 を supersede。実機事実（§2）が設計を作り直した。
