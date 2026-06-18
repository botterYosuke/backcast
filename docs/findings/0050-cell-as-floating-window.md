# findings 0050 — #81 やり直し: 1 cell = 1 Strategy Editor（canvas 上の floating window）

issue #81 やり直し（前任設計＝findings 0049 は SUPERSEDED）。owner が HITL で確定したモデル:
**marimo 3D モードそのもの — 各セルが infinite canvas 上の独立した floating window**（drag 移動・
z-order・位置永続化を持つ）。前任の「1 窓 = .py 丸ごとにテキスト追記」（0049）でも、grill 中盤に検討した
「1 窓に箱を縦積み（案B）」でもない。**案A 確定**。

方針: ADR-0012（target authored = marimo cell-DAG）/ findings 0046（thin-drain runtime）/
0010（編集コア）/ 0044（WYSIWYR）/ 0025 §8（adopt 不変）/ 0049（編集コア・検知ミラーの流用元、設計前提は破棄）。
backcast に FLOWS.md は無いため本 findings が設計＋検証の正本。ADR-0012 は参照のみ（書き戻さない）。

## モデル（owner 確定・marimo 実ソース裏取り済み）

- **1 セル = canvas 上の 1 窓**（`strategy_editor:region_NNN`）。marimo `CellFlowNode`
  （`frontend/.../cell-flow-node.tsx`・className `"cell-3d-wrapper floating-window"`・titlebar drag・
  source/target `Handle` で依存矢印）と同型。drag/z-order/位置永続化は既存 floating window 基盤を流用。
- **窓に映るのはセル本体だけ**。`@app.cell` / `def _(refs)` / `return defs` は画面に出さない
  （marimo も編集対象は `cellData.code` 本体のみ・ラッパは codegen 形式）。
- **reactive DAG**: 隠れた `def _(refs)` 引数と `return (defs)` は marimo が変数解析で自動生成。窓間の
  依存矢印は同じ refs/defs の可視化。**実行に矢印は不要**（reactive 解決は marimo 側で閉じている）。

## seam（C# = 空間 UI / Python(marimo) = 合成・分解・DAG）— **spike で凍結（GREEN）**

C# は「セル本体テキスト + 窓位置」だけを持ち、def/ref 解析・return 合成・DAG 解析を C# に再実装しない
（[[ttwr-parity-first]]）。Python(marimo) 純正関数を in-proc で直接呼ぶ:

- **合成（N 本体 → 1 .py）**: `marimo._ast.codegen.generate_filecontents(codes, names, cell_configs, config)`
  （`codegen.py:537`）。内部 `to_functiondef`（`:282`）が `cell.refs`→`def _(refs)` 引数、`cell.defs`→
  `return (defs)` を自前計算。
- **分解（1 .py → N 本体）**: `marimo._ast.load.load_app`（`load.py:161`）→ `app._cell_manager.codes()/names()/configs()`。

### spike 実証（throwaway・`python/spike/marimo_cell_synthesis*.py`）

1. `marimo_cell_synthesis_spike` **ALL PASS**: 生本体3つ→`generate_filecontents`→`load_app` が
   (a) 3 セル収集 (b) 本体だけ復元（ラッパ隠蔽）(c) `本体→.py→本体→.py` がバイト idempotent。
2. `marimo_cell_synthesis_exec_spike` **ALL PASS**: `generate_filecontents` 出力（2 セル・cross-cell
   edge `qty`・host API が **arg 形** `def _(get_bar):`・run-guard 付き）を実 `KernelRunner` +
   `MarimoStrategy` で走らせ、命令型 twin と **order/fill/equity 完全一致**（200 fills・両側）。

### 凍結した事実（実証済み・後段で前提にしてよい）

- **canonical-form 差分は無害**: `generate_filecontents` 出力は backcast 既存の footer-less 正準形と違い、
  `__generated_with = "<version>"` 行＋`if __name__ == "__main__": app.run()` フッタを**必ず付ける**。
  だが `load_app`／adapter／KernelRunner はそのまま受理し実行も一致。**新 on-disk 正準形 = `generate_filecontents` 出力**に統一する（既存 golden / `test_marimo_strategy_adapter.py` の手書き footer-less 形は更新対象）。
- **host API が arg 形になっても動く**: 既存手書き形は `get_bar`/`submit_market` を free ref（globals 解決）に
  していたが、`generate_filecontents` は arg（`def _(get_bar):`）に昇格させる。marimo は未定義 ref を globals
  から解決するので host-seeding と両立（exec spike で実証）。**C# 側の skeleton/検知は free-ref 前提を引きずらない**。
- **version 行の二重管理に注意**: `__generated_with` に marimo version が焼き付く。provenance 既出なら golden は
  version 非依存に書く（行を mask）か、version 固定を明示する。

## 段階（owner 確定）

- **Slice 1（最初の出荷単位・矢印なし）**: セル窓（本体だけ）/ [+] で新窓 spawn / 削除 / drag・z-order・位置永続化 /
  Save = `generate_filecontents` で 1 .py / Open = `load_app` で N 窓に分解 / provider = 合成済み 1 .py を供給。
  → 「空間上で複数セルの marimo 戦略を編集して Run」が成立＝出荷可能。
- **Slice 2**: 依存矢印の描画（marimo refs/defs → 窓間エッジ・純粋可視化）。
- **Slice 3**: 明示並べ替え順 UI・セル名 等。

## 設計の木（grill 進行中・確定次第追記）

- [x] **document/provider モデル → ADR-0013**: `MarimoNotebookDocument`（ノート集約）が 1 .py/パス/
  dirty/provider を持つ。各セル窓 = 本体断片の軽量エディタ（path/Open/Save 無し）。`StrategyDocument` を
  上下に分割（I/O+supplyable→集約・buffer→セル窓）。`EditorFileProvider` は集約を指す（供給口は 1 パスのまま）。
