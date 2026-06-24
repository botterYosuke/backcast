# CLAUDE.md — The-Trader-Was-Replaced

## 実装完了後の必須アクション

実装・修正・フェーズが完了したとき（「完成した」「done」「finished」「実装した」「修正した」「コミットした」「マージする」「フェーズ終了」などのフレーズが出たとき）は、**必ず** `post-impl-skill-update`, `code-review(simplify)` スキルを発動すること。

### `post-impl-skill-update`スキルは：
- 今回使用したスキルの振り返り
- 使えばよかった（使い忘れた）スキルの特定
- スキルの description（トリガー条件）や内容の改善

を行い、スキルエコシステムを育てる。

### `code-review(simplify)` スキルを発動した際は：

**Medium** 以上の指摘が無くなるまで `/pair-relay` でレビュー＆修正を繰り返すこと。

## ビルド・テスト実行コマンド

> このリポジトリの Python は **uv 管理 venv**（`python/.venv`・`python/uv.lock`）。`poetry` / `pip` は使わない。
> 依存追加は `cd python && uv sync`。

### Python 取引エンジン (kernel / orchestrator / exchanges)

- 全テスト: `cd python && uv run pytest`
- 単一ファイル: `cd python && uv run pytest tests/test_inproc_prompt_login.py -v`
- **シナリオ紐づけ（Gap 3）**: 台帳の Action-ID を正本に持つ pytest は `@pytest.mark.scenario("KABU-LIVE-03")` を付与。`tests/conftest.py` が**実 outcome から** `[E2E <id> PASS/FAIL/SKIP]` を出力（手動 print ではないのでバイパス不可）。marker は `--strict-markers` 登録済み（typo は失敗）
- venv の python を直接: `python/.venv/Scripts/python.exe -m pytest python/tests`
- 構文/ import 健全性の素早い確認: `cd python && ./.venv/Scripts/python.exe -c "import engine.live.live_orchestrator; print('import OK')"`
- 静的解析 / Linter: **未設定**（ruff/black/flake8/mypy はプロジェクトに無い）。新設するまで lint コマンドは存在しない（捏造しない）。

### Unity フロントエンド (headless / batchmode)

Unity 本体パスは **`.env` の `UNITY_EDITOR_PATH`** から解決する（process env 優先 → `<repo>/.env` → `<repo>/python/.env`、`EnvConfig.cs` と同じ順）。例:
```
UNITY_EDITOR_PATH="C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"
```
⚠️ batchmode は **1 プロジェクト 1 インスタンス**。Unity Editor で同プロジェクトを開いたまま実行すると project lock で失敗する（先に Editor を閉じる）。

- 実 venue ログイン E2E（**env 経路**・headless・HITL）—— ランチャ経由:
  - kabu verify: `pwsh scripts/run-live-e2e.ps1 -Venue kabu`（kabuステーション本体が起動・ログイン済・API 有効＋`.env` に `DEV_KABU_API_PASSWORD`）
  - 立花 demo: `pwsh scripts/run-live-e2e.ps1 -Venue tachibana`（`.env` に `DEV_TACHIBANA_AUTH_ID_DEMO` / `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` / `DEV_TACHIBANA_SECOND`）
- compile だけ通すゲート: `pwsh scripts/run-live-e2e.ps1 -CompileOnly`（ログに `error CS\d+` が無ければ PASS）
- 任意の AFK probe / E2E runner: `pwsh scripts/run-live-e2e.ps1 -Method <FullName>.Run`（例 `VenueLoginSecretProbe.Run`）
- ランチャを介さない素の形（参考）:
  ```
  & $UNITY_EDITOR_PATH -batchmode -nographics -quit -projectPath . -executeMethod <Runner>.Run -logFile <abs log>
  # 終了コード: runner が EditorApplication.Exit(0=PASS / 1=FAIL) を呼ぶ。PASS/FAIL 行はログに出る
  ```

ランチャの堅牢化仕様（AFK エージェントが回しても詰まらないように）:
- ログは常に **`Temp/Unity_E2E.log`**（プロジェクト配下・毎回新規）。既定の AppData `Editor.log` に混ぜない
- 実行中はログを**ライブ・ストリーミング表示**し、Unity 終了で抜ける（一括末尾表示のハング誤認を回避）
- `Temp/UnityLockfile` は**排他削除を試行**：消せれば stale として除去し続行、消せなければ実 Editor が保持中とみなし `exit 3`（クラッシュ残骸での偽デッドロックを防ぐ）
- パラメータは排他（`-CompileOnly` / `-Venue` / `-Method` は同時指定不可。PowerShell ParameterSet で強制）
- 終了コード: `0`=PASS / `1`=FAIL or compile error / `2`=設定不備 / `3`=Editor 起動中
- 実行後に **シナリオ rollup レポート**を出力（ログの `[E2E <NAME|Action-ID> PASS/FAIL]` タグを集計・FAIL 優先）。**タグがあればタグが verdict の正本**（一部 runner の shutdown segfault `exit=139` でもタグで PASS 判定・E2E-INDEX 規約）。タグ無しのみプロセス終了コードに従う

### 統合（Unity + Python を1コマンド・シナリオ rollup 合流）

- `pwsh scripts/run-all-tests.ps1` — pytest を実行し、**Unity と Python の Action-ID を1つの merged rollup** に合流。`-Venue kabu|tachibana` / `-Method <X>.Run` で Unity runner も併走、`-PytestArgs '<file/expr>'` で pytest を絞れる
- verdict: **`pytest exit≠0`（floor＝未タグ失敗/collection error を捕捉）OR rollup に FAIL OR Unity 失敗** で exit 1
- rollup ロジックは `scripts/E2ERollup.ps1` に共有抽出（`run-live-e2e.ps1` と `run-all-tests.ps1` が dot-source）。SKIP は中立表示（fail にカウントしない）

> 注意: 上記 runner は `credentials_source="env"` で**実 venue へのログイン**を検証する（headless 可）。
> #122 の **prompt（tkinter ダイアログ）経路は表示が要るため headless では走らない**——実ダイアログの確認は owner の HITL 専用（findings 0093 §HITL）。

## E2E実行時のルール

- 原則としてUI操作を通じて確認する
- モックAPIではなく、デプロイ済み環境に対して実行する
- API直接実行は補助的な確認に限定する
- テストが常に成功するような実装に変更しない
- 画面上の表示値と保存後のデータ状態を両方確認する
- 失敗した場合は、原因を調査して修正し、再実行する
