# 戦略ノートブックは在庫 0 セルを許す（永続化の床は marimo 由来で 1）

**Status:** accepted (2026-06-25)。**ADR-0013 Decision 5 を supersede**（「Notebook is always ≥1 cell / Delete never reaches 0 cells」を覆す）。ADR-0013 の他の決定（D1–D4・cell-as-floating-window 集約モデル）は不変。

## Context

ADR-0013 Decision 5 は「ノートは常に ≥1 セル・最後の 1 個は削除不可（marimo `canDelete={!hasOnlyOneCell}` の移植）」を凍結した。owner はこれを覆し「**全セルを消せる**ようにしたい」と要求（2026-06-25）。

grill 中に owner が「marimo も最後の 1 枚を消せて、ディスクにはヘッダだけの `.py`（`import marimo` / `app = marimo.App()` / footer）が残る」と事実主張し、**実機（marimo 0.20.4・プロジェクト seam `cell_synthesis.py`）で裏取り**した結果：

- `generate_filecontents(codes=[], ...)` は確かに**ヘッダだけの空 `.py`** を出す（owner の主張どおり）。
- **だが `load_app`（= `decompose_json`）はその空ファイルを「空ボディのセル 1 枚」に展開して返す**（`[{"body": "", "name": "_", ...}]`）。marimo は `.py` の中に「セル 0」を表現できず、空ファイルは必ず 1 セルに戻る。

→ ADR-0013 D5 が引用した「marimo は最後の 1 枚を消せない」は不正確。正しくは「marimo の `.py` は空にできるが、load 時に必ず 1 セルへ膨らむ」。この実機事実が下記の決定を作った。詳細な設計の木と証跡は findings 0114。

## Decision

1. **セッション中は 0 セル（0 窓）を許す。** `MarimoNotebookDocument.RemoveCell` の `if (_cells.Count <= 1) return false;`（≥1 削除床）を撤去し、[✕] で最後の 1 個も消せる。0 セル時はキャンバスに floating cell 窓が 1 つも無く、画面固定の **[+] Add Cell ボタンだけ**が残る（そこから足し直す）。下流の本番コード（`SyncWindowsToNotebook` / `CapturePositions` / `UpdatePlaceholders` / run routing）は全て 0 件 foreach・境界チェック済みで 0 セル安全（findings 0114 §3 で裏取り）。

2. **永続化の床は 1（marimo 由来）。X1 を採用。** 保存→Open し直すと、marimo が空 `.py` を空セル 1 枚に展開するので **0 ではなく空セル 1 枚で戻る**。これを backcast 側で 0 のまま貫く（layout sidecar `.json` を正本にして 0 を復元する＝X2）案は**却下**：marimo の `.py` 意味論からの乖離と「`.py` は 1 と言うが `.json` の 0 を優先する」追加機構（複雑化）を持ち込むため。X1 は「空ファイルは 1 セルで戻る」という **marimo 自身の仕様にそのまま追従**する（[[ttwr-parity-first]]）。永続化は追加実装ゼロ。

3. **0 窓からの 1 枚目は File→New と同一動線。** 0 セル到達時、never-Destroy 殻 `region_001` は必ず dormant（hide のみ・破棄されない）。次の `AddCell` は dormant `region_001` を再利用する既存経路に乗る（ADR-0013 D4）ので、特別分岐は新設しない。File→New（新規）の初期状態は従来どおり**空セル 1 枚**を維持（真っ白だとタイプ開始点が無いため・marimo の New と一致）。

## Considered options

- **X1（採用）— 開き直しは空セル 1 枚で戻る。** marimo と完全 parity・変更は実質「削除床 1 行撤去」。
- **X2（却下）— `.json` を正本に 0 窓で復元。** literal な「完全な空」だが marimo の `.py` 意味論から乖離＋ sidecar-authoritative-count 機構の複雑化。owner が X1 を選択。
- **≥1 を維持（却下＝本 ADR の前提）。** owner 要求「全セルを消せる」に反する。ADR-0013 D5 の根拠（marimo は最後の 1 枚を消せない）は実機反証済み。

## Consequences

- ADR-0013 D5 の `Open` 0-cell 拒否ガード（`MarimoNotebookDocument.Open` の `if (decomposed.Count == 0) return Fail(...)`）は **死にコード**と判明（valid な marimo file の decompose が 0 を返すことは起きない＝marimo が必ず 1 に膨らます）。コメント訂正 or 撤去（任意・無害）。marimo か否かの本判定は同関数の `decomposed == null`（marimo の `load_app` 由来＝not_marimo / syntax_error）が担い、0 セル許容で検出力は落ちない。
- `BackcastWorkspaceRoot.OnDeleteCell` の「a notebook keeps at least one cell.」エラー文言は死ぬ（`DeleteCell` が false を返すのは未知 region 等の本物の異常時のみ）。整理する。
- ≥1 を assert する E2E（最後の 1 枚削除が失敗する前提の節）は**契約反転**（削除成功 → 0 窓）。File→New=1 セルは不変なので FileNavGuard 系は GREEN 継続。
- 実装着手は `behavior-to-e2e` を formal invoke して AFK gate を RED 先行で固定してから（findings 0114 §5）。

ADR-0013 参照のみ（D1–D4 は不変・本 ADR は D5 のみ supersede）。スライス記録 + 実機証跡: findings 0114。