- [x] **本文の所有 = 集約（案A 確定・marimo 中央ストアと同型）**: 「buffer→セル窓」は字義上 (B) に読めるが、
  marimo 実装は (A)＝**中央ストアが順序付きセル本文の真実源・窓はビュー**（`cells.ts` の `cellData[id].code`＋
  `inOrderIds`、`cell-flow-node.tsx` は `useCellData().code` を読むだけ、位置は別ストア `cell3DPositionsAtom`）。
  集約の順序付きセルリストがセル本文の真実源で、窓は自分のセルを映す/編集するビュー。編集 = 窓→セル→集約 dirty の1経路。
  **直列化は2本に割り再結合しない**: 論理セル内容（**本体＋名前＋設定**）＋順→`.py`（`generate_filecontents`）／位置→layout sidecar の cell-order 並走
  リスト（id キー無し）。位置を `.py` に焼かない。dormant region_001（殻）は本文も位置も持たない＝論理セルと自然に分離。
- [x] **既存レイアウト永続化との衝突の落とし所（案A の帰結）**: 今の `strategyEditors = {id, filePath}`（窓 id キー・
  per-window filePath）は本 findings と正面衝突。(A) の帰結として **per-window filePath は廃止**（窓はファイルを持たない・
  集約が 1 つの `.py`）。cold-open で復元する位置の正本は**窓 id 辞書ではなく cell-order 並走リスト**。`floatingWindows` の
  x/y/w/h は live 窓の geometry として残るが、cell 位置の永続正本ではない。窓 id は runtime ホスト識別に降格。
- [x] **物理窓 ≠ 論理セル / adopt = hide-not-destroy → ADR-0013**: region_001 = never-Destroy の殻。
  セル削除は region_002+ なら Despawn、region_001 なら hide(SetActive(false))→dormant。新セルは dormant
  region_001 を最優先で再利用。セル identity を窓 GameObject に固定しない（位置はセルに紐づく＝窓が飛ばない）。
- [x] **ノートは常に ≥1 セル**（marimo `canDelete={!hasOnlyOneCell}`）: 削除で 0 セルに到達しない。
  0 セルは File→New / 空 .py を開いた一過性のみ → [+]/bootstrap で cell 1。dormant region_001 は論理セル数と独立。
- [x] **セル順の正本 = `.py` ファイル順**（`generate_filecontents` の `codes` 順 = `load_app` の返却順）。
  reactive DAG なので**並び順は実行に無関係**（marimo は依存解決・論理順 `inOrderIds` と空間 `cell3DPositionsAtom`
  は別 atom で分離）。空間位置（窓 x,y）はセル順と**独立**＝ドラッグしても `.py` 順は変わらない（diff 安定・WYSIWYR）。
  [+] = 末尾 append。reorder は Slice 3（S1 は作成順固定）。
- [x] **レイアウト永続化の所有分割＋キー**: 本体＋順 = `.py`（正本）／空間配置 = sidecar。**位置は窓 id でも cell id でもなく
  「`.py` セル順に並走する順序付きリスト」で持つ**（`position[i] ⟷ cell[i]`）。理由: marimo `CellId` はランダム 4 文字生成で
  `.py` に焼かれず（`ids.ts:23`）、cold-open で id が振り直され位置 lookup が壊れる（marimo 本体も 3D 位置を cold-open で失う）。
  順リストなら順が保たれる限り append/delete/cold-open をまたいで窓が飛ばない（marimo より忠実度が上）。Save: 同一スナップショットから
  `.py` と位置リストを同順で書く。Open: `load_app` → 本体 N（順付き）→ `cell[i]` に `position[i]`（無し/長さ不一致→既定グリッド）。
  窓 GameObject（region_001=never-destroy）は live ホストにすぎず永続キーにしない。
- [x] **位置スキーマの具体 = `cellPositions: [{x,y}, …]`（cell 順並走・id 無し・確定）**: 両権威一致で **x,y のみ**焼く。
  - **w/h を焼かない**: S1 はリサイズ不可（`FloatingWindowSpec.cs:6` no resizable・`FloatingWindowHitlHarness.cs:18/239` resize は #15 scope 外）＝全セル同一既定サイズ・spawn 時に `sizeDelta` を当てるだけ。marimo `position3D = {x,y,z}` も w/h を持たない（`types.ts:67`・ReactFlow は content サイズ）。旧 sidecar に w/h 無くても既定適用で forward-compat 損なし。
  - **z を焼かない**: cold-open の z = **cell 順 = spawn 順**（`cell[i]→region` を i 昇順 Spawn＝後の i が前面）で決定的。findings 0050 の「S1 に z-order」は runtime focus→`BringToFront`（live で効く）を指し cold-open 永続ではない（marimo も z は ephemeral focus zIndex 1000＝忠実）。z 永続は Slice 2/3。
  - **構造**: セル窓は `floatingWindows` から**除外**（`Capture()` が `strategy_editor:*` をスキップ・集約が `cellPositions` を書く＝単一正本／二重保存と「id 勝ち vs index 勝ち」根絶）。`floatingWindows` は Order ticket 等の非セル窓専用に戻す。
  - **「窓が飛ばない」AC は x,y 並走だけで満たせる**（サイズ定数・z 決定的順）。w/h・z はリサイズ/z 永続が来る将来 slice の **additive 拡張として予約（今フィールドを作らない）**。
