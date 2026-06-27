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
- **live venue（kabu）の集約/チャート挙動を回帰テストするときは実 venue に繋がない**。実 prod 採取の codec-replayable mock `python/tests/fixtures/kabu_live_mock_4sym.json` を `KabuPushFrameProcessor` で再生する（4銘柄同時更新・partial-append ワートを決定的に再現）。採取は `python/spike/kabu_capture_mock.py`、再生リファレンスは `python/spike/kabu_replay_multi.py` / `kabu_replay_wart.py`。詳細は **findings 0117**。検証 18081 は無配信なので mock が唯一の決定的経路。

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

## リリース手順（shippable build → draft Release → HITL publish）

> shippable Windows64 build は **GitHub Actions を退役**し、owner マシン上の **`scripts/build-and-release.ps1`** に一本化した（ADR-0038 / findings 0050 退役バナー / issue #180）。self-hosted runner は不要。正本はこの script のヘッダ docstring。

### 前提条件（満たさないと script が exit 2/3 で止まる）

- **Unity Editor を閉じる**：同プロジェクトを開いたままだと project lock で `exit 3`（script は stale lock の排他削除を試行し、消せなければ実 Editor が保持中とみなす）
- **`.env` の `UNITY_EDITOR_PATH`**：process env → `<repo>/.env` → `<repo>/python/.env` の順で解決。未設定なら `ProjectVersion.txt` のバージョンから Hub パス（`C:\Program Files\Unity\Hub\Editor\<ver>\Editor\Unity.exe`）へフォールバック。実体が無ければ `exit 2`
- **`uv` が PATH または `%USERPROFILE%\.local\bin\uv.exe`**：無ければ `exit 2`（`uv sync --frozen` で `python/.venv` を materialize する）
- **Release を発行する場合のみ**：`gh auth` 済みで write 権限を持つ owner アカウント（**`-GhAccount botterYosuke`**）。token は gh サブプロセスにだけ渡され、global の active account は変えない（[[gh-two-accounts-workflow-scope]] 参照）

### コマンド

```powershell
# ① build + smoke のみ（リリースに触れない。ローカル検証 / bit-rot チェック）
powershell -ExecutionPolicy Bypass -File scripts/build-and-release.ps1 -SkipRelease

# ② build + smoke + draft Release 発行（既定。HITL publish 待ち）
powershell -ExecutionPolicy Bypass -File scripts/build-and-release.ps1 -GhAccount botterYosuke

# ③ tag を切ってリリース（version を明示。未指定なら HEAD の tag、無ければ local-<shortsha>）
powershell -ExecutionPolicy Bypass -File scripts/build-and-release.ps1 -Version v0.3.0 -GhAccount botterYosuke
```

主なフラグ: `-SkipRelease`（build+smoke 止まり）/ `-PublishNow`（draft でなく即 publish・HITL を飛ばす）/ `-SkipSmoke`（4 stage smoke を省略・本番前は非推奨）/ `-BuildTimeoutMin`（既定 40）。

### 処理フロー（script が自動でやること）

clean → `uv sync --frozen` → Unity build（`BackcastShippableBuild.BuildWindows64`・**`-Wait` を使わず artifact poll + timeout force-kill**：duckdb 上流の shutdown hang 対策、成否は exit code でなく成果物で判定）→ build 出力検証 → Library 8GB ガード → `dist/` 組み立て + single-top-folder zip（System32 bsdtar で long-path 対応）→ Python SBOM（CycloneDX）→ **smoke 4 stage**（zip 整合 / manifest schema / venv import / Player GUI contract）→ SHA256SUMS → **draft** GitHub Release 作成・asset upload。

進捗は repo 直下 `build-and-release.progress.log` に即時 flush（`Get-Content -Wait` でライブ追跡可）。成果物は `dist/`（zip・`runtime-manifest.json`・SBOM・`SHA256SUMS.txt`）。

### HITL publish（owner 手動・script は draft 止まり）

1. draft Release の zip を `gh release download <ver> -p '*.zip' -p 'SHA256SUMS.txt'` で取得し、`Get-FileHash` で `SHA256SUMS.txt` と照合
2. **clean machine** で展開（system Python / VC++ Redist 不要：cpython + vcruntime DLL は bundle 済み）
3. Replay AC を完走確認
4. GitHub で「Publish release」をクリック

> 終了コード: `0`=OK / `1`=build or smoke 失敗 / `2`=設定不備 / `3`=Editor 起動中。

## E2E実行時のルール

- 原則としてUI操作を通じて確認する
- モックAPIではなく、デプロイ済み環境に対して実行する
- API直接実行は補助的な確認に限定する
- テストが常に成功するような実装に変更しない
- 画面上の表示値と保存後のデータ状態を両方確認する
- 失敗した場合は、原因を調査して修正し、再実行する