- [x] **セル lifecycle UI（marimo 3D 実ソース確認済み・findings 0049 を 2 点訂正）**:
  - **[+] 配置**: 画面固定オーバーレイ・**水平中央**・**1 個**（`edit-app.tsx:454` `relative z-50 flex justify-center
    pointer-events-none` + `AddCellButtons`）。findings 0049 の「右下固定」は text-append 用の遺物＝**撤回**。S1 は
    「**+ Python cell**」ボタン1個のみ（Markdown/SQL/AI は #81 非目標 full notebook UI）。クリック=末尾 append・コード無し
    （`cell-array.tsx:279-288` `createNewCell({type:"__end__"}, before:false)`）。pixel 位置は chrome 衝突回避で調整可（ADR-0003）。
  - **新セル本体 = 空**（findings 0049 の `_bar/_pf/_qty/submit_market(_qty)` skeleton は text-append 遺物＝**破棄**）。
    `createNewCell` の Python ボタンが code を渡さない＝本体 `""`。空なので「足しただけでは誤発注しない」は自動成立（no-op 仕掛け不要）。
  - **placeholder ヒント**: marimo `showPlaceholder = hasOnlyOneCell`（`cell-3d-renderer.tsx`）＝**単一セル時のみ** CodeMirror
    placeholder として表示（本体に文字は入れない）。**backcast 固有**: 注入グローバルは marimo 汎用ノートに無いので、この単一セル
    placeholder 文言に `get_bar()` / `get_portfolio()` / `submit_market(qty)` を書く（本体は空・option (a)）。テンプレ挿入 affordance
    （option (b)）は (a) 不足時の Slice 3 fallback。**本体への seed 焼き込みは却下**（marimo 非忠実＋毎回「消すべき例」が付く）。
  - **spawn 位置 = 3分割で確定**（`calcSpawnPosition` 移植・marimo `cell-3d-renderer.tsx:45-81` 裏取り済み）:
    - `static Vector2 SpawnPlacement.Next(IReadOnlyList<Vector2> existingTopLefts, Vector2 anchorTopLeft, float offset)` — **純関数・層1 AFK で単体テスト**。
    - `RectTransform FloatingWindowController.SpawnAuto(kind, id, w, h, Vector2 anchorTopLeft, bool visible)` — `_windows`→全窓 top-left 集約→`Next`→`Spawn`。衝突回避は窓集合を持つ controller の責務。
    - `BackcastWorkspaceRoot.OnAddCell` — viewport 中心を canvas-logical へ変換して `anchorTopLeft` を渡すだけ。
    - **marimo 忠実の釘**: `SPAWN_OFFSET=30`／衝突しきい値 `<10`／x と z（=x,y）に同一 offset を足す**対角カスケード**（baseX+offset, baseZ+offset）／canvas-logical 単位。**窓は重なってよい**（macOS 新窓カスケード型・「完全非重複に改善」しない＝非忠実回避）。しきい値10 =「ほぼ同一点でなければ可」。
    - **anchor 意味の確定**: anchor = viewport 中心点を**基準 top-left としてそのまま使う**（半サイズ中央寄せ補正をしない＝ヘルパに w/h 不要）。衝突判定も top-left 同士。真の centering が要れば呼び出し側の半サイズ補正＝ヘルパの責務外。→ 層1 assert は「中央起点・対角カスケード・非衝突」を w/h 抜きで純粋検証。
    - **衝突母集合 = `_windows` 全窓 top-left**（cell だけでなく Order 窓等も避ける＝新セルが既存窓の真下に湧かない・非忠実でもない）。**#15 境界維持**: ヘルパ/overload は rect＋anchor だけ＝content 非依存（cell 固有性ゼロ）。
  - **削除** = 窓タイトルバーの X・`canDelete=!hasOnlyOneCell`（≥1）。region_002+ は Despawn・region_001 は hide(dormant)（ADR-0013）。
  - **focus で前面** = `zIndex 1000` → 既存 `FloatingWindowController.BringToFront`。
- [x] **調整役 = 新規 `NotebookCellCoordinator`（MonoBehaviour 非依存・DI・確定）**: spawn/delete/open/save の調整が増えるため root に同居させず抜く。**先例 = `WorkspaceEngineHost`**（CONTEXT.md/ADR-0010 が「root を engine orchestration の置き場にしない・駆動は host へ分離」と明記）と同型・同 altitude 原則（ADR-0009）。利点: root 肥大（1838 行）回避＋層1/2 AFK が full scene 無しで `AddCell/DeleteCell/Open/Save` を直接駆動（fake synthesizer・fake controller 注入）。
  - 保持: 集約 `MarimoNotebookDocument`・`region id ↔ Cell` 束縛・依存（`FloatingWindowController`・`IMarimoSynthesizer`・spawn コールバック）。root は配線（adopt・viewport 変換・X コールバック）だけ委譲。
  - **前提（移行の束ね方）**: この抽出は **Q3 の `StrategyDocument` 退役と同一の移行**。今 root に同居する `OnAddCell`/`Open`(L367)/`Save`(L1613) を coordinator へ移すこと自体が Open/Save を集約対象へ作り替えること。`NotebookCellCoordinator`＋`MarimoNotebookDocument` は**一括で来る**（root はその 3 メソッドを gut して委譲）＝別タスクに数えない。
- [x] **削除ルーティング（`DeleteCell` 確定・罠2点を回避）**:
  - ① **≥1 ガード** `canDelete=!hasOnlyOneCell`（marimo 忠実）。② 集約リストから Cell 除去→**dirty**（構造変更=dirty）。③ **region_001→hide(`SetActive(false)`・deregister しない＝adopt/dormant)／region_002+→`Close`(despawn＋deregister)**。
  - **罠①（位置の詰め方）**: `cellPositions` を「並走配列を splice して re-index」しない。位置は live な (Cell, 窓位置) に紐づき、`cellPositions` は **Save 時に cell 順で再生成する派生物**。削除すれば再生成で自然に詰まる。手で詰めると index-drift バグ族を呼ぶ → **「regenerate from live」を契約**に。
  - **罠②（dormant 再利用の位置）**: `AddCell` が dormant region_001 を再利用するとき、再利用するのは **GameObject の殻だけ**（Destroy/Instantiate 回避）。位置は region_001 の**古い隠れ座標ではなく** `SpawnPlacement` の新規 spawn 位置（viewport 中心＋cascade）を当てる。さもないと新セルが「前に region_001 がいた場所」に湧く。
  - **窓↔セル束縛の健全性（(A) を採った理由）**: 束縛は Cell identity（`region_id ↔ Cell` 参照）で持つ。中間セルを消してリストが re-index されても**他窓の表示は rebind/ジャンプしない**。region_001 の id は**順序を意味しない**＝dormant 再利用で末尾セル（[+]=末尾）を載せて良い。id = never-destroy の殻識別子であって「最初のセル」ではない（物理窓≠論理セルの decouple）。
  - **X ボタン**: 共有 `StrategyEditorWindowFrame` に新設（adopt+spawn 一致・findings 0025 §8）→ クリック → `coordinator.DeleteCell(id)`。
- [x] **Open/Save/New の機構**（ADR-0013＋Q4 から導出）:
  - **Save**（File→Save / Save As）: 同一スナップショットから live セル本体を `.py` 順に集め `generate_filecontents` → `.py` 書き出し
    ＋ 位置リストを同順で sidecar へ（`position[i]⟷cell[i]`）。Save で dirty 解消。Save As = picker + path rebind。
  - **Open**（#80 `OnOpenStrategy`/`EnumerateStrategies`）: `load_app` → `codes()`（順付き）→ cell 0 を dormant/adopt 済み region_001 へ・
    cell 1.. を region_002+ spawn、各々 sidecar `position[i]`（無し→既定グリッド）。
  - **File→New**: 空ノート（unbound・path 無し）＝**空セル 1 個を region_001 に**（marimo File→New = 空セル 1 個）。Save As で path 束縛。
  - **空 .py / 0 セル の cold open**: `load_app`→None/0 セル → region_001 に空セル 1 個を bootstrap（≥1 ルール）。

- [x] **`StrategyDocument` 作り替え = 新規集約＋軽量 `Cell`・`StrategyDocument` 退役（確定）**: 責務が 2 方向に分裂するためリネーム流用せず **新規 `MarimoNotebookDocument`** を起こす。持つもの = path/dirty/`_openedOrSaved`/順序付き `List<Cell>`/`Save`（`Cell` 本体を順に集め `Synthesize`→`WriteAtomic`）/`Open`（read→`Decompose`→cells 充填）/`SaveAs`/`ResetUnboundEmpty`/`TryGetStrategyFile`。`Cell` = 本体テキスト＋**名前＋設定（S1 は非編集・opaque 運搬）**＋集約へ notify だけ（path/Open/Save/provider 無し・位置は焼かない）。
  - **退役の前提（grep で裏取り済み）**: production の concrete `StrategyDocument` 参照は編集/レイアウト系のみ（`StrategyEditorView`・`StrategyEditorContentBuilder:24` factory・`StrategyEditorRestore:20`・`AtomicFile:12` はコメント）。run/供給経路は `RegistryStrategyFileProvider`→registry→`IStrategyFileProvider` で **インターフェース疎結合**＝集約が `IStrategyFileProvider` を同 id で登録すれば run 経路無改修。→ 退役 OK・共存不要。
  - **退役 = 契約の移送（delete ではない）**: probe 群（`StrategyEditorProbe §3` file-model・`MultiDocLayoutProbe §4` SaveAs・`StrategyPickerProbe §5` stale-guard・`BackcastWorkspaceProbe` AdoptedDoc）が固定する I/O 契約（atomic-replace 保存・SaveAs が新 `.py` を書き旧ファイルを独立に残す・消えた `.py` を Open が弾く・stale-list guard）を**集約へ運び probe も移行**。落とすと file-model 回帰網が消える（ADR-0013 Consequences に記載）。
    - **更新（#76 命令型 sunset・2026-06-19）**: `StrategyPickerProbe` は #76 で `git rm` 済み（#80 ピッカー退役）。その `§5` stale-guard 契約（消えた `.py` を Open が弾く）は **`StrategyEditorProbe` S3:275（`Open(missing .py)→false`）が直接等価で保持**しているので、#81 が `StrategyEditorProbe §3` を集約へ運べば自動で移行する（別途 StrategyPickerProbe を見に行く必要はない＝該当ファイルは存在しない）。詳細は `docs/findings/0047` §5。
  - **意味論シフト（二重カウント防止）**: 新モデルは N 窓→1 ノート→1 `.py` なので **`SaveAs` = ノート全体を新パスへ**（窓単位 rebind ではない）。窓ごと filePath レイアウトは Q4 で退役（id キー→cell-order index 並走）。よって `MultiDocLayoutProbe`／`StrategyEditorRestore` の移行は **Q4 レイアウト・スキーマ移行と同一作業**＝別タスクに数えない。
  - **dirty 源は本文編集だけではない（確定）**: セル view→`Cell.SetBody`→集約 dirty／dirty は集約に 1 つ／`TryGetStrategyFile` は dirty 中 false（findings 0044）／Save・Open 成功で解消。**add／delete／reorder も `.py` を変えるので集約 dirty を立てる**（`Cell.SetBody` を唯一の dirty 源にしない＝「窓追加したが未保存」も供給不可へ正しく倒れる）。
- [x] **断片エディタの作り替え＋provider 键の logical 分離（確定）**:
  - **`StrategyEditorView` を断片ビューへ作り替え（新クラス起こさない）**: InputField/`EditHistory`/highlight/`ApplyTextAndSelection`（保持確定）の機構をそのまま要するため、`Document` 束縛→`Cell` 束縛の 1:1 差し替え（Q3 退役と同論法）。テキスト同期 `Document.SetText`→`Cell.SetBody`（→集約 notify=dirty）。view から `Open`/`Save` 撤去 → coordinator/集約へ。
  - **registry 登録を view から外す**: `StrategyEditorContentBuilder.Build` は Cell 束縛の断片 view を作るだけ・registry 非接触。**唯一の provider = 集約**（coordinator が登録）。
  - **`EditHistory` はセル窓ごと**（marimo 忠実: 各 cell が独立 CodeMirror・独立 history）。cell A の Ctrl+Z は cell A だけ。
  - **provider registry 键 = logical `strategy_editor:notebook`（流用却下・確定）**: `region_001` は物理窓 id として **5重に現用**（provider lookup L251／view 辞書 `_editors` L343/370/469/1633／reveal `Show` L355／adopt L465／title・layout filePath L467,1666-67）。新モデルで用途 2〜5 は物理窓として正しく残る（region_001 は実在 adopt 窓・`_editors` は region_001..N の per-窓 view マップ）。だが provider 键は「実行する `.py` を供給する **ノート（論理集約・窓ではない）**の id」＝意味的に窓 id ではない。`region_001` 流用は「窓でない物を 5重物理窓 id で指す」stale-name で through-line（③・(A)・「id で键しない」）に逆行。→ **provider 键だけを `strategy_editor:notebook` へ分離**し run 経路を向け直す（region_001 は消さない）。コスト些少: `NOTEBOOK_ID` const 1 個＋coordinator が集約を 1 回 Register＋`MenuBarHitlHarness.cs:107` の stub 登録键を新键へ。「流用=ゼロ改修」は弱い（登録側はどのみち変わる）。
- [x] **合成/分解 seam = `IMarimoSynthesizer`（インターフェース注入・確定）**: 旧 text-append は純 C# だったが新モデルは Save に合成・Open に分解（marimo `generate_filecontents`/`load_app`）が必須。集約が pythonnet 直結だと層1 が純粋でなくなるため**差し替え可能な seam の向こうへ追い出す**（既存 `IStrategyFileProvider`＋`RegistryStrategyFileProvider`＋`StrategyProviderRegistry` と同じ畳み方）。
  - **シグネチャ確定（名前・設定込みに訂正・旧 bodies-only 版は破棄）**: `Synthesize(IReadOnlyList<Cell> cells) → string py`（cell = **body+name+config**・順 = リスト順 = `.py` セル順正本）／`Decompose(string py) → IReadOnlyList<Cell>`（`load_app` の `codes()/names()/configs()` を**全部拾う**・ラッパ剥離済み）。
  - **なぜ訂正したか（#76 衝突裏取り）**: 旧凍結署名 `Synthesize(bodies)`／`Decompose→bodies` は **cell 名・config を捨てていた**が、#76 本番 `v19_morning_cell.py` は可読のため **named cell**（`def _config()`/`def _feedback()`/`def _strategy()`・`test_marimo_strategy_adapter.py` も `_signal`/`_bars`/`_rebal`/`_svc`）。bodies-only だと v19 を開いて保存しただけで `def _config()`→`def _()` に潰れ **#76 の本番 artifact を破壊**。しかも seam 凍結の根拠 spike は実は `generate_filecontents(codes, names, cell_configs)` で **names/configs を往復保存していた**＝凍結署名が spike 実態を取りこぼした実バグ。名前喪失は実行的には無害（marimo は cell 名を実行に使わない＝exec parity 不変）だが artifact 破壊＋下記 idempotency の嘘を生む。
  - **往復 idempotency の前提**: 「往復でバイト同一」は spike が **names+configs を運んでいたから**成立。署名を名前込みに戻して **named-cell ファイルでも真**にする（bodies-only 版では偽だった）。
  - **S1 は名前・設定を編集しない**（名前 UI は Slice 3）: Open で拾い Save でそのまま書き戻す **opaque 運搬**のみ。新規セルは名前なし→ marimo 既定 `_`（無名）・既定 config で合成（＝marimo の新セルそのもの・「空セル」と一致）。名前が付くのは named ファイルを開いたときだけ。**2経路分けは不変**: 論理セル内容（本体＋名前＋設定）＋順→`.py`／位置→別 sidecar（位置に名前は混ざらない・Q3/Q5）。
  - **機構**: 本番 `PythonnetMarimoSynthesizer` は **`WorkspaceEngineHost` 経由で Python を触る**（独自 `PythonEngine` を持たない）。`Py.GIL()` 下・起動時初期化済み（`Awake` L218-221）の host を再利用。**single Python owner（ADR-0009）を壊さない**＝第二の Python 所有者を作らない。subprocess/再実装はしない。
  - **fake drift 防止（最重要）**: 層1 の fake が現実とズレると「fake に GREEN」になる。fake は本物と同じ契約を満たす（`Decompose(Synthesize(bodies)) == bodies` 順保存往復同一・≥1 ガード）。この契約を**層2（実 pythonnet 1 回）＋層3（実 marimo）が同一シナリオで assert**＝findings 0049 の共有 golden fixture 規律（C# 出力＝両側が読む golden）で fake↔本物の乖離を機械検出。
  - **エラー契約**: `Synthesize` は不正本体でも **throw しない**（marimo `safe_serialize_cell→generate_unparsable_cell` が吸収・確認済み）。`Decompose` が壊れた `.py` で失敗したら **fail-soft**（`ShowMessage` 通知のみ・バッファ非破壊・「供給可能」は false のまま path を返さない＝findings 0044 契約）。throw を UI まで上げず seam の戻り値で表現。
  - **C# 検知ミラーは持ち込まない（findings 0049 から撤回・確定）**: 非 marimo な `.py` の Open は `load_app`（None/失敗）に委ね、seam の fail-soft（バッファ非破壊＋notice）で倒す＝**Python が権威**。findings 0049 の「保守的 `is_marimo` C# ミラー」は新モデルで不要。

## S1 検証層（findings 0049 の 4 層を踏襲・backcast に FLOWS.md 無し＝本 findings＋AFK probe＋pytest golden が正本）

1. **pytest golden（層3・spike を昇格）**: `generate_filecontents`↔`load_app` 往復 idempotency・exec parity（spike 2 本を tests/ の
   golden fixture 方式へ）。version 行 mask（`__generated_with` 非依存）。
2. **純粋 C# AFK probe（層1）**: ノート集約の合成順・dirty 遷移・空セル・≥1 削除ガード・dormant region_001 再利用（UnityEngine-free コア）。
3. **view AFK probe（層2）**: 実 InputField で本体編集→Save 往復・窓 add/delete/位置永続→cold open で窓が飛ばない・provider 供給可能判定。
4. **HITL（層4）**: 実 workspace で [+]→中央 spawn→本体編集→Save→Open で位置復元→複数セル戦略を Replay run（AC5）。

## 実装着地 — Step 0+1（2026-06-18・引き継ぎセッション）

S1 実装の **Step 0（前任 text-append 成果物の作り直し）と Step 1（層3 pytest golden 先行）** を完了。owner が
Step 0+1 を選択（C# コア＝Step 2 に入る前に Python 側の正本を GREEN で固める）。Step 2/3 は次セッション。

- **Step 0（撤去・確定）**: `MarimoCellInsert.cs`(+meta) 削除／`StrategyEditorView` の `InsertMarimoCell`・
  `FocusSelection` 撤去（**`ApplyTextAndSelection` 抽出は保持**＝undo/redo の唯一所有者・Step 2 の本体プログラム編集で再利用）／
  `BackcastWorkspaceRoot.BuildAddCellButton`（右下）・`OnAddCell` と呼出を撤去（新モデルの中央 [+] は Step 2 で coordinator 経由）／
  `StrategyEditorProbe` §10/§11＋helper（`CompareGolden*`/`Norm`/`FixtureDir`/`CountOccurrences`）＋chain＋PASS サマリ撤去／
  旧 `python/tests/test_marimo_cell_insert_golden.py`＋`fixtures/marimo_cell_insert/` 削除。
  **保持**: `FloatingWindowController.Show`（reveal+raise＝dormant region_001 再利用の流用先・Step 2）、`CONTEXT.md` の
  新モデル glossary（cell window / notebook aggregate）。**C# 検知ミラーは持ち込まない**を維持（findings 0049 から撤回済み）。
  Unity scene の再シリアライズ churn と `EditorBuildSettings.asset` の改行差分は #81 の意図ではないため未改変（Step 2 で scene UI を起こす）。
- **Step 1（層3 golden・GREEN）**: spike 2 本を恒久ゲートへ昇格。
  - `python/tests/fixtures/marimo_cell_synthesis/two_cell_dag.py` = `generate_filecontents` の**バイト忠実**出力
    （`_write_golden()` で再生成・手書きしない）＋README。2 セル・cross-cell edge `qty`・host API（get_bar/submit_market）が
    **arg 形** `def _(get_bar):`・`return (qty,)`・run-guard footer・`__generated_with` version 行。
  - `python/tests/test_marimo_cell_synthesis_golden.py`（**6 passed**）: ① synthesis-form（version 行 mask で golden 一致）
    ② decompose 復元 ③ `本体→.py→本体→.py` バイト idempotency ④ host API/DAG edge の arg・return 構造 ⑤ exec parity
    （実 `KernelRunner`+`MarimoStrategy` で命令型 twin と order/fill/equity 一致）。
  - `test_marimo_strategy_adapter.py` を **canonical 形へ更新**（**7 passed**）: 4 つの `_MARIMO_*_SRC` 手書き footer-less 形を
    `_synth(bodies)`＝`generate_filecontents` 出力に差し替え。arg 形でも services=/constants= passthrough が parity（最リスク箇所）
    を確認＝編集器が書き出す canonical 形を本番 adapter がそのまま実走する裏取り。
  - **検証**: 対象 13 tests GREEN／**全 Python スイート 368 passed**（撤去・正準化による回帰なし）。
- **code-review(simplify) 着地（Medium+ 解消）**: ① `ApplyTextAndSelection` の `if (_input != null)` ガード撤去＝
  挙動同値の純リファクタへ（唯一の呼出 undo/redo は `_input` null 時 early-return ＝ガードは未テストの防御コードで desync を隠す恐れ）。
  ② adapter test の import 時 `generate_filecontents` を**遅延化**（body リテラル＋write 時 synth）＝marimo codegen API drift が
  module collection error でなく該当 1 test の失敗に局所化。③ golden test の tmp 書込を `newline=""`＝disk 往復をバイト忠実 LF に。
  **保持（plan 準拠の judgment）**: `FloatingWindowController.Show`・`ApplyTextAndSelection` 抽出は Step 2 再利用 infra として保持。
  test harness の cross-file 重複は LOW（分離許容）。
- **C# compile-only gate GREEN（2026-06-18）**: Unity 6000.4.11f1 を `-batchmode -nographics -quit`（executeMethod 無し）で回し、撤去＋null-guard 撤去後の全スクリプトが **`error CS` 0 件・`Application will terminate with return code 0`・assembly reload 成功**（touched files に error/warning 無し）。前任 text-append の撤去は HEAD への復帰（prior-session の未コミット追加を除去）＝commit `c4e432f` には FloatingWindowController(+Show)/StrategyEditorView(ApplyTextAndSelection 抽出)/docs/tests のみが入り、Probe/Root は HEAD 一致で差分無し。

**残（次セッション・Step 2/3）**: C# コア一括（`MarimoNotebookDocument`＋`NotebookCellCoordinator`＋`IMarimoSynthesizer`
＋本番 `PythonnetMarimoSynthesizer`＋`SpawnPlacement`/`SpawnAuto`＋中央 [+]＋X ボタン＋root の OnAddCell/Open/Save gut→委譲
＋レイアウト・スキーマ移行 `cellPositions`）→ 層1/2 AFK probe → 層4 HITL（AC5）。**C# コンパイルゲートは Step 0 の撤去について
owner が Unity batchmode で要確認**（ローカルで C# を実コンパイルしていない）。

## 実装着地 — Step 2（2026-06-18・引き継ぎセッション・merge commit `89a0c23`）

S1 **Step 2（C# コア一括）** を凍結プラン通りに実装し、`origin/main` の #76（C# UI cutover）を merge。compile gate＋層1/2 AFK＋層3 pytest が全て GREEN。**層4 HITL（AC5）のみ owner 実行待ち**。

### 成果物（新規）
- 純コア（UnityEngine-free）: `Cell`（body+name+config opaque・dirty notify）／`IMarimoSynthesizer`（`Synthesize(cells)→py` / `Decompose(py)→cells`・fail-soft null）／`MarimoNotebookDocument`（集約＝1 .py/dirty/順序付き cells/Save・Open・SaveAs・ResetUnboundEmpty・≥1 削除ガード・TryGetStrategyFile 5条件・`StrategyDocument` の atomic-write 契約を移送）／`SpawnPlacement.Next`（純関数 cascade）。
- seam: `python/engine/strategy_runtime/cell_synthesis.py`（`synthesize_json`/`decompose_json`＝`generate_filecontents`/`load_app` を names+configs 込み往復）＋本番 `PythonnetMarimoSynthesizer`（`WorkspaceEngineHost.SynthesizeCells`/`DecomposeCells` 経由・`Py.GIL`・single Python owner=ADR-0009）。
- 空間 UI: `FloatingWindowController.SpawnAuto`/`Hide`/`CaptureTopLefts`／`NotebookCellCoordinator`（region↔Cell・AddCell/DeleteCell/Open/Save・dormant region_001 再利用・≥1 ルーティング）／`StrategyEditorWindowFrame.EnsureCloseButton`（X）／`StrategyEditorView` を Cell 束縛へ作り替え（Document/registry/file-ops 撤去・`ApplyTextAndSelection`/EditHistory/highlight 保持）／中央 [+] overlay。
- root: provider 键＝logical `strategy_editor:notebook`／OnFileOpen・Save・SaveAs・New・OnOpenStrategy・Resume を coordinator へ委譲／`CaptureLayout` で cell 窓を `floatingWindows` から除外＋`cellPositions` 追加／`SetSynthesizer` 注入 seam。
- レイアウト・スキーマ: `LayoutDocument.cellPositions:[{x,y}]`（cell 順並走・id 無し）＋`LayoutStore.NormalizeCellPositions`＋Clone/StructurallyEqual（index 一致）。per-window `strategyEditors` は cell 用途では退役（field は additive 残置）。

### `StrategyDocument` の扱い（退役の現実解）
production の編集経路は `MarimoNotebookDocument` へ全面移行。`StrategyDocument`/`StrategyEditorRestore` は **削除せず regression net として残置**（`StrategyEditorProbe §3/§4/§7` が I/O 契約を引き続き固定）。集約は同契約（atomic-replace・SaveAs fork・vanished-reject・5条件 supplyable）を移送して持つ＝契約の二重保証。`StrategyEditorHitlHarness`＋その menu は**退役（削除）**＝単一バッファ前提の遺物で、層4 HITL は workspace root 本体で行う。

### #76 ↔ #81 マージ調停（merge `89a0c23`・正本: 本節）
- **U1 ▶ Run ボタン**（#76）= 維持。`RunReadinessViewModel` は `strategyReady` を `EditorFileProvider`（→ NOTEBOOK_ID → 集約）から取る純ロジックなので #81 と無改修で両立。
- **U2 File→New の template seed**（#76）= **#81 が supersede**。findings 0050 は「新セル本体への seed 焼き込みを却下（marimo 非忠実）」と凍結済み → File→New = 空セル 1 個＋placeholder。`MarimoStrategyTemplate.NewStrategy` は dead（残置）。
- **U3 起動時 canonical v19 を開く**（#76）= **採用**。ただし `editor.Open` は退役済みのため **coordinator 経由**（`OpenCanonicalDefault` → `_coordinator.Open(v19, sidecar positions)`）に配線し直し、N セル窓へ分解。失敗時は空ノートへ fallback。untitled 維持（canonical の sidecar を boot/quit が書かない）。
- 共有 4 ファイル（`BackcastWorkspaceRoot`/`WorkspaceEngineHost`/`StrategyEditorView`/`MenuBarCutoverProbe`）の conflict を上記方針で解決。`WorkspaceUiCutoverProbe`（#76 新規・`editor.Document` 参照）も notebook 版へ移行。

### 検証（全 GREEN）
- **層3 pytest**: `test_marimo_cell_synthesis_golden.py`（9）＋`test_marimo_strategy_adapter.py`（7）= 16 passed（merge 後）。entry-point seam に named-cell 往復 idempotency＋fail-soft＋新セル既定の 3 test を追加。
- **C# compile gate**: merge 後ツリーを `-batchmode -nographics -quit` で `error CS` 0 件・return code 0。
- **層1/2 AFK**: `StrategyEditorProbe`（集約・SpawnPlacement・coordinator の新 §10/11/12＋既存 §1-9）／`MenuBarCutoverProbe`（File→New cell reset）／`WorkspaceUiCutoverProbe`（#76 U1/U3＋#81 notebook boot）／`BackcastWorkspaceProbe`（#78/#81 seeding）= **4 本とも PASS**。fake synthesizer（`FakeMarimoSynthesizer`・Editor）注入で Python-free に駆動。
- **層4 HITL（AC5）GREEN（2026-06-18・owner 実行）**: 実 workspace（Unity 6000.4.11f1 Play）で boot→canonical v19 が 3 セル窓（`_config`/`_feedback`/`_strategy`・本体のみ）へ分解→中央 [+] で新セル spawn→本体編集→Save→Open で位置復元（窓が飛ばない）→複数セル戦略を Replay run→✕ 削除（≥1 ガード）まで一連を owner が確認し **PASS**。→ **S1 は全 4 層（pytest golden / 純 AFK / view AFK / HITL）GREEN ＝ 出荷可能**。残スコープは Slice 2（依存矢印）/ Slice 3（並べ替え UI・セル名編集）。

正本: `docs/adr/0013` ＋ 本 findings。ADR-0013 は immutable（書き戻さない）— 本節が Step 2 着地と #76 調停の記録。

## Slice 2/3 を nice-to-have として保留・#81 close（owner 判断 2026-06-19）

S1（必須 ＝ #81 本体 AC：セル追加 UI・複数セル編集→Save→Run）は上記のとおり全4層 GREEN で出荷済み。残る **Slice 2（依存矢印）/ Slice 3（並べ替え順 UI・セル名編集・テンプレ挿入）は「あったらいい」枠**で、owner が「なきゃだめではないので今は不要」と判断。**未着手のまま #81 を close**（本体 AC は S1 で充足済み）。再 grill を避けるため、Slice 2 で今回の grill が確定させた下位決定を以下に固定する（**復活時はここから再開・Q1/Q2 は code 裏取り済みで再導出不要**）。

- **マクロ（ADR-0013 で凍結済み・不変）**: 矢印 = 純粋可視化。Python が DAG を所有（`load_app` の `cell.defs`/`cell.refs` から静的算出＝ライブカーネル不要、probe 実証済み）／C# が空間 UI を所有（窓・線）。
- **Q1 = (a) import（モジュール）由来の依存は矢印にしない**（marimo `cell-3d-renderer.tsx:141` `dataType==="module"` 除外の忠実移植）。静的代理は **`_cell.imports` のうち `imported_symbol is None` の `definition` 名を除外**（＝丸ごとモジュール束縛 `import X`/`import X as Y`）。`from X import Y`（`imported_symbol` 有り＝値はシンボル）は残す＝marimo 一致。`imported_namespaces` は別名 `pd` を取りこぼすため不採用（probe で確認）。
- **Q2 = (c) 構造変化（追加/削除/Open）＋ focus-out（編集確定）で再計算**。marimo は矢印を keystroke でなく離散的なコード登録（run）で更新する（`runtime.py` の Variables broadcast は `_mutate_graph`→register 経路のみ）ので、live カーネルの無い backcast では focus-out が register の忠実な対応物。線の位置追従は毎フレーム C#（Python 不要）／focus-out で `(defs,refs)` 差分ゼロなら edge を re-push しない／構文壊れ→`load_app` 失敗時は直前の矢印を保持（fail-soft）。
- **Q3（途中・骨格のみ）**: 単一 `MaskableGraphic`（EdgeOverlay）を `FloatingWindowLayer` 直背面・同 Content 配下に1枚（pan/zoom 追従無料・既存 `PythonSyntaxMeshEffect` の頂点描画イディオム）／ベジエ（marimo `type:"default"`）＋矢じり（`ArrowClosed`）＋固定ハンドル（元=窓下端中央・先=窓上端中央）／純関数 `EdgeGeometry`（rect→頂点）を層1 AFK で検証／矢印集合は Python・幾何は C# で**再結合しない**。未決＝animated 破線（marimo `animated:true`）を含めるか（保留）。
- 検証層（復活時）: 層3 pytest golden（defs/refs→重複排除 index ペア・module 除外）／層1 純 C# AFK（`EdgeGeometry`）／層2 view AFK（実 pythonnet 1回＋mesh）／層4 HITL（矢印表示・ドラッグ追従・依存の増減で矢印 増減）。

## 出荷後の owner 修正（2026-06-19）— [+] を右下へ（#80 メニュー退役は #76 命令型 sunset と統合）

#81 出荷物への owner レビューで 2 点を修正。**[+] ボタンの位置**が本セッションの実質変更、**#80 Strategy メニュー退役**は並行ブランチ `feat/76-imperative-sunset`（commit `409fa33`）が既に実装済みだったため、そちらをカノニカルとして main に統合し（重複実装を破棄）、本セッションは [+] 修正のみを上乗せした（owner 判断: 統合先＝main）。

- **[+] ボタン = 中央上 → 右下**（`BackcastWorkspaceRoot.BuildAddCellButton`）。marimo `edit-app.tsx:454` の top-centre 配置からの**意図的な逸脱**（[[ttwr-parity-first]] の divergence、根拠＝owner の明示要求）。anchor/pivot=(1,0)・`anchoredPosition=(-20,56)`（y=footer 40px＋16px gap で footer バーを避ける）。コメントに divergence 理由を明記。AFK probe は button geometry を assert しない（層4 HITL 正本）。
- **#80 Strategy メニュー退役**は #76 sunset 側に記録済み（本 findings 上の「#76 命令型 sunset」節＋`docs/findings/0047` §5＋commit `409fa33`）。`MenuBarView` の Strategy 一式・root の `OnOpenStrategy`/`EnumerateStrategies`・`StrategyPickerModel`/`StrategyPickerProbe` を撤去。**挙動の帰結**: 既存戦略を開く UI は File→Open のみ（layout sidecar 必須）になり、sidecar 無しの素の `.py` を UI から開く #80 経路は失われる。
- 統合時の cleanup（simplify pass）: `ReseedFromEditor` 呼出コメントの `#78/#80` → `#78`、`FileDialog.cs` の "(matches the #80 strategy picker)" 削除。
- 検証: compile gate `error CS` 0・`MenuBarCutoverProbe` PASS（統合後ツリー）。

正本: `docs/adr/0013`（immutable・不変）＋ 本 findings。
